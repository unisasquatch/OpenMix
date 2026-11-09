using System.Text.Json;

namespace OpenMix.Cards;

/// <summary>A Wheel of Misfortune slice: the wheel's result value and its relative weight.</summary>
public sealed record WheelSlice(int Value, double Weight);

/// <summary>A stinger midisong: its folder under audio_unpacked/_obb and the variant (midisong name) in it.</summary>
public sealed record Stinger(string Dir, string Variant);

/// <summary>A slot music effect prefab (MusicEffectController and the effect components on its GameObject).
/// Persist: "card" (until the card is removed), "turn", or a bar count. Each DSP part is null when the prefab has none.</summary>
public sealed record MusicEffectDef(
    string Persist,
    (int bits, int down, double wet)? Crush,
    (double freq, double q)? LowPass,
    (double beatDelay, double dry, double wet, double fb)? Delay,
    (double[] f, double depth, double rate, double wet, double shape)? Vibe,
    (double closed, double open, double qc, double qo, double rate, double shape)? Wah,
    double? GainDb,
    int KeyUp, int Tempo, bool ToggleMode);

/// <summary>The board-light settings the original configured: its LED colours (RGB 0..1, by OpenMix name: lead, loop,
/// beat, bass, fx, wildLeadLoop, wildLoopBeat, wildBeatBass, collectionOwned), latency factors and the card-meter gain.</summary>
public sealed record BoardLightSettings(double LedLatencyAdjustFactor, double AudioLatencyAdjust, double MeterGain, IReadOnlyDictionary<string, (double r, double g, double b)> Colours);

/// <summary>The music clock the original configured (GameAudio): the tempo a session starts at, the tempo range, the
/// in-game creep (+Amount BPM every SubdivisionBars bars while below Floor) and the master loop length in bars.</summary>
public sealed record GameAudioSettings(double StartingTempo, double MinTempo, double MaxTempo, int LoopLengthInBars,
    double AccelerateTempoFloor, int AccelerateTempoSubdivisionBars, double AccelerateTempoAmount);

/// <summary>Clash rules the original configured (ActionCounter, ScoreKeeper, CardsInHandCounter, ClashConfig). Hand and
/// deck values are per team size: [0] = a one-player team, [1] = a two-player team.</summary>
public sealed record ClashRules(int ActionsPerTurn, int FillMixBonus, int CardRemovalPenalty, double AutomaticTurnEndSeconds,
    int[] StartingHand, int[] MaxHand, int[] DrawSize, int[] DeckSize, int[] PowerBudget, int[] MaxDuplicates, int StackHeightWarningThreshold);

/// <summary>Saved mixes (SnapshotManager): the lowest playback level a saved slot gets and the cards a save needs; and
/// the playback pace (Menu_SnapshotViewer: beats between the cards entering).</summary>
public sealed record SnapshotSettings(double MinimumVolume, int MinRequiredCards, int BeatsBetweenCards);

/// <summary>An effect on an FMOD bus, with the original's parameters (FMOD's built-in DSP units).</summary>
public abstract record MixEffect;
/// <summary>FMOD three-band EQ: gains of the bands below/between/above the crossovers; slope 0 = 12 dB/oct.</summary>
public sealed record ThreeBandEqEffect(double LowDb, double MidDb, double HighDb, double LowCrossoverHz, double HighCrossoverHz, int Slope) : MixEffect;
public sealed record ParametricEqEffect(double CenterHz, double Octaves, double GainDb) : MixEffect;
public sealed record CompressorEffect(double ThresholdDb, double Ratio, double AttackMs, double ReleaseMs, double MakeupDb) : MixEffect;
public sealed record LimiterEffect(double ReleaseMs, double CeilingDb, double MaximizerDb) : MixEffect;
/// <summary>An effect OpenMix doesn't reproduce (a plugin, send or meter), kept so the chain order is visible.</summary>
public sealed record OtherEffect(string Kind) : MixEffect;

