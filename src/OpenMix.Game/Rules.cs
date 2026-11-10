using OpenMix.Cards;

namespace OpenMix.Game;

public enum Side { None = 0, Blue = 1, Red = 2 }

/// <summary>Board rules shared by the modes.</summary>
public static class Rules
{
    public const int Slots = 5;

    /// <summary>The card types each game slot (0..4, left to right) accepts, in the dual-colour layout the original uses
    /// for Wilds. Program logic: the original's game-config class fills these per-slot arrays in its static constructor
    /// with literal element stores; nothing serialized holds them (see docs/PROVENANCE.md).</summary>
    public static readonly CardType[][] SlotAccepts =
    {
        new[] { CardType.Lead, CardType.Loop },
        new[] { CardType.Loop },
        new[] { CardType.Loop, CardType.Beat },
        new[] { CardType.Beat },
        new[] { CardType.Beat, CardType.Bass },
    };

    public static bool IsTypeValidInSlot(CardType t, int slot) =>
        t is CardType.Wild or CardType.Fx || (slot >= 0 && slot < Slots && SlotAccepts[slot].Contains(t));

    /// <summary>The colours (card types) a card counts as in a slot: its own type; a Wild counts
    /// as every type the slot accepts; FX counts as none.</summary>
    public static IEnumerable<CardType> CountsAs(Card c, int slot) => c.Type switch
    {
        CardType.Wild => SlotAccepts[slot],
        CardType.Fx => Array.Empty<CardType>(),
        _ => new[] { c.Type },
    };

    /// <summary>Colour bounty check order.</summary>
    public static readonly CardType[] ColourOrder = { CardType.Bass, CardType.Beat, CardType.Loop, CardType.Lead };

    public static Side Other(Side s) => s == Side.Blue ? Side.Red : s == Side.Red ? Side.Blue : Side.None;

    /// <summary>The audio part a card plays in a slot (Wild plays the slot's colour).</summary>
    public static string? AudioPart(Card c, int slot)
    {
        var t = c.Type == CardType.Wild ? WildPart(slot) : c.Type;
        return t switch { CardType.Beat => "beat", CardType.Bass => "bass", CardType.Lead => "lead", CardType.Loop => "loop", _ => null };
    }

    /// <summary>All parts a card plays in a slot: a Wild in a two-colour slot plays both colours
    /// at once; everything else plays one part.</summary>
    public static string?[] AudioParts(Card c, int slot)
    {
        if (c.Type != CardType.Wild) return new[] { AudioPart(c, slot) };
        return SlotAccepts[Math.Clamp(slot, 0, Slots - 1)].Select(t => t switch
            { CardType.Beat => "beat", CardType.Bass => "bass", CardType.Lead => "lead", CardType.Loop => "loop", _ => (string?)null }).ToArray();
    }

    /// <summary>Which part a Wild plays in each slot. Single-colour slots are unambiguous; for the
    /// dual slots we use the slot's first listed colour (Lead / Loop / Beat).</summary>
    /// <summary>Which slots an FX card's music effect (slot DSP) lands on.
    /// Empty = global (key/tempo/mode only); null = ExtraAction (applied where the free card is played).</summary>
    public static int[]? FxMusicTargets(Card fx, int pad, Func<int, PadCard?> topValid)
    {
        var ab = FxAbility.Parse(fx.Ability);
        if (ab == null) return Array.Empty<int>();
        if (ab.Kind == FxKind.ExtraAction) return null;
        if (ab.Kind is FxKind.ScoreAdjust or FxKind.EqSpin or FxKind.Draw or FxKind.Cover or FxKind.Block or FxKind.Unknown
            || (ab.Kind == FxKind.SetBonus && ab.Filter == null)) return Array.Empty<int>();
        var slots = Enumerable.Range(0, Slots).Where(i => i != pad && topValid(i) != null).ToList();
        static bool Same(Card a, Card b) => string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase);
        return ab.Filter?.Kind switch
        {
            "adjacent" => slots.Where(i => Math.Abs(i - pad) == 1).ToArray(),
            "random_opponent" => slots.Where(i => topValid(i)!.Silenced).ToArray(),      // the slot the ability picked
            "song" => slots.Where(i => slots.Any(j => j != i && Same(topValid(i)!.Card, topValid(j)!.Card))).ToArray(),
            null when ab.Kind == FxKind.Combo => slots.Where(i => slots.Any(j => j != i && Same(topValid(i)!.Card, topValid(j)!.Card))).ToArray(),
            null => slots.ToArray(),
            _ => slots.Where(i => ab.Filter.Matches(topValid(i)!.Card, i)).ToArray(),
        };
    }

    public static CardType WildPart(int slot) => SlotAccepts[Math.Clamp(slot, 0, Slots - 1)][0];
}

// ------------------------------------------------------------------------------------------
// FX ability scripts: "timing:kind(args)"
// ------------------------------------------------------------------------------------------

public enum FxTiming { Instant, Turn, Persist }
public enum FxKind { Remove, ExtraAction, ScoreAdjust, EqSpin, SetBonus, Combo, Draw, Cover, Block, Unknown }
public enum FxTarget { Self, Opponent, Both }

