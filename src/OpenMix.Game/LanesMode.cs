using System.Text.Json;
using OpenMix.Cards;

namespace OpenMix.Game;

/// <summary>
/// Lanes, shown to players as "Puzzle". Each slot owns a lane of 8 cells.
/// Requests (colour or level) arrive at the far end in beat-synced spawn events and march toward
/// the slot. Playing a card clears matching requests in that slot's lane; equal neighbours form
/// blocks that score size × tier (gold and rainbow tiers past their thresholds). After the round's actions
/// comes a Finale, then a bonus for the biggest block. Scores, thresholds, timers and spawn odds come from the
/// user's own game (data/lanes/lanes_config.json). The original is single-player; here everyone at the table
/// plays together on one score.
///
/// Approximation: the original moves cross-lane blocks rigidly. Here each lane
/// compacts on its own and blocks are re-merged after every spawn event and clear.
/// </summary>
public sealed class LanesMode : GameMode
{
    public override string Id => "lanes";
    public override string Title => "Puzzle";

    // ---- rules (program logic) ----
    public const int Cells = 8, HandSize = 6;      // LanesRequestMgr.kWidth (a compiled constant); the hand the port deals
    const int HardPulses = 9;
    static readonly TimeSpan SpinTime = TimeSpan.FromSeconds(3.5), WheelShow = TimeSpan.FromSeconds(2.0);
    static readonly TimeSpan ClearReveal = TimeSpan.FromSeconds(1.5), ShuffleTime = TimeSpan.FromSeconds(2.5);
    static readonly TimeSpan FullClearDelay = TimeSpan.FromSeconds(1.9);      // LanesSlot.LaunchFullClearScore
    readonly List<(DateTime at, int pts, int lane)> _pendingFullClear = new();
    const string SeedIcon = "sweets";   // LanesDeckSelect.GetRequestSeedCards: "sweets" ×2, whatever was picked

    // ---- scene values, all from lanes_config.json (imported at setup); a game can't start without them ----
    /// <summary>LanesGameMgr, LanesRequestMgr and LanesGameEndScreen settings.</summary>
    sealed record Tuning(int Actions, int GoldAt, double GoldMult, int RainbowAt, double RainbowMult,
        double NormalTimer, double HardTimer, double Slop, int FullClearPerRequest, int BiggestBlockBonus, int HardUnlockScore,
        int NormalPulses, int EmptyLaneBonus, TimeSpan GameOverTime, double SuccessorWeight1, double SuccessorWeight2,
        double YellowGreenBoost, IReadOnlyDictionary<string, (int Score, double Frequency)> Requests);
    Tuning _cfg = null!;
    string? _configError = "Puzzle settings aren't loaded.";

    public enum Phase { Lobby, Shuffle, Spawning, Play, Spinning, Finale, Over }
    DateTime _overflowAt = DateTime.MinValue;
    public override LightCue Lights =>
        _phase == Phase.Lobby ? new LightCue(LightScene.RegistrationLanes, Side.None, new[] { false, false, T.Players.Count() > 0, false, false })
        : _phase == Phase.Shuffle ? new LightCue(LightScene.GameStarting)
        : DateTime.UtcNow - _overflowAt < TimeSpan.FromSeconds(2.4) ? new LightCue(LightScene.Overflow, Side.Red, Serial: _overflowAt.Ticks)
        : LightCue.Normal;
    public override (bool InGame, double Progress) Crowd => (_phase != Phase.Lobby, _phase == Phase.Over ? 1 : 0);
    public override bool AcceptsBoardCards => _phase != Phase.Lobby;

    sealed class Req
    {
        public long Id; public string Type = ""; public bool Danger;   // Type: Beat/Bass/Loop/Lead/Level1..3
        public int Block;          // block id after merge (0 = none yet)
        public int Cell;           // 0 = the overflow edge next to the slot, 7 = the spawn end; may go < 0 before an overflow
    }

    // lane i: requests ordered from the FRONT (nearest the slot, lowest cell) to the back. Cells are explicit:
    // requests pack toward the spawn end (cell 7) as rigid blocks, so a lane can have gaps.
    readonly List<Req>[] _lanes = Enumerable.Range(0, Rules.Slots).Select(_ => new List<Req>()).ToArray();
    readonly bool[] _dangerSpawn = new bool[Rules.Slots];   // red warning: next event may overflow it

    Phase _phase = Phase.Lobby; DateTime _phaseAt;
    bool _hard; bool _hardUnlocked; string[] _playlists = Array.Empty<string>();
    int _score, _actionsLeft, _biggestBlock; long _reqId, _eventId, _spinId;
    int _white, _gold, _rainbow, _whitePts, _goldPts, _rainbowPts, _fullClears, _fullClearPts, _requestsMet;
    bool _finaleWarned;

    // spawn event
    int _pulsesLeft, _bonusPulsesLeft; bool[] _incomingEmpty = new bool[Rules.Slots]; bool[] _hadFront = new bool[Rules.Slots];
    double _eighthAcc;
    // turn timer (null = not running)
    double? _timer; double _timerLen;
    // wheel
    int _wheelResult; DateTime _wheelAt; bool _wheelLanded;
    // end screens
    int _endBonus; bool _unlockedNow; bool _submitted; string _leaderName = "";

    readonly List<ScoreEvent> _events = new();
    readonly List<object> _clears = new();     // recent cleared cells for the UI: {id, lane, cells[], kind}
    (long id, string? text) _notice;
    readonly HashSet<string> _playedCids = new(); int _deckSize = 30;
    List<Card> _seedCards = new();
    DateTime _lastTick = DateTime.UtcNow;
    string? _leaderboardPath;
    List<(string name, int score, int difficulty, int requests)> _board = new();

    // ======================================================================================
    // setup
    // ======================================================================================

    public override void Configure(IDictionary<string, string> o)
    {
        if (o.TryGetValue("leaderboard", out var lb)) _leaderboardPath = lb;
        LoadConfig(o.TryGetValue("lanesData", out var ld) ? ld : null);
        if (o.TryGetValue("hard", out var h)) _hard = h is "1" or "true";
        if (o.TryGetValue("playlists", out var pl)) _playlists = pl.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        LoadBoard();
    }

