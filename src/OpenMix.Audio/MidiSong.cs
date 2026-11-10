namespace OpenMix.Audio;

public readonly record struct NoteEvent(long Tick, int Note, int Velocity, long OffTick);

// Parsed .midisong + its .mid: the note sequence that drives the sampler, plus tempo/length.
public sealed class MidiSong
{
    public required FusionPatch Patch;
    public int Division;                 // ticks per quarter note
    public double Bpm = 120;
    public long LoopTicks;               // length of the loop (last end-of-track / max off)
    public List<NoteEvent> Notes { get; } = new();
    /// <summary>Pitch bend events (tick, -8192..8191); 1,080 in the shipped MIDIs.</summary>
    public List<(long Tick, int Value)> Bends { get; } = new();
    /// <summary>Controller events: CC7 channel volume and CC64 sustain pedal (tick, controller, value).</summary>
    public List<(long Tick, int Cc, int Value)> Controls { get; } = new();
    /// <summary>MIDI text/marker events, e.g. stutter_4n, transition_end (transition stingers).</summary>
    public List<(long Tick, string Text)> Markers { get; } = new();
    public double TickToBeats(long tick) => tick / (double)Division;

    public double TicksToSeconds(long ticks) => ticks / (double)Division * (60.0 / Bpm);
    public double LoopSeconds => TicksToSeconds(LoopTicks);
    /// <summary>Loop length in bars once <see cref="FitLoop"/> has run (else derived from LoopTicks).</summary>
    public int LoopBars => (int)Math.Round(LoopTicks / (Division * 4.0));
    /// <summary>`pickup` marker positions in beats (the original's audio starts a joining part here, §1.2).</summary>
    public IEnumerable<double> PickupBeats => Markers.Where(m => m.Text == "pickup").Select(m => TickToBeats(m.Tick));

    /// <summary>
    /// The game loops every part on its content-DB "Num Bars" (8/16/32 bars of 4/4), not on the MIDI's
    /// end-of-track, which is off in 472 of 606 card MIDIs (a tick short up to several bars) and made
    /// loops drift against each other. If notes start past Num Bars (15 files), round up to the next
    /// of 8/16/32 so nothing is cut and the loop still divides the 32-bar master. Notes starting at or
    /// after the loop end (a downbeat of the next cycle) are dropped.
    /// </summary>
    public void FitLoop(int numBars)
    {
        if (numBars <= 0 || Division <= 0) return;
        long bar = Division * 4L;
        double lastStart = Notes.Count == 0 ? 0 : Notes.Max(n => n.Tick) / (double)bar;
        int bars = numBars;
        foreach (int b in new[] { 8, 16, 32 }) if (bars < b && lastStart >= bars) bars = b;
        LoopTicks = bars * bar;
        Notes.RemoveAll(n => n.Tick >= LoopTicks);
        for (int k = 0; k < Notes.Count; k++)
            if (Notes[k].OffTick > LoopTicks) Notes[k] = Notes[k] with { OffTick = LoopTicks };
    }

    public string Variant { get; private set; } = "";

    /// <summary>All arrangement names in a card dir, e.g. 24kMagic_bass_maj, 24kMagic_beat.</summary>
    public static string[] Variants(string songDir) =>
        Directory.GetFiles(songDir, "*.midisong").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(s => s).ToArray();

    /// <summary>
    /// Pick the arrangement for a part ("beat","bass","lead","loop") and mode (minor or major).
    /// Cards ship per-part and per-mode arrangements (name_part_maj/min); single-arrangement cards
    /// (e.g. drums) just have one. Falls back gracefully when a part/mode isn't present.
    /// </summary>
    public static string PickVariant(string songDir, string? part, bool? minor)
    {
        var all = Variants(songDir);
        if (all.Length == 0) throw new FileNotFoundException("no .midisong in " + songDir);
        string ModeTag(string n) => n.EndsWith("_min", StringComparison.OrdinalIgnoreCase) ? "min"
                                  : n.EndsWith("_maj", StringComparison.OrdinalIgnoreCase) ? "maj" : "";
        int Score(string n)
        {
            int s = 0; var lower = n.ToLowerInvariant();
            if (part != null && lower.Contains("_" + part.ToLowerInvariant())) s += 10;
            var m = ModeTag(n);
            if (minor is bool mn && m != "") s += (mn ? m == "min" : m == "maj") ? 5 : -5;
            return s;
        }
        return all.OrderByDescending(Score).ThenBy(n => n.Length).First();
    }

    public static MidiSong Load(string songDir) => Load(songDir, null, null);

