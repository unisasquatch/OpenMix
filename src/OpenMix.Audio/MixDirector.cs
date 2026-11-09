using OpenMix.Cards;

namespace OpenMix.Audio;

/// <summary>
/// The original's music rules (, the original's audio):
///  1. the first keyed card sets the key/mode when no other playing card has a key;
///  2. the first card into an empty mix sets the tempo (and restarts the timeline);
///  3. a Wild played into a non-empty mix plays its transition: tempo ramps from beat 3 to the
///     transition_end bar, the mix stutters on the stinger's stutter markers, and key/mode/tempo
///     switch to the Wild's at transition_end;
///  4. removing cards never changes key or tempo;
///  5. FX "music effects" change key, tempo (within GameAudio's min/max tempo) or flip major/minor — and persist;
///  6. outside Freestyle, tempo creeps (GameAudio.accelerateTempo*: +amount every few bars up to a floor);
///  7. while the Freestyle panel is open, cards don't change key/tempo (the "gate").
/// The Freestyle DropMix button triggers a random transition (new key, random mode, tempo ≥20 BPM away).
/// </summary>
public sealed class MixDirector
{
    readonly MixEngine _mix;
    readonly string _audioDir;
    readonly Random _rng = new();
    readonly object _lock = new();
    readonly Card?[] _slots = new Card?[5];

    readonly GameAudioSettings _a;                         // imported GameAudio settings
    double _tempo; int? _key; bool? _minor;                // master (starts at GameAudio.startingTempo)
    string? _lastWild;
    int _lastBar = -1;

    public bool StudioOpen { get; private set; }            // Freestyle tempo/key panel
    public bool PinMaster { get; set; }                     // mix playback: keep the saved tempo/key, cards never set them
    public bool TempoCreep { get; set; }                    // true outside Freestyle
    /// <summary>Lanes Finale: Wild transitions are suppressed and tempo climbs +1 BPM a bar to the maximum tempo.</summary>
    public bool Finale { get; set; }
    public double SfxGain { get; set; } = 0.6;
    public double MinTempo => _a.MinTempo;
    public double MaxTempo => _a.MaxTempo;
    double CreepFloor => _a.AccelerateTempoFloor;
    int LoopBars => _a.LoopLengthInBars;

    /// <summary>Needs the imported GameAudio settings (<see cref="GameTables.Audio"/>); throws without them.</summary>
    public MixDirector(MixEngine mix, string audioDir)
    {
        _a = GameTables.Audio ?? throw new InvalidOperationException($"the game's audio settings aren't imported ({GameTables.Error}); run setup again");
        _mix = mix; _audioDir = audioDir; _tempo = _a.StartingTempo;
    }

    public event Action<string>? Log;
    void L(string s) => Log?.Invoke(s);

    // ------------------------------------------------------------------ cards

    public void OnSlot(int pad, Card? card, string? part) => OnSlot(pad, card, new[] { part }, false);

    /// <param name="parts">the arrangement(s) this card plays here — two for a Wild in a two-colour slot</param>
    /// <param name="silenced">slot hit by the wheel/FX: bitcrushed + low-passed until removed</param>
    public void OnSlot(int pad, Card? card, IReadOnlyList<string?> parts, bool silenced)
    {
        lock (_lock)
        {
            if (card == null)
            {
                _slots[pad] = null; _mix.ClearCard(pad); _slotBeat[pad] = false;
                EndSlotFx(pad, null); _mix.SetSlotSilenced(pad, false);
                if (_slots.All(c => c == null)) { _mix.ClearAll(); _fx.Clear(); _clapOn = false; _lastWild = null; L("mix: board empty"); }
                else UpdateClap();
                return;
            }
            if (ReferenceEquals(_slots[pad], card)) { _mix.SetSlotSilenced(pad, silenced); return; }
            EndSlotFx(pad, card?.Cid);                                     // CardPlayer.SetEffects(0) + controllers bound to it
            string? part = parts.FirstOrDefault(p => p != null && p != "beat") ?? parts.FirstOrDefault();
            bool hadOthers = _slots.Where((c, i) => i != pad && c != null).Any();
            bool othersKeyed = _slots.Where((c, i) => i != pad && c != null).Any(c => Keyed(c!, null));
            _slots[pad] = card; _slotBeat[pad] = parts.Contains("beat");

            string dir = Path.Combine(_audioDir, card.AudioId, card.AudioId);
            bool hasAudio = !string.IsNullOrEmpty(card.AudioId) && Directory.Exists(dir);
            bool gate = !StudioOpen && !PinMaster;
            bool keyed = Keyed(card, part);

            if (gate && !Finale && card.Type == CardType.Wild && hadOthers && !_mix.IsTransitioning
                && !(card.Cid == _lastWild && _tempo == card.Tempo && _key == KeyOf(card) && _minor == MinorOf(card)))
            {
                WildTransition(card);
            }
            else
            {
                bool changed = false;
                if (gate && !hadOthers) { _tempo = card.Tempo > 0 ? card.Tempo : _tempo; _mix.RestartTimeline(); changed = true; }
                if (gate && keyed && !othersKeyed) { _key = KeyOf(card); _minor = MinorOf(card) ?? _minor ?? false; changed = true; }
                if (changed) Apply($"{card.Name} sets the mix");
                if (PinMaster && !hadOthers) { _mix.RestartTimeline(); Apply("mix playback"); }   // the empty board dropped the master
            }

            // a saved mix already holds its FX tempo/key changes; playing it back must not apply them twice
            if (card.Type == CardType.Fx && !string.IsNullOrEmpty(card.MusicEffect) && !PinMaster) MusicEffect(card.MusicEffect, card.Name);

            if (!hasAudio) { _mix.ClearCard(pad); L($"mix: no audio for {card.Name}"); return; }
            var audios = parts.Select(pt =>
            {
                bool k = Keyed(card, pt);
                return new CardAudio(dir, pt, k ? KeyOf(card) ?? -1 : -1, k ? MinorOf(card) : null, card.Tempo, card.KeyShifts, card.NumBars);
            }).ToList();
            _mix.SetCard(pad, audios);
            UpdateClap();
            _mix.SetSlotSilenced(pad, silenced);
            var a0 = audios[0];
            L($"slot {pad + 1}: {card.Name} — {card.Artist} [{card.Type}{(parts.Count > 0 && parts[0] != null ? ", " + string.Join("+", parts) : "")}]" +
              (a0.KeyRoot >= 0 && _key is int kk && a0.TransposeTo(kk) != 0 ? $" {a0.TransposeTo(kk):+0;-0} st" : ""));
        }
    }

