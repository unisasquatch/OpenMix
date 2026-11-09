using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenMix.Importer;

/// <summary>Rebuilds data/walkthrough.json (the voiced introduction) and data/guide.json (the Guide screen) from the
/// user's own scene objects: the <c>Walkthrough</c> and <c>TutorialViewer</c> MonoBehaviours dumped by
/// <see cref="SceneDump"/>, plus the TutorialSlides prefabs they point to. Object references are resolved to
/// GameObject names by reading the serialized files in &lt;work&gt;/combined (next to &lt;work&gt;/scene); OpenMix's own
/// tables below only say which of its UI parts each named object stands for.</summary>
public static partial class SceneConfigs
{
    // ---- OpenMix's own knowledge: which of its UI parts (OpenMix key) each walkthrough GameObject stands for.
    // Matched against the end of the object's path in the scene; the longest match wins. ----
    static readonly Dictionary<string, string> WtObjectKeys = new()
    {
        ["remove_cards"] = "removecards",
        ["walkthrough_objects/prompt"] = "hint",
        ["set_up"] = "setup",
        ["playlist_instructions"] = "playlist",
        ["playlist_instructions/Popup/genres"] = "playlist",
        ["playlist_instructions/Popup/power_arrows_bottom"] = "playlistDetail",
        ["playlist_instructions/Popup/power_arrows_top"] = "levels",
        ["instrument_explanation"] = "instruments",
        ["instrument_explanation/Popup/single_instrument"] = "instruments",
        ["instrument_explanation/Popup/multi_insrument"] = "instruments2",
        ["snapshot_circle_highlight"] = "savemix",
        ["save_mix_focus_scrim"] = "savemix",
        ["fx_card_explanation"] = "fx",
        ["fx_card_explanation/Popup/single_instrument"] = "fx2",
        ["fx_card_explanation/Popup/multi_insrument"] = "fx2",
        ["game_modes"] = "modes",
        ["game_modes/Popup/clash"] = "clash",
        ["game_modes/Popup/party"] = "party",
        ["game_modes/Popup/freestyle"] = "freestyle",
    };

    /// <summary>The walkthrough's step-condition names, card-type names and text-token names by number. They are read
    /// from the enums in Il2CppDumper's dump.cs, made from the user's own copy during setup (see <see cref="WtTokens"/>).</summary>
    static Dictionary<int, string> WtPredicates = new();
    static string[] WtCardTypes = Array.Empty<string>();

