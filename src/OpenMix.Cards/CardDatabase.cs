using System.Globalization;
using OpenMix.Board;

namespace OpenMix.Cards;

/// <summary>Loads the card database and resolves a scanned CTI (from the board) to a
/// <see cref="Card"/>. The importer builds <c>cards.csv</c> from the card tables in the
/// user's own copy of the game; this loader reads it. A tolerant,
/// header-driven CSV parser is used so column order/extras don't matter.</summary>
public sealed class CardDatabase
{
    private readonly Dictionary<ushort, Card> _byCti = new();
    private readonly Dictionary<string, Card> _byCid = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byCid.Count;
    public IReadOnlyCollection<Card> Cards => _byCid.Values;

    public Card? Resolve(Cti cti) => _byCti.GetValueOrDefault(cti.Value);
    public Card? ResolveCid(string cid) => _byCid.GetValueOrDefault(cid);

    public void Add(Card card)
    {
        _byCid[card.Cid] = card;
        if (card.Cti != 0) _byCti[card.Cti] = card;
    }

    /// <summary>Load from a merged CSV with a header row. Recognised columns (case-
    /// insensitive, extras ignored): CID, CTI, Name, Artist, Type, Rarity, Key, Mode,
    /// Tempo, Power, Instrument1..4, Audio, Image.</summary>
    public static CardDatabase LoadCsv(string path)
    {
        var db = new CardDatabase();
        using var reader = new StreamReader(path);
        string? header = reader.ReadLine();
        if (header == null) return db;
        var cols = SplitCsv(header);
        int Idx(params string[] names)
        {
            foreach (var n in names)
            {
                int i = cols.FindIndex(c => c.Trim().Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }
        int iCid = Idx("CID"), iSrc = Idx("SourceCID", "Source CID"), iCti = Idx("CTI"), iName = Idx("Name"), iArtist = Idx("Artist"),
            iType = Idx("Type", "Item Type"), iRar = Idx("Rarity"), iKey = Idx("Key"),
            iMode = Idx("Mode"), iTempo = Idx("Tempo"), iPower = Idx("Power"),
            iAudio = Idx("Audio"), iImage = Idx("Image"),
            iIcon = Idx("Icon", "Series Icon"), iAbility = Idx("Ability"), iScreen = Idx("ScreenText", "Screen Text"), iBars = Idx("NumBars", "Num Bars"),
            iMfx = Idx("MusicEffect", "Music Effect"), iTrans = Idx("Transition"),
            iWbk = Idx("WildBeatHasKey", "Wild Beat Has Key"), iShifts = Idx("KeyShifts");
        int[] iInstr = { Idx("Instrument 1", "Instrument1"), Idx("Instrument 2", "Instrument2"),
                         Idx("Instrument 3", "Instrument3"), Idx("Instrument 4", "Instrument4") };

        // content_db.csv (same folder) by Source CID: Genre, Year, Illustrator, Art Crop Center, Credits
        var extra = new Dictionary<string, (string genre, string year, string ill, string crop, string credits)>(StringComparer.OrdinalIgnoreCase);
        var contentPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "content_db.csv");
        if (File.Exists(contentPath))
        {
            using var cr = new StreamReader(contentPath);
            var ch = SplitCsv(cr.ReadLine() ?? "");
            int C(string n) => ch.FindIndex(c => c.Trim().Equals(n, StringComparison.OrdinalIgnoreCase));
            int cCid = C("CID"), cG = C("Genre"), cY = C("Year"), cI = C("Illustrator"), cA = C("Art Crop Center"), cC = C("Credits");
            string? cl;
            while ((cl = cr.ReadLine()) != null)
            {
                var cf = SplitCsv(cl); string G(int i) => i >= 0 && i < cf.Count ? cf[i].Trim() : "";
                if (G(cCid).Length > 0) extra[G(cCid)] = (G(cG), G(cY), G(cI), G(cA), G(cC));
            }
        }

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var f = SplitCsv(line);
            string Get(int i) => (i >= 0 && i < f.Count) ? f[i].Trim() : "";
            if (Get(iCid).Length == 0) continue;

            var instruments = iInstr.Select(Get).Where(s => s.Length > 0).ToArray();
            db.Add(new Card
            {
                Cid = Get(iCid),
                SourceCid = Get(iSrc),
                Cti = ParseUShort(Get(iCti)),
                Name = Get(iName),
                Artist = Get(iArtist),
                Type = ParseType(Get(iType), Get(iCid)),
                Rarity = ParseRarity(Get(iRar)),
                Key = ParseKey(Get(iKey)),
                Mode = ParseMode(Get(iMode)),
                Tempo = ParseFloat(Get(iTempo)),
                Power = (int)ParseFloat(Get(iPower)),
                Instruments = instruments,
                AudioId = Get(iAudio),
                ImageName = Get(iImage),
                Icon = Get(iIcon),
                Ability = Get(iAbility),
                ScreenText = Get(iScreen),
                NumBars = (int)ParseFloat(Get(iBars)),
                MusicEffect = Get(iMfx),
                Transition = Get(iTrans),
                WildBeatHasKey = Get(iWbk).Equals("yes", StringComparison.OrdinalIgnoreCase),
                KeyShifts = ParseShifts(Get(iShifts)),
                Genres = extra.TryGetValue(Get(iSrc), out var ex) ? ex.genre.Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>(),
                Year = ex.year ?? "", Illustrator = ex.ill ?? "", ArtCrop = ex.crop ?? "", Credits = ex.credits ?? "",
            });
        }
        return db;
    }

