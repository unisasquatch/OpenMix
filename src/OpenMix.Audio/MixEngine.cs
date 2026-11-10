using OpenMix.Cards;

namespace OpenMix.Audio;

/// <summary>What the mix needs to know about a card part to render it in tempo and key.</summary>
/// <param name="SongDir">audio_unpacked/&lt;AudioId&gt;/&lt;AudioId&gt;</param>
/// <param name="Part">arrangement to play: "beat","bass","lead","loop" (null = default)</param>
/// <param name="KeyRoot">card's native key root 0..11 (C=0), or -1 if keyless (drums, Wild beat part)</param>
/// <param name="Minor">card's native mode, or null</param>
/// <param name="Bpm">card's native tempo</param>
/// <param name="KeyShifts">the game's per-mix-key transposition table (C..B), if the card has one</param>
/// <param name="NumBars">content-DB loop length in bars (0 = take it from the MIDI)</param>
public sealed record CardAudio(string SongDir, string? Part, int KeyRoot, bool? Minor, double Bpm, int?[]? KeyShifts = null, int NumBars = 0)
{
    /// <summary>Semitones to move this part into the mix key: the
    /// card's own table when present, else the nearest shift in -6..+6 (a tritone goes up).</summary>
    public int TransposeTo(int? mixKey)
    {
        if (KeyRoot < 0 || mixKey is not int mk) return 0;
        if (KeyShifts is { Length: 12 } t && t[((mk % 12) + 12) % 12] is int v) return v;
        return MixEngine.Transpose(KeyRoot, mk);
    }
}

/// <summary>A scheduled mix transition (Wild card or the Freestyle DropMix button).</summary>
public sealed record MixTransition(
    double TargetBpm, int? TargetKey, bool? TargetMinor,
    float[]? Stinger,                                       // one-shot layer, rendered at the current tempo
    double EndBeat,                                         // transition_end, in beats from the start
    IReadOnlyList<(double Beat, double Length)> Stutters);  // stutter_<len> markers

/// <summary>
/// The mix on a beat clock, following the original's scheduling:
///  - parts loop in phase with the master timeline; a new part starts at the NEXT BAR, except the
///    first card into an empty mix, which restarts the timeline from bar 1;
///  - key/mode changes swap every part at the next bar (re-rendered in the background first);
///  - tempo changes act immediately (varispeed) while stretched loops render and swap in;
///  - Wild transitions start at the next bar: the outgoing mix's first 2 beats are captured and
///    re-triggered at the stutter markers, tempo ramps from beat 3 to the start of the last bar,
///    old parts stop after transition bar 1, and the whole mix restarts from bar 1 in the new key;
///  - levels: +3 dB for a new card's first 8 bars; a Lead in slot 1 alternates 0/−9 dB once a master loop
///    after its first 28; slots hit by the wheel are bitcrushed + low-passed until removed.
/// Thread-safe: the audio thread calls Read; everything else may be called from any thread.
/// </summary>
public sealed class MixEngine
{
    public int SampleRate { get; }

    sealed class Voice
    {
        public required CardAudio Card;
        public int Slot;
        public float[]? Loop;
        public double LoopBeats = 128;                  // the part's loop length (Num Bars × 4)
        public double[] Pickups = Array.Empty<double>(); // `pickup` markers, in beats
        public (double bpm, int? key, bool? minor) At;
        public double StartBeat = double.MaxValue;     // master beat this part starts sounding
        public float Gain = 1f;
    }

    readonly object _lock = new();
    readonly SampleCache _cache = new();
    readonly object _cacheLock = new();
    readonly Dictionary<int, Voice> _voices = new();        // key: slot*4 + part index
    readonly Dictionary<string, Rendered> _rendered = new(StringComparer.OrdinalIgnoreCase);
    sealed record Rendered(float[] Loop, double LoopBeats, double[] Pickups);
    readonly double MasterBeats;                             // GameAudio.loopLengthInBars × 4
    readonly int MasterBars;
    const double LookAhead = 100.0 / 480;                    // the original's audio's 100-tick look-ahead
    readonly List<(float[] data, double pos, bool bus, string? tag)> _shots = new();     // bus = through bus:/CardMusic (transition stingers)
    readonly Dictionary<int, float> _slotGain = new();

    readonly double _startBpm;                               // GameAudio.startingTempo
    double _renderedBpm; int? _rKey; bool? _rMinor;          // what Read plays
    double _tBpm; int? _tKey; bool? _tMinor;                 // what we want
    bool _hasMaster;
    double _playBpm, _beat;                                  // clock
    MixTransition? _tr; double _trStart, _trFromBpm; bool _trActive, _muteUntilSwap;
    readonly SemaphoreSlim _signal = new(0);
    Dictionary<int, Rendered>? _pending; (double, int?, bool?) _pendingAt; bool _pendingReady; double _swapAt = -1;