    static int? KeyOf(Card c) => c.Key == MusicKey.None ? null : (int)c.Key;
    static bool? MinorOf(Card c) => c.Mode switch { MusicMode.Minor => true, MusicMode.Major => false, _ => null };
    /// <summary>Keyless: no key in the data, or a Wild's beat part without WildBeatHasKey.</summary>
    static bool Keyed(Card c, string? part) => c.Key != MusicKey.None && !(c.Type == CardType.Wild && part == "beat" && !c.WildBeatHasKey);

    void Apply(string why)
    {
        _mix.SetMaster(_tempo, _key, _minor);
        L($"mix: {_tempo:0} BPM, {(_key is int k ? KeyName(k) + (_minor == true ? " minor" : " major") : "no key")} ({why})");
    }

    // ------------------------------------------------------------------ transitions

    void WildTransition(Card wild)
    {
        _lastWild = wild.Cid;
        double toBpm = wild.Tempo > 0 ? wild.Tempo : _tempo;
        int? toKey = KeyOf(wild) ?? _key; bool? toMinor = MinorOf(wild) ?? _minor;
        string tdir = Path.Combine(_audioDir, wild.AudioId, wild.Transition);
        var (stinger, endBeat, stutters) = !string.IsNullOrEmpty(wild.Transition) ? LoadStinger(tdir, null, 0) : ((float[]?)null, 16.0, new List<(double, double)>());
        _tempo = toBpm; _key = toKey; _minor = toMinor;
        _mix.StartTransition(new MixTransition(toBpm, toKey, toMinor, stinger, endBeat, stutters));
        L($"mix: {wild.Name} (Wild) transition -> {toBpm:0} BPM, {(toKey is int k ? KeyName(k) + (toMinor == true ? " minor" : " major") : "")} in {endBeat / 4:0.#} bars");
    }

    /// <summary>Render a transition midisong at the current tempo and read its transition_end /
    /// stutter_* markers (beats from the stinger's start).</summary>
    (float[]? data, double endBeat, List<(double, double)> stutters) LoadStinger(string dir, string? variant, int transpose)
    {
        double endBeat = 16; var stutters = new List<(double, double)>();
        if (!Directory.Exists(dir)) return (null, endBeat, stutters);
        try
        {
            var (data, song) = _mix.RenderStinger(dir, _mix.PlayBpm, variant, transpose);
            foreach (var (tick, text) in song.Markers)
            {
                double beat = song.TickToBeats(tick);
                if (text.Equals("transition_end", StringComparison.OrdinalIgnoreCase)) endBeat = beat;
                else if (text.StartsWith("stutter_", StringComparison.OrdinalIgnoreCase))
                {
                    double len = text[8..].ToLowerInvariant() switch { "1n" => 4, "2n" => 2, "4n" => 1, "8n" => 0.5, "16n" => 0.25, "32n" => 0.125, "4nt" => 2.0 / 3, "8nt" => 1.0 / 3, "16nt" => 1.0 / 6, _ => 1 };
                    stutters.Add((beat, len));
                }
            }
            return (data, endBeat, stutters);
        }
        catch (Exception ex) { L($"mix: transition failed ({ex.Message})"); return (null, endBeat, stutters); }
    }

    /// <summary>Only the generic (trans_Gen*) stingers are transposed into the new key; the others play as authored.</summary>
    static bool Transposed(Stinger s) => s.Variant.StartsWith("trans_Gen", StringComparison.OrdinalIgnoreCase);

    /// <summary>Party round transition: PartyMode.roundFusionTransitions (imported), same random key/mode/tempo rules
    /// as the Freestyle button.</summary>
    public bool RandomTransition(string set)
    {
        if (set != "round_trans") return RandomTransition();
        return RandomTransitionWith(GameTables.PartyRoundStingers);
    }

    /// <summary>Freestyle DropMix button (§B.6): Menu_StudioMode.eqFusionTransitions (imported); random key ≠ current,
    /// random mode, tempo ≥20 BPM away.</summary>
    public bool RandomTransition() => RandomTransitionWith(GameTables.FreestyleStingers);

