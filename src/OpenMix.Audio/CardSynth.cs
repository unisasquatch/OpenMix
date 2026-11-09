namespace OpenMix.Audio;

/// <summary>
/// Renders one card's looping phrase to interleaved stereo float, matched to the mix:
///  - the MIDI is re-sequenced at <c>targetBpm</c> (so every card's 32-bar loop lines up exactly),
///  - pitched material is transposed by <c>transpose</c> semitones (card key -> mix key),
///  - phrase samples flagged sync_tempo+maintain_time are time-stretched WITHOUT pitch change
///    from their recorded orig_tempo to the mix tempo (and transposed by resample+stretch),
///  - drum one-shots (unpitched, not maintain_time) are only re-timed.
/// Tails past the loop end wrap to the front so the loop is seamless.
/// </summary>
public static class CardSynth
{
    /// <summary>Whether unpitched tempo-synced phrases (rap/spoken vocals, drum loops) follow the mix key. Fusion
    /// transposes by shifting the note, which an unpitched zone ignores, so they stay at their recorded pitch: phone
    /// A/B on Heartbeat, pitch-profile match A minor 0.66 → 0.72, D minor 0.75 → 0.80 (2026-11-05). 90 zones.</summary>
    public static bool TransposeUnpitchedPhrases { get; set; } = false;

    static double Herm(double xm1, double x0, double x1, double x2, double t)
    {
        double c1 = 0.5 * (x1 - xm1), c2 = xm1 - 2.5 * x0 + 2 * x1 - 0.5 * x2, c3 = 0.5 * (x2 - xm1) + 1.5 * (x0 - x1);
        return ((c3 * t + c2) * t + c1) * t + x0;
    }

    public static float[] Render(MidiSong song, SampleCache cache, int targetRate, out int loopFrames)
        => Render(song, cache, targetRate, out loopFrames, song.Bpm, 0);

