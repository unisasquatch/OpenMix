using OpenMix.Cards;

namespace OpenMix.Audio;

/// <summary>
/// The original's FMOD mixer, rebuilt from the chains imported at setup from the user's Master Bank.bank
/// (<see cref="GameTables.FmodMix"/>): the cardSlot1 event's master track (slot 1 only), bus:/CardMusic (all card
/// parts) and the master bus. Each chain runs its pre-fader effects, the fader level, then its post-fader effects, in
/// the bank's order. OpenMix reproduces FMOD's three-band EQ, parametric EQ, compressor and limiter; plugins, sends and
/// meters in a chain are skipped. Without imported chains every bus is a neutral pass-through.
/// Stereo, per-sample, allocation-free. FMOD's exact detector/knee internals aren't in the bank; these are
/// standard feed-forward designs with the bank's parameters (hard knee, stereo-linked peak detection).
/// </summary>
public sealed class MixBus
{
    // ---------------- building blocks ----------------

    /// <summary>One stereo processing stage of a chain.</summary>
    public interface IStage { void Process(ref double l, ref double r); }

    /// <summary>A fixed gain (a fader level, or the three-band EQ's mid band).</summary>
    public sealed class Gain : IStage
    {
        readonly double _g;
        public Gain(double db) => _g = Math.Pow(10, db / 20.0);
        public void Process(ref double l, ref double r) { l *= _g; r *= _g; }
    }

    /// <summary>RBJ-cookbook biquad, stereo.</summary>
    public sealed class Biquad : IStage
    {
        double b0, b1, b2, a1, a2, xl1, xl2, yl1, yl2, xr1, xr2, yr1, yr2;

        public static Biquad Peaking(double fs, double f0, double octaves, double gainDb)
        {
            double A = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * f0 / fs;
            double alpha = Math.Sin(w) * Math.Sinh(Math.Log(2) / 2 * octaves * w / Math.Sin(w));
            return new Biquad().Set(1 + alpha * A, -2 * Math.Cos(w), 1 - alpha * A, 1 + alpha / A, -2 * Math.Cos(w), 1 - alpha / A);
        }

        public static Biquad Shelf(double fs, double f0, double gainDb, bool high)
        {
            double A = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * f0 / fs, c = Math.Cos(w);
            double alpha = Math.Sin(w) / 2 * Math.Sqrt(2), s = 2 * Math.Sqrt(A) * alpha;   // S = 1 (12 dB/oct)
            return high
                ? new Biquad().Set(A * ((A + 1) + (A - 1) * c + s), -2 * A * ((A - 1) + (A + 1) * c), A * ((A + 1) + (A - 1) * c - s),
                                   (A + 1) - (A - 1) * c + s, 2 * ((A - 1) - (A + 1) * c), (A + 1) - (A - 1) * c - s)
                : new Biquad().Set(A * ((A + 1) - (A - 1) * c + s), 2 * A * ((A - 1) - (A + 1) * c), A * ((A + 1) - (A - 1) * c - s),
                                   (A + 1) + (A - 1) * c + s, -2 * ((A - 1) + (A + 1) * c), (A + 1) + (A - 1) * c - s);
        }

        Biquad Set(double nb0, double nb1, double nb2, double na0, double na1, double na2)
        { b0 = nb0 / na0; b1 = nb1 / na0; b2 = nb2 / na0; a1 = na1 / na0; a2 = na2 / na0; return this; }

        public void Process(ref double l, ref double r)
        {
            double yl = b0 * l + b1 * xl1 + b2 * xl2 - a1 * yl1 - a2 * yl2; xl2 = xl1; xl1 = l; yl2 = yl1; yl1 = yl; l = yl;
            double yr = b0 * r + b1 * xr1 + b2 * xr2 - a1 * yr1 - a2 * yr2; xr2 = xr1; xr1 = r; yr2 = yr1; yr1 = yr; r = yr;
        }
    }

    /// <summary>Feed-forward compressor: stereo-linked peak detector, hard knee, attack/release in dB domain.</summary>
    public sealed class Compressor : IStage
    {
        readonly double _thr, _slope, _att, _rel, _makeup; double _env;
        public Compressor(double fs, double thresholdDb, double ratio, double attackMs, double releaseMs, double makeupDb)
        {
            _thr = thresholdDb; _slope = 1 - 1 / ratio; _makeup = makeupDb;
            _att = 1 - Math.Exp(-1 / (attackMs * fs / 1000)); _rel = 1 - Math.Exp(-1 / (releaseMs * fs / 1000));
        }
        public void Process(ref double l, ref double r)
        {
            double pk = Math.Max(Math.Abs(l), Math.Abs(r));
            double over = pk > 1e-9 ? 20 * Math.Log10(pk) - _thr : -1;
            double gr = over > 0 ? over * _slope : 0;
            _env += (gr - _env) * (gr > _env ? _att : _rel);
            double g = Math.Pow(10, (_makeup - _env) / 20);
            l *= g; r *= g;
        }
    }

