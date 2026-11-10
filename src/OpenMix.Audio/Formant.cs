namespace OpenMix.Audio;

/// <summary>
/// Formant correction after a pitch shift (Fusion keyzone <c>maintain_formant</c>, honoured by the game only in
/// slot 0 — <c>PlayMusicArgs.dontAllowMaintainFormant = slot != 0</c>). A plain pitch shift by factor p moves the
/// spectral envelope (the vocal-tract formants) by p too, which is what makes transposed vocals sound "chipmunky".
/// Per STFT frame we estimate the shifted signal's envelope by cepstral smoothing and multiply the spectrum by
/// env(f·p) / env(f), putting the formants back where they were while the harmonics keep the new pitch.
/// </summary>
public static class Formant
{
    const int N = 1024, Hop = 256, Lifter = 28;       // 42.7 ms frames at 24 kHz; envelope = first 28 cepstral bins

    public static float[] Correct(float[] interleaved, int channels, double pitch)
    {
        if (Math.Abs(pitch - 1) < 0.01 || interleaved.Length < N * channels) return interleaved;
        int frames = interleaved.Length / channels;
        var outBuf = new float[interleaved.Length];
        var win = new double[N];
        for (int i = 0; i < N; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N);
        var norm = new double[frames + N];
        var re = new double[N]; var im = new double[N]; var cr = new double[N]; var ci = new double[N];
        var env = new double[N / 2 + 1];
        for (int ch = 0; ch < channels; ch++)
        {
            var acc = new double[frames + N];
            for (int start = -N / 2; start < frames; start += Hop)
            {
                for (int i = 0; i < N; i++)
                {
                    int k = start + i;
                    re[i] = k >= 0 && k < frames ? interleaved[k * channels + ch] * win[i] : 0; im[i] = 0;
                }
                Fft(re, im, false);
                // cepstral envelope of log|X|
                for (int i = 0; i < N; i++) { cr[i] = Math.Log(Math.Sqrt(re[i] * re[i] + im[i] * im[i]) + 1e-9); ci[i] = 0; }
                Fft(cr, ci, true);
                for (int i = Lifter; i <= N - Lifter; i++) { cr[i] = 0; ci[i] = 0; }
                Fft(cr, ci, false);
                for (int b = 0; b <= N / 2; b++) env[b] = cr[b] / N;               // smoothed log envelope
                for (int b = 0; b <= N / 2; b++)
                {
                    double src = b * pitch;                                         // where this bin's formant came from
                    double e = src >= N / 2 ? env[N / 2] : env[(int)src] + (env[Math.Min(N / 2, (int)src + 1)] - env[(int)src]) * (src - (int)src);
                    double g = Math.Exp(Math.Clamp(e - env[b], -2.3, 2.3));       // ≤ ±20 dB
                    re[b] *= g; im[b] *= g;
                    if (b > 0 && b < N / 2) { re[N - b] = re[b]; im[N - b] = -im[b]; }   // keep it real
                }
                Fft(re, im, true);
                for (int i = 0; i < N; i++)
                {
                    int k = start + i; if (k < 0 || k >= frames) continue;
                    acc[k] += re[i] / N * win[i];
                    if (ch == 0) norm[k] += win[i] * win[i];
                }
            }
            for (int k = 0; k < frames; k++) outBuf[k * channels + ch] = (float)(norm[k] > 1e-6 ? acc[k] / norm[k] : 0);
        }
        return outBuf;
    }

    /// <summary>In-place radix-2 complex FFT (inverse unscaled).</summary>
    static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int j = 0; j < len / 2; j++)
                {
                    int a = i + j, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }
}
