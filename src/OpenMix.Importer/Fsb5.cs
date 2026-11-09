using System.Text;

namespace OpenMix.Importer;

/// <summary>Decodes the FMOD FSB5 sample archives inside the game's .bank files to WAV (voice lines, UI sounds).
/// The banks use FADPCM: per channel, 0x8C-byte frames of u32 coefficient indices (8 nibbles), u32 shifts
/// (8 nibbles), s16 hist1, s16 hist2, then 8 subframes of 32 nibbles (low nibble first). Each frame carries its own
/// history, so frames decode independently. (FADPCM is a compression format, not encryption; the algorithm
/// follows vgmstream's open-source decoder.)</summary>
public static class Fsb5
{
    static readonly int[,] Coefs = { { 0, 0 }, { 60, 0 }, { 122, 60 }, { 115, 52 }, { 98, 55 }, { 0, 0 }, { 0, 0 }, { 0, 0 } };
    static readonly Dictionary<int, int> Freqs = new()
    { [0] = 4000, [1] = 8000, [2] = 11000, [3] = 11025, [4] = 16000, [5] = 22050, [6] = 24000, [7] = 32000, [8] = 44100, [9] = 48000, [10] = 96000 };
    const int Frame = 0x8C;

    /// <summary>Extract every sample in <paramref name="bankPath"/> to <c>outDir/&lt;name&gt;.wav</c>. Returns the count.</summary>
    public static int Extract(string bankPath, string outDir)
    {
        var d = File.ReadAllBytes(bankPath);
        int b = d.AsSpan().IndexOf("FSB5"u8);
        if (b < 0) throw new InvalidDataException($"No FSB5 archive in {Path.GetFileName(bankPath)}");
        uint ver = U32(d, b + 4); int num = (int)U32(d, b + 8); int shdr = (int)U32(d, b + 12);
        int namesSize = (int)U32(d, b + 16); int dataSize = (int)U32(d, b + 20);
        int hdrLen = ver == 1 ? 0x3C : 0x40;
        int p = b + hdrLen;
        var samples = new List<(int freq, int ch, long off, long count)>();
        for (int i = 0; i < num; i++)
        {
            ulong bits = BitConverter.ToUInt64(d, p); p += 8;
            bool next = (bits & 1) != 0;
            int freq = Freqs.GetValueOrDefault((int)((bits >> 1) & 0xF), 44100);
            int ch = ((bits >> 5) & 3) switch { 0 => 1, 1 => 2, 2 => 6, _ => 8 };
            long off = (long)((bits >> 7) & 0x7FFFFFF) << 5;
            long count = (long)((bits >> 34) & 0x3FFFFFFF);
            while (next)
            {
                uint c = U32(d, p); p += 4;
                next = (c & 1) != 0; int size = (int)((c >> 1) & 0xFFFFFF); int typ = (int)((c >> 25) & 0x7F);
                if (typ == 1) ch = d[p];
                else if (typ == 2) freq = (int)U32(d, p);
                p += size;
            }
            samples.Add((freq, ch, off, count));
        }
        int namesAt = b + hdrLen + shdr;
        var names = new string[num];
        for (int i = 0; i < num; i++)
        {
            if (namesSize == 0) { names[i] = $"sample_{i:0000}"; continue; }
            int s = namesAt + (int)U32(d, namesAt + i * 4);
            int e = Array.IndexOf(d, (byte)0, s);
            names[i] = Encoding.UTF8.GetString(d, s, e - s);
        }
        int dataAt = namesAt + namesSize;
        Directory.CreateDirectory(outDir);
        for (int i = 0; i < num; i++)
        {
            var (freq, ch, off, count) = samples[i];
            long end = i + 1 < num ? samples[i + 1].off : dataSize;
            int start = (int)(dataAt + off), len = (int)(end - off);
            int nframes = len / (Frame * ch);
            var chans = new short[ch][];
            for (int c = 0; c < ch; c++)
            {
                var pcm = new short[nframes * 256];
                for (int k = 0; k < nframes; k++) DecodeFrame(d, start + (k * ch + c) * Frame, pcm, k * 256);
                chans[c] = count > 0 && count < pcm.Length ? pcm[..(int)count] : pcm;
            }
            int n = chans.Min(x => x.Length);
            var safe = new string(names[i].Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '.' ? c : '_').ToArray());
            WriteWav(Path.Combine(outDir, safe + ".wav"), chans, n, freq);
        }
        return num;
    }

    /// <summary>The sample names in a bank's FSB5 archive, by sample index, as <see cref="Extract"/> names the WAV files.</summary>
    public static string[] SampleNames(byte[] d)
    {
        int b = d.AsSpan().IndexOf("FSB5"u8);
        if (b < 0) throw new InvalidDataException("No FSB5 archive in the bank");
        uint ver = U32(d, b + 4); int num = (int)U32(d, b + 8); int shdr = (int)U32(d, b + 12), namesSize = (int)U32(d, b + 16);
        int namesAt = b + (ver == 1 ? 0x3C : 0x40) + shdr;
        var names = new string[num];
        for (int i = 0; i < num; i++)
        {
            if (namesSize == 0) { names[i] = $"sample_{i:0000}"; continue; }
            int s = namesAt + (int)U32(d, namesAt + i * 4);
            int e = Array.IndexOf(d, (byte)0, s);
            names[i] = new string(Encoding.UTF8.GetString(d, s, e - s).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '.' ? c : '_').ToArray());
        }
        return names;
    }

    static void DecodeFrame(byte[] d, int at, short[] dst, int dstAt)
    {
        uint coefs = U32(d, at), shifts = U32(d, at + 4);
        long h1 = BitConverter.ToInt16(d, at + 8), h2 = BitConverter.ToInt16(d, at + 10);
        for (int i = 0; i < 8; i++)
        {
            int idx = (int)((coefs >> (i * 4)) & 7);
            long c1 = Coefs[idx, 0], c2 = Coefs[idx, 1];
            int sh = 22 - (int)((shifts >> (i * 4)) & 0xF);
            for (int j = 0; j < 32; j++)
            {
                int k = i * 32 + j;
                int by = d[at + 12 + k / 2];
                int nib = (k & 1) == 0 ? by & 0xF : by >> 4;
                long signed = (long)(nib >= 8 ? nib - 16 : nib) << 28;
                long s = signed >> sh;
                s = (s - h2 * c2 + h1 * c1) >> 6;
                s = Math.Clamp(s, -32768, 32767);
                dst[dstAt + k] = (short)s; h2 = h1; h1 = s;
            }
        }
    }

    static void WriteWav(string path, short[][] chans, int n, int rate)
    {
        int ch = chans.Length, bytes = n * ch * 2;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)ch); w.Write(rate); w.Write(rate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        for (int i = 0; i < n; i++) for (int c = 0; c < ch; c++) w.Write(chans[c][i]);
    }

    static uint U32(byte[] d, int i) => BitConverter.ToUInt32(d, i);
}
