using System.IO.Compression;

namespace OpenMix.Importer;

/// <summary>Dumps the game's scene objects (MonoBehaviours) to JSON so OpenMix can read its walkthrough, guide and mode
/// settings from the user's own copy. Il2CppDumper (MIT) builds type stubs from the user's libil2cpp.so and
/// global-metadata.dat; AssetStudioModCLI then dumps every MonoBehaviour with those types.
/// Output: &lt;work&gt;/scene/&lt;serialized file&gt;_export/&lt;Class&gt; @&lt;PathID&gt;.json</summary>
public static class SceneDump
{
    public static string Run(string apkPath, string apkData, string combinedDir, string work, string assetStudio, string il2cppDumper, Action<string> log)
    {
        var il2 = Path.Combine(work, "il2cpp");
        Directory.CreateDirectory(il2);
        var so = Path.Combine(il2, "libil2cpp.so");
        using (var z = ZipFile.OpenRead(apkPath))
        {
            var e = z.GetEntry("lib/arm64-v8a/libil2cpp.so") ?? z.GetEntry("lib/armeabi-v7a/libil2cpp.so")
                    ?? throw new InvalidDataException("libil2cpp.so not found in the APK.");
            e.ExtractToFile(so, true);
        }
        var meta = Path.Combine(apkData, "Managed", "Metadata", "global-metadata.dat");
        var stubs = Path.Combine(il2, "out");
        Directory.CreateDirectory(stubs);
        AssetExport.RunTool(il2cppDumper, log, so, meta, stubs);
        var dummy = Path.Combine(stubs, "DummyDll");
        if (!Directory.Exists(dummy)) throw new InvalidOperationException("Il2CppDumper didn't produce type stubs.");

        var outDir = Path.Combine(work, "scene");
        AssetExport.RunTool(assetStudio, log, combinedDir, "-m", "jsonDump", "-t", "monoBehaviour", "--assembly-folder", dummy,
                            "-g", "fileName", "-f", "assetName_pathID", "-o", outDir, "--log-level", "warning");
        log($"scene objects dumped: {Directory.EnumerateFiles(outDir, "*.json", SearchOption.AllDirectories).Count()}");
        return outDir;
    }
}
