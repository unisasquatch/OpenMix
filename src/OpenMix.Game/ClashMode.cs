using OpenMix.Cards;

namespace OpenMix.Game;

/// <summary>
/// Clash (head-to-head), implemented from (the original's scoring / ActionCounter /
/// WheelOfMisfortune). Section numbers in comments refer to that spec.
///
/// Adaptations for virtual cards: hands are dealt and drawn automatically (the original only
/// prompted players), invalid plays are rejected before the card leaves the hand, and cards the
/// wheel/FX mark for clearing are removed automatically after a short reveal (physical board cards
/// still have to be lifted off).
/// </summary>
public sealed class ClashMode : GameMode
{
    public override string Id => "clash";
    public override string Title => "Clash";

    // ---- options (§1.4) ----
    public int WinningScore = 21;            // 15 or 21
    public double? TurnLimit;                // null (off), 15, 30, 60 s

    // ---- scene values (§1, §3, §5, §7): ActionCounter, ScoreKeeper, CardsInHandCounter and ClashConfig, imported into
    // game_tables.json at setup; a game can't start without them ----
    static ClashRules R => GameTables.Clash!;
    static int ActionsPerTurn => GameTables.Clash?.ActionsPerTurn ?? 0;
    static TimeSpan AutoTurnEnd => TimeSpan.FromSeconds(R.AutomaticTurnEndSeconds);
    static readonly TimeSpan SpinTime = TimeSpan.FromSeconds(4.0);           // fallback without a music clock
    static readonly TimeSpan SpinCancelWindow = TimeSpan.FromSeconds(1.5);

    /// <summary>WheelOfMisfortune timing: spin up until the next bar downbeat (+4 beats if under half a beat
    /// away, +4 more if under 1 s), then exactly 8 beats of spin-down; cancel during spin-up + 2 beats.</summary>
    (TimeSpan total, TimeSpan down, TimeSpan cancel, double bpm) SpinTiming()
    {
        if (T.BeatClock?.Invoke() is not (double beat, double bpm) || bpm <= 0)
            return (SpinTime, SpinTime, SpinCancelWindow, GameTables.Audio?.StartingTempo ?? 0);   // no music clock: the clock's start tempo
        double toBar = Math.Ceiling(beat / 4 - 1e-9) * 4 - beat;
        if (toBar < 0.5) toBar += 4;
        if (toBar * 60 / bpm < 1) toBar += 4;
        double spb = 60 / bpm;
        return (TimeSpan.FromSeconds((toBar + 8) * spb), TimeSpan.FromSeconds(8 * spb), TimeSpan.FromSeconds((toBar + 2) * spb), bpm);
    }
    static readonly TimeSpan WheelShow = TimeSpan.FromSeconds(2.5);
    static readonly TimeSpan ClearReveal = TimeSpan.FromSeconds(2.5);

    enum Phase { Lobby, Play, Over }
    enum Wheel { Idle, Spinning, Landed }

    sealed class State
    {
        public Phase Phase = Phase.Lobby;
        public Side Current = Side.Blue;
        public int ActionsLeft = ActionsPerTurn;
        public FxFilter? FreeFilter; public string? FreeText;           // pending free action (extra_action)
        public bool FreeEq;                                              // pending free wheel spin (eq_spin)
        public string? FreeFx; public int FreeFxPad; public string? FreeFxCid;
        public string? Search;
        public string? FreeCid;                                          // the FX that granted the pending free action (badges)                                           // FX_0041 "search your deck": the player choosing   // the extra-action FX's music effect, for the free play
        public Dictionary<Side, int> Score = new() { [Side.Blue] = 0, [Side.Red] = 0 };
        public bool FirstTurnEnded;
        public int TurnNo = 1;
        public Side[] StartOwner = new Side[Rules.Slots];
        public string?[] StartCid = new string?[Rules.Slots];
        public Side Winner = Side.None;
        public Dictionary<Side, string> Icon = new();                   // team deck icons (Menu_IconSelect → the original's config)
        public double Elapsed;                                           // turn timer seconds
        public DateTime? TurnEndAt;
        public Wheel Wheel = Wheel.Idle; public DateTime WheelAt; public int WheelResult; public bool WheelBonus;
        public bool WheelPaidFree; public Side WheelSide; public long WheelSpinId;
        public TimeSpan WheelDur = TimeSpan.FromSeconds(4), WheelDown = TimeSpan.FromSeconds(4), WheelCancel = TimeSpan.FromSeconds(1.5); public double WheelBpm;
        public State Clone()
        {
            var s = (State)MemberwiseClone();
            s.Score = new Dictionary<Side, int>(Score); s.Icon = new Dictionary<Side, string>(Icon);
            s.StartOwner = (Side[])StartOwner.Clone(); s.StartCid = (string?[])StartCid.Clone();
            return s;
        }
    }

    State S = new();
    readonly List<ScoreEvent> _events = new();

    (long id, string text, string? to) _notice;
    /// <summary>A play this turn, with the table and state just before it (lift-to-undo and phone take-back).</summary>
    sealed class PlayRec
    {
        public Table.TableSnap Table = null!; public State Before = null!;
        public string Player = ""; public Card Card = null!; public int Pad; public CardSource Src;
        public int Phys;                                    // _phys when played: nothing physical happened since if equal
        public Dictionary<Side, int> Delta = new();         // points this play changed, per side
        public bool UsedFree; public FxFilter? Granted; public bool GrantedEq;
    }
    readonly List<PlayRec> _plays = new();
    int _phys;                                              // bumps on wheel spins and cleared cards (non-replayable events)
    bool _quiet;                                            // replaying plays after a lift: no sounds or score pops
    bool _usedFree;
    void Emit(string m) { if (!_quiet) T.EmitMusic(m); }
    DateTime _lastTick = DateTime.UtcNow;

    // ======================================================================================
    // setup
    // ======================================================================================

    public override void Configure(IDictionary<string, string> o)
    {
        if (o.TryGetValue("score", out var sc) && int.TryParse(sc, out var n)) WinningScore = n <= 15 ? 15 : 21;
        if (o.TryGetValue("timer", out var tm)) TurnLimit = double.TryParse(tm, out var t) && t > 0 ? t : null;
    }

    // ======================================================================================
    // team icons (Menu_IconSelect): slots 1-2 = team A (Red), 4-5 = team B (Blue); a slot's cards offer their deck
    // icons, otherwise any deck in play; the other team's icon is excluded; random pick; tap re-rolls
    // ======================================================================================

    /// <summary>ClashConfig.playerIds (imported into game_tables.json).</summary>
    static IReadOnlyList<string> PlayerIds => GameTables.ClashPlayerIds;
    readonly List<Card>[] _lobbyCards = Enumerable.Range(0, Rules.Slots).Select(_ => new List<Card>()).ToArray();
    static Side SideOfSlot(int pad) => pad <= 1 ? Side.Red : pad >= 3 ? Side.Blue : Side.None;