    public static void BuildWalkthrough(string sceneDir, string dataDir, Action<string> log)
    {
        var (file, wt) = WtFindObject(sceneDir, "Walkthrough");
        var tokens = WtTokens(sceneDir);
        var scene = WtUnityFile.Open(WtCombinedFile(sceneDir, file)!);
        var unknown = new SortedSet<string>(StringComparer.Ordinal);

        string? Tok(JsonElement e) { int t = e.GetInt32(); return t == 0 ? null : WtTokenName(tokens, t); }
        static string? Str(JsonElement e) { var s = e.GetString(); return string.IsNullOrEmpty(s) ? null : s; }
        string? Key(JsonElement pptr)
        {
            long pid = pptr.GetProperty("m_PathID").GetInt64();
            if (pid == 0) return null;
            var path = scene.GameObjectPath(pid) ?? throw new InvalidDataException($"walkthrough: object {pid} isn't a GameObject");
            var hit = WtObjectKeys.Where(kv => path == kv.Key || path.EndsWith("/" + kv.Key, StringComparison.Ordinal))
                                  .OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Value).FirstOrDefault();
            if (hit == null) unknown.Add(path);
            return hit;
        }
        JsonArray Keys(JsonElement arr)
        {
            var ks = arr.EnumerateArray().Select(Key).Where(k => k != null).Distinct().OrderBy(k => k, StringComparer.Ordinal);
            return new JsonArray(ks.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
        }

        var root = new JsonObject
        {
            ["lastPlayedTextFullTime"] = wt.GetProperty("lastPlayedTextFullTime").GetDouble(),
            ["lastPlayedTextFadeTime"] = wt.GetProperty("lastPlayedTextFadeTime").GetDouble(),
            ["slotTypes"] = new JsonArray(wt.GetProperty("slotData").EnumerateArray()
                .Select(s => (JsonNode?)JsonValue.Create(WtCardTypes[s.GetProperty("linkedCardType").GetInt32()])).ToArray()),
        };
        var stages = new JsonArray();
        foreach (var st in wt.GetProperty("stages").EnumerateArray())
        {
            var steps = new JsonArray();
            foreach (var s in st.GetProperty("steps").EnumerateArray())
                steps.Add(new JsonObject
                {
                    ["vo"] = Str(s.GetProperty("voEvent")),
                    ["usePopup"] = s.GetProperty("usePopup").GetBoolean(),
                    ["text"] = Tok(s.GetProperty("displayToken")),
                    ["usePreviousText"] = s.GetProperty("usePreviousToken").GetBoolean(),
                    ["secondaryWhen"] = WtPredicates[s.GetProperty("useSecondaryTextPredicateType").GetInt32()],
                    ["secondaryText"] = Tok(s.GetProperty("secondaryDisplayToken")),
                    ["playEarlyIfCardPlayed"] = s.GetProperty("playEarlyIfCardPlayed").GetBoolean(),
                    ["voWrongSlot"] = Str(s.GetProperty("voWrongSlotEvent")),
                    ["slot"] = s.GetProperty("slot").GetInt32(),
                    ["useSlotEffect"] = s.GetProperty("useSlotEffect").GetBoolean(),
                    ["effectType"] = WtCardTypes[s.GetProperty("effectType").GetInt32()],
                    ["startDelay"] = s.GetProperty("startTimeDelay").GetDouble(),
                    ["requiredType"] = WtCardTypes[s.GetProperty("requiredType").GetInt32()],
                    ["timeout"] = s.GetProperty("timeoutDelay").GetDouble(),
                    ["voTimeout"] = Str(s.GetProperty("voTimeoutEvent")),
                    ["skipWhen"] = WtPredicates[s.GetProperty("skipPredicateType").GetInt32()],
                    ["finishWhen"] = WtPredicates[s.GetProperty("finishPredicateType").GetInt32()],
                    ["customPopup"] = Key(s.GetProperty("customPopup")),
                    ["enable"] = Keys(s.GetProperty("enableOnEnter")),
                    ["disable"] = Keys(s.GetProperty("disableOnExit")),
                });
            stages.Add(new JsonObject
            {
                ["name"] = st.GetProperty("stageName").GetString(),
                ["silenceCards"] = st.GetProperty("silenceCardsDuringStage").GetBoolean(),
                ["showRestart"] = st.GetProperty("showRestartButton").GetBoolean(),
                ["showSkip"] = st.GetProperty("showSkipButton").GetBoolean(),
                ["useSlotType"] = st.GetProperty("useSlotType").GetBoolean(),
                ["steps"] = steps,
            });
        }
        root["stages"] = stages;
        if (unknown.Count > 0) log("walkthrough: objects OpenMix doesn't know (left out): " + string.Join(", ", unknown));
        WtWrite(Path.Combine(dataDir, "walkthrough.json"), root);
        log($"walkthrough: {stages.Count} stages, {stages.Sum(s => s!["steps"]!.AsArray().Count)} steps");
    }

