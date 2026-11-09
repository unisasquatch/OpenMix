using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenMix.Importer;

/// <summary>Writes data/game_tables.json: the small game tables the engine needs (score chimes, Wheel of Misfortune
/// odds, DropMix-button stingers, slot music effects, Clash decks and Party leaderboard names, board-light colours),
/// read from the user's own scene objects (<see cref="SceneDump"/>) and <c>global-metadata.dat</c>. Only class, field
/// and enum-member names live here; every value comes from the user's files.</summary>
public static partial class SceneConfigs
{
    /// <summary>Locators of the score-chime note tables (static arrays in &lt;PrivateImplementationDetails&gt;):
    /// major, then minor; 35 int32 notes each.</summary>
    const string ChimeMajorField = "17F6BF14", ChimeMinorField = "CCC8EA3A";
    const int ChimeNotes = 35;

    /// <summary>The music-effect components OpenMix understands (classes deriving from the controller's base effect).</summary>
    static readonly string[] EffectClasses =
    {
        "BitcrushEffect", "FilterEffect", "DelayEffect", "VibeEffect", "WahEffect", "GainEffect",
        "ChangeKeyEffect", "ChangeTempoEffect", "ToggleKeyModeEffect", "DistortEffect",
    };

    /// <summary>ClashConfig colour fields for the board lights → OpenMix's names for them.</summary>
    static readonly (string field, string key)[] LedColourFields =
    {
        ("leadLEDColor", "lead"), ("loopLEDColor", "loop"), ("beatLEDColor", "beat"), ("bassLEDColor", "bass"), ("fxLEDColor", "fx"),
        ("wildLeadLoopLEDColor", "wildLeadLoop"), ("wildLoopBeatLEDColor", "wildLoopBeat"), ("wildBeatBassLEDColor", "wildBeatBass"),
        ("collectionOwnedColor", "collectionOwned"),
    };

