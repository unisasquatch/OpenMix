namespace OpenMix.Audio;

public static class WavWriter
{
    // interleaved stereo float [-1,1] -> 16-bit PCM WAV file
    public static void WriteStereo16(string path, float[] interleaved, int sampleRate)
    {
        using var fs = new FileStream(path, FileMode.Create);
        WriteStereo16(fs, interleaved, sampleRate);
    }

    /// <summary>interleaved stereo float [-1,1] -> a 16-bit PCM WAV held in memory (for serving a preview).</summary>
    public static byte[] Stereo16Bytes(float[] interleaved, int sampleRate)
    {
        using var ms = new MemoryStream();
        WriteStereo16(ms, interleaved, sampleRate);
        return ms.ToArray();
    }

    static void WriteStereo16(Stream stream, float[] interleaved, int sampleRate)
    {
        int frames = interleaved.Length / 2;
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int byteRate = sampleRate * 2 * 2, dataBytes = frames * 2 * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataBytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(sampleRate); w.Write(byteRate); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataBytes);
        foreach (var s in interleaved)
            w.Write((short)Math.Clamp((int)(s * 32767f), -32768, 32767));
    }
}
