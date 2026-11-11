using OpenMix.Cards;

namespace OpenMix.Game;

/// <summary>A score change for the UI to pop on a slot (kinds mirror SlotScoreIndicator).</summary>
public sealed record ScoreEvent(long Id, Side Side, int Points, string Kind, int Slot, string? Note);

/// <summary>
/// Rules for one game mode. All hooks run under the table lock; return true from Tick when
/// state changed. The table handles mechanics (slots, deck, hands, players, undo snapshots).
/// </summary>
/// <summary>What the physical board's lights should show (the original's board lights modes, docs/BOARD.md).</summary>
/// <summary>the original's board lights modes. Registration* = DeckRegistrationHeadToHead/Party/Lanes (4/5/12), GameStarting 6,
/// Overflow = OverflowLanes 13, ErrorBlink = ErrorDisplay showing, CardRegistration 14 (Menu_DeckVerification).</summary>
public enum LightScene { Normal, ClashStart, TurnChange, Spinner, Victory, RemoveAll, RegistrationClash, RegistrationParty, RegistrationLanes, GameStarting, Overflow, ErrorBlink, CardRegistration }

/// <param name="Side">the side the scene is about (turn, winner)</param>
/// <param name="Vulnerable">Spinner: slots the wheel can hit</param>
/// <param name="Serial">bumps on each new occurrence (e.g. every turn change) so a repeat restarts the scene</param>
public sealed record LightCue(LightScene Scene, Side Side = Side.None, bool[]? Vulnerable = null, long Serial = 0, bool[]? Owned = null)
{
    public static readonly LightCue Normal = new(LightScene.Normal);
}

public abstract class GameMode
{
    // Ids the screens de-duplicate on (score events, requests, spins, notices). They must never repeat across
    // mode instances or server restarts: a table page remembers the last id it showed, and per-instance
    // counters restarting at 1 made pops, toasts and spins after a mode switch look "already seen".
    static long s_nextId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    protected static long NextId() => Interlocked.Increment(ref s_nextId);

    protected Table T = null!;
    internal void Attach(Table t) => T = t;

    public abstract string Id { get; }
    public abstract string Title { get; }

    public virtual void Configure(IDictionary<string, string> options) { }
    public virtual void Start() { }
    public virtual void OnPlayerJoined(Player p) { }
    public virtual void OnPlayerLeft(Player p) { }
    public virtual bool Tick(DateTime now) => false;
    /// <summary>The table was paused for <paramref name="d"/>; modes that time things by wall clock shift their timestamps.</summary>
    public virtual void Resumed(TimeSpan d) { }
        /// <summary>The active decks changed (the table already rebuilt its draw pile).</summary>
    public virtual void DecksChanged() { }
    /// <summary>Board-light scene for the current moment; Normal unless the mode says otherwise.</summary>
    public virtual LightCue Lights => LightCue.Normal;
    /// <summary>False in setup/lobby phases: board placements are ignored there (BluetoothBoardReader ignore-board).</summary>
    public virtual bool AcceptsBoardCards => true;
    /// <summary>Physical cards the database doesn't know (Clash shows CARD NOT RECOGNIZED; others ignore them).</summary>
    public virtual bool AcceptsUnknownCards => false;

    /// <summary>GameFlow.DoRestart: a game can't start while cards are on the board. Returns true (and raises the
    /// IconSelect_ReadyToStart prompt + red lights) when the start must be refused.</summary>
    protected bool BoardMustBeCleared()
    {
        // The lobby join decks (one per seated board player) are allowed to stay on the board at start.
        int extra = (T.BoardCardCount?.Invoke() ?? 0) - T.SeatedPads;
        if (extra <= 0) { T.RemoveCardsPrompt = false; return false; }
        T.RemoveCardsPrompt = true;
        return true;
    }
    /// <summary>For the crowd (the original's audio): is a game running, and progress 0..1 toward winning.</summary>
    public virtual (bool InGame, double Progress) Crowd => (false, 0);

    public virtual ActionResult ChooseTeam(Player p, Side side) => ActionResult.Fail("This mode has no teams.");
    public abstract ActionResult Play(Player p, Card card, int pad);
    public virtual ActionResult Draw(Player p, int count) => ActionResult.Fail("You can't draw now.");
    public virtual ActionResult Clear(Player p, int pad) => ActionResult.Fail("You can't clear slots in this mode.");
    public virtual ActionResult Undo(Player p) => ActionResult.Fail("Nothing to undo.");
    public virtual ActionResult Button(Player? p) => ActionResult.Fail("The DropMix button does nothing here.");
    public virtual ActionResult Action(Player p, string action, string? arg) => ActionResult.Fail("Unknown action.");
    public virtual ActionResult HostAction(string action, IDictionary<string, string> args) => ActionResult.Fail("Unknown action.");

    /// <summary>Physical board: a deck was set down during a setup/lobby phase (when <see cref="AcceptsBoardCards"/> is
    /// false, so it isn't a play). Lobby modes override this to seat a board player ("ADD YOUR DECK TO JOIN").</summary>
    public virtual void BoardSetup(Card card, int pad) { }

    /// <summary>Physical board: a card was set down / lifted (no hand involved).</summary>
    public virtual void BoardPlaced(Card card, int pad) =>
        T.Pads[pad].Add(new PadCard { Card = card, Source = CardSource.Board });
    public virtual void BoardLifted(Card card, int pad)
    {
        var s = T.Pads[pad];
        int i = s.FindLastIndex(pc => pc.Source == CardSource.Board && pc.Card.Cid == card.Cid);
        if (i >= 0) s.RemoveAt(i);
    }

    /// <summary>Which card types each slot accepts right now (drives the board's glow colours).</summary>
    public virtual IReadOnlyList<CardType> Accepts(int pad) => Rules.SlotAccepts[pad];

    /// <summary>Mode block for a viewer (null = the big screen). Serialized as-is to clients.</summary>
    public abstract object View(Player? viewer);

    // ---- shared helpers ----
    protected object[] SlotViews() => Enumerable.Range(0, Rules.Slots).Select(i =>
    {
        var top = T.TopOf(i);
        return (object)new
        {
            accepts = Accepts(i).Select(t => t.ToString()).ToArray(),
            owner = top?.Owner is Side s && s != Side.None ? s.ToString() : null,
            silenced = top?.Silenced ?? false,
            valid = top?.Valid ?? true,
        };
    }).ToArray();

    /// <summary>For each card in a hand, the slots it may legally go to right now.</summary>
    protected Dictionary<string, int[]> LegalMap(Player? p, Func<Card, int, bool> legal)
    {
        var d = new Dictionary<string, int[]>();
        if (p == null) return d;
        foreach (var c in p.Hand)
            d[c.Cid] = Enumerable.Range(0, Rules.Slots).Where(i => legal(c, i)).ToArray();
        return d;
    }
}