    /// <param name="fmodMix">The FMOD mixer chains read from Master Bank.bank (<see cref="FmodMix"/>), or null.</param>
    public static void BuildGameTables(string sceneDir, string metadataPath, string dataDir, Action<string> log, JsonObject? fmodMix = null)
    {
        var root = new JsonObject { ["_source"] = ModesNote };

        // score chimes
        var meta = File.ReadAllBytes(metadataPath);
        root["scoreChimes"] = new JsonObject
        {
            ["major"] = MetaInts(meta, ChimeMajorField, ChimeNotes, log),
            ["minor"] = MetaInts(meta, ChimeMinorField, ChimeNotes, log),
        };

        // Wheel of Misfortune: OpenMix implements the "specified slices" wheel only
        var wheel = ModesLoad(sceneDir, "WheelOfMisfortune", log);
        var sliceModes = DumpEnum(sceneDir, "kSpecifiedSlices", "kEvenSlots", "kHeavyEnds");
        JsonArray Slices(string modeField)
        {
            int m = wheel[modeField]!.GetValue<int>();
            var name = sliceModes.TryGetValue(m, out var n) ? n : m.ToString();
            if (name != "kSpecifiedSlices") throw new InvalidDataException($"WheelOfMisfortune.{modeField} is {name}; OpenMix supports only kSpecifiedSlices");
            var a = new JsonArray();
            foreach (var s in wheel["slices"]!.AsArray())
                a.Add(new JsonObject { ["value"] = s!["value"]!.GetValue<int>(), ["weight"] = s["weight"]!.GetValue<double>() });
            return a;
        }
        root["wheel"] = new JsonObject { ["slices"] = Slices("slicesMode"), ["partySlices"] = Slices("partySlicesMode") };

        // DropMix-button stingers (Freestyle) and Party round transitions
        root["freestyleStingers"] = Stingers(ModesLoad(sceneDir, "Menu_StudioMode", log)["eqFusionTransitions"]!.AsArray());
        root["partyRoundStingers"] = Stingers(ModesLoad(sceneDir, "PartyMode", log)["roundFusionTransitions"]!.AsArray());

        // slot music effects
        root["musicEffects"] = MusicEffects(sceneDir, log, out var effectByPid);

        // Clash decks and Party leaderboard default names (text keys; the text itself is in data/loc/strings.json)
        var clash = ModesLoad(sceneDir, "ClashConfig", log);
        var tokens = WtTokens(sceneDir);
        var ids = new JsonArray(); var names = new JsonObject();
        foreach (var p in clash["playerIds"]!.AsArray())
        {
            var id = p!["id"]!.GetValue<string>();
            ids.Add(id);
            int tok = p["partyLeaderboardDefaultName"]!.GetValue<int>();
            if (tok != 0) names[id] = WtTokenName(tokens, tok);
        }
        root["clashPlayerIds"] = ids;
        // the effect a silenced slot gets (ClashConfig.silenceMusicEffect, a MusicEffectController in level0)
        var sil = clash["silenceMusicEffect"]!;
        long silPid = sil["m_PathID"]!.GetValue<long>();
        if (sil["m_FileID"]!.GetValue<int>() != 0 || !effectByPid.TryGetValue(silPid, out var silName))
            throw new InvalidDataException("ClashConfig.silenceMusicEffect doesn't point at a music effect in the scene");
        root["silenceMusicEffect"] = silName;
        root["partyLeaderboardNames"] = names;

        // board lights: the colours the original configured, and its LED latency factor
        var colours = new JsonObject();
        foreach (var (field, key) in LedColourFields)
        {
            var c = clash[field] ?? throw new InvalidDataException($"ClashConfig.{field} missing from the scene dump");
            colours[key] = new JsonArray(c["r"]!.GetValue<double>(), c["g"]!.GetValue<double>(), c["b"]!.GetValue<double>());
        }
        var blc = ModesLoad(sceneDir, "BoardLightController", log);
        var audio = ModesLoad(sceneDir, "GameAudio", log);
        root["boardLights"] = new JsonObject
        {
            ["ledLatencyAdjustFactor"] = blc["ledLatencyAdjustFactor"]!.GetValue<double>(),
            // the gain on a card's precomputed meter when it drives the slot lights (CardMeterDatabase.gain)
            ["meterGain"] = ModesLoad(sceneDir, "CardMeterDatabase", log)["gain"]?.GetValue<double>()
                ?? throw new InvalidDataException("CardMeterDatabase.gain missing from the scene dump"),
            ["audioLatencyAdjust"] = audio["audioLatencyAdjust"]!.GetValue<double>(),
            ["colours"] = colours,
        };

        // the music clock: starting tempo, the tempo range, the in-game tempo creep and the master loop length
        root["gameAudio"] = ModesPick(audio, "GameAudio", GameAudioFields);
        // the Fusion synth's shared voice pool (HmxAudioMgr init config)
        var hmx = ModesLoad(sceneDir, "HmxAudioMgr", log);
        var fusion = hmx["initConfig"]?["fusionConfig"] as JsonObject ?? throw new InvalidDataException("HmxAudioMgr.initConfig.fusionConfig missing from the scene dump");
        root["fusionVoices"] = ModesPick(fusion, "HmxAudioMgr.initConfig.fusionConfig", new[] { "softVoiceLimit" });

        // Clash turn, hand and deck rules
        var rules = new JsonObject();
        foreach (var (cls, fields) in ClashRuleObjects)
            rules[cls] = ModesPick(cls == "ClashConfig" ? clash : ModesLoad(sceneDir, cls, log), cls, fields);
        root["clashRules"] = rules;

        // saved mixes (SnapshotManager)
        root["snapshots"] = ModesPick(ModesLoad(sceneDir, "SnapshotManager", log), "SnapshotManager", new[] { "minimumSnapshotVolume", "minRequiredCards" });
        root["snapshots"]!["beatsBetweenCards"] = ModesLoad(sceneDir, "Menu_SnapshotViewer", log)["beatsBetweenCards"]?.GetValue<int>()
            ?? throw new InvalidDataException("Menu_SnapshotViewer.beatsBetweenCards missing from the scene dump");

        // the wheel's slice and glyph tints
        root["wheel"]!["colours"] = ModesPick(wheel, "WheelOfMisfortune", WheelColourFields);

        // screen colours the original configured for elements OpenMix draws the same way: Party request tiles and the
        // Clash score pop-ups
        var ui = new JsonObject();
        foreach (var (cls, fields) in UiColourObjects)
        {
            var o = ModesLoad(sceneDir, cls, log);
            foreach (var f in fields)
            {
                var c = o[f] ?? throw new InvalidDataException($"{cls}.{f} missing from the scene dump");
                ui[$"{cls}.{f}"] = new JsonArray(c["r"]!.GetValue<double>(), c["g"]!.GetValue<double>(), c["b"]!.GetValue<double>());
            }
        }
        root["uiColours"] = ui;

        // slot VU meters: the bloom per instrument (the set most VUMeterTriangle objects share)
        root["vuBloom"] = VuBloom(sceneDir);

        // the FMOD mixer: effect chains and fader levels of the card slot event, CardMusic, master and SFX buses
        if (fmodMix != null) root["fmodMix"] = fmodMix;

        ModesWrite(Path.Combine(dataDir, "game_tables.json"), root);
        log($"game tables: {ids.Count} decks, {names.Count} party names, {root["musicEffects"]!.AsObject().Count} music effects");
    }