    /// <summary>Reads lanes_config.json. Without it (or with a field missing) <see cref="_configError"/> says so and
    /// no game starts.</summary>
    void LoadConfig(string? dir)
    {
        const string Rerun = "Run setup again to import your game's Puzzle settings.";
        if (dir == null) { _configError = "Puzzle settings folder isn't set. " + Rerun; return; }
        var p = Path.Combine(dir, "lanes_config.json");
        try
        {
            if (!File.Exists(p)) { _configError = $"Puzzle settings are missing ({p}). " + Rerun; return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            var r = doc.RootElement;
            JsonElement gm = r.GetProperty("LanesGameMgr"), rm = r.GetProperty("LanesRequestMgr");
            static double D(JsonElement o, string k) => o.GetProperty(k).GetDouble();
            static int I(JsonElement o, string k) => o.GetProperty(k).GetInt32();
            // AnimationCurve [time, value] keys; the shipped successor curves are flat, so their first key is the weight
            static double Curve0(JsonElement curve) => curve[0][1].GetDouble();
            var succ = rm.GetProperty("successorFrequencies");
            // request types by name (the dumped enum) -> score and spawn frequency
            var names = r.GetProperty("requestTypeEnum").EnumerateObject().ToDictionary(x => x.Value.GetInt32(), x => x.Name);
            var reqs = new Dictionary<string, (int, double)>();
            foreach (var q in rm.GetProperty("requestInfo").EnumerateArray())
                if (names.TryGetValue(I(q, "type"), out var n)) reqs[n] = (I(q, "score"), D(q, "frequency"));
            _cfg = new Tuning(gm.GetProperty("playsPerRound")[0].GetInt32(), I(gm, "goldBlockThreshold"), D(gm, "goldBlockBonus"),
                I(gm, "rainbowBlockThreshold"), D(gm, "rainbowBlockBonus"), D(gm, "endTurnTimer"), D(gm, "hardModeEndTurnTimer"),
                D(gm, "endTurnSlopTime"), I(gm, "laneClearBonusPerRequest"), I(gm, "blockSizeBonusMultiplier"), I(gm, "scoreToUnlockHardMode"),
                I(rm, "pulsesPerStep"), I(rm, "emptyLaneBonusSpawns"), TimeSpan.FromSeconds(D(r.GetProperty("LanesGameEndScreen"), "gameOverScreenSeconds")),
                Curve0(succ[1]), Curve0(succ[2]), D(rm, "yellowGreenSuccessorBoost"), reqs);
            var missing = new[] { "Level1", "Level2", "Level3", "Beat", "Loop", "Bass", "Lead" }.Where(t => !reqs.ContainsKey(t)).ToList();
            _configError = missing.Count > 0 ? $"Puzzle settings have no request info for {string.Join(", ", missing)}. " + Rerun : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
        {
            _configError = $"Puzzle settings couldn't be read ({ex.Message}). " + Rerun;
        }
    }

    /// <summary>Playlists: series icons with exactly 15 cards (others fall back to sweets), limited
    /// to the active decks (all of them if fewer than two playlists are active).</summary>
    public IReadOnlyList<string> Playlists()
    {
        var all = T.AllCards.Where(c => c.Icon.Length > 0).GroupBy(c => c.Icon)
            .Where(g => g.Count() == 15).Select(g => g.Key).OrderBy(k => k).ToList();
        var on = all.Where(T.IsDeckActive).ToList();
        return on.Count >= 2 ? on : all;
    }

    public override void DecksChanged()
    {
        var lists = Playlists();
        _playlists = _playlists.Where(lists.Contains).ToArray();
    }

    IEnumerable<Card> PlaylistCards(string icon)
    {
        var l = T.AllCards.Where(c => c.Icon == icon).ToList();
        return l.Count == 15 ? l : T.AllCards.Where(c => c.Icon == SeedIcon);
    }

    public override void Start()
    {
        _phase = Phase.Lobby;
        T.ClearBoardPlayers();
        if (BoardMustBeCleared()) { Notice("Remove all cards from the board to start."); return; }
        foreach (var l in _lanes) l.Clear();
        Array.Clear(_dangerSpawn);
        _events.Clear(); _clears.Clear();
    }

    // "ADD YOUR DECK TO JOIN": the lobby offers a single join on the centre slot (the lights mark slot 3).
    public override void BoardSetup(Card card, int pad)
    {
        if (_phase == Phase.Lobby && pad == 2 && T.Players.Count(p => p.Status != PlayerStatus.Orphaned) < 5)
            T.SeatBoardPlayer(pad);
    }


    void BeginGame()
    {
        if (_configError != null) { Notice(_configError); return; }
        _pendingFullClear.Clear();
        var lists = Playlists();
        var chosen = _playlists.Where(lists.Contains).Distinct().Take(2).ToList();
        while (chosen.Count < 2 && lists.Count > chosen.Count)
        {
            var pick = lists[T.Rng.Next(lists.Count)];
            if (!chosen.Contains(pick)) chosen.Add(pick);
        }
        _playlists = chosen.ToArray();
        _leaderName = chosen.Count > 0 ? Pretty(chosen[0]) : "Puzzle";

        // board, deck and hands
        foreach (var p in T.Pads) p.Clear();
        T.Deck.Clear(); T.DiscardPile.Clear();
        foreach (var icon in chosen) T.Deck.AddRange(PlaylistCards(icon));
        T.Shuffle(T.Deck); _deckSize = T.Deck.Select(c => c.Cid).Distinct().Count();
        foreach (var pl in T.Players) { pl.Hand.Clear(); T.DrawTo(pl, HandSize); }

        var seed = PlaylistCards(SeedIcon).ToList();
        _seedCards = seed.Concat(seed).ToList();

        foreach (var l in _lanes) l.Clear();
        Array.Clear(_dangerSpawn);
        _score = 0; _actionsLeft = _cfg.Actions; _biggestBlock = 0; _requestsMet = 0;
        _white = _gold = _rainbow = _whitePts = _goldPts = _rainbowPts = _fullClears = _fullClearPts = 0;
        _finaleWarned = false; _timer = null; _endBonus = 0; _unlockedNow = false; _submitted = false;
        _playedCids.Clear(); _events.Clear(); _clears.Clear();
        SetPhase(Phase.Shuffle);
        T.EmitMusic("lanes_start");
        Notice(_hard ? "Fever! The rows fill up every turn." : $"Playlists: {string.Join(" + ", chosen.Select(Pretty))}");
    }

    public override void OnPlayerJoined(Player p) { if (_phase != Phase.Lobby) T.DrawTo(p, HandSize); }

    static string Pretty(string icon) => icon.Length == 0 ? icon : char.ToUpperInvariant(icon[0]) + icon[1..];
    public override void Resumed(TimeSpan d) { _lastTick += d; _phaseAt += d; }
    void SetPhase(Phase p) { _phase = p; _phaseAt = DateTime.UtcNow; }
    void Notice(string text) => _notice = (NextId(), text);

    // ======================================================================================
    // host / player actions
    // ======================================================================================

    public override ActionResult HostAction(string action, IDictionary<string, string> args) => Act(action, args.TryGetValue("value", out var v) ? v : null);
    public override ActionResult Action(Player p, string action, string? arg) => Act(action, arg);

    ActionResult Act(string action, string? arg)
    {
        switch (action)
        {
            case "resetscores":                                   // the high-score panel's trash can
                if (_leaderboardPath != null && File.Exists(_leaderboardPath)) File.Delete(_leaderboardPath);
                LoadBoard(); return ActionResult.Success;
            case "start":
                if (_phase != Phase.Lobby && _phase != Phase.Over) return ActionResult.Fail("A game is already running.");
                BeginGame(); return ActionResult.Success;
            case "lobby":
                Start(); return ActionResult.Success;
            case "difficulty":
                if (_phase != Phase.Lobby && _phase != Phase.Over) return ActionResult.Fail("Change difficulty between games.");
                bool want = arg is "hard" or "fever" or "4";
                if (want && !_hardUnlocked) return ActionResult.Fail(_configError ?? $"Score {_cfg.HardUnlockScore:N0} to unlock Fever.");
                _hard = want; return ActionResult.Success;
            case "playlist":   // arg "slot:icon" (slot 0/1) or just "icon" (toggle)
            {
                if (_phase != Phase.Lobby && _phase != Phase.Over) return ActionResult.Fail("Pick playlists between games.");
                var lists = Playlists();
                var parts = (arg ?? "").Split(':');
                string icon = parts[^1];
                if (!lists.Contains(icon)) return ActionResult.Fail("Unknown playlist.");
                var cur = _playlists.ToList();
                if (parts.Length == 2 && int.TryParse(parts[0], out int slot) && slot is 0 or 1)
                {
                    while (cur.Count <= slot) cur.Add("");
                    cur.Remove(icon); if (cur.Count <= slot) cur.Add(""); cur[slot] = icon;
                }
                else if (cur.Contains(icon)) cur.Remove(icon);
                else { cur.Add(icon); if (cur.Count > 2) cur.RemoveAt(0); }
                _playlists = cur.Where(s => s.Length > 0).ToArray();
                return ActionResult.Success;
            }
        }
        return ActionResult.Fail("Unknown action.");
    }

    // ======================================================================================
    // playing a card
    // ======================================================================================

    bool AnySilenced() => Enumerable.Range(0, Rules.Slots).Any(i => T.TopOf(i)?.Silenced == true);

    string? CanPlayNow()
    {
        return _phase switch
        {
            Phase.Lobby => "The game hasn't started.",
            Phase.Over => "The game is over.",
            Phase.Shuffle or Phase.Spawning => "Wait — new requests are arriving.",          // CardErrorSpawning
            Phase.Spinning => "Wait for the wheel.",                                             // CardErrorSpinner
            _ when AnySilenced() => "Clear the silenced card first.",                            // CardErrorClearSilenced
            Phase.Play when _actionsLeft < 1 => "No actions left.",                              // CardErrorOutOfActions
            _ => null,
        };
    }

    static string FullClearLabel => GameTables.PlainText("Lanes_FullClear", "row bonus!").ToLowerInvariant();

    /// <summary>The on-screen error for a rejected physical card (CardError* screens, then the fouls).</summary>
    string ErrorTitle(Card c, int pad) => _phase switch
    {
        Phase.Shuffle or Phase.Spawning => GameTables.Caps("Lanes_ErrorCardSpawning", "REQUESTS ARE LOADING"),
        Phase.Spinning => GameTables.Caps("Lanes_ErrorCardSpinner", "WAIT FOR THE WHEEL"),
        _ when AnySilenced() => GameTables.Caps("Lanes_ErrorCardClearSilenced", "CLEAR THE SILENCED CARDS"),
        Phase.Play when _actionsLeft < 1 => GameTables.Caps("Error_NoMoreActions", "OUT OF PLAYS"),
        _ when c.Cid.StartsWith("unknown_") => "UNKNOWN CARD",
        _ when !Rules.IsTypeValidInSlot(c.Type, pad) => GameTables.Caps("Error_WrongColor", "WRONG SLOT"),
        _ when T.TopValid(pad) is PadCard top && c.Power < top.Card.Power => GameTables.Caps("Error_LevelTooLow", "LEVEL NOT HIGH ENOUGH"),
        _ => GameTables.Caps("Error_DuplicateCard", "ALREADY IN THE MIX"),
    };

    /// <summary>Fouls (the original's scoring).</summary>
    string? Foul(Card c, int pad)
    {
        if (c.Cid.StartsWith("unknown_")) return "Unknown card.";
        if (!Rules.IsTypeValidInSlot(c.Type, pad)) return $"{c.Type} cards don't go in slot {pad + 1}.";        // FoulInvalidSlot
        var top = T.TopValid(pad);
        if (top != null && c.Power < top.Card.Power) return $"Needs level {top.Card.Power} or higher to cover that card."; // FoulPowerLow
        for (int i = 0; i < Rules.Slots; i++) if (T.TopValid(i)?.Card.Cid == c.Cid) return "That card is already in the mix."; // FoulDuplicate
        return null;
    }

    public override ActionResult Play(Player p, Card card, int pad)
    {
        if (CanPlayNow() is string why) return ActionResult.Fail(why);
        if (Foul(card, pad) is string foul) return ActionResult.Fail(foul);
        p.Hand.Remove(card);
        T.Pads[pad].Add(new PadCard { Card = card, Source = CardSource.Player, PlayedBy = p.Name });
        CardAdded(card, pad);
        T.DrawTo(p, HandSize);
        return ActionResult.Success;
    }

    public override bool AcceptsUnknownCards => _phase != Phase.Lobby;

    public override void BoardPlaced(Card card, int pad)
    {
        string? err = CanPlayNow() ?? Foul(card, pad);
        if (err != null)
        {
            T.Pads[pad].Add(new PadCard { Card = card, Source = CardSource.Board, Valid = false, Error = _phase == Phase.Over ? null : ErrorTitle(card, pad) });
            Notice(err); return;
        }
        T.Pads[pad].Add(new PadCard { Card = card, Source = CardSource.Board });
        CardAdded(card, pad);
    }

    string? _lastCid;
    void CardAdded(Card card, int pad)
    {
        _lastCid = card.Cid;
        _playedCids.Add(card.Cid);
        var claimed = new HashSet<Req>();
        bool fx = card.Type == CardType.Fx;
        if (fx)
        {
            // FX: every block of size 1 in all lanes, then a normal event in its own lane (Levels can score)
            foreach (var blk in Blocks()) if (blk.Count == 1) claimed.Add(blk[0].r);
        }
        foreach (var r in _lanes[pad])
            if (Satisfies(r.Type, card, pad))
                foreach (var (_, br) in BlockOf(r)) claimed.Add(br);

        int pts = ScoreClaimed(claimed, pad, card.Name);
        if (_phase == Phase.Play) _actionsLeft = Math.Max(0, _actionsLeft - 1);     // TookAction(CardPlay); free in the Finale
        T.EmitMusic(claimed.Count > 0 ? "sfx:request_success" : "sfx:request_failure");
        _timer = 0; _timerLen = (_hard || _phase == Phase.Finale) ? _cfg.HardTimer : _cfg.NormalTimer;   // DoneClearingLane restarts the timer
        if (claimed.Count == 0) Notice($"{card.Name}: no matching requests.");
        else Notice($"{card.Name}: {claimed.Count} request{(claimed.Count == 1 ? "" : "s")} cleared, +{pts}");
        CheckEnd();
    }

    /// <summary>PartyRequest.IsRequestSatisfiedByCard: Level n = card power n; colours by what the card counts as here.</summary>
    static bool Satisfies(string req, Card c, int pad)
    {
        if (req.StartsWith("Level")) return req == "Level" + c.Power;                  // FX too: its chained Normal event (§4)
        return Enum.TryParse<CardType>(req, out var t) && Rules.CountsAs(c, pad).Contains(t);
    }

    /// <summary>Score claimed requests block by block, Full Clear lanes, then remove them and close gaps.</summary>
    int ScoreClaimed(HashSet<Req> claimed, int pad, string? why)
    {
        if (claimed.Count == 0) return 0;
        int total = 0;
        var c = _cfg;
        // per block: points = the requests' scores × tier
        foreach (var grp in claimed.GroupBy(r => r.Block == 0 ? -r.Id : r.Block))
        {
            int n = grp.Count();
            double mult = n >= c.RainbowAt ? c.RainbowMult : n >= c.GoldAt ? c.GoldMult : 1;
            int pts = (int)Math.Round(grp.Sum(r => c.Requests[r.Type].Score) * mult);
            string kind = n >= c.RainbowAt ? "Rainbow" : n >= c.GoldAt ? "Gold" : "White";
            if (kind == "Rainbow") { _rainbow++; _rainbowPts += pts; } else if (kind == "Gold") { _gold++; _goldPts += pts; } else { _white++; _whitePts += pts; }
            _biggestBlock = Math.Max(_biggestBlock, n);
            int lane = LaneOf(grp.First());
            Award(pts, kind, lane, n > 1 ? $"{n}-block" : null);
            total += pts;
        }
        // Full Clear: every request in a lane claimed, and at least one of them in danger
        for (int i = 0; i < Rules.Slots; i++)
        {
            var l = _lanes[i];
            if (l.Count > 0 && l.All(claimed.Contains) && (l.Any(r => r.Danger) || _dangerSpawn[i]))
            {
                int pts = c.FullClearPerRequest * l.Count;
                _fullClears++; _fullClearPts += pts;
                _pendingFullClear.Add((DateTime.UtcNow + FullClearDelay, pts, i));
                if (_lastCid != null) T.EmitMusic($"badge:lanes_clearFullLane:{_lastCid}:0:1");
                T.EmitMusic("sfx:blockclear");
                total += pts;
            }
        }
        _requestsMet += claimed.Count;
        for (int i = 0; i < Rules.Slots; i++)
        {
            var cells = _lanes[i].Where(claimed.Contains).Select(r => r.Cell).ToArray();
            if (cells.Length > 0) _clears.Add(new { id = NextId(), lane = i, cells });
            _lanes[i].RemoveAll(claimed.Contains);
            if (_lanes[i].Count == 0) _dangerSpawn[i] = false;
        }
        while (_clears.Count > 20) _clears.RemoveAt(0);
        Solve(0);                                                  // Fall(): close the gaps toward the spawn end
        MergeBlocks();
        return total;
    }

    void Award(int pts, string kind, int slot, string? note)
    {
        if (pts == 0) return;
        _score = Math.Max(0, _score + pts);
        _events.Add(new ScoreEvent(NextId(), Side.None, pts, kind, slot, note));
        if (_events.Count > 30) _events.RemoveAt(0);
    }

    // ======================================================================================
    // grid: cells, blocks
    // ======================================================================================

    /// <summary>The front request's cell (nearest the overflow edge), or 8 if the lane is empty = its free space.</summary>
    int FrontCell(int lane) => _lanes[lane].Count == 0 ? Cells : _lanes[lane].Min(r => r.Cell);
    bool Full(int lane) => FrontCell(lane) <= 0;

    /// <summary>Blocks (merged groups, plus groups that sit on each other in both
    /// directions) move as rigid units toward the spawn end until they hit the top (7 + n) or a settled group
    /// above; then every request moves n cells toward the overflow edge. Pulse = 1 (lanes that got a request at
    /// cell 8 are pushed one cell toward 0; a multi-lane block drags its other lanes along), Fall = 0.</summary>
    void Solve(int n)
    {
        var all = _lanes.SelectMany((l, lane) => l.Select(r => (r, lane))).ToList();
        if (all.Count == 0) return;
        // groups: one per block id (unmerged requests are their own group)
        var gid = new Dictionary<Req, int>(); var keys = new Dictionary<long, int>();
        foreach (var (r, _) in all) { long k = r.Block != 0 ? r.Block : -r.Id; if (!keys.TryGetValue(k, out int g)) keys[k] = g = keys.Count; gid[r] = g; }
        int G = keys.Count;
        // groups that are each directly above the other somewhere are interlocked: merge them (SCC of "above")
        var above = new bool[G, G];
        var pos = all.ToDictionary(x => (x.lane, x.r.Cell), x => gid[x.r]);
        foreach (var (r, lane) in all)
            if (pos.TryGetValue((lane, r.Cell + 1), out int up) && up != gid[r]) above[gid[r], up] = true;
        var reach = (bool[,])above.Clone();
        for (int k = 0; k < G; k++) for (int i = 0; i < G; i++) if (reach[i, k]) for (int j = 0; j < G; j++) if (reach[k, j]) reach[i, j] = true;
        var root = Enumerable.Range(0, G).ToArray();
        for (int i = 0; i < G; i++) for (int j = 0; j < i; j++) if (reach[i, j] && reach[j, i] && root[i] == i) root[i] = root[j];
        var groups = all.GroupBy(x => root[gid[x.r]]).Select(g => g.ToList()).ToList();
        var off = new int[groups.Count]; var settled = new bool[groups.Count];
        int top = Cells - 1 + n;
        bool changed;
        do
        {
            changed = false;
            var occ = new Dictionary<(int, int), int>();
            for (int gi = 0; gi < groups.Count; gi++) foreach (var (r, lane) in groups[gi]) occ[(lane, r.Cell + off[gi])] = gi;
            for (int gi = 0; gi < groups.Count; gi++)
            {
                if (settled[gi]) continue;
                var over = groups[gi].Select(x => occ.TryGetValue((x.lane, x.r.Cell + off[gi] + 1), out int h) ? h : -1).Where(h => h >= 0 && h != gi).Distinct().ToList();
                if (over.Count == 0)
                {
                    if (groups[gi].Max(x => x.r.Cell) + off[gi] < top)
                    {
                        foreach (var (r, lane) in groups[gi]) occ.Remove((lane, r.Cell + off[gi]));
                        off[gi]++;
                        foreach (var (r, lane) in groups[gi]) occ[(lane, r.Cell + off[gi])] = gi;
                    }
                    else settled[gi] = true;
                    changed = true;
                }
                else if (over.Any(h => settled[h])) { settled[gi] = true; changed = true; }
            }
        } while (changed);
        for (int gi = 0; gi < groups.Count; gi++) foreach (var (r, _) in groups[gi]) r.Cell = r.Cell - n + off[gi];
        foreach (var l in _lanes) l.Sort((a, b) => a.Cell.CompareTo(b.Cell));
    }
    int LaneOf(Req r) { for (int i = 0; i < Rules.Slots; i++) if (_lanes[i].Contains(r)) return i; return -1; }

    Req?[,] Grid()
    {
        var g = new Req?[Rules.Slots, Cells];
        for (int i = 0; i < Rules.Slots; i++)
            foreach (var r in _lanes[i]) if (r.Cell >= 0 && r.Cell < Cells) g[i, r.Cell] = r;
        return g;
    }

    /// <summary>MergeBlocks: 4-connected components of equal request type (same lane ±1 cell, neighbour lane same cell).</summary>
    void MergeBlocks()
    {
        var g = Grid(); int next = 0;
        foreach (var l in _lanes) foreach (var r in l) r.Block = 0;
        for (int i = 0; i < Rules.Slots; i++)
            for (int c = 0; c < Cells; c++)
            {
                if (g[i, c] is not Req start || start.Block != 0) continue;
                int id = ++next; var stack = new Stack<(int, int)>(); stack.Push((i, c)); start.Block = id;
                while (stack.Count > 0)
                {
                    var (x, y) = stack.Pop();
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= Rules.Slots || ny >= Cells) continue;
                        if (g[nx, ny] is Req n && n.Block == 0 && n.Type == start.Type) { n.Block = id; stack.Push((nx, ny)); }
                    }
                }
            }
    }

