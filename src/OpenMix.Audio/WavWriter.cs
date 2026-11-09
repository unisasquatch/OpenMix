namespace OpenMix.Audio;

public static class WavWriter
{
    // interleaved stereo float [-1,1] -> 16-bit PCM WAV
    public static void WriteStereo16(string path, float[] interleaved, int sampleRate)
    {
        int frames = interleaved.Length / 2;
        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs);
        int byteRate = sampleRate * 2 * 2, dataBytes = frames * 2 * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataBytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(sampleRate); w.Write(byteRate); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataBytes);
        foreach (var s in interleaved)
            w.Write((short)Math.Clamp((int)(s * 32767f), -32768, 32767));
    }
}
