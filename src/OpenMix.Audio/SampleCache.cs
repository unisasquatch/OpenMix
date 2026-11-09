using NVorbis;

namespace OpenMix.Audio;

// Decoded PCM sample: interleaved float, with channel count and sample rate.
public sealed class PcmSample
{
    public required float[] Data;    // interleaved
    public int Channels;
    public int SampleRate;
    public int Frames => Channels == 0 ? 0 : Data.Length / Channels;

    // Read one frame as stereo (duplicating mono), applying gain and pan.
    public void MixFrame(int frame, double gain, double panRight, ref float l, ref float r)
    {
        if (frame < 0 || frame >= Frames) return;
        float sl, sr;
        if (Channels == 1) { sl = sr = Data[frame]; }
        else { sl = Data[frame * Channels]; sr = Data[frame * Channels + 1]; }
        double p = Math.Clamp(panRight, -1, 1), lg, rg;
        if (Channels == 1)
        {   // mono source: equal-power pan
            lg = gain * Math.Cos((p + 1) * Math.PI / 4); rg = gain * Math.Sin((p + 1) * Math.PI / 4);
        }
        else
        {   // stereo source (every card sample): balance, unity at centre (equal-power cost them −3 dB)
            lg = gain * (p > 0 ? 1 - p : 1); rg = gain * (p < 0 ? 1 + p : 1);
        }
        l += (float)(sl * lg);
        r += (float)(sr * rg);
    }
}

public sealed class SampleCache
{
    readonly Dictionary<string, PcmSample> _cache = new(StringComparer.OrdinalIgnoreCase);

    public PcmSample Load(string oggPath)
    {
        if (_cache.TryGetValue(oggPath, out var s)) return s;
        s = Decode(oggPath);
        _cache[oggPath] = s;
        return s;
    }

    readonly Dictionary<string, PcmSample> _processed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A phrase sample converted to <paramref name="targetRate"/>, transposed by
    /// <paramref name="semitones"/>, and time-stretched by <paramref name="stretch"/> (duration
    /// multiplier) without changing pitch. Cached per (path, stretch, semitones).
    /// </summary>
    public PcmSample Processed(string oggPath, int targetRate, double stretch, double semitones, bool keepFormants = false)
    {
        string key = $"{oggPath}|{targetRate}|{stretch:0.0000}|{semitones:0.00}|{keepFormants}";
        if (_processed.TryGetValue(key, out var p)) return p;
        var src = Load(oggPath);
        int ch = Math.Max(1, src.Channels);
        double pitch = Math.Pow(2, semitones / 12.0);
        // 1) resample: converts rate AND raises pitch by `pitch` (shortening by 1/pitch)
        var shifted = TimeStretch.Resample(src.Data, ch, src.SampleRate / (double)targetRate * pitch);
        // 2) WSOLA: restore/adjust duration so the phrase lasts orig*stretch, pitch unchanged
        var stretched = TimeStretch.Wsola(shifted, ch, stretch * pitch, targetRate);
        if (keepFormants) stretched = Formant.Correct(stretched, ch, pitch);
        p = new PcmSample { Data = stretched, Channels = ch, SampleRate = targetRate };
        _processed[key] = p;
        return p;
    }

    static PcmSample Decode(string path)
    {
        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return DecodeWav(path);
        using var vr = new VorbisReader(path);
        int ch = vr.Channels;
        var all = new List<float>((int)(vr.TotalSamples * ch));
        var buf = new float[ch * 4096];
        int n;
        while ((n = vr.ReadSamples(buf, 0, buf.Length)) > 0)
            for (int k = 0; k < n; k++) all.Add(buf[k]);
        return new PcmSample { Data = all.ToArray(), Channels = ch, SampleRate = vr.SampleRate };
    }

    /// <summary>RIFF/WAVE: PCM 8/16/24/32-bit or IEEE float.</summary>
    public static PcmSample DecodeWav(string path)
    {
        var b = File.ReadAllBytes(path);
        int pos = 12, fmt = 1, ch = 1, rate = 44100, bits = 16;
        float[]? data = null;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int len = BitConverter.ToInt32(b, pos + 4); int body = pos + 8;
            if (id == "fmt ")
            {
                fmt = BitConverter.ToUInt16(b, body); ch = BitConverter.ToUInt16(b, body + 2);
                rate = BitConverter.ToInt32(b, body + 4); bits = BitConverter.ToUInt16(b, body + 14);
                if (fmt == 0xFFFE && len >= 26) fmt = BitConverter.ToUInt16(b, body + 24);   // WAVE_FORMAT_EXTENSIBLE
            }
            else if (id == "data")
            {
                len = Math.Min(len, b.Length - body);
                int bps = bits / 8, n = len / bps; data = new float[n];
                for (int i = 0; i < n; i++)
                {
                    int o = body + i * bps;
                    data[i] = (fmt, bits) switch
                    {
                        (3, 32) => BitConverter.ToSingle(b, o),
                        (_, 8) => (b[o] - 128) / 128f,
                        (_, 16) => BitConverter.ToInt16(b, o) / 32768f,
                        (_, 24) => ((b[o] | b[o + 1] << 8 | (sbyte)b[o + 2] << 16)) / 8388608f,
                        (_, 32) => BitConverter.ToInt32(b, o) / 2147483648f,
                        _ => 0,
                    };
                }
            }
            pos = body + len + (len & 1);
        }
        return new PcmSample { Data = data ?? Array.Empty<float>(), Channels = ch, SampleRate = rate };
    }
}