    List<List<(int lane, Req r)>> Blocks()
    {
        var d = new Dictionary<int, List<(int, Req)>>();
        for (int i = 0; i < Rules.Slots; i++) foreach (var r in _lanes[i])
            { if (!d.TryGetValue(r.Block, out var l)) d[r.Block] = l = new(); l.Add((i, r)); }
        return d.Values.ToList();
    }

    IEnumerable<(int lane, Req r)> BlockOf(Req r)
    {
        for (int i = 0; i < Rules.Slots; i++) foreach (var x in _lanes[i]) if (x.Block == r.Block) yield return (i, x);
    }

    // ======================================================================================
    // spawning
    // ======================================================================================

    void BeginSpawn()
    {
        int pulses = _hard ? HardPulses : _cfg.NormalPulses;
        bool anyFull = Enumerable.Range(0, Rules.Slots).Any(Full);
        if (!anyFull) pulses = Math.Min(pulses, Enumerable.Range(0, Rules.Slots).Max(FrontCell));   // overflow protection (event)
        for (int i = 0; i < Rules.Slots; i++) { _incomingEmpty[i] = _lanes[i].Count == 0; _hadFront[i] = Full(i); }
        _pulsesLeft = Math.Max(0, pulses);
        _bonusPulsesLeft = _incomingEmpty.Any(e => e) ? _cfg.EmptyLaneBonus : 0;
        _timer = null; _eighthAcc = 0;
        SetPhase(Phase.Spawning);
        T.EmitMusic("sfx:request_appear");
    }

