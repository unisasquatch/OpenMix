using System.Security.Cryptography;
using OpenMix.Cards;

namespace OpenMix.Game;

public enum CardSource { Board, Player }

/// <summary>A card sitting in a slot.</summary>
public sealed class PadCard
{
    public required Card Card { get; init; }
    public CardSource Source { get; init; }
    public string? PlayedBy { get; init; }
    public Side Owner { get; set; }            // side whose turn it was when played (Clash)
    public bool Valid { get; set; } = true;    // invalid plays (e.g. physical card in wrong slot) don't count
    public int TurnPlayed { get; set; }
    public bool Silenced { get; set; }         // marked for clearing (wheel / FX remove)
    public Side SilencedOwner { get; set; }
    public string? Error { get; set; }         // Clash ErrorDisplay text for a foul card (Error_WrongColor, ...)
    public DateTime SilencedAt { get; set; }
    public PadCard Clone() => (PadCard)MemberwiseClone();
}

public enum PlayerStatus { Connected, Orphaned }

public sealed class Player
{
    public required string Name { get; init; }
    public List<Card> Hand { get; } = new();
    public Side Team { get; set; }
    /// <summary>This player's own virtual deck (Clash: built within the deck rules); null = draw from the shared pile.</summary>
    public List<Card>? OwnDeck { get; set; }
    internal string Token = "";
    internal int Connections;
    internal DateTime DisconnectedAt = DateTime.MinValue;
    public PlayerStatus Status { get; internal set; } = PlayerStatus.Connected;
}

public enum JoinOutcome { Created, Resumed, Reclaimed, NameInUse, InvalidName }
public sealed record JoinResult(JoinOutcome Outcome, string? Token, string? Name);
public sealed record ActionResult(bool Ok, string? Error = null)
{
    public static readonly ActionResult Success = new(true);
    public static ActionResult Fail(string why) => new(false, why);
}

/// <summary>
/// The shared table: 5 slots, one shared virtual deck, named players with hands, and the active
/// <see cref="GameMode"/> that decides the rules. Thread-safe; modes run under the table lock.
///
/// Name rules: a player is identified by name and keeps their hand. While a device holds the
/// name (an open stream), nobody else can take it. After the last connection drops, the name
/// becomes Orphaned after <see cref="OrphanGrace"/>, and any device can claim it (new token; the
/// old device's token dies).
/// </summary>
public sealed class Table
{
    public const int PadCount = Rules.Slots;
    /// <summary>The music clock (master beat, BPM), set by the host when audio runs; modes use it to time
    /// beat-locked things like the wheel. Null without audio.</summary>
    public Func<(double Beat, double Bpm)>? BeatClock { get; set; }
    /// <summary>Cards physically on the board (host-provided; null without a board).</summary>
    public Func<int>? BoardCardCount { get; set; }
    public bool BoardHasCards => (BoardCardCount?.Invoke() ?? 0) > 0;
    /// <summary>A game start was refused because cards are on the board (GameFlow.DoRestart → kRemoveCardsToStart).</summary>
    public bool RemoveCardsPrompt { get; set; }

    internal readonly object Lock = new();
    internal readonly List<PadCard>[] Pads = Enumerable.Range(0, PadCount).Select(_ => new List<PadCard>()).ToArray();
    internal readonly List<Card> Deck = new();
    internal readonly List<Card> DiscardPile = new();
    internal readonly Dictionary<string, Player> PlayersByName = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Random Rng;
    readonly List<Card> _allCards;
    readonly Card?[] _lastTops = new Card?[PadCount];
    readonly bool[] _lastSilenced = new bool[PadCount];