    static readonly string[] GameAudioFields =
        { "startingTempo", "minTempo", "maxTempo", "loopLengthInBars", "accelerateTempoFloor", "accelerateTempoSubdivisionBars", "accelerateTempoAmount" };

    static readonly (string Class, string[] Fields)[] ClashRuleObjects =
    {
        ("ActionCounter", new[] { "numActionsPerTurn" }),
        ("ScoreKeeper", new[] { "fillMixBonus", "cardRemovalPenalty", "automaticTurnEndTime" }),
        ("CardsInHandCounter", new[] { "startingHandSize1v1", "startingHandSize2v2", "maxHandSize1v1", "maxHandSize2v2", "drawSize1v1", "drawSize2v2" }),
        ("ClashConfig", new[] { "minHandSize1Player", "minHandSize2Players", "maxPowerBudget1Player", "maxPowerBudget2Players",
            "maxDuplicateCards1Player", "maxDuplicateCards2Players", "stackHeightWarningThreshold" }),
    };

    static readonly string[] WheelColourFields =
    {
        "sliceNormalColor", "sliceHighlightColor", "sliceSelectedColor", "volumeNormalColor", "volumeDimmedColor",
        "volumeHighlightColor", "volumeSelectedColor", "bonusNormalColor", "bonusHighlightColor", "bonusSelectedColor",
    };

    static readonly (string Class, string[] Fields)[] UiColourObjects =
    {
        ("PartyRequest", new[] { "perfectBackgroundColor", "failedBackgroundColor" }),
        ("SlotScoreIndicator", new[] { "slotScoreColor", "fxScoreColor", "fxScoreBackgroundColor", "mixFillScoreColor" }),
    };

    static readonly (string field, string key)[] VuBloomFields =
        { ("bloomLead", "lead"), ("bloomLoop", "loop"), ("bloomBeat", "beat"), ("bloomBass", "bass"), ("bloomFX", "fx") };

    /// <summary>VUMeterTriangle bloom per instrument: the most common set across the dumped meters.</summary>
    static JsonObject VuBloom(string sceneDir)
    {
        var sets = new Dictionary<string, (int n, JsonObject o)>();
        foreach (var f in Directory.EnumerateFiles(sceneDir, "VUMeterTriangle @*.json", SearchOption.AllDirectories))
        {
            var j = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
            var o = new JsonObject();
            foreach (var (field, key) in VuBloomFields)
                o[key] = j[field]?.GetValue<double>() ?? throw new InvalidDataException($"VUMeterTriangle.{field} missing from the scene dump");
            var k = o.ToJsonString();
            sets[k] = sets.TryGetValue(k, out var s) ? (s.n + 1, s.o) : (1, o);
        }
        if (sets.Count == 0) throw new FileNotFoundException("scene object VUMeterTriangle not found in the scene dump");
        return sets.Values.OrderByDescending(s => s.n).First().o;
    }

    /// <summary>"fusion/&lt;dir&gt;/&lt;variant&gt;.midisong" → {dir, variant}.</summary>
    static JsonArray Stingers(JsonArray paths)
    {
        var a = new JsonArray();
        foreach (var p in paths)
        {
            var s = p!.GetValue<string>().Replace('\\', '/');
            if (s.StartsWith("fusion/", StringComparison.Ordinal)) s = s["fusion/".Length..];
            int slash = s.LastIndexOf('/');
            if (slash <= 0) throw new InvalidDataException($"unexpected stinger path {p}");
            a.Add(new JsonObject { ["dir"] = s[..slash], ["variant"] = Path.GetFileNameWithoutExtension(s[(slash + 1)..]) });
        }
        return a;
    }

