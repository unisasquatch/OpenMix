/// <summary>Where imported game content lives. Nothing from the original game ships with OpenMix: the importer
/// rebuilds it here from the user's own APK, OBB and cards.</summary>
public static class ContentPaths
{
    public static string Default => Environment.GetEnvironmentVariable("OPENMIX_CONTENT") is { Length: > 0 } dev ? dev
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenMix", "content");

    /// <summary>AssetStudioModCLI (MIT): bundled in tools\AssetStudioCLI, or OPENMIX_ASSETSTUDIO for development.</summary>
    public static string? AssetStudio() => new[]
    {
        Environment.GetEnvironmentVariable("OPENMIX_ASSETSTUDIO"),
        Path.Combine(AppContext.BaseDirectory, "tools", "AssetStudioCLI", "AssetStudioModCLI.exe"),
    }.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));

    /// <summary>Folder holding wwwroot: next to the app when installed, else the working directory (development).</summary>
    public static string WebRoot() =>
        Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : Directory.GetCurrentDirectory();

    /// <summary>Il2CppDumper (MIT): bundled in tools/Il2CppDumper, or OPENMIX_IL2CPPDUMPER for development.</summary>
    public static string? Il2CppDumper() => new[]
    {
        Environment.GetEnvironmentVariable("OPENMIX_IL2CPPDUMPER"),
        Path.Combine(AppContext.BaseDirectory, "tools", "Il2CppDumper", "Il2CppDumper.exe"),
    }.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
}