    void ChooseIcon(Side side)
    {
        var other = S.Icon.TryGetValue(Rules.Other(side), out var o) ? o : null;
        var fromCards = Enumerable.Range(0, Rules.Slots).Where(i => SideOfSlot(i) == side).SelectMany(i => _lobbyCards[i])
            .Select(c => c.Icon).Where(ic => PlayerIds.Contains(ic) && ic != other).Distinct().ToList();
        var owned = PlayerIds.Where(id => id != other && T.IsDeckActive(id) && T.ActiveCards.Any(c => c.Icon == id)).ToList();
        var pool = fromCards.Count > 0 ? fromCards : owned.Count > 0 ? owned : PlayerIds.Where(id => id != other).ToList();
        if (pool.Count == 0) return;                                     // no decks imported
        S.Icon[side] = pool[T.Rng.Next(pool.Count)];
        Emit("icon_pick");
    }

    public override void Start()
    {
        S = new State();
        foreach (var l in _lobbyCards) l.Clear();
        ChooseIcon(Side.Red); ChooseIcon(Side.Blue);
        _events.Clear(); _plays.Clear();
        foreach (var p in T.Players) if (p.Team == Side.None) p.Team = SmallerSide();
        Notice("Pick a team, then press Start.");
    }

    Side SmallerSide() => TeamOf(Side.Blue).Count <= TeamOf(Side.Red).Count ? Side.Blue : Side.Red;
    List<Player> TeamOf(Side s) => T.Players.Where(p => p.Team == s).OrderBy(p => p.Name).ToList();
    /// <summary>Index into the per-team-size rules: 0 = a one-player team, 1 = two players.</summary>
    int TeamSize(Side s) => TeamOf(s).Count >= 2 ? 1 : 0;
    int StartHand(Side s) => R.StartingHand[TeamSize(s)];      // §1.2
    int DrawSize(Side s) => R.DrawSize[TeamSize(s)];
    int MaxHand(Side s) => R.MaxHand[TeamSize(s)];

    public override void OnPlayerJoined(Player p)
    {
        var side = SmallerSide();
        if (TeamOf(side).Count >= 2 && S.Phase != Phase.Lobby) { p.Team = Side.None; Notice($"{p.Name} is watching (teams are full)."); return; }
        p.Team = side;
        if (S.Phase == Phase.Play) T.DrawTo(p, StartHand(side));
    }

    public override void OnPlayerLeft(Player p) { }

    public override ActionResult ChooseTeam(Player p, Side side)
    {
        if (S.Phase != Phase.Lobby) return ActionResult.Fail("Teams are locked once the game starts.");
        if (side is not (Side.Blue or Side.Red)) return ActionResult.Fail("Pick Blue or Red.");
        if (p.Team != side && TeamOf(side).Count >= 2) return ActionResult.Fail($"{side} already has 2 players.");
        if (p.Team != side) Emit("lobby_join");
        p.Team = side;
        return ActionResult.Success;
    }

    public override ActionResult HostAction(string action, IDictionary<string, string> args)
    {
        switch (action)
        {
            case "start": return StartGame();
            case "options":
                if (S.Phase != Phase.Lobby) return ActionResult.Fail("Change options in the lobby.");
                Configure(args); return ActionResult.Success;
            case "lobby": Start(); foreach (var p in T.Players) { p.Hand.Clear(); p.OwnDeck = null; } return ActionResult.Success;
            case "reroll":
                if (S.Phase != Phase.Lobby) return ActionResult.Fail("Pick icons in the lobby.");
                ChooseIcon(args.TryGetValue("side", out var sd) && sd == "Blue" ? Side.Blue : Side.Red); return ActionResult.Success;
        }
        return ActionResult.Fail("Unknown action.");
    }

    public string? TeamIcon(Side s) => S.Icon.GetValueOrDefault(s);

    ActionResult SearchPick(Player p, string? cid)
    {
        if (S.Search != p.Name || p.OwnDeck == null) return ActionResult.Fail("Nothing to search for.");
        var c = p.OwnDeck.FirstOrDefault(x => x.Cid == cid);
        if (c == null) return ActionResult.Fail("That card isn't in your deck.");
        p.OwnDeck.Remove(c); p.Hand.Add(c); T.Shuffle(p.OwnDeck);
        S.Search = null; Notice($"{p.Name} found a card.");
        return ActionResult.Success;
    }

    public override ActionResult Action(Player p, string action, string? arg) => action switch
    {
        "start" => StartGame(),
        "team" => ChooseTeam(p, Enum.TryParse<Side>(arg, true, out var s) ? s : Side.None),
        "search" => SearchPick(p, arg),
        _ => ActionResult.Fail("Unknown action."),
    };

    public override bool AcceptsBoardCards => true;          // in the lobby, decks on the team slots choose the icons

    ActionResult StartGame()
    {
        if (BoardMustBeCleared()) { Notice("Clear the board before starting."); return ActionResult.Fail(GameTables.Caps("IconSelect_ReadyToStart", "CLEAR THE BOARD FIRST")); }
        if (S.Phase == Phase.Play) return ActionResult.Fail("Already playing.");
        if (GameTables.Clash == null) return ActionResult.Fail($"Clash rules aren't imported ({GameTables.Error}). Run setup again.");
        if (TeamOf(Side.Blue).Count == 0 || TeamOf(Side.Red).Count == 0) return ActionResult.Fail("Each team needs at least one player.");
        if (S.Phase == Phase.Over)
        {   // rematch: clear the board, return everything to the deck
            for (int i = 0; i < Rules.Slots; i++) while (T.Pads[i].Count > 0) T.PopTop(i);
            foreach (var pl in T.Players) { T.ReturnToDeck(pl.Hand); pl.Hand.Clear(); if (pl.OwnDeck != null) { T.ReturnToDeck(pl.OwnDeck); pl.OwnDeck = null; } }
            T.ReturnToDeck(T.DiscardPile.ToList()); T.DiscardPile.Clear();
        }
        var icons = S.Icon;
        S = new State { Phase = Phase.Play, Icon = icons };
        foreach (var l in _lobbyCards) l.Clear();
        BuildDecks();
        _turnStartScore[Side.Blue] = 0; _turnStartScore[Side.Red] = 0;
        Emit("intro:clash");
        _events.Clear(); _plays.Clear();
        foreach (var side in new[] { Side.Blue, Side.Red })
            foreach (var pl in TeamOf(side)) T.DrawTo(pl, StartHand(side));
        S.Current = T.Rng.NextDouble() < 0.5 ? Side.Red : Side.Blue;   // coin flip (§1.3)
        SnapshotTurnStart();
        _startedAt = DateTime.UtcNow;
        Notice($"Coin flip: {S.Current} goes first!");
        return ActionResult.Success;
    }