    public TimeSpan OrphanGrace { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>The table screen's Pause popup: mode timers are held while set.</summary>
    public bool Paused { get; set; }
    DateTime? _pausedAt;
    /// <summary>Current mix tempo, fed by the host's music clock (Lanes spawns pulse on eighth notes).</summary>
    public double MusicBpm { get; set; } = 120;
    public long Version { get; private set; }
    public GameMode Mode { get; private set; }

    /// <summary>Raised (outside the lock) after any state change.</summary>
    public event Action? Changed;
    /// <summary>Raised (outside the lock) when the card that should SOUND in a slot changes.</summary>
    public event Action<int, Card?>? PadChanged;
    /// <summary>Raised (outside the lock) for mix-wide music commands from the mode (e.g. "freestyle_button").</summary>
    public event Action<string>? Music;
    readonly List<string> _music = new();
    internal void EmitMusic(string cmd) => _music.Add(cmd);

    public Table(IEnumerable<Card> allCards, int? seed = null)
    {
        Rng = seed is int s ? new Random(s) : new Random();
        _allCards = allCards.Where(c => !string.IsNullOrEmpty(c.AudioId) || c.Type == CardType.Fx).ToList();
        Deck.AddRange(_allCards); Shuffle(Deck);
        Mode = new FreestyleMode();
        Mode.Attach(this);
        Mode.Start();
    }

    public IReadOnlyList<Card> AllCards => _allCards;

    // ---------------- decks ----------------

    HashSet<string>? _activeDecks;   // null = every deck
    List<Card>? _activeCards;

    /// <summary>The virtual-card pool: cards from the active decks (physical board cards are always accepted).</summary>
    public IReadOnlyList<Card> ActiveCards => _activeCards ?? _allCards;
    public bool IsDeckActive(string icon) => _activeDecks == null || _activeDecks.Contains(icon);
    public IReadOnlyCollection<string>? ActiveDecks { get { lock (Lock) return _activeDecks?.ToArray(); } }

    /// <summary>Choose which decks (series icons) the virtual deck draws from; null = all. Takes effect
    /// at once: the draw pile is rebuilt from the new pool, minus cards already in hands or on slots.</summary>
    public ActionResult SetActiveDecks(IEnumerable<string>? icons)
    {
        HashSet<string>? set = icons == null ? null : new HashSet<string>(icons, StringComparer.OrdinalIgnoreCase);
        var pool = set == null ? _allCards : _allCards.Where(c => set.Contains(c.Icon)).ToList();
        if (pool.Count < 15) return ActionResult.Fail("Pick at least one full deck (15 cards).");
        Run(() =>
        {
            _activeDecks = set; _activeCards = set == null ? null : pool;
            var inPlay = new HashSet<Card>(PlayersByName.Values.SelectMany(p => p.Hand).Concat(Pads.SelectMany(s => s.Select(pc => pc.Card))));
            Deck.Clear(); DiscardPile.Clear();
            Deck.AddRange(pool.Where(c => !inPlay.Contains(c))); Shuffle(Deck);
            Mode.DecksChanged();
        });
        return ActionResult.Success;
    }

    // ---------------- modes ----------------

    public static readonly string[] ModeNames = { "freestyle", "clash", "lanes", "party", "mixgrid" };

    public ActionResult SetMode(string name, IDictionary<string, string>? options = null)
    {
        GameMode m = name.ToLowerInvariant() switch
        {
            "freestyle" => new FreestyleMode(),
            "clash" => new ClashMode(),
            "lanes" or "puzzle" => new LanesMode(),
            "party" => new PartyMode(),
            "mixgrid" => new MixGridMode(),
            _ => null!,
        };
        if (m == null) return ActionResult.Fail($"Unknown mode '{name}'.");
        return SetMode(m, options);
    }

    /// <summary>Switch to a mode built by the caller (e.g. mix playback, which carries its cards).</summary>
    public ActionResult SetMode(GameMode m, IDictionary<string, string>? options = null)
    {
        Run(() =>
        {
            ResetTableUnlocked();
            Paused = false; _pausedAt = null;
            Mode = m;
            m.Attach(this);
            m.Configure(options ?? new Dictionary<string, string>());
            m.Start();
        });
        return ActionResult.Success;
    }

    /// <summary>Restart the current mode (same options).</summary>
    public void Restart() => Run(() => { ResetTableUnlocked(); Mode.Start(); });

    void ResetTableUnlocked()
    {
        foreach (var p in Pads) p.Clear();
        foreach (var pl in PlayersByName.Values) { pl.Hand.Clear(); pl.OwnDeck = null; }
        Deck.Clear(); DiscardPile.Clear();
        Deck.AddRange(ActiveCards); Shuffle(Deck);
    }

    // ---------------- players ----------------

    public JoinResult Join(string rawName, string? token)
    {
        string name = (rawName ?? "").Trim();
        if (name.Length is 0 or > 24) return new(JoinOutcome.InvalidName, null, null);
        JoinResult result = null!;
        Run(() =>
        {
            if (PlayersByName.TryGetValue(name, out var p))
            {
                RefreshOrphan(p);
                if (token != null && token == p.Token) result = new(JoinOutcome.Resumed, p.Token, p.Name);
                else if (p.Status == PlayerStatus.Orphaned)
                {
                    p.Token = NewToken(); p.Status = PlayerStatus.Connected; p.DisconnectedAt = DateTime.MinValue;
                    result = new(JoinOutcome.Reclaimed, p.Token, p.Name);
                }
                else result = new(JoinOutcome.NameInUse, null, p.Name);
            }
            else
            {
                p = new Player { Name = name, Token = NewToken(), DisconnectedAt = DateTime.UtcNow };
                PlayersByName[name] = p;
                Mode.OnPlayerJoined(p);
                result = new(JoinOutcome.Created, p.Token, p.Name);
            }
        });
        return result;
    }

    public Player? ByToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        lock (Lock) return PlayersByName.Values.FirstOrDefault(p => p.Token == token);
    }
    internal Player? ByTokenUnlocked(string? token) =>
        string.IsNullOrEmpty(token) ? null : PlayersByName.Values.FirstOrDefault(p => p.Token == token);