    /// <summary>Needs the imported GameAudio settings (<see cref="GameTables.Audio"/>); throws without them.</summary>
    public MixEngine(int sampleRate = 24000)
    {
        var a = GameTables.Audio ?? throw new InvalidOperationException($"the game's audio settings aren't imported ({GameTables.Error}); run setup again");
        MasterBars = a.LoopLengthInBars; MasterBeats = MasterBars * 4.0;
        _startBpm = _renderedBpm = _tBpm = _playBpm = a.StartingTempo;
        SampleRate = sampleRate; _bus = new MixBus(sampleRate);
        _chains = Enumerable.Range(0, 5).Select(_ => new SlotChain(sampleRate)).ToArray();
        _ = Task.Run(Worker);
    }

    // ---------------- state ----------------
    public double? MasterBpm { get { lock (_lock) return _hasMaster ? _tBpm : null; } }
    public int? MasterKey { get { lock (_lock) return _tKey; } }
    public bool? MasterMinor { get { lock (_lock) return _tMinor; } }
    public double PlayBpm { get { lock (_lock) return _playBpm; } }
    public double BeatPosition { get { lock (_lock) return _beat; } }
    public bool IsTransitioning { get { lock (_lock) return _trActive; } }
    public bool IsEmpty { get { lock (_lock) return _voices.Count == 0; } }
    /// <summary>True once a part in this slot has reached its start and is audible (for the board lights).</summary>
    public bool SlotSounding(int slot) { lock (_lock) return !_muteUntilSwap && _voices.Values.Any(v => v.Slot == slot && v.Loop != null && _beat >= v.StartBeat); }
    /// <summary>Output buffer latency in seconds (MixPlayer's WaveOut DesiredLatency).</summary>
    public double OutputLatency { get; set; } = 0.15;
    public IReadOnlyCollection<int> ActiveSlots { get { lock (_lock) return _voices.Values.Select(v => v.Slot).Distinct().ToArray(); } }
    public int MasterLoopFrames { get { lock (_lock) return _voices.Values.Where(v => v.Loop != null).Select(v => v.Loop!.Length / 2).DefaultIfEmpty(0).Max(); } }

    /// <summary>Semitones from cardKey to masterKey, nearest in -6..+6 (tritone = +6).</summary>
    public static int Transpose(int cardKey, int? masterKey)
    {
        if (cardKey < 0 || masterKey is not int mk) return 0;
        int d = ((mk - cardKey) % 12 + 12) % 12;
        return d > 6 ? d - 12 : d;
    }

    double NextBar() => Math.Floor(_beat / 4 + 1e-9) * 4 + (_beat % 4 < 1e-6 ? 0 : 4);

    // ---------------- control ----------------

    public void SetMaster(double bpm, int? key, bool? minor)
    {
        lock (_lock)
        {
            _tBpm = bpm; _tKey = key; _tMinor = minor; _hasMaster = true;
            // Don't speed up loops rendered at the old tempo (that would shift their pitch): the new tempo
            // takes effect when the re-stretched loops swap in. Only an empty/unrendered mix changes now.
            if (!_trActive && !_voices.Values.Any(v => v.Loop != null)) _playBpm = bpm;
            InvalidatePending();
        }
        _signal.Release();
    }

    /// <summary>The first card into an empty mix restarts the timeline from bar 1.</summary>
    public void RestartTimeline() { lock (_lock) _beat = 0; }

    public void SetCard(int slot, CardAudio card) => SetCard(slot, new[] { card });

    /// <summary>Put a card in a slot; a Wild in a two-colour slot passes both parts.</summary>
    public void SetCard(int slot, IReadOnlyList<CardAudio> parts)
    {
        lock (_lock)
        {
            if (!_hasMaster && parts.Count > 0) { _tBpm = _playBpm = _renderedBpm = parts[0].Bpm > 0 ? parts[0].Bpm : _startBpm; _hasMaster = true; }
            foreach (var k in _voices.Keys.Where(k => k / 4 == slot).ToList()) _voices.Remove(k);
            for (int i = 0; i < parts.Count && i < 4; i++) _voices[slot * 4 + i] = new Voice { Card = parts[i], Slot = slot };
            if (slot == 0) _duckOn = false;                            // a new lead starts its bar count over
            InvalidatePending();
        }
        _signal.Release();
    }

    public void SetCard(int slot, string songDir, double gain = 1.0)
    {
        var song = MidiSong.Load(songDir);
        SetCard(slot, new CardAudio(songDir, null, -1, null, song.Bpm));
    }

    /// <summary>Settings → MUSIC VOLUME (0..1), applied to every card part.</summary>
    public float MusicGain { get; set; } = 1f;

