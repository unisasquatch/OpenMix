using System.Text;
using System.Text.Json.Nodes;

namespace OpenMix.Importer;

/// <summary>Reads the original's FMOD Studio mixer settings from the user's own <c>Master Bank.bank</c> for
/// data/game_tables.json ("fmodMix"): the effect chains and fader levels of the buses and events (their master tracks)
/// OpenMix reproduces. Buses and events are found by path through the <c>GUIDs.txt</c> FMOD Studio ships next to the banks
/// in the OBB; nothing here depends on byte offsets of particular objects.
///
/// Bank layout used: a RIFF file whose LIST objects (GBUS group bus, MBUS master bus or event master track, EVNT
/// event, BEFF built-in effect, …) start with a chunk holding the object's GUID. A bus's <c>BUS </c> chunk is: u8,
/// u32, the pre-fader effect GUIDs, the post-fader effect GUIDs, then u16 and the fader volume (f32 dB). Each GUID
/// list is u16 (count·2 + 1), then (when the count isn't 0) u16 element size and the elements. A built-in effect's
/// <c>BEFB</c> chunk has the FMOD_DSP_TYPE at byte 20; its <c>PMEF</c> chunk is the parameter list: u32 count, then
/// per parameter u32 kind (0 float, 1 int, 2 bool, 3 string) and the value (string: u16 length + bytes).</summary>
public static class FmodMix
{
    /// <summary>The bus and event paths OpenMix reproduces → their key in "fmodMix".</summary>
    static readonly (string path, string key, bool isEvent)[] Targets =
    {
        ("bus:/", "master", false),
        ("bus:/CardMusic", "cardMusic", false),
        ("bus:/Crowd_Audio", "crowd", false),
        ("bus:/QueueStingers", "queueStingers", false),
        ("event:/CardSlots/cardSlot1", "cardSlot1", true),
        ("event:/Crowd/silence_cheer_chant", "chantEvent", true),
        ("event:/Actions/Intro", "introEvent", true),
        ("event:/Actions/OutroStinger", "outroStingerEvent", true),
    };

    // FMOD_DSP_TYPE values of the effects OpenMix implements (parameters are read in FMOD's documented order)
    const int DspLimiter = 11, DspParamEq = 12, DspCompressor = 18, DspThreeEq = 28;

    /// <summary>The crowd loop event whose track volumes follow a parameter (OpenMix's Waiting/Normal/Hyped crowd).</summary>
    const string CrowdLoopEvent = "event:/Crowd/crowd_loop_main";

    sealed record Obj(string Type, Dictionary<string, (int start, int end)> Chunks);

    /// <summary>Everything indexed from one bank: objects by GUID (first wins), objects by type and GUID (several object
    /// kinds share a GUID, e.g. a parameter and its layout), and the loose CTRL (automation), CURV (curve) and WAV
    /// (waveform → sample index) chunks.</summary>
    sealed class Index
    {
        public readonly Dictionary<Guid, Obj> Objs = new();
        public readonly Dictionary<(string, Guid), Obj> Typed = new();
        public readonly List<(int start, int end)> Controls = new();
        public readonly Dictionary<Guid, (int start, int end)> Curves = new();
        public readonly Dictionary<Guid, int> WaveSample = new();
    }

    public static JsonObject Read(string bankPath, string guidsPath, Action<string> log)
    {
        var d = File.ReadAllBytes(bankPath);
        var paths = ReadGuids(guidsPath);
        var ix = new Index();
        Walk(d, 0, d.Length, ix);
        var objs = ix.Objs;

        var root = new JsonObject { ["_source"] = "Imported from your copy of DropMix (Master Bank.bank) by OpenMix." };
        foreach (var (path, key, isEvent) in Targets)
        {
            if (!paths.TryGetValue(path, out var id)) throw new InvalidDataException($"{path} isn't listed in GUIDs.txt");
            if (!objs.TryGetValue(id, out var o)) throw new InvalidDataException($"{path} isn't in Master Bank.bank");
            if (isEvent) o = EventMaster(d, o, objs, path);
            root[key] = Bus(d, o, objs, path);
        }
        var samples = Fsb5.SampleNames(d);
        if (!paths.TryGetValue(CrowdLoopEvent, out var crowdId)) throw new InvalidDataException($"{CrowdLoopEvent} isn't listed in GUIDs.txt");
        root["crowdLoop"] = TrackAutomation(d, ix, crowdId, CrowdLoopEvent, samples);

        var timelines = new JsonObject();
        foreach (var (key, path, sample) in TimelineTargets)
        {
            Guid ev;
            if (path != null)
            {
                if (!paths.TryGetValue(path, out ev)) throw new InvalidDataException($"{path} isn't listed in GUIDs.txt");
            }
            else ev = EventPlaying(d, ix, samples, sample!);
            timelines[key] = Timeline(d, ix, ev, samples, path ?? $"the event playing {string.Join(" and ", sample!)}");
        }
        root["timelines"] = timelines;
        log($"fmod mix: {Targets.Length} chains, {root["crowdLoop"]!["tracks"]!.AsObject().Count} crowd loop tracks and {timelines.Count} event timelines from Master Bank.bank");
        return root;
    }