    public bool Connect(string token)
    {
        bool ok = false;
        Run(() =>
        {
            var p = ByTokenUnlocked(token); if (p == null) return;
            p.Connections++; p.Status = PlayerStatus.Connected; p.DisconnectedAt = DateTime.MinValue; ok = true;
        });
        return ok;
    }

    public void Disconnect(string token) => Run(() =>
    {
        var p = ByTokenUnlocked(token); if (p == null) return;
        p.Connections = Math.Max(0, p.Connections - 1);
        if (p.Connections == 0) p.DisconnectedAt = DateTime.UtcNow;
    });

    /// <summary>Periodic: orphan timeouts and mode timers. Call ~10x a second.</summary>
    public void Tick()
    {
        bool changed = false;
        lock (Lock)
        {
            foreach (var p in PlayersByName.Values)
            {
                var before = p.Status; RefreshOrphan(p);
                if (p.Status != before) changed = true;
            }
            if (Paused) { _pausedAt ??= DateTime.UtcNow; }
            else
            {
                if (_pausedAt is DateTime pa) { Mode.Resumed(DateTime.UtcNow - pa); _pausedAt = null; }
                if (Mode.Tick(DateTime.UtcNow)) changed = true;
            }
        }
        if (changed) Commit(true);
    }

    void RefreshOrphan(Player p)
    {
        if (p.Connections == 0 && p.DisconnectedAt != DateTime.MinValue && DateTime.UtcNow - p.DisconnectedAt >= OrphanGrace)
            p.Status = PlayerStatus.Orphaned;
    }

    public bool RemovePlayer(string name)
    {
        bool ok = false;
        Run(() =>
        {
            if (!PlayersByName.Remove(name, out var p)) return;
            DiscardPile.AddRange(p.Hand); p.Hand.Clear(); ok = true;
            Mode.OnPlayerLeft(p);
        });
        return ok;
    }

    public ActionResult SetTeam(string token, Side side) => Act(token, p => Mode.ChooseTeam(p, side));

    // ---------------- actions (delegated to the mode) ----------------

    public ActionResult Play(string token, string cid, int pad) => Act(token, p =>
    {
        if (pad < 0 || pad >= PadCount) return ActionResult.Fail("No such slot.");
        var card = p.Hand.FirstOrDefault(c => string.Equals(c.Cid, cid, StringComparison.OrdinalIgnoreCase));
        if (card == null) return ActionResult.Fail("That card isn't in your hand.");
        return Mode.Play(p, card, pad);
    });

    public ActionResult Draw(string token, int count = 1) => Act(token, p => Mode.Draw(p, count));
    public ActionResult ClearPad(string token, int pad) => Act(token, p => Mode.Clear(p, pad));
    public ActionResult Undo(string token) => Act(token, p => Mode.Undo(p));
    public ActionResult ModeAction(string token, string action, string? arg) => Act(token, p => Mode.Action(p, action, arg));

    /// <summary>The DropMix button: physical board (player null) or a phone/table press.</summary>
    public ActionResult Button(string? token)
    {
        ActionResult r = ActionResult.Success;
        Run(() => r = Mode.Button(ByTokenUnlocked(token)));
        return r;
    }

