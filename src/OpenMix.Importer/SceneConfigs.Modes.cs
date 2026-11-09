using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenMix.Importer;

/// <summary>Mode tuning read from the user's own copy of the game: Lanes (Puzzle) and Party settings from the scene
/// dump (<see cref="SceneDump"/>), and the coin-flip landing curve from <c>global-metadata.dat</c>. Only class and
/// field names live here; every value comes from the user's files.</summary>
public static partial class SceneConfigs
{
    const string ModesNote = "Imported from your copy of DropMix by OpenMix.";

    // ---- Lanes ----

    static readonly (string Class, string[] Fields)[] LanesObjects =
    {
        ("LanesGameMgr", new[] { "playsPerRound", "goldBlockThreshold", "goldBlockBonus", "rainbowBlockThreshold", "rainbowBlockBonus",
            "endTurnTimer", "hardModeEndTurnTimer", "endTurnSlopTime", "laneClearBonusPerRequest", "blockSizeBonusMultiplier",
            "scoreToUnlockHardMode", "fixSlotsButton" }),
        ("LanesRequestMgr", new[] { "measuresForSuccessorRamp", "successorFrequencies", "pulsesPerStep", "eqSpawnRate", "alwaysFillGrid",
            "ySpacing", "yOffset", "xSpacing", "xOffset", "showGrid", "eqLaneClearStingerPath", "eqRequestPowerMultiplier",
            "yellowGreenSuccessorBoost", "clearMatchingFXPowerLevels", "spawnEQRequests", "enableOverflowProtection",
            "emptyLaneBonusSpawns", "restrictFailClearToLane", "mIsBoardValid", "requestInfo" }),
        ("LanesGameEndScreen", new[] { "gameOverScreenSeconds" }),
        ("WheelOfMisfortune", new[] { "slices", "displayMode", "slicesMode", "partySlicesMode" }),
        ("GameAudio", new[] { "pointsSfxThreshold", "maxScoringCards", "loopLengthInBars", "startingTempo", "minTempo", "maxTempo",
            "cardDuckingValueDucked", "cardDuckingValueUnducked", "cardDuckingSeconds", "cardUnduckingSeconds", "accelerateTempoFloor",
            "accelerateTempoSubdivisionBars", "accelerateTempoAmount", "audioLatencyAdjust" }),
    };

    /// <summary>Request-type enum members Lanes uses (names only; values come from the user's Il2CppDumper output).</summary>
    static readonly string[] LanesRequestTypeNames =
        { "Level1", "Level2", "Level3", "Equalizer", "Lead", "Loop", "Beat", "Bass", "Synth", "Guitar", "Horns", "Strings", "Vocal", "Drums", "None", "Sampler" };

    /// <summary>Writes &lt;dataDir&gt;/lanes/lanes_config.json from the level0 scene objects.</summary>
    public static void BuildLanes(string sceneDir, string dataDir, Action<string> log)
    {
        var root = new JsonObject { ["_source"] = ModesNote };
        foreach (var (cls, fields) in LanesObjects)
            root[cls] = ModesPick(ModesLoad(sceneDir, cls, log), cls, fields);

        var en = LanesRequestTypes(sceneDir);
        if (en != null) root["requestTypeEnum"] = en;
        else log("lanes: request-type enum not found (dump.cs missing); requestTypeEnum left out");

        ModesWrite(Path.Combine(dataDir, "lanes", "lanes_config.json"), root);
        log("lanes: config imported");
    }

    static JsonObject? LanesRequestTypes(string sceneDir)
    {
        string? dump = new[]
        {
            Path.Combine(sceneDir, "..", "il2cpp", "out", "dump.cs"),
            Path.Combine(sceneDir, "..", "il2", "dump.cs"),
            Path.Combine(sceneDir, "..", "dump.cs"),
        }.FirstOrDefault(File.Exists);
        if (dump == null) return null;

        // Find the enum block whose constants include the Lanes request names (the enum's own name is obfuscated).
        var constRx = new Regex(@"^\s*public const \S+ (\w+) = (-?\d+);");
        Dictionary<string, long>? cur = null;
        foreach (var line in File.ReadLines(dump))
        {
            if (line.StartsWith("public enum ")) { cur = new(); continue; }
            if (cur == null) continue;
            if (line.StartsWith("}"))
            {
                if (LanesRequestTypeNames.All(cur.ContainsKey))
                {
                    var o = new JsonObject();
                    foreach (var n in LanesRequestTypeNames.OrderBy(n => cur[n])) o[n] = cur[n];
                    return o;
                }
                cur = null;
                continue;
            }
            var m = constRx.Match(line);
            if (m.Success) cur[m.Groups[1].Value] = long.Parse(m.Groups[2].Value);
        }
        return null;
    }

    // ---- Party ----