    /// <summary>One pulse per eighth note: every incoming lane gets a request at the far end.</summary>
    void Pulse()
    {
        bool bonus = _pulsesLeft == 0, missedThisPulse = false;
        for (int i = 0; i < Rules.Slots; i++)
        {
            if (bonus && !_incomingEmpty[i]) continue;
            var l = _lanes[i];
            if (Full(i) && !_hadFront[i]) { _dangerSpawn[i] = true; continue; }   // per-pulse overflow protection
            l.Add(new Req { Id = NextId(), Type = PickRequest(i), Cell = Cells });   // enters just past the far end
        }
        Solve(1);
        // overflow: a lane whose front request was pushed below cell 0; one action per pulse at most
        for (int i = 0; i < Rules.Slots; i++)
            if (_lanes[i].Count > 0 && FrontCell(i) < 0) { Missed(i, charge: !missedThisPulse); missedThisPulse = true; }
        if (bonus) _bonusPulsesLeft--; else _pulsesLeft--;
        foreach (var (l, i) in _lanes.Select((l, i) => (l, i))) foreach (var r in l) r.Danger = _dangerSpawn[i];
    }

    void EndSpawn()
    {
        MergeBlocks();
        for (int i = 0; i < Rules.Slots; i++) if (Full(i)) _dangerSpawn[i] = true;
        foreach (var (l, i) in _lanes.Select((l, i) => (l, i))) foreach (var r in l) r.Danger = _dangerSpawn[i];
        SetPhase(Phase.Play);
        if (_hard) { _timer = 0; _timerLen = _cfg.HardTimer; }   // Fever: the timer runs after every spawn
        if (_dangerSpawn.Any(d => d)) T.EmitMusic("sfx:request_warning");
        CheckFinale();
    }