/// <summary>An FMOD bus (or event master track): the effects before the fader, the fader level, the effects after it.</summary>
public sealed record MixChain(IReadOnlyList<MixEffect> PreFader, double VolumeDb, IReadOnlyList<MixEffect> PostFader);

/// <summary>The original's FMOD mixer, imported from Master Bank.bank: the cardSlot1 event's master track, the
/// CardMusic, master, Crowd_Audio and QueueStingers buses, and the master tracks of the other events OpenMix plays
/// (Crowd/silence_cheer_chant, Actions/Intro, Actions/OutroStinger).</summary>
public sealed record FmodMixSettings(MixChain CardSlot1, MixChain CardMusic, MixChain Master, MixChain Crowd, MixChain QueueStingers,
    MixChain ChantEvent, MixChain IntroEvent, MixChain OutroStingerEvent, TrackAutomation CrowdLoop,
    IReadOnlyDictionary<string, EventTimeline> Timelines)
{
    /// <summary>When (seconds into the event) the instrument playing a sample whose name starts with
    /// <paramref name="samplePrefix"/> starts on timeline <paramref name="timeline"/> ("intro", "outroStinger",
    /// "menuAmbience"), or null.</summary>
    public double? StartOf(string timeline, string samplePrefix) =>
        Timelines.TryGetValue(timeline, out var t)
            ? t.Instruments.FirstOrDefault(i => i.Samples.Any(s => s.StartsWith(samplePrefix, StringComparison.Ordinal)))?.StartSec
            : null;
}

/// <summary>A placed instrument on an event timeline: start and length in seconds, and the samples it can play.</summary>
public sealed record TimelineInstrument(double StartSec, double LengthSec, IReadOnlyList<string> Samples);
/// <summary>An event's timeline: its instruments and loop regions (seconds).</summary>
public sealed record EventTimeline(IReadOnlyList<TimelineInstrument> Instruments, IReadOnlyList<(double StartSec, double EndSec)> Loops);

/// <summary>An event's track volume automation: the parameter driving it and, per track (by the sample it plays), the
/// curve points (parameter value, volume dB) in order.</summary>
public sealed record TrackAutomation(string Parameter, IReadOnlyDictionary<string, IReadOnlyList<(double X, double Db)>> Tracks);

/// <summary>
/// The game's small data tables, imported at setup from the user's own copy of DropMix into data/game_tables.json
/// (see OpenMix.Importer's SceneConfigs.Tables). Nothing here has built-in values: until <see cref="Load"/> succeeds
/// every table is empty and the engine behaves neutrally (no score chimes, a wheel that always lands on 0, no
/// stingers, no music effects, no Clash decks, no board-light colours, a neutral mix) or refuses what needs them (the mix engine
/// without <see cref="Audio"/>, Clash without <see cref="Clash"/>, saving mixes without <see cref="Snapshots"/>).
/// </summary>
public static class GameTables
{
    public const string FileName = "game_tables.json";