    static readonly string[] PartyFields =
    {
        "timingRandomness", "gameIntroSeconds", "roundIntroSeconds", "roundBreakSeconds", "perfectScoreGracePeriod",
        "minimumRequestScore", "handSizeForPlayerCount", "partyStartRound", "roundFusionTransitions",
    };

    /// <summary>Writes &lt;dataDir&gt;/party/party_config.json from the Party scene's PartyMode object.</summary>
    public static void BuildParty(string sceneDir, string dataDir, Action<string> log)
    {
        var root = ModesPick(ModesLoad(sceneDir, "PartyMode", log), "PartyMode", PartyFields);
        // how long the bonus bumper and the foul banner stay up
        foreach (var cls in new[] { "PartyBonusBumper", "PartyFoulDisplay" })
            root[cls] = ModesPick(ModesLoad(sceneDir, cls, log), cls, new[] { "displayTime" });
        root["_source"] = ModesNote;
        ModesWrite(Path.Combine(dataDir, "party", "party_config.json"), root);
        log("party: config imported");
    }

    // ---- Coin-flip curve ----

    /// <summary>Locator for the static array WheelOfMisfortune.Start reads (a &lt;PrivateImplementationDetails&gt; field).</summary>
    const string CoinFieldPrefix = "7C2F61BE";
    const int CoinSamples = 260;

    /// <summary>Writes &lt;contentRoot&gt;/assets_raw/obb_ui/wheel_coinflip_curve.json from the static array initializer in
    /// the user's <c>global-metadata.dat</c> (v24).</summary>
    public static void BuildCoinCurve(string metadataPath, string contentRoot, Action<string> log)
    {
        var data = MetaFieldData(File.ReadAllBytes(metadataPath), CoinFieldPrefix, CoinSamples * 4, s => log("coin curve: " + s));
        var table = new JsonArray();
        for (int i = 0; i < CoinSamples; i++)
        {
            double v = Math.Round((double)BitConverter.ToSingle(data.Slice(i * 4, 4)), 6);
            table.Add(v == 0 ? 0.0 : v);
        }

        var root = new JsonObject
        {
            ["source"] = ModesNote,
            ["usage"] = "spinDown coin flip: p=1-beatsLeft/8; f=p*260; i=floor(f); pos=lerp(t[clamp(i)],t[clamp(i+1)],f-i)+target; wrap to [0,1)",
            ["table"] = table,
        };
        ModesWrite(Path.Combine(contentRoot, "assets_raw", "obb_ui", "wheel_coinflip_curve.json"), root);
        log($"coin curve: {CoinSamples} samples imported");
    }

    // ---- shared helpers ----

    /// <summary>Loads the dumped MonoBehaviour for <paramref name="cls"/> ("&lt;Class&gt; @&lt;PathID&gt;.json").</summary>
    static JsonObject ModesLoad(string sceneDir, string cls, Action<string> log)
    {
        var files = Directory.EnumerateFiles(sceneDir, cls + " @*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0) throw new FileNotFoundException($"scene object {cls} not found in the scene dump");
        if (files.Count > 1) log($"{cls}: {files.Count} objects in the scene dump, using {Path.GetFileName(files[0])}");
        return JsonNode.Parse(File.ReadAllText(files[0]))!.AsObject();
    }

    static JsonObject ModesPick(JsonObject src, string cls, string[] fields)
    {
        var o = new JsonObject();
        foreach (var f in fields)
        {
            if (!src.TryGetPropertyValue(f, out var v)) throw new InvalidDataException($"{cls}.{f} missing from the scene dump");
            o[f] = ModesValue(v);
        }
        return o;
    }

    /// <summary>Copies a dumped value; AnimationCurves become [time,value] keyframe lists (their tangents are flat).</summary>
    static JsonNode? ModesValue(JsonNode? v)
    {
        if (v is JsonObject o && o.TryGetPropertyValue("m_Curve", out var keys) && keys is JsonArray ka)
        {
            var a = new JsonArray();
            foreach (var k in ka) a.Add(new JsonArray(k!["time"]!.DeepClone(), k["value"]!.DeepClone()));
            return a;
        }
        if (v is JsonArray arr)
        {
            var a = new JsonArray();
            foreach (var x in arr) a.Add(ModesValue(x));
            return a;
        }
        if (v is JsonObject obj)
        {
            var c = new JsonObject();
            foreach (var (k, x) in obj)
                if (!ModesIsRef(x)) c[k] = ModesValue(x); // object references (sprites etc.) mean nothing outside Unity
            return c;
        }
        return v?.DeepClone();
    }

    static bool ModesIsRef(JsonNode? v) => v is JsonObject o && o.Count == 2 && o.ContainsKey("m_FileID") && o.ContainsKey("m_PathID");

    static void ModesWrite(string path, JsonNode root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }
}