    public static void BuildGuide(string sceneDir, string dataDir, Action<string> log)
    {
        var (_, tv) = WtFindObject(sceneDir, "TutorialViewer");
        var tokens = WtTokens(sceneDir);
        var images = GuideSpriteNames();
        var slideFiles = GuideSlideFiles(sceneDir);
        var list = new JsonArray();
        int nSlides = 0;
        foreach (var t in tv.GetProperty("tutorials").EnumerateArray())
        {
            if (t.GetProperty("platform").GetInt32() == 1) continue;   // iOS-only entries; OpenMix follows the Android build
            var slides = new JsonArray();
            foreach (var p in t.GetProperty("slides").EnumerateArray())
            {
                var name = Path.GetFileNameWithoutExtension(p.GetString()!);
                slides.Add(GuideSlide(sceneDir, name, slideFiles, images, log));
                nSlides++;
            }
            var url = t.GetProperty("url").GetString();
            list.Add(new JsonObject
            {
                ["title"] = WtTokenName(tokens, t.GetProperty("name").GetInt32()),
                ["header"] = t.GetProperty("isHeader").GetBoolean(),
                ["video"] = string.IsNullOrEmpty(url) ? null : url,
                ["slides"] = slides,
            });
        }
        WtWrite(Path.Combine(dataDir, "guide.json"), list);
        log($"guide: {list.Count} entries, {nSlides} slides");
    }

    // ---- guide slides ----

    /// <summary>One TutorialSlides prefab: its pictures (sprite names, from OpenMix's content manifest) and its texts
    /// (the Localizers' keys).</summary>
    static JsonObject GuideSlide(string sceneDir, string name, Dictionary<string, string> slideFiles,
                                 Dictionary<(string, long), string> sprites, Action<string> log)
    {
        var imgs = new JsonArray(); var texts = new JsonArray(); var seen = new HashSet<string>();
        if (slideFiles.TryGetValue(name, out var file))
        {
            var uf = WtUnityFile.Open(WtCombinedFile(sceneDir, file)!);
            var dir = Path.Combine(sceneDir, file + "_export");
            var order = uf.ComponentOrder();
            foreach (var (cls, pid, j) in WtObjects(dir).OrderBy(o => order.TryGetValue(o.pid, out var x) ? x.pos : int.MaxValue))
            {
                if (cls == "Image")
                {
                    var sp = j.GetProperty("m_Sprite");
                    int fid = sp.GetProperty("m_FileID").GetInt32(); long spid = sp.GetProperty("m_PathID").GetInt64();
                    if (spid == 0) continue;
                    // the arrows from a text to part of the picture: OpenMix shows the texts under the pictures instead
                    if (order.TryGetValue(pid, out var at) && uf.GameObjectName(at.go) == "arrow") continue;
                    var src = fid == 0 ? file : uf.External(fid);
                    if (src != null && sprites.TryGetValue((src, spid), out var n) && seen.Add(n)) imgs.Add(n);
                }
                else if (cls == "Localizer")
                {
                    var k = j.TryGetProperty("tokenSetter", out var ts) ? ts.GetString() : null;
                    if (!string.IsNullOrEmpty(k)) texts.Add(k);
                }
            }
        }
        else log($"guide: slide {name} not found");
        return new JsonObject { ["name"] = name, ["images"] = imgs, ["texts"] = texts };
    }

