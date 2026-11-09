using System.Text;

namespace OpenMix.Importer;

/// <summary>Builds data/cards.csv from the card tables inside the game's Unity data.
/// <list type="bullet">
/// <item>Content database (level0): a run of TextAssets, one CSV row each (the first is the header).</item>
/// <item>Set tables (sharedassets0.assets): one TextAsset per set mapping physical cards (CID, CTI) to content.</item>
/// </list>
/// TextAsset strings are stored as a little-endian u32 length followed by the text, padded to 4 bytes.</summary>
public static class CardTables
{
    public static readonly string[] OutCols =
    {
        "CID", "CTI", "SourceCID", "Name", "Artist", "Type", "Power", "Key", "Mode", "Tempo",
        "Instrument 1", "Instrument 2", "Instrument 3", "Instrument 4", "Audio", "Image",
        "Icon", "Ability", "ScreenText", "NumBars", "MusicEffect", "Transition", "WildBeatHasKey", "KeyShifts",
    };
    static readonly string[] KeyCols = { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };

    /// <summary>Writes content_db.csv, set_N.csv and cards.csv into <paramref name="outData"/>. Returns the card count.</summary>
    public static int Build(string unityData, string outData, Action<string>? log = null)
    {
        Directory.CreateDirectory(outData);
        var level0 = File.ReadAllBytes(Path.Combine(unityData, "level0"));
        var shared = File.ReadAllBytes(Path.Combine(unityData, "sharedassets0.assets"));

        int hdr = IndexOf(level0, "CID,Artist,Name,Audio"u8, 0);
        if (hdr < 4) throw new InvalidDataException("Card content table not found in level0.");
        var rows = CarveSequence(level0, hdr - 4);
        var content = string.Concat(rows.Select(r => r.EndsWith('\n') || r.EndsWith('\r') ? r : r + "\n"));
        File.WriteAllText(Path.Combine(outData, "content_db.csv"), content, new UTF8Encoding(false));
        log?.Invoke($"card content table: {rows.Count - 1} rows");

        var sets = new List<string>();
        for (int off = 0, n = 1; n < 99; n++)
        {
            int i = IndexOf(shared, "CID,Source CID"u8, off);
            if (i < 4) break;
            int len = (int)BitConverter.ToUInt32(shared, i - 4);
            var text = Latin1(shared, i, len);
            var name = $"set_{n}.csv";
            File.WriteAllText(Path.Combine(outData, name), text, new UTF8Encoding(false));
            sets.Add(text);
            off = i + len;
        }
        log?.Invoke($"card set tables: {sets.Count}");

        // decode as the bytes were written (UTF-8), then undo any double-encoding
        var contentRows = Csv.Read(Utf8OrLatin1(Encoding.Latin1.GetBytes(content)))
            .Select(Fix).GroupBy(r => r.GetValueOrDefault("CID", "")).ToDictionary(g => g.Key, g => g.First());
        var outRows = new List<Dictionary<string, string>>();
        foreach (var setText in sets)
            foreach (var s0 in Csv.Read(Utf8OrLatin1(Encoding.Latin1.GetBytes(setText))))
            {
                var s = Fix(s0);
                string S(string k) => s.GetValueOrDefault(k, "");
                var src = S("Source CID").Trim();
                contentRows.TryGetValue(src, out var c); c ??= new();
                string C(string k) => c.GetValueOrDefault(k, "");
                bool anyKey = KeyCols.Any(k => C(k).Trim().Length > 0);
                outRows.Add(new()
                {
                    ["CID"] = S("CID"), ["CTI"] = S("CTI"), ["SourceCID"] = src,
                    ["Name"] = C("Name"), ["Artist"] = C("Artist"),
                    ["Type"] = Or(C("Type"), S("TypeRef")), ["Power"] = Or(S("Power"), C("Test Power")),
                    ["Key"] = C("Key"), ["Mode"] = C("Mode"), ["Tempo"] = C("Tempo"),
                    ["Instrument 1"] = C("Instrument"), ["Instrument 2"] = C("Instrument 2"),
                    ["Instrument 3"] = C("Instrument 3"), ["Instrument 4"] = C("Instrument 4"),
                    ["Audio"] = C("Audio"), ["Image"] = C("Image"),
                    ["Icon"] = S("Series Icon").Trim(), ["Ability"] = C("Ability").Trim(), ["ScreenText"] = C("Screen Text").Trim(),
                    ["NumBars"] = C("Num Bars").Trim(), ["MusicEffect"] = C("Music Effect").Trim(),
                    ["Transition"] = C("Transition").Trim(), ["WildBeatHasKey"] = C("Wild Beat Has Key").Trim(),
                    ["KeyShifts"] = anyKey ? string.Join(" ", KeyCols.Select(k => C(k).Trim() is { Length: > 0 } v ? v : "_")) : "",
                });
            }
        Csv.Write(Path.Combine(outData, "cards.csv"), OutCols, outRows);
        log?.Invoke($"cards.csv: {outRows.Count} cards");
        return outRows.Count;
    }

    static string Or(string a, string b) => a.Length > 0 ? a : b;

    static List<string> CarveSequence(byte[] d, int pos, int maxRows = 5000)
    {
        var rows = new List<string>();
        for (int k = 0; k < maxRows && pos + 4 <= d.Length; k++)
        {
            uint len = BitConverter.ToUInt32(d, pos);
            if (len == 0 || len > 200000 || pos + 4 + len > d.Length) break;
            var span = d.AsSpan(pos + 4, (int)len);
            if (!IsCsvText(span)) break;
            rows.Add(Encoding.Latin1.GetString(span));
            pos = (pos + 4 + (int)len + 3) & ~3;
        }
        return rows;
    }

    static bool IsCsvText(ReadOnlySpan<byte> b)
    {
        int ok = 0;
        foreach (var c in b) if (c is 9 or 10 or 13 || (c >= 32 && c < 127)) ok++;
        return b.Length > 0 && ok / (double)b.Length > 0.95;
    }

    static string Latin1(byte[] d, int i, int len) => Encoding.Latin1.GetString(d, i, len);

    static string Utf8OrLatin1(byte[] raw)
    {
        try { return new UTF8Encoding(false, true).GetString(raw); } catch (DecoderFallbackException) { return Encoding.Latin1.GetString(raw); }
    }

    static Dictionary<string, string> Fix(Dictionary<string, string> r) => r.ToDictionary(kv => kv.Key, kv => FixMojibake(kv.Value));

    static readonly Encoding Cp1252 = CodePagesEncoding();
    static Encoding CodePagesEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    /// <summary>Undo repeated "UTF-8 bytes read as cp1252" damage in the game's own tables.</summary>
    public static string FixMojibake(string s)
    {
        for (int round = 0; round < 3; round++)
        {
            if (s.IndexOfAny(new[] { 'Ã', 'Â', 'â' }) < 0) break;
            var b = new List<byte>();
            foreach (var ch in s)
            {
                if (ch < 256) b.Add((byte)ch);
                else
                {
                    try { b.AddRange(Cp1252.GetBytes(ch.ToString())); }
                    catch (EncoderFallbackException) { b.AddRange(Encoding.UTF8.GetBytes(ch.ToString())); }
                }
            }
            try { s = new UTF8Encoding(false, true).GetString(b.ToArray()); } catch (DecoderFallbackException) { break; }
        }
        return s;
    }

    static int IndexOf(byte[] hay, ReadOnlySpan<byte> needle, int from)
    {
        int i = hay.AsSpan(from).IndexOf(needle);
        return i < 0 ? -1 : i + from;
    }
}
