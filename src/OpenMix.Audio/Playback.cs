using NAudio.Wave;

namespace OpenMix.Audio;

// Streams a MixEngine to the default output device via NAudio.
public sealed class MixSampleProvider : ISampleProvider
{
    readonly MixEngine _mix;
    public WaveFormat WaveFormat { get; }
    public MixSampleProvider(MixEngine mix)
    {
        _mix = mix;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, 2);
    }
    public int Read(float[] buffer, int offset, int count)
    {
        _mix.Read(buffer, offset, count);   // endless stream
        return count;
    }
}

public sealed class MixPlayer : IDisposable
{
    readonly MixEngine _mix;
    IWavePlayer? _out;
    public MixPlayer(MixEngine mix) => _mix = mix;

    public void Start()
    {
        _out = new WaveOutEvent { DesiredLatency = 150 };
        _out.Init(new MixSampleProvider(_mix));
        if (Environment.GetEnvironmentVariable("DM_MUTE") == "1") _out.Volume = 0;   // test instances: render but stay silent
        _out.Play();
    }
    public void Stop() { _out?.Stop(); }
    public void Dispose() { _out?.Dispose(); _out = null; }
}
