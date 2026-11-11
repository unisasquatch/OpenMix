; OpenMix installer (Inno Setup 6). Build with installer\build.ps1.
; Contains no game content: on first launch OpenMix imports it from the user's own copy of DropMix.

#define AppName "OpenMix"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define Stage "..\build\stage"
#define StageAdb "..\build\stage-adb"

[Setup]
AppId={{9B1F6C2E-4A37-4E0B-8D4C-0F3A7E2B51C8}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=OpenMix contributors
LicenseFile=..\LICENSE
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.18362
OutputDir=..\build\installer
OutputBaseFilename=OpenMix-Setup-{#AppVersion}
SetupIconFile=..\src\OpenMix.Host\app.ico
UninstallDisplayIcon={app}\OpenMix.exe
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Full installation (recommended)"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "core"; Description: "OpenMix"; Types: full custom; Flags: fixed
Name: "adb"; Description: "Android platform-tools (adb) — copy DropMix straight from a phone over USB. Not needed if you already have the game's .apk and .obb files."; Types: full custom

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "firewall"; Description: "Let phones on my home network join (adds a Windows Firewall rule; asks for administrator approval)"; GroupDescription: "Network:"

[Files]
Source: "{#Stage}\*"; DestDir: "{app}"; Components: core; Flags: ignoreversion recursesubdirs createallsubdirs
; adb (optional): its own component, staged separately so it installs only when ticked
Source: "{#StageAdb}\platform-tools\*"; DestDir: "{app}\tools\platform-tools"; Components: adb; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; files from an older version that this one no longer ships (imported content and saves live in %LOCALAPPDATA%\OpenMix)
Type: filesandordirs; Name: "{app}\tools"
Type: filesandordirs; Name: "{app}\wwwroot"
Type: filesandordirs; Name: "{app}\runtimes"
Type: files; Name: "{app}\*.dll"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\OpenMix.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\OpenMix.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""OpenMix"""; Verb: runas; \
  Flags: shellexec runhidden waituntilterminated; Tasks: firewall; StatusMsg: "Adding the firewall rule..."
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""OpenMix"" dir=in action=allow program=""{app}\OpenMix.exe"" enable=yes profile=private,domain"; \
  Verb: runas; Flags: shellexec runhidden waituntilterminated; Tasks: firewall
Filename: "{app}\OpenMix.exe"; Description: "Start OpenMix"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""OpenMix"""; Verb: runas; \
  Flags: shellexec runhidden waituntilterminated; Tasks: firewall; RunOnceId: "OpenMixFirewall"

[UninstallDelete]
; an empty scratch folder the runtime creates next to the program; imported content lives in %LOCALAPPDATA%\OpenMix
Type: filesandordirs; Name: "{app}\temp"

[Code]
function WebView2Installed(): Boolean;
var v: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0'));
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not WebView2Installed() then
    if SuppressibleMsgBox('OpenMix draws its window with Microsoft Edge WebView2, which is not installed on this PC.' + #13#10#13#10 +
              'Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 (Microsoft) before starting OpenMix.' + #13#10#13#10 +
              'Continue installing anyway?', mbConfirmation, MB_YESNO, IDYES) = IDNO then
      Result := False;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var data: String;
begin
  data := ExpandConstant('{localappdata}\OpenMix');
  if (CurUninstallStep = usPostUninstall) and DirExists(data) then
    if SuppressibleMsgBox('Also delete the game content OpenMix imported, and your saved mixes, collection and progress?' + #13#10 +
              '(Choose No to keep them for a later reinstall.)', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      DelTree(data, True, True, True);
end;