    /// <summary>Event timelines OpenMix follows: by event path, or (for events FMOD didn't export a path for) the one
    /// event whose timeline plays all the given samples.</summary>
    static readonly (string key, string? path, string[]? sample)[] TimelineTargets =
    {
        ("intro", "event:/Actions/Intro", null),
        ("outroStinger", "event:/Actions/OutroStinger", null),
        ("menuAmbience", null, new[] { "amb_loop" }),
        ("partyIntro", null, new[] { "ClashIntro_1", "PartyIntro_ping" }),
        ("puzzleIntro", null, new[] { "ClashIntro_1", "PuzzlePing1" }),
    };

    /// <summary>Timeline positions are counted at 48 000 per second.</summary>
    const double TimelineRate = 48000;
    static readonly string[] InstrumentTypes = { "WAIT", "MUIT", "EVIT" };

    static bool IsInstrument(Index ix, Guid g) => InstrumentTypes.Any(t => ix.Typed.ContainsKey((t, g)));

    /// <summary>The one event (TLNB bytes 16..32) whose timeline's instruments play all of <paramref name="want"/>.</summary>
    static Guid EventPlaying(byte[] d, Index ix, string[] samples, string[] want)
    {
        var found = new HashSet<Guid>();
        foreach (var ((type, _), o) in ix.Typed)
        {
            if (type != "TMLN" || !o.Chunks.TryGetValue("TLNB", out var tb) || tb.end - tb.start < 32) continue;
            var names = new SortedSet<string>(StringComparer.Ordinal);
            for (int i = tb.start + 32; i + 24 <= tb.end; i++)
            {
                var g = new Guid(d.AsSpan(i, 16));
                if (IsInstrument(ix, g)) { CollectSamples(d, ix, g, samples, names, new HashSet<Guid>()); i += 23; }
            }
            if (want.All(names.Contains)) found.Add(new Guid(d.AsSpan(tb.start + 16, 16)));
        }
        return found.Count == 1 ? found.First()
            : throw new InvalidDataException($"expected one event timeline playing {string.Join(" and ", want)}, found {found.Count}");
    }

    /// <summary>An event's timeline: each placed instrument (TLNB: its GUID, then u32 start and u32 length) with the
    /// samples it can play, and its loop regions (a TRNS/TRAN/TRNB transition from a region back to itself; the region's
    /// start follows its GUID in the TLNB, the loop end is the u32 at TRNB byte 32).</summary>
    static JsonObject Timeline(byte[] d, Index ix, Guid eventId, string[] samples, string what)
    {
        var tls = ix.Typed.Where(kv => kv.Key.Item1 == "TMLN" && kv.Value.Chunks.TryGetValue("TLNB", out var c) && c.end - c.start >= 32
                                         && new Guid(d.AsSpan(c.start + 16, 16)) == eventId).Select(kv => kv.Value).ToList();
        if (tls.Count != 1) throw new InvalidDataException($"{what}: expected one timeline, found {tls.Count}");
        var tl = tls[0]; var tlnb = tl.Chunks["TLNB"];
        var items = new JsonArray();
        for (int i = tlnb.start + 32; i + 24 <= tlnb.end; i++)
        {
            var g = new Guid(d.AsSpan(i, 16));
            if (!IsInstrument(ix, g)) continue;
            var names = new SortedSet<string>(StringComparer.Ordinal);
            CollectSamples(d, ix, g, samples, names, new HashSet<Guid>());
            items.Add(new JsonObject
            {
                ["startSec"] = Round(BitConverter.ToUInt32(d, i + 16) / TimelineRate),
                ["lengthSec"] = Round(BitConverter.ToUInt32(d, i + 20) / TimelineRate),
                ["samples"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()),
            });
            i += 23;
        }
        var loops = new JsonArray();
        if (tl.Chunks.TryGetValue("LIST:TRNS", out var trns))
            foreach (var (tStart, tEnd) in SubLists(d, trns.start + 4, trns.end, "TRAN"))
            {
                if (!Chunks(d, tStart, tEnd).TryGetValue("TRNB", out var tb) || tb.end - tb.start < 36) continue;
                var region = new Guid(d.AsSpan(tb.start, 16));
                if (new Guid(d.AsSpan(tb.start + 16, 16)) != region) continue;      // a jump elsewhere, not a loop
                int at = d.AsSpan(tlnb.start, tlnb.end - tlnb.start).IndexOf(region.ToByteArray());
                if (at < 0 || tlnb.start + at + 20 > tlnb.end) continue;
                loops.Add(new JsonObject
                {
                    ["startSec"] = Round(BitConverter.ToUInt32(d, tlnb.start + at + 16) / TimelineRate),
                    ["endSec"] = Round(BitConverter.ToUInt32(d, tb.start + 32) / TimelineRate),
                });
            }
        if (items.Count == 0) throw new InvalidDataException($"{what}: no instruments on the timeline");
        return new JsonObject { ["instruments"] = items, ["loops"] = loops };
    }

