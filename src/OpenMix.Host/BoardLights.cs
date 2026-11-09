using OpenMix.Audio;
using OpenMix.Board;
using OpenMix.Cards;
using OpenMix.Game;

/// <summary>
/// Drives the board's slot lights the way the original app does (docs/BOARD.md, from
/// the original's board lights / the original's slot lights):
///  - on connect: LED_TIME 0x14, the startup colours (imported instrument colours) and the 3-frame startup sweep;
///  - Normal play (Freestyle "studioMode"): each slot shows its card's instrument colour on both
///    sides, empty slots black; silenced slots red and every other slot black. Colours are re-sent
///    only when they change, at most every 0.5 s;
///  - every 0.8 s an 8-step brightness frame per slot: empty = off, card placed but not yet sounding =
///    full, silenced = blink, music transition = V sweep, sounding = the card's precomputed meter
///    (assets_raw/meters, 64 values per bar) at the original's latency offset, Lerp(0.07, 1, m·gain)
///    (gain = CardMeterDatabase.gain, imported; 1 without it).
///    Nothing is sent while every slot is empty.
/// </summary>
sealed class BoardLights
{
    readonly DropMixBoard _board;
    readonly Table _table;
    readonly MixEngine? _mix;
    readonly string _meterDir;
    readonly Dictionary<string, float[]?> _meters = new(StringComparer.OrdinalIgnoreCase);
    const double FramePeriod = 0.8, ColourMinGap = 0.5;
    /// <summary>Extra light offset in seconds (+ = lights earlier), tuned live via /api/board/lights.</summary>
    public static double ExtraOffset { get; set; }
    public static Action<string>? Trace { get; set; }
    const double Floor = 0.07;
    static double MeterGain => GameTables.BoardLights?.MeterGain ?? 1;
    // BoardLightController.ledLatencyAdjustFactor and GameAudio.audioLatencyAdjust (imported; 0 = no adjustment)
    static double LedLatencyAdjustFactor => GameTables.BoardLights?.LedLatencyAdjustFactor ?? 0;
    static double AudioLatencyAdjust => GameTables.BoardLights?.AudioLatencyAdjust ?? 0;

    /// <summary>An instrument colour the original configured (ClashConfig, imported); plain white when not imported.</summary>
    static (byte, byte) C(string name) =>
        GameTables.BoardLights?.Colours.TryGetValue(name, out var c) == true ? BoardLeds.FromColour(c.r, c.g, c.b) : BoardLeds.White;
    static (byte, byte) Lead => C("lead");
    static (byte, byte) Loop => C("loop");
    static (byte, byte) Beat => C("beat");
    static (byte, byte) Bass => C("bass");
    static (byte, byte) Fx => C("fx");

    /// <summary>Startup / GameStarting: Blue side lead, loop, loop, beat, beat; Red side loop, loop, beat, beat, bass.</summary>
    static (byte, byte)[] Startup => new[] { Lead, Loop, Loop, Beat, Beat, Loop, Loop, Beat, Beat, Bass };

    public BoardLights(DropMixBoard board, Table table, MixEngine? mix, string meterDir)
    { _board = board; _table = table; _mix = mix; _meterDir = meterDir; }

    public async Task RunAsync(CancellationToken ct)
    {
        await _board.SetLedStepTimeAsync(0x14);
        byte[] lastColours = BoardLeds.Encode(Startup);
        await _board.SetLedsRawAsync(lastColours);
        foreach (var f in BoardLeds.StartupFrames)
        {
            await _board.SetBrightnessRawAsync(BoardLeds.Frame(f));
            await Task.Delay(TimeSpan.FromSeconds(FramePeriod), ct);
        }

        LightCue? cue = null; int counter = 0;                                  // frames sent in the current scene
        var lastColourAt = DateTime.UtcNow; var clock = System.Diagnostics.Stopwatch.StartNew(); double nextFrame = 0;
        while (!ct.IsCancellationRequested)
        {
            var slots = Enumerable.Range(0, 5).Select(i => _table.Sounding(i)).ToArray();
            var stacks = _board.Contents(); HasCards = i => i >= 0 && i < stacks.Length && stacks[i].Count > 0;
            var owners = _table.LightSlots();
            var latest = Override?.Invoke() ?? _table.Lights();
            if (cue == null || latest.Scene != cue.Scene || latest.Side != cue.Side || latest.Serial != cue.Serial) counter = 0;   // a mode change resets the frame counter
            cue = latest;

            var colours = BoardLeds.Encode(cue.Scene == LightScene.Normal ? Colours(slots, owners, _table.Mode.Id) : SceneColours(cue));
            if (!colours.AsSpan().SequenceEqual(lastColours) && (DateTime.UtcNow - lastColourAt).TotalSeconds >= ColourMinGap)
            {
                await _board.SetLedsRawAsync(colours);
                lastColours = colours; lastColourAt = DateTime.UtcNow;
            }

            double now = clock.Elapsed.TotalSeconds;
            if (now >= nextFrame)
            {
                // steady 0.8 s cadence (catch up without bunching if a write ran long)
                nextFrame = now - nextFrame > FramePeriod ? now + FramePeriod : nextFrame + FramePeriod;
                if (cue.Scene != LightScene.Normal || slots.Any(s => s.card != null))
                {
                    int n = counter++;
                    var frame = BoardLeds.Frame(Enumerable.Range(0, 5).Select(i => cue.Scene == LightScene.Normal ? Brightness(i, slots[i]) : SceneBrightness(cue, i, n)).ToList());
                    double t0 = clock.Elapsed.TotalMilliseconds;
                    await _board.SetBrightnessRawAsync(frame);
                    Trace?.Invoke($"lights frame at {now:0.000}s, write {clock.Elapsed.TotalMilliseconds - t0:0} ms, beat {_mix?.BeatPosition:0.00}");
                }
            }
            double wait = Math.Clamp((nextFrame - clock.Elapsed.TotalSeconds) * 1000, 1, 50);
            await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
        }
    }

