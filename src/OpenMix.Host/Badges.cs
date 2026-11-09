using System.Globalization;
using System.Text.Json;

/// <summary>
/// Badges (badges01.txt: 8 per playlist, 224): a tracker, a count to reach, an optional threshold value and either a
/// specific card or "any card from the playlist". Progress is kept in data/badges.json. Texts: the original English
/// descriptions (matched by content); titles where identifiable, otherwise port wording.
/// </summary>
public sealed class BadgeStore
{
    public sealed record Def(string Playlist, int Index, string Tracker, int Count, double? Value, string? CardId, string? Icon, string TitleKey)
    {
        public string Key => $"{Playlist}:{Index}";
    }
    public sealed record Unlock(long Id, string Key, string Title, string Text, string? Image, string? Icon, DateTime At);

    readonly List<Def> _defs = new();
    readonly Dictionary<string, int> _progress = new();
    readonly HashSet<string> _unlocked = new();
    readonly List<Unlock> _recent = new();
    readonly string _file;
    readonly Func<string, (string name, string image)?> _card;
    readonly object _lock = new();
    long _seq;
    public event Action<Unlock>? Unlocked;

    public BadgeStore(string dataDir, Func<string, (string name, string image)?> card)
    {
        _card = card; _file = Path.Combine(dataDir, "badges.json");
        var csv = Path.Combine(dataDir, "badges.csv");
        if (File.Exists(csv))
            foreach (var line in File.ReadAllLines(csv).Skip(1))
            {
                var f = line.Split(',');
                if (f.Length < 8 || !int.TryParse(f[1], out int idx)) continue;
                _defs.Add(new Def(f[0].Trim(), idx, f[2].Trim(), int.TryParse(f[3], out int c) ? Math.Max(1, c) : 1,
                    double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null,
                    f[5].Trim().Length > 0 ? f[5].Trim() : null, f[6].Trim().Length > 0 ? f[6].Trim() : null, f[7].Trim()));
            }
        try
        {
            if (File.Exists(_file))
            {
                using var d = JsonDocument.Parse(File.ReadAllText(_file));
                foreach (var p in d.RootElement.GetProperty("progress").EnumerateObject()) _progress[p.Name] = p.Value.GetInt32();
                foreach (var u in d.RootElement.GetProperty("unlocked").EnumerateArray()) _unlocked.Add(u.GetString()!);
            }
        }
        catch { }
    }

