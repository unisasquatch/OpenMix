namespace OpenMix.Importer;

/// <summary>What to import from and where to put it.</summary>
public sealed record ImportOptions(string Apk, string? Obb, string ContentRoot, string WorkDir)
{
    /// <summary>AssetStudioModCLI.exe (MIT, downloaded by the user); needed for sprites, fonts and text assets.</summary>
    public string? AssetStudio { get; init; }
    /// <summary>Il2CppDumper.exe (MIT, downloaded by the user); needed for the scene settings.</summary>
    public string? Il2CppDumper { get; init; }
    /// <summary>Keep the unpacked APK/OBB in WorkDir afterwards (for debugging).</summary>
    public bool KeepWork { get; init; }
}

/// <summary>Rebuilds OpenMix's game content from the user's own copy of the game. Every step reads only the
/// files the user supplies and writes only under <see cref="ImportOptions.ContentRoot"/>.</summary>
public sealed class Importer
{
    readonly ImportOptions _o;
    readonly Action<string> _log;
    public Importer(ImportOptions o, Action<string>? log = null) { _o = o; _log = log ?? Console.WriteLine; }

    public string ApkData => Path.Combine(_o.WorkDir, "apk", "bin", "Data");
    public string ObbRoot => Path.Combine(_o.WorkDir, "obb");
    public string ObbData => Path.Combine(ObbRoot, "assets", "bin", "Data");
    public string DataDir => Path.Combine(_o.ContentRoot, "data");

    /// <summary>AssetStudioModCLI: the given path, else tools\AssetStudioCLI next to OpenMix.</summary>
    string FindAssetStudio()
    {
        var c = new[] { _o.AssetStudio, Path.Combine(AppContext.BaseDirectory, "tools", "AssetStudioCLI", "AssetStudioModCLI.exe") }
            .FirstOrDefault(p => p != null && File.Exists(p));
        return c ?? throw new FileNotFoundException("AssetStudioModCLI.exe not found. Download AssetStudioMod (MIT) and pass --assetstudio <path>.");
    }

    /// <summary>FMOD banks (voice lines, UI sounds) to WAV, and the OBB's sampler instruments (transitions, SFX).
    /// Encrypted .mogg stems are skipped: OpenMix doesn't decrypt them.</summary>
    void ImportSounds()
    {
        var banks = Path.Combine(ObbRoot, "assets", "fmod", "Mobile");
        var fmodOut = Path.Combine(_o.ContentRoot, "assets_raw", "fmod");
        foreach (var (bank, dir) in new[] { ("Master Bank.bank", "master"), ("FTUE Bank.bank", "ftue"), ("Kiosk Demo Bank.bank", "kiosk") })
        {
            var f = Path.Combine(banks, bank);
            if (File.Exists(f)) _log($"{bank}: {Fsb5.Extract(f, Path.Combine(fmodOut, dir))} sounds");
            else _log($"{bank}: not in this OBB");
        }
        var fusion = Path.Combine(ObbRoot, "assets", "fusion");
        int copied = 0, skipped = 0;
        if (Directory.Exists(fusion))
            foreach (var f in Directory.EnumerateFiles(fusion, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".mogg", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                var to = Path.Combine(_o.ContentRoot, "audio_unpacked", "_obb", Path.GetRelativePath(fusion, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to, true); copied++;
            }
        _log($"instruments: {copied} files" + (skipped > 0 ? $" ({skipped} encrypted stems skipped)" : ""));
    }

    /// <summary>Il2CppDumper (MIT): the given path, else tools\Il2CppDumper next to OpenMix.</summary>
    string FindIl2CppDumper()
    {
        var c = new[] { _o.Il2CppDumper, Environment.GetEnvironmentVariable("OPENMIX_IL2CPPDUMPER"),
                        Path.Combine(AppContext.BaseDirectory, "tools", "Il2CppDumper", "Il2CppDumper.exe") }
            .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        return c ?? throw new FileNotFoundException("Il2CppDumper.exe not found. Download Il2CppDumper (MIT) and pass --il2cppdumper <path>.");
    }

    /// <summary>The walkthrough, guide, Puzzle and Party settings and the coin-flip curve, read from the game's own scene
    /// objects and metadata in the user's copy.</summary>
    void ImportSceneSettings()
    {
        var scene = SceneDump.Run(_o.Apk, ApkData, Path.Combine(_o.WorkDir, "combined"), _o.WorkDir, FindAssetStudio(), FindIl2CppDumper(), _log);
        SceneConfigs.BuildWalkthrough(scene, DataDir, _log);
        SceneConfigs.BuildGuide(scene, DataDir, _log);
        SceneConfigs.BuildLanes(scene, DataDir, _log);
        SceneConfigs.BuildParty(scene, DataDir, _log);
        SceneConfigs.BuildCoinCurve(Path.Combine(ApkData, "Managed", "Metadata", "global-metadata.dat"), _o.ContentRoot, _log);
        SceneConfigs.BuildGameTables(scene, Path.Combine(ApkData, "Managed", "Metadata", "global-metadata.dat"), DataDir, _log, ImportFmodMix());
    }

    /// <summary>The original's FMOD mixer chains (Master Bank.bank, found by path through the OBB's GUIDs.txt), or null
    /// when they can't be read: OpenMix then plays through a neutral mix.</summary>
    System.Text.Json.Nodes.JsonObject? ImportFmodMix()
    {
        var fmod = Path.Combine(ObbRoot, "assets", "fmod");
        try { return FmodMix.Read(Path.Combine(fmod, "Mobile", "Master Bank.bank"), Path.Combine(fmod, "GUIDs.txt"), _log); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            _log($"fmod mix skipped ({e.Message}); OpenMix will play through a neutral mix");
            return null;
        }
    }

    public void Run()
    {
        Directory.CreateDirectory(_o.WorkDir);
        Directory.CreateDirectory(DataDir);
        Step("Unpacking the APK", () => GameFiles.Extract(_o.Apk, "assets/", Path.Combine(_o.WorkDir, "apk"), _log));
        if (_o.Obb != null) Step("Unpacking the OBB", () => GameFiles.Extract(_o.Obb, "", ObbRoot, _log));
        Step("Card tables", () => CardTables.Build(ApkData, DataDir, _log));
        Step("Game text in 13 languages", () => Localization.Build(Path.Combine(ApkData, "Managed", "Metadata", "global-metadata.dat"), DataDir, _log));
        if (_o.Obb != null)
            Step("Images and text", () => AssetExport.Run(FindAssetStudio(), ApkData, ObbData, _o.WorkDir, _o.ContentRoot, _log));
        Step("Fonts", () =>
        {   // rebuilt from the game's own font atlases; on failure OpenMix falls back to system fonts
            try { _log($"{FontBuilder.Build(new[] { Path.Combine(_o.WorkDir, "combined"), ApkData }.Where(Directory.Exists), FindAssetStudio(), _o.ContentRoot, _log)} fonts"); }
            catch (Exception e) { _log("fonts skipped: " + e.Message); }
        });
        if (_o.Obb != null) Step("Game sounds", ImportSounds);
        if (_o.Obb != null) Step("MixGrid level list", () => MixGridLevels.BuildList(DataDir, _log));
        if (_o.Obb != null) Step("Scene settings", ImportSceneSettings);
        if (!_o.KeepWork) try { Directory.Delete(_o.WorkDir, true); } catch { }
        _log("import finished");
    }

    void Step(string name, Action a)
    {
        _log($"== {name}");
        var t = System.Diagnostics.Stopwatch.StartNew();
        a();
        _log($"   ({t.Elapsed.TotalSeconds:0.0} s)");
    }
}