/// <summary>A card filter: type{..} instrument{..} power{power_1} song empty{empty} adjacent random_opponent.</summary>
public sealed record FxFilter(string Kind, string[] Values)
{
    public bool Matches(Card c, int slot)
    {
        switch (Kind)
        {
            case "type":
                return Values.Any(v => Enum.TryParse<CardType>(v, true, out var t) && Rules.CountsAs(c, slot).Contains(t));
            case "instrument":
                return Values.Any(v => c.Instruments.Any(i => i.Equals(v, StringComparison.OrdinalIgnoreCase)));
            case "power":
                return Values.Any(v => v.StartsWith("power_") && int.TryParse(v[6..], out var p) && c.Power == p);
            default:
                return false;
        }
    }
    public override string ToString() => Values.Length == 0 ? Kind : $"{Kind}{{{string.Join("|", Values)}}}";
}

public sealed class FxAbility
{
    public FxTiming Timing; public FxKind Kind;
    public FxTarget Target = FxTarget.Self;
    public FxFilter? Filter;
    public int Amount;                 // points / cards; -1 = random 1..3
    public string? CoverEffect;        // for cover(...): "1" or "draw{Self,1}"
    public string Raw = "";

    public static FxAbility? Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var a = new FxAbility { Raw = s.Trim() };
        int colon = s.IndexOf(':');
        string timing = colon > 0 ? s[..colon] : "instant";
        a.Timing = timing.Trim().ToLowerInvariant() switch { "turn" => FxTiming.Turn, "persist" => FxTiming.Persist, _ => FxTiming.Instant };
        string body = colon > 0 ? s[(colon + 1)..].Trim() : s.Trim();
        int lp = body.IndexOf('(');
        string kind = lp > 0 ? body[..lp] : body;
        string args = lp > 0 && body.EndsWith(")") ? body[(lp + 1)..^1] : "";
        a.Kind = kind.Trim().ToLowerInvariant() switch
        {
            "remove" => FxKind.Remove, "extra_action" => FxKind.ExtraAction, "score_adjust" => FxKind.ScoreAdjust,
            "eq_spin" => FxKind.EqSpin, "set_bonus" => FxKind.SetBonus, "combo" => FxKind.Combo,
            "draw" => FxKind.Draw, "cover" => FxKind.Cover, "block" => FxKind.Block, _ => FxKind.Unknown,
        };
        var parts = SplitTop(args);
        foreach (var raw in parts)
        {
            var p = raw.Trim();
            if (p is "Self" or "Opponent" or "Both") { a.Target = Enum.Parse<FxTarget>(p); continue; }
            if (int.TryParse(p, out var n)) { if (a.Kind == FxKind.Cover && a.Filter != null) a.CoverEffect = p; else a.Amount = n; continue; }
            if (p.StartsWith("random{")) { a.Amount = -1; continue; }
            if (a.Kind == FxKind.Cover && a.Filter != null) { a.CoverEffect = p; continue; }
            a.Filter = ParseFilter(p);
        }
        return a;
    }

    static FxFilter ParseFilter(string p)
    {
        int b = p.IndexOf('{');
        if (b < 0) return new FxFilter(p.Trim(), Array.Empty<string>());
        string k = p[..b].Trim(); string v = p[(b + 1)..].TrimEnd('}');
        return new FxFilter(k, v.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    static List<string> SplitTop(string s)
    {
        var res = new List<string>(); int depth = 0; var cur = new System.Text.StringBuilder();
        foreach (char ch in s)
        {
            if (ch is '{' or '(') depth++;
            if (ch is '}' or ')') depth--;
            if (ch == ',' && depth == 0) { res.Add(cur.ToString()); cur.Clear(); } else cur.Append(ch);
        }
        if (cur.Length > 0) res.Add(cur.ToString());
        return res;
    }

    /// <summary>OpenMix's own short rule text, for cards whose imported text (the fx_* screen text key) is missing.</summary>
    public string Describe()
    {
        string who = Target switch { FxTarget.Opponent => "opponent's", FxTarget.Both => "all", _ => "your" };
        string f = Filter == null ? "" : Filter.Kind switch
        {
            "type" => string.Join("/", Filter.Values) + " ", "instrument" => string.Join("/", Filter.Values) + " ",
            "power" => "level-1 ", "song" => "same-song ", "empty" => "empty-slot ", "adjacent" => "adjacent ", "random_opponent" => "a random opponent ",
            _ => "",
        };
        return Kind switch
        {
            FxKind.Remove when Filter?.Kind == "adjacent" => "Clear the cards next to this one",
            FxKind.Remove when Filter?.Kind == "random_opponent" => "Random opponent card cleared",
            FxKind.Remove => $"Clear {who} {f}cards".Replace("  ", " "),
            FxKind.ExtraAction => $"Extra action: play a {f}card".Replace("  ", " "),
            FxKind.ScoreAdjust => Amount == -1 ? "Random score change" : $"{(Amount >= 0 ? "+" : "")}{Amount} points to {(Target == FxTarget.Self ? "you" : Target == FxTarget.Opponent ? "opponent" : "both teams")}",
            FxKind.EqSpin => "Free spin of the wheel",
            FxKind.SetBonus => $"+{Amount} for each {f}card in your mix".Replace("  ", " "),
            FxKind.Combo => $"+{Amount} for each pair of same-song cards in your mix",
            FxKind.Draw => Amount == 0 ? "Redraw your hand" : $"{(Target == FxTarget.Both ? "Everyone draws" : "Draw")} {Amount}",
            FxKind.Cover when Filter?.Kind == "empty" => "+1 if played into an empty slot",
            FxKind.Cover => $"Draw 1 if played on a {f}card".Replace("  ", " "),
            FxKind.Block => $"While in the mix, no one can play {f}cards".Replace("  ", " "),
            _ => Raw,
        };
    }
}
