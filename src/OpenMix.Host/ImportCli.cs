using OpenMix.Importer;

/// <summary><c>OpenMix --import --apk &lt;file&gt; [--obb &lt;file&gt;] [--content &lt;dir&gt;] [--assetstudio &lt;exe&gt;] [--il2cppdumper &lt;exe&gt;] [--keep-work]</c>:
/// rebuild game content from the user's own files without starting the game.</summary>
static class ImportCli
{
    public static int Run(string[] a)
    {
        string? apk = null, obb = null, content = null, studio = null, il2 = null; bool keep = false;
        for (int i = 0; i < a.Length; i++)
            switch (a[i])
            {
                case "--apk": apk = a[++i]; break;
                case "--obb": obb = a[++i]; break;
                case "--content": content = a[++i]; break;
                case "--assetstudio": studio = a[++i]; break;
                case "--il2cppdumper": il2 = a[++i]; break;
                case "--keep-work": keep = true; break;
                default: Console.Error.WriteLine($"unknown option {a[i]}"); return 2;
            }
        if (apk == null) { Console.Error.WriteLine("usage: OpenMix --import --apk <file> [--obb <file>] [--content <dir>] [--assetstudio <exe>]"); return 2; }
        content ??= ContentPaths.Default;
        var work = Path.Combine(Path.GetTempPath(), "OpenMix-import-" + Environment.ProcessId);
        try
        {
            new Importer(new ImportOptions(apk, obb, content, work) { AssetStudio = studio, Il2CppDumper = il2, KeepWork = keep }).Run();
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine($"import failed: {e.Message}"); return 1; }
    }
}
