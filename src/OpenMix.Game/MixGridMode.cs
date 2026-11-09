using System.Text.Json;
using System.Text.RegularExpressions;
using OpenMix.Cards;

namespace OpenMix.Game;

/// <summary>
/// MixGrid: the original's unreleased "test drive" puzzle mode. A 7×7 grid of
/// requests; drag a path through neighbouring requests your card satisfies, the card drops into a mix
/// slot and onto the path's last cell, the requests clear and the grid refills from above. Score is
/// requests × points × the mix multiplier (filled slots: 1–4 → ×n, 5 → ×8). Each level has 1 or 3
/// rounds with a goal; after the last round an encore runs against a timer at double multiplier.
///
/// Everyone at the table shares one hand (the active card and the on-deck card) and anyone can draw
/// a path, from a phone or the table screen. The physical board is ignored, as in the original
/// (readerMode 0).
///
/// OpenMix's own choices (MixGrid was unreleased; its scene settings and tuning tables aren't in the game files;
/// see docs/PROVENANCE.md): hand 2, deck 30, minimum path 1, no explosions, the default request weights below, the
/// level order in data/mixgrid/level_list.csv, the tile-property flags in <see cref="Props"/>, the
/// "NX POINTS FOR …" descriptions applied as points, and score stars for single-round levels.
/// </summary>
public sealed class MixGridMode : GameMode
{
    public override string Id => "mixgrid";
    public override string Title => "MixGrid";

    public const int W = 7, H = 7, HandSize = 2, DeckSize = 30, MinPath = 1, NumPlaylists = 2;
    static readonly TimeSpan RevealTime = TimeSpan.FromSeconds(2.5), GoalDoneTime = TimeSpan.FromSeconds(5), ReshuffleTime = TimeSpan.FromSeconds(0.5);

    static readonly string[] RandomTypes = { "Level1", "Level2", "Level3", "Beat", "Lead", "Loop", "Bass", "Synth", "Guitar", "Vocal", "Drums" };
    static readonly Dictionary<string, int> DefaultWeights = new()
    {
        ["Level1"] = 3, ["Level2"] = 3, ["Level3"] = 2, ["Beat"] = 10, ["Lead"] = 9, ["Loop"] = 10, ["Bass"] = 9,
        ["Synth"] = 2, ["Guitar"] = 2, ["Vocal"] = 2, ["Drums"] = 2,
    };
    static readonly string[] Colours = { "Lead", "Loop", "Beat", "Bass" };

    /// <summary>Tile properties used by the level files, mapped to MixGridTilePropertyPrefabs flags [port guess].</summary>
    [Flags] enum Prop { None = 0, BurnDown = 1, Immovable = 2, InvalidEnd = 4, Absorbant = 8, Target = 16, MustEnd = 32 }
    static readonly Dictionary<string, Prop> Props = new(StringComparer.OrdinalIgnoreCase)
    {
        ["target"] = Prop.Target | Prop.Immovable,
        ["wall"] = Prop.Immovable | Prop.InvalidEnd,
        ["speed_bump"] = Prop.MustEnd,
        ["target_bump"] = Prop.Target | Prop.MustEnd,
        ["falling_goal"] = Prop.Target,           // the target request itself falls (not pinned)
        ["sapper_wall"] = Prop.Immovable | Prop.Absorbant,
    };

    // ======================================================================================
    // level data
    // ======================================================================================

    sealed class RoundDef
    {
        public bool[,] Exists = new bool[W, H];
        public string?[,] Fixed = new string?[W, H];
        public string?[,] PropName = new string?[W, H];
        public string Goal = "Score,0";
    }

    sealed class Level
    {
        public string File = "", Name = "", Sub = "";
        public int Version;
        public int[] Stars = Array.Empty<int>();
        public string Background = ""; public double Zoom = 1, Cx = .5, Cy = .5;
        public List<RoundDef> Rounds = new();
        public Dictionary<string, (int w, int pts)> Requests = new();
        public Dictionary<string, int> Deck = new();
        public Dictionary<string, int> PointBoost = new();
    }