    /// <summary>Overflow (LanesGameMgr.Missed): the lane empties without scoring, one action is lost and the slot's card is silenced.</summary>
    void Missed(int lane, bool charge = true)
    {
        int n = _lanes[lane].Count;
        _lanes[lane].Clear(); _dangerSpawn[lane] = false;
        _clears.Add(new { id = NextId(), lane, cells = Enumerable.Range(0, Cells).ToArray(), kind = "fail" });
        if (charge) _actionsLeft = Math.Max(0, _actionsLeft - 1);       // TookAction(Miss): at most once per frame
        _overflowAt = DateTime.UtcNow;                                  // OverflowLanes lights (3 frames)
        _events.Add(new ScoreEvent(NextId(), Side.None, 0, "Overflow", lane, charge ? "-1 action" : null));
        if (T.TopValid(lane) is PadCard pc) Silence(pc);
        Solve(0); MergeBlocks();                                   // the lane's pieces of multi-lane blocks are gone: re-settle
        T.EmitMusic("sfx:overflow");
        Notice($"Row {lane + 1} overflowed! -1 action" + (n > 0 ? "" : ""));
    }

    static void Silence(PadCard pc) { pc.Silenced = true; pc.SilencedAt = DateTime.UtcNow; }

    // ---- which request type spawns (§3.1) ----