    /// <summary>Each phone player's own virtual deck within the deck rules (§1.2, ClashConfig, imported): a deck size and a total
    /// level budget per team size (minHandSize*, maxPowerBudget*). Wild and FX count as level 4. Cards come
    /// from the decks in play, preferring the team's icon deck; nobody shares a card.</summary>
    void BuildDecks()
    {
        var pool = T.Deck.ToList(); T.Shuffle(pool);
        foreach (var side in new[] { Side.Red, Side.Blue })
        {
            var team = TeamOf(side); if (team.Count == 0) continue;
            int size = R.DeckSize[TeamSize(side)], budget = R.PowerBudget[TeamSize(side)];
            string? icon = S.Icon.GetValueOrDefault(side);
            foreach (var pl in team)
            {
                var deck = new List<Card>(); int level = 0;
                static int Lv(Card c) => c.Type is CardType.Wild or CardType.Fx ? 4 : Math.Max(1, c.Power);
                foreach (var c in pool.OrderByDescending(c => c.Icon == icon).ThenBy(_ => T.Rng.Next()).ToList())
                {
                    if (deck.Count >= size) break;
                    int left = size - deck.Count - 1;                         // keep room for level-1 cards to fill up
                    if (level + Lv(c) + left > budget) continue;
                    deck.Add(c); level += Lv(c); pool.Remove(c);
                }
                T.Shuffle(deck);
                pl.OwnDeck = deck;
                foreach (var c in deck) T.Deck.Remove(c);
            }
        }
    }

    // ======================================================================================
    // play validity (§3.2)
    // ======================================================================================

    bool MeetsFree(Card c, int pad) => S.FreeFilter is FxFilter f && (f.Kind == "song"
        ? Enumerable.Range(0, Rules.Slots).Any(i => T.TopValid(i) is PadCard pc && SameSong(pc.Card, c))
        : f.Matches(c, pad));

    static bool SameSong(Card a, Card b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase);

    IEnumerable<(FxAbility fx, int pad)> ActiveBlocks()
    {
        for (int i = 0; i < Rules.Slots; i++)
            if (T.TopValid(i) is PadCard pc && !pc.Silenced && pc.Card.Type == CardType.Fx
                && FxAbility.Parse(pc.Card.Ability) is { Kind: FxKind.Block } fx)
                yield return (fx, i);
    }

    /// <summary>null = valid, otherwise the player-facing reason (first failure wins).</summary>
    string? Validity(Card c, int pad, bool ignoreActions = false) => Check(c, pad, ignoreActions)?.msg;

    /// <summary>the original's scoring (§3.2) in order; title = the ErrorDisplay text for a physical foul.</summary>
    (string title, string msg)? Check(Card c, int pad, bool ignoreActions = false)
    {
        bool free = MeetsFree(c, pad);
        if (S.FreeFilter != null && !free) return ($"FREE ACTION: {S.FreeText?.ToUpperInvariant()}", $"Your free action needs: {S.FreeText}.");   // DoesntMeetFreeAction
        if (!ignoreActions && S.ActionsLeft < 1 && !free) return (GameTables.Caps("Error_NoMoreActions", "OUT OF PLAYS"), "No plays left this turn.");                              // NotEnoughActions
        if (c.Cid.StartsWith("unknown_")) return ("UNKNOWN CARD", "Unknown card.");                                                // NoData
        if (!Rules.IsTypeValidInSlot(c.Type, pad)) return (GameTables.Caps("Error_WrongColor", "WRONG SLOT"), $"{c.Type} cards don't go in slot {pad + 1}.");                       // InvalidSlot
        var existing = T.TopValid(pad);
        if (existing != null && c.Power < existing.Card.Power)                                                                                   // NotEnoughPower
            return (GameTables.Caps("Error_LevelTooLow", "LEVEL NOT HIGH ENOUGH"), $"Needs level {existing.Card.Power} or higher to cover that card.");
        for (int i = 0; i < Rules.Slots; i++)                                                                                                    // DuplicateCard
            if (T.TopValid(i)?.Card.Cid == c.Cid) return (GameTables.Caps("Error_DuplicateCard", "ALREADY IN THE MIX"), "That card is already in the mix.");
        foreach (var (fx, _) in ActiveBlocks())                                                                                                  // Blocked
            if (fx.Filter != null && fx.Filter.Matches(c, pad)) return (GameTables.Caps("Error_Blocked", "BLOCKED"), $"Blocked: {fx.Describe()}.");
        if (AnySilenced()) return (GameTables.Caps("Lanes_ErrorCardClearSilenced", "CLEAR THE SILENCED CARDS"), "Clear the silenced cards first.");                                // SilencedOnBoard
        if (AnyFoul()) return ("HOLD ON", "Return the flashing card to your hand first.");                                                // FoulOnBoard
        return null;
    }

    /// <summary>ErrorDisplay.IsSlotInError: a foul card is sitting on top of a slot.</summary>
    bool AnyFoul() => Enumerable.Range(0, Rules.Slots).Any(i => T.TopOf(i) is { Valid: false, Error: not null });
    public override bool AcceptsUnknownCards => S.Phase == Phase.Play;

    bool AnySilenced() => Enumerable.Range(0, Rules.Slots).Any(i => T.TopOf(i)?.Silenced == true);

    bool CanAct(Player? p, out string? why)
    {
        why = null;
        if (S.Phase == Phase.Lobby) { why = "The game hasn't started."; return false; }
        if (S.Phase == Phase.Over) { why = "The game is over."; return false; }
        if (p != null && p.Team != S.Current) { why = p.Team == Side.None ? "You're watching this game." : $"It's {S.Current}'s turn."; return false; }
        if (S.Wheel == Wheel.Spinning) { why = "Wait for the wheel."; return false; }
        return true;
    }

    // ======================================================================================
    // playing a card
    // ======================================================================================

    public override ActionResult Play(Player p, Card card, int pad)
    {
        if (!CanAct(p, out var why)) return ActionResult.Fail(why!);
        if (Validity(card, pad) is string err) return ActionResult.Fail(err);
        PlayRecorded(card, pad, p, CardSource.Player);
        return ActionResult.Success;
    }

