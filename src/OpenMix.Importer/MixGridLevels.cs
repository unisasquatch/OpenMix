using System.Text;
using System.Text.RegularExpressions;

namespace OpenMix.Importer;

/// <summary>Builds data/mixgrid/level_list.csv from the MixGrid level files imported from the OBB. MixGrid was an
/// unreleased test mode and its real level order isn't in the game files, so OpenMix orders the levels itself:
/// levels whose name carries a number ("1. WELCOME") by that number, then the 3-round (version 4) levels, then the
/// rest. The version 1 test file is left out.</summary>
public static class MixGridLevels
{
    static readonly Regex Numbered = new(@"^(\d+)\.\s*(.*)$", RegexOptions.Compiled);

    public static int BuildList(string dataDir, Action<string> log)
    {
        var dir = Path.Combine(dataDir, "mixgrid", "levels");
        if (!Directory.Exists(dir)) { log("no MixGrid levels imported"); return 0; }
        var levels = new List<(string file, int group, int number, string name)>();
        foreach (var f in Directory.GetFiles(dir, "*.txt"))
        {
            var lines = File.ReadAllText(f).Replace("\r", "").Split('\n');
            int version = lines.Length > 1 && int.TryParse(lines[1].Split(',')[0], out var v) ? v : 0;
            if (version < 2) continue;
            var name = lines.Length > 3 ? Csv.Split(lines[3])[0].Trim() : "";
            var m = Numbered.Match(name);
            var display = m.Success ? m.Groups[2].Value : name.TrimStart('*', ' ');
            int group = m.Success ? 0 : version >= 4 ? 1 : 2;
            levels.Add((Path.GetFileNameWithoutExtension(f), group, m.Success ? int.Parse(m.Groups[1].Value) : 0, display));
        }
        var sb = new StringBuilder("Level,Note\r\n");
        foreach (var l in levels.OrderBy(l => l.group).ThenBy(l => l.number).ThenBy(l => l.file, StringComparer.Ordinal))
            sb.Append(l.file).Append(',').Append(Csv.Quote(l.name)).Append("\r\n");
        File.WriteAllText(Path.Combine(dataDir, "mixgrid", "level_list.csv"), sb.ToString(), new UTF8Encoding(false));
        log($"MixGrid: {levels.Count} levels listed");
        return levels.Count;
    }
}
