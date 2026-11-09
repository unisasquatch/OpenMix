using OpenMix.Cards;

namespace OpenMix.Audio;

/// <summary>
/// Per-slot music effects: the original's native slot DSP (libHmxAudioPlugin "Hmx.SoundClashSlot", one per slot) driven
/// by the MusicEffects prefabs. Processing order, as in SoundClashSlotEffect::Process:
/// Bitcrush → (Distortion, unused) → Wah → Vibe → Delay → Biquad filter → Gain. LFOs are tempo-synced: a rate r means
/// one cycle per 4/r beats (rate 4 = a quarter note, 8 = an eighth).
/// </summary>
public sealed record SlotFxParams(
    (int bits, int down, double wet)? Crush = null,
    (double freq, double q)? LowPass = null,
    (double beatDelay, double dry, double wet, double fb)? Delay = null,
    (double[] f, double depth, double rate, double wet, double shape)? Vibe = null,
    (double closed, double open, double qc, double qo, double rate, double shape)? Wah = null,
    double? GainDb = null);

/// <summary>The slot DSP of the imported music-effect prefabs (<see cref="GameTables.MusicEffects"/>); prefabs that only
/// change key, tempo or mode have no entry here.</summary>
public static class SlotFxPresets
{
    static IReadOnlyDictionary<string, MusicEffectDef>? _from;
    static Dictionary<string, (SlotFxParams fx, string persist)> _all = new();

    /// <summary>Prefab name → (DSP params, persistence). Persistence: "card" until removal, "turn", or a bar count.</summary>
    public static IReadOnlyDictionary<string, (SlotFxParams fx, string persist)> All
    {
        get
        {
            var src = GameTables.MusicEffects;
            if (!ReferenceEquals(src, _from))
            {
                var d = new Dictionary<string, (SlotFxParams, string)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, e) in src)
                    if (e.Crush != null || e.LowPass != null || e.Delay != null || e.Vibe != null || e.Wah != null || e.GainDb != null)
                        d[name] = (new SlotFxParams(e.Crush, e.LowPass, e.Delay, e.Vibe, e.Wah, e.GainDb), e.Persist);
                _all = d; _from = src;
            }
            return _all;
        }
    }
}

/// <summary>One slot's DSP state. Effective params: for each component, the most recently activated controller's.</summary>
sealed class SlotChain
{
    readonly int _rate;
    public SlotChain(int rate) { _rate = rate; }

    // active controllers on this slot, oldest first
    public readonly List<(int id, SlotFxParams fx, double delaySecs)> Active = new();
    SlotFxParams? _crushP, _lpP, _delayP, _vibeP, _wahP; double _gain = 1; double _delaySecs;
    public bool Any => Active.Count > 0;

    public void Recompute()
    {
        _crushP = _lpP = _delayP = _vibeP = _wahP = null; double? gdb = null; _delaySecs = 0;
        foreach (var (_, fx, ds) in Active)
        {
            if (fx.Crush != null) _crushP = fx; if (fx.LowPass != null) _lpP = fx; if (fx.Vibe != null) _vibeP = fx; if (fx.Wah != null) _wahP = fx;
            if (fx.Delay != null) { _delayP = fx; _delaySecs = ds; }
            if (fx.GainDb != null) gdb = fx.GainDb;
        }
        _gain = gdb is double d ? Math.Pow(10, d / 20) : 1;
        if (_lpP?.LowPass is var (f, q)) _lp = SfxBiquad.LowPass(_rate, f, q);
        if (_delayP == null) { _dl = null; }
        else
        {
            int n = Math.Max(1, (int)Math.Round(_delaySecs * _rate));
            if (_dl == null || _dl.Length / 2 != n) { _dl = new float[n * 2]; _dpos = 0; }
        }
        if (Active.Count == 0) Reset();
    }

    void Reset() { _holdN = 0; _hl = _hr = 0; _lpL = default; _lpR = default; _dl = null; Array.Clear(_apL); Array.Clear(_apR); _wL = default; _wR = default; }

    // bitcrush
    int _holdN; float _hl, _hr;
    // low-pass
    SfxCoefs _lp; SfxState _lpL, _lpR;
    // delay
    float[]? _dl; int _dpos;
    // vibe: 4 first-order all-passes per channel
    readonly double[] _apL = new double[4], _apR = new double[4];
    // wah: band-pass biquad, coefficients refreshed every 32 samples
    SfxCoefs _wc; SfxState _wL, _wR; int _wahN;