    /// <summary>Score chime notes (MIDI) by the team's running total this turn, for major and minor keys.</summary>
    public static IReadOnlyList<int> ScoreChimesMajor { get; private set; } = Array.Empty<int>();
    public static IReadOnlyList<int> ScoreChimesMinor { get; private set; } = Array.Empty<int>();
    /// <summary>Wheel of Misfortune slices for Clash and Puzzle, and for Party.</summary>
    public static IReadOnlyList<WheelSlice> WheelSlices { get; private set; } = Array.Empty<WheelSlice>();
    public static IReadOnlyList<WheelSlice> PartyWheelSlices { get; private set; } = Array.Empty<WheelSlice>();
    /// <summary>Freestyle DropMix-button stingers and Party round transitions.</summary>
    public static IReadOnlyList<Stinger> FreestyleStingers { get; private set; } = Array.Empty<Stinger>();
    public static IReadOnlyList<Stinger> PartyRoundStingers { get; private set; } = Array.Empty<Stinger>();
    /// <summary>Slot music effects by prefab name (e.g. "FxWahKey1").</summary>
    public static IReadOnlyDictionary<string, MusicEffectDef> MusicEffects { get; private set; } = new Dictionary<string, MusicEffectDef>();
    /// <summary>The music effect a silenced slot gets (a key of <see cref="MusicEffects"/>), or null.</summary>
    public static string? SilenceMusicEffect { get; private set; }
    /// <summary>Clash team decks (deck ids), in the original's order.</summary>
    public static IReadOnlyList<string> ClashPlayerIds { get; private set; } = Array.Empty<string>();
    /// <summary>Deck id → text key of the Party leaderboard's default name for that deck (decks without one are absent).</summary>
    public static IReadOnlyDictionary<string, string> PartyLeaderboardNameKeys { get; private set; } = new Dictionary<string, string>();
    public static BoardLightSettings? BoardLights { get; private set; }
    /// <summary>Wheel of Misfortune tints (grey levels or RGB 0..1) by WheelOfMisfortune field name, e.g. "sliceNormalColor".</summary>
    public static IReadOnlyDictionary<string, (double r, double g, double b)> WheelColours { get; private set; } = new Dictionary<string, (double, double, double)>();
    public static GameAudioSettings? Audio { get; private set; }
    /// <summary>Screen colours (RGB 0..1) by "Class.field", e.g. "PartyRequest.perfectBackgroundColor".</summary>
    public static IReadOnlyDictionary<string, (double r, double g, double b)> UiColours { get; private set; } = new Dictionary<string, (double, double, double)>();
    /// <summary>The Fusion synth's shared voice pool soft limit, or null (no shared limit).</summary>
    public static int? FusionSoftVoiceLimit { get; private set; }
    public static ClashRules? Clash { get; private set; }
    public static SnapshotSettings? Snapshots { get; private set; }
    /// <summary>Slot VU meter bloom per instrument (lead, loop, beat, bass, fx).</summary>
    public static IReadOnlyDictionary<string, double> VuBloom { get; private set; } = new Dictionary<string, double>();
    /// <summary>The FMOD mixer chains, or null: the mix engine then plays through a neutral mix (no EQ or dynamics, unity gains).</summary>
    public static FmodMixSettings? FmodMix { get; private set; }

    /// <summary>Text for a localisation key. Defaults to the imported English table (data/loc/strings.json); the host
    /// may replace it to follow the chosen language.</summary>
    public static Func<string, string?> Localize { get; set; } = k => English.TryGetValue(k, out var v) ? v : null;
    /// <summary>The imported text for <paramref name="key"/> (rich-text tags kept), or OpenMix's own
    /// <paramref name="fallback"/> when the strings table doesn't have it.</summary>
    public static string Text(string key, string fallback) => Localize(key) ?? fallback;

    /// <summary><see cref="Text"/> as plain text for places that show no rich text: tags dropped, line breaks and runs
    /// of spaces collapsed to one space.</summary>
    public static string PlainText(string key, string fallback) => Plain(Text(key, fallback));

    /// <summary><see cref="PlainText"/> in capitals (the on-screen error and prompt banners).</summary>
    public static string Caps(string key, string fallback) => PlainText(key, fallback).ToUpperInvariant();

