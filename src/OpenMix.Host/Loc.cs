using System.Text.Json;

/// <summary>
/// The original's localization table, as imported from the user's own copy of the game:
/// data/loc/strings.json = { lang: { keyName: text } } for 13 languages (en, fr, de, it, es-es, es-mx, nl, fi, no, pl,
/// pt-pt, pt-br, sv). Key names are the enum names (e.g. "Badge_ClashFullMix", "fx_action_bass").
/// </summary>
public static class Loc
{
    static Dictionary<string, Dictionary<string, string>> _t = new();
    public static string Language { get; set; } = "en";
    public static IEnumerable<string> Languages => _t.Keys;

    public static void Load(string dataDir)
    {
        var f = Path.Combine(dataDir, "loc", "strings.json");
        try { if (File.Exists(f)) _t = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(f)) ?? new(); }
        catch (Exception ex) { Console.WriteLine($"loc: couldn't read strings.json ({ex.Message})"); }
    }

    /// <summary>The text for a key in the current language (English, then null, as fallbacks).</summary>
    public static string? Get(string key, string? lang = null)
    {
        if (_t.TryGetValue(lang ?? Language, out var d) && d.TryGetValue(key, out var v)) return v;
        return _t.TryGetValue("en", out var en) && en.TryGetValue(key, out var e) ? e : null;
    }

    public static Dictionary<string, string> Table(string lang) =>
        _t.TryGetValue(lang, out var d) ? d : _t.TryGetValue("en", out var en) ? en : new();
}
