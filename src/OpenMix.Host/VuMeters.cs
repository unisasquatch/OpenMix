using OpenMix.Audio;



/// <summary>
/// Per-slot VU levels for the table's meters, after the original the original's audio: every frame it reads
/// Hmx.Audio.Analyzer.GetVUResult() for each slot bus (VU enabled, range 50 dB → 0..1) into a short history,
/// and GetVUMeter(slot) reads that history back delayed by the audio output latency so the bars move with
/// what is heard. Here: ~30 Hz samples of the engine's per-slot RMS, VU ballistics, a latency-delayed read,
/// plus the heard beat position so the screen can run its own clock between samples.
/// </summary>
public sealed class VuMeters
{
    const double RangeDb = 50, MaxDb = 0;          // SetVURangeDB(50); max left at the plugin default (0 dBFS)
    const double RiseMs = 30, FallMs = 300;        // VU-style ballistics (plugin defaults not visible in the dump)
    readonly MixEngine _mix;
    readonly double[] _vu = new double[5];
    readonly Queue<(DateTime t, float[] lv)> _hist = new();
    readonly object _lock = new();

    public VuMeters(MixEngine mix) { _mix = mix; }

    public void Start(CancellationToken ct) => Task.Run(async () =>
    {
        var last = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(16, ct).ContinueWith(_ => { });
            var now = DateTime.UtcNow; double dt = Math.Max(0.001, (now - last).TotalSeconds); last = now;
            var (rms, _) = _mix.TakeSlotLevels();
            var lv = new float[5];
            for (int i = 0; i < 5; i++)
            {
                double db = rms[i] > 1e-6 ? 20 * Math.Log10(rms[i]) : -120;
                double target = Math.Clamp((db - (MaxDb - RangeDb)) / RangeDb, 0, 1);
                double tau = (target > _vu[i] ? RiseMs : FallMs) / 1000;
                _vu[i] += (target - _vu[i]) * (1 - Math.Exp(-dt / tau));
                lv[i] = (float)Math.Round(_vu[i], 3);
            }
            lock (_lock)
            {
                _hist.Enqueue((now, lv));
                while (_hist.Count > 0 && (now - _hist.Peek().t).TotalSeconds > 2) _hist.Dequeue();
            }
        }
    }, ct);

    /// <summary>What the meters should show now: levels from OutputLatency ago, the heard beat, and the tempo.</summary>
    public object Now()
    {
        double lat = _mix.OutputLatency;
        float[] lv = new float[5];
        lock (_lock)
        {
            var cut = DateTime.UtcNow.AddSeconds(-lat);
            foreach (var (t, l) in _hist) { if (t > cut) break; lv = l; }
        }
        double bpm = _mix.PlayBpm;
        return new
        {
            lv,
            beat = _mix.IsEmpty ? -1 : Math.Round(Math.Max(0, _mix.BeatPosition - lat * bpm / 60), 4),
            bpm = Math.Round(bpm, 3),
        };
    }
}
