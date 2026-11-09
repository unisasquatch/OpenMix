namespace OpenMix.Board;

/// <summary>Turns a stream of A3 poll summaries into card add/remove events, using the
/// per-slot CTI-XOR trick the game itself uses: a stack whose count rises by one has a
/// new top card whose CTI = newXor ⊕ oldXor; a count that falls by one is verified the
/// same way. Any other delta (or a mismatch) is reported as "needs a full read", which
/// the caller satisfies with an A5 and feeds back via <see cref="ApplyFullRead"/>.
/// Pure logic — no BLE.</summary>
public sealed class SlotTracker
{
    private sealed class State
    {
        public List<ushort> Ctis = new();
        public ushort Xor;
        public int Count;
    }

    private readonly State[] _slots = { new(), new(), new(), new(), new() };
    // a lower count must be seen on two consecutive polls before it counts as a lift (the app ignores a
    // lower count while a higher one was seen recently: half-lifted cards, flicker)
    private readonly (int count, ushort xor)?[] _pendingDrop = new (int, ushort)?[5];

    /// <summary>Total cards across all slots, and the tallest stack (for the poll read size).</summary>
    public (int total, int tallest) Heights()
    {
        int t = 0, m = 0;
        foreach (var s in _slots) { t += s.Count; m = Math.Max(m, s.Count); }
        return (t, m);
    }

    /// <summary>Start from known contents (after a reconnect) so only real changes produce events.</summary>
    public void Seed(Slot slot, IReadOnlyList<Cti> ctis)
    {
        var st = _slots[(int)slot];
        st.Ctis = ctis.Select(c => c.Value).ToList();
        st.Xor = 0; foreach (var c in st.Ctis) st.Xor ^= c;
        st.Count = st.Ctis.Count;
        _pendingDrop[(int)slot] = null;
    }

    public IReadOnlyList<Cti> Contents(Slot s) =>
        _slots[(int)s].Ctis.ConvertAll(v => new Cti(v));

    /// <summary>Apply one full poll (summaries indexed by BOARD slot, as
    /// <see cref="BoardProtocol.ParsePoll"/> returns). Returns the card events that can
    /// be resolved immediately, and in <paramref name="needFullRead"/> the game slots
    /// whose change was ambiguous and need an A5.</summary>
    public IReadOnlyList<CardEvent> Update(SlotSummary[] byBoardIndex, out List<Slot> needFullRead)
    {
        var events = new List<CardEvent>();
        needFullRead = new List<Slot>();

        for (int b = 0; b < 5; b++)
        {
            Slot slot = BoardProtocol.FromBoardIndex(b);
            State st = _slots[(int)slot];
            SlotSummary sum = byBoardIndex[b];

            // read error (count bit 7): the app logs it and flags the slot; no card event, no resync
            if (sum.ReadError) continue;

            if (sum.Count == st.Count && sum.CtiXor == st.Xor) { _pendingDrop[(int)slot] = null; continue; }   // no change

            if (sum.Count < st.Count)
            {
                var seen = _pendingDrop[(int)slot];
                if (seen is not { } d || d.count != sum.Count || d.xor != sum.CtiXor)
                { _pendingDrop[(int)slot] = (sum.Count, sum.CtiXor); continue; }      // confirm on the next poll
            }
            _pendingDrop[(int)slot] = null;

            if (sum.Count == st.Count + 1)
            {
                ushort newCti = (ushort)(sum.CtiXor ^ st.Xor);
                st.Ctis.Add(newCti);
                st.Xor = sum.CtiXor;
                st.Count = sum.Count;
                events.Add(new CardEvent(slot, new Cti(newCti), Added: true, st.Count));
            }
            else if (sum.Count == st.Count - 1 && st.Ctis.Count > 0)
            {
                ushort top = st.Ctis[^1];
                if ((ushort)(st.Xor ^ top) == sum.CtiXor)
                {
                    st.Ctis.RemoveAt(st.Ctis.Count - 1);
                    st.Xor = sum.CtiXor;
                    st.Count = sum.Count;
                    events.Add(new CardEvent(slot, new Cti(top), Added: false, st.Count));
                }
                else needFullRead.Add(slot);                // not the top card — resync
            }
            else if (sum.Count == 0)
            {
                foreach (var cti in st.Ctis)
                    events.Add(new CardEvent(slot, new Cti(cti), Added: false, 0));
                st.Ctis.Clear(); st.Xor = 0; st.Count = 0;
            }
            else needFullRead.Add(slot);                    // multi-card delta — resync
        }
        return events;
    }

    /// <summary>Replace a slot's contents from a full A5 read, emitting add/remove
    /// events for the difference against what we had.</summary>
    public IReadOnlyList<CardEvent> ApplyFullRead(Slot slot, IReadOnlyList<Cti> ctis)
    {
        State st = _slots[(int)slot];
        var events = new List<CardEvent>();

        var old = new List<ushort>(st.Ctis);
        var now = new List<ushort>();
        ushort xor = 0;
        foreach (var c in ctis) { now.Add(c.Value); xor ^= c.Value; }

        // removals (old not present in new, by multiset)
        var remaining = new List<ushort>(now);
        foreach (var o in old)
        {
            int idx = remaining.IndexOf(o);
            if (idx >= 0) remaining.RemoveAt(idx);
            else events.Add(new CardEvent(slot, new Cti(o), Added: false, now.Count));
        }
        // additions (new not matched by old)
        var matched = new List<ushort>(old);
        foreach (var n in now)
        {
            int idx = matched.IndexOf(n);
            if (idx >= 0) matched.RemoveAt(idx);
            else events.Add(new CardEvent(slot, new Cti(n), Added: true, now.Count));
        }

        st.Ctis = now; st.Xor = xor; st.Count = now.Count;
        return events;
    }
}