    static double Lfo(double beat, double rate, double shape)
    {
        // tempo-synced: one cycle per 4/rate beats; shape skews the rise (0.5 = symmetric) → 0..1
        double ph = beat * rate / 4; ph -= Math.Floor(ph);
        double s = Math.Clamp(shape, 0.05, 0.95);
        double x = ph < s ? ph / s * 0.5 : 0.5 + (ph - s) / (1 - s) * 0.5;
        return 0.5 - 0.5 * Math.Cos(2 * Math.PI * x);
    }

    public void Process(ref float l, ref float r, double beat)
    {
        if (_crushP?.Crush is var (bits, down, wet))
        {   // sample-hold by `down` at the native 48 kHz (= down/2 here at 24 kHz), quantise to `bits`
            int hold = Math.Max(1, (int)Math.Round(down * _rate / 48000.0));
            if (_holdN-- <= 0)
            {
                _holdN = hold - 1;
                float steps = 1 << (bits - 1);
                _hl = MathF.Round(l * steps) / steps; _hr = MathF.Round(r * steps) / steps;
            }
            l = (float)(l * (1 - wet) + _hl * wet); r = (float)(r * (1 - wet) + _hr * wet);
        }
        if (_wahP?.Wah is var (closed, open, qc, qo, wrate, wshape))
        {
            if (_wahN-- <= 0)
            {
                _wahN = 31;
                double t = Lfo(beat, wrate, wshape);
                double f = closed * Math.Pow(open / closed, t), q = qc + (qo - qc) * t;
                _wc = SfxBiquad.BandPass(_rate, Math.Min(f, _rate * 0.45), q);
            }
            l = (float)_wL.Run(_wc, l); r = (float)_wR.Run(_wc, r);
        }
        if (_vibeP?.Vibe is var (freqs, depth, vrate, vwet, vshape))
        {   // phaser: 4 all-pass stages swept down by `depth` from their centre frequencies
            double t = Lfo(beat, vrate, vshape);
            double xl = l, xr = r;
            for (int i = 0; i < 4; i++)
            {
                double f = freqs[i] * (1 - depth * t) + 20;
                double tn = Math.Tan(Math.PI * Math.Min(f, _rate * 0.45) / _rate), a = (tn - 1) / (tn + 1);
                double yl = a * xl + _apL[i]; _apL[i] = xl - a * yl; xl = yl;
                double yr = a * xr + _apR[i]; _apR[i] = xr - a * yr; xr = yr;
            }
            l = (float)(l * (1 - vwet) + xl * vwet); r = (float)(r * (1 - vwet) + xr * vwet);
        }
        if (_delayP?.Delay is var (_, dry, dwet, fb) && _dl != null)
        {
            float dlL = _dl[_dpos * 2], dlR = _dl[_dpos * 2 + 1];
            _dl[_dpos * 2] = (float)(l + dlL * fb); _dl[_dpos * 2 + 1] = (float)(r + dlR * fb);
            _dpos = (_dpos + 1) % (_dl.Length / 2);
            l = (float)(l * dry + dlL * dwet); r = (float)(r * dry + dlR * dwet);
        }
        if (_lpP != null) { l = (float)_lpL.Run(_lp, l); r = (float)_lpR.Run(_lp, r); }
        if (_gain != 1) { l *= (float)_gain; r *= (float)_gain; }
    }
}

struct SfxCoefs { public double b0, b1, b2, a1, a2; }
struct SfxState
{
    double x1, x2, y1, y2;
    public double Run(in SfxCoefs c, double x)
    {
        double y = c.b0 * x + c.b1 * x1 + c.b2 * x2 - c.a1 * y1 - c.a2 * y2;
        x2 = x1; x1 = x; y2 = y1; y1 = y; return y;
    }
}

static class SfxBiquad
{
    // RBJ cookbook
    public static SfxCoefs LowPass(int fs, double f, double q)
    {
        double w = 2 * Math.PI * Math.Min(f, fs * 0.45) / fs, al = Math.Sin(w) / (2 * q), cw = Math.Cos(w), a0 = 1 + al;
        return new SfxCoefs { b0 = (1 - cw) / 2 / a0, b1 = (1 - cw) / a0, b2 = (1 - cw) / 2 / a0, a1 = -2 * cw / a0, a2 = (1 - al) / a0 };
    }
    public static SfxCoefs BandPass(int fs, double f, double q)
    {   // constant 0 dB peak gain
        double w = 2 * Math.PI * f / fs, al = Math.Sin(w) / (2 * q), cw = Math.Cos(w), a0 = 1 + al;
        return new SfxCoefs { b0 = al / a0, b1 = 0, b2 = -al / a0, a1 = -2 * cw / a0, a2 = (1 - al) / a0 };
    }
}
