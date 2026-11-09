/// <summary>
/// the original's battery monitor: the board reports a percentage (byte 4 of the battery notify, read
/// every 10 s); the last 6 readings are kept with how many slots held cards at the time (loaded alkalines read lower
/// under load). Each reading is flagged against thresholds for "no cards" vs "1+ slots"; Low / Critical /
/// ForceDisconnect are true when at least 4 of the 6 are flagged. The display level is the average of
/// InverseLerp(floor, 100) per reading.
/// </summary>
public sealed class BoardBattery
{
    // (low ≤, critical ≤, force disconnect, display floor): program logic — the original's battery monitor stores these
    // as integer literals in its static constructor (see docs/PROVENANCE.md)
    static readonly (int low, int crit, int force, bool forceInclusive, int floor) NoCards = (53, 45, 40, true, 36);
    static readonly (int low, int crit, int force, bool forceInclusive, int floor) WithCards = (30, 20, 15, false, 15);
    readonly Queue<(int pct, int slots)> _q = new();
    readonly object _lock = new();
    DateTime _lastReading;

    public double Level { get; private set; } = -1;     // 0..1 display bar, -1 = no reading yet
    public bool Low { get; private set; }
    public bool Critical { get; private set; }
    public bool ForceDisconnect { get; private set; }
    public int LastPercent { get; private set; } = -1;

    public void Add(int pct, int slotsWithCards)
    {
        lock (_lock)
        {
            _lastReading = DateTime.UtcNow; LastPercent = pct;
            _q.Enqueue((pct, slotsWithCards)); while (_q.Count > 6) _q.Dequeue();
            int low = 0, crit = 0, force = 0; double lvl = 0;
            foreach (var (p, s) in _q)
            {
                var t = s == 0 ? NoCards : WithCards;
                if (p <= t.low) low++;
                if (p <= t.crit) crit++;
                if (t.forceInclusive ? p <= t.force : p < t.force) force++;
                lvl += Math.Clamp((p - t.floor) / (100.0 - t.floor), 0, 1);
            }
            Level = lvl / _q.Count;
            Low = low >= 4; Critical = crit >= 4; ForceDisconnect = force >= 4;
        }
    }

    /// <summary>After 40 s without readings the history is dropped, which lets a forced-off board be retried.</summary>
    public void ExpireIfIdle()
    {
        lock (_lock)
            if (_q.Count > 0 && DateTime.UtcNow - _lastReading > TimeSpan.FromSeconds(40))
            { _q.Clear(); Low = Critical = ForceDisconnect = false; Level = -1; }
    }
}