    string PickRequest(int lane)
    {
        // successors: [0] the slot card (weight 0 in the shipped curves), [1],[2] best-scoring round cards
        var shuffled = _seedCards.OrderBy(_ => T.Rng.Next()).ToList();
        var best = shuffled.Select(c => (c, s: SuccessorScore(c, lane))).OrderByDescending(x => x.s).Take(2).Select(x => x.c).ToList();
        if (best.Count == 0) return "Beat";
        double w1 = _cfg.SuccessorWeight1, w2 = _cfg.SuccessorWeight2;
        var card = best.Count == 1 || T.Rng.NextDouble() < w1 / (w1 + w2) ? best[0] : best[1];

        double F(string type) => _cfg.Requests[type].Frequency;     // LanesRequestMgr.requestInfo
        var opts = new List<(string type, double w)>();
        if (card.Power is >= 1 and <= 3) opts.Add(("Level" + card.Power, F("Level" + card.Power)));
        switch (card.Type)
        {
            case CardType.Beat: opts.Add(("Beat", F("Beat"))); break;
            case CardType.Loop: opts.Add(("Loop", F("Loop"))); break;
            case CardType.Bass: opts.Add(("Bass", F("Bass"))); break;
            case CardType.Lead: opts.Add(("Lead", F("Lead"))); break;
            case CardType.Fx: opts.Add(("Beat", F("Beat"))); break;
            case CardType.Wild:
                var (a, b) = lane switch { 0 => ("Lead", "Loop"), 1 or 2 => ("Loop", "Beat"), _ => ("Beat", "Bass") };
                opts.Add((a, 0.25)); opts.Add((b, 0.25)); break;
        }
        double r = T.Rng.NextDouble() * opts.Sum(o => o.w);
        foreach (var (type, w) in opts) { if (r < w) return type; r -= w; }
        return opts[^1].type;
    }

    int SuccessorScore(Card c, int lane)
    {
        if (!Rules.IsTypeValidInSlot(c.Type, lane)) return 0;
        if (T.Pads.Any(s => s.Any(pc => pc.Card.Cid == c.Cid))) return 1;
        if (T.DiscardPile.Any(d => d.Cid == c.Cid)) return 3;
        if (T.TopValid(lane) is PadCard top && c.Power < top.Card.Power) return 4;
        if (c.Type == CardType.Fx) return 5;
        if (c.Type is CardType.Bass or CardType.Lead) return T.Rng.NextDouble() < _cfg.YellowGreenBoost ? 7 : 6;   // yellow (Lead) / green (Bass)
        return 6;
    }

    // ======================================================================================
    // EQ: the DropMix button (§5)
    // ======================================================================================