    /// <summary>The bodies (after the list type) of the LISTs of <paramref name="type"/> directly inside a range.</summary>
    static List<(int, int)> SubLists(byte[] d, int o, int end, string type)
    {
        var list = new List<(int, int)>();
        while (o + 8 <= end)
        {
            int size = (int)BitConverter.ToUInt32(d, o + 4), body = o + 8;
            if (body + size > end) break;
            if (Encoding.ASCII.GetString(d, o, 4) == "LIST" && Encoding.ASCII.GetString(d, body, 4) == type) list.Add((body + 4, body + size));
            o = body + size + (size & 1);
        }
        return list;
    }

    /// <summary>The samples an instrument can play: a waveform instrument its sample; a multi instrument the instruments
    /// in its playlist; an event instrument everything on the nested event's timelines and parameter sheets.</summary>
    static void CollectSamples(byte[] d, Index ix, Guid g, string[] samples, SortedSet<string> into, HashSet<Guid> seen)
    {
        if (!seen.Add(g)) return;
        if (ix.Typed.TryGetValue(("WAIT", g), out var w) && w.Chunks.TryGetValue("WAIB", out var wb))
        {
            if (ix.WaveSample.TryGetValue(new Guid(d.AsSpan(wb.start + 16, 16)), out int si) && si < samples.Length) into.Add(samples[si]);
        }
        else if (ix.Typed.TryGetValue(("MUIT", g), out var m))
        {
            foreach (var c in m.Chunks.Values)
                for (int i = c.start; i + 16 <= c.end; i++)
                {
                    var r = new Guid(d.AsSpan(i, 16));
                    if (r != g && IsInstrument(ix, r)) CollectSamples(d, ix, r, samples, into, seen);
                }
        }
        else if (ix.Typed.TryGetValue(("EVIT", g), out var e) && e.Chunks.TryGetValue("EVIB", out var eb) && eb.end - eb.start >= 32)
        {
            var nested = new Guid(d.AsSpan(eb.start + 16, 16));
            // the nested event's timelines (TLNB bytes 16..32) and parameter sheets (PMLB bytes 32..48)
            foreach (var ((type, _), o) in ix.Typed)
            {
                (int start, int end) c;
                if (type == "TMLN" && o.Chunks.TryGetValue("TLNB", out c) && c.end - c.start >= 32 && new Guid(d.AsSpan(c.start + 16, 16)) == nested) c.start += 32;
                else if (type == "PMLO" && o.Chunks.TryGetValue("PMLB", out c) && c.end - c.start >= 48 && new Guid(d.AsSpan(c.start + 32, 16)) == nested) c.start += 48;
                else continue;
                for (int i = c.start; i + 16 <= c.end; i++)
                {
                    var r = new Guid(d.AsSpan(i, 16));
                    if (IsInstrument(ix, r)) CollectSamples(d, ix, r, samples, into, seen);
                }
            }
        }
    }