    /// <summary>Normal mode (the original's slot lights). Freestyle / MixGrid ("studioMode") and Party light both sides for
    /// any card; Clash lights only the owner's side; Lanes only the Red side. LEDs 0-4 Blue, 5-9 Red.</summary>
    static (byte, byte)[] Colours((Card? card, string?[] parts, bool silenced)[] slots, (Card? Card, Side Owner, bool Silenced)[] owners, string mode)
    {
        var leds = new (byte, byte)[10];
        bool anySilenced = slots.Any(s => s.card != null && s.silenced);
        for (int i = 0; i < 5; i++)
        {
            var (card, _, sil) = slots[i];
            var c = anySilenced ? (sil && card != null ? BoardLeds.Red : BoardLeds.Black)
                  : card == null ? BoardLeds.Black : Instrument(card.Type, i);
            bool blue = true, red = true;
            if (!anySilenced && card != null)
            {
                if (mode == "clash" && owners[i].Owner != Side.None) { blue = owners[i].Owner == Side.Blue; red = !blue; }
                else if (mode == "lanes") blue = false;
            }
            leds[i] = blue ? c : BoardLeds.Black; leds[i + 5] = red ? c : BoardLeds.Black;
        }
        return leds;
    }

    /// <summary>the original's board lights tables for the Clash scenes; LEDs a scene does not set stay unset (00 00).</summary>
    static (byte, byte)[] SceneColours(LightCue cue)
    {
        var leds = Enumerable.Repeat(BoardLeds.Unset, 10).ToArray();
        static int Base(Side s) => s == Side.Red ? 5 : 0;
        switch (cue.Scene)
        {
            case LightScene.ClashStart:
            case LightScene.TurnChange:
                for (int i = 0; i < 5; i++) leds[Base(cue.Side) + i] = BoardLeds.White;
                break;
            case LightScene.Victory:
                var win = new[] { Lead, Loop, Fx, Beat, Bass };
                for (int i = 0; i < 5; i++)
                {
                    leds[i] = cue.Side == Side.Blue ? win[i] : BoardLeds.Black;
                    leds[i + 5] = cue.Side == Side.Red ? win[i] : BoardLeds.Black;
                }
                break;
            case LightScene.Spinner:
                for (int i = 0; i < 5; i++)
                {
                    var hit = cue.Vulnerable is { } v && v[i] ? BoardLeds.Red : BoardLeds.White;
                    if (cue.Side == Side.None) { leds[i] = hit; leds[i + 5] = hit; }       // coin flip: both sides
                    else
                    {   // attack: targeted side red where vulnerable, attacker side black
                        leds[Base(cue.Side) + i] = hit;
                        leds[Base(cue.Side == Side.Red ? Side.Blue : Side.Red) + i] = BoardLeds.Black;
                    }
                }
                break;
            case LightScene.RemoveAll:
                for (int k = 0; k < 10; k++) leds[k] = BoardLeds.Red;
                break;
            case LightScene.RegistrationClash:                 // Red side slots 1-2, Blue side slots 4-5, white
                leds[5] = leds[6] = BoardLeds.White; leds[3] = leds[4] = BoardLeds.White;
                break;
            case LightScene.RegistrationParty:
                for (int k = 0; k < 10; k++) leds[k] = BoardLeds.White;
                break;
            case LightScene.RegistrationLanes:
                leds[2] = leds[7] = BoardLeds.White;
                break;
            case LightScene.GameStarting:
                var startup = Startup;
                for (int k = 0; k < 10; k++) leds[k] = startup[k];
                break;
            case LightScene.Overflow:                          // Red side all red (Blue side unset)
                for (int i = 0; i < 5; i++) leds[5 + i] = BoardLeds.Red;
                break;
            case LightScene.ErrorBlink:                        // red on the slots in error on the current side; else black
                for (int i = 0; i < 5; i++)
                {
                    bool err = cue.Vulnerable is { } v && v[i];
                    leds[i] = err && cue.Side == Side.Blue ? BoardLeds.Red : BoardLeds.Black;
                    leds[i + 5] = err && cue.Side == Side.Red ? BoardLeds.Red : BoardLeds.Black;
                }
                break;
            case LightScene.CardRegistration:                  // Add to Collection: slots whose cards are all owned show collectionOwnedColor
                for (int i = 0; i < 5; i++) leds[i] = leds[i + 5] = cue.Owned is { } own && own[i] ? CollectionOwned : BoardLeds.White;
                break;
        }
        return leds;
    }