    /// <summary>A play, remembered with the table just before it so it can be taken back or lifted off.</summary>
    void PlayRecorded(Card card, int pad, Player? p, CardSource src)
    {
        var rec = new PlayRec { Table = T.SaveState(), Before = S.Clone(), Player = p?.Name ?? "board", Card = card, Pad = pad, Src = src, Phys = _phys };
        var freeBefore = S.FreeFilter; bool eqBefore = S.FreeEq;
        p?.Hand.Remove(card);
        DoPlay(card, pad, p, src);
        foreach (var sd in new[] { Side.Blue, Side.Red }) rec.Delta[sd] = S.Score[sd] - rec.Before.Score[sd];
        rec.UsedFree = _usedFree;
        rec.Granted = S.FreeFilter != null && !ReferenceEquals(S.FreeFilter, freeBefore) ? S.FreeFilter : null;
        rec.GrantedEq = S.FreeEq && !eqBefore;
        _plays.Add(rec);
    }

    void DoPlay(Card card, int pad, Player? p, CardSource src)
    {
        // spend: a matching free action first, otherwise a normal action (§3.1)
        _usedFree = S.FreeFilter != null && MeetsFree(card, pad);
        if (_usedFree)
        {
            if (S.FreeCid != null) Badge("clash_playExtra", S.FreeCid);   // the extra-action FX's music effect lands on the slot where the free card goes
            if (S.FreeFx != null) Emit($"fxdsp:{S.FreeFx}:{S.FreeFxPad}:{S.FreeFxCid}:{pad}");
            S.FreeFilter = null; S.FreeText = null; S.FreeFx = null;
        }
        else S.ActionsLeft = Math.Max(0, S.ActionsLeft - 1);

        int scoreBefore = S.Score[S.Current]; var side = S.Current;
        Badge("clash_playCount", card.Cid);
        // colours present before this play: other slots' cards + this slot's previous card (§5.1)
        var before = new HashSet<CardType>();
        for (int i = 0; i < Rules.Slots; i++)
            if (T.TopValid(i) is PadCard pc && !pc.Silenced) foreach (var t in Rules.CountsAs(pc.Card, i)) before.Add(t);
        var covered = T.TopValid(pad);

        T.Pads[pad].Add(new PadCard { Card = card, Source = src, PlayedBy = p?.Name, Owner = S.Current, TurnPlayed = S.TurnNo });
        S.TurnEndAt = null;
        Emit(card.Type == CardType.Fx ? "keysfx:enter_fx" : "keysfx:enter_music");     // CardEnterMix

        // ---- points (§5.1) ----
        Award(S.Current, 1, "Slot", pad);
        if (S.FirstTurnEnded)
            foreach (var col in Rules.ColourOrder)
                if (Rules.CountsAs(card, pad).Contains(col) && !before.Contains(col))
                    Award(S.Current, 1, "Bounty" + col, pad, $"new {ColourName(col)}");

        var fx = card.Type == CardType.Fx ? FxAbility.Parse(card.Ability) : null;
        if (fx != null) ApplyFx(fx, card, pad, p, covered);

        if (OwnsAll(S.Current) && S.StartOwner[pad] != S.Current)
        {
            Award(S.Current, R.FillMixBonus, "MixFill", pad, GameTables.PlainText("HeadToHead_FullMix", "all slots").ToLowerInvariant() + "!");
            foreach (var mc in Enumerable.Range(0, Rules.Slots).Select(i => T.TopValid(i)?.Card).Where(c => c != null).DistinctBy(c => c!.Icon)) Badge("clash_fullMix", mc!.Cid);
            Emit("keysfx:full_mix");
        }
        if (S.Score[side] > scoreBefore) Badge("clash_scoreSinglePlay", card.Cid, S.Score[side] - scoreBefore);
        CheckWin();
    }

    /// <summary>A badge event for the host's BadgeStore (silent while plays are being re-applied after a lift).</summary>
    void Badge(string tracker, string cid, double measured = 0, int amount = 1) =>
        Emit(FormattableString.Invariant($"badge:{tracker}:{cid}:{measured}:{amount}"));

    static string ColourName(CardType t) => t switch { CardType.Bass => "green", CardType.Beat => "blue", CardType.Loop => "red", CardType.Lead => "yellow", _ => t.ToString() };

    bool OwnsAll(Side s) => Enumerable.Range(0, Rules.Slots).All(i => T.TopValid(i) is PadCard pc && !pc.Silenced && pc.Owner == s);

    readonly Dictionary<Side, int> _turnStartScore = new() { [Side.Blue] = 0, [Side.Red] = 0 };

    DateTime _scoreQueueUntil = DateTime.MinValue;
    double BeatSecs => T.BeatClock?.Invoke() is (double, double bpm) && bpm > 0 ? 60 / bpm : 0.5;

    void Award(Side s, int pts, string kind, int slot, string? note = null)
    {
        if (S.Winner != Side.None || pts == 0) return;
        if (pts > 0 && !_quiet)
        {   // the original's scoring queues each gain and applies one per beat (the chimes): the queue must drain before a win / turn end
            var now = DateTime.UtcNow; if (_scoreQueueUntil < now) _scoreQueueUntil = now;
            _scoreQueueUntil = _scoreQueueUntil.AddSeconds(BeatSecs * Math.Min(pts, 6));
        }
        int old = S.Score[s];
        S.Score[s] = Math.Max(0, S.Score[s] + pts);
        // audio (the original's scoring): gains queue a chime per event, paced one per beat; losses play score_loss now
        if (S.Score[s] > old) Emit($"score:{S.Score[s] - _turnStartScore[s]}");
        else if (S.Score[s] < old) Emit("keysfx:score_loss");
        if (!_quiet) _events.Add(new ScoreEvent(NextId(), s, pts, kind, slot, note));
        if (_events.Count > 30) _events.RemoveAt(0);
    }

    void CheckWin()
    {
        if (S.Winner != Side.None || AnySilenced() || AnyFoul() || DateTime.UtcNow < _scoreQueueUntil) return;     // CanProgress: no silenced slots, no error showing
        foreach (var s in new[] { S.Current, Rules.Other(S.Current) })
            if (S.Score[s] >= WinningScore) { S.Winner = s; S.Phase = Phase.Over; Notice($"{s} wins {S.Score[s]}–{S.Score[Rules.Other(s)]}!"); Emit("outro:outro_clash"); Emit($"winvo:{s}"); return; }
    }

    // ======================================================================================
    // FX abilities (§6)
    // ======================================================================================

    IEnumerable<Side> Targets(FxTarget t) => t switch
    {
        FxTarget.Opponent => new[] { Rules.Other(S.Current) },
        FxTarget.Both => new[] { Side.Blue, Side.Red },
        _ => new[] { S.Current },
    };