    // per-slot loudness meter for the board lights (sum of squares since the last Take)
    readonly double[] _lvlSum = new double[5]; readonly double[] _lvlPeak = new double[5]; long _lvlFrames;

    /// <summary>RMS and peak of each slot's output (after slot gain) since the last call, then reset.</summary>
    public (float[] rms, float[] peak) TakeSlotLevels()
    {
        lock (_lock)
        {
            var rms = new float[5]; var peak = new float[5];
            for (int i = 0; i < 5; i++)
            {
                rms[i] = _lvlFrames > 0 ? (float)Math.Sqrt(_lvlSum[i] / (2.0 * _lvlFrames)) : 0;
                peak[i] = (float)_lvlPeak[i]; _lvlSum[i] = 0; _lvlPeak[i] = 0;
            }
            _lvlFrames = 0;
            return (rms, peak);
        }
    }

    public void SetSlotGainDb(int slot, double db) { lock (_lock) _slotGain[slot] = (float)Math.Pow(10, db / 20); }
    /// <summary>SlotVolumeControl marks a slot "manual" once the player sets it: auto-ducking leaves it alone.</summary>
    public void SetSlotManual(int slot, bool manual) { lock (_lock) if ((uint)slot < 5) _slotManual[slot] = manual; }
    readonly bool[] _slotManual = new bool[5];

    // ---- lead auto-duck (§3.3): crossing master bar loopLengthInBars−2, a slot-1 Lead that has played
    // ≥ loopLengthInBars−4 bars toggles 0 ↔ -9 dB with a 2 s ramp (the dB and ramp are the original's code constants) ----
    bool _duckOn; double _duckDb; float _duckGain = 1; long _lastMasterBar = -1;
    void LeadDuckTick()
    {
        long mb = (long)Math.Floor(_beat / 4);
        if (mb != _lastMasterBar)
        {
            if (_lastMasterBar >= 0 && ((mb % MasterBars) + MasterBars) % MasterBars == MasterBars - 2 && !_slotManual[0])
            {
                var lead = _voices.Values.FirstOrDefault(v => v.Slot == 0 && v.Card.Part == "lead" && v.Loop != null);
                if (lead != null && (_beat - lead.StartBeat) / 4 >= MasterBars - 4) _duckOn = !_duckOn;
            }
            _lastMasterBar = mb;
        }
        if (_slotManual[0]) _duckOn = false;
        double target = _duckOn ? -9 : 0;
        if (_duckDb != target)
        {
            double stepDb = 9.0 / (2 * SampleRate);
            _duckDb = _duckDb < target ? Math.Min(target, _duckDb + stepDb) : Math.Max(target, _duckDb - stepDb);
            _duckGain = (float)Math.Pow(10, _duckDb / 20);
        }
    }

    /// <summary>Wheel/FX "silenced" slot: the imported silence effect until the card is removed.</summary>
    public void SetSlotSilenced(int slot, bool on)
    {   // ClashConfig.silenceMusicEffect (imported) on the slot's DSP until the card is removed
        if ((uint)slot >= 5) return;
        lock (_lock)
        {
            if (on && _silId[slot] == 0 && GameTables.SilenceMusicEffect is string sil && SlotFxPresets.All.TryGetValue(sil, out var pr))
                _silId[slot] = AddSlotEffectUnlocked(slot, pr.fx);
            else if (!on && _silId[slot] != 0) { RemoveSlotEffectUnlocked(_silId[slot]); _silId[slot] = 0; }
        }
    }

    // ---- per-slot music effects (SlotFx.cs) ----
    SlotChain[] _chains = null!;
    readonly int[] _silId = new int[5];
    readonly double[] _fsL = new double[5], _fsR = new double[5];
    int _fxSeq;
    /// <summary>Activate an effect on a slot; returns its id. Delay time is fixed from the tempo now (60/bpm × beatDelay).</summary>
    public int AddSlotEffect(int slot, SlotFxParams fx) { lock (_lock) return AddSlotEffectUnlocked(slot, fx); }
    public void RemoveSlotEffect(int id) { lock (_lock) RemoveSlotEffectUnlocked(id); }
    /// <summary>CardPlayer.SetEffects(0): a new card on the slot drops every effect on it (FxSilenced is re-set by the caller).</summary>
    public void ClearSlotEffects(int slot)
    {
        if ((uint)slot >= 5) return;
        lock (_lock) { _chains[slot].Active.Clear(); _chains[slot].Recompute(); _silId[slot] = 0; }
    }
    public bool SlotHasEffects(int slot) { lock (_lock) return (uint)slot < 5 && _chains[slot].Any; }
    int AddSlotEffectUnlocked(int slot, SlotFxParams fx)
    {
        int id = ++_fxSeq;
        double ds = fx.Delay is var (bd, _, _, _) ? 60.0 / Math.Max(1, _playBpm) * bd : 0;
        _chains[slot].Active.Add((id, fx, ds)); _chains[slot].Recompute();
        return id;
    }
    void RemoveSlotEffectUnlocked(int id)
    {
        foreach (var c in _chains) if (c.Active.RemoveAll(x => x.id == id) > 0) c.Recompute();
    }

