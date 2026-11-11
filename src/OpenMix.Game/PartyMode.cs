using System.Globalization;
using System.Text.Json;
using OpenMix.Cards;

namespace OpenMix.Game;

/// <summary>
/// Party: everyone plays together on one score, meeting timed requests the
/// crowd calls out. There are 5 rounds; a round met completely earns its bonus requests; a game
/// with no failed request earns the Encore rounds. Fouls fail every request on screen and cost
/// the table's foul_penalty. Round tables come from the OBB (data/party/party_rounds*.txt) and
/// the timing values from the Party scene (data/party/party_config.json).
/// </summary>
public sealed class PartyMode : GameMode
{
    public override string Id => "party";
    public override string Title => "Party";

    // ---- scene values, all from party_config.json (imported at setup); a game can't start without them ----
    double _timingRandomness, _gameIntro, _roundIntro, _roundBreak, _grace, _bumperTime, _foulTime;
    int _minScore;
    int[] _handSizes = Array.Empty<int>();
    string? _configError = "Party settings aren't loaded.";
    const double FirstGroupDelay = 1.0, TileLinger = 2.0, FoulThrottle = 0.25;
    const double RoundCompleteHold = 1.0, ReseatWindow = 1.0, EqExtraDelay = 1.0, WarnAt = 0.2;
    static readonly TimeSpan SpinTime = TimeSpan.FromSeconds(3.5), WheelShow = TimeSpan.FromSeconds(2.0), ClearReveal = TimeSpan.FromSeconds(1.5);
    static readonly string[] AddInstruments = { "synth", "vocal", "guitar", "drums", "horns", "strings" };

    public enum Difficulty { Normal = 1, Hard = 2, Easy = 3 }
    public enum Phase { Lobby, GameStart, RoundIntro, Play, WaitForRoundToEnd, RoundComplete, RoundBreak, GameOver }
    public override LightCue Lights => _phase switch
    {
        Phase.Lobby => new LightCue(LightScene.RegistrationParty, Side.None, Enumerable.Range(0, Rules.Slots).Select(i => i < T.Players.Count()).ToArray()),
        Phase.GameStart => new LightCue(LightScene.GameStarting),
        _ => LightCue.Normal,
    };
    public override (bool InGame, double Progress) Crowd => (_phase != Phase.Lobby, _phase == Phase.GameOver ? 1 : 0);

    // ---- round tables ----
    sealed record Group(string[] Types, double Weight, double[] Max, double[] Dur);
    sealed class Round
    {
        public string Name = ""; public double Gap; public int[] Counts = new int[6]; public bool Encore;
        public List<Group> Groups = new(); public Round? Bonus;
        public int Count(int players) => Counts[Math.Clamp(players, 1, 6) - 1];
    }
    sealed class RoundTable { public int FoulPenalty; public List<Round> Rounds = new(); }

    enum TileState { Active, Met, Perfect, Failed, Dismissed }
    sealed class Tile
    {
        public long Id; public string Type = ""; public string? Instrument; public double Dur, Left, Max; public bool Bonus;
        public TileState State; public int Points; public double DoneFor; public bool Warned; public int Group;
    }

    // ---- state ----
    string? _dataDir, _leaderboardPath;
    Difficulty _difficulty = Difficulty.Normal;
    RoundTable _table = new();
    Phase _phase = Phase.Lobby; double _phaseT;
    int _round, _resolved, _met, _score, _players = 1;
    bool _bonus, _anyFail;
    double _delay;
    int _lastGroup = -1; readonly HashSet<int> _failedGroups = new();
    readonly List<Tile> _tiles = new(); long _tileId, _eventId;
    readonly List<string> _pips = new();     // per request this round: pending/met/perfect/failed/skipped (+bonus ones)
    double _bumperLeft, _foulLeft; string? _bumperText, _foulText; long _bumperId, _foulId;
    DateTime _lastFoul = DateTime.MinValue;
    readonly List<(string cid, int pad, double t)> _reseat = new();
    // wheel
    bool _spinning, _landed; int _wheelResult; DateTime _wheelAt; long _spinId;
    // results
    int _perfectN, _perfectPts, _goodN, _goodPts, _failedN, _fouls, _highScoreBefore, _rank = -1;
    bool _newHigh;
    List<(int score, string difficulty, int players, string name)> _board = new();
    readonly List<ScoreEvent> _events = new();
    (long id, string? text) _notice;
    DateTime _lastTick = DateTime.UtcNow;
    readonly Random _rng = new();

    // ======================================================================================
    // setup
    // ======================================================================================

    public override void Configure(IDictionary<string, string> o)
    {
        if (o.TryGetValue("partyData", out var d)) _dataDir = d;
        if (o.TryGetValue("difficulty", out var df) && Enum.TryParse<Difficulty>(df, true, out var dv)) _difficulty = dv;
        _leaderboardPath = _dataDir == null ? null : Path.Combine(_dataDir, "party_leaderboard.json");
        LoadConfig(); LoadBoard();
        // test/host overrides (seconds)
        static double? Num(IDictionary<string, string> o, string k) => o.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        _gameIntro = Num(o, "introSeconds") ?? _gameIntro;
        _roundBreak = Num(o, "breakSeconds") ?? _roundBreak;
    }