    void ApplyFx(FxAbility fx, Card card, int pad, Player? p, PadCard? covered)
    {
        switch (fx.Kind)
        {
            case FxKind.Remove:
            {
                var sides = Targets(fx.Target).ToHashSet();
                IEnumerable<int> hit;
                if (fx.Filter?.Kind == "adjacent") hit = new[] { pad - 1, pad + 1 }.Where(i => i >= 0 && i < Rules.Slots);
                else if (fx.Filter?.Kind == "random_opponent")
                {
                    var opp = Enumerable.Range(0, Rules.Slots).Where(i => i != pad && T.TopValid(i)?.Owner == Rules.Other(S.Current)).ToList();
                    hit = opp.Count == 0 ? Array.Empty<int>() : new[] { opp[T.Rng.Next(opp.Count)] };
                    sides = new HashSet<Side> { Rules.Other(S.Current) };
                }
                else hit = Enumerable.Range(0, Rules.Slots).Where(i => T.TopValid(i) is PadCard pc && (fx.Filter == null || fx.Filter.Matches(pc.Card, i)));
                int n = 0;
                foreach (var i in hit.Where(i => i != pad))
                    if (T.TopValid(i) is PadCard pc && sides.Contains(pc.Owner) && !pc.Silenced) { Silence(pc); n++; }
                Notice(n > 0 ? $"{card.Name}: {n} card{(n == 1 ? "" : "s")} cleared!" : $"{card.Name}: nothing to clear.");
                if (n > 0) Badge("clash_clearCumulative", card.Cid, n, n);
                break;
            }
            case FxKind.ExtraAction:
                S.FreeFilter = fx.Filter ?? new FxFilter("any", Array.Empty<string>());
                S.FreeFx = card.MusicEffect.Length > 0 ? card.MusicEffect : null; S.FreeFxPad = pad; S.FreeFxCid = card.Cid; S.FreeCid = card.Cid;
                S.FreeText = fx.Filter == null ? "any card" : fx.Describe().Replace("Extra action: play ", "");
                Notice($"Free action: play {S.FreeText}.");
                break;
            case FxKind.EqSpin:
                S.FreeEq = true; Notice("Free spin! Press the DropMix button.");
                break;
            case FxKind.ScoreAdjust:
            {
                int amt = fx.Amount == -1 ? T.Rng.Next(1, 4) : fx.Amount;
                foreach (var s in Targets(fx.Target)) Award(s, amt, "FX", pad, card.Name);
                if (amt < 0 && fx.Target != FxTarget.Self) Badge("clash_subtractCumulative", card.Cid, -amt, -amt);
                break;
            }
            case FxKind.SetBonus:
            {
                int pts = SetBonusPoints(fx, pad);
                if (pts > 0) Award(S.Current, pts, "FX", pad, card.Name); else Notice($"{card.Name}: no bonus this time.");
                break;
            }
            case FxKind.Combo:
            {
                var mine = MyCards(S.Current, pad).Select(x => x.card).ToList();
                int pairs = mine.GroupBy(c => (c.Name.ToLowerInvariant(), c.Artist.ToLowerInvariant())).Sum(g => g.Count() / 2);
                int pts = pairs * Math.Max(1, fx.Amount);
                if (pts > 0) Award(S.Current, pts, "FX", pad, card.Name); else Notice($"{card.Name}: no same-song pairs.");
                break;
            }
            case FxKind.Draw when card.ScreenText == "fx_cards_search" && fx.Target == FxTarget.Self && p?.OwnDeck is { Count: > 0 }:
                S.Search = p.Name; Notice($"{p.Name}: search your deck for a card.");
                break;
            case FxKind.Draw:
                foreach (var pl in fx.Target == FxTarget.Both ? T.Players.Where(x => x.Team != Side.None) : (p != null ? new[] { p } : Array.Empty<Player>()))
                {
                    if (fx.Amount == 0) { int k = pl.Hand.Count; T.ReturnToDeck(pl.Hand); pl.Hand.Clear(); T.DrawN(pl, k); }
                    else T.DrawN(pl, fx.Amount);
                }
                Notice(fx.Describe() + ".");
                break;
            case FxKind.Cover:
            {
                bool hitCover = fx.Filter?.Kind == "empty" ? covered == null
                              : covered != null && fx.Filter != null && fx.Filter.Matches(covered.Card, pad);
                if (!hitCover) { Notice($"{card.Name}: no cover bonus."); break; }
                if (int.TryParse(fx.CoverEffect, out var pts)) Award(S.Current, pts, "FX", pad, card.Name);
                else if (fx.CoverEffect?.StartsWith("draw") == true && p != null) { T.DrawN(p, 1); Notice($"{p.Name} draws 1."); }
                Badge("clash_coverBonus", card.Cid);
                break;
            }
            case FxKind.Block:
                Notice($"{card.Name}: {fx.Describe()}.");
                break;
        }
    }

    IEnumerable<(Card card, int slot)> MyCards(Side side, int excludePad) =>
        Enumerable.Range(0, Rules.Slots).Where(i => i != excludePad)
            .Select(i => (pc: T.TopValid(i), i)).Where(x => x.pc != null && !x.pc.Silenced && x.pc.Owner == side)
            .Select(x => (x.pc!.Card, x.i));

    int SetBonusPoints(FxAbility fx, int pad)
    {
        if (fx.Filter == null) return 0;
        if (fx.Filter.Kind == "empty")
            return Enumerable.Range(0, Rules.Slots).Count(i => i != pad && T.TopValid(i) == null) * fx.Amount;
        var mine = MyCards(S.Current, pad).ToList();
        if (fx.Filter.Values.Length <= 1)
            return mine.Count(x => fx.Filter.Matches(x.card, x.slot)) * fx.Amount;
        // several values: n per complete set (one card of each value) [spec: inferred from screen text]
        int sets = fx.Filter.Values.Min(v => mine.Count(x => new FxFilter(fx.Filter.Kind, new[] { v }).Matches(x.card, x.slot)));
        return sets * fx.Amount;
    }

    void Silence(PadCard pc) { pc.Silenced = true; pc.SilencedOwner = pc.Owner; pc.SilencedAt = DateTime.UtcNow; }

    // ======================================================================================
    // DropMix button: Equalizer / Wheel of Misfortune (§7)
    // ======================================================================================