    bool RandomTransitionWith(IReadOnlyList<Stinger> set)
    {
        lock (_lock)
        {
            if (_mix.IsEmpty || _mix.IsTransitioning) return false;
            if (set.Count == 0) { L("mix: no transition stingers imported (game_tables.json)"); return false; }
            int key; do key = _rng.Next(12); while (_key == key);
            bool minor = _rng.Next(2) == 1;
            double bpm; do bpm = _rng.Next((int)MinTempo, (int)MaxTempo); while (Math.Abs(bpm - _tempo) < 20);
            _tempo = bpm; _key = key; _minor = minor;
            var st = set[_rng.Next(set.Count)];
            // stingers are authored in C; generic ones are moved to the new key (nearest, -6..+5)
            int tr = Transposed(st) ? ((key % 12) + 18) % 12 - 6 : 0;
            var (data, endBeat, stutters) = LoadStinger(Path.Combine(_audioDir, "_obb", st.Dir), st.Variant, tr);
            _mix.StartTransition(new MixTransition(bpm, key, minor, data, endBeat, stutters));
            L($"mix: DropMix button -> {bpm:0} BPM, {KeyName(key)} {(minor ? "minor" : "major")} ({st.Variant}{(data == null ? ", no audio" : "")})");
            return true;
        }
    }

    // ------------------------------------------------------------------ sound effects (OBB fusion/*)