    /// <summary>Rich text → plain text (tags dropped, whitespace collapsed).</summary>
    public static string Plain(string s) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(s, "<[^>]*>", ""), @"\s+", " ").Trim();

    static Dictionary<string, string> English => _english;
    static Dictionary<string, string> _english = new();

    public static bool Loaded { get; private set; }
    /// <summary>Why the tables aren't loaded (null once they are).</summary>
    public static string? Error { get; private set; } = "game tables not loaded yet";

    /// <summary>Reads &lt;dataDir&gt;/game_tables.json. On failure the tables stay empty and <see cref="Error"/> says why.</summary>
    public static void Load(string dataDir)
    {
        var path = Path.Combine(dataDir, FileName);
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"{FileName} is missing; run setup to import it from your copy of DropMix");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;

            var chimes = r.GetProperty("scoreChimes");
            var major = Ints(chimes.GetProperty("major"));
            var minor = Ints(chimes.GetProperty("minor"));
            var wheel = r.GetProperty("wheel");
            var slices = Slices(wheel.GetProperty("slices"));
            var partySlices = Slices(wheel.GetProperty("partySlices"));
            var freestyle = Stingers(r.GetProperty("freestyleStingers"));
            var round = Stingers(r.GetProperty("partyRoundStingers"));

            var fx = new Dictionary<string, MusicEffectDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in r.GetProperty("musicEffects").EnumerateObject()) fx[p.Name] = Effect(p.Value);

            var silence = r.GetProperty("silenceMusicEffect").GetString();
            var ids = r.GetProperty("clashPlayerIds").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var names = r.GetProperty("partyLeaderboardNames").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase);

            var bl = r.GetProperty("boardLights");
            var colours = bl.GetProperty("colours").EnumerateObject().ToDictionary(p => p.Name, p =>
            {
                var c = p.Value.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                return (c[0], c[1], c[2]);
            }, StringComparer.OrdinalIgnoreCase);
            var lights = new BoardLightSettings(bl.GetProperty("ledLatencyAdjustFactor").GetDouble(), bl.GetProperty("audioLatencyAdjust").GetDouble(), D(bl, "meterGain"), colours);
            var wheelColours = wheel.GetProperty("colours").EnumerateObject().ToDictionary(p => p.Name, p => Rgb(p.Value), StringComparer.Ordinal);

            var ga = r.GetProperty("gameAudio");
            var audio = new GameAudioSettings(D(ga, "startingTempo"), D(ga, "minTempo"), D(ga, "maxTempo"), I(ga, "loopLengthInBars"),
                D(ga, "accelerateTempoFloor"), I(ga, "accelerateTempoSubdivisionBars"), D(ga, "accelerateTempoAmount"));
            if (audio.LoopLengthInBars <= 0 || audio.StartingTempo <= 0 || audio.MinTempo <= 0 || audio.MaxTempo < audio.MinTempo)
                throw new InvalidOperationException("gameAudio values are out of range");
            int softVoices = I(r.GetProperty("fusionVoices"), "softVoiceLimit");

            var cr = r.GetProperty("clashRules");
            JsonElement ac = cr.GetProperty("ActionCounter"), sk = cr.GetProperty("ScoreKeeper"), hc = cr.GetProperty("CardsInHandCounter"), cc = cr.GetProperty("ClashConfig");
            var clash = new ClashRules(I(ac, "numActionsPerTurn"), I(sk, "fillMixBonus"), I(sk, "cardRemovalPenalty"), D(sk, "automaticTurnEndTime"),
                new[] { I(hc, "startingHandSize1v1"), I(hc, "startingHandSize2v2") }, new[] { I(hc, "maxHandSize1v1"), I(hc, "maxHandSize2v2") },
                new[] { I(hc, "drawSize1v1"), I(hc, "drawSize2v2") }, new[] { I(cc, "minHandSize1Player"), I(cc, "minHandSize2Players") },
                new[] { I(cc, "maxPowerBudget1Player"), I(cc, "maxPowerBudget2Players") }, new[] { I(cc, "maxDuplicateCards1Player"), I(cc, "maxDuplicateCards2Players") },
                I(cc, "stackHeightWarningThreshold"));

            var sn = r.GetProperty("snapshots");
            var snapshots = new SnapshotSettings(D(sn, "minimumSnapshotVolume"), I(sn, "minRequiredCards"), I(sn, "beatsBetweenCards"));
            var uiColours = r.GetProperty("uiColours").EnumerateObject().ToDictionary(p => p.Name, p =>
            {
                var c = p.Value.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                return (c[0], c[1], c[2]);
            }, StringComparer.Ordinal);
            var bloom = r.GetProperty("vuBloom").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.OrdinalIgnoreCase);

            ScoreChimesMajor = major; ScoreChimesMinor = minor;
            WheelSlices = slices; PartyWheelSlices = partySlices;
            FreestyleStingers = freestyle; PartyRoundStingers = round;
            MusicEffects = fx; SilenceMusicEffect = silence; ClashPlayerIds = ids; PartyLeaderboardNameKeys = names; BoardLights = lights;
            WheelColours = wheelColours; Audio = audio; FusionSoftVoiceLimit = softVoices; Clash = clash; Snapshots = snapshots; VuBloom = bloom; UiColours = uiColours;
            Loaded = true; Error = null;
            LoadFmodMix(r);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or UnauthorizedAccessException)
        {
            Loaded = false; FmodMix = null;
            Error = $"game tables: couldn't read {path} ({ex.Message})";
            Console.WriteLine(Error);
        }

        try
        {
            var loc = Path.Combine(dataDir, "loc", "strings.json");
            if (File.Exists(loc))
            {
                var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllBytes(loc));
                _english = all != null && all.TryGetValue("en", out var en) ? en : new();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { Console.WriteLine($"game tables: couldn't read strings.json ({ex.Message})"); }
    }

    /// <summary>The "fmodMix" section. Optional: without it (or if it's unreadable) <see cref="FmodMix"/> stays null and
    /// the reason is logged.</summary>
    static void LoadFmodMix(JsonElement r)
    {
        FmodMix = null;
        try
        {
            if (!r.TryGetProperty("fmodMix", out var m)) throw new KeyNotFoundException("no fmodMix section; run setup again to import it");
            FmodMix = new FmodMixSettings(Chain(m.GetProperty("cardSlot1")), Chain(m.GetProperty("cardMusic")), Chain(m.GetProperty("master")),
                Chain(m.GetProperty("crowd")), Chain(m.GetProperty("queueStingers")),
                Chain(m.GetProperty("chantEvent")), Chain(m.GetProperty("introEvent")), Chain(m.GetProperty("outroStingerEvent")),
                Automation(m.GetProperty("crowdLoop")),
                m.GetProperty("timelines").EnumerateObject().ToDictionary(p => p.Name, p => Timeline(p.Value), StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Console.WriteLine($"game tables: the FMOD mix isn't imported ({ex.Message}); audio plays through a neutral mix");
        }

        static EventTimeline Timeline(JsonElement t) => new(
            t.GetProperty("instruments").EnumerateArray().Select(i => new TimelineInstrument(D(i, "startSec"), D(i, "lengthSec"),
                i.GetProperty("samples").EnumerateArray().Select(s => s.GetString() ?? "").ToArray())).ToArray(),
            t.GetProperty("loops").EnumerateArray().Select(l => (D(l, "startSec"), D(l, "endSec"))).ToArray());
        static TrackAutomation Automation(JsonElement a) => new(a.GetProperty("parameter").GetString() ?? "",
            a.GetProperty("tracks").EnumerateObject().ToDictionary(t => t.Name, t => (IReadOnlyList<(double, double)>)t.Value.GetProperty("volumeDbPoints")
                .EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).OrderBy(p => p.Item1).ToArray(), StringComparer.OrdinalIgnoreCase));
        static MixChain Chain(JsonElement c) =>
            new(c.GetProperty("preFader").EnumerateArray().Select(MixFx).ToArray(), D(c, "volumeDb"), c.GetProperty("postFader").EnumerateArray().Select(MixFx).ToArray());
        static MixEffect MixFx(JsonElement e) => e.GetProperty("type").GetString() switch
        {
            "threeBandEq" => new ThreeBandEqEffect(D(e, "lowDb"), D(e, "midDb"), D(e, "highDb"), D(e, "lowCrossoverHz"), D(e, "highCrossoverHz"), I(e, "slope")),
            "parametricEq" => new ParametricEqEffect(D(e, "centerHz"), D(e, "octaves"), D(e, "gainDb")),
            "compressor" => new CompressorEffect(D(e, "thresholdDb"), D(e, "ratio"), D(e, "attackMs"), D(e, "releaseMs"), D(e, "makeupDb")),
            "limiter" => new LimiterEffect(D(e, "releaseMs"), D(e, "ceilingDb"), D(e, "maximizerDb")),
            var k => new OtherEffect(k ?? "unknown"),
        };
    }

    /// <summary>Picks a slice by weight; 0 (no effect) when there are no slices.</summary>
    public static int SpinWheel(IReadOnlyList<WheelSlice> slices, Random rng)
    {
        double total = slices.Sum(s => Math.Max(0, s.Weight));
        if (total <= 0) return 0;
        double x = rng.NextDouble() * total;
        foreach (var s in slices)
        {
            if (s.Weight <= 0) continue;
            if (x < s.Weight) return s.Value;
            x -= s.Weight;
        }
        return slices.Last(s => s.Weight > 0).Value;
    }

    /// <summary>The localised Party leaderboard default name for a deck, or null when the deck has none.</summary>
    public static string? PartyLeaderboardName(string? deck) =>
        deck != null && PartyLeaderboardNameKeys.TryGetValue(deck, out var key) ? Localize(key) : null;

    static double D(JsonElement o, string k) => o.GetProperty(k).GetDouble();
    static int I(JsonElement o, string k) => o.GetProperty(k).GetInt32();
    /// <summary>A dumped Unity colour {r, g, b, a} → RGB.</summary>
    static (double, double, double) Rgb(JsonElement c) => (D(c, "r"), D(c, "g"), D(c, "b"));
    static int[] Ints(JsonElement a) => a.EnumerateArray().Select(x => x.GetInt32()).ToArray();
    static WheelSlice[] Slices(JsonElement a) =>
        a.EnumerateArray().Select(s => new WheelSlice(s.GetProperty("value").GetInt32(), s.GetProperty("weight").GetDouble())).ToArray();
    static Stinger[] Stingers(JsonElement a) =>
        a.EnumerateArray().Select(s => new Stinger(s.GetProperty("dir").GetString()!, s.GetProperty("variant").GetString()!)).ToArray();

    static MusicEffectDef Effect(JsonElement e)
    {
        static JsonElement? P(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;
        static double D(JsonElement o, string k) => o.GetProperty(k).GetDouble();
        return new MusicEffectDef(
            e.GetProperty("persist").GetString()!,
            P(e, "crush") is { } c ? (c.GetProperty("bits").GetInt32(), c.GetProperty("down").GetInt32(), D(c, "wet")) : null,
            P(e, "lowPass") is { } l ? (D(l, "freq"), D(l, "q")) : null,
            P(e, "delay") is { } d ? (D(d, "beatDelay"), D(d, "dry"), D(d, "wet"), D(d, "feedback")) : null,
            P(e, "vibe") is { } v ? (v.GetProperty("freqs").EnumerateArray().Select(x => x.GetDouble()).ToArray(), D(v, "depth"), D(v, "rate"), D(v, "wet"), D(v, "shape")) : null,
            P(e, "wah") is { } w ? (D(w, "closed"), D(w, "open"), D(w, "closedQ"), D(w, "openQ"), D(w, "rate"), D(w, "shape")) : null,
            P(e, "gainDb")?.GetDouble(),
            P(e, "keyUp")?.GetInt32() ?? 0,
            P(e, "tempo")?.GetInt32() ?? 0,
            P(e, "toggleMode")?.GetBoolean() ?? false);
    }
}