    /// <summary>Scene brightness; n = frames already sent in this scene.</summary>
    static float[] SceneBrightness(LightCue cue, int slot, int n) => cue.Scene switch
    {
        LightScene.ClashStart => BoardLeds.ClashPulse,
        LightScene.TurnChange => BoardLeds.Vee,
        LightScene.Spinner => BoardLeds.Spinner(slot, n % 2),
        LightScene.Victory => BoardLeds.Victory(slot, n % 3),
        LightScene.RemoveAll => HasCards(slot) ? BoardLeds.Blink : BoardLeds.Off,
        LightScene.RegistrationClash or LightScene.RegistrationParty or LightScene.RegistrationLanes =>
            cue.Vulnerable is { } a && a[slot] ? BoardLeds.Full : BoardLeds.SoftVee,      // deck placement: active Full, else SoftVee
        LightScene.GameStarting or LightScene.Overflow => BoardLeds.Vee,
        LightScene.ErrorBlink => BoardLeds.Blink,
        LightScene.CardRegistration => cue.Vulnerable is { } c && c[slot] ? BoardLeds.Full : BoardLeds.Off,
        _ => BoardLeds.Full,
    };
    /// <summary>A host-side scene that wins over the mode's (the Deck Builder's CardRegistration).</summary>
    public static Func<LightCue?>? Override;
    static (byte, byte) CollectionOwned => C("collectionOwned");
    static Func<int, bool> HasCards = _ => true;

    /// <summary>the original's config: Wilds take the slot's wild colour (single-colour
    /// slots 1 and 3 use that colour — inferred).</summary>
    static (byte, byte) Instrument(CardType t, int slot) => t switch
    {
        CardType.Lead => Lead, CardType.Loop => Loop, CardType.Beat => Beat,
        CardType.Bass => Bass, CardType.Fx => Fx,
        CardType.Wild => slot switch { 0 => C("wildLeadLoop"), 1 => Loop, 2 => C("wildLoopBeat"), 3 => Beat, _ => C("wildBeatBass") },
        _ => BoardLeds.White,
    };

    float[] Brightness(int slot, (Card? card, string?[] parts, bool silenced) s)
    {
        if (s.card == null) return BoardLeds.Off;
        if (s.silenced) return BoardLeds.Blink;
        if (_mix == null) return BoardLeds.Full;
        if (_mix.IsTransitioning) return BoardLeds.Vee;
        if (!_mix.SlotSounding(slot)) return BoardLeds.Full;
        var meter = Meter(s.card, slot);
        if (meter == null || meter.Length == 0) return BoardLeds.Full;

        // the original works from the timeline position (ahead of what's heard by the output latency),
        // then applies (−1.5 × (latency − 0.1) − 0.25) s
        double bpm = _mix.PlayBpm;
        double rawLatency = _mix.OutputLatency - AudioLatencyAdjust;
        double off = (LedLatencyAdjustFactor * rawLatency - 0.25 + ExtraOffset) * bpm / 60;   // in beats
        double heard = _mix.BeatPosition;
        double span = FramePeriod * bpm / 60;
        double loopBeats = Math.Max(4, s.card.NumBars * 4);
        var v = new float[8];
        for (int k = 0; k < 8; k++)
        {
            double b = heard + off + span * k / 8;
            double inLoop = ((b % loopBeats) + loopBeats) % loopBeats;
            float m = meter[(int)Math.Round(inLoop * 16) % meter.Length];
            v[k] = (float)Math.Clamp(Floor + (1 - Floor) * Math.Clamp(m * MeterGain, 0, 1), 0, 1);
        }
        return v;
    }

    /// <summary>CardMeterDatabase: TextAsset per card (SourceCID; Wilds per slot, "_Wild_&lt;slot&gt;").</summary>
    float[]? Meter(Card c, int slot)
    {
        string name = c.Type == CardType.Wild ? $"{c.SourceCid}_{slot}" : c.SourceCid;
        if (_meters.TryGetValue(name, out var m)) return m;
        string path = Path.Combine(_meterDir, name + ".txt");
        float[]? data = null;
        if (File.Exists(path))
            data = File.ReadAllText(path).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => float.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f).ToArray();
        _meters[name] = data;
        return data;
    }
}
