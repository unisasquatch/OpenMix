using System.Text.Json;

namespace OpenMix.Cards;

/// <summary>One physical product's worth of cards: a 15-card playlist (Starter, Playlist Pack,
/// Discovery Pack), the Baffler bonus cards, or the promo cards. Keyed by the series icon.</summary>
public sealed record DeckInfo(string Icon, string Name, string Kind, int Season, int Order, int Count);

/// <summary>Deck list built from the set CSVs (<c>data/set_N.csv</c>: "Series Icon", "Item Type",
/// "Deck ID", "Season"), plus the persisted set of active decks.</summary>
public sealed class DeckCatalog
{
    public IReadOnlyList<DeckInfo> Decks { get; }

    DeckCatalog(List<DeckInfo> decks) => Decks = decks;

    public static DeckCatalog Load(string dataDir, IEnumerable<Card> cards)
    {
        var counts = cards.Where(c => c.Icon.Length > 0).GroupBy(c => c.Icon).ToDictionary(g => g.Key, g => g.Count());
        var meta = new Dictionary<string, (string kind, int season, int order)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.Exists(dataDir) ? Directory.GetFiles(dataDir, "set_*.csv").OrderBy(x => x) : Enumerable.Empty<string>())
        {
            using var r = new StreamReader(f);
            var head = Split(r.ReadLine() ?? "");
            int iIcon = head.IndexOf("Series Icon"), iType = head.IndexOf("Item Type"), iDeck = head.IndexOf("Deck ID"), iSeason = head.IndexOf("Season");
            if (iIcon < 0) continue;
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                var c = Split(line);
                string Get(int i) => i >= 0 && i < c.Count ? c[i] : "";
                var icon = Get(iIcon); if (icon.Length == 0 || meta.ContainsKey(icon)) continue;
                int.TryParse(Get(iSeason), out var season); int.TryParse(Get(iDeck), out var deck);
                string kind = icon == "baffler" ? "Bonus" : Get(iType) switch { "Starter" => "Starter", "Discovery" => "Discovery", "Promo" => "Promo", _ => "Playlist" };
                meta[icon] = (kind, season, season * 100 + deck);
            }
        }
        // the decks' real names (card_sets*.txt: Deck Name per Deck Icon)
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.Exists(dataDir) ? Directory.GetFiles(dataDir, "card_sets_*.csv") : Array.Empty<string>())
        {
            var lines = File.ReadAllLines(f); if (lines.Length < 2) continue;
            var h = Split(lines[0]); int iN = h.IndexOf("Deck Name"), iI = h.IndexOf("Deck Icon");
            foreach (var l in lines.Skip(1)) { var c = Split(l); if (iN >= 0 && iI >= 0 && c.Count > Math.Max(iN, iI)) names[c[iI]] = c[iN]; }
        }
        var decks = counts.Keys.Select(icon =>
        {
            var (kind, season, order) = meta.TryGetValue(icon, out var m) ? m : ("Playlist", 0, 9999);
            if (kind == "Playlist" && season == 2) kind = "Season 2";
            return new DeckInfo(icon, names.TryGetValue(icon, out var dn) ? dn : Pretty(icon), kind, season, order, counts[icon]);
        })
        .OrderBy(d => d.Kind switch { "Starter" => 0, "Playlist" => 1, "Discovery" => 2, "Season 2" => 3, "Bonus" => 4, _ => 5 })
        .ThenBy(d => d.Order).ToList();
        return new DeckCatalog(decks);
    }

    /// <summary>Printed card details from the set CSVs: CID → ("S01 C046", series index, series count).</summary>
    public static Dictionary<string, (string print, int index, int count)> LoadPrintInfo(string dataDir)
    {
        var d = new Dictionary<string, (string, int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.Exists(dataDir) ? Directory.GetFiles(dataDir, "set_*.csv") : Array.Empty<string>())
        {
            using var r = new StreamReader(f);
            var head = Split(r.ReadLine() ?? "");
            int iCid = head.IndexOf("CID"), iPrint = head.IndexOf("Print ID"), iIdx = head.IndexOf("Series Index"), iCnt = head.IndexOf("Series Count");
            if (iCid < 0) continue;
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                var c = Split(line);
                string Get(int i) => i >= 0 && i < c.Count ? c[i] : "";
                int.TryParse(Get(iIdx), out var idx); int.TryParse(Get(iCnt), out var cnt);
                if (Get(iCid).Length > 0) d[Get(iCid)] = (Get(iPrint), idx, cnt);
            }
        }
        return d;
    }

    public static string Pretty(string icon) => icon switch
    {
        "theblade" => "The Blade",
        "" => "",
        _ => char.ToUpperInvariant(icon[0]) + icon[1..],
    };

    // ---- persistence: data/decks.json {"active":[icons]}; missing file = every deck ----
    public static string[]? LoadActive(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("active").EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray();
        }
        catch { return null; }
    }

    public static void SaveActive(string path, IEnumerable<string> active)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { active = active.ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static List<string> Split(string line)
    {
        var res = new List<string>(); var sb = new System.Text.StringBuilder(); bool q = false;
        foreach (char ch in line)
        {
            if (ch == '"') q = !q;
            else if (ch == ',' && !q) { res.Add(sb.ToString().Trim()); sb.Clear(); }
            else if (ch != '﻿') sb.Append(ch);
        }
        res.Add(sb.ToString().Trim());
        return res;
    }
}