    /// <summary>Every MusicEffectController in level0, by its GameObject's name (the effect prefab name), with the DSP
    /// and music-change components on the same GameObject mapped onto OpenMix's slot-effect parameters.</summary>
    static JsonObject MusicEffects(string sceneDir, Action<string> log, out Dictionary<long, string> byPid)
    {
        var dir = Path.Combine(sceneDir, "level0_export");
        var uf = WtUnityFile.Open(WtCombinedFile(sceneDir, "level0")!);
        var persist = DumpEnum(sceneDir, "UntilCardRemoval", "UntilTurnEnd", "UntilBarCountdown");
        var filterTypes = DumpEnum(sceneDir, "kLowPass", "kHighPass", "kBandPass");

        static long Go(JsonElement j) => j.GetProperty("m_GameObject").GetProperty("m_PathID").GetInt64();
        var parts = new Dictionary<long, List<(string cls, JsonElement j)>>();
        foreach (var cls in EffectClasses)
            foreach (var f in Directory.EnumerateFiles(dir, cls + " @*.json"))
            {
                using var d = JsonDocument.Parse(File.ReadAllBytes(f));
                var j = d.RootElement.Clone();
                if (!parts.TryGetValue(Go(j), out var l)) parts[Go(j)] = l = new();
                l.Add((cls, j));
            }

        var all = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        byPid = new();
        foreach (var f in Directory.EnumerateFiles(dir, "MusicEffectController @*.json"))
        {
            long pid = long.Parse(WtDumpName.Match(Path.GetFileName(f)).Groups["pid"].Value);
            using var d = JsonDocument.Parse(File.ReadAllBytes(f));
            var mec = d.RootElement;
            long go = Go(mec);
            var name = uf.GameObjectName(go) ?? throw new InvalidDataException($"music effect object {go} has no GameObject name");
            byPid[pid] = name;
            int pt = mec.GetProperty("persistenceType").GetInt32();
            var o = new JsonObject
            {
                ["persist"] = (persist.TryGetValue(pt, out var pn) ? pn : null) switch
                {
                    "UntilCardRemoval" => "card",
                    "UntilTurnEnd" => "turn",
                    "UntilBarCountdown" => mec.GetProperty("numBars").GetInt32().ToString(),
                    _ => throw new InvalidDataException($"{name}: unknown persistence type {pt}"),
                },
            };
            static double D(JsonElement j, string k) => j.GetProperty(k).GetDouble();
            foreach (var (cls, j) in parts.TryGetValue(go, out var l) ? l : new())
                switch (cls)
                {
                    case "BitcrushEffect":   // wetMix is a percentage
                        o["crush"] = new JsonObject { ["bits"] = (int)D(j, "crush"), ["down"] = j.GetProperty("downsample").GetInt32(), ["wet"] = D(j, "wetMix") / 100 };
                        break;
                    case "FilterEffect":
                        int ft = j.GetProperty("type").GetInt32();
                        if (filterTypes.TryGetValue(ft, out var fn) && fn == "kLowPass") o["lowPass"] = new JsonObject { ["freq"] = D(j, "f"), ["q"] = D(j, "q") };
                        else log($"{name}: filter type {(filterTypes.TryGetValue(ft, out var x) ? x : ft)} isn't supported, left out");
                        break;
                    case "DelayEffect":
                        o["delay"] = new JsonObject { ["beatDelay"] = D(j, "beatDelay"), ["dry"] = D(j, "dry"), ["wet"] = D(j, "wet"), ["feedback"] = D(j, "feedback") };
                        break;
                    case "VibeEffect":
                        o["vibe"] = new JsonObject
                        {
                            ["freqs"] = new JsonArray(D(j, "freq1"), D(j, "freq2"), D(j, "freq3"), D(j, "freq4")),
                            ["depth"] = D(j, "lfoDepth"), ["rate"] = D(j, "lfoRate"), ["wet"] = D(j, "wetMix"), ["shape"] = D(j, "lfoShape"),
                        };
                        break;
                    case "WahEffect":
                        o["wah"] = new JsonObject
                        {
                            ["closed"] = D(j, "closedFreq"), ["open"] = D(j, "openFreq"), ["closedQ"] = D(j, "closedQ"), ["openQ"] = D(j, "openQ"),
                            ["rate"] = D(j, "lfoRate"), ["shape"] = D(j, "lfoShape"),
                        };
                        break;
                    case "GainEffect": o["gainDb"] = D(j, "gainDb"); break;
                    case "ChangeKeyEffect": o["keyUp"] = j.GetProperty("semitones").GetInt32(); break;
                    case "ChangeTempoEffect": o["tempo"] = j.GetProperty("amount").GetInt32(); break;
                    case "ToggleKeyModeEffect": o["toggleMode"] = true; break;
                    default: log($"{name}: {cls} isn't supported, left out"); break;
                }
            if (!all.TryAdd(name, o)) log($"music effects: {name} appears twice, keeping the first");
        }
        var res = new JsonObject();
        foreach (var (k, v) in all) res[k] = v;
        return res;
    }

