namespace OpenMix.Board;

/// <summary>A board slot, 0..4 (game numbering). Board hardware numbers them in
/// reverse; the protocol layer maps between the two.</summary>
public enum Slot { Slot0 = 0, Slot1 = 1, Slot2 = 2, Slot3 = 3, Slot4 = 4 }

/// <summary>Buttons the board reports on BTN_STS (0005).</summary>
public enum BoardButton { Eq = 0x01, MotionSensor = 0x02 }

/// <summary>A card type index (CTI) — the top 16 bits of a card's 64-bit UID.
/// Resolves to a card via the card database.</summary>
public readonly record struct Cti(ushort Value)
{
    public override string ToString() => $"CTI:{Value}";
}

/// <summary>An RGB colour, channels 0..1. The board quantises each to 4 bits.</summary>
public readonly record struct LedColor(float R, float G, float B)
{
    public static readonly LedColor Off = new(0, 0, 0);
}

/// <summary>One A3-poll summary for a board slot: how many cards are stacked and the
/// XOR of their CTIs (used to detect single add/remove without a full read).</summary>
public readonly record struct SlotSummary(int Count, ushort CtiXor, bool ReadError);

/// <summary>Raised when a card is added to or removed from a slot.</summary>
public sealed record CardEvent(Slot Slot, Cti Cti, bool Added, int StackCount);

/// <summary>Raised when a slot's full contents are (re)read (A4/A5), e.g. after an
/// ambiguous poll delta.</summary>
public sealed record SlotContents(Slot Slot, IReadOnlyList<Cti> Ctis);