    /// <summary>An event's audio tracks that have volume automation: per track, the sample its waveform instrument plays
    /// and the automation curve (parameter value → volume dB). Tracks are found through the waveform instruments on
    /// the event's timeline (each instrument names its track bus and its waveform); the automation is the CTRL chunk
    /// targeting the track's volume (property 0), owned by a parameter (PARM, named in its PRMB chunk), with a CURV
    /// chunk of points: u16 (count·2 + 1), u16 size, then per point f32 x, f32 y, f32 shape, f32.</summary>
    static JsonObject TrackAutomation(byte[] d, Index ix, Guid eventId, string path, string[] samples)
    {
        if (!ix.Typed.TryGetValue(("EVNT", eventId), out var ev) || !ev.Chunks.TryGetValue("EVTB", out var evtb))
            throw new InvalidDataException($"{path} isn't in Master Bank.bank");
        var timelines = GuidsIn(d, evtb).Where(g => ix.Typed.ContainsKey(("TMLN", g))).ToHashSet();
        var tracks = new JsonObject(); string? parameter = null;
        foreach (var ((type, _), inst) in ix.Typed)
        {
            if (type != "WAIT" || !inst.Chunks.TryGetValue("WAIB", out var waib) || !inst.Chunks.TryGetValue("INST", out var ic)) continue;
            if (ic.end - ic.start < 16 || !timelines.Contains(new Guid(d.AsSpan(ic.start, 16)))) continue;
            var wave = new Guid(d.AsSpan(waib.start + 16, 16));
            if (!ix.WaveSample.TryGetValue(wave, out int si) || si < 0 || si >= samples.Length) throw new InvalidDataException($"{path}: a waveform has no sample");
            var trackBus = GuidsIn(d, ic).Where(g => ix.Typed.ContainsKey(("GBUS", g))).Distinct().ToList();
            if (trackBus.Count != 1) throw new InvalidDataException($"{path}: an instrument has {trackBus.Count} track buses");
            var ctrls = ix.Controls.Where(c => c.end - c.start >= 68 && new Guid(d.AsSpan(c.start + 16, 16)) == trackBus[0]
                                                && BitConverter.ToUInt32(d, c.start + 64) == 0).ToList();
            if (ctrls.Count == 0) continue;                                         // a track without volume automation
            if (ctrls.Count > 1) throw new InvalidDataException($"{path}: track of {samples[si]} has {ctrls.Count} volume automations");
            var c = ctrls[0];
            var owner = new Guid(d.AsSpan(c.start + 32, 16));
            string name = ix.Typed.TryGetValue(("PARM", owner), out var parm) && parm.Chunks.TryGetValue("PRMB", out var pb) ? ParamName(d, pb)
                          : throw new InvalidDataException($"{path}: the automation of {samples[si]} isn't owned by a parameter");
            if (parameter != null && parameter != name) throw new InvalidDataException($"{path}: tracks follow different parameters");
            parameter = name;
            if (!ix.Curves.TryGetValue(new Guid(d.AsSpan(c.start + 48, 16)), out var cv)) throw new InvalidDataException($"{path}: missing automation curve");
            int p = cv.start + 32;
            int enc = BitConverter.ToUInt16(d, p), size = BitConverter.ToUInt16(d, p + 2); p += 4;
            int n = enc >> 1;
            if ((enc & 1) == 0 || n < 1 || size < 8 || p + n * size > cv.end) throw new InvalidDataException($"{path}: unexpected automation curve");
            var pts = new JsonArray();
            for (int i = 0; i < n; i++, p += size)
                pts.Add(new JsonArray(Round(BitConverter.ToSingle(d, p)), Round(BitConverter.ToSingle(d, p + 4))));
            tracks[samples[si]] = new JsonObject { ["volumeDbPoints"] = pts };
        }
        if (tracks.Count == 0) throw new InvalidDataException($"{path}: no automated tracks");
        return new JsonObject { ["parameter"] = parameter, ["tracks"] = tracks };
    }

    static IEnumerable<Guid> GuidsIn(byte[] d, (int start, int end) c)
    {
        for (int i = c.start; i + 16 <= c.end; i++) yield return new Guid(d.AsSpan(i, 16));
    }

    /// <summary>A parameter's name in its PRMB chunk: u16 length, then that many characters and a NUL.</summary>
    static string ParamName(byte[] d, (int start, int end) c)
    {
        for (int i = c.start + 16; i + 2 < c.end; i++)
        {
            int len = BitConverter.ToUInt16(d, i);
            if (len < 1 || i + 3 + len > c.end || d[i + 2 + len] != 0) continue;
            var s = d.AsSpan(i + 2, len);
            if (s.ToArray().All(b => b is >= 0x20 and < 0x7F)) return Encoding.ASCII.GetString(s);
        }
        throw new InvalidDataException("a parameter without a name");
    }

