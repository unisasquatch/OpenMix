namespace OpenMix.Audio;

/// <summary>
/// Pitch-preserving time stretch (WSOLA: waveform-similarity overlap-add) and plain
/// resampling, for interleaved stereo float. Used to fit "maintain_time" phrase samples to the
/// mix tempo, and (stretch + resample together) to transpose them without changing length.
/// </summary>
public static class TimeStretch
{
    /// <summary>Change duration by <paramref name="factor"/> (2 = twice as long) keeping pitch.</summary>
    public static float[] Wsola(float[] src, int channels, double factor, int sampleRate)
    {
        int frames = src.Length / channels;
        if (frames < 64 || Math.Abs(factor - 1) < 1e-3) return (float[])src.Clone();

        int win = Math.Max(256, (int)(sampleRate * 0.046));      // ~46 ms analysis window
        win -= win % 2;
        int hopOut = win / 2;                                     // 50% overlap
        double hopIn = hopOut / factor;
        int tol = Math.Max(32, win / 4);                          // search range for best alignment

        int outFrames = (int)Math.Ceiling(frames * factor) + win;
        var outBuf = new float[outFrames * channels];
        var norm = new float[outFrames];
        var hann = new float[win];
        for (int i = 0; i < win; i++) hann[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (win - 1)));

        // mono copy for correlation
        var mono = new float[frames];
        for (int f = 0; f < frames; f++) { float s = 0; for (int c = 0; c < channels; c++) s += src[f * channels + c]; mono[f] = s / channels; }

        int prevIn = 0;            // where the last chosen segment started in the input
        int outPos = 0;
        for (int k = 0; ; k++)
        {
            int target = (int)Math.Round(k * hopIn);
            if (target + win >= frames) break;
            int best = target;
            if (k > 0)
            {
                // natural continuation of previous segment is prevIn + hopOut; pick the candidate
                // near `target` whose start best matches it
                int natural = prevIn + hopOut;
                double bestScore = double.NegativeInfinity;
                int lo = Math.Max(0, target - tol), hi = Math.Min(frames - win - 1, target + tol);
                int cmp = Math.Min(win / 2, frames - natural - 1);
                if (cmp > 16)
                    for (int cand = lo; cand <= hi; cand += 2)
                    {
                        double sc = 0;
                        for (int j = 0; j < cmp; j += 4) sc += mono[cand + j] * mono[natural + j];
                        if (sc > bestScore) { bestScore = sc; best = cand; }
                    }
            }
            for (int j = 0; j < win; j++)
            {
                int o = outPos + j; if (o >= outFrames) break;
                float w = hann[j];
                for (int c = 0; c < channels; c++) outBuf[o * channels + c] += src[(best + j) * channels + c] * w;
                norm[o] += w;
            }
            prevIn = best;
            outPos += hopOut;
        }
        int want = (int)Math.Round(frames * factor);
        var result = new float[want * channels];
        for (int f = 0; f < want && f < outFrames; f++)
        {
            float n = norm[f] > 1e-3f ? norm[f] : 1f;
            for (int c = 0; c < channels; c++) result[f * channels + c] = outBuf[f * channels + c] / n;
        }
        return result;
    }

    /// <summary>Linear-interpolation resample by <paramref name="step"/> source frames per output frame.</summary>
    public static float[] Resample(float[] src, int channels, double step)
    {
        int frames = src.Length / channels;
        if (Math.Abs(step - 1) < 1e-6) return (float[])src.Clone();
        int outFrames = Math.Max(1, (int)(frames / step));
        var dst = new float[outFrames * channels];
        for (int i = 0; i < outFrames; i++)
        {
            double sp = i * step; int s0 = (int)sp; double fr = sp - s0;
            int s1 = Math.Min(s0 + 1, frames - 1); if (s0 >= frames) s0 = frames - 1;
            for (int c = 0; c < channels; c++)
                dst[i * channels + c] = (float)(src[s0 * channels + c] + (src[s1 * channels + c] - src[s0 * channels + c]) * fr);
        }
        return dst;
    }
}
