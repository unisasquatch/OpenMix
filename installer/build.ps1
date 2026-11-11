# Builds build\installer\OpenMix-Setup-<version>.exe
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1 -AssetStudio <folder> -Il2CppDumper <folder> [-Version 0.1.1] [-Iscc <ISCC.exe>]
# Needs: .NET 9 SDK, Inno Setup 6, and these exact third-party releases (their files are checked against the hashes below):
#   AssetStudioModCLI 0.20.0, net9 build (MIT)  https://github.com/aelurum/AssetStudio/releases
#   Il2CppDumper 6.7.46, win-x64 build (MIT)     https://github.com/Perfare/Il2CppDumper/releases
param(
    [Parameter(Mandatory = $true)][string]$AssetStudio,
    [Parameter(Mandatory = $true)][string]$Il2CppDumper,
    [string]$Version = '0.1.1',
    [string]$Iscc,
    [string]$PlatformToolsZip   # optional: a local platform-tools_r35.0.0-windows.zip; otherwise it is downloaded
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $repo 'build\stage'
if (-not $Iscc) {
    $Iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) { throw 'Inno Setup 6 (ISCC.exe) not found; pass -Iscc <path>.' }

# The only third-party tool files OpenMix ships, with their SHA-256. Anything else in those folders is left out
# (for AssetStudio: its FBX exporter, FMOD and Oodle libraries, which OpenMix never calls).
$assetStudioFiles = [ordered]@{
    'AssetStudio.dll'                                    = '9B6E262BBC112790700756CC267C240F724A0C1F842D83BACC3038D603CC65AE'
    'AssetStudio.PInvoke.dll'                            = 'BE14CE7D08D330E2AFD4BCE9E065EB60FDF212903BC76674E44CDB30AEAD721C'
    'AssetStudioModCLI.deps.json'                        = '300E1DD8D61677CC424244E65CCD192B22E73D7963C7F27D9A934DE2C6F6B881'
    'AssetStudioModCLI.dll'                              = '9141D0D2C763B02079B400A141B76C6A60C52389B51B938C36D5A8C9259BAC18'
    'AssetStudioModCLI.exe'                              = 'BACE7F24BEA152E66512841D4B8D75C9386476D928A9599EF5B12591D57C83F9'
    'AssetStudioModCLI.runtimeconfig.json'               = '3C0BF772B95C93363A0A14E5DF78E4EC408C90F04CB7103205AF97B2D276FAB0'
    'AssetStudioUtility.dll'                             = '47298D4381E7269D77733F34035305D62CD3E9FD8C7B19D973E115507F2E85D0'
    'K4os.Compression.LZ4.dll'                           = 'E282AADC2353B864FE95CD5FB525A1A093D99A30A5BF2530B7AB831D56179816'
    'Mono.Cecil.dll'                                     = '831DCA77470D85CB6FFBEA3072DAA7A3DF5B7C9FCFD9C3F43674A9BE99D4BFCF'
    'Mono.Cecil.Mdb.dll'                                 = '28CB367972BDC1CD43E4006306AF2FD96D37F4ED4B239EE90E1DC7237A93AF7F'
    'Mono.Cecil.Pdb.dll'                                 = 'A332332633FBCB20E8D50E49B4DB7BD1557721417122CF0C5F4C42F2332391D0'
    'Mono.Cecil.Rocks.dll'                               = 'BF992F3DCE364EBCC3200FA7832EF07E20B4E2DBC3A8A6213CE44E3D239DB984'
    'SixLabors.Fonts.dll'                                = 'E661C6AC204861EAB86E45EB8736988491170DA9A8F5D040D380CF6B9CD395FF'
    'SixLabors.ImageSharp.dll'                           = '27EF3F04651914BE319D3C75A890E985F7E77F9C340AD9BC7BB0B337A9730588'
    'SixLabors.ImageSharp.Drawing.dll'                   = 'E9DFE5D8692C7B03B6224795229A941D3B297A35F3455799E13B57AF5F265308'
    'System.IO.Hashing.dll'                              = '64E9A0EC3FF4AC241AB35D481677C71DB2ECF621EA9A2A372222C625B4F2366B'
    'Texture2DDecoderWrapper.dll'                        = '5744C1808393BCBBE368AD44EEE0E0E715C1BF29A70F3DA9A680649746CD23F6'
    'ZstdSharp.dll'                                      = 'B285BB9F59928BECB8E048BF2A75E872F6555F8773D7B72386A9F77F447F425C'
    'runtimes\win-x64\native\Texture2DDecoderNative.dll' = 'AF106BDCAA1E6EC1CD0236740CA18B375023F9F89CD5DEF210EDB4D60641E101'
}
$il2cppFiles = [ordered]@{
    'Il2CppDumper.exe' = '071E36D396AE93CB2CFEC032513B46A6BFD67E9B93157830711AB6D79DB55045'   # self-contained single file
}

function Copy-Pinned($from, $to, $files) {
    foreach ($rel in $files.Keys) {
        $src = Join-Path $from $rel
        if (-not (Test-Path $src)) { throw "missing $rel in $from" }
        $h = (Get-FileHash $src -Algorithm SHA256).Hash
        if ($h -ne $files[$rel]) { throw "$rel in $from doesn't match the pinned release (got $h)" }
        $dst = Join-Path $to $rel
        New-Item -ItemType Directory -Force (Split-Path $dst -Parent) | Out-Null
        Copy-Item $src $dst
    }
}

# 1. the app, self-contained (no .NET install needed)
# DebugType=none keeps the build machine's source path (which includes a home directory) out of the shipped DLLs'
# PE debug directory; no PDBs are shipped anyway.
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
dotnet publish (Join-Path $repo 'src\OpenMix.Host') -c Release -r win-x64 --self-contained true -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false -o $stage
if ($LASTEXITCODE) { throw 'publish failed' }
Get-ChildItem $stage -Filter *.pdb | Remove-Item

# 2. the import tools: exactly the pinned files
Copy-Pinned $AssetStudio (Join-Path $stage 'tools\AssetStudioCLI') $assetStudioFiles
$il2 = Join-Path $stage 'tools\Il2CppDumper'
Copy-Pinned $Il2CppDumper $il2 $il2cppFiles
# OpenMix's settings for it: no struct/header output, never wait for a key press
@{ DumpMethod = $true; DumpField = $true; DumpProperty = $true; DumpAttribute = $true; DumpFieldOffset = $true; DumpMethodOffset = $true
   DumpTypeDefIndex = $true; GenerateDummyDll = $true; GenerateStruct = $false; DummyDllAddToken = $true; RequireAnyKey = $false
   ForceIl2CppVersion = $false; ForceVersion = 16; ForceDump = $false; NoRedirectedPointer = $false } |
    ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $il2 'config.json')