    /// <summary>FMOD Studio's GUIDs.txt: "{guid} path" per line.</summary>
    static Dictionary<string, Guid> ReadGuids(string path)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            int sp = t.IndexOf(' ');
            if (sp > 0 && Guid.TryParse(t[..sp].Trim('{', '}'), out var g)) map[t[(sp + 1)..].Trim()] = g;
        }
        return map;
    }

    /// <summary>Indexes every LIST object by the GUID at the start of its first chunk, and the loose CTRL, CURV and WAV
    /// chunks (a WAV chunk is the waveform's GUID, u16, u32, then the u32 sample index in the bank's FSB5 archive).</summary>
    static void Walk(byte[] d, int o, int end, Index ix)
    {
        while (o + 8 <= end)
        {
            string id = Encoding.ASCII.GetString(d, o, 4);
            int size = (int)BitConverter.ToUInt32(d, o + 4), body = o + 8;
            if (body + size > end) break;
            if (id is "RIFF" or "LIST")
            {
                string type = Encoding.ASCII.GetString(d, body, 4);
                var chunks = Chunks(d, body + 4, body + size);
                if (type is not ("PROJ" or "FEV ") && chunks.Count > 0 && chunks.First() is var (first, (s, e))
                    && !first.StartsWith("LIST") && e - s >= 16)
                {
                    var g = new Guid(d.AsSpan(s, 16)); var obj = new Obj(type, chunks);
                    ix.Objs.TryAdd(g, obj); ix.Typed.TryAdd((type, g), obj);
                }
                if (type != "SND ") Walk(d, body + 4, body + size, ix);
            }
            else if (id == "CTRL") ix.Controls.Add((body, body + size));
            else if (id == "CURV" && size >= 36) ix.Curves.TryAdd(new Guid(d.AsSpan(body, 16)), (body, body + size));
            else if (id == "WAV " && size >= 26) ix.WaveSample.TryAdd(new Guid(d.AsSpan(body, 16)), (int)BitConverter.ToUInt32(d, body + 22));
            o = body + size + (size & 1);
        }
    }

    /// <summary>The chunks directly inside a LIST (nested lists keyed "LIST:type"), first occurrence wins.</summary>
    static Dictionary<string, (int start, int end)> Chunks(byte[] d, int o, int end)
    {
        var map = new Dictionary<string, (int start, int end)>();
        while (o + 8 <= end)
        {
            string id = Encoding.ASCII.GetString(d, o, 4);
            int size = (int)BitConverter.ToUInt32(d, o + 4), body = o + 8;
            if (body + size > end) break;
            if (id == "LIST") id = "LIST:" + Encoding.ASCII.GetString(d, body, 4);
            map.TryAdd(id, (body, body + size));
            o = body + size + (size & 1);
        }
        return map;
    }

    /// <summary>An event's master track: the one MBUS object its EVTB chunk refers to.</summary>
    static Obj EventMaster(byte[] d, Obj ev, Dictionary<Guid, Obj> objs, string path)
    {
        if (ev.Type != "EVNT" || !ev.Chunks.TryGetValue("EVTB", out var c)) throw new InvalidDataException($"{path} isn't an event");
        var found = new List<Obj>();
        for (int i = c.start; i + 16 <= c.end; i++)
            if (objs.TryGetValue(new Guid(d.AsSpan(i, 16)), out var o) && o.Type == "MBUS" && !found.Contains(o)) found.Add(o);
        if (found.Count != 1) throw new InvalidDataException($"{path}: expected one master track, found {found.Count}");
        return found[0];
    }

    static JsonObject Bus(byte[] d, Obj bus, Dictionary<Guid, Obj> objs, string path)
    {
        if (bus.Type is not ("GBUS" or "MBUS") || !bus.Chunks.TryGetValue("BUS ", out var c))
            throw new InvalidDataException($"{path} isn't a bus ({bus.Type})");
        int p = c.start + 5;
        var pre = GuidList(d, ref p, c.end, path);
        var post = GuidList(d, ref p, c.end, path);
        if (p + 6 > c.end || BitConverter.ToUInt16(d, p) < 4) throw new InvalidDataException($"{path}: no fader level");
        double volume = BitConverter.ToSingle(d, p + 2);
        if (!double.IsFinite(volume) || volume is < -80 or > 10) throw new InvalidDataException($"{path}: fader level {volume} dB is out of range");
        JsonArray Effects(List<Guid> ids) => new(ids.Select(g => (JsonNode)Effect(d, g, objs, path)).ToArray());
        return new JsonObject { ["volumeDb"] = Round(volume), ["preFader"] = Effects(pre), ["postFader"] = Effects(post) };
    }

    static List<Guid> GuidList(byte[] d, ref int p, int end, string path)
    {
        if (p + 2 > end) throw new InvalidDataException($"{path}: truncated effect list");
        int enc = BitConverter.ToUInt16(d, p); p += 2;
        if ((enc & 1) == 0) throw new InvalidDataException($"{path}: unexpected effect list header {enc}");
        int n = enc >> 1;
        var list = new List<Guid>();
        if (n == 0) return list;
        int size = BitConverter.ToUInt16(d, p); p += 2;
        if (size != 16 || p + n * size > end) throw new InvalidDataException($"{path}: unexpected effect list");
        for (int i = 0; i < n; i++, p += size) list.Add(new Guid(d.AsSpan(p, 16)));
        return list;
    }

    /// <summary>One effect as JSON. Effects OpenMix doesn't implement (plugins, sends, meters) are kept by kind only, so
    /// the chain order stays visible.</summary>
    static JsonObject Effect(byte[] d, Guid id, Dictionary<Guid, Obj> objs, string path)
    {
        if (!objs.TryGetValue(id, out var o)) return new JsonObject { ["type"] = "unknown" };
        if (o.Type == "PEFF") return new JsonObject { ["type"] = "plugin" };
        if (o.Type == "SEFF") return new JsonObject { ["type"] = "send" };
        if (o.Type != "BEFF" || !o.Chunks.TryGetValue("BEFB", out var b) || !o.Chunks.TryGetValue("PMEF", out var pm))
            return new JsonObject { ["type"] = "other" };
        int dsp = (int)BitConverter.ToUInt32(d, b.start + 20);
        var v = Params(d, pm.start, pm.end, path);
        double F(int i) => i < v.Count && v[i] is double x ? Round(x) : throw new InvalidDataException($"{path}: effect type {dsp} parameter {i} isn't a number");
        return dsp switch
        {
            DspThreeEq => new JsonObject
            {
                ["type"] = "threeBandEq", ["lowDb"] = F(0), ["midDb"] = F(1), ["highDb"] = F(2),
                ["lowCrossoverHz"] = F(3), ["highCrossoverHz"] = F(4), ["slope"] = v.Count > 5 && v[5] is int s ? s : 0,
            },
            DspParamEq => new JsonObject { ["type"] = "parametricEq", ["centerHz"] = F(0), ["octaves"] = F(1), ["gainDb"] = F(2) },
            DspCompressor => new JsonObject
            {
                ["type"] = "compressor", ["thresholdDb"] = F(0), ["ratio"] = F(1), ["attackMs"] = F(2), ["releaseMs"] = F(3), ["makeupDb"] = F(4),
            },
            DspLimiter => new JsonObject { ["type"] = "limiter", ["releaseMs"] = F(0), ["ceilingDb"] = F(1), ["maximizerDb"] = F(2) },
            _ => new JsonObject { ["type"] = "other", ["dspType"] = dsp },
        };
    }

    /// <summary>A PMEF parameter list: floats as double, ints as int, bools as bool, strings as string.</summary>
    static List<object> Params(byte[] d, int p, int end, string path)
    {
        int n = (int)BitConverter.ToUInt32(d, p); p += 4;
        var v = new List<object>();
        for (int i = 0; i < n; i++)
        {
            if (p + 4 > end) throw new InvalidDataException($"{path}: truncated effect parameters");
            uint kind = BitConverter.ToUInt32(d, p); p += 4;
            switch (kind)
            {
                case 0: v.Add((double)BitConverter.ToSingle(d, p)); p += 4; break;
                case 1: v.Add(BitConverter.ToInt32(d, p)); p += 4; break;
                case 2: v.Add(d[p] != 0); p += 1; break;
                case 3: int len = BitConverter.ToUInt16(d, p); v.Add(Encoding.UTF8.GetString(d, p + 2, len)); p += 2 + len; break;
                default: throw new InvalidDataException($"{path}: unknown effect parameter kind {kind}");
            }
        }
        return v;
    }

    /// <summary>The bank stores single floats; round away float noise (e.g. 0.75 stays 0.75, 6.5 stays 6.5).</summary>
    static double Round(double x) => Math.Round(x, 4);
}
