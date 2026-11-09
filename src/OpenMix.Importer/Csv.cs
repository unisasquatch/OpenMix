using System.Text;

namespace OpenMix.Importer;

/// <summary>Minimal CSV helpers: one record per line (the game's tables have no multi-line fields).</summary>
public static class Csv
{
    public static List<string> Split(string line)
    {
        var f = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; }
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { f.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        f.Add(sb.ToString());
        return f;
    }

    /// <summary>Header-keyed rows; missing cells read as "".</summary>
    public static List<Dictionary<string, string>> Read(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var rows = new List<Dictionary<string, string>>();
        if (lines.Length == 0) return rows;
        var head = Split(lines[0]);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            var cells = Split(line);
            var r = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < head.Count; i++) r[head[i]] = i < cells.Count ? cells[i] : "";
            rows.Add(r);
        }
        return rows;
    }

    public static string Quote(string v) =>
        v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    public static void Write(string path, IReadOnlyList<string> cols, IEnumerable<IReadOnlyDictionary<string, string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", cols.Select(Quote))).Append("\r\n");
        foreach (var r in rows) sb.Append(string.Join(",", cols.Select(c => Quote(r.TryGetValue(c, out var v) ? v : "")))).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