    /// <summary>Host-screen controls (start/options) — not tied to a player.</summary>
    public ActionResult HostAction(string action, IDictionary<string, string>? args)
    {
        ActionResult r = ActionResult.Success;
        Run(() => r = Mode.HostAction(action, args ?? new Dictionary<string, string>()));
        return r;
    }

    /// <summary>Physical board: a card was placed on / lifted off a slot.</summary>
    public void BoardCard(int pad, Card card, bool added)
    {
        if (pad < 0 || pad >= PadCount) return;
        // the app ignores the board's card reports outside play (setup screens); lifts always apply
        // a card the database doesn't know (placeholder Cid "unknown_<cti>") only matters to modes that report it
        if (added && card.Cid.StartsWith("unknown_") && !Mode.AcceptsUnknownCards) return;
        Run(() =>
        {
            if (added) { if (Mode.AcceptsBoardCards) Mode.BoardPlaced(card, pad); }
            else { Mode.BoardLifted(card, pad); if (Mode.Id is not ("freestyle" or "mixplay")) EmitMusic("card_removed"); }   // SFXCard/card_removed
        });
    }

    ActionResult Act(string token, Func<Player, ActionResult> f)
    {
        ActionResult r = ActionResult.Fail("Your session ended — join again.");
        Run(() => { var p = ByTokenUnlocked(token); if (p != null) r = f(p); });
        return r;
    }

    void Run(Action a)
    {
        lock (Lock) a();
        Commit(true);
    }

    /// <summary>Bump version, notify, and raise PadChanged for slots whose sounding card changed.</summary>
    void Commit(bool changed)
    {
        var padEvents = new List<(int, Card?)>();
        string[] music;
        lock (Lock)
        {
            music = _music.ToArray(); _music.Clear();
            if (changed) Version++;
            for (int i = 0; i < PadCount; i++)
            {
                var now = SoundingTop(i);
                bool sil = TopValid(i)?.Silenced ?? false;
                if (!ReferenceEquals(now, _lastTops[i]) || sil != _lastSilenced[i])
                {
                    // an FX card entering: its music effect's slot DSP goes to the slots its ability targets
                    if (!ReferenceEquals(now, _lastTops[i]) && now is { Type: CardType.Fx } fx && fx.MusicEffect.Length > 0
                        && Rules.FxMusicTargets(fx, i, TopValid) is { Length: > 0 } targets)
                        music = music.Append($"fxdsp:{fx.MusicEffect}:{i}:{fx.Cid}:{string.Join(",", targets)}").ToArray();
                    _lastTops[i] = now; _lastSilenced[i] = sil; padEvents.Add((i, now));
                }
            }
        }
        if (changed) Changed?.Invoke();
        foreach (var (pad, c) in padEvents) PadChanged?.Invoke(pad, c);
        foreach (var m in music) Music?.Invoke(m);
    }

    /// <summary>Bump the version so screens refresh (e.g. the mix tempo/key changed).</summary>
    public void Touch() => Commit(true);

    /// <summary>The card in a slot and the audio part it should play there (Wilds play the slot's colour).</summary>
    public (Card? card, string?[] parts, bool silenced) Sounding(int pad)
    {
        lock (Lock)
        {
            var top = TopValid(pad);
            return (top?.Card, top == null ? Array.Empty<string?>() : Rules.AudioParts(top.Card, pad), top?.Silenced ?? false);
        }
    }

    /// <summary>The card that should be heard in a slot: the top valid card.</summary>
    internal Card? SoundingTop(int pad)
    {
        var s = Pads[pad];
        for (int i = s.Count - 1; i >= 0; i--) if (s[i].Valid) return s[i].Card;
        return null;
    }

    public Card? Top(int pad) { lock (Lock) return SoundingTop(pad); }
    public LightCue Lights()
    {
        lock (Lock)
        {
            if (RemoveCardsPrompt) { if (BoardHasCards) return new LightCue(LightScene.RemoveAll); RemoveCardsPrompt = false; }
            return Mode.Lights;
        }
    }
    public (bool InGame, double Progress) Crowd() { lock (Lock) return Mode.Crowd; }
    /// <summary>Per slot for the board lights: sounding card, its owner (Clash side), silenced.</summary>
    public (Card? Card, Side Owner, bool Silenced)[] LightSlots()
    {
        lock (Lock)
            return Enumerable.Range(0, PadCount).Select(i =>
            {
                var pc = TopValid(i); var c = SoundingTop(i);
                return (c, pc?.Owner ?? Side.None, pc?.Silenced ?? false);
            }).ToArray();
    }