    static Level ParseLevel(string file, string text)
    {
        var lv = new Level { File = file };
        string sec = ""; int shapeRow = 0;
        RoundDef? cur = null;
        RoundDef NewRound() { var r = new RoundDef(); lv.Rounds.Add(r); shapeRow = 0; return r; }
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var c = raw.Trim().Split(',').Select(x => x.Trim()).ToArray();
            string k = c[0];
            switch (k)
            {
                case "Version": case "Name": case "Background": case "Shape": case "Goal": case "Requests":
                case "Deck": case "Stars": case "1 Star": case "Playlists":
                    sec = k; if (k == "Shape") { cur ??= NewRound(); shapeRow = 0; } continue;
                case "Round": cur = NewRound(); sec = ""; continue;
            }
            if (raw.Trim().Length == 0) continue;
            switch (sec)
            {
                case "Version":
                    int.TryParse(k, out lv.Version); if (lv.Version < 4) cur = NewRound(); sec = ""; break;
                case "Name":
                    lv.Name = k; lv.Sub = c.Length > 1 ? c[1] : ""; sec = ""; break;
                case "Stars":
                    lv.Stars = c.Where(x => x.Length > 0).Select(x => int.TryParse(x, out var v) ? v : 0).ToArray(); sec = ""; break;
                case "Background":
                    if (lv.Version >= 2 && k.Length > 0)
                    {
                        lv.Background = k;
                        if (c.Length > 1 && double.TryParse(c[1], System.Globalization.CultureInfo.InvariantCulture, out var z)) lv.Zoom = z;
                        if (c.Length > 2 && double.TryParse(c[2], System.Globalization.CultureInfo.InvariantCulture, out var x)) lv.Cx = x;
                        if (c.Length > 3 && double.TryParse(c[3], System.Globalization.CultureInfo.InvariantCulture, out var y)) lv.Cy = y;
                    }
                    sec = ""; break;
                case "Shape":
                    if (cur == null || shapeRow >= H) break;
                    int yy = H - 1 - shapeRow++;
                    for (int xx = 0; xx < W && xx < c.Length; xx++)
                    {
                        var p = c[xx].Split('|');
                        if (p[0].Length == 0) continue;
                        cur.Exists[xx, yy] = true;
                        if (p.Length > 1 && p[1].Length > 0) cur.Fixed[xx, yy] = p[1];
                        if (p.Length > 2 && p[2].Length > 0) cur.PropName[xx, yy] = p[2];
                    }
                    break;
                case "Goal":
                    if (cur != null) cur.Goal = string.Join(",", c.Where(x => x.Length > 0)); break;
                case "Requests":
                    if (lv.Version >= 3 && k.Length > 0 && c.Length > 1 && int.TryParse(c[1], out var w))
                        lv.Requests[k] = (w, c.Length > 2 && int.TryParse(c[2], out var pts) ? pts : 100);
                    break;
                case "Deck":
                    if (lv.Version >= 3 && k.Length > 0 && c.Length > 1 && int.TryParse(c[1], out var dw)) lv.Deck[k] = dw;
                    break;
            }
        }
        if (lv.Rounds.Count == 0) lv.Rounds.Add(new RoundDef());
        // "NX POINTS FOR RED AND SYNTH REQUESTS" → points for those types, when the file has no Requests rows [port guess]
        var m = Regex.Match(lv.Sub, @"(\d)X POINTS FOR (.+) REQUESTS", RegexOptions.IgnoreCase);
        if (m.Success && lv.Requests.Count == 0)
        {
            int mul = int.Parse(m.Groups[1].Value);
            foreach (var word in m.Groups[2].Value.Split(new[] { " AND ", " " }, StringSplitOptions.RemoveEmptyEntries))
            {
                string? t = word.ToUpperInvariant() switch
                {
                    "RED" => "Loop", "BLUE" => "Beat", "GREEN" => "Bass", "YELLOW" => "Lead",
                    "SYNTH" => "Synth", "GUITAR" => "Guitar", "VOCAL" => "Vocal", "DRUMS" => "Drums", _ => null,
                };
                if (t != null) lv.PointBoost[t] = mul;
            }
        }
        return lv;
    }

    // ======================================================================================
    // state
    // ======================================================================================

    sealed class Req { public long Id; public string Type = ""; public bool Pinned; public bool Target; }
    sealed class GridCard { public Card Card = null!; public int Slot = -1; public long Id; }
    sealed class Cell { public bool Exists; public Prop Prop; public string? PropName; public Req? Req; public GridCard? Card; }

    public enum Phase { Levels, Reveal, Play, GoalDone, Over }
    public override (bool InGame, double Progress) Crowd => (_phase != Phase.Levels, _phase == Phase.Over ? 1 : 0);

    readonly Cell[,] _grid = new Cell[W, H];
    readonly List<string> _levelFiles = new();
    readonly Dictionary<string, Level> _levels = new();
    string? _dataDir, _progressPath;
    Dictionary<string, (int best, int stars, bool beaten, bool skipped)> _progress = new();
    bool _unlockAll;

    Phase _phase = Phase.Levels; DateTime _phaseAt;
    int _levelIdx; Level? _lv; int _round; bool _encore; double _encoreLeft, _encoreLen;
    int _score, _encorePoints, _lastBonus, _starsNow; bool _passed, _newBest;
    string[] _playlists = Array.Empty<string>();
    readonly List<Card> _deck = new(), _hand = new();
    readonly Dictionary<string, int> _cleared = new(); int _targetsCleared;
    long _reqId, _cardId, _evId;
    (long id, string? text) _notice;
    object? _lastPlay;   // {id, cells:[[x,y]], pts:[..], mult, total}
    DateTime _lastTick = DateTime.UtcNow;

    public MixGridMode() { for (int x = 0; x < W; x++) for (int y = 0; y < H; y++) _grid[x, y] = new Cell(); }

    public override void Configure(IDictionary<string, string> o)
    {
        if (o.TryGetValue("mixgridData", out var d)) _dataDir = d;
        if (o.TryGetValue("playlists", out var pl)) _playlists = pl.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _dataDir ??= Path.Combine("data", "mixgrid");
        _progressPath = Path.Combine(_dataDir, "mixgrid_progress.json");
        LoadLevels(); LoadProgress();
    }

    void LoadLevels()
    {
        _levelFiles.Clear(); _levels.Clear();
        string list = Path.Combine(_dataDir!, "level_list.csv"), dir = Path.Combine(_dataDir!, "levels");
        var names = File.Exists(list)
            ? File.ReadAllText(list).Replace("\r", "").Split('\n').Skip(1).Select(l => l.Trim().Split(',')[0].Trim()).Where(n => n.Length > 0)
            : Directory.Exists(dir) ? Directory.GetFiles(dir, "*.txt").Select(Path.GetFileNameWithoutExtension).OfType<string>() : Enumerable.Empty<string>();
        foreach (var n in names)
        {
            var f = Path.Combine(dir, n + ".txt");
            if (!File.Exists(f)) continue;
            try { _levels[n] = ParseLevel(n, File.ReadAllText(f)); _levelFiles.Add(n); } catch { }
        }
    }

    public override void Start()
    {
        _phase = Phase.Levels; _phaseAt = DateTime.UtcNow;
        ClearGrid(); _hand.Clear(); _deck.Clear(); SyncPads();
    }

    public override void DecksChanged() { var l = Playlists(); _playlists = _playlists.Where(l.Contains).ToArray(); }

    IReadOnlyList<string> Playlists()
    {
        var all = T.AllCards.Where(c => c.Icon.Length > 0).GroupBy(c => c.Icon).Where(g => g.Count() == 15).Select(g => g.Key).OrderBy(k => k).ToList();
        var on = all.Where(T.IsDeckActive).ToList();
        return on.Count >= NumPlaylists ? on : all;
    }

    // physical board is off in MixGrid (CardSlots.readerMode = 0)
    public override void BoardPlaced(Card card, int pad) { }
    public override void BoardLifted(Card card, int pad) { }
    public override ActionResult Play(Player p, Card card, int pad) => ActionResult.Fail("In MixGrid, draw a path on the grid.");

    // ======================================================================================
    // actions (phones and the table screen)
    // ======================================================================================

    public override ActionResult Action(Player p, string action, string? arg) => Do(action, arg);
    public override ActionResult HostAction(string action, IDictionary<string, string> args) =>
        Do(action, args.TryGetValue("arg", out var a) ? a : args.Values.FirstOrDefault());

    ActionResult Do(string action, string? arg)
    {
        switch (action)
        {
            case "level":
                if (_phase is not (Phase.Levels or Phase.Over)) return ActionResult.Fail("Finish this level first.");
                if (!int.TryParse(arg, out var li) || li < 0 || li >= _levelFiles.Count) return ActionResult.Fail("No such level.");
                if (!Unlocked(li)) return ActionResult.Fail("Beat or skip the previous level first.");
                _levelIdx = li; _phase = Phase.Levels; return ActionResult.Success;
            case "playlist":
                if (_phase is not (Phase.Levels or Phase.Over)) return ActionResult.Fail("Pick playlists between levels.");
                if (arg == null || !Playlists().Contains(arg)) return ActionResult.Fail("Unknown playlist.");
                var cur = _playlists.ToList();
                if (!cur.Remove(arg)) { cur.Add(arg); if (cur.Count > NumPlaylists) cur.RemoveAt(0); }
                _playlists = cur.ToArray(); return ActionResult.Success;
            case "unlockall": _unlockAll = !_unlockAll; return ActionResult.Success;
            case "start": case "retry":
                if (_phase is not (Phase.Levels or Phase.Over)) return ActionResult.Fail("A level is already running.");
                if (_levelFiles.Count == 0) return ActionResult.Fail("No MixGrid levels found.");
                if (!Unlocked(_levelIdx)) return ActionResult.Fail("That level is locked.");
                BeginLevel(); return ActionResult.Success;
            case "next":
                if (_phase != Phase.Over) return ActionResult.Fail("Not now.");
                if (_levelIdx + 1 >= _levelFiles.Count || !Unlocked(_levelIdx + 1)) return ActionResult.Fail("No next level unlocked.");
                _levelIdx++; BeginLevel(); return ActionResult.Success;
            case "skip":
                if (_phase != Phase.Over || _passed) return ActionResult.Fail("Not now.");
                var pr = Prog(_levelFiles[_levelIdx]); _progress[_levelFiles[_levelIdx]] = (pr.best, pr.stars, pr.beaten, true); SaveProgress();
                if (_levelIdx + 1 < _levelFiles.Count) _levelIdx++;
                _phase = Phase.Levels; return ActionResult.Success;
            case "levels": case "lobby":
                if (_phase is Phase.Play or Phase.Reveal or Phase.GoalDone) EndLevel();
                _phase = Phase.Levels; ClearGrid(); _hand.Clear(); SyncPads(); return ActionResult.Success;
            case "finish":
                if (_phase != Phase.Play) return ActionResult.Fail("Not now.");
                if (_hand.Count > 0) return ActionResult.Fail("You still have cards to play.");
                EndLevel(); return ActionResult.Success;
            case "swap":
                if (_phase != Phase.Play) return ActionResult.Fail("Not now.");
                if (_hand.Count < 2) return ActionResult.Fail("Only one card left.");
                (_hand[0], _hand[1]) = (_hand[1], _hand[0]); return ActionResult.Success;
            case "play":
                if (_phase != Phase.Play) return ActionResult.Fail(_phase == Phase.Levels ? "Pick a level and start." : "Wait a moment…");
                return PlayPath(arg ?? "");
        }
        return ActionResult.Fail("Unknown action.");
    }

    // ======================================================================================
    // flow
    // ======================================================================================

    void BeginLevel()
    {
        _lv = _levels[_levelFiles[_levelIdx]];
        var lists = Playlists();
        var chosen = _playlists.Where(lists.Contains).Distinct().Take(NumPlaylists).ToList();
        while (chosen.Count < NumPlaylists && lists.Count > chosen.Count) { var pick = lists[T.Rng.Next(lists.Count)]; if (!chosen.Contains(pick)) chosen.Add(pick); }
        _playlists = chosen.ToArray();
        _score = 0; _encorePoints = 0; _lastBonus = 0; _round = 0; _encore = false; _starsNow = 0; _passed = false; _newBest = false;
        _cleared.Clear(); _targetsCleared = 0; _lastPlay = null;
        foreach (var pad in T.Pads) pad.Clear();
        ClearGrid();
        BuildDeck();
        BuildRound(false);
        SetPhase(Phase.Reveal);
        T.EmitMusic("lanes_start");
        T.EmitMusic("sfx:request_appear");
    }

    public override void Resumed(TimeSpan d) { _lastTick += d; _phaseAt += d; }
    void SetPhase(Phase p) { _phase = p; _phaseAt = DateTime.UtcNow; }

    public override bool Tick(DateTime now)
    {
        double dt = Math.Min(0.5, (now - _lastTick).TotalSeconds); _lastTick = now;
        switch (_phase)
        {
            case Phase.Reveal when now - _phaseAt >= RevealTime:
                SetPhase(Phase.Play); CheckFlow(); return true;
            case Phase.GoalDone when now - _phaseAt >= GoalDoneTime:
                _round++;
                if (_round < _lv!.Rounds.Count) { BuildRound(true); SetPhase(Phase.Reveal); }
                else
                {
                    _encore = true; _encoreLen = _encoreLeft = Math.Clamp((_deck.Count + _hand.Count) * 10, 10, 60);
                    Notice("ENCORE! Double points until time runs out"); T.EmitMusic("sfx:finale_ping");
                    SetPhase(Phase.Play); CheckFlow();
                }
                return true;
            case Phase.Play when _encore:
                _encoreLeft -= dt;
                if (_encoreLeft <= 0) { _encoreLeft = 0; Notice($"Encore complete: +{_encorePoints:N0}"); EndLevel(); }
                return true;
        }
        return false;
    }

    /// <summary>After a play: goal → GoalDone; out of cards and moves → end; no move → reshuffle.</summary>
    void CheckFlow()
    {
        if (_phase != Phase.Play) return;
        if (!_encore && GoalComplete())
        {
            int mult = Multiplier(false);
            _lastBonus = (int)Math.Round(mult * (_round * 1000.0 + 1000));
            _score += _lastBonus; _starsNow = Math.Max(_starsNow, _round + 1);
            Notice($"GOAL COMPLETE! Bonus +{_lastBonus:N0}");
            T.EmitMusic("sfx:request_success"); T.EmitMusic("party_round");
            SetPhase(Phase.GoalDone); return;
        }
        for (int tries = 0; tries < 8; tries++)
        {
            if (AnyMove()) return;
            if (_hand.Count == 0) { EndLevel(); return; }
            Reshuffle();
        }
        if (!AnyMove()) EndLevel();
    }

    void EndLevel()
    {
        if (_lv == null) return;
        // stars: rounds completed; single-round levels add score stars from the file's Stars row [port guess]
        int stars = _starsNow;
        if (_lv.Rounds.Count == 1 && stars > 0 && _lv.Stars.Length >= 3)
            stars += (_lv.Stars[1] > 0 && _score >= _lv.Stars[1] ? 1 : 0) + (_lv.Stars[2] > 0 && _score >= _lv.Stars[2] ? 1 : 0);
        stars = Math.Min(3, stars);
        _starsNow = stars; _passed = stars > 0;
        var key = _lv.File; var pr = Prog(key);
        _newBest = _score > pr.best;
        _progress[key] = (Math.Max(pr.best, _score), Math.Max(pr.stars, stars), pr.beaten || _passed, pr.skipped);
        SaveProgress();
        _encore = false;
        SetPhase(Phase.Over);
        T.EmitMusic("lanes_end");
        T.EmitMusic(_passed ? "sfx:outro_puzzle" : "sfx:request_failure");
    }

    // ======================================================================================
    // grid build, gravity, refill
    // ======================================================================================

    void ClearGrid()
    {
        for (int x = 0; x < W; x++) for (int y = 0; y < H; y++) _grid[x, y] = new Cell();
    }

    void BuildRound(bool keepCards)
    {
        var rd = _lv!.Rounds[_round];
        var carried = new List<(int x, int y, GridCard c)>();
        if (keepCards)
            for (int x = 0; x < W; x++) for (int y = 0; y < H; y++) if (_grid[x, y].Card is { } gc) carried.Add((x, y, gc));
        ClearGrid();
        for (int x = 0; x < W; x++) for (int y = 0; y < H; y++)
        {
            var cell = _grid[x, y];
            cell.Exists = rd.Exists[x, y];
            if (!cell.Exists) continue;
            cell.PropName = rd.PropName[x, y];
            cell.Prop = cell.PropName != null && Props.TryGetValue(cell.PropName, out var pf) ? pf : Prop.None;
            if (rd.Fixed[x, y] != null || cell.PropName != null)
            {
                bool falling = cell.PropName?.Equals("falling_goal", StringComparison.OrdinalIgnoreCase) == true;
                cell.Req = NewReq(rd.Fixed[x, y] ?? RandomType(), pinned: !falling, target: (cell.Prop & Prop.Target) != 0);
            }
        }
        foreach (var (x, y, c) in carried)
        {
            var cell = _grid[x, y];
            if (cell.Exists && cell.Req == null && cell.Card == null && (cell.Prop & Prop.Immovable) == 0) { cell.Card = c; continue; }
            bool placed = false;
            for (int yy = 0; yy < H && !placed; yy++)
            {
                var d = _grid[x, yy];
                if (d.Exists && d.Req == null && d.Card == null && (d.Prop & Prop.Immovable) == 0) { d.Card = c; placed = true; }
            }
            if (!placed) { c.Slot = -1; T.Discard(c.Card); }
        }
        Refill();
        SyncPads();
        Notice($"ROUND {_round + 1}/{_lv.Rounds.Count} · {GoalText()}");
    }

    Req NewReq(string type, bool pinned = false, bool target = false) => new() { Id = NextId(), Type = type, Pinned = pinned, Target = target };

    string RandomType()
    {
        int total = RandomTypes.Sum(Weight);
        if (total <= 0) return "Drums";
        int r = T.Rng.Next(total);
        foreach (var t in RandomTypes) { r -= Weight(t); if (r < 0) return t; }
        return "Drums";
    }
    int Weight(string t) => _lv != null && _lv.Requests.TryGetValue(t, out var e) ? e.w : DefaultWeights.GetValueOrDefault(t);
    int Points(string t)
    {
        int pts = _lv != null && _lv.Requests.TryGetValue(t, out var e) ? e.pts : 100;
        return pts * (_lv?.PointBoost.GetValueOrDefault(t, 1) ?? 1);
    }

    bool Movable(Cell c) => c.Exists && (c.Prop & Prop.Immovable) == 0 && !(c.Req?.Pinned ?? false);

    /// <summary>Per column: movable content drops into empty cells below it, passing holes; pinned or
    /// immovable cells are barriers.</summary>
    void Gravity()
    {
        for (int x = 0; x < W; x++)
            for (int y = 0; y < H; y++)
            {
                var c = _grid[x, y];
                if (!c.Exists || (c.Prop & Prop.Immovable) != 0 || c.Req != null || c.Card != null) continue;
                for (int yy = y + 1; yy < H; yy++)
                {
                    var a = _grid[x, yy];
                    if (!a.Exists) continue;
                    if (!Movable(a)) break;
                    if (a.Req == null && a.Card == null) continue;
                    c.Req = a.Req; c.Card = a.Card; a.Req = null; a.Card = null;
                    break;
                }
            }
    }

    void Refill()
    {
        Gravity();
        for (int x = 0; x < W; x++) for (int y = 0; y < H; y++)
        {
            var c = _grid[x, y];
            if (c.Exists && c.Req == null && c.Card == null) c.Req = NewReq(RandomType());
        }
    }

    void Reshuffle()
    {
        for (int x = 0; x < W; x++) for (int y = 0; y < H; y++)
        {
            var c = _grid[x, y];
            if (c.Exists && c.Card == null && (c.Prop & Prop.Immovable) == 0 && !(c.Req?.Target ?? false)) c.Req = null;
        }
        Refill();
        Notice("Reshuffling…");
    }

    // ======================================================================================
    // deck & hand
    // ======================================================================================

    void BuildDeck()
    {
        _deck.Clear(); _hand.Clear();
        var pool = _playlists.SelectMany(icon => T.AllCards.Where(c => c.Icon == icon)).Distinct().ToList();
        if (pool.Count < DeckSize) pool.AddRange(T.AllCards.Where(c => c.Icon == "sweets" && !pool.Contains(c)));
        int Wt(Card c) { string k = (c.Type == CardType.Fx ? "FX" : c.Type.ToString()) + Math.Clamp(c.Power, 1, 3); return _lv!.Deck.TryGetValue(k, out var w) ? w : 1; }
        var bag = pool.ToList();
        while (_deck.Count < DeckSize && bag.Count > 0)
        {
            int total = bag.Sum(Wt);
            if (total <= 0) break;
            int r = T.Rng.Next(total), i = 0;
            for (; i < bag.Count; i++) { r -= Wt(bag[i]); if (r < 0) break; }
            i = Math.Min(i, bag.Count - 1);
            _deck.Add(bag[i]); bag.RemoveAt(i);
        }
        T.Shuffle(_deck);
        DrawHand();
    }

    /// <summary>Refill to HandSize, each time taking the best-scoring deck card.</summary>
    void DrawHand()
    {
        var shapeTypes = _lv!.Rounds[Math.Min(_round, _lv.Rounds.Count - 1)].Fixed.Cast<string?>().Where(s => s != null).ToHashSet();
        while (_hand.Count < HandSize && _deck.Count > 0)
        {
            int best = -1, bi = 0;
            for (int i = 0; i < _deck.Count; i++)
            {
                var c = _deck[i]; int s = 0;
                if (_hand.Count == 0 && shapeTypes.Any(t => Options(c).Any(o => Satisfies(t!, c, o)))) s += 8;
                if (_hand.Count > 0 && c.Type != _hand[0].Type) s += 4;
                if (Enumerable.Range(0, Rules.Slots).Any(sl => T.Pads[sl].Count == 0 && Rules.IsTypeValidInSlot(c.Type, sl))) s += 2;
                if (c.Type is not (CardType.Wild or CardType.Fx) && !Enumerable.Range(0, Rules.Slots).Any(sl => T.SoundingTop(sl)?.Type == c.Type)) s += 1;
                if (s > best) { best = s; bi = i; }
            }
            _hand.Add(_deck[bi]); _deck.RemoveAt(bi);
        }
    }

    // ======================================================================================
    // requests & options
    // ======================================================================================

    readonly record struct Opt(int Slot, string Colour);

    static IEnumerable<Opt> Options(Card c)
    {
        for (int s = 0; s < Rules.Slots; s++)
        {
            if (!Rules.IsTypeValidInSlot(c.Type, s)) continue;
            if (c.Type is CardType.Wild or CardType.Fx) foreach (var t in Rules.SlotAccepts[s]) yield return new Opt(s, t.ToString());
            else yield return new Opt(s, c.Type.ToString());
        }
    }

    static bool Satisfies(string req, Card c, Opt o)
    {
        switch (req)
        {
            case "Level1": return c.Power == 1;
            case "Level2": return c.Power == 2;
            case "Level3": return c.Power == 3;
            case "Lead": case "Loop": case "Beat": case "Bass": return o.Colour == req;
            case "FX": return c.Type == CardType.Fx;
        }
        if (req.StartsWith("Slot") && int.TryParse(req[4..], out var sn)) return o.Slot == sn;
        return HasInstrument(c, o.Slot, req);
    }

    /// <summary>A Wild's Instrument 1..4 are its per-colour variants (party.md); other cards have one.</summary>
    static bool HasInstrument(Card c, int slot, string inst)
    {
        var ins = c.Instruments;
        if (c.Type != CardType.Wild) return ins.Any(i => i.Equals(inst, StringComparison.OrdinalIgnoreCase));
        if (ins.Length != 4) return false;
        int[] idx = slot switch { 0 => new[] { 3, 2 }, 1 => new[] { 2 }, 2 => new[] { 2, 1 }, 3 => new[] { 1 }, _ => new[] { 1, 0 } };
        return idx.Any(i => ins[i].Equals(inst, StringComparison.OrdinalIgnoreCase));
    }

    static bool Adjacent((int x, int y) a, (int x, int y) b) => Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.y - b.y)) == 1;
    bool In(int x, int y) => x >= 0 && x < W && y >= 0 && y < H;

    bool AnyMove()
    {
        bool CanEnd(Cell c) => c.Exists && c.Card == null && c.Req != null && (c.Prop & Prop.InvalidEnd) == 0;
        foreach (var card in _hand)
            for (int x = 0; x < W; x++) for (int y = 0; y < H; y++)
                if (CanEnd(_grid[x, y]) && Options(card).Any(o => Satisfies(_grid[x, y].Req!.Type, card, o))) return true;
        for (int x = 0; x < W; x++) for (int y = 0; y < H; y++)
        {
            if (_grid[x, y].Card is not { } gc) continue;
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
                if ((dx, dy) != (0, 0) && In(x + dx, y + dy) && CanEnd(_grid[x + dx, y + dy]) && Options(gc.Card).Any(o => Satisfies(_grid[x + dx, y + dy].Req!.Type, gc.Card, o))) return true;
        }
        return false;
    }

    int Multiplier(bool forScoring)
    {
        int n = Enumerable.Range(0, Rules.Slots).Count(s => T.Pads[s].Count > 0);
        int m = n == 0 ? 1 : n == 5 ? 8 : n;
        return _encore ? m * 2 : m;
    }

    // ======================================================================================
    // play a path
    // ======================================================================================

    ActionResult PlayPath(string arg)
    {
        var pts = new List<(int x, int y)>();
        foreach (var part in arg.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var xy = part.Split(',');
            if (xy.Length != 2 || !int.TryParse(xy[0], out var x) || !int.TryParse(xy[1], out var y) || !In(x, y)) return ActionResult.Fail("Bad path.");
            pts.Add((x, y));
        }
        if (pts.Count == 0) return ActionResult.Fail("Draw a path through the requests.");

        // a path that starts on a grid card moves that card
        GridCard? moving = null; (int x, int y) origin = default;
        if (_grid[pts[0].x, pts[0].y].Card is { } gc0) { moving = gc0; origin = pts[0]; pts.RemoveAt(0); }
        var card = moving?.Card ?? (_hand.Count > 0 ? _hand[0] : null);
        if (card == null) return ActionResult.Fail("No cards left.");

        var opts = Options(card).ToList();
        (int x, int y)? prev = moving != null ? origin : null;
        var seen = new HashSet<(int, int)>();
        foreach (var p in pts)
        {
            var c = _grid[p.x, p.y];
            if (!c.Exists || c.Card != null || c.Req == null) return ActionResult.Fail("A path can only cross open requests.");
            if (!seen.Add(p)) return ActionResult.Fail("A path can't cross itself.");
            if (prev is { } pv)
            {
                if (!Adjacent(pv, p)) return ActionResult.Fail("Each step must touch the last one.");
                if (!(moving != null && pv == origin) && (_grid[pv.x, pv.y].Prop & Prop.MustEnd) != 0) return ActionResult.Fail("That tile ends the path.");
            }
            opts = opts.Where(o => Satisfies(c.Req.Type, card, o)).ToList();
            if (opts.Count == 0) return ActionResult.Fail($"“{card.Name}” can't meet {Prompt(c.Req.Type)}.");
            prev = p;
        }
        while (pts.Count > 0 && (_grid[pts[^1].x, pts[^1].y].Prop & Prop.InvalidEnd) != 0) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < MinPath) return ActionResult.Fail("A path can't end on a wall.");

        // pick the highest-priority slot option: empty slot 2, odd slot 1
        var opt = opts.OrderByDescending(o => (T.Pads[o.Slot].Count == 0 ? 2 : 0) | (o.Slot % 2 == 1 ? 1 : 0)).ThenBy(o => o.Slot).First();

        // the card enters the mix slot; a grid card that held that slot moves to a free valid slot
        GridCard gcard = moving ?? new GridCard { Card = card, Id = NextId() };
        if (moving == null) _hand.RemoveAt(0);
        foreach (var other in AllGridCards().ToList())
            if (other != gcard && other.Slot == opt.Slot)
            {
                var free = Enumerable.Range(0, Rules.Slots).FirstOrDefault(s => s != opt.Slot && !AllGridCards().Any(g => g.Slot == s) && Rules.IsTypeValidInSlot(other.Card.Type, s), -1);
                other.Slot = free;
            }
        gcard.Slot = opt.Slot;
        SyncPads(gcard);   // the played card isn't on the grid yet but already sounds (and counts for the multiplier)

        // score
        int mult = Multiplier(true), total = 0;
        var perCell = new List<int>();
        foreach (var p in pts)
        {
            var r = _grid[p.x, p.y].Req!;
            int v = Points(r.Type) * mult; total += v; perCell.Add(v);
            _cleared[r.Type] = _cleared.GetValueOrDefault(r.Type) + 1;
            if (r.Target) _targetsCleared++;
            _grid[p.x, p.y].Req = null;
        }
        _score += total; if (_encore) _encorePoints += total;
        _lastPlay = new { id = NextId(), cells = pts.Select(p => new[] { p.x, p.y }).ToArray(), pts = perCell, mult, total, card = card.Name };

        // place the card on the last cell (absorbed on an absorbant tile)
        if (moving != null) _grid[origin.x, origin.y].Card = null;
        var end = _grid[pts[^1].x, pts[^1].y];
        if ((end.Prop & Prop.Absorbant) != 0) { gcard.Slot = -1; T.Discard(card); }
        else end.Card = gcard;
        // burnDown tiles drop their cards
        for (int x = 0; x < W; x++) for (int y = 0; y < H; y++)
            if ((_grid[x, y].Prop & Prop.BurnDown) != 0 && _grid[x, y].Card is { } b) { b.Slot = -1; _grid[x, y].Card = null; }
        Refill();
        SyncPads();
        if (moving == null) DrawHand();

        T.EmitMusic(pts.Count >= 3 ? "sfx:blockclear" : "sfx:request_success");
        CheckFlow();
        return ActionResult.Success;
    }

    IEnumerable<GridCard> AllGridCards()
    {
        for (int x = 0; x < W; x++) for (int y = 0; y < H; y++) if (_grid[x, y].Card is { } c) yield return c;
    }

    /// <summary>Mirror the grid cards' slots into the table's 5 mix slots (drives the music).</summary>
    void SyncPads(GridCard? extra = null)
    {
        var bySlot = new GridCard?[Rules.Slots];
        foreach (var g in AllGridCards().Append(extra)) if (g != null && g.Slot is >= 0 and < Rules.Slots) bySlot[g.Slot] = g;
        for (int s = 0; s < Rules.Slots; s++)
        {
            var want = bySlot[s]?.Card;
            var have = T.Pads[s].Count > 0 ? T.Pads[s][^1].Card : null;
            if (ReferenceEquals(want, have) && T.Pads[s].Count <= 1) continue;
            T.Pads[s].Clear();
            if (want != null) T.Pads[s].Add(new PadCard { Card = want, Source = CardSource.Board });   // Board: never sent to the discard pile by PopTop
        }
    }

    // ======================================================================================
    // goals
    // ======================================================================================

    string[] GoalParts() => _encore || _lv == null ? Array.Empty<string>() : _lv.Rounds[Math.Min(_round, _lv.Rounds.Count - 1)].Goal.Split(',');

    List<(string type, int cur, int target)> GoalItems()
    {
        var g = GoalParts(); var res = new List<(string, int, int)>();
        if (g.Length == 0) return res;
        switch (g[0])
        {
            case "Score": res.Add(("Score", _score, g.Length > 1 && int.TryParse(g[1], out var n) ? n : 0)); break;
            case "Targets": res.Add(("Targets", _targetsCleared, g.Length > 1 && int.TryParse(g[1], out var tn) ? tn : 0)); break;
            case "RequestTypeCount":
                for (int i = 1; i + 1 < g.Length; i += 2)
                    if (int.TryParse(g[i + 1], out var cnt)) res.Add((g[i], _cleared.GetValueOrDefault(g[i]), cnt));
                break;
        }
        return res;
    }

    bool GoalComplete() { var g = GoalItems(); return g.Count > 0 && g.All(i => i.cur >= i.target); }

    string GoalText()
    {
        var g = GoalItems();
        if (g.Count == 0) return "Score as much as you can";
        if (g[0].type == "Score") return $"Score {g[0].target:N0}";
        if (g[0].type == "Targets") return $"Collect {g[0].target} targets";
        return "Clear " + string.Join(" + ", g.Select(i => $"{i.target} {Prompt(i.type)}"));
    }

    static string Prompt(string t) => t switch
    {
        "Level1" => "level 1", "Level2" => "level 2", "Level3" => "level 3",
        "Beat" => "blue", "Bass" => "green", "Loop" => "red", "Lead" => "yellow",
        _ when t.StartsWith("Slot") => "slot " + (int.TryParse(t[4..], out var n) ? n + 1 : 0),
        _ => t.ToLowerInvariant(),
    };

    void Notice(string text) => _notice = (NextId(), text);

    // ======================================================================================
    // progress (PlayerPrefs mixgrid_<name>_*)
    // ======================================================================================

    (int best, int stars, bool beaten, bool skipped) Prog(string name) => _progress.GetValueOrDefault(name);

    bool Unlocked(int i)
    {
        if (i == 0 || _unlockAll) return true;
        if (i < 0 || i >= _levelFiles.Count) return false;
        var p = Prog(_levelFiles[i - 1]);
        return p.beaten || p.skipped;
    }

    void LoadProgress()
    {
        _progress.Clear();
        try
        {
            if (_progressPath == null || !File.Exists(_progressPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_progressPath));
            foreach (var e in doc.RootElement.EnumerateObject())
                _progress[e.Name] = (e.Value.GetProperty("best").GetInt32(), e.Value.GetProperty("stars").GetInt32(),
                    e.Value.GetProperty("beaten").GetBoolean(), e.Value.GetProperty("skipped").GetBoolean());
        }
        catch { }
    }

    void SaveProgress()
    {
        if (_progressPath == null) return;
        try
        {
            File.WriteAllText(_progressPath, JsonSerializer.Serialize(_progress.ToDictionary(k => k.Key,
                k => new { best = k.Value.best, stars = k.Value.stars, beaten = k.Value.beaten, skipped = k.Value.skipped })));
        }
        catch { }
    }

    // ======================================================================================
    // view
    // ======================================================================================

    static object CardView(Card c) => new
    {
        cid = c.Cid, name = c.Name, artist = c.Artist, type = c.Type.ToString(), power = c.Power, instruments = c.Instruments, image = c.ImageName,
    };

    static string DisplayName(string n) => Regex.Replace(n, @"^(\*\s*|\d+\.\s*)", "").Trim();

    string? BackgroundImage()
    {
        if (_lv == null || _lv.Background.Length == 0) return null;
        var c = T.AllCards.FirstOrDefault(x => x.Cid.Equals(_lv.Background, StringComparison.OrdinalIgnoreCase) || x.SourceCid.Equals(_lv.Background, StringComparison.OrdinalIgnoreCase));
        return c?.ImageName;
    }

    public override object View(Player? viewer)
    {
        bool lobby = _phase is Phase.Levels or Phase.Over;
        var sel = _levelFiles.Count > 0 ? _levels[_levelFiles[Math.Clamp(_levelIdx, 0, _levelFiles.Count - 1)]] : null;
        var lv = lobby && _phase == Phase.Levels ? sel : _lv ?? sel;
        object?[][]? grid = null;
        if (!lobby || _phase == Phase.Over)
        {
            grid = new object?[H][];
            for (int row = 0; row < H; row++)
            {
                int y = H - 1 - row; grid[row] = new object?[W];
                for (int x = 0; x < W; x++)
                {
                    var c = _grid[x, y];
                    if (!c.Exists) continue;
                    grid[row][x] = new
                    {
                        x, y, t = c.Req?.Type, rid = c.Req?.Id, pin = c.Req?.Pinned ?? false, tg = c.Req?.Target ?? false,
                        p = c.PropName, mustEnd = (c.Prop & Prop.MustEnd) != 0, noEnd = (c.Prop & Prop.InvalidEnd) != 0,
                        absorb = (c.Prop & Prop.Absorbant) != 0, wall = (c.Prop & Prop.Immovable) != 0,
                        card = c.Card == null ? null : new { id = c.Card.Id, slot = c.Card.Slot, c = CardView(c.Card.Card) },
                        pts = c.Req == null ? 0 : Points(c.Req.Type),
                    };
                }
            }
        }
        return new
        {
            id = Id, title = Title,
            subtitle = _phase switch
            {
                Phase.Levels => "Pick a level",
                Phase.Over => _passed ? "Level passed" : "Level failed",
                _ => _lv == null ? "" : DisplayName(_lv.Name),
            },
            phase = _phase switch { Phase.Levels => "lobby", Phase.Reveal => "reveal", Phase.GoalDone => "goal", Phase.Over => "over", _ => "play" },
            canPlay = false, waitText = "Draw a path on the grid", allowDraw = false, allowClear = false, allowUndo = false,
            slots = SlotViews(),
            levels = lobby ? _levelFiles.Select((f, i) =>
            {
                var l = _levels[f]; var p = Prog(f);
                return new { i, name = DisplayName(l.Name), sub = l.Sub, rounds = l.Rounds.Count, best = p.best, stars = p.beaten ? p.stars : 0, beaten = p.beaten, skipped = p.skipped && !p.beaten, unlocked = Unlocked(i) };
            }).ToArray() : null,
            levelIdx = _levelIdx, unlockAll = _unlockAll,
            level = lv == null ? null : new { name = DisplayName(lv.Name), sub = lv.Sub, rounds = lv.Rounds.Count, stars = lv.Stars, number = _levelIdx + 1, bg = BackgroundImage(), zoom = lv.Zoom, cx = lv.Cx, cy = lv.Cy },
            playlists = _playlists, allPlaylists = lobby ? Playlists() : null, numPlaylists = NumPlaylists,
            round = _round, rounds = _lv?.Rounds.Count ?? 1,
            goal = new { text = _encore ? "ENCORE" : GoalText(), items = GoalItems().Select(i => new { type = i.type, label = i.type is "Score" or "Targets" ? i.type : Prompt(i.type), cur = i.cur, target = i.target }).ToArray(), done = _phase == Phase.GoalDone },
            score = _score, mult = Multiplier(false), encore = _encore, encoreLeft = _encore ? _encoreLeft : (double?)null, encoreLen = _encoreLen, encorePoints = _encorePoints,
            bonus = _lastBonus,
            grid,
            hand = _hand.Select(CardView).ToArray(), deckLeft = _deck.Count,
            last = _lastPlay,
            result = _phase == Phase.Over ? new { passed = _passed, stars = _starsNow, best = Prog(_lv?.File ?? "").best, newBest = _newBest, hasNext = _levelIdx + 1 < _levelFiles.Count && Unlocked(_levelIdx + 1), canSkip = !_passed && _levelIdx + 1 < _levelFiles.Count } : null,
            notice = new { id = _notice.id, text = _notice.text },
        };
    }
}