    /// <summary>Play a game sound from audio_unpacked/_obb: "folder" or "folder:variant". A folder
    /// with a midisong is rendered at the mix tempo; otherwise its *_c3 sample (else the first) is
    /// played, moved to the mix key. Runs off the caller's thread.</summary>
    public void PlaySfx(string name)
    {
        var parts = name.Split(':', 2);
        string dir = Path.Combine(_audioDir, "_obb", parts[0]);
        string? variant = parts.Length > 1 ? parts[1] : null;
        if (!Directory.Exists(dir)) return;
        int? key; lock (_lock) key = _key;
        int tr = key is int k ? ((k % 12) + 18) % 12 - 6 : 0;
        _ = Task.Run(() =>
        {
            try
            {
                float[] data;
                if (Directory.GetFiles(dir, "*.midisong").Length > 0)
                    data = _mix.RenderStinger(dir, _mix.PlayBpm, variant, tr).data;
                else
                {
                    var files = Directory.Exists(Path.Combine(dir, "samples"))
                        ? Directory.GetFiles(Path.Combine(dir, "samples")).Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f).ToArray()
                        : Array.Empty<string>();
                    if (files.Length == 0)   // patch-only folder: samples live next to another patch
                        files = Directory.GetFiles(Path.Combine(dir, "patches"), "*.fusion")
                            .SelectMany(f => FusionPatch.Load(f).Zones.Select(z => z.SamplePath)).Where(File.Exists).Distinct().ToArray();
                    if (files.Length == 0) return;
                    string pick = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).EndsWith("_c3", StringComparison.OrdinalIgnoreCase)) ?? files[0];
                    var smp = _mix.LoadProcessed(pick, pick.Contains("_c3", StringComparison.OrdinalIgnoreCase) ? tr : 0);
                    data = new float[smp.Frames * 2];
                    for (int f = 0; f < smp.Frames; f++)
                    {
                        float l = smp.Data[f * smp.Channels], r = smp.Channels > 1 ? smp.Data[f * smp.Channels + 1] : l;
                        data[f * 2] = l; data[f * 2 + 1] = r;
                    }
                }
                if (SfxGain != 1) for (int i = 0; i < data.Length; i++) data[i] *= (float)SfxGain;
                _mix.PlayOneShot(data);
            }
            catch (Exception ex) { L($"sfx {name}: {ex.Message}"); }
        });
    }

    // ------------------------------------------------------------------ game cues

    string? Fusion(string folder)
    {
        string dir = Path.Combine(_audioDir, "_obb", folder);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.fusion", SearchOption.AllDirectories).OrderBy(f => f.Length).FirstOrDefault() : null;
    }

    /// <summary>A note-on at 48 + mix key (48 with no key), velocity 127, no note-off.</summary>
    int KeyNote() { int? k; lock (_lock) k = _key; return 48 + (k ?? 0); }

    // rendered cues, reused: (folder|variant|note-or-transpose|bpm) -> audio
    readonly Dictionary<string, float[]> _cueCache = new();
    float[] Cached(string key, Func<float[]> make)
    {
        lock (_cueCache) if (_cueCache.TryGetValue(key, out var hit)) return hit;
        var d = make(); Scale(d);
        lock (_cueCache) { if (_cueCache.Count > 200) _cueCache.Clear(); _cueCache[key] = d; }
        return d;
    }

    void Note(string folder, int note, double delayBeats = 0, string? tag = null)
    {
        var f = Fusion(folder); if (f == null) return;
        double at = _mix.BeatPosition + delayBeats;                 // absolute: a slow render still lands in sync
        _ = Task.Run(() =>
        {
            try
            {
                // random-mode patches pick a zone per note-on, so don't cache those
                var d = FusionPatch.Load(f).LayerMode == "random" ? Scaled(_mix.RenderPatchNote(f, note))
                      : Cached($"{f}|{note}|{SfxGain:0.###}", () => _mix.RenderPatchNote(f, note));
                _mix.PlayOneShotAtBeat(d, at, tag);
            }
            catch (Exception ex) { L($"sfx {folder}: {ex.Message}"); }
        });
    }

    float[] Scaled(float[] d) { Scale(d); return d; }

    void Scale(float[] d) { if (SfxGain != 1) for (int i = 0; i < d.Length; i++) d[i] *= (float)SfxGain; }

    /// <summary>A key-rooted one-shot: enter_music, enter_fx, full_mix, request_warning, score_loss.</summary>
    public void KeySfx(string folder) => Note(folder, KeyNote());

    /// <summary>A plain bank clip (assets_raw/fmod/master/{name}.wav), e.g. snapshot_success / snapshot_fail.</summary>
    public void PlayClip(string name) { var c = Clip(name); if (c != null) _mix.PlayOneShot(Scaled((float[])c.Clone())); }

    /// <summary>A voice line from another bank folder (e.g. "../ftue/ftue_ENG_001_welcome"), tagged so the next line or
    /// <see cref="StopVoice"/> cuts it off. Returns its length in seconds (0 if missing).</summary>
    public double PlayVoice(string relName, string tag = "vo")
    {
        var c = Clip(relName);
        if (c == null) return 0;
        _mix.StopShots(tag);
        _mix.PlayOneShotIn(Scaled((float[])c.Clone()), 0, tag);
        return c.Length / 2.0 / _mix.SampleRate;
    }
    public void StopVoice(string tag = "vo") => _mix.StopShots(tag);

    // score chimes: the original's scoring queues events; the original's audio plays ONE per master beat. Note =
    // table[min(p, last)] + k, p = the team's running total this turn (tables imported into game_tables.json); the
    // last one adds the end bell (24 + k).
    readonly Queue<int> _scoreQueue = new();
    double _nextScoreBeat = -1;
    public void QueueScore(int turnTotal) { lock (_lock) _scoreQueue.Enqueue(Math.Max(0, turnTotal)); }

    void PumpScores()
    {
        if (_scoreQueue.Count == 0 || _mix.IsEmpty && _scoreQueue.Count == 0) return;
        double beat = _mix.BeatPosition;
        if (beat < _nextScoreBeat - 0.35) return;                          // one per beat
        double delay = _mix.BeatsToNext(1);
        int p = _scoreQueue.Dequeue(); bool last = _scoreQueue.Count == 0;
        int k = _key is int kk ? (kk <= 5 ? kk : kk - 12) : 4;
        var table = _minor == true ? GameTables.ScoreChimesMinor : GameTables.ScoreChimesMajor;
        if (table.Count > 0) Note("score_sfx", table[Math.Min(p, table.Count - 1)] + k, delay);
        if (last) Note("score_sfx", 24 + k, delay);                       // score_bell_end (both layered zones)
        _nextScoreBeat = beat + delay + 1;
    }

    /// <summary>WheelOfMisfortune: spin-up at the next 16th; spin-down so its end hit (beat 8) lands when the
    /// wheel stops, on a beat; the spin-down stops the spin-up.</summary>
    public void WheelSpin(double seconds)
    {
        double bpm = _mix.PlayBpm, upDelay = _mix.BeatsToNext(0.25);
        PlayMidisong("equalizer", "equalizer_spinup", 0, upDelay, "wheel");
        double downIn = Math.Max(upDelay + 1, Math.Round(seconds * bpm / 60 - 8));    // beats from now, whole beats
        double toBeat = _mix.BeatsToNext(1);
        downIn = toBeat + Math.Max(0, Math.Round(downIn - toBeat));
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, (downIn - 0.5) * 60 / bpm)));
            _mix.StopShots("wheel");
            PlayMidisong("equalizer", "equalizer_spindown", 0, _mix.BeatsToNext(1), "wheel");
        });
    }
    public void WheelStop() => _mix.StopShots("wheel");
    public void WheelUp() => PlayMidisong("equalizer", "equalizer_spinup", 0, _mix.BeatsToNext(0.25), "wheel");
    public void WheelDown() { _mix.StopShots("wheel"); PlayMidisong("equalizer", "equalizer_spindown", 0, _mix.BeatsToNext(1), "wheel"); }

    /// <summary>PlayGameOverSound: outro_clash at the next bar, transposed from G (−6..+5); card music
    /// −96 dB over 1 s when it starts, then back to 0 dB over 3 s at "gameoverhit" (one bar later). The card
    /// parts keep playing. (Announcer Actions/OutroStinger is in the FMOD bank, not extracted.)</summary>
    public void Outro(string name)
    {
        int t; lock (_lock) { int key = _key ?? 7; t = key - 7; if (t < -6) t += 12; }
        double bar = _mix.BeatsToNext(4);
        if (bar < 0.5) bar += 4;
        PlayMidisong(name, null, t, bar, "outro");
        _mix.ScheduleMusicFade(bar, -96, 1);
        _mix.ScheduleMusicFade(bar + 4, 0, 3);
    }

    void PlayMidisong(string folder, string? variant, int transpose, double delayBeats, string? tag)
    {
        string dir = Path.Combine(_audioDir, "_obb", folder);
        if (!Directory.Exists(dir)) return;
        double at = _mix.BeatPosition + delayBeats, bpm = _mix.PlayBpm;
        _ = Task.Run(() =>
        {
            try
            {
                var d = Cached($"{dir}|{variant}|{transpose}|{bpm:0.##}|{SfxGain:0.###}", () => _mix.RenderStinger(dir, bpm, variant, transpose).data);
                _mix.PlayOneShotAtBeat(d, at, tag);
            }
            catch (Exception ex) { L($"sfx {folder}: {ex.Message}"); }
        });
    }

    // ------------------------------------------------------------------ crowd, ambience, announcer
    // From Master Bank.bank + the original's audio: clips extracted to
    // assets_raw/fmod/master (imported from the FMOD banks). The Crowd_Audio and QueueStingers bus faders are imported
    // with the mix (GameTables.FmodMix); 0 dB without it.

    string FmodDir => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_audioDir)) ?? ".", "assets_raw", "fmod", "master");
    readonly Dictionary<string, float[]?> _clips = new(StringComparer.OrdinalIgnoreCase);
    float[]? Clip(string name)
    {
        lock (_clips)
        {
            if (_clips.TryGetValue(name, out var c)) return c;
            string p = Path.Combine(FmodDir, name + ".wav");
            float[]? d = null;
            if (File.Exists(p))
            {
                var s = SampleCache.DecodeWav(p);
                d = new float[s.Frames * 2];
                for (int f = 0; f < s.Frames; f++)
                { float l = s.Data[f * s.Channels], r = s.Channels > 1 ? s.Data[f * s.Channels + 1] : l; d[f * 2] = l; d[f * 2 + 1] = r; }
                // FSB clips are 24 kHz like the engine; resample otherwise (linear is fine for these beds/VO)
                if (s.SampleRate != _mix.SampleRate) d = TimeStretch.Resample(d, 2, s.SampleRate / (double)_mix.SampleRate);
            }
            else L($"crowd: missing clip {name}");
            _clips[name] = d; return d;
        }
    }
    static float Db(double db) => (float)Math.Pow(10, db / 20);
    float[] Gained(float[] d, double db) { var o = (float[])d.Clone(); float g = Db(db) * (float)SfxGain; for (int i = 0; i < o.Length; i++) o[i] *= g; return o; }
    void Shot(string clip, double at, double db, string? tag = null) { var d = Clip(clip); if (d != null) _mix.PlayOneShotIn(Gained(d, db), at, tag); }
    string Pick(params string[] names) => names[_rng.Next(names.Length)];

    static double CrowdBus => GameTables.FmodMix?.Crowd.VolumeDb ?? 0;
    static double QueueBus => GameTables.FmodMix?.QueueStingers.VolumeDb ?? 0;
    // event master-track levels (imported with the mix; 0 dB without it)
    static double ChantEvent => GameTables.FmodMix?.ChantEvent.VolumeDb ?? 0;          // Crowd/silence_cheer_chant
    static double IntroEvent => GameTables.FmodMix?.IntroEvent.VolumeDb ?? 0;          // Actions/Intro
    static double OutroEvent => GameTables.FmodMix?.OutroStingerEvent.VolumeDb ?? 0;   // Actions/OutroStinger
    enum Crowd { Menu, Beginning, Waiting, Normal, Hyped, Annoyed, GameOver }
    Crowd _crowd = Crowd.Menu; DateTime _crowdAt = DateTime.UtcNow, _lastWaitingOneoff = DateTime.UtcNow;
    double _loopVol = 0, _loopTarget = 0; DateTime _lastCrowdTick = DateTime.UtcNow;
    bool _inGame; double _progress; string _modeId = "";
    /// <summary>Crowd volume setting (0..1); the CROWD VOLUME slider.</summary>
    public double CrowdGain { get; set; } = 1;

    /// <summary>Called by the host every tick: which mode, whether a game is running, and game progress
    /// (max score / points to win) — the original's audio.</summary>
    public void SetGame(string modeId, bool inGame, double progress)
    {
        lock (_lock)
        {
            _modeId = modeId; _progress = progress;
            if (inGame && !_inGame) EnterGame();
            else if (!inGame && _inGame) LeaveGame();
            _inGame = inGame;
        }
    }

    // ---- clap to the beat (§3.5): Clash/Party, when the last Beat part leaves; stops when a Beat part starts ----
    readonly bool[] _slotBeat = new bool[5];
    bool _clapOn, _hadBeat;
    const int ClapVoice = 5;                                   // a sixth, non-slot voice on the card bus, beat-locked
    void UpdateClap()
    {
        bool game = _inGame && _modeId is "clash" or "party";
        bool beat = _slotBeat.Any(b => b);
        if (beat) _hadBeat = true;
        bool want = game && !beat && _hadBeat && _slots.Any(c => c != null);
        if (want == _clapOn) return;
        _clapOn = want;
        if (want)
        {
            string dir = Path.Combine(_audioDir, "_obb", "clap_to_the_beat");
            if (!Directory.Exists(dir)) { _clapOn = false; return; }
            double bpm; try { bpm = MidiSong.Load(dir).Bpm; } catch { bpm = 120; }
            _mix.SetCard(ClapVoice, new CardAudio(dir, null, -1, null, bpm));
            L("crowd: clap to the beat");
        }
        else _mix.ClearCard(ClapVoice);
    }

    /// <summary>A UI sound from the bank: one of name1..N (or the name itself) at random, on the master SFX bus.</summary>
    public void UiSfx(string name)
    {
        var files = Directory.Exists(FmodDir) ? Directory.GetFiles(FmodDir, name + "*.wav").Select(Path.GetFileNameWithoutExtension).Where(f => f != null).ToArray() : Array.Empty<string?>();
        if (files.Length == 0) return;
        PlayClip(files[Random.Shared.Next(files.Length)]!);
    }

    void EnterGame()
    {
        // GameFlow.StartGame → crowd kBeginning: stop the menu loop, intro_outro_cheer (two random layers)
        _mix.StopLoop("amb");
        if (_modeId == "freestyle") return;
        Shot(Pick("intro_outro_1", "intro_outro_2", "intro_outro_3", "intro_outro_4", "intro_outro_5"), 0, CrowdBus);
        Shot(Pick("intro_outro_1.1", "intro_outro_2.1", "intro_outro_3.1", "intro_outro_4.1"), 0, CrowdBus);
        _mix.StopLoop("chant");
        SetCrowd(Crowd.Beginning);
        _hadBeat = false;
    }

    void LeaveGame()
    {
        foreach (var id in new[] { "crowd6", "crowd7", "crowdpeak", "chant" }) _mix.StopLoop(id);
        _loopVol = _loopTarget = 0;
        SetCrowd(Crowd.Menu);
        _hadBeat = false; if (_clapOn) { _clapOn = false; _mix.ClearCard(ClapVoice); }
    }

    void SetCrowd(Crowd c)
    {
        if (c == _crowd) return;
        if (_crowd == Crowd.Annoyed) _mix.StopLoop("chant");
        _crowd = c; _crowdAt = DateTime.UtcNow;
        switch (c)
        {
            case Crowd.Waiting:
                // crowd_loop_main: base_loop6 / base_loop7 / base_peak, looping, faders driven by crowd_loop_vol
                StartLoop("crowd6", "base_loop6"); StartLoop("crowd7", "base_loop7"); StartLoop("crowdpeak", "base_peak");
                _loopVol = 0.5; _lastWaitingOneoff = DateTime.UtcNow; break;
            case Crowd.Annoyed: StartChant(); break;
        }
    }

    void StartLoop(string id, string clip) { var d = Clip(clip); if (d != null) _mix.SetLoop(id, d, 0, d.Length / 2 / (double)_mix.SampleRate, 0); }

    void StartChant()
    {   // silence_cheer_chant: the chant build (28.9 s) then chant_loop
        var a = Clip("silence_cheer_chant_2"); var b = Clip("chant_loop");
        if (a == null || b == null) return;
        var d = new float[a.Length + b.Length]; a.CopyTo(d, 0); b.CopyTo(d, a.Length);
        _mix.SetLoop("chant", d, a.Length / 2 / (double)_mix.SampleRate, d.Length / 2 / (double)_mix.SampleRate, Db(CrowdBus + ChantEvent) * (float)(SfxGain * CrowdGain), fadeIn: false);
    }

    /// <summary>crowd_loop_vol → a crowd_loop_main track's fader (dB), piecewise linear between the imported automation
    /// points (GameTables.FmodMix.CrowdLoop, by the track's sample); 0 dB without them.</summary>
    static double TrackDb(string sample, double v)
    {
        if (GameTables.FmodMix?.CrowdLoop.Tracks.TryGetValue(sample, out var pts) != true || pts!.Count == 0) return 0;
        if (v <= pts[0].X) return pts[0].Db;
        for (int i = 1; i < pts.Count; i++)
            if (v <= pts[i].X)
            {
                var (x0, y0) = pts[i - 1]; var (x1, y1) = pts[i];
                return x1 > x0 ? y0 + (y1 - y0) * ((v - x0) / (x1 - x0)) : y1;
            }
        return pts[^1].Db;
    }

    void CrowdTick()
    {
        var now = DateTime.UtcNow; double dt = Math.Min(0.5, (now - _lastCrowdTick).TotalSeconds); _lastCrowdTick = now;
        if (!_inGame)
        {
            // MenuController kMainMenu → StartMenuLoop: amb_loop, looping over its event's loop region (imported) until a
            // game starts; the whole clip without it
            var amb = Clip("amb_loop");
            if (amb != null)
            {
                double len = amb.Length / 2 / (double)_mix.SampleRate;
                var (ls, le) = GameTables.FmodMix?.Timelines.GetValueOrDefault("menuAmbience")?.Loops.FirstOrDefault() is { EndSec: > 0 } lp ? lp : (0.0, len);
                _mix.SetLoop("amb", amb, ls, Math.Min(le, len), Db(CrowdBus) * (float)(SfxGain * CrowdGain));
            }
            return;
        }
        if (_modeId == "freestyle") return;                                 // studio: crowd forced to kNone
        bool empty = _mix.IsEmpty;
        switch (_crowd)
        {
            case Crowd.Beginning: if ((now - _crowdAt).TotalSeconds >= 1) SetCrowd(Crowd.Waiting); break;
            case Crowd.Waiting:
                _loopTarget = 1;
                if (!empty)
                {
                    Shot(Pick("intro_outro_1.1", "intro_outro_2.1", "intro_outro_3.1", "intro_outro_4.1", "reaction_1", "reaction_2", "reaction_3",
                              "reaction_4", "reaction_5", "reaction_6", "reaction_7", "reaction_2.1", "reaction_5.1", "reaction_6.1"), 0, CrowdBus);
                    SetCrowd(Crowd.Normal);
                }
                else if ((now - _lastWaitingOneoff).TotalSeconds >= 10)
                { Shot(Pick("Rock_you_clap", "param_oneoff_1"), 0, CrowdBus); _lastWaitingOneoff = now; }
                break;
            case Crowd.Normal:
                _loopTarget = 0.5;
                if (_progress >= 1) SetCrowd(Crowd.GameOver); else if (_progress >= 0.8) SetCrowd(Crowd.Hyped); else if (empty) SetCrowd(Crowd.Annoyed);
                break;
            case Crowd.Hyped:
                _loopTarget = 1;
                if (_progress >= 1) SetCrowd(Crowd.GameOver); else if (empty) SetCrowd(Crowd.Annoyed);
                break;
            case Crowd.Annoyed:
                _loopTarget = 1;
                if (_progress >= 1) SetCrowd(Crowd.GameOver);
                else if (!empty) SetCrowd(_progress >= 0.8 ? Crowd.Hyped : Crowd.Normal);
                break;
            case Crowd.GameOver: _loopTarget = 0; break;
        }
        // crowd_loop_vol ramps +0.25/s up, −0.05/s down
        _loopVol = _loopTarget > _loopVol ? Math.Min(_loopTarget, _loopVol + 0.25 * dt) : Math.Max(_loopTarget, _loopVol - 0.05 * dt);
        if (_crowd >= Crowd.Waiting)
        {
            float k = (float)(SfxGain * CrowdGain);
            _mix.SetLoopGain("crowd6", Db(CrowdBus + TrackDb("base_loop6", _loopVol)) * k);
            _mix.SetLoopGain("crowd7", Db(CrowdBus + TrackDb("base_loop7", _loopVol)) * k);
            _mix.SetLoopGain("crowdpeak", Db(CrowdBus + TrackDb("base_peak", _loopVol)) * k);
        }
    }

    /// <summary>A deck/team joined during setup (Menu_IconSelect): crowd cheer, and the crowd chant starts on
    /// the first one. (The original's Crowd/icon_cheer clip wasn't identified; a crowd ooh stands in.)</summary>
    public void LobbyJoin()
    {
        Shot(Pick("crowd_ooh1", "crowd_ooh2", "crowd_ooh3"), 0, CrowdBus);
        if (!_mix.HasLoop("chant")) StartChant();
    }

    /// <summary>Actions/Intro (1v1): ClashIntro, Player1's deck name, ping, "vs", Player2's deck name.
    /// QueueStingers bus + the Intro event's level, and each sound's start on the event's timeline (all imported; 0 s
    /// without them). Takes chosen at random like the multi-instruments.</summary>
    public void ClashIntro(string deckA, string deckB)
    {
        _mix.StopLoop("chant");
        Shot("cheer_oneoff_8", 0, CrowdBus);                                // chant_flow = 1 releases the chant into the cheer
        double db = QueueBus + IntroEvent;
        Shot(Pick("ClashIntro_1", "ClashIntro_2", "ClashIntro_3"), At("intro", "ClashIntro_"), db);
        Shot($"vo_p1_{deckA}{_rng.Next(1, 4)}", At("intro", "vo_p1_"), db);
        Shot(Pick("Ping1", "Ping2", "Ping3"), At("intro", "Ping"), db);
        Shot($"vo_vs{_rng.Next(1, 6)}", At("intro", "vo_vs"), db);
        Shot($"vo_p2_{deckB}{_rng.Next(1, 3)}", At("intro", "vo_p2_"), db);
    }

    /// <summary>A sound's start (s) on an imported event timeline (GameTables.FmodMix.Timelines), 0 without it.</summary>
    static double At(string timeline, string samplePrefix) => GameTables.FmodMix?.StartOf(timeline, samplePrefix) ?? 0;

    /// <summary>The Party intro event (the bank's unnamed event playing ClashIntro and PartyIntro_ping): both sounds at
    /// their imported timeline starts; Actions/Intro's level (the event's own level isn't imported).</summary>
    public void PartyIntro()
    {
        double db = QueueBus + IntroEvent;
        Shot(Pick("ClashIntro_1", "ClashIntro_2", "ClashIntro_3"), At("partyIntro", "ClashIntro_"), db);
        Shot("PartyIntro_ping", At("partyIntro", "PartyIntro_ping"), db);
    }

    /// <summary>The Puzzle intro event (the bank's unnamed event playing ClashIntro and PuzzlePing1): ClashIntro at its
    /// imported timeline start; Actions/Intro's level. (The event's other sounds aren't played.)</summary>
    public void PuzzleIntro() => Shot(Pick("ClashIntro_1", "ClashIntro_2", "ClashIntro_3"), At("puzzleIntro", "ClashIntro_"), QueueBus + IntroEvent);

    /// <summary>Actions/OutroStinger (one winner): score_win, OutroStinger, "&lt;Deck&gt; wins", at their imported timeline
    /// starts.</summary>
    public void WinVo(string deck)
    {
        double db = QueueBus + OutroEvent;
        Shot("score_win", At("outroStinger", "score_win"), db); Shot("OutroStinger", At("outroStinger", "OutroStinger"), db);
        Shot($"vo_w1_{deck}Wins1", At("outroStinger", "vo_w1_"), db);
    }

    // ------------------------------------------------------------------ FX music effects (§C)

    void MusicEffect(string fx, string card)
    {
        // the prefab's ToggleKeyMode / ChangeKey / ChangeTempo components (imported into game_tables.json)
        if (!GameTables.MusicEffects.TryGetValue(fx, out var def)) return;
        bool toggle = def.ToggleMode; int keyUp = def.KeyUp; double tempo = def.Tempo;
        if (!toggle && keyUp == 0 && tempo == 0) return;
        if (toggle && _minor != null) _minor = !_minor;
        if (keyUp != 0 && _key is int k) _key = (k + keyUp) % 12;
        if (tempo != 0) _tempo = Math.Clamp(_tempo + tempo, MinTempo, MaxTempo);
        Apply($"{card}: {fx}");
    }

    // ------------------------------------------------------------------ FX card slot DSP (§4.2–4.3)

    /// <summary>False in Freestyle: studio mode deactivates music-effect controllers on their first Update (§3.5).</summary>
    public bool SlotFxEnabled { get; set; } = true;

    sealed class FxCtl { public int Id, Target, FxSlot; public string? Owner; public string Persist = "card"; public int BarsLeft; public long LastBar; }
    readonly List<FxCtl> _fx = new();

    /// <summary>MusicEffectController.Activate on each target slot: the prefab's DSP, ended by its persistence
    /// (bar countdown: after numBars+1 downbeats; turn: TurnEnd(); card: the slot's card changing).</summary>
    public void FxEffect(string effect, int fxSlot, string? ownerCid, IEnumerable<int> targets)
    {
        if (!SlotFxPresets.All.TryGetValue(effect, out var pr) || !SlotFxEnabled) return;
        UiSfx("slider_");                                          // SFXCard/fxCardActivate (slider_1..4)
        lock (_lock)
        {
            long bar = (long)Math.Floor(_mix.BeatPosition / 4);
            foreach (int t in targets.Where(t => t is >= 0 and < 5).Distinct())
            {
                var c = new FxCtl { Id = _mix.AddSlotEffect(t, pr.fx), Target = t, FxSlot = fxSlot, Owner = ownerCid, Persist = pr.persist, LastBar = bar };
                if (int.TryParse(pr.persist, out int n)) c.BarsLeft = n + 1;
                _fx.Add(c);
                L($"fx: {effect} on slot {t + 1} ({(c.BarsLeft > 0 ? $"{n} bars" : pr.persist)})");
            }
        }
    }

    /// <summary>the original's scoring → controllers with UntilTurnEnd persistence stop.</summary>
    public void TurnEnd() { lock (_lock) EndFx(c => c.Persist == "turn"); }

    void EndSlotFx(int pad, string? newCid) =>
        EndFx(c => c.Target == pad || (c.FxSlot == pad && c.Owner != newCid));

    void EndFx(Func<FxCtl, bool> which)
    {
        foreach (var c in _fx.Where(which).ToList()) { _mix.RemoveSlotEffect(c.Id); _fx.Remove(c); L($"fx: effect off slot {c.Target + 1}"); }
    }

    void FxTick()
    {
        if (_fx.Count == 0) return;
        long bar = (long)Math.Floor(_mix.BeatPosition / 4);
        foreach (var c in _fx.Where(c => c.BarsLeft > 0).ToList())
        {
            if (bar == c.LastBar) continue;
            c.BarsLeft -= bar > c.LastBar ? (int)(bar - c.LastBar) : 1;       // the timeline restarted: count one downbeat
            c.LastBar = bar;
            if (c.BarsLeft <= 0) { _mix.RemoveSlotEffect(c.Id); _fx.Remove(c); L($"fx: effect on slot {c.Target + 1} ran out"); }
        }
    }

    // ------------------------------------------------------------------ Freestyle studio panel (§B.1, §B.2)

    public void SetStudioOpen(bool open) { lock (_lock) { StudioOpen = open; _mix.BoostEnabled = !open; } }

    public void SetTempo(double bpm) { lock (_lock) { _tempo = Math.Clamp(Math.Round(bpm), MinTempo, MaxTempo); Apply("studio"); } }

    /// <summary>Key slider walks the circle of fifths: key = (pos*7) % 12, wrapping.</summary>
    public void StepKey(int dir)
    {
        lock (_lock)
        {
            int key = _key ?? 0;
            int pos = (key + (key % 2 == 1 ? 6 : 0)) % 12;
            pos = ((pos + Math.Sign(dir)) % 12 + 12) % 12;
            _key = pos * 7 % 12; _minor ??= true;
            Apply("studio");
        }
    }

    /// <summary>Set key and mode directly (studio / tests).</summary>
    public void SetKey(int key, bool minor) { lock (_lock) { _key = ((key % 12) + 12) % 12; _minor = minor; Apply("studio"); } }

    public void ToggleMode() { lock (_lock) { _minor = !(_minor ?? false); Apply("studio"); } }

    /// <summary>Per-card volume, level 0..1 (SliderToDb: 0–0.05 → −96..−24 dB, 0.05–1 → −24..+12 dB).</summary>
    /// <summary>Per-slot volume slider levels (0..1); 0.683 = 0 dB, the line's resting place.</summary>
    readonly double[] _volumes = Enumerable.Repeat(0.683, 5).ToArray();

    public double Volume(int slot) { lock (_lock) return slot >= 0 && slot < _volumes.Length ? _volumes[slot] : 0.683; }
    public (double Tempo, int? Key, bool? Minor) Master { get { lock (_lock) return (_tempo, _key, _minor); } }

    public void SetSlotVolume(int slot, double level)
    {
        level = Math.Clamp(level, 0, 1);
        if (slot >= 0 && slot < _volumes.Length) _volumes[slot] = level;
        _mix.SetSlotManual(slot, Math.Abs(level - 0.683) > 1e-3);       // moved off the resting line = player-set
        double db = level <= 0.05 ? -96 + level / 0.05 * 72 : -24 + (level - 0.05) / 0.95 * 36;
        _mix.SetSlotGainDb(slot, db);
    }

    // ------------------------------------------------------------------ clock

    /// <summary>Call often (e.g. 10 Hz). Applies the in-game tempo creep on bar boundaries.</summary>
    public void Tick()
    {
        lock (_lock)
        {
            PumpScores();
            CrowdTick();
            FxTick();
            if (_mix.IsEmpty) { _lastBar = -1; return; }
            int bar = (int)(_mix.BeatPosition / 4) % LoopBars;             // bar within the master loop
            if (bar == _lastBar) return;
            _lastBar = bar;
            if (Finale && !_mix.IsTransitioning && bar != 0 && _tempo < MaxTempo)
            { _tempo = Math.Min(_tempo + 1, MaxTempo); _mix.SetMaster(_tempo, _key, _minor); }
            else if (TempoCreep && !Finale && !_mix.IsTransitioning && bar != 0 && bar % _a.AccelerateTempoSubdivisionBars == 0 && _tempo < CreepFloor)
            { _tempo = Math.Min(_tempo + _a.AccelerateTempoAmount, CreepFloor); Apply("tempo creep"); }
        }
    }

    public object State() => new
    {
        tempo = Math.Round(_mix.PlayBpm, 1),
        key = _key is int k ? KeyName(k) : null,
        minor = _minor,
        studioOpen = StudioOpen,
        volumes = _volumes.Select(v => Math.Round(v, 3)).ToArray(),
        minTempo = MinTempo, maxTempo = MaxTempo,
        transitioning = _mix.IsTransitioning,
        bar = _mix.IsEmpty ? -1 : (int)(_mix.BeatPosition / 4) % LoopBars,
    };

    public static string KeyName(int k) => new[] { "C", "D♭", "D", "E♭", "E", "F", "G♭", "G", "A♭", "A", "B♭", "B" }[((k % 12) + 12) % 12];
}