    public override ActionResult Button(Player? p)
    {
        if (_phase == Phase.Finale) return ActionResult.Fail("No EQ during the Finale.");
        if (_phase != Phase.Play) return ActionResult.Fail(_phase == Phase.Spawning ? "Wait — new requests are arriving." : "Not now.");
        if (AnySilenced()) return ActionResult.Fail("Clear the silenced card first.");
        if (_actionsLeft < 1) return ActionResult.Fail("No actions left.");
        if (Enumerable.Range(0, Rules.Slots).All(i => T.TopValid(i) == null)) return ActionResult.Fail("Put a card on the board first.");
        _actionsLeft--;
        _wheelResult = GameTables.SpinWheel(GameTables.WheelSlices, T.Rng);
        _wheelAt = DateTime.UtcNow; _wheelLanded = false; _spinId = NextId(); _timer = null;
        SetPhase(Phase.Spinning);
        T.EmitMusic("wheel_up");
        return ActionResult.Success;
    }

    void LandWheel()
    {
        _wheelLanded = true; _wheelAt = DateTime.UtcNow;
        string label = _wheelResult == 0 ? "X" : _wheelResult.ToString();
        int silenced = 0;
        for (int i = 0; i < Rules.Slots; i++)
            if (T.TopValid(i) is PadCard pc && !pc.Silenced && (_wheelResult == 0 || pc.Card.Power == _wheelResult)) { Silence(pc); silenced++; }
        var claimed = new HashSet<Req>();
        foreach (var l in _lanes) foreach (var r in l)
            if (_wheelResult == 0 ? r.Type.StartsWith("Level") : r.Type == "Level" + _wheelResult)
                foreach (var (_, br) in BlockOf(r)) claimed.Add(br);
        int pts = ScoreClaimed(claimed, 2, "EQ");
        T.EmitMusic("wheel_down");
        Notice(claimed.Count > 0 ? $"EQ {label}: {claimed.Count} level request{(claimed.Count == 1 ? "" : "s")}, +{pts}" : $"EQ {label}: nothing to clear.");
        if (silenced > 0) Notice($"EQ {label}: +{pts}. Lift the {silenced} silenced card{(silenced == 1 ? "" : "s")}.");
        _timer = 0; _timerLen = _hard ? _cfg.HardTimer : _cfg.NormalTimer;
    }

    // ======================================================================================
    // physical board lifts
    // ======================================================================================

    public override void BoardLifted(Card card, int pad)
    {
        if (_phase == Phase.Lobby) { T.UnseatBoardPlayer(pad); return; }   // lifting the deck withdraws the board player
        var s = T.Pads[pad];
        int i = s.FindLastIndex(pc => pc.Source == CardSource.Board && pc.Card.Cid == card.Cid);
        if (i >= 0) s.RemoveAt(i);
    }

    // ======================================================================================
    // time
    // ======================================================================================

    public override bool Tick(DateTime now)
    {
        double dt = Math.Min(0.5, (now - _lastTick).TotalSeconds); _lastTick = now;
        bool changed = false;

        // virtual cards that were silenced leave after a short reveal (a physical card must be lifted)
        for (int i = 0; i < Rules.Slots; i++)
            if (T.TopOf(i) is { Silenced: true, Source: CardSource.Player } pc && now - pc.SilencedAt >= ClearReveal)
            { T.PopTop(i); changed = true; }

        for (int k = _pendingFullClear.Count - 1; k >= 0; k--)
            if (now >= _pendingFullClear[k].at)
            {
                var (_, pts, lane) = _pendingFullClear[k]; _pendingFullClear.RemoveAt(k);
                Award(pts, "FullClear", lane, FullClearLabel); changed = true;
            }
        // early end (Finale): the grid is clear and no score is still to come
        if (_phase == Phase.Finale && _pendingFullClear.Count == 0 && _lanes.All(l => l.Count == 0)) { EndGame(); return true; }

        switch (_phase)
        {
            case Phase.Shuffle when now - _phaseAt >= ShuffleTime:
                BeginSpawn(); return true;

            case Phase.Spawning:
            {
                double eighth = 30.0 / Math.Clamp(T.MusicBpm, 60, 200);
                _eighthAcc += dt;
                while (_eighthAcc >= eighth && _phase == Phase.Spawning)
                {
                    _eighthAcc -= eighth;
                    if (_pulsesLeft > 0 || _bonusPulsesLeft > 0) { Pulse(); changed = true; }
                    if (_pulsesLeft <= 0 && _bonusPulsesLeft <= 0) { EndSpawn(); changed = true; }
                }
                return changed;
            }

            case Phase.Spinning:
                if (!_wheelLanded && now - _wheelAt >= SpinTime) { LandWheel(); return true; }
                if (_wheelLanded && now - _wheelAt >= WheelShow) { SetPhase(Phase.Play); CheckFinale(); return true; }
                return changed;

            case Phase.Play:
            case Phase.Finale:
            {
                if (AnySilenced()) return changed;                    // waiting for a lift
                if (_phase == Phase.Play && CheckFinale()) return true;
                if (_timer is double t)
                {
                    int before = (int)t; _timer = t + dt;
                    if ((int)_timer != before) { changed = true; if (_timerLen - _timer <= 2 && Enumerable.Range(0, Rules.Slots).Any(Full)) T.EmitMusic("sfx:timerticks"); }
                    if (_timer > _timerLen + _cfg.Slop)
                    {
                        _timer = null;
                        if (_phase == Phase.Finale) EndGame(); else BeginSpawn();
                        return true;
                    }
                }
                return changed;
            }

            case Phase.Over:
                if (!_submitted && now - _phaseAt >= _cfg.GameOverTime) { Submit(); return true; }
                return changed;
        }
        return changed;
    }

    /// <summary>Finale: no actions left and nothing running. No more spawns; plays are free; the hard-mode timer.</summary>
    bool CheckFinale()
    {
        if (_phase != Phase.Play || _actionsLeft >= 1 || AnySilenced()) return false;
        SetPhase(Phase.Finale);
        _finaleWarned = true;
        _timer = 0; _timerLen = _cfg.HardTimer;
        T.EmitMusic("lanes_finale");
        T.EmitMusic("sfx:finale_ping");
        Notice("Finale! Play what you can — it's free.");
        return true;
    }

    void CheckEnd()
    {
        if (_phase is Phase.Over or Phase.Lobby) return;
        // GetCardsRemaining: playlist cards minus distinct cards played
        if (_playedCids.Count >= _deckSize || (T.Deck.Count == 0 && T.Players.All(p => p.Hand.Count == 0))) EndGame();
        else if (_phase == Phase.Play) CheckFinale();
    }