    /// <summary>Reads party_config.json. Without it (or with a field missing) <see cref="_configError"/> says so and
    /// no game starts.</summary>
    void LoadConfig()
    {
        const string Rerun = "Run setup again to import your game's Party settings.";
        if (_dataDir == null) { _configError = "Party settings folder isn't set. " + Rerun; return; }
        var p = Path.Combine(_dataDir, "party_config.json");
        try
        {
            if (!File.Exists(p)) { _configError = $"Party settings are missing ({p}). " + Rerun; return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            var r = doc.RootElement;
            double D(string k) => r.GetProperty(k).GetDouble();
            _timingRandomness = D("timingRandomness"); _gameIntro = D("gameIntroSeconds");
            _roundIntro = D("roundIntroSeconds"); _roundBreak = D("roundBreakSeconds");
            _grace = D("perfectScoreGracePeriod"); _minScore = (int)D("minimumRequestScore");
            _bumperTime = r.GetProperty("PartyBonusBumper").GetProperty("displayTime").GetDouble();
            _foulTime = r.GetProperty("PartyFoulDisplay").GetProperty("displayTime").GetDouble();
            _handSizes = r.GetProperty("handSizeForPlayerCount").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            _configError = _handSizes.Length == 0 ? "Party settings have no hand sizes. " + Rerun : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            _configError = $"Party settings couldn't be read ({ex.Message}). " + Rerun;
        }
    }

    /// <summary>The round-table grammar.</summary>
    static RoundTable Parse(string text)
    {
        var t = new RoundTable(); Round? cur = null; List<Group>? pool = null;
        static double F(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        foreach (var raw in text.Split('\n'))
        {
            var f = raw.Trim().Split(',');
            if (f.Length < 2 || f[0].Trim().Length == 0) continue;
            string name = f[0].Trim();
            if (name == "foul_penalty") { t.FoulPenalty = (int)F(f[1]); continue; }
            if (name.StartsWith("round_"))
            {
                var r = new Round { Name = name, Gap = F(f[1]), Encore = name.Contains("encore") };
                for (int i = 0; i < 6 && i + 2 < f.Length; i++) r.Counts[i] = (int)F(f[i + 2]);
                if (name.EndsWith("_bonus") && cur != null) { cur.Bonus = r; pool = r.Groups; }
                else { t.Rounds.Add(r); cur = r; pool = r.Groups; }
                continue;
            }
            if (pool == null || f.Length < 4) continue;
            var types = f[0].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            pool.Add(new Group(types, F(f[1]),
                f[2].Split(';').Select(F).ToArray(), f[3].Split(';').Select(F).ToArray()));
        }
        return t;
    }

    void LoadTable()
    {
        string file = _difficulty switch { Difficulty.Easy => "party_rounds_easy.txt", Difficulty.Hard => "party_rounds_hard.txt", _ => "party_rounds.txt" };
        var p = _dataDir == null ? null : Path.Combine(_dataDir, file);
        _table = p != null && File.Exists(p) ? Parse(File.ReadAllText(p)) : new RoundTable();
    }

    public override void Start()
    {
        _phase = Phase.Lobby; _tiles.Clear(); _pips.Clear(); _events.Clear();
        _spinning = _landed = false;
        T.ClearBoardPlayers();
    }

    int HandSize => _handSizes[Math.Clamp(_players, 1, _handSizes.Length) - 1];

    public override bool AcceptsBoardCards => _phase != Phase.Lobby;

    // "ADD YOUR DECK TO JOIN": in the lobby, a deck on a join slot (the three centre slots, as the lobby draws them)
    // seats a co-op board player. One per slot; they stay joined after the deck is lifted, until the game (re)starts.
    static readonly int[] JoinPads = { 1, 2, 3 };
    public override void BoardSetup(Card card, int pad)
    {
        if (_phase == Phase.Lobby && Array.IndexOf(JoinPads, pad) >= 0 && T.Players.Count(p => p.Status != PlayerStatus.Orphaned) < 5)
            T.SeatBoardPlayer(pad);
    }


    void BeginGame()
    {
        if (_configError != null) { Notice(_configError); return; }
        if (BoardMustBeCleared()) { Notice("Remove all cards from the board to start."); return; }
        LoadTable();
        if (_table.Rounds.Count == 0) { Notice("Party round tables are missing (data/party)."); return; }
        _players = Math.Clamp(T.Players.Count(p => p.Status != PlayerStatus.Orphaned), 1, 5);
        foreach (var s in T.Pads) s.Clear();
        T.Deck.Clear(); T.DiscardPile.Clear(); T.Deck.AddRange(T.ActiveCards); T.Shuffle(T.Deck);
        foreach (var p in T.Players) { p.Hand.Clear(); T.DrawTo(p, HandSize); }
        _score = 0; _anyFail = false; _bonus = false; _round = 0; _delay = FirstGroupDelay;
        _perfectN = _perfectPts = _goodN = _goodPts = _failedN = _fouls = 0; _rank = -1; _newHigh = false; _firstIcon = null; _partyName = "";
        _highScoreBefore = _board.Count > 0 ? _board.Max(e => e.score) : 0;
        _tiles.Clear(); _events.Clear(); _reseat.Clear();
        _bumperLeft = _foulLeft = 0;
        Enter(Phase.GameStart);
        T.EmitMusic("party_start");
        Notice($"Let's party! Draw {HandSize} cards.");
    }

    public override void OnPlayerJoined(Player p) { if (_phase != Phase.Lobby && _phase != Phase.GameOver) T.DrawTo(p, HandSize); }

    void Enter(Phase p)
    {
        _phase = p; _phaseT = 0;
        switch (p)
        {
            case Phase.RoundIntro:
                _resolved = _met = 0; _lastGroup = -1; _failedGroups.Clear();
                _pips.Clear();
                for (int i = 0; i < N; i++) _pips.Add("pending");
                for (int i = 0; i < B; i++) _pips.Add("bonus");
                if (_bonus) for (int i = 0; i < _pips.Count; i++) _pips[i] = "pending";
                break;
            case Phase.Play: _delay = FirstGroupDelay; break;
            case Phase.RoundComplete:
                T.EmitMusic(_round < _table.Rounds.Count ? "sfx:request_success3x" : "sfx:outro_party_hs");
                break;
        }
    }

    Round Cur => _table.Rounds[Math.Min(_round, _table.Rounds.Count - 1)];
    int N => Cur.Count(_players);
    int B => Cur.Bonus?.Count(_players) ?? 0;

    string RoundLabel(int i)
    {
        if (i < 0 || i >= _table.Rounds.Count) return "";
        bool enc = _table.Rounds[i].Encore, prevEnc = i > 0 && _table.Rounds[i - 1].Encore;
        bool nextEnc = i + 1 < _table.Rounds.Count && _table.Rounds[i + 1].Encore;
        return enc ? (prevEnc ? GameTables.Caps("Party_SecondEncore", "ENCORE II") : GameTables.Caps("Party_FirstEncore", "ENCORE"))
            : nextEnc ? GameTables.Caps("Party_FinalRound", "LAST ROUND") : GameTables.Caps("Party_Round", "ROUND {0}").Replace("{0}", (i + 1).ToString());
    }

    void Notice(string text) => _notice = (NextId(), text);

    // ======================================================================================
    // actions
    // ======================================================================================

    public override ActionResult HostAction(string action, IDictionary<string, string> args) => Act(action, args.TryGetValue("value", out var v) ? v : null);
    public override ActionResult Action(Player p, string action, string? arg) => Act(action, arg);

    ActionResult Act(string action, string? arg)
    {
        switch (action)
        {
            case "start":
                if (_phase != Phase.Lobby && _phase != Phase.GameOver) return ActionResult.Fail("A game is already running.");
                BeginGame(); return ActionResult.Success;
            case "lobby": Start(); return ActionResult.Success;
            case "rename": return RenameEntry(arg);
            case "resetscores":
                if (_leaderboardPath != null && File.Exists(_leaderboardPath)) File.Delete(_leaderboardPath);
                LoadBoard(); _highScoreBefore = _board.Count > 0 ? _board.Max(e => e.score) : 0; return ActionResult.Success;
            case "difficulty":
                if (_phase != Phase.Lobby && _phase != Phase.GameOver) return ActionResult.Fail("Change difficulty between games.");
                if (!Enum.TryParse<Difficulty>(arg, true, out var d)) return ActionResult.Fail("Easy, Normal or Hard.");
                _difficulty = d; return ActionResult.Success;
        }
        return ActionResult.Fail("Unknown action.");
    }

    bool InGame => _phase is Phase.GameStart or Phase.RoundIntro or Phase.Play or Phase.WaitForRoundToEnd or Phase.RoundComplete or Phase.RoundBreak;

    /// <summary>the original's scoring → foul kind (null = valid).</summary>
    string? Placement(Card c, int pad)
    {
        if (!Rules.IsTypeValidInSlot(c.Type, pad)) return GameTables.PlainText("Party_PartyFoul_WrongColor", "Wrong slot");
        var top = T.TopValid(pad);
        if (top != null && c.Power < top.Card.Power) return GameTables.PlainText("Party_PartyFoul_LevelTooLow", "Level not high enough");
        for (int i = 0; i < Rules.Slots; i++) if (T.TopValid(i)?.Card.Cid == c.Cid) return GameTables.PlainText("Party_PartyFoul_DuplicateCard", "Already in the mix");
        return null;
    }

    public override ActionResult Play(Player p, Card card, int pad)
    {
        if (!InGame) return ActionResult.Fail(_phase == Phase.Lobby ? "The party hasn't started." : "The party is over.");
        if (_spinning) return ActionResult.Fail("Wait for the wheel.");
        p.Hand.Remove(card);
        var pc = new PadCard { Card = card, Source = CardSource.Player, PlayedBy = p.Name };
        string? placement = Placement(card, pad);           // judged against the board BEFORE the card lands
        T.Pads[pad].Add(pc);
        OnPlayed(pc, pad, placement);
        return ActionResult.Success;   // no draw: cards are drawn only between rounds
    }

    public override void BoardPlaced(Card card, int pad)
    {
        var pc = new PadCard { Card = card, Source = CardSource.Board };
        string? placement = Placement(card, pad);
        T.Pads[pad].Add(pc);
        if (InGame) OnPlayed(pc, pad, placement); else pc.Valid = placement == null;
    }

    /// <summary>The per-frame party update, for a fresh play.</summary>
    void OnPlayed(PadCard pc, int pad, string? foul)
    {
        var card = pc.Card;
        // The party update runs whenever GameFlow is in game: during the intro, round breaks and a wheel spin there are no
        // active requests, so a fresh play is a foul too (rule violation first, else Party_PartyFoul_UnrequestedCard)
        if (foul != null) { Invalidate(pc); Foul(foul); return; }

        _firstIcon ??= card.Icon;
        int matched = 0, pts = 0;
        foreach (var t in _tiles.Where(t => t.State == TileState.Active))
            if (SatisfiedByCard(t, card, pad)) { pts += Satisfy(t, pad); matched++; }

        if (matched == 0)
        {
            int k = _reseat.FindIndex(r => r.cid == card.Cid && r.pad == pad);
            if (k >= 0) _reseat.RemoveAt(k);                                   // re-seat within 1 s: no foul
            else if (card.Type == CardType.Fx && _tiles.Any(t => t.State == TileState.Active)) { }
            else { Invalidate(pc); Foul(GameTables.PlainText("Party_PartyFoul_UnrequestedCard", "Not asked for")); return; }
        }
        // FX wipe: dismiss everything still active (met, 0 points)
        if (card.Type == CardType.Fx)
            foreach (var t in _tiles.Where(t => t.State == TileState.Active)) Resolve(t, TileState.Dismissed, 0);
        if (matched > 1) { Notice($"{card.Name}: {matched} requests at once! +{pts}"); T.EmitMusic($"badge:party_clearRequestsSinglePlay:{card.Cid}:{matched}:1"); }
        AfterResolve();
    }

    /// <summary>CardSlots.SetPlayValidity(false). A virtual card leaves after a short reveal; a physical one must be lifted.</summary>
    static void Invalidate(PadCard pc) { pc.Valid = false; pc.SilencedAt = DateTime.UtcNow; }

    public override ActionResult Clear(Player p, int pad)
    {
        if (!InGame) return ActionResult.Fail("Not now.");
        var top = T.TopOf(pad);
        if (top == null || top.Source != CardSource.Player) return ActionResult.Fail("Nothing to clear.");
        T.PopTop(pad);
        OnRemoved(top, pad);
        return ActionResult.Success;
    }

    public override void BoardLifted(Card card, int pad)
    {
        if (_phase == Phase.Lobby) { T.UnseatBoardPlayer(pad); return; }   // lifting the deck withdraws the board player
        var s = T.Pads[pad];
        int i = s.FindLastIndex(pc => pc.Source == CardSource.Board && pc.Card.Cid == card.Cid);
        if (i < 0) return;
        var pc = s[i]; s.RemoveAt(i);
        if (InGame && pc.Valid && !pc.Silenced) OnRemoved(pc, pad);
    }

    void OnRemoved(PadCard pc, int pad)
    {
        if (!pc.Valid) return;
        int matched = 0;
        foreach (var t in _tiles.Where(t => t.State == TileState.Active))
            if (SatisfiedByRemoval(t, pc.Card, pad)) { Satisfy(t, pad); matched++; }
        if (matched == 0 && pc.Source == CardSource.Board) _reseat.Add((pc.Card.Cid, pad, 0));   // no foul for unrequested removals
        AfterResolve();
    }

    // ---- matching (PartyRequest.IsRequestSatisfiedByCard / ByCardRemoval) ----

    static bool SatisfiedByCard(Tile t, Card c, int pad)
    {
        switch (t.Type)
        {
            case "Slot0": case "Slot1": case "Slot2": case "Slot3": case "Slot4": return pad == t.Type[4] - '0';
            case "Level1": case "Level2": case "Level3": return c.Power == t.Type[5] - '0';
            case "Lead": case "Loop": case "Beat": case "Bass": return ColourMatch(c, pad, t.Type);
            case "FX": return c.Type == CardType.Fx;
            case "AddInstrument": return t.Instrument != null && HasInstrument(c, pad, t.Instrument);
            case "Synth": case "Guitar": case "Horns": case "Strings": case "Vocal": case "Drums": case "Sampler":
                return HasInstrument(c, pad, t.Type.ToLowerInvariant());
            default: return false;   // NewGenre, NewInstrument, AddGenre, Crossfader, Equalizer, None
        }
    }

    static bool SatisfiedByRemoval(Tile t, Card c, int pad) => t.Type switch
    {
        "RemoveLead" => ColourMatch(c, pad, "Lead"), "RemoveLoop" => ColourMatch(c, pad, "Loop"),
        "RemoveBeat" => ColourMatch(c, pad, "Beat"), "RemoveBass" => ColourMatch(c, pad, "Bass"),
        _ => false,
    };

    /// <summary>A Wild is Beat in 2–4, Bass in 4, Loop in 0–2, Lead in 0.</summary>
    static bool ColourMatch(Card c, int pad, string type)
    {
        if (c.Type != CardType.Wild) return c.Type.ToString() == type;
        return type switch { "Beat" => pad >= 2, "Bass" => pad == 4, "Loop" => pad <= 2, "Lead" => pad == 0, _ => false };
    }

    /// <summary>A Wild's Instrument 1..4 are its green/blue/red/yellow variants;
    /// it uses the variant(s) of the slot it is in.</summary>
    static bool HasInstrument(Card c, int pad, string inst)
    {
        var ins = c.Instruments;
        if (c.Type != CardType.Wild) return ins.Length == 1 && ins[0].Equals(inst, StringComparison.OrdinalIgnoreCase);
        if (ins.Length != 4) return false;
        int[] idx = pad switch { 0 => new[] { 3, 2 }, 1 => new[] { 2 }, 2 => new[] { 2, 1 }, 3 => new[] { 1 }, _ => new[] { 1, 0 } };
        return idx.Any(i => ins[i].Equals(inst, StringComparison.OrdinalIgnoreCase));
    }

    // ---- resolution & scoring ----

    int Satisfy(Tile t, int slot)
    {
        double f = t.Dur > 0 ? (t.Left / t.Dur) / (1 - _grace) : 1;
        bool perfect = f >= 1;
        int pts = (int)Math.Floor(Math.Max(Math.Clamp(f, 0, 1) * t.Max, _minScore));
        Resolve(t, perfect ? TileState.Perfect : TileState.Met, pts);
        _score += pts;
        if (perfect) { _perfectN++; _perfectPts += pts; } else { _goodN++; _goodPts += pts; }
        _events.Add(new ScoreEvent(NextId(), Side.None, pts, perfect ? "Perfect" : "Request", slot, perfect ? "perfect!" : null));
        if (_events.Count > 30) _events.RemoveAt(0);
        T.EmitMusic(perfect ? "sfx:request_success2x" : "sfx:request_success");
        return pts;
    }

    void Resolve(Tile t, TileState s, int pts)
    {
        t.State = s; t.Points = pts; t.DoneFor = 0;
        _resolved++;
        if (s != TileState.Failed) _met++;
        else
        {
            _anyFail = true; _failedN++;
            if (t.Type != "Equalizer") _failedGroups.Add(t.Group);
            T.EmitMusic("sfx:request_failure");
        }
        int pip = _resolved - 1;
        if (pip >= 0 && pip < _pips.Count)
            _pips[pip] = s switch { TileState.Perfect => "perfect", TileState.Met => "met", TileState.Dismissed => "skipped", _ => "failed" };
    }

    /// <summary>After each resolution, once nothing is active.</summary>
    void AfterResolve()
    {
        if (_phase != Phase.Play || _tiles.Any(t => t.State == TileState.Active)) return;
        if (!_bonus)
        {
            if (_resolved < N) return;
            if (_resolved == _met && B > 0)
            {
                _bonus = true; Bumper(GameTables.PlainText("Party_BonusRequest", "Bonus round"));
                T.EmitMusic("sfx:score_sfx");
                return;
            }
            Bumper(B > 0 ? GameTables.PlainText("Party_BonusMissed", "No bonus") : null);
            EndRound();
        }
        else
        {
            if (_resolved == _met && _resolved < N + B) return;
            EndRound();
        }
    }

    void Bumper(string? text) { if (text == null) return; _bumperText = text; _bumperLeft = _bumperTime; _bumperId = NextId(); }

    void EndRound()
    {
        _bonus = false; _round++;
        if (_round < _table.Rounds.Count && _table.Rounds[_round].Count(_players) == 0) _bonus = true;   // e.g. double encore: all bonus
        Enter(Phase.WaitForRoundToEnd);
    }

    bool WantsAnotherRound() => _round < _table.Rounds.Count && (!_table.Rounds[_round].Encore || !_anyFail);

    // ---- fouls ----

    void Foul(string reason)
    {
        if (!InGame) return;
        var now = DateTime.UtcNow;
        if ((now - _lastFoul).TotalSeconds < FoulThrottle) return;
        _lastFoul = now;
        _fouls++;
        _foulText = reason; _foulLeft = _foulTime; _foulId = NextId();
        foreach (var t in _tiles.Where(t => t.State == TileState.Active)) Resolve(t, TileState.Failed, 0);
        _score -= _table.FoulPenalty;                       // not clamped: the score can go negative
        _events.Add(new ScoreEvent(NextId(), Side.None, -_table.FoulPenalty, "Foul", 2, reason));
        T.EmitMusic("sfx:score_loss");
        Notice($"{GameTables.PlainText("Party_PartyFoul", "Foul!")} {reason}");
        AfterResolve();
    }

    // ---- the DropMix button: Equalizer requests (§2.2) ----

    public override void Resumed(TimeSpan d) => _lastTick += d;

    public override ActionResult Button(Player? p)
    {
        // lobby (Menu_PartySetup): IconSelect_HitButton
        if (_phase is Phase.Lobby or Phase.GameOver) { BeginGame(); return ActionResult.Success; }
        if (!InGame) return ActionResult.Fail("Not now.");
        if (_spinning) return ActionResult.Fail("The wheel is already spinning.");
        var eq = _tiles.FirstOrDefault(t => t.State == TileState.Active && t.Type == "Equalizer");
        if (eq == null) { Foul("DropMix Button Not Requested"); return ActionResult.Success; }
        _wheelResult = GameTables.SpinWheel(GameTables.PartyWheelSlices, _rng);
        _spinning = true; _landed = false; _wheelAt = DateTime.UtcNow; _spinId = NextId();
        _delay += EqExtraDelay;
        Satisfy(eq, 2);
        T.EmitMusic("wheel_up");
        AfterResolve();
        return ActionResult.Success;
    }

    void LandWheel()
    {
        _landed = true; _wheelAt = DateTime.UtcNow;
        int n = 0;
        for (int i = 0; i < Rules.Slots; i++)
            if (T.TopValid(i) is PadCard pc && !pc.Silenced && (_wheelResult == 0 || pc.Card.Power == _wheelResult))
            { pc.Silenced = true; pc.SilencedAt = DateTime.UtcNow; n++; }
        string label = _wheelResult == 0 ? "X" : _wheelResult.ToString();
        T.EmitMusic("wheel_down");
        Notice(n == 0 ? $"Wheel: {label} — nothing to clear." : $"Wheel: {label} — lift {n} card{(n == 1 ? "" : "s")}.");
    }

    // ======================================================================================
    // spawning (§3.1, §3.2)
    // ======================================================================================

    int Occupied() => Enumerable.Range(0, Rules.Slots).Count(i => T.TopValid(i) is PadCard pc && !pc.Silenced);
    bool AllFull() => Enumerable.Range(0, Rules.Slots).All(i => T.TopValid(i) != null);

    bool IsFinalRequestIfSuccessful() => !_bonus ? (_met == _resolved ? false : _resolved == N - 1) : _met == N + B - 1;

    bool TypePossible(string type)
    {
        int occ = Occupied();
        bool InMix(string col) => Enumerable.Range(0, Rules.Slots).Any(i => T.TopValid(i) is PadCard pc && !pc.Silenced && ColourMatch(pc.Card, i, col));
        bool AllPower(int min) => Enumerable.Range(0, Rules.Slots).All(i => T.TopValid(i) is PadCard pc && pc.Card.Power >= min);
        return type switch
        {
            "Equalizer" => AllFull() && !_spinning && !IsFinalRequestIfSuccessful(),
            "NewGenre" or "NewInstrument" or "Crossfader" => occ >= 1,
            "RemoveLead" => occ >= 4 && InMix("Lead"), "RemoveLoop" => occ >= 4 && InMix("Loop"),
            "RemoveBeat" => occ >= 4 && InMix("Beat"), "RemoveBass" => occ >= 4 && InMix("Bass"),
            "RemoveFX" => occ >= 4 && Enumerable.Range(0, Rules.Slots).Any(i => T.TopValid(i)?.Card.Type == CardType.Fx),
            "AddGenre" => _players > 1,
            "FX" => occ is >= 1 and <= 4,
            "Level1" => !AllPower(2),
            "Level2" => !AllPower(3),
            _ => true,
        };
    }

    int PickGroup(List<Group> pool, int remaining)
    {
        double W(int i, bool history)
        {
            var g = pool[i];
            if (g.Types.Length > remaining) return 0;
            if (history && (i == _lastGroup || _failedGroups.Contains(i))) return 0;
            if (!g.Types.All(TypePossible)) return 0;
            return g.Weight;
        }
        for (int pass = 0; pass < 2; pass++)
        {
            var w = Enumerable.Range(0, pool.Count).Select(i => W(i, pass == 0)).ToArray();
            double sum = w.Sum();
            if (sum <= 0) continue;
            double r = _rng.NextDouble() * sum;
            for (int i = 0; i < w.Length; i++) { r -= w[i]; if (r <= 0 && w[i] > 0) return i; }
            return Array.FindLastIndex(w, x => x > 0);
        }
        return 0;
    }

    void SpawnGroup()
    {
        var pool = _bonus ? Cur.Bonus : Cur;
        if (pool == null || pool.Groups.Count == 0) { EndRound(); return; }
        int remaining = (_bonus ? N + B : N) - _resolved;
        if (remaining <= 0) { AfterResolve(); return; }
        int gi = PickGroup(pool.Groups, remaining);
        var g = pool.Groups[gi]; _lastGroup = gi;
        _tiles.Clear();
        for (int i = 0; i < g.Types.Length; i++)
        {
            double max = g.Max.Length == 0 ? 1000 : g.Max[Math.Min(i, g.Max.Length - 1)];
            double dur = g.Dur.Length == 0 ? 8 : g.Dur[Math.Min(i, g.Dur.Length - 1)];
            _tiles.Add(new Tile
            {
                Id = NextId(), Type = g.Types[i], Dur = dur, Left = dur, Max = max, Bonus = _bonus, Group = gi,
                Instrument = g.Types[i] == "AddInstrument" ? AddInstruments[_rng.Next(AddInstruments.Length)] : null,
            });
        }
        _delay = pool.Gap * (1 - _timingRandomness + _rng.NextDouble() * 2 * _timingRandomness);
        T.EmitMusic("sfx:request_appear");
    }

    // ======================================================================================
    // time
    // ======================================================================================

    bool Blocked() => _bumperLeft > 0 || _foulLeft > 0 || _spinning || AnySilenced();
    bool AnySilenced() => Enumerable.Range(0, Rules.Slots).Any(i => T.TopOf(i)?.Silenced == true);

    public override bool Tick(DateTime now)
    {
        double dt = Math.Min(0.5, (now - _lastTick).TotalSeconds); _lastTick = now;
        if (!InGame) return false;
        bool changed = false;
        int secBefore = (int)_phaseT; _phaseT += dt;
        if ((int)_phaseT != secBefore) changed = true;
        if (_bumperLeft > 0) { _bumperLeft -= dt; if (_bumperLeft <= 0) changed = true; }
        if (_foulLeft > 0) { _foulLeft -= dt; if (_foulLeft <= 0) changed = true; }
        for (int i = _reseat.Count - 1; i >= 0; i--) { var r = _reseat[i]; r.t += dt; if (r.t > ReseatWindow) _reseat.RemoveAt(i); else _reseat[i] = r; }

        // wheel; silenced virtual cards leave after a short reveal
        if (_spinning && !_landed && now - _wheelAt >= SpinTime) { LandWheel(); changed = true; }
        else if (_spinning && _landed && now - _wheelAt >= WheelShow) { _spinning = false; changed = true; }
        for (int i = 0; i < Rules.Slots; i++)
            if (T.TopOf(i) is { Source: CardSource.Player } pc && (pc.Silenced || !pc.Valid) && now - pc.SilencedAt >= ClearReveal) { T.PopTop(i); changed = true; }

        // request timers
        foreach (var t in _tiles)
        {
            if (t.State == TileState.Active)
            {
                double before = t.Left; t.Left -= dt; changed = true;
                if (!t.Warned && t.Left / t.Dur <= WarnAt) { t.Warned = true; T.EmitMusic("sfx:request_warning"); }
                if (t.Left <= 0) { t.Left = 0; Resolve(t, TileState.Failed, 0); AfterResolve(); }
            }
            else t.DoneFor += dt;
        }
        // tiles leave the screen once all are done (resolved + 2 s; Equalizer also waits for the wheel)
        if (_tiles.Count > 0 && _tiles.All(t => t.State != TileState.Active && t.DoneFor >= TileLinger) && !(_spinning && _tiles.Any(t => t.Type == "Equalizer")))
        { _tiles.Clear(); changed = true; }

        switch (_phase)
        {
            case Phase.GameStart when _phaseT >= _gameIntro: Enter(Phase.RoundIntro); return true;
            case Phase.RoundIntro when _phaseT >= _roundIntro: Enter(Phase.Play); return true;
            case Phase.Play:
                if (_tiles.Count == 0 && !Blocked())
                {
                    if (_delay <= 0) { SpawnGroup(); changed = true; }
                    else _delay -= dt;
                }
                break;
            case Phase.WaitForRoundToEnd:
                if (_tiles.Count == 0 && !Blocked()) { Enter(Phase.RoundComplete); return true; }
                break;
            case Phase.RoundComplete when _phaseT >= RoundCompleteHold:
                if (WantsAnotherRound()) T.EmitMusic("party_round");   // roundFusionTransitions
                foreach (var p in T.Players) T.DrawTo(p, HandSize);      // Party_RefillCards
                Enter(Phase.RoundBreak); return true;
            case Phase.RoundBreak when _phaseT >= _roundBreak:
                if (WantsAnotherRound()) Enter(Phase.RoundIntro); else EndGame();
                return true;
        }
        return changed;
    }

    // default leaderboard names by the first played card's deck: ClashConfig.playerIds[].partyLeaderboardDefaultName,
    // a text key resolved in the imported strings (GameTables.PartyLeaderboardName)
    string? _firstIcon; string _partyName = "";

    /// <summary>Rename the leaderboard entry from the results screen (PartyResultsScreen's ✎ name field).</summary>
    ActionResult RenameEntry(string? name)
    {
        name = (name ?? "").Trim();
        if (_phase != Phase.GameOver || _rank < 1 || name.Length is 0 or > 30) return ActionResult.Fail("Can't rename now.");
        var e = _board[_rank - 1]; _board[_rank - 1] = (e.score, e.difficulty, e.players, name); _partyName = name;
        SaveBoard(); return ActionResult.Success;
    }

    void EndGame()
    {
        _tiles.Clear();
        _phase = Phase.GameOver; _phaseT = 0;
        T.EmitMusic("party_end");
        T.EmitMusic("sfx:outro_party_hs");
        // leaderboard: only if the score isn't below the board's minimum
        int min = _board.Count >= 10 ? _board.Min(e => e.score) : int.MinValue;
        if (_score >= min)
        {
            string name = GameTables.PartyLeaderboardName(_firstIcon) is { Length: > 0 } pn ? pn
                : GameTables.Text("Party_DefaultScoreName", "Party: {0}").Replace("{0}", string.Join(" & ", T.Players.Select(p => p.Name).Take(3)));
            _partyName = name;
            _board.Add((_score, _difficulty.ToString(), _players, name));
            _board = _board.OrderByDescending(e => e.score).Take(20).ToList();
            _rank = _board.FindIndex(e => e.score == _score && e.name == name) + 1;
            SaveBoard();
        }
        _newHigh = _score > _highScoreBefore;
        Notice(_newHigh ? $"{GameTables.Caps("Party_NewHighScore", "BEST SCORE YET")} {_score:N0}" : $"Party over — {_score:N0}");
    }

    // ---- leaderboard (LeaderboardMgr.partyLeaderboard) ----

    void LoadBoard()
    {
        _board.Clear();
        if (_dataDir == null) return;
        try
        {
            if (_leaderboardPath != null && File.Exists(_leaderboardPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_leaderboardPath));
                foreach (var e in doc.RootElement.EnumerateArray())
                    _board.Add((e.GetProperty("score").GetInt32(), e.GetProperty("difficulty").GetString() ?? "", e.GetProperty("players").GetInt32(), e.GetProperty("name").GetString() ?? ""));
                return;
            }
            var csv = Path.Combine(_dataDir, "party_leaderboard_default.csv");
            if (File.Exists(csv))
                foreach (var line in File.ReadLines(csv).Skip(1))
                {
                    var f = line.Split(',');
                    if (f.Length >= 3 && int.TryParse(f[0], out int s) && int.TryParse(f[2], out int pl)) _board.Add((s, f[1], pl, "DropMix"));
                }
        }
        catch { }
    }

    void SaveBoard()
    {
        if (_leaderboardPath == null) return;
        try { File.WriteAllText(_leaderboardPath, JsonSerializer.Serialize(_board.Select(e => new { e.score, e.difficulty, e.players, e.name }))); }
        catch { }
    }

    // ======================================================================================
    // view
    // ======================================================================================

    static string Prompt(Tile t) => t.Type switch
    {
        "Lead" => GameTables.Caps("Party_AddYellow", "PLAY YELLOW"), "Loop" => GameTables.Caps("Party_AddRed", "PLAY RED"),
        "Beat" => GameTables.Caps("Party_AddBlue", "PLAY BLUE"), "Bass" => GameTables.Caps("Party_AddGreen", "PLAY GREEN"),
        "RemoveLead" => GameTables.Caps("Party_ClearYellow", "REMOVE YELLOW"), "RemoveLoop" => GameTables.Caps("Party_ClearRed", "REMOVE RED"),
        "RemoveBeat" => GameTables.Caps("Party_ClearBlue", "REMOVE BLUE"), "RemoveBass" => GameTables.Caps("Party_ClearGreen", "REMOVE GREEN"),
        "Level1" => "LEVEL 1", "Level2" => "LEVEL 2", "Level3" => "LEVEL 3",
        "Equalizer" => "EQUALIZER!", "FX" => "ADD FX",
        "Synth" => GameTables.Caps("Party_AddSynth", "PLAY SYNTH"), "Guitar" => GameTables.Caps("Party_AddGuitar", "PLAY GUITAR"),
        "Horns" => GameTables.Caps("Party_AddHorns", "PLAY HORNS"), "Strings" => "ADD STRINGS",
        "Vocal" => GameTables.Caps("Party_AddVocals", "PLAY VOCALS"), "Drums" => GameTables.Caps("Party_AddDrums", "PLAY DRUMS"), "Sampler" => "ADD SAMPLER",
        "AddInstrument" => "ADD " + (t.Instrument ?? "").ToUpperInvariant(),
        var s when s.StartsWith("Slot") => $"SLOT {s[4] - '0' + 1}",
        _ => t.Type.ToUpperInvariant(),
    };

    public override object View(Player? viewer)
    {
        string phase = _phase switch
        {
            Phase.Lobby => "lobby", Phase.GameStart => "intro", Phase.RoundIntro => "roundIntro", Phase.Play => "play",
            Phase.WaitForRoundToEnd => "play", Phase.RoundComplete or Phase.RoundBreak => "break", _ => "over",
        };
        bool canPlay = InGame && !_spinning;
        int roundShown = _phase is Phase.RoundComplete or Phase.RoundBreak or Phase.WaitForRoundToEnd ? _round - 1 : _round;
        return new
        {
            id = Id, title = Title,
            subtitle = _phase == Phase.Lobby ? "Everyone plays together" : $"{_difficulty} · {_players} player{(_players == 1 ? "" : "s")}",
            phase,
            slots = SlotViews(),
            legal = LegalMap(viewer, (c, i) => Placement(c, i) == null),
            canPlay,
            waitText = canPlay ? null : _phase == Phase.Lobby ? "Waiting for the party to start…" : _spinning ? "The wheel is spinning…" : "The party is over.",
            allowDraw = false, allowClear = InGame, allowUndo = false,
            difficulty = _difficulty.ToString(),
            score = _score,
            round = roundShown + 1, rounds = _table.Rounds.Count, roundLabel = RoundLabel(Math.Max(0, roundShown)),
            bonusPhase = _bonus, encoreAlive = !_anyFail,
            handSize = HandSize, players = _players,
            introLeft = _phase == Phase.GameStart ? Math.Max(0, _gameIntro - _phaseT) : (double?)null,
            breakLeft = _phase == Phase.RoundBreak ? Math.Max(0, _roundBreak - _phaseT) : (double?)null,
            nextLabel = _phase is Phase.RoundComplete or Phase.RoundBreak && WantsAnotherRound() ? RoundLabel(_round) : null,
            pips = _pips.ToArray(),
            tiles = _tiles.Select(t => new
            {
                id = t.Id, type = t.Type, prompt = Prompt(t), instrument = t.Instrument,
                fill = t.Dur > 0 ? Math.Clamp(1.1 * (t.Left / t.Dur) - 0.1, 0, 1) : 0,
                left = Math.Round(t.Left, 1), dur = t.Dur, max = (int)t.Max, bonus = t.Bonus,
                state = t.State.ToString().ToLowerInvariant(), points = t.Points,
            }).ToArray(),
            bumper = _bumperLeft > 0 ? new { id = _bumperId, text = _bumperText } : null,
            foul = _foulLeft > 0 ? new { id = _foulId, text = _foulText, penalty = _table.FoulPenalty } : null,
            wheel = new { state = _spinning ? (_landed ? "landed" : "spinning") : "idle", result = _landed ? (_wheelResult == 0 ? "X" : _wheelResult.ToString()) : null,
                          resultValue = _wheelResult, spin = _spinId, ms = (int)SpinTime.TotalMilliseconds, side = "none" },
            buttonArmed = _tiles.Any(t => t.State == TileState.Active && t.Type == "Equalizer"),
            highScore = _board.Count > 0 ? _board.Max(e => e.score) : 0,
            results = _phase == Phase.GameOver ? new
            {
                perfect = _perfectN, perfectPts = _perfectPts, good = _goodN, goodPts = _goodPts, failed = _failedN, fouls = _fouls,
                rank = _rank, newHigh = _newHigh, roundsPlayed = _round, name = _partyName,
            } : null,
            leaderboard = _board.Take(10).Select(e => new { e.name, e.score, e.difficulty, e.players }).ToArray(),
            events = _events.TakeLast(12).ToArray(),
            notice = new { id = _notice.id, text = _notice.text },
        };
    }
}