    public void ClearCard(int slot)
    {
        lock (_lock) { foreach (var k in _voices.Keys.Where(k => k / 4 == slot).ToList()) _voices.Remove(k); InvalidatePending(); }
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _voices.Clear(); _shots.Clear(); _tr = null; _trActive = false; _muteUntilSwap = false; _beat = 0; _hasMaster = false; InvalidatePending();
            foreach (var c in _chains) { c.Active.Clear(); c.Recompute(); } Array.Clear(_silId);
        }
    }

    /// <summary>Begin a transition at the next bar.</summary>
    public void StartTransition(MixTransition t)
    {
        lock (_lock)
        {
            _tr = t; _trActive = true;
            _trStart = NextBar();
            _trFromBpm = _playBpm;
            _tBpm = t.TargetBpm; _tKey = t.TargetKey ?? _tKey; _tMinor = t.TargetMinor ?? _tMinor; _hasMaster = true;
            if (t.Stinger != null) _shots.Add((t.Stinger, -(_trStart - _beat) * 60.0 / _playBpm * SampleRate, true, null));
            InvalidatePending();
        }
        _signal.Release();
    }

    /// <summary>Layer a one-shot (interleaved stereo at <see cref="SampleRate"/>) over the mix now.</summary>
    public void PlayOneShot(float[] stereo) { lock (_lock) _shots.Add((stereo, 0, false, null)); }

    /// <summary>Beats from now to the next multiple of div beats (0.25 = 16th, 1 = beat, 4 = bar).</summary>
    public double BeatsToNext(double div)
    {
        lock (_lock) { double n = Math.Ceiling(_beat / div - 1e-9) * div; return Math.Max(0, n - _beat); }
    }

    /// <summary>Layer a one-shot on the master bus (SFX / QueueStingers) after delayBeats.</summary>
    public void PlayOneShotAt(float[] stereo, double delayBeats, string? tag = null)
    {
        lock (_lock) _shots.Add((stereo, -delayBeats * 60.0 / Math.Max(1, _playBpm) * SampleRate, false, tag));
    }

    /// <summary>Layer a one-shot that starts at an absolute master beat. If the render arrives after that
    /// beat, it joins part-way in, so it stays in sync instead of starting late.</summary>
    public void PlayOneShotAtBeat(float[] stereo, double beat, string? tag = null)
    {
        lock (_lock) _shots.Add((stereo, (_beat - beat) * 60.0 / Math.Max(1, _playBpm) * SampleRate, false, tag));
    }

    /// <summary>A one-shot on the master bus after <paramref name="seconds"/> (announcer / crowd timelines).</summary>
    public void PlayOneShotIn(float[] stereo, double seconds, string? tag = null)
    { lock (_lock) _shots.Add((stereo, -seconds * SampleRate, false, tag)); }

    // looping beds (Crowd/amb_loop, crowd_loop_main tracks, chant): master bus, smoothed gain
    sealed class Loop { public required float[] Data; public int Start, End; public double Pos; public float Gain, Target; }
    readonly Dictionary<string, Loop> _loops = new();
    public void SetLoop(string id, float[] stereo, double loopStartSec, double loopEndSec, float gain, bool fadeIn = true)
    {
        lock (_lock)
        {
            int frames = stereo.Length / 2;
            int st = (int)Math.Clamp(loopStartSec * SampleRate, 0, frames - 1), en = (int)Math.Clamp(loopEndSec * SampleRate, st + 1, frames);
            if (_loops.TryGetValue(id, out var cur) && ReferenceEquals(cur.Data, stereo)) { cur.Target = gain; return; }
            _loops[id] = new Loop { Data = stereo, Start = st, End = en, Gain = fadeIn ? 0 : gain, Target = gain };
        }
    }
    public void SetLoopGain(string id, float gain) { lock (_lock) if (_loops.TryGetValue(id, out var l)) l.Target = gain; }
    public void StopLoop(string id) { lock (_lock) if (_loops.TryGetValue(id, out var l)) l.Target = -1; }   // fade out, then drop
    public bool HasLoop(string id) { lock (_lock) return _loops.ContainsKey(id); }

    /// <summary>Stop (and cancel pending) one-shots with this tag, e.g. spin-up when spin-down starts.</summary>
    public void StopShots(string tag) { lock (_lock) _shots.RemoveAll(x => x.tag == tag); }

    // card-music fades (the original's audio slot volumes at game over): scheduled at a master beat, linear in dB
    readonly List<(double beat, double db, double secs)> _fades = new();
    double _fadeDb, _fadeFrom, _fadeTo, _fadeLeft, _fadeLen;
    public void ScheduleMusicFade(double inBeats, double toDb, double seconds)
    { lock (_lock) { _fades.Add((_beat + inBeats, toDb, seconds)); _fades.Sort((a, b) => a.beat.CompareTo(b.beat)); } }
    float MusicFadeGain()
    {
        while (_fades.Count > 0 && _beat >= _fades[0].beat)
        {
            (_, _fadeTo, _fadeLen) = _fades[0]; _fades.RemoveAt(0);
            _fadeFrom = _fadeDb; _fadeLeft = Math.Max(1, _fadeLen * SampleRate); _fadeLen = _fadeLeft;
        }
        if (_fadeLeft > 0) { _fadeLeft--; _fadeDb = _fadeTo + (_fadeFrom - _fadeTo) * (_fadeLeft / _fadeLen); }
        return _fadeDb == 0 ? 1f : (float)Math.Pow(10, _fadeDb / 20);
    }

    /// <summary>One note-on through a Fusion patch, no note-off (samples ring to their end): every zone the
    /// patch's layer mode selects, pitched by note minus root unless unpitched, zone + patch volume.</summary>
    public float[] RenderPatchNote(string fusionPath, int note, int velocity = 127)
    {
        var patch = FusionPatch.Load(fusionPath);
        var zones = patch.SelectZones(note, velocity, Random.Shared);
        var parts = new List<float[]>();
        lock (_cacheLock)
            foreach (var z in zones)
            {
                PcmSample smp;
                try { smp = _cache.Load(z.SamplePath); } catch { continue; }
                double semis = (z.Unpitched ? 0 : note - z.RootNote) + z.FineTuneCents / 100.0;
                double step = smp.SampleRate / (double)SampleRate * Math.Pow(2, semis / 12.0);
                double gain = Math.Pow(10, (z.VolumeDb + patch.VolumeDb) / 20.0) * (z.VelocityToVolume ? velocity / 127.0 : 1);
                int n = (int)(smp.Frames / step);
                var buf = new float[n * 2];
                for (int f = 0; f < n; f++)
                {
                    double sp = f * step; int i = (int)sp; double t = sp - i;
                    float l0 = 0, r0 = 0, l1 = 0, r1 = 0;
                    smp.MixFrame(i, gain, z.PanRight, ref l0, ref r0); smp.MixFrame(i + 1, gain, z.PanRight, ref l1, ref r1);
                    buf[f * 2] = (float)(l0 + (l1 - l0) * t); buf[f * 2 + 1] = (float)(r0 + (r1 - r0) * t);
                }
                parts.Add(buf);
            }
        var outBuf = new float[parts.Count == 0 ? 0 : parts.Max(b => b.Length)];
        foreach (var b in parts) for (int i = 0; i < b.Length; i++) outBuf[i] += b[i];
        return outBuf;
    }

    public PcmSample LoadProcessed(string path, double semitones) { lock (_cacheLock) return _cache.Processed(path, SampleRate, 1, semitones); }

    public (float[] data, MidiSong song) RenderStinger(string dir, double bpm, string? variant = null, int transpose = 0)
    {
        var song = MidiSong.Load(dir, null, null, variant);
        float[] data;
        lock (_cacheLock) data = CardSynth.Render(song, _cache, SampleRate, out _, bpm, transpose, wrap: false);
        return (data, song);
    }

    void InvalidatePending() { _pending = null; _pendingReady = false; _swapAt = -1; }

    // ---------------- background rendering ----------------

    async Task Worker()
    {
        while (true)
        {
            await _signal.WaitAsync();
            while (_signal.CurrentCount > 0) await _signal.WaitAsync();
            for (int guard = 0; guard < 8; guard++)
            {
                (double bpm, int? key, bool? minor) target; List<(int id, Voice v)> todo; bool swapAll;
                lock (_lock)
                {
                    target = (_tBpm, _tKey, _tMinor);
                    bool anyPlaying = _voices.Values.Any(v => v.Loop != null);
                    swapAll = anyPlaying && (_renderedBpm, _rKey, _rMinor) != target;
                    if (!anyPlaying) { _renderedBpm = target.bpm; _rKey = target.key; _rMinor = target.minor; }
                    todo = _voices.Where(kv => kv.Value.Loop == null || kv.Value.At != target).Select(kv => (kv.Key, kv.Value)).ToList();
                }
                if (todo.Count == 0) break;

                var results = new Dictionary<int, Rendered>();
                foreach (var (id, v) in todo)
                {
                    try { results[id] = RenderFor(v.Card, target, v.Slot == 0); }
                    catch (Exception ex) { Console.WriteLine($"mix: render failed for {Path.GetFileName(v.Card.SongDir)}: {ex.Message}"); results[id] = new Rendered(new float[2], 4, Array.Empty<double>()); }
                }

                lock (_lock)
                {
                    if ((_tBpm, _tKey, _tMinor) != target) continue;
                    if (!swapAll)
                    {
                        // same tempo/key: new parts join at their pickup or the next bar; the first
                        // card into an empty mix restarts the timeline (from its last-bar pickup if any)
                        bool anyPlaying = _voices.Values.Any(v => v.Loop != null);
                        var joined = new List<Voice>();
                        foreach (var (id, rr) in results)
                            if (_voices.TryGetValue(id, out var v) && todo.Any(t => t.id == id && ReferenceEquals(t.v, v)))
                            { v.Loop = rr.Loop; v.LoopBeats = rr.LoopBeats; v.Pickups = rr.Pickups; v.At = target; joined.Add(v); }
                        if (anyPlaying) foreach (var v in joined) v.StartBeat = JoinBeat(v);
                        else
                        {
                            var starts = joined.Select(FirstCardStart).ToList();
                            double first = starts.Count > 0 ? starts.Min() : 0;
                            for (int k = 0; k < joined.Count; k++) joined[k].StartBeat = first == 0 ? 0 : starts[k];
                            _beat = first;
                        }
                        foreach (var v in joined) Boosted(v);
                        _renderedBpm = target.bpm; _rKey = target.key; _rMinor = target.minor;
                        if (!_trActive) _playBpm = target.bpm;
                    }
                    else
                    {
                        _pending = results; _pendingAt = target; _pendingReady = true;
                        // key/mode change: swap on the next bar (the original's audio); tempo only:
                        // swap now — parts are phase-locked to the beat, so nothing jumps
                        bool keyChange = (_rKey, _rMinor) != (target.key, target.minor);
                        if (!_trActive) _swapAt = keyChange ? NextBar() : _beat;
                    }
                }
            }
        }
    }

    // call under lock
    void SwapPending(bool restart)
    {
        if (!_pendingReady || _pending == null) return;
        foreach (var (id, rr) in _pending)
            if (_voices.TryGetValue(id, out var v))
            {
                bool isNew = v.Loop == null;
                v.Loop = rr.Loop; v.LoopBeats = rr.LoopBeats; v.Pickups = rr.Pickups; v.At = _pendingAt;
                if (restart || isNew) v.StartBeat = restart ? 0 : Math.Min(v.StartBeat, _beat);
                if (isNew && !restart) Boosted(v);
            }
        (_renderedBpm, _rKey, _rMinor) = _pendingAt;
        _pending = null; _pendingReady = false; _swapAt = -1; _muteUntilSwap = false;
        if (!_trActive) _playBpm = _renderedBpm;
        if (_voices.Values.Any(v => v.Loop == null || v.At != (_tBpm, _tKey, _tMinor))) _signal.Release();
    }

    // call under lock. Look one bar ahead (from now + 100 ticks) in the part's
    // own loop for a `pickup` marker; if found the part starts there (before the downbeat), else at the next bar.
    double JoinBeat(Voice v)
    {
        double pos = _beat + LookAhead, lb = v.LoopBeats;
        double inPart = ((pos % lb) + lb) % lb;
        double best = double.MaxValue;
        foreach (double p in v.Pickups)
            foreach (double q in new[] { p, p + lb })                       // window may cross the loop end
                if (q >= inPart && q < inPart + 4) best = Math.Min(best, pos - inPart + q);
        return best < double.MaxValue ? best : NextBar();
    }

    // Empty mix: the game searches the part's LAST bar (master bar 31) for a pickup and restarts the
    // timeline just before it, so the first card's anacrusis is heard. 0 = start at bar 1.
    double FirstCardStart(Voice v)
    {
        double pos = MasterBeats - 4, inPart = pos % v.LoopBeats;
        double best = double.MaxValue;
        foreach (double p in v.Pickups) if (p >= inPart && p < inPart + 4) best = Math.Min(best, pos - inPart + p);
        return best < double.MaxValue ? best : 0;
    }

    Rendered RenderFor(CardAudio card, (double bpm, int? key, bool? minor) at, bool slot0 = false)
    {
        bool? minor = at.minor ?? card.Minor;
        string variant = MidiSong.PickVariant(card.SongDir, card.Part, minor);
        int tr = card.TransposeTo(at.key);
        string cacheKey = $"{card.SongDir}|{variant}|{at.bpm:0.00}|{tr}|{slot0}";
        lock (_cacheLock)
        {
            if (_rendered.TryGetValue(cacheKey, out var hit)) return hit;
            var song = MidiSong.Load(card.SongDir, card.Part, minor);
            song.FitLoop(card.NumBars);
            var loop = CardSynth.Render(song, _cache, SampleRate, out _, at.bpm, tr, allowFormant: slot0);   // dontAllowMaintainFormant = slot != 0
            var r = new Rendered(loop, song.LoopBars * 4.0, song.PickupBeats.Where(b => b < song.LoopBars * 4.0).Distinct().OrderBy(b => b).ToArray());
            if (_rendered.Count > 400) _rendered.Clear();
            _rendered[cacheKey] = r;
            return r;
        }
    }

    // ---------------- audio thread ----------------

    static readonly float Boost = (float)Math.Pow(10, 3 / 20.0), Duck = (float)Math.Pow(10, -9 / 20.0);
    readonly MixBus _bus;
    // New-play boost (the original's audio): only the newest card's slot, +3 dB ramped over 1 s from when
    // its parts start, held > 8 bars, then back to 0 dB over 1 s. Not re-triggered by transitions/replays.
    int _boostSlot = -1; double _boostBeat;
    /// <summary>False while the Freestyle key/tempo panel is open (StudioModeControls.IsOpen).</summary>
    public bool BoostEnabled { get; set; } = true;
    void Boosted(Voice v) { if (BoostEnabled) { _boostSlot = v.Slot; _boostBeat = v.StartBeat; } }
    float BoostGain(int slot)
    {
        if (slot != _boostSlot) return 1f;
        double secs = (_beat - _boostBeat) * 60 / Math.Max(1, _playBpm), bars = (_beat - _boostBeat) / 4;
        double up = Math.Clamp(secs, 0, 1);
        double down = bars <= 8 ? 1 : Math.Clamp(1 - (bars - 8) * 4 * 60 / Math.Max(1, _playBpm), 0, 1);
        if (down <= 0) { _boostSlot = -1; return 1f; }
        return (float)(1 + (Boost - 1) * Math.Min(up, down));
    }

    public void Read(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            double fpb = SampleRate * 60.0 / _renderedBpm;
            for (int i = 0; i < count; i += 2)
            {
                double l = 0, r = 0, l0 = 0, r0 = 0, xl = 0, xr = 0;   // l/r: card bus; l0/r0: slot 1; xl/xr: SFX → master
                if (_voices.Count > 0 || _shots.Count > 0)
                {
                    if (_swapAt >= 0 && _beat >= _swapAt && !_trActive) { SwapPending(false); fpb = SampleRate * 60.0 / _renderedBpm; }

                    double b = _beat; bool partsOn = !_muteUntilSwap;
                    if (_trActive && _tr != null && _beat >= _trStart)
                    {
                        double rel = _beat - _trStart;
                        double rampEnd = Math.Max(3.25, _tr.EndBeat - 4);
                        if (rel >= 3) _playBpm = rel >= rampEnd ? _tr.TargetBpm : _trFromBpm + (_tr.TargetBpm - _trFromBpm) * ((rel - 3) / (rampEnd - 3));
                        // stutter: the outgoing mix's first 2 beats, re-triggered at each stutter marker (≥ 2 beats)
                        bool stuttering = false;
                        foreach (var (sb, len) in _tr.Stutters)
                            if (sb >= 2 && rel >= sb && rel < sb + len) { b = _trStart + Math.Min(rel - sb, 2 - 1e-6); stuttering = true; }
                        if (!stuttering && rel >= 8 && rel < _tr.EndBeat) partsOn = false;     // old parts stopped after bar 1
                        if (rel >= _tr.EndBeat)
                        {   // transition end: the whole mix restarts from bar 1 in the new key/tempo
                            _trActive = false; _tr = null; _beat = 0; b = 0; _playBpm = _tBpm;
                            if (_pendingReady) SwapPending(true); else { _muteUntilSwap = true; partsOn = false; }
                            foreach (var v in _voices.Values) if (v.Loop != null) v.StartBeat = 0;
                            fpb = SampleRate * 60.0 / _renderedBpm;
                        }
                    }

                    float fade = MusicFadeGain();
                    LeadDuckTick();
                    if (partsOn)
                    {
                        foreach (var v in _voices.Values)
                        {
                            var loop = v.Loop; if (loop == null || _beat < v.StartBeat) continue;
                            int frames = loop.Length / 2;
                            double lb = v.LoopBeats, pb = ((b % lb) + lb) % lb;             // phase-locked to the master beat
                            double p = Math.Min(pb * fpb, frames - 1);
                            // at the rendered tempo the read head moves exactly 1 frame per frame: take the nearest
                            // sample (linear interpolation at a fixed fractional offset is a low-pass, up to −8 dB
                            // near 12 kHz); during tempo ramps use 4-point Hermite
                            bool exact = Math.Abs(_playBpm - _renderedBpm) < 1e-6 && b == _beat;
                            float g = v.Gain * MusicGain * (_slotGain.TryGetValue(v.Slot, out var sg) ? sg : 1f);
                            double played = (_beat - v.StartBeat) / 4;                     // bars since this part started
                            g *= BoostGain(v.Slot) * fade;
                            if (v.Slot == 0) g *= _duckGain;
                            float sl, sr;
                            if (exact) { int k = (int)Math.Round(p) % frames; sl = loop[k * 2] * g; sr = loop[k * 2 + 1] * g; }
                            else { sl = (float)Hermite(loop, frames, p, 0) * g; sr = (float)Hermite(loop, frames, p, 1) * g; }
                            if ((uint)v.Slot < 5) { _fsL[v.Slot] += sl; _fsR[v.Slot] += sr; }
                            else { l += sl; r += sr; }
                        }
                    }
                    // each slot through its DSP (Hmx.SoundClashSlot), then the slot meter, then the buses
                    for (int s = 0; s < 5; s++)
                    {
                        float sl = (float)_fsL[s], sr = (float)_fsR[s]; _fsL[s] = _fsR[s] = 0;
                        if (_chains[s].Any) _chains[s].Process(ref sl, ref sr, b);
                        _lvlSum[s] += sl * sl + sr * sr;
                        double pk = Math.Max(Math.Abs(sl), Math.Abs(sr)); if (pk > _lvlPeak[s]) _lvlPeak[s] = pk;
                        if (s == 0) { l0 += sl; r0 += sr; } else { l += sl; r += sr; }
                    }
                    for (int s = _shots.Count - 1; s >= 0; s--)
                    {
                        var (data, pos, bus, tag) = _shots[s];
                        int k = (int)pos;
                        if (k >= 0 && k * 2 + 1 < data.Length)
                        { if (bus) { l += data[k * 2]; r += data[k * 2 + 1]; } else { xl += data[k * 2]; xr += data[k * 2 + 1]; } }
                        if (k * 2 + 1 >= data.Length) _shots.RemoveAt(s); else _shots[s] = (data, pos + 1, bus, tag);
                    }
                    _beat += _playBpm / 60.0 / SampleRate;
                    _lvlFrames++;
                }
                if (_loops.Count > 0)
                {
                    List<string>? done = null;
                    foreach (var (id, lp) in _loops)
                    {
                        float target = Math.Max(0, lp.Target);
                        lp.Gain += (target - lp.Gain) * 0.0004f;                      // ~100 ms smoothing at 24 kHz
                        if (lp.Target < 0 && lp.Gain < 0.0005f) { (done ??= new()).Add(id); continue; }
                        int k = (int)lp.Pos;
                        xl += lp.Data[k * 2] * lp.Gain; xr += lp.Data[k * 2 + 1] * lp.Gain;
                        lp.Pos += 1; if (lp.Pos >= lp.End) lp.Pos = lp.Start;
                    }
                    if (done != null) foreach (var id in done) _loops.Remove(id);
                }
                // FMOD chain (MixBus): cardSlot1 effects → bus:/CardMusic → master compressor + limiter
                _bus.Slot1(ref l0, ref r0);
                l += l0; r += r0;
                _bus.CardMusic(ref l, ref r);
                l += xl; r += xr;
                _bus.Master(ref l, ref r);
                buffer[offset + i] = (float)Math.Clamp(l, -1, 1);
                buffer[offset + i + 1] = (float)Math.Clamp(r, -1, 1);
            }
        }
    }

    /// <summary>4-point, 3rd-order Hermite interpolation on an interleaved stereo loop (wrapping).</summary>
    internal static double Hermite(float[] d, int frames, double pos, int ch)
    {
        int i = (int)Math.Floor(pos); double t = pos - i;
        double xm1 = d[((i - 1 + frames) % frames) * 2 + ch], x0 = d[(i % frames) * 2 + ch];
        double x1 = d[((i + 1) % frames) * 2 + ch], x2 = d[((i + 2) % frames) * 2 + ch];
        double c1 = 0.5 * (x1 - xm1), c2 = xm1 - 2.5 * x0 + 2 * x1 - 0.5 * x2, c3 = 0.5 * (x2 - xm1) + 1.5 * (x0 - x1);
        return ((c3 * t + c2) * t + c1) * t + x0;
    }

    public float[] RenderMix(double seconds)
    {
        WaitIdle();
        int frames = (int)(seconds * SampleRate);
        var buf = new float[frames * 2];
        const int chunk = 4096;
        for (int o = 0; o < buf.Length; o += chunk) Read(buf, o, Math.Min(chunk, buf.Length - o));
        return buf;
    }

    /// <summary>Block until every part is rendered at the current target (tests/offline use).</summary>
    public void WaitIdle(int timeoutMs = 120000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            lock (_lock)
                if (_pendingReady || _voices.Values.All(v => v.Loop != null && v.At == (_tBpm, _tKey, _tMinor))) return;
            Thread.Sleep(20);
        }
    }
}
