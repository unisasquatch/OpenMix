namespace OpenMix.Audio;

// One keyzone: maps a MIDI note (and velocity) range to a sample, with playback params.
public sealed class KeyZone
{
    public required string SamplePath;   // resolved to .ogg
    public int RootNote;
    public int MinNote, MaxNote;
    public int MinVelocity, MaxVelocity = 127;
    public bool Unpitched;
    public bool VelocityToVolume;
    public double VolumeDb;              // (volume) is in dB (0 = unity, negative = quieter)
    public bool MaintainFormant;         // formant-preserving pitch shift (the game allows it in slot 0 only)
    public double PanRight;              // -1..+1
    public double FineTuneCents;
    public bool NoteOffZone;
    public int Priority = 50;
    public double RandomWeight = 50;
    // timestretch_settings: phrase samples recorded at OrigTempo that must follow the mix tempo
    public bool SyncTempo;
    public bool MaintainTime;
    public double OrigTempo = 120;

    public bool Matches(int note, int vel) =>
        note >= MinNote && note <= MaxNote && vel >= MinVelocity && vel <= MaxVelocity;
}

// A .fusion sampler patch = a list of keyzones.
public sealed class FusionPatch
{
    public List<KeyZone> Zones { get; } = new();
    /// <summary>Patch-level gain in dB: top-level (volume) in version-1 patches, else presets/&lt;first&gt;/volume.
    /// Every card patch has one (0 to −15 dB); it sets the balance between cards.</summary>
    public double VolumeDb { get; private set; }
    /// <summary>The first enabled volume ADSR (times in ms; every card patch has one, almost all a gate
    /// 0/0/1/release). Null = no envelope: the sample plays to its end.</summary>
    public Adsr? VolumeEnvelope { get; private set; }
    /// <summary>"layers" (every overlapping zone sounds), "random" (one, weighted by random_weight), "cycle".</summary>
    public string LayerMode { get; private set; } = "layers";
    int _cycle;
    public sealed record Adsr(double AttackMs, double DecayMs, double Sustain, double ReleaseMs, double Depth);
    /// <summary>Max simultaneous voices for this patch (data: 32/100 mostly; 1–10 on 48 patches = choke groups).</summary>
    public int VoiceLimit { get; private set; } = 32;
    /// <summary>(pitch_bend (range lo hi)) in cents: down for negative bend values, up for positive (data ±200).</summary>
    public double BendDownCents { get; private set; } = -200;
    public double BendUpCents { get; private set; } = 200;
    /// <summary>Enabled LFOs (all shipped ones target pan): depth, Hz (or cycles per beat when beat-synced).</summary>
    public List<Lfo> Lfos { get; } = new();
    public sealed record Lfo(string Target, double Depth, double Frequency, bool BeatSync, double InitialPhase);
    /// <summary>Legato portamento glide time in seconds (null = off; 5 patches, 2–55 ms).</summary>
    public double? PortamentoSecs { get; private set; }
    /// <summary>Patch delay_effect (11 patches): time in beats when beat-synced, else seconds.</summary>
    public PatchDelay? Delay { get; private set; }
    public sealed record PatchDelay(bool BeatSync, double Time, double Wet, double Dry, double Feedback);

