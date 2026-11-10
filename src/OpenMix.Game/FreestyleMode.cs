using OpenMix.Cards;

namespace OpenMix.Game;

/// <summary>
/// Freestyle: no turns or scoring — everyone drops cards whenever they like and the mix plays.
/// Cards must still go in a slot that accepts their colour (Wild/FX anywhere), and the same card
/// can't be in two slots. Hands refill after each play.
/// </summary>
public sealed class FreestyleMode : GameMode
{
    public override string Id => "freestyle";
    public override string Title => "Freestyle";
    public int HandSize { get; set; } = 5;
    long _buttonPresses;

    public override void Configure(IDictionary<string, string> o)
    {
        if (o.TryGetValue("hand", out var h) && int.TryParse(h, out var n)) HandSize = Math.Clamp(n, 1, 10);
    }

    public override void Start() { foreach (var p in T.Players) T.DrawTo(p, HandSize); }
    public override void OnPlayerJoined(Player p) => T.DrawTo(p, HandSize);

    string? Check(Card c, int pad)
    {
        if (!Rules.IsTypeValidInSlot(c.Type, pad)) return $"{c.Type} cards don't go in slot {pad + 1}.";
        for (int i = 0; i < Rules.Slots; i++)
            if (T.TopValid(i)?.Card.Cid == c.Cid) return "That card is already in the mix.";
        return null;
    }

    public override ActionResult Play(Player p, Card card, int pad)
    {
        if (Check(card, pad) is string err) return ActionResult.Fail(err);
        p.Hand.Remove(card);
        T.Pads[pad].Add(new PadCard { Card = card, Source = CardSource.Player, PlayedBy = p.Name });
        T.DrawTo(p, HandSize);
        return ActionResult.Success;
    }

    public override ActionResult Draw(Player p, int count) { T.DrawN(p, Math.Max(1, count)); return ActionResult.Success; }

    public override ActionResult Clear(Player p, int pad)
    {
        var s = T.Pads[pad];
        if (s.Count == 0 || s[^1].Source != CardSource.Player) return ActionResult.Fail("Nothing to clear.");
        T.PopTop(pad);
        return ActionResult.Success;
    }

    public override ActionResult Undo(Player p)
    {
        // take back your own card from the top of a slot
        for (int i = 0; i < Rules.Slots; i++)
        {
            var top = T.TopOf(i);
            if (top is { Source: CardSource.Player } && top.PlayedBy == p.Name)
            {
                T.Pads[i].RemoveAt(T.Pads[i].Count - 1);
                p.Hand.Add(top.Card);
                return ActionResult.Success;
            }
        }
        return ActionResult.Fail("None of your cards are on top.");
    }

    /// <summary>DropMix button in Freestyle: a random mix transition.</summary>
    public override ActionResult Button(Player? p)
    {
        if (Enumerable.Range(0, Rules.Slots).All(i => T.TopValid(i) == null)) return ActionResult.Fail("Put a card in the mix first.");
        _buttonPresses++;
        T.EmitMusic("freestyle_button");
        return ActionResult.Success;
    }

    public override object View(Player? viewer) => new
    {
        id = Id, title = Title, subtitle = "Drop cards from your phone",
        phase = "play",
        slots = SlotViews(),
        legal = LegalMap(viewer, (c, i) => Check(c, i) == null),
        canPlay = true, allowDraw = true, allowClear = true, allowUndo = true,
        buttonPresses = _buttonPresses,
    };
}