    /// <summary>Slide prefab name → the serialized file (in combined/) whose GameObjects include it.</summary>
    static Dictionary<string, string> GuideSlideFiles(string sceneDir)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(sceneDir, "*_export"))
        {
            var file = Path.GetFileName(dir)[..^"_export".Length];
            var path = WtCombinedFile(sceneDir, file, mustExist: false);
            if (path == null || new FileInfo(path).Length > 4 << 20) continue;   // slides are small files of their own
            WtUnityFile uf;
            try { uf = WtUnityFile.Open(path); } catch { continue; }
            var root = uf.RootGameObjectName();
            if (root != null) map.TryAdd(root, file);
        }
        return map;
    }

    /// <summary>(source file, PathID) → picture name for the guide's pictures, from OpenMix's content manifest.</summary>
    static Dictionary<(string, long), string> GuideSpriteNames()
    {
        var m = new Dictionary<(string, long), string>();
        foreach (var (dest, src, pid) in AssetExport.Manifest())
            if (dest.StartsWith("assets_raw/tutorial/", StringComparison.Ordinal) && long.TryParse(pid, out var p))
                m.TryAdd((src, p), Path.GetFileNameWithoutExtension(dest));
        return m;
    }

    // ---- shared helpers ----

    static void WtWrite(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    static readonly Regex WtDumpName = new(@"^(?<cls>.+) @(?<pid>-?\d+)\.json$", RegexOptions.Compiled);

    static IEnumerable<(string cls, long pid, JsonElement json)> WtObjects(string exportDir)
    {
        if (!Directory.Exists(exportDir)) yield break;
        foreach (var f in Directory.EnumerateFiles(exportDir, "*.json"))
        {
            var m = WtDumpName.Match(Path.GetFileName(f));
            if (!m.Success) continue;
            using var d = JsonDocument.Parse(File.ReadAllBytes(f));
            yield return (m.Groups["cls"].Value, long.Parse(m.Groups["pid"].Value), d.RootElement.Clone());
        }
    }

    /// <summary>The one dumped object of a class (level0 first, then any other file). Returns its file and JSON.</summary>
    static (string file, JsonElement json) WtFindObject(string sceneDir, string cls)
    {
        var dirs = Directory.EnumerateDirectories(sceneDir, "*_export")
            .OrderBy(d => Path.GetFileName(d) == "level0_export" ? 0 : 1).ThenBy(d => d, StringComparer.Ordinal);
        foreach (var dir in dirs)
            foreach (var f in Directory.EnumerateFiles(dir, cls + " @*.json"))
            {
                using var d = JsonDocument.Parse(File.ReadAllBytes(f));
                return (Path.GetFileName(dir)[..^"_export".Length], d.RootElement.Clone());
            }
        throw new FileNotFoundException($"no {cls} object in the scene dump ({sceneDir})");
    }

    /// <summary>The serialized file a dump folder came from: &lt;work&gt;/combined/&lt;file&gt;.</summary>
    static string? WtCombinedFile(string sceneDir, string file, bool mustExist = true)
    {
        var p = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sceneDir).TrimEnd('\\', '/'))!, "combined", file);
        if (File.Exists(p)) return p;
        if (mustExist) throw new FileNotFoundException($"serialized file {file} not found next to the scene dump", p);
        return null;
    }

    /// <summary>Text-table token id → key, from Il2CppDumper's dump.cs next to the scene dump when present.</summary>
    static Dictionary<int, string> WtTokens(string sceneDir)
    {
        var work = Path.GetDirectoryName(Path.GetFullPath(sceneDir).TrimEnd('\\', '/'))!;
        var dump = Path.Combine(work, "il2cpp", "out", "dump.cs");   // where SceneDump runs Il2CppDumper
        if (!File.Exists(dump)) throw new FileNotFoundException("Il2CppDumper's dump.cs is missing next to the scene dump.");
        var map = new Dictionary<int, string>();
        // the text-token enum is the one that holds "Kiosk_010"; the step conditions, "HasCardInSlot"; card types, "kBeat" and "kWild"
        var re = new Regex(@"^\s*public const (?<t>\S+) (?<n>\w+) = (?<v>-?\d+);", RegexOptions.Compiled);
        string? type = null; var all = new List<(string t, string n, int v)>();
        foreach (var line in File.ReadLines(dump))
        {
            if (!line.Contains("public const ")) continue;
            var m = re.Match(line);
            if (!m.Success || !int.TryParse(m.Groups["v"].Value, out var v)) continue;
            all.Add((m.Groups["t"].Value, m.Groups["n"].Value, v));
            if (m.Groups["n"].Value == "Kiosk_010") type = m.Groups["t"].Value;
        }
        if (type != null) foreach (var (t, n, v) in all) if (t == type) map[v] = n;
        string? Enum(params string[] names) => all.GroupBy(x => x.t).FirstOrDefault(g => names.All(n => g.Any(x => x.n == n)))?.Key;
        var pred = Enum("HasCardInSlot", "MixSaved") ?? throw new InvalidDataException("walkthrough condition names not found in dump.cs");
        WtPredicates = all.Where(x => x.t == pred).ToDictionary(x => x.v, x => x.n);
        var types = Enum("kBeat", "kWild", "kInvalid") ?? throw new InvalidDataException("card type names not found in dump.cs");
        var byVal = all.Where(x => x.t == types).ToDictionary(x => x.v, x => x.n[1..]);
        WtCardTypes = Enumerable.Range(0, byVal.Keys.Max() + 1).Select(v => byVal.TryGetValue(v, out var n) ? (n == "FX" ? "Fx" : n) : "").ToArray();
        return map;
    }

    static string WtTokenName(Dictionary<int, string> tokens, int id) =>
        tokens.TryGetValue(id, out var n) ? n : throw new InvalidDataException($"text token {id} isn't known");

    /// <summary>Just enough of Unity's serialized-file format (2017.4, version 17) to name GameObjects and resolve
    /// cross-file references.</summary>
    sealed class WtUnityFile
    {
        byte[] _d = Array.Empty<byte>();
        uint _dataOffset;
        readonly Dictionary<long, (uint start, uint size, int cls)> _objects = new();
        readonly List<string> _externals = new();
        readonly List<long> _order = new();

        public static WtUnityFile Open(string path)
        {
            var f = new WtUnityFile { _d = File.ReadAllBytes(path) };
            f.Parse();
            return f;
        }

        /// <summary>FileID n (≥1) → the external file's name (last path part).</summary>
        public string? External(int fileId) =>
            fileId >= 1 && fileId <= _externals.Count ? Path.GetFileName(_externals[fileId - 1].Replace('\\', '/')) : null;

        public string? GameObjectName(long pathId)
        {
            if (!_objects.TryGetValue(pathId, out var o) || o.cls != 1) return null;
            int p = (int)(_dataOffset + o.start);
            int n = I32(p); p += 4 + n * 12;   // m_Component: PPtr (int fileID, long pathID)
            p += 4;                            // m_Layer
            int len = I32(p);
            return Encoding.UTF8.GetString(_d, p + 4, len);
        }

        /// <summary>"root/…/parent/name" of a GameObject, following its Transform's parents.</summary>
        public string? GameObjectPath(long pathId)
        {
            var parts = new List<string>();
            for (long go = pathId; go != 0 && parts.Count < 64;)
            {
                var name = GameObjectName(go);
                if (name == null) break;
                parts.Add(name);
                go = ParentGameObject(go);
            }
            if (parts.Count == 0) return null;
            parts.Reverse();
            return string.Join("/", parts);
        }

        long ParentGameObject(long goId)
        {
            int p = (int)(_dataOffset + _objects[goId].start), n = I32(p);
            for (int i = 0; i < n; i++)
            {
                long c = I64(p + 4 + i * 12 + 4);
                if (!_objects.TryGetValue(c, out var t) || t.cls is not (4 or 224)) continue;
                long father = TransformFather(c);
                return father != 0 && _objects.TryGetValue(father, out var f) ? I64((int)(_dataOffset + f.start) + 4) : 0;
            }
            return 0;
        }

        /// <summary>Components in hierarchy order: depth-first from the root, each GameObject's components in order.
        /// Value: (position, GameObject, GameObject active).</summary>
        public Dictionary<long, (int pos, long go, bool active)> ComponentOrder()
        {
            var res = new Dictionary<long, (int, long, bool)>();
            long root = 0;
            foreach (var pid in _order)
            {
                var o = _objects[pid];
                if (o.cls != 4 && o.cls != 224) continue;
                if (TransformFather(pid) == 0) { root = pid; break; }
            }
            void Walk(long tr, bool parentActive)
            {
                long go = I64((int)(_dataOffset + _objects[tr].start) + 4);
                bool active = parentActive && GameObjectActive(go);
                int p = (int)(_dataOffset + _objects[go].start), n = I32(p);
                for (int i = 0; i < n; i++) res[I64(p + 4 + i * 12 + 4)] = (res.Count, go, active);
                foreach (var c in TransformChildren(tr)) Walk(c, active);
            }
            if (root != 0) Walk(root, true);
            return res;
        }

        public bool GameObjectActive(long goId)
        {
            var o = _objects[goId];
            int p = (int)(_dataOffset + o.start);
            int n = I32(p); p += 4 + n * 12 + 4;
            int len = I32(p); p += 4 + len; p = (p + 3) & ~3;
            return _d[p + 2] != 0;   // after m_Tag (u16)
        }

        // Transform: m_GameObject PPtr (12), m_LocalRotation (16), m_LocalPosition (12), m_LocalScale (12), m_Children, m_Father
        int TransformChildrenAt(long tr) => (int)(_dataOffset + _objects[tr].start) + 12 + 16 + 12 + 12;
        IEnumerable<long> TransformChildren(long tr)
        {
            int q = TransformChildrenAt(tr), n = I32(q);
            for (int i = 0; i < n; i++) yield return I64(q + 4 + i * 12 + 4);
        }
        long TransformFather(long tr)
        {
            int q = TransformChildrenAt(tr), n = I32(q);
            return I64(q + 4 + n * 12 + 4);
        }

        /// <summary>The name of the GameObject whose Transform has no parent.</summary>
        public string? RootGameObjectName()
        {
            foreach (var pid in _order)
                if (_objects[pid].cls is 4 or 224 && TransformFather(pid) == 0)   // Transform / RectTransform
                    return GameObjectName(I64((int)(_dataOffset + _objects[pid].start) + 4));
            return null;
        }

        int _p;
        int I32(int at) => BitConverter.ToInt32(_d, at);
        long I64(int at) => BitConverter.ToInt64(_d, at);
        static uint BE(byte[] d, int at) => (uint)(d[at] << 24 | d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3]);
        int RI32() { var v = I32(_p); _p += 4; return v; }
        string RStr() { int s = _p; while (_d[_p] != 0) _p++; var v = Encoding.UTF8.GetString(_d, s, _p - s); _p++; return v; }
        void Align() => _p = (_p + 3) & ~3;

        void Parse()
        {
            uint version = BE(_d, 8);
            _dataOffset = BE(_d, 12);
            if (version < 14 || version > 17) throw new InvalidDataException($"serialized file version {version} isn't supported");
            if (_d[16] != 0) throw new InvalidDataException("big-endian serialized files aren't supported");
            _p = 20;
            RStr();          // Unity version
            RI32();          // target platform
            bool typeTree = _d[_p++] != 0;
            int typeCount = RI32();
            var classes = new int[typeCount];
            for (int i = 0; i < typeCount; i++)
            {
                int cls = RI32();
                _p += 1;                              // isStrippedType
                _p += 2;                              // scriptTypeIndex
                if (version >= 16 ? cls == 114 : cls < 0) _p += 16;   // script id
                _p += 16;                             // old type hash
                if (typeTree) { int nodes = RI32(), strs = RI32(); _p += nodes * 24 + strs; }
                classes[i] = cls;
            }
            int objCount = RI32();
            for (int i = 0; i < objCount; i++)
            {
                Align();
                long pid = I64(_p); _p += 8;
                uint start = (uint)RI32(), size = (uint)RI32();
                int type = RI32();
                int cls = version >= 16 ? classes[type] : type;
                if (version < 16) _p += 2;            // class id (u16)
                if (version < 17 && version >= 11) _p += 2;   // script type index
                if (version == 15 || version == 16) _p += 1;  // stripped
                _objects[pid] = (start, size, cls);
                _order.Add(pid);
            }
            int scripts = RI32();
            for (int i = 0; i < scripts; i++) { _p += 4; Align(); _p += 8; }
            int ext = RI32();
            for (int i = 0; i < ext; i++) { RStr(); _p += 16 + 4; _externals.Add(RStr()); }
        }
    }
}
