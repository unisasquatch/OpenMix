using OpenMix.Board;
using OpenMix.Cards;

/// <summary>
/// The Add to Collection screen's state (Menu_CollectionClaim): the UIDs last read from the board, split into
/// Collection_InCollection / Collection_NotInCollection, whether an ADD is running, and the result message.
/// </summary>
public sealed class ClaimState
{
    /// <summary>Limits for one ADD (the {0} and {1} of Collection_ClaimExplanation): the original allowed 30 per pad.</summary>
    public const int MaxPerSlot = 30, MaxTotal = 150;

    readonly object _lock = new();
    List<(int Slot, ulong Uid)> _uids = new();
    bool[] _owned = new bool[5];
    int _busy;
    public volatile bool Dirty = true;
    public string? Message; public bool Error; public int NewCards; public DateTime ResultAt;

    public bool Busy => Volatile.Read(ref _busy) != 0;
    public bool TryBegin() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
    public void End() => Volatile.Write(ref _busy, 0);

    public void Set(List<(int Slot, ulong Uid)> uids, CardCollection col)
    {
        var owned = Enumerable.Range(0, 5).Select(i => uids.Any(x => x.Slot == i) && uids.Where(x => x.Slot == i).All(x => col.Owns(x.Uid))).ToArray();
        lock (_lock) { _uids = uids; _owned = owned; }
    }

    /// <summary>Slots whose cards are all in the collection (collectionOwnedColor on the board).</summary>
    public bool[] OwnedSlots() { lock (_lock) return (bool[])_owned.Clone(); }

    public object View(bool open, string board, CardCollection col, CardDatabase db)
    {
        List<(int Slot, ulong Uid)> u; lock (_lock) u = _uids.ToList();
        var cards = u.Select(x => (x.Slot, x.Uid, Card: db.Resolve(BoardProtocol.CtiFromUid(x.Uid)), Owned: col.Owns(x.Uid))).ToList();
        return new
        {
            open, board, busy = Busy, maxPerSlot = MaxPerSlot, maxTotal = MaxTotal,
            inCollection = cards.Count(c => c.Owned), notInCollection = cards.Count(c => !c.Owned),
            cards = cards.Select(c => new { slot = c.Slot, owned = c.Owned, cid = c.Card?.Cid, name = c.Card?.Name, image = c.Card?.ImageName }).ToArray(),
            message = Message, error = Error, newCards = NewCards, resultAt = ResultAt == default ? (DateTime?)null : ResultAt,
            collection = col.Count,
        };
    }
}