    void EndGame()
    {
        _timer = null;
        foreach (var (_, pts, lane) in _pendingFullClear) Award(pts, "FullClear", lane, FullClearLabel);
        _pendingFullClear.Clear();
        _endBonus = _biggestBlock * _cfg.BiggestBlockBonus;
        if (_endBonus > 0) Award(_endBonus, "Bonus", 2, $"biggest block {_biggestBlock}");
        SetPhase(Phase.Over);
        T.EmitMusic("lanes_end");
        T.EmitMusic("sfx:outro_puzzle");
        Notice($"Game over — {_score:N0} points");
    }

    void Submit()
    {
        _submitted = true;
        if (_score >= _cfg.HardUnlockScore && !_hardUnlocked) { _hardUnlocked = true; _unlockedNow = true; }
        _board.Add((_leaderName, _score, _hard ? 4 : 2, _requestsMet));
        _board = _board.OrderByDescending(e => e.score).Take(20).ToList();
        SaveBoard();
    }

    // ---- leaderboard (LeaderboardMgr.lanesLeaderboard) ----

    void LoadBoard()
    {
        _board.Clear();
        if (_leaderboardPath == null) return;
        try
        {
            if (File.Exists(_leaderboardPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_leaderboardPath));
                _hardUnlocked = doc.RootElement.TryGetProperty("hardUnlocked", out var hu) && hu.GetBoolean();
                foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
                    _board.Add((e.GetProperty("name").GetString() ?? "", e.GetProperty("score").GetInt32(), e.GetProperty("difficulty").GetInt32(), e.GetProperty("requests").GetInt32()));
                return;
            }
            // default seed rows shipped with the game
            string csv = Path.Combine(Path.GetDirectoryName(_leaderboardPath) ?? ".", "lanes_leaderboard_default.csv");
            if (File.Exists(csv))
                foreach (var line in File.ReadLines(csv).Skip(1))
                {
                    var f = line.Split(',');
                    if (f.Length >= 2 && int.TryParse(f[1], out int sc))
                        _board.Add((f[0], sc, f.Length > 2 && int.TryParse(f[2], out int d) ? d : 2, f.Length > 3 && int.TryParse(f[3], out int r) ? r : 0));
                }
            _board = _board.OrderByDescending(e => e.score).ToList();
        }
        catch { }
    }

    void SaveBoard()
    {
        if (_leaderboardPath == null) return;
        try
        {
            File.WriteAllText(_leaderboardPath, JsonSerializer.Serialize(new
            {
                hardUnlocked = _hardUnlocked,
                entries = _board.Select(e => new { e.name, e.score, e.difficulty, e.requests }),
            }));
        }
        catch { }
    }

    // ======================================================================================
    // view
    // ======================================================================================

    public override object View(Player? viewer)
    {
        string? why = CanPlayNow();
        var grid = Enumerable.Range(0, Rules.Slots).Select(i =>
        {
            var cells = new object?[Cells];
            foreach (var r in _lanes[i])
            {
                if (r.Cell < 0 || r.Cell >= Cells) continue;
                int size = _lanes.Sum(l => l.Count(x => x.Block == r.Block));
                cells[r.Cell] = new { id = r.Id, t = r.Type, b = r.Block, n = size };
            }
            return new { cells, danger = _dangerSpawn[i], full = Full(i) };
        }).ToArray();

        string phase = _phase.ToString().ToLowerInvariant();
        return new
        {
            id = Id, title = Title,
            subtitle = _phase == Phase.Lobby ? "Pick two playlists" : _hard ? "Fever" : "Clear the requests",
            phase,
            slots = SlotViews(),
            legal = LegalMap(viewer, (c, i) => Foul(c, i) == null),
            canPlay = why == null,
            waitText = viewer == null ? null : why,
            allowDraw = false, allowClear = false, allowUndo = false,
            lanes = grid,
            score = _score, actionsLeft = _actionsLeft, actions = _configError == null ? _cfg.Actions : 0,
            timerLeft = _timer is double t ? Math.Max(0, _timerLen - t) : (double?)null, timerLen = _timerLen,
            hard = _hard, hardUnlocked = _hardUnlocked, unlockScore = _configError == null ? _cfg.HardUnlockScore : 0,
            playlists = _playlists, allPlaylists = _phase is Phase.Lobby or Phase.Over ? Playlists() : null,
            biggestBlock = _biggestBlock,
            // LanesErrorDisplay: a rejected physical card shows its error on that slot until it's lifted
            errors = Enumerable.Range(0, Rules.Slots).Where(i => T.TopOf(i) is { Valid: false, Error: not null })
                .Select(i => new { slot = i, text = T.TopOf(i)!.Error }).ToArray(),
            wheel = new { state = _phase == Phase.Spinning ? (_wheelLanded ? "landed" : "spinning") : "idle",
                          result = _wheelLanded ? (_wheelResult == 0 ? "X" : _wheelResult.ToString()) : null,
                          resultValue = _wheelResult, spin = _spinId, ms = (int)SpinTime.TotalMilliseconds },
            buttonArmed = _phase == Phase.Play && _actionsLeft > 0 && !AnySilenced() && Enumerable.Range(0, Rules.Slots).Any(i => T.TopValid(i) != null),
            recap = _phase == Phase.Over ? new
            {
                white = _white, whitePts = _whitePts, gold = _gold, goldPts = _goldPts, rainbow = _rainbow, rainbowPts = _rainbowPts,
                fullClears = _fullClears, fullClearPts = _fullClearPts, biggest = _biggestBlock, bonus = _endBonus, bonusPerBlock = _cfg.BiggestBlockBonus,
                requests = _requestsMet, unlocked = _unlockedNow,
            } : null,
            leaderboard = _board.Take(10).Select(e => new { e.name, e.score, hard = e.difficulty == 4, e.requests }).ToArray(),
            events = _events.TakeLast(12).ToArray(),
            clears = _clears.TakeLast(8).ToArray(),
            notice = new { id = _notice.id, text = _notice.text },
            finaleWarned = _finaleWarned,
        };
    }
}