    /// <summary>FMOD-style limiter: maximizer gain, then instant gain reduction to the ceiling, release in ms.</summary>
    public sealed class Limiter : IStage
    {
        readonly double _ceil, _gain, _rel; double _gr = 1;
        public Limiter(double fs, double ceilingDb, double maximizerDb, double releaseMs)
        { _ceil = Math.Pow(10, ceilingDb / 20); _gain = Math.Pow(10, maximizerDb / 20); _rel = 1 - Math.Exp(-1 / (releaseMs * fs / 1000)); }
        public void Process(ref double l, ref double r)
        {
            l *= _gain; r *= _gain;
            double pk = Math.Max(Math.Abs(l), Math.Abs(r));
            double need = pk > _ceil ? _ceil / pk : 1;
            _gr = need < _gr ? need : _gr + (1 - _gr) * _rel;     // instant attack, smooth release
            if (_gr > need) _gr = need;
            l *= _gr; r *= _gr;
        }
    }

    // ---------------- the imported chains ----------------

    readonly IStage[] _slot1, _cardMusic, _master;

    /// <summary>The chains imported at setup (<see cref="GameTables.FmodMix"/>); neutral when they weren't imported.</summary>
    public MixBus(int fs) : this(fs, GameTables.FmodMix) { }

    public MixBus(int fs, FmodMixSettings? mix)
    {
        _slot1 = mix == null ? Array.Empty<IStage>() : Build(fs, mix.CardSlot1, doubleParametricEq: false);
        // CALIBRATION (measured, 2026-11-05): against the real app the CardMusic parametric EQ cut (320 Hz) is ~2× the
        // bank's gain (a +5 dB bump at 250–400 Hz remained on both a single Beat and a full mix). A second identical
        // stage matches; its source (per-slot HMX.SoundClashSlot defaults or a second bus into CardMusic) is unknown.
        _cardMusic = mix == null ? Array.Empty<IStage>() : Build(fs, mix.CardMusic, doubleParametricEq: true);
        _master = mix == null ? Array.Empty<IStage>() : Build(fs, mix.Master, doubleParametricEq: false);
    }

    /// <summary>A chain's stages: pre-fader effects, fader (left out at 0 dB), post-fader effects.</summary>
    static IStage[] Build(int fs, MixChain chain, bool doubleParametricEq)
    {
        var s = new List<IStage>();
        void Add(MixEffect e)
        {
            switch (e)
            {
                case ThreeBandEqEffect q:   // shelves at 12 dB/oct (FMOD's default slope), relative to the mid band
                    s.Add(Biquad.Shelf(fs, Math.Min(q.LowCrossoverHz, fs * 0.45), q.LowDb - q.MidDb, high: false));
                    s.Add(Biquad.Shelf(fs, Math.Min(q.HighCrossoverHz, fs * 0.45), q.HighDb - q.MidDb, high: true));
                    if (q.MidDb != 0) s.Add(new Gain(q.MidDb));
                    break;
                case ParametricEqEffect p:
                    s.Add(Biquad.Peaking(fs, Math.Min(p.CenterHz, fs * 0.45), p.Octaves, p.GainDb));
                    if (doubleParametricEq) s.Add(Biquad.Peaking(fs, Math.Min(p.CenterHz, fs * 0.45), p.Octaves, p.GainDb));
                    break;
                case CompressorEffect c: s.Add(new Compressor(fs, c.ThresholdDb, c.Ratio, c.AttackMs, c.ReleaseMs, c.MakeupDb)); break;
                case LimiterEffect m: s.Add(new Limiter(fs, m.CeilingDb, m.MaximizerDb, m.ReleaseMs)); break;
            }
        }
        foreach (var e in chain.PreFader) Add(e);
        if (chain.VolumeDb != 0) s.Add(new Gain(chain.VolumeDb));
        foreach (var e in chain.PostFader) Add(e);
        return s.ToArray();
    }

    static void Run(IStage[] stages, ref double l, ref double r) { foreach (var s in stages) s.Process(ref l, ref r); }

    /// <summary>cardSlot1's event master track (slot index 0 only).</summary>
    public void Slot1(ref double l, ref double r) => Run(_slot1, ref l, ref r);

    /// <summary>bus:/CardMusic (all card parts, transitions).</summary>
    public void CardMusic(ref double l, ref double r) => Run(_cardMusic, ref l, ref r);

    /// <summary>The master bus.</summary>
    public void Master(ref double l, ref double r) => Run(_master, ref l, ref r);
}