    public static float[] Render(MidiSong song, SampleCache cache, int targetRate, out int loopFrames,
                                 double targetBpm, int transpose, bool wrap = true, bool allowFormant = false)
    {
        double secPerTick = 60.0 / targetBpm / song.Division;
        loopFrames = Math.Max(1, (int)Math.Round(song.LoopTicks * secPerTick * targetRate));
        int bufFrames = wrap ? loopFrames : loopFrames + targetRate * 6;      // one-shots keep their tails
        var outL = new float[bufFrames];
        var outR = new float[bufFrames];

        var rng = new Random(song.Variant.GetHashCode() ^ song.Notes.Count);      // deterministic per arrangement
        var patchP = song.Patch;
        double framesPerTick = secPerTick * targetRate;

        // ---- controllers: CC64 sustain holds note-offs; CC7 channel volume; pitch bend ----
        var bends = song.Bends.OrderBy(b => b.Tick).ToArray();
        var vols = song.Controls.Where(c => c.Cc == 7).OrderBy(c => c.Tick).Select(c => (c.Tick, c.Value)).ToArray();
        var pedal = song.Controls.Where(c => c.Cc == 64).OrderBy(c => c.Tick).Select(c => (c.Tick, c.Value)).ToArray();
        static int At((long Tick, int Value)[] ev, long tick, int dflt)
        {   // last value at or before tick
            int lo = 0, hi = ev.Length - 1, r = -1;
            while (lo <= hi) { int m = (lo + hi) / 2; if (ev[m].Tick <= tick) { r = m; lo = m + 1; } else hi = m - 1; }
            return r < 0 ? dflt : ev[r].Value;
        }
        long Sustained(long off)
        {   // pedal down at the note-off: the note holds until the pedal comes up
            if (pedal.Length == 0 || At(pedal, off, 0) < 64) return off;
            foreach (var (t, v) in pedal) if (t > off && v < 64) return t;
            return song.LoopTicks;
        }
        double BendCents(long tick)
        {
            if (bends.Length == 0) return 0;
            int v = At(bends, tick, 0);
            return v >= 0 ? v / 8191.0 * Math.Abs(patchP.BendUpCents) : v / 8192.0 * Math.Abs(patchP.BendDownCents);   // range lo is a magnitude ("200 200", "-200 200", "-0 200")
        }
        static double Cc7Gain(int v) => v >= 127 ? 1 : v <= 0 ? 0 : Math.Pow(v / 127.0, 2);   // 40·log10 curve

        // ---- pass 1: voices with their natural end ----
        var voices = new List<Voice>();
        var notes = song.Notes.OrderBy(n => n.Tick).ToList();
        NoteEvent? prevNote = null;
        foreach (var ev0 in notes)
        {
            var ev = ev0 with { OffTick = Sustained(ev0.OffTick) };
            // legato portamento: a note starting while the previous one is still held glides from its pitch
            double? glideFrom = patchP.PortamentoSecs != null && prevNote is NoteEvent pn && pn.OffTick > ev.Tick && pn.Tick < ev.Tick ? pn.Note : null;
            prevNote = ev;
            foreach (var zone in patchP.SelectZones(ev.Note, ev.Velocity, rng))
            {
                bool phrase = zone.SyncTempo && zone.MaintainTime;               // stretch, keep pitch
                bool followsKey = !zone.Unpitched || (phrase && TransposeUnpitchedPhrases);   // drum one-shots don't
                double semis = (zone.Unpitched ? 0 : ev.Note - zone.RootNote)
                             + (followsKey ? transpose : 0) + zone.FineTuneCents / 100.0;
                PcmSample smp;
                try
                {
                    smp = phrase
                        ? cache.Processed(zone.SamplePath, targetRate, stretch: zone.OrigTempo / targetBpm, semitones: semis, keepFormants: allowFormant && zone.MaintainFormant)
                        : cache.Load(zone.SamplePath);
                }
                catch { continue; }
                if (smp.Frames == 0) continue;
                double step;
                if (phrase) step = 1.0;                                            // already at target rate/tempo/pitch
                else if (zone.SyncTempo)                                          // tape-style follow: speed = tempo ratio
                    step = smp.SampleRate / (double)targetRate * (targetBpm / zone.OrigTempo) * Math.Pow(2, semis / 12.0);
                else
                    step = smp.SampleRate / (double)targetRate * Math.Pow(2, semis / 12.0);
                double gain = Math.Pow(10, (zone.VolumeDb + patchP.VolumeDb) / 20.0);
                if (zone.VelocityToVolume) gain *= ev.Velocity / 127.0;
                int startFrame = (int)Math.Round(ev.Tick * framesPerTick);
                var adsr = patchP.VolumeEnvelope;
                int offAt = adsr == null ? int.MaxValue : (int)Math.Round((ev.OffTick - ev.Tick) * framesPerTick);
                double rel = adsr == null ? 0 : Math.Max(1, adsr.ReleaseMs * targetRate / 1000.0);
                int natural = (int)Math.Min(loopFrames * 2L, (long)((smp.Frames - 1) / step));
                if (adsr != null) natural = (int)Math.Min(natural, (long)offAt + (long)rel);
                voices.Add(new Voice(ev, zone, smp, step, gain, startFrame, offAt, rel, phrase,
                                     glideFrom is double gf && !zone.Unpitched ? gf - ev.Note : 0) { End = startFrame + natural });
            }
        }

        // ---- voice limit (Fusion voice_limit; the shared pool's soft limit, HmxAudioMgr fusionConfig.softVoiceLimit):
        // steal the lowest priority (highest number) voice, the oldest first; priority 0 is never stolen — then the
        // new voice doesn't sound ----
        int limit = OpenMix.Cards.GameTables.FusionSoftVoiceLimit is int pool ? Math.Min(patchP.VoiceLimit, pool) : patchP.VoiceLimit;
        var active = new List<Voice>();
        foreach (var v in voices)
        {
            active.RemoveAll(a => a.End <= v.Start);
            if (active.Count >= limit)
            {
                var victim = active.Where(a => a.Zone.Priority != 0).OrderByDescending(a => a.Zone.Priority).ThenBy(a => a.Start).FirstOrDefault();
                if (victim == null) { v.End = v.Start; continue; }
                victim.End = Math.Min(victim.End, v.Start); victim.Stolen = true;
                active.Remove(victim);
            }
            active.Add(v);
        }

        // ---- pass 2: render ----
        double beatsPerFrame = targetBpm / 60.0 / targetRate;
        int fadeFrames = Math.Max(1, targetRate * 3 / 1000);                    // 3 ms when a voice is stolen
        foreach (var v in voices)
        {
            if (v.End <= v.Start) continue;
            var (ev, zone, smp) = (v.Ev, v.Zone, v.Smp);
            var adsr = patchP.VolumeEnvelope;
            double perMs = targetRate / 1000.0;
            double atk = adsr == null ? 0 : adsr.AttackMs * perMs, dec = adsr == null ? 0 : adsr.DecayMs * perMs;
            double sus = adsr?.Sustain ?? 1, depth = adsr?.Depth ?? 1;
            double levelAtOff = 1, srcPos = 0;
            bool modPitch = !v.Phrase && (bends.Length > 0 || v.Glide != 0);
            double stepNow = v.Step, panNow = zone.PanRight, ccGain = 1;
            for (int outFrame = v.Start; outFrame < v.End; outFrame++)
            {
                if (srcPos >= smp.Frames - 1) break;
                int t = outFrame - v.Start;
                if ((t & 63) == 0)
                {   // control rate: bend + glide, CC7, pan LFOs
                    long tick = (long)(outFrame / framesPerTick);
                    if (modPitch)
                    {
                        double cents = BendCents(tick);
                        if (v.Glide != 0) cents += v.Glide * 100 * Math.Max(0, 1 - t / (patchP.PortamentoSecs!.Value * targetRate));
                        stepNow = v.Step * Math.Pow(2, cents / 1200);
                    }
                    ccGain = vols.Length == 0 ? 1 : Cc7Gain(At(vols, tick, 127));
                    panNow = zone.PanRight;
                    foreach (var lf in patchP.Lfos)
                        if (lf.Target == "pan")
                        {
                            double cyc = lf.BeatSync ? outFrame * beatsPerFrame * lf.Frequency : outFrame / (double)targetRate * lf.Frequency;
                            panNow += lf.Depth * Math.Sin(2 * Math.PI * (cyc + lf.InitialPhase));
                        }
                    panNow = Math.Clamp(panNow, -1, 1);
                }
                double env;
                if (adsr == null) env = 1;
                else
                {
                    double held = t < atk ? t / atk : t < atk + dec ? 1 - (1 - sus) * (t - atk) / dec : sus;
                    if (t < v.OffAt) { env = held; levelAtOff = held; }
                    else
                    {
                        double r = (t - v.OffAt) / v.Rel;
                        if (r >= 1) break;                                         // released: voice ends
                        env = levelAtOff * (1 - r);
                    }
                    env = 1 - depth * (1 - env);
                }
                if (v.Stolen && v.End - outFrame < fadeFrames) env *= (v.End - outFrame) / (double)fadeFrames;
                int si = (int)srcPos; double ft = srcPos - si;
                double g = v.Gain * env * ccGain;
                int w = wrap ? outFrame % loopFrames : outFrame;
                if (w >= bufFrames) break;
                if (ft < 1e-9)
                {   // integer step (unpitched / native rate): no interpolation
                    float l0 = 0, r0 = 0; smp.MixFrame(si, g, panNow, ref l0, ref r0);
                    outL[w] += l0; outR[w] += r0;
                }
                else
                {   // 4-point Hermite (linear interpolation dulls the top octave)
                    float am = 0, bm = 0, a0 = 0, b0 = 0, a1 = 0, b1 = 0, a2 = 0, b2 = 0;
                    smp.MixFrame(si - 1, g, panNow, ref am, ref bm); smp.MixFrame(si, g, panNow, ref a0, ref b0);
                    smp.MixFrame(si + 1, g, panNow, ref a1, ref b1); smp.MixFrame(si + 2, g, panNow, ref a2, ref b2);
                    outL[w] += (float)Herm(am, a0, a1, a2, ft); outR[w] += (float)Herm(bm, b0, b1, b2, ft);
                }
                srcPos += stepNow;
            }
        }

        // ---- patch delay_effect (beat-synced time in beats): run twice round the loop so the tail wraps ----
        if (patchP.Delay is { } pd && pd.Wet > 0)
        {
            int dn = Math.Max(1, (int)Math.Round((pd.BeatSync ? pd.Time * 60.0 / targetBpm : pd.Time) * targetRate));
            var dl = new float[dn * 2]; int dp = 0;
            var dryL = (float[])outL.Clone(); var dryR = (float[])outR.Clone();
            for (int pass = 0; pass < (wrap ? 2 : 1); pass++)
                for (int f = 0; f < bufFrames; f++)
                {
                    float xl = dryL[f], xr = dryR[f], yl = dl[dp * 2], yr = dl[dp * 2 + 1];
                    dl[dp * 2] = (float)(xl + yl * pd.Feedback); dl[dp * 2 + 1] = (float)(xr + yr * pd.Feedback);
                    dp = (dp + 1) % dn;
                    if (pass == (wrap ? 1 : 0)) { outL[f] = (float)(xl * pd.Dry + yl * pd.Wet); outR[f] = (float)(xr * pd.Dry + yr * pd.Wet); }
                }
        }

        var outBuf = new float[bufFrames * 2];
        for (int f = 0; f < bufFrames; f++)
        {
            outBuf[f * 2] = outL[f];                 // no per-card saturation: the master limiter handles peaks
            outBuf[f * 2 + 1] = outR[f];
        }
        return outBuf;
    }

    sealed class Voice(NoteEvent ev, KeyZone zone, PcmSample smp, double step, double gain, int start, int offAt, double rel, bool phrase, double glide)
    {
        public NoteEvent Ev = ev; public KeyZone Zone = zone; public PcmSample Smp = smp;
        public double Step = step, Gain = gain, Rel = rel, Glide = glide;
        public int Start = start, OffAt = offAt, End;
        public bool Phrase = phrase, Stolen;
    }
}