    // ---- parsing helpers -------------------------------------------------------

    /// <summary>"C..B" as 12 space-separated semitone values, "_" = blank.</summary>
    private static int?[] ParseShifts(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<int?>();
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 12) return Array.Empty<int?>();
        return parts.Select(p => int.TryParse(p, out var v) ? v : (int?)null).ToArray();
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var sb = new System.Text.StringBuilder();
        foreach (char ch in line)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (ch == ',' && !inQuotes) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }

    private static ushort ParseUShort(string s) =>
        ushort.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : (ushort)0;

    private static float ParseFloat(string s) =>
        float.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0f;

    private static CardType ParseType(string type, string cid)
    {
        string t = type.Length > 0 ? type : cid;   // CIDs end in _Beat/_Bass/_Loop/_Lead/_FX/_Wild
        t = t.ToLowerInvariant();
        if (t.Contains("beat")) return CardType.Beat;
        if (t.Contains("bass")) return CardType.Bass;
        if (t.Contains("loop")) return CardType.Loop;
        if (t.Contains("lead")) return CardType.Lead;
        if (t.Contains("wild")) return CardType.Wild;
        if (t.Contains("fx")) return CardType.Fx;
        return CardType.Invalid;
    }

    private static CardRarity ParseRarity(string s) => s.ToLowerInvariant() switch
    {
        var x when x.Contains("legend") => CardRarity.Legendary,
        var x when x.Contains("rare") => CardRarity.Rare,
        _ => CardRarity.Common,
    };

    private static MusicMode ParseMode(string s) => s.ToLowerInvariant() switch
    {
        var x when x.StartsWith("maj") => MusicMode.Major,
        var x when x.StartsWith("min") => MusicMode.Minor,
        _ => MusicMode.None,
    };

    private static MusicKey ParseKey(string s)
    {
        s = s.Trim();
        return s.ToUpperInvariant() switch
        {
            "C" => MusicKey.C, "C#" or "DB" => MusicKey.Cs, "D" => MusicKey.D,
            "D#" or "EB" => MusicKey.Ds, "E" => MusicKey.E, "F" => MusicKey.F,
            "F#" or "GB" => MusicKey.Fs, "G" => MusicKey.G, "G#" or "AB" => MusicKey.Gs,
            "A" => MusicKey.A, "A#" or "BB" => MusicKey.As, "B" => MusicKey.B,
            _ => MusicKey.None,
        };
    }
}