    public override ActionResult Button(Player? p)
    {
        if (S.Phase == Phase.Lobby && p == null) return StartGame();   // setup: IconSelect_HitButton
        if (S.Phase != Phase.Play) return ActionResult.Fail("The game hasn't started.");
        if (p != null && p.Team != S.Current) return ActionResult.Fail($"It's {S.Current}'s turn.");
        if (S.Wheel == Wheel.Spinning)
        {
            // second press early in the spin cancels it and refunds the action (§7.4)
            if (!S.WheelPaidFree && DateTime.UtcNow - S.WheelAt < S.WheelCancel)
            { S.Wheel = Wheel.Idle; S.ActionsLeft++; Notice("Spin cancelled."); Emit("wheel_stop"); return ActionResult.Success; }
            return ActionResult.Fail("The wheel is spinning.");
        }
        if (AnySilenced()) return ActionResult.Fail("Clear cards first.");
        if (AnyFoul()) return ActionResult.Fail("Return the flashing card to your hand first.");
        if (S.FreeFilter != null && !S.FreeEq) return ActionResult.Fail($"Use your free action first ({S.FreeText}).");
        if (S.ActionsLeft < 1 && !S.FreeEq) return ActionResult.Fail("No plays left.");

        var opp = Rules.Other(S.Current);
        S.WheelBonus = !Enumerable.Range(0, Rules.Slots).Any(i => T.TopValid(i) is PadCard pc && !pc.Silenced && pc.Owner == opp);
        if (S.FreeEq) { S.FreeEq = false; S.WheelPaidFree = true; } else { S.ActionsLeft--; S.WheelPaidFree = false; }
        S.WheelResult = GameTables.SpinWheel(GameTables.WheelSlices, T.Rng);
        S.Wheel = Wheel.Spinning; S.WheelAt = DateTime.UtcNow; S.WheelSide = S.Current; S.WheelSpinId = NextId();
        (S.WheelDur, S.WheelDown, S.WheelCancel, S.WheelBpm) = SpinTiming();
        Emit(FormattableString.Invariant($"wheel_spin:{S.WheelDur.TotalSeconds}"));
        S.TurnEndAt = null;
        _phys++;
        Notice($"{S.Current} spins the wheel!");
        return ActionResult.Success;
    }

    void LandWheel()
    {
        var opp = Rules.Other(S.WheelSide);
        string label = S.WheelResult == 0 ? "X" : S.WheelResult.ToString();
        if (S.WheelBonus)
        {
            if (S.WheelResult == 0) { Award(S.WheelSide, 1, "Wheel", 2, "bonus!"); Notice("Bonus slice! +1"); }
            else Notice($"Wheel: {label} — nothing to clear.");
        }
        else
        {
            int n = 0;
            for (int i = 0; i < Rules.Slots; i++)
                if (T.TopValid(i) is PadCard pc && !pc.Silenced && pc.Owner == opp && (S.WheelResult == 0 || pc.Card.Power == S.WheelResult))
                { Silence(pc); n++; }
            Notice(S.WheelResult == 0 ? $"X! {opp} must clear ALL their cards ({n})." : n > 0
                ? $"Level {label}: {opp} clears {n} card{(n == 1 ? "" : "s")}." : $"Level {label}: {opp} has none.");
        }
        S.Wheel = Wheel.Landed; S.WheelAt = DateTime.UtcNow;
    }

    // ======================================================================================
    // physical board
    // ======================================================================================

    public override void BoardPlaced(Card card, int pad)
    {
        if (S.Phase == Phase.Lobby)
        {
            _lobbyCards[pad].Add(card);
            if (SideOfSlot(pad) is Side sd and not Side.None && PlayerIds.Contains(card.Icon) && S.Icon.GetValueOrDefault(sd) != card.Icon
                && S.Icon.GetValueOrDefault(Rules.Other(sd)) != card.Icon) ChooseIcon(sd);
            return;
        }
        if (S.Phase != Phase.Play) { T.Pads[pad].Add(new PadCard { Card = card, Source = CardSource.Board, Valid = false }); return; }
        // ErrorDisplay: a foul shows on the current side's half until the card is lifted; nothing else can happen meanwhile
        (string title, string msg)? foul = S.Wheel == Wheel.Spinning ? (GameTables.Caps("Lanes_ErrorCardSpinner", "WAIT FOR THE WHEEL"), "Wait for the wheel.") : Check(card, pad);
        if (foul is var (title, msg))
        {
            T.Pads[pad].Add(new PadCard { Card = card, Source = CardSource.Board, Valid = false, Owner = S.Current, TurnPlayed = S.TurnNo, Error = title });
            Notice($"{msg} Return the card to your hand.");
            Emit("keysfx:request_warning");
            return;
        }
        PlayRecorded(card, pad, null, CardSource.Board);
        if (GameTables.Clash is { } rules && T.Pads[pad].Count > rules.StackHeightWarningThreshold)   // ClashConfig.stackHeightWarningThreshold
            Notice($"Slot {pad + 1} has more than {rules.StackHeightWarningThreshold} cards: the board may not read them all.");
    }

    public override void BoardLifted(Card card, int pad)
    {
        if (S.Phase == Phase.Lobby) { int li = _lobbyCards[pad].FindLastIndex(c => c.Cid == card.Cid); if (li >= 0) _lobbyCards[pad].RemoveAt(li); return; }
        var s = T.Pads[pad];
        int i = s.FindLastIndex(pc => pc.Source == CardSource.Board && pc.Card.Cid == card.Cid);
        if (i < 0) return;
        var pc = s[i];
        if (i == s.Count - 1 && LiftUndo(pc, pad)) return;
        s.RemoveAt(i);                                   // a foul, or an older card: the card underneath shows again
        if (pc.Silenced) { _phys++; Award(pc.SilencedOwner, -R.CardRemovalPenalty, "Clear", pad, "cleared"); CheckWin(); }
    }

    /// <summary>Lifting a card played this turn takes the play back (ActionCounter.CardPlayed kRemoved → UndoAction;
    /// the original's scoring re-scores the turn's remaining plays). If only plays happened since, the table is rewound to just
    /// before it and the later plays still down are re-applied in order, so the action or free action comes back, an
    /// extra action it granted goes, and every later play is re-scored. After a wheel spin or a clear, that play's own
    /// points are taken off and its action refunded.</summary>
    bool LiftUndo(PadCard pc, int pad)
    {
        if (S.Phase != Phase.Play || !pc.Valid || pc.TurnPlayed != S.TurnNo || pc.Owner != S.Current || S.Wheel == Wheel.Spinning) return false;
        int k = _plays.FindLastIndex(r => r.Src == CardSource.Board && r.Pad == pad && r.Card.Cid == pc.Card.Cid);
        if (k < 0) return false;
        var rec = _plays[k];
        var side = S.Current; int before = S.Score[side];
        if (_plays.Skip(k).All(r => r.Phys == _phys))
        {
            var fouls = Enumerable.Range(0, Rules.Slots).SelectMany(i => T.Pads[i].Where(x => !x.Valid).Select(x => (i, x))).ToList();
            var later = _plays.Skip(k + 1).ToList();
            _plays.RemoveRange(k, _plays.Count - k);
            T.RestoreState(rec.Table); S = rec.Before.Clone();
            _quiet = true;
            try { foreach (var r in later) Replay(r); }
            finally { _quiet = false; }
            foreach (var (i, x) in fouls) if (!T.Pads[i].Contains(x)) T.Pads[i].Add(x);
        }
        else
        {
            T.Pads[pad].Remove(pc);
            foreach (var (sd, d) in rec.Delta) if (d != 0) S.Score[sd] = Math.Max(0, S.Score[sd] - d);
            if (rec.UsedFree) { S.FreeFilter = rec.Before.FreeFilter; S.FreeText = rec.Before.FreeText; }
            else S.ActionsLeft = Math.Min(ActionsPerTurn, S.ActionsLeft + 1);
            if (rec.Granted != null && ReferenceEquals(S.FreeFilter, rec.Granted)) { S.FreeFilter = null; S.FreeText = null; }
            if (rec.GrantedEq) S.FreeEq = false;
            _plays.RemoveAt(k);
            _phys++;
        }
        S.TurnEndAt = null;
        int diff = S.Score[side] - before;
        if (diff != 0) _events.Add(new ScoreEvent(NextId(), side, diff, "Undo", pad, "card lifted"));
        if (diff < 0) Emit("keysfx:score_loss");
        Notice($"{pc.Card.Name} lifted — action given back.");
        return true;
    }