# 2b. adb (Android platform-tools, Apache-2.0) for the optional "adb" installer component. Pinned to r35.0.0 and
#     verified by SHA-256; staged apart from $stage so the .iss can tie it to its own component.
$ptUrl = 'https://dl.google.com/android/repository/platform-tools_r35.0.0-windows.zip'
$ptSha = '7AB78A8F8B305AE4D0DE647D99C43599744DE61A0838D3A47BDA0CDFFEFEE87E'
$ptWant = @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'NOTICE.txt', 'source.properties')
$ptOut = Join-Path $repo 'build\stage-adb\platform-tools'
if (Test-Path (Join-Path $repo 'build\stage-adb')) { Remove-Item -Recurse -Force (Join-Path $repo 'build\stage-adb') }
New-Item -ItemType Directory -Force $ptOut | Out-Null
$ptZip = $PlatformToolsZip
if (-not $ptZip) {
    $ptZip = Join-Path $env:TEMP 'openmix-platform-tools.zip'
    Write-Host "downloading platform-tools r35.0.0 ..."
    Invoke-WebRequest -Uri $ptUrl -OutFile $ptZip -UseBasicParsing
}
$h = (Get-FileHash $ptZip -Algorithm SHA256).Hash
if ($h -ne $ptSha) { throw "platform-tools zip SHA-256 mismatch (got $h; expected $ptSha)" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$za = [System.IO.Compression.ZipFile]::OpenRead($ptZip)
try {
    foreach ($name in $ptWant) {
        $e = $za.Entries | Where-Object { $_.FullName -eq "platform-tools/$name" } | Select-Object -First 1
        if (-not $e) { throw "platform-tools zip is missing $name" }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $ptOut $name), $true)
    }
} finally { $za.Dispose() }

# 3. a private .NET runtime folder for AssetStudioModCLI (framework-dependent), from the runtime pack publish used
$packRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.netcore.app.runtime.win-x64'
$ver = (Get-ChildItem $packRoot -Directory | Where-Object Name -like '9.*' | Sort-Object { [version]$_.Name } | Select-Object -Last 1).Name
$pack = Join-Path $packRoot "$ver\runtimes\win-x64"
$shared = Join-Path $stage "tools\dotnet\shared\Microsoft.NETCore.App\$ver"
$fxr = Join-Path $stage "tools\dotnet\host\fxr\$ver"
New-Item -ItemType Directory -Force $shared, $fxr | Out-Null
Copy-Item (Join-Path $pack 'lib\net9.0\*') $shared
Get-ChildItem (Join-Path $pack 'native') -File | Where-Object Name -ne 'hostfxr.dll' | Copy-Item -Destination $shared
Copy-Item (Join-Path $pack 'native\hostfxr.dll') $fxr

# 4. licences: OpenMix's, the third-party ones in licenses\, and the .NET runtime's from its package
$lic = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force $lic | Out-Null
Copy-Item (Join-Path $repo 'LICENSE') $stage
Copy-Item (Join-Path $repo 'THIRD_PARTY.md') $stage
Copy-Item (Join-Path $repo 'licenses\*') $lic
Copy-Item (Join-Path $packRoot "$ver\LICENSE.TXT") (Join-Path $lic 'dotnet-LICENSE.txt')
Copy-Item (Join-Path $packRoot "$ver\THIRD-PARTY-NOTICES.TXT") (Join-Path $lic 'dotnet-THIRD-PARTY-NOTICES.txt')

& $Iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'OpenMix.iss')
if ($LASTEXITCODE) { throw 'ISCC failed' }
Get-Item (Join-Path $repo "build\installer\OpenMix-Setup-$Version.exe") | Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB) } }