    void Save()
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(new { progress = _progress, unlocked = _unlocked })); } catch { }
    }

    // titles/descriptions come from the imported Badge_* keys (the opposite-mode badge uses Badge_AudioMinorKey for
    // major cards); without strings.json, short OpenMix wording stands in
    static string TitleKey(Def d, string? cardMode) => d.TitleKey == "Badge_AudioMajorKey" && cardMode == "Major" ? "Badge_AudioMinorKey" : d.TitleKey;
    string Fill(string t, Def d)
    {
        string card = d.CardId != null && _card(d.CardId) is { } c ? c.name : "";
        string pl = Loc.Get("Deck_" + d.Playlist) ?? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(d.Playlist);
        return t.Replace("{card}", card).Replace("{count}", d.Count.ToString()).Replace("{value}", d.Value?.ToString(CultureInfo.InvariantCulture) ?? "").Replace("{playlist}", pl);
    }
    string Title(Def d, string? cardMode) => Loc.Get(TitleKey(d, cardMode)) is string t ? Fill(t, d) : TitleFallback(d, cardMode);
    static string TitleFallback(Def d, string? cardMode) => d.Tracker switch
    {
        "global_playCardBelowTempo" => "Slow Tempo", "global_playCardAboveTempo" => "Fast Tempo",
        "global_playCardInOppositeMode" => cardMode == "Major" ? "Key Swap (minor)" : "Key Swap (major)",
        "party_clearRequestsSinglePlay" => "Combo", "snapshot_shareMix" => "Mix Sharer", "clash_playExtra" => "Extra Plays",
        "clash_subtractCumulative" => "Point Thief", "clash_scoreSinglePlay" => "High Scorer", "clash_coverBonus" => "Cover Bonus",
        "clash_clearCumulative" => "Clearer", "clash_playCount" => "Regular", "clash_fullMix" => "All Slots",
        "collection_registerAll" => "Complete Set", "lanes_clearFullLane" => "Row Clearer",
        _ => d.TitleKey.Replace("Badge_", ""),
    };

    string Text(Def d, string? cardMode) => Loc.Get(TitleKey(d, cardMode) + "_Desc") is string t ? Fill(t, d) : TextFallback(d, cardMode);
    string TextFallback(Def d, string? cardMode)
    {
        string card = d.CardId != null && _card(d.CardId) is { } c ? c.name : "a card";
        string pl = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(d.Playlist);
        string val = d.Value?.ToString(CultureInfo.InvariantCulture) ?? "";
        return d.Tracker switch
        {
            "collection_registerAll" => $"{pl}: every card added.",
            "snapshot_shareMix" => $"{pl}: {val} cards in one shared mix.",
            "global_playCardInOppositeMode" => $"{card}, played in {(cardMode == "Major" ? "minor" : "major")}.",
            "global_playCardBelowTempo" => $"{card}, played under {val} BPM.",
            "global_playCardAboveTempo" => $"{card}, played over {val} BPM.",
            "clash_playExtra" => $"Clash: {d.Count} extra plays from {card}.",
            "clash_fullMix" => $"Clash: every slot filled, using {pl} cards.",
            "clash_scoreSinglePlay" => d.Count > 1 ? $"Clash: {val} points from {card}, {d.Count} times." : $"Clash: {val} points from {card} in one turn.",
            "clash_clearCumulative" => $"Clash: {d.Count} cards cleared by {card}.",
            "clash_playCount" => $"Clash: {card} played {d.Count} times.",
            "clash_coverBonus" => $"Clash: {d.Count} cover bonuses from {card}.",
            "clash_subtractCumulative" => $"Clash: {d.Count} points taken by {card}.",
            "party_clearRequestsSinglePlay" => $"Party: one {pl} card meeting 2+ requests.",
            "lanes_clearFullLane" => $"Puzzle: {d.Count} full rows with {pl} cards.",
            _ => d.Tracker,
        };
    }

    /// <summary>An event: tracker name, the card involved (cid + its deck icon), a measured value (points, tempo, cards)
    /// and how much to add. A badge counts it when its card matches (or, without a card, the card's deck matches).</summary>
    public void Track(string tracker, string? cid, string? icon, double measured = 0, int amount = 1, string? cardMode = null)
    {
        lock (_lock)
        {
            foreach (var d in _defs.Where(d => d.Tracker == tracker))
            {
                if (_unlocked.Contains(d.Key)) continue;
                if (d.CardId != null ? d.CardId != cid : !string.Equals(d.Playlist, icon, StringComparison.OrdinalIgnoreCase)) continue;
                bool ok = tracker switch
                {
                    "global_playCardBelowTempo" => d.Value is double v1 && measured < v1,
                    "global_playCardAboveTempo" => d.Value is double v2 && measured > v2,
                    "clash_scoreSinglePlay" or "snapshot_shareMix" => d.Value is not double v3 || measured >= v3,
                    _ => true,
                };
                if (!ok) continue;
                int now = (_progress.TryGetValue(d.Key, out var p) ? p : 0) + Math.Max(1, amount);
                _progress[d.Key] = now;
                if (now >= d.Count)
                {
                    _unlocked.Add(d.Key);
                    var img = d.CardId != null && _card(d.CardId) is { } c ? c.image : null;
                    var u = new Unlock(++_seq, d.Key, Title(d, cardMode), Text(d, cardMode), img, d.Icon ?? (img == null ? d.Playlist : null), DateTime.UtcNow);
                    _recent.Add(u); if (_recent.Count > 10) _recent.RemoveAt(0);
                    Unlocked?.Invoke(u);
                }
            }
            Save();
        }
    }

    /// <summary>Unlocks in the last few seconds (the table shows them as pop-ups).</summary>
    public object Recent() { lock (_lock) return _recent.Where(u => DateTime.UtcNow - u.At < TimeSpan.FromSeconds(6)).ToArray(); }

    public object View(Func<string, string?> cardMode)
    {
        lock (_lock)
            return _defs.GroupBy(d => d.Playlist).Select(g => new
            {
                playlist = g.Key,
                unlocked = g.Count(d => _unlocked.Contains(d.Key)),
                badges = g.OrderBy(d => d.Index).Select(d =>
                {
                    string? mode = d.CardId != null ? cardMode(d.CardId) : null;
                    var img = d.CardId != null && _card(d.CardId) is { } c ? c.image : null;
                    return new { key = d.Key, title = Title(d, mode), text = Text(d, mode), done = _unlocked.Contains(d.Key),
                                 progress = Math.Min(d.Count, _progress.TryGetValue(d.Key, out var p) ? p : 0), count = d.Count, image = img, icon = d.Icon ?? (img == null ? d.Playlist : null) };
                }).ToArray(),
            }).ToArray();
    }

    public void Reset() { lock (_lock) { _progress.Clear(); _unlocked.Clear(); Save(); } }
}