    void Replay(PlayRec r)
    {
        if (r.Src == CardSource.Board)
        {
            if (Check(r.Card, r.Pad) is var (title, _))
                T.Pads[r.Pad].Add(new PadCard { Card = r.Card, Source = CardSource.Board, Valid = false, Owner = S.Current, TurnPlayed = S.TurnNo, Error = title });
            else PlayRecorded(r.Card, r.Pad, null, CardSource.Board);
        }
        else if (T.Players.FirstOrDefault(x => x.Name == r.Player) is Player p && p.Hand.Contains(r.Card) && Validity(r.Card, r.Pad) == null)
            PlayRecorded(r.Card, r.Pad, p, CardSource.Player);
        // a phone play that's no longer legal just stays in that player's (restored) hand
    }

    // ======================================================================================
    // undo (§3.1): take back the last card you played this turn
    // ======================================================================================

    public override ActionResult Undo(Player p)
    {
        if (S.Phase != Phase.Play || p.Team != S.Current) return ActionResult.Fail("You can only undo on your turn.");
        if (S.Wheel != Wheel.Idle) return ActionResult.Fail("Wait for the wheel.");
        if (_plays.Count == 0 || _plays[^1].Player != p.Name || _plays[^1].Phys != _phys) return ActionResult.Fail("Nothing of yours to take back this turn.");
        var rec = _plays[^1]; _plays.RemoveAt(_plays.Count - 1);
        var fouls = Enumerable.Range(0, Rules.Slots).SelectMany(i => T.Pads[i].Where(x => !x.Valid).Select(x => (i, x))).ToList();
        T.RestoreState(rec.Table); S = rec.Before.Clone();
        foreach (var (i, x) in fouls) if (!T.Pads[i].Contains(x)) T.Pads[i].Add(x);
        Notice($"{p.Name} took back a card.");
        return ActionResult.Success;
    }

    // ======================================================================================
    // time: wheel, clears, timer, end of turn (§3.1, §3.3, §5.2)
    // ======================================================================================

    public override bool Tick(DateTime now)
    {
        double dt = (now - _lastTick).TotalSeconds; _lastTick = now;
        if (S.Phase != Phase.Play) return false;
        bool changed = false;

        if (S.Wheel == Wheel.Spinning && now - S.WheelAt >= S.WheelDur) { LandWheel(); changed = true; }
        else if (S.Wheel == Wheel.Landed && now - S.WheelAt >= WheelShow) { S.Wheel = Wheel.Idle; changed = true; }

        // virtual cards marked for clearing leave after a short reveal; owner loses a point each
        for (int i = 0; i < Rules.Slots; i++)
            if (T.TopOf(i) is { Silenced: true, Source: CardSource.Player } pc && now - pc.SilencedAt >= ClearReveal)
            {
                T.PopTop(i); _phys++;
                Award(pc.SilencedOwner, -R.CardRemovalPenalty, "Clear", i, "cleared");
                changed = true;
            }
        if (changed) CheckWin();
        if (S.Phase != Phase.Play) return true;

        bool paused = S.Wheel != Wheel.Idle || AnySilenced() || AnyFoul();
        if (TurnLimit is double lim && !paused)
        {
            int before = (int)S.Elapsed; double was = S.Elapsed; S.Elapsed += dt;
            if (was < lim - 1.5 && S.Elapsed >= lim - 1.5) Emit("keysfx:request_warning");
            if ((int)S.Elapsed != before) changed = true;
            if (S.Elapsed >= lim) { Notice("Time's up!"); EndTurn(); return true; }
        }

        if (S.Winner == Side.None && now >= _scoreQueueUntil) { CheckWin(); if (S.Phase != Phase.Play) return true; }
        bool done = S.ActionsLeft == 0 && S.FreeFilter == null && !S.FreeEq && !paused && S.Search == null && now >= _scoreQueueUntil;
        if (!done && S.TurnEndAt != null) { S.TurnEndAt = null; changed = true; }     // a foul or a lift stopped it
        if (done)
        {
            if (S.TurnEndAt == null) { S.TurnEndAt = now + AutoTurnEnd; changed = true; }
            else if (now >= S.TurnEndAt) { EndTurn(); return true; }
        }
        return changed;
    }

    void EndTurn()
    {
        S.FirstTurnEnded = true;
        var side = S.Current;
        foreach (var pl in TeamOf(side))
        {
            int room = MaxHand(side) - pl.Hand.Count;
            if (room > 0) T.DrawN(pl, Math.Min(DrawSize(side), room));
        }
        S.Current = Rules.Other(side);
        S.ActionsLeft = ActionsPerTurn;
        S.FreeFilter = null; S.FreeText = null; S.FreeEq = false; S.FreeFx = null; S.Search = null;
        Emit("turnend");                                                  // UntilTurnEnd music effects stop
        S.Elapsed = 0; S.TurnEndAt = null; S.TurnNo++;
        foreach (var sd in new[] { Side.Blue, Side.Red }) _turnStartScore[sd] = S.Score[sd];
        _turnChangedAt = DateTime.UtcNow;
        _plays.Clear();
        SnapshotTurnStart();
        Notice($"{S.Current}'s turn.");
    }

    void SnapshotTurnStart()
    {
        for (int i = 0; i < Rules.Slots; i++)
        { var pc = T.TopValid(i); S.StartOwner[i] = pc?.Owner ?? Side.None; S.StartCid[i] = pc?.Card.Cid; }
    }

    void Notice(string text, string? to = null) => _notice = (NextId(), text, to);

    // ======================================================================================
    // view
    // ======================================================================================