    // ---------------- helpers for modes (call under lock) ----------------

    internal PadCard? TopOf(int pad) => Pads[pad].Count > 0 ? Pads[pad][^1] : null;
    internal PadCard? TopValid(int pad) { var s = Pads[pad]; for (int i = s.Count - 1; i >= 0; i--) if (s[i].Valid) return s[i]; return null; }
    internal IEnumerable<Player> Players => PlayersByName.Values;

    internal void DrawTo(Player p, int target)
    {
        while (p.Hand.Count < target && Draw(p, out var c)) p.Hand.Add(c);
    }
    internal int DrawN(Player p, int n) { int k = 0; for (; k < n && Draw(p, out var c); k++) p.Hand.Add(c); return k; }
    bool Draw(Player p, out Card card)
    {
        if (p.OwnDeck is { } d) { card = null!; if (d.Count == 0) return false; card = d[^1]; d.RemoveAt(d.Count - 1); return true; }
        return DrawOne(out card);
    }
    bool DrawOne(out Card card)
    {
        card = null!;
        if (Deck.Count == 0)
        {
            if (DiscardPile.Count == 0) return false;
            Deck.AddRange(DiscardPile); DiscardPile.Clear(); Shuffle(Deck);   // cleared cards are reshuffled
        }
        card = Deck[^1]; Deck.RemoveAt(Deck.Count - 1); return true;
    }
    internal void Discard(Card c) => DiscardPile.Add(c);
    internal void ReturnToDeck(IEnumerable<Card> cards) { Deck.AddRange(cards); Shuffle(Deck); }

    /// <summary>Remove a slot's top card (to the discard pile if it was a virtual card).</summary>
    internal PadCard? PopTop(int pad)
    {
        var s = Pads[pad]; if (s.Count == 0) return null;
        var top = s[^1]; s.RemoveAt(s.Count - 1);
        if (top.Source == CardSource.Player) DiscardPile.Add(top.Card);
        return top;
    }

    // Undo snapshots (table part)
    internal sealed record TableSnap(List<PadCard>[] Pads, Dictionary<string, List<Card>> Hands, List<Card> Deck, List<Card> Discard);
    internal TableSnap SaveState() => new(
        Pads.Select(s => s.Select(pc => pc.Clone()).ToList()).ToArray(),
        PlayersByName.Values.ToDictionary(p => p.Name, p => p.Hand.ToList(), StringComparer.OrdinalIgnoreCase),
        Deck.ToList(), DiscardPile.ToList());
    internal void RestoreState(TableSnap s)
    {
        for (int i = 0; i < PadCount; i++) { Pads[i].Clear(); Pads[i].AddRange(s.Pads[i]); }
        foreach (var p in PlayersByName.Values)
        { p.Hand.Clear(); if (s.Hands.TryGetValue(p.Name, out var h)) p.Hand.AddRange(h); }
        Deck.Clear(); Deck.AddRange(s.Deck); DiscardPile.Clear(); DiscardPile.AddRange(s.Discard);
    }

    internal void Shuffle(List<Card> l)
    {
        for (int i = l.Count - 1; i > 0; i--) { int j = Rng.Next(i + 1); (l[i], l[j]) = (l[j], l[i]); }
    }

    // ---------------- snapshots for clients ----------------

    public sealed record PadView(int Pad, PadCard? Top, int StackCount);
    public sealed record PlayerView(string Name, PlayerStatus Status, int HandCount, Side Team);
    public sealed record Snapshot(long Version, PadView[] Pads, PlayerView[] Players, int DeckCount, Card[]? Hand, string? You, object Mode);

    public Snapshot Snap(string? token = null)
    {
        lock (Lock)
        {
            var me = ByTokenUnlocked(token);
            return new Snapshot(
                Version,
                Enumerable.Range(0, PadCount).Select(i => new PadView(i, TopOf(i), Pads[i].Count)).ToArray(),
                PlayersByName.Values.OrderBy(p => p.Name).Select(p => new PlayerView(p.Name, p.Status, p.Hand.Count, p.Team)).ToArray(),
                Deck.Count,
                me?.Hand.ToArray(),
                me?.Name,
                Mode.View(me));
        }
    }

    static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