    public static MidiSong Load(string songDir, string? part, bool? minor, string? variant = null)
    {
        // songDir contains <variant>.midisong, midi/, patches/, samples/
        variant ??= PickVariant(songDir, part, minor);
        var midisongPath = Path.Combine(songDir, variant + ".midisong");
        var ms = Dta.ParseFile(midisongPath);

        string midiRel = Find(ms, "midi_path")?.Items.ElementAtOrDefault(1) as string ?? "";
        string patchRel = FirstPatchPath(ms) ?? "";
        string midiPath = Path.GetFullPath(Path.Combine(songDir, midiRel));
        string patchPath = Path.GetFullPath(Path.Combine(songDir, patchRel));

        var song = new MidiSong { Patch = FusionPatch.Load(patchPath), Variant = variant };
        ParseMidi(File.ReadAllBytes(midiPath), song);
        return song;
    }

    static string? FirstPatchPath(Dta root)
    {
        foreach (var p in FindAllNodes(root, "patch"))
            if (p.Items.ElementAtOrDefault(1) is string s) return s;
        return null;
    }

    static Dta? Find(Dta root, string head) => FindAllNodes(root, head).FirstOrDefault();

    static IEnumerable<Dta> FindAllNodes(Dta node, string head)
    {
        foreach (var c in node.Children)
        {
            if (c.Head == head) yield return c;
            foreach (var g in FindAllNodes(c, head)) yield return g;
        }
    }

    // ---- Standard MIDI File parse ----
    static void ParseMidi(byte[] d, MidiSong song)
    {
        if (d.Length < 14 || d[0] != 'M' || d[1] != 'T' || d[2] != 'h' || d[3] != 'd')
            throw new InvalidDataException("not a MIDI file");
        int ntrk = (d[10] << 8) | d[11];
        song.Division = (d[12] << 8) | d[13];
        int i = 14;
        var openNotes = new Dictionary<int, (long tick, int vel)>();
        for (int t = 0; t < ntrk && i + 8 <= d.Length; t++)
        {
            if (!(d[i] == 'M' && d[i + 1] == 'T' && d[i + 2] == 'r' && d[i + 3] == 'k')) break;
            int len = (d[i + 4] << 24) | (d[i + 5] << 16) | (d[i + 6] << 8) | d[i + 7];
            int j = i + 8, end = j + len;
            long tick = 0; int running = 0;
            openNotes.Clear();
            while (j < end)
            {
                tick += ReadVlq(d, ref j);
                int status = d[j];
                if ((status & 0x80) != 0) { running = status; j++; }
                else status = running;
                int type = status >> 4;
                if (status == 0xFF)           // meta
                {
                    int meta = d[j++]; int mlen = (int)ReadVlq(d, ref j);
                    if (meta == 0x51 && mlen == 3)
                    {
                        int us = (d[j] << 16) | (d[j + 1] << 8) | d[j + 2];
                        song.Bpm = 60000000.0 / us;
                    }
                    else if (meta == 0x2F) song.LoopTicks = Math.Max(song.LoopTicks, tick);
                    else if (meta is 0x01 or 0x06 && mlen > 0)
                        song.Markers.Add((tick, System.Text.Encoding.ASCII.GetString(d, j, mlen).Trim()));
                    j += mlen;
                }
                else if (status == 0xF0 || status == 0xF7) { int sl = (int)ReadVlq(d, ref j); j += sl; }
                else if (type is 0x8 or 0x9 or 0xA or 0xB or 0xE)   // 2-byte channel msgs
                {
                    int p1 = d[j], p2 = d[j + 1]; j += 2;
                    if (type == 0xE) song.Bends.Add((tick, ((p2 << 7) | p1) - 8192));
                    else if (type == 0xB && p1 is 7 or 64) song.Controls.Add((tick, p1, p2));
                    if (type == 0x9 && p2 > 0) openNotes[p1] = (tick, p2);
                    else if (type == 0x8 || (type == 0x9 && p2 == 0))  // note off
                    {
                        if (openNotes.TryGetValue(p1, out var on))
                        {
                            song.Notes.Add(new NoteEvent(on.tick, p1, on.vel, tick));
                            openNotes.Remove(p1);
                        }
                    }
                }
                else if (type is 0xC or 0xD) j += 1;   // 1-byte channel msgs
                else j++;                              // unknown: skip a byte defensively
            }
            // notes with no explicit off: end them at loop end
            foreach (var (note, on) in openNotes)
                song.Notes.Add(new NoteEvent(on.tick, note, on.vel, long.MaxValue));
            i = end;
        }
        long maxOff = song.Notes.Count == 0 ? 0 : song.Notes.Max(n => n.OffTick == long.MaxValue ? n.Tick : n.OffTick);
        if (song.LoopTicks == 0) song.LoopTicks = maxOff;
        // fix MaxValue offs to loop end
        for (int k = 0; k < song.Notes.Count; k++)
            if (song.Notes[k].OffTick == long.MaxValue)
                song.Notes[k] = song.Notes[k] with { OffTick = song.LoopTicks };
    }

    static long ReadVlq(byte[] d, ref int i)
    {
        long v = 0;
        while (true)
        {
            byte b = d[i++];
            v = (v << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return v;
    }
}