    // ---- board lights (the original's board lights, docs/BOARD.md) ----
    DateTime _startedAt = DateTime.MinValue, _turnChangedAt = DateTime.MinValue;
    static readonly TimeSpan CoinSpin = TimeSpan.FromSeconds(2.4),            // the table's FIRST UP coin animation
                             ClashStartHold = TimeSpan.FromSeconds(4.0),      // 5 light frames
                             TurnChangeHold = TimeSpan.FromSeconds(2.4);      // 3 light frames

    public override LightCue Lights
    {
        get
        {
            var now = DateTime.UtcNow;
            if (S.Phase == Phase.Over) return new LightCue(LightScene.Victory, S.Winner);
            if (S.Phase == Phase.Lobby)
                return new LightCue(LightScene.RegistrationClash, Side.None,
                    Enumerable.Range(0, Rules.Slots).Select(i => i <= 1 ? TeamOf(Side.Red).Count > 0 || _lobbyCards[i].Count > 0 : i >= 3 && (TeamOf(Side.Blue).Count > 0 || _lobbyCards[i].Count > 0)).ToArray());
            if (S.Phase != Phase.Play) return LightCue.Normal;
            if (AnyFoul())                                                       // ErrorDisplay: every slot blinks, red where in error
                return new LightCue(LightScene.ErrorBlink, S.Current, Enumerable.Range(0, Rules.Slots).Select(i => T.TopOf(i) is { Valid: false, Error: not null }).ToArray());
            if (S.Wheel == Wheel.Spinning)
            {   // Wheel of Misfortune: the opponent's cards are the targets
                var target = Rules.Other(S.WheelSide);
                return new LightCue(LightScene.Spinner, target, Vulnerable(target), S.WheelSpinId);
            }
            if (now - _startedAt < CoinSpin)                                    // coin flip spin: Side None
                return new LightCue(LightScene.Spinner, Side.None, Vulnerable(Rules.Other(S.Current)));
            if (now - _startedAt < CoinSpin + ClashStartHold) return new LightCue(LightScene.ClashStart, S.Current);
            if (now - _turnChangedAt < TurnChangeHold) return new LightCue(LightScene.TurnChange, S.Current, Serial: S.TurnNo);
            return LightCue.Normal;
        }
    }

    public override (bool InGame, double Progress) Crowd =>
        S.Phase == Phase.Lobby ? (false, 0) : (true, S.Phase == Phase.Over ? 1 : Math.Max(S.Score[Side.Red], S.Score[Side.Blue]) / (double)WinningScore);

    bool[] Vulnerable(Side target) =>
        Enumerable.Range(0, Rules.Slots).Select(i => T.TopValid(i) is PadCard pc && pc.Owner == target).ToArray();

    public override void Resumed(TimeSpan d) { _lastTick += d; S.WheelAt += d; if (S.TurnEndAt is DateTime t) S.TurnEndAt = t + d; }

    public override object View(Player? viewer)
    {
        bool myTurn = viewer != null && S.Phase == Phase.Play && viewer.Team == S.Current && S.Wheel == Wheel.Idle && !AnySilenced();
        double? left = TurnLimit is double lim ? Math.Max(0, lim - S.Elapsed) : null;
        object TeamView(Side s) => new { score = S.Score[s], players = TeamOf(s).Select(p => p.Name).ToArray() };
        string phase = S.Phase.ToString().ToLowerInvariant();
        string wait = S.Phase switch
        {
            Phase.Lobby => "Waiting for the game to start…",
            Phase.Over => $"{S.Winner} wins!",
            _ when viewer?.Team == Side.None => "You're watching this game.",
            _ when S.Wheel != Wheel.Idle => "The wheel is spinning…",
            _ when AnySilenced() => "Clearing cards…",
            _ when AnyFoul() => "Return the flashing card to your hand",
            _ => $"{S.Current}'s turn",
        };
        return new
        {
            id = Id, title = Title,
            subtitle = S.Phase == Phase.Lobby ? "Pick a team" : $"First to {WinningScore}",
            phase,
            slots = SlotViews(),
            legal = LegalMap(viewer, (c, i) => Validity(c, i, ignoreActions: true) == null),
            canPlay = myTurn && (S.ActionsLeft > 0 || S.FreeFilter != null),
            waitText = myTurn ? (S.ActionsLeft == 0 && S.FreeFilter == null ? "Turn ending…" : null) : wait,
            allowDraw = false, allowClear = false,
            allowUndo = viewer != null && _plays.Count > 0 && _plays[^1].Player == viewer.Name && _plays[^1].Phys == _phys,
            // ErrorDisplay: per side, the slot(s) in error and the text (the Error_ReturnToHand text goes under it)
            errors = Enumerable.Range(0, Rules.Slots).Where(i => T.TopOf(i) is { Valid: false, Error: not null })
                .Select(i => new { slot = i, side = T.TopOf(i)!.Owner.ToString(), text = T.TopOf(i)!.Error }).ToArray(),
            you = viewer == null ? null : new { team = viewer.Team.ToString() },
            current = S.Current.ToString(),
            icons = new { red = S.Icon.GetValueOrDefault(Side.Red), blue = S.Icon.GetValueOrDefault(Side.Blue) },
            deckLeft = viewer?.OwnDeck?.Count,
            search = viewer != null && S.Search == viewer.Name && viewer.OwnDeck != null
                ? viewer.OwnDeck.OrderBy(c => c.Type).ThenBy(c => c.Name).Select(c => new { cid = c.Cid, name = c.Name, artist = c.Artist, type = c.Type.ToString(), power = c.Power, image = c.ImageName }).ToArray() : null,
            actionsLeft = S.ActionsLeft,
            free = S.FreeText, freeSpin = S.FreeEq,
            target = WinningScore, timerLimit = TurnLimit, timerLeft = left,
            turnEnding = S.TurnEndAt != null,
            blue = TeamView(Side.Blue), red = TeamView(Side.Red),
            winner = S.Winner == Side.None ? null : S.Winner.ToString(),
            firstTurnEnded = S.FirstTurnEnded,
            wheel = new { state = S.Wheel.ToString().ToLowerInvariant(), result = S.Wheel == Wheel.Landed ? (S.WheelResult == 0 ? "X" : S.WheelResult.ToString()) : null,
                          spin = S.WheelSpinId, bonus = S.WheelBonus, side = S.WheelSide.ToString(), resultValue = S.WheelResult, ms = (int)S.WheelDur.TotalMilliseconds,
                          downMs = (int)S.WheelDown.TotalMilliseconds, bpm = S.WheelBpm, elapsedMs = (int)(DateTime.UtcNow - S.WheelAt).TotalMilliseconds },
            buttonArmed = S.Phase == Phase.Play && S.Wheel == Wheel.Idle && (S.ActionsLeft > 0 || S.FreeEq) && !AnySilenced(),
            events = _events.TakeLast(12).ToArray(),
            notice = new { id = _notice.id, text = _notice.text },
        };
    }
}