    /// <summary>Value → member name of the dump.cs enum that has all of <paramref name="members"/>.</summary>
    static Dictionary<int, string> DumpEnum(string sceneDir, params string[] members)
    {
        var work = Path.GetDirectoryName(Path.GetFullPath(sceneDir).TrimEnd('\\', '/'))!;
        var dump = Path.Combine(work, "il2cpp", "out", "dump.cs");
        if (!File.Exists(dump)) throw new FileNotFoundException("Il2CppDumper's dump.cs is missing next to the scene dump.");
        var constRx = new Regex(@"^\s*public const \S+ (\w+) = (-?\d+);");
        Dictionary<string, int>? cur = null;
        foreach (var line in File.ReadLines(dump))
        {
            if (line.StartsWith("public enum ")) { cur = new(); continue; }
            if (cur == null) continue;
            if (line.StartsWith("}"))
            {
                if (members.All(cur.ContainsKey)) return cur.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
                cur = null; continue;
            }
            var m = constRx.Match(line);
            if (m.Success && int.TryParse(m.Groups[2].Value, out var v)) cur[m.Groups[1].Value] = v;
        }
        throw new InvalidDataException($"enum with {string.Join(", ", members)} not found in dump.cs");
    }

    // ---- static array initializers in global-metadata.dat ----

    /// <summary>The default data of the first field whose name contains <paramref name="fieldLocator"/> (a
    /// &lt;PrivateImplementationDetails&gt; array initializer), at least <paramref name="bytes"/> long.</summary>
    static ReadOnlySpan<byte> MetaFieldData(byte[] d, string fieldLocator, int bytes, Action<string> log)
    {
        if (BitConverter.ToUInt32(d, 0) != 0xFAB11BAF) throw new InvalidDataException("global-metadata.dat isn't IL2CPP metadata");
        int version = BitConverter.ToInt32(d, 4);
        if (version != 24) log($"metadata version {version} (expected 24), trying anyway");
        int H(int at) => BitConverter.ToInt32(d, at);
        int strOff = H(24), strBytes = H(28);
        int fdvOff = H(64), fdvBytes = H(68);       // fieldDefaultValues: {fieldIndex, typeIndex, dataIndex}
        int dataOff = H(72);                        // fieldAndParameterDefaultValueData
        int fieldsOff = H(96), fieldsBytes = H(100); // fields: {nameIndex, typeIndex, [customAttributeIndex,] token}
        // v24.0 field records carry a customAttributeIndex (16 bytes); v24.1+ drop it (12 bytes). The last word is a
        // metadata token in the Field table (0x04xxxxxx), which tells the two apart.
        int stride = (H(fieldsOff + 12) >>> 24) == 0x04 && fieldsBytes % 16 == 0 ? 16 : 12;
        if ((H(fieldsOff + stride - 4) >>> 24) != 0x04) throw new InvalidDataException("unrecognised field table layout in global-metadata.dat");

        string Str(int idx)
        {
            if (idx < 0 || idx >= strBytes) return "";
            int s = strOff + idx, e = s;
            while (d[e] != 0) e++;
            return Encoding.UTF8.GetString(d, s, e - s);
        }

        int field = -1;
        for (int i = 0; i < fieldsBytes / stride; i++)
        {
            var name = Str(H(fieldsOff + i * stride));
            int at = name.IndexOf(fieldLocator, StringComparison.Ordinal);
            if (at >= 0 && (at == 0 || !char.IsLetterOrDigit(name[at - 1]))) { field = i; break; }
        }
        if (field < 0) throw new InvalidDataException($"field {fieldLocator} not found in global-metadata.dat");

        int dataIndex = -1;
        for (int i = 0; i < fdvBytes / 12; i++)
            if (H(fdvOff + i * 12) == field) { dataIndex = H(fdvOff + i * 12 + 8); break; }
        if (dataIndex < 0) throw new InvalidDataException($"field {fieldLocator} has no initializer data in global-metadata.dat");

        int p = dataOff + dataIndex;
        if (p + bytes > d.Length) throw new InvalidDataException($"field {fieldLocator} data runs past the end of the file");
        return d.AsSpan(p, bytes);
    }

    static JsonArray MetaInts(byte[] d, string fieldLocator, int count, Action<string> log)
    {
        var span = MetaFieldData(d, fieldLocator, count * 4, log);
        var a = new JsonArray();
        for (int i = 0; i < count; i++) a.Add(BitConverter.ToInt32(span.Slice(i * 4, 4)));
        return a;
    }
}