    public static FusionPatch Load(string fusionPath)
    {
        var root = Dta.ParseFile(fusionPath);
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(fusionPath))!;
        var patch = new FusionPatch();
        var top = root.Children.FirstOrDefault(c => c.Head == "volume");
        var preset = FindAll(root, "presets").FirstOrDefault()?.Children.FirstOrDefault();
        patch.VolumeDb = top != null ? root.Dbl("volume") : preset?.Dbl("volume") ?? 0;
        var env = FindAll(root, "adsr").FirstOrDefault(a => a.Int("enabled") != 0
                    && (a.Child("target")?.Items.OfType<string>().LastOrDefault() ?? "volume") == "volume");
        var lsm = FindAll(root, "layer_select_mode").FirstOrDefault();
        if (lsm?.Items.OfType<string>().LastOrDefault() is string mode && mode != "layer_select_mode") patch.LayerMode = mode;
        if (FindAll(root, "voice_limit").FirstOrDefault() is Dta vl && vl.Items.OfType<string>().LastOrDefault() is string vls
            && int.TryParse(vls, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out int vli) && vli > 0) patch.VoiceLimit = vli;
        if (FindAll(root, "pitch_bend").FirstOrDefault()?.Child("range") is Dta pr)
        {
            var nums = pr.Items.OfType<string>().Skip(1).Select(x => double.TryParse(x, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN).Where(v => !double.IsNaN(v)).ToList();
            if (nums.Count == 2) { patch.BendDownCents = nums[0]; patch.BendUpCents = nums[1]; }
        }
        foreach (var lfo in FindAll(root, "lfo").Where(l => l.Int("enabled") != 0))
            patch.Lfos.Add(new Lfo(lfo.Child("target")?.Items.OfType<string>().LastOrDefault() ?? "pan", lfo.Dbl("depth"), lfo.Dbl("frequency"), lfo.Int("beat_sync") != 0, lfo.Dbl("initial_phase")));
        if (FindAll(root, "portamento").FirstOrDefault(p => p.Int("enabled") != 0) is Dta po) patch.PortamentoSecs = Math.Max(0.0005, po.Dbl("time"));
        if (FindAll(root, "delay_effect").FirstOrDefault(p => p.Int("enabled") != 0) is Dta de)
            patch.Delay = new PatchDelay(de.Int("beat_sync") != 0, de.Dbl("time"), de.Dbl("wet_gain"), de.Dbl("dry_gain", 1), de.Dbl("feedback"));
        if (env != null)
            patch.VolumeEnvelope = new Adsr(env.Dbl("attack"), env.Dbl("decay"), env.Dbl("sustain", 1), env.Dbl("release"), env.Dbl("depth", 1));

        // root -> (version ..) (keymap (keyzone ...) ...)
        var keymap = root.Children.SelectMany(c => c.Head == "keymap" ? new[] { c } : c.ChildrenNamed("keymap"))
                                  .FirstOrDefault()
                     ?? root.ChildrenNamed("keymap").FirstOrDefault();
        // The parser wraps top forms in a root; the real doc's first form IS the (version)/(keymap) pair
        // so search recursively for keyzones to be safe.
        foreach (var kz in FindAll(root, "keyzone"))
        {
            string sp = kz.Value("sample_path") ?? kz.Child("sample_path")?.Items.OfType<string>().LastOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(sp)) continue;
            // sample_path is relative to the patch dir and names the encrypted .mogg, which OpenMix never reads.
            // It plays a plain stem of the same name that the user supplied beside it: a .ogg, or failing that a .wav.
            string full = Path.GetFullPath(Path.Combine(baseDir, sp));
            string ogg = full.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                ? full
                : Path.ChangeExtension(full, ".ogg") is var o && File.Exists(o) ? o : Path.ChangeExtension(full, ".wav");

            // stretch settings are either nested in (timestretch_settings …) (123 patches) or written
            // directly in the keyzone (773 patches, 622 of them tempo-synced) — read whichever is there
            var ts = kz.Child("timestretch_settings") ?? kz;
            patch.Zones.Add(new KeyZone
            {
                SyncTempo = ts.Int("sync_tempo") != 0,
                MaintainTime = ts.Int("maintain_time") != 0,
                MaintainFormant = ts.Int("maintain_formant") != 0,
                OrigTempo = ts.Dbl("orig_tempo", 120),
                SamplePath = File.Exists(ogg) ? ogg : full,
                RootNote = kz.Int("root_note", 60),
                MinNote = kz.Int("min_note", 0),
                MaxNote = kz.Int("max_note", 127),
                MinVelocity = kz.Int("min_velocity", 0),
                MaxVelocity = kz.Int("max_velocity", 127),
                Unpitched = kz.Int("unpitched") != 0,
                VelocityToVolume = kz.Int("velocity_to_volume") != 0,
                VolumeDb = kz.Dbl("volume"),
                PanRight = kz.Dbl("pan_right"),
                FineTuneCents = kz.Dbl("fine_tune"),
                NoteOffZone = kz.Int("is_note_off_zone") != 0,
                Priority = kz.Int("priority", 50),
                RandomWeight = kz.Dbl("random_weight", 50),
            });
        }
        return patch;
    }

    /// <summary>The zones a note-on sounds, per layer_select_mode (random mode: ONLY ONE overlapping zone,
    /// weighted by random_weight; "layers" = all of them). `priority` is the voice-steal order, not a rank.</summary>
    public IReadOnlyList<KeyZone> SelectZones(int note, int vel, Random rng)
    {
        var hits = Zones.Where(z => !z.NoteOffZone && z.Matches(note, vel)).ToList();
        if (hits.Count <= 1 || LayerMode == "layers") return hits;
        if (LayerMode == "cycle") return new[] { hits[_cycle++ % hits.Count] };
        double total = hits.Sum(z => Math.Max(0, z.RandomWeight)), r = rng.NextDouble() * total;
        foreach (var z in hits) { r -= Math.Max(0, z.RandomWeight); if (r <= 0) return new[] { z }; }
        return new[] { hits[^1] };
    }

    // Choose the best keyzone for a note-on (highest priority match; ignore note-off zones).
    public KeyZone? Select(int note, int vel)
    {
        KeyZone? best = null;
        foreach (var z in Zones)
        {
            if (z.NoteOffZone || !z.Matches(note, vel)) continue;
            if (best == null || z.Priority > best.Priority) best = z;
        }
        return best;
    }

    static IEnumerable<Dta> FindAll(Dta node, string head)
    {
        foreach (var c in node.Children)
        {
            if (c.Head == head) yield return c;
            foreach (var g in FindAll(c, head)) yield return g;
        }
    }
}
