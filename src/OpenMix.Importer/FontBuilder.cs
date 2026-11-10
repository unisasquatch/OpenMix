using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenMix.Importer;

/// <summary>Rebuilds the game's three UI fonts as TrueType files from its TextMesh Pro font assets.
/// Each TMP_FontAsset (a MonoBehaviour: m_fontInfo + m_glyphInfoList) says where every glyph sits in its
/// signed-distance-field atlas (alpha channel, edge at 0.5) plus its bearings and advance. For each glyph we
/// upsample the distance field, trace the 0.5 iso-contour (marching squares), simplify it (Douglas-Peucker),
/// orient outer contours clockwise / holes counter-clockwise and write a TrueType font with the game's metrics.
/// The atlases must already be in content/assets_raw (AssetExport places them); the font assets are read as
/// raw MonoBehaviour bytes exported by AssetStudioModCLI, found by name.</summary>
public static class FontBuilder
{
    const int Upm = 1000;
    const int Up = 4;            // distance-field upsampling before tracing
    const double Tol = 0.12;     // simplification tolerance, in atlas pixels

    sealed record Spec(string Asset, string Family, string Style, int Weight)
    {
        public string FileName => Family.Replace(" ", "") + "-" + Style + ".ttf";
        public string Atlas => Asset + " Atlas.png";
        public bool Bold => Weight >= 700 && Style == "Bold";
    }

    static readonly Spec[] Fonts =
    [
        new("Avenir-Book", "DropMix Avenir", "Book", 400),
        new("Avenir-Heavy", "DropMix Avenir", "Heavy", 800),
        new("DIN", "DropMix DIN", "Bold", 700),
    ];

    static readonly Regex Exported = new(@"^(?<name>.*) @(?<pid>-?\d+)\.dat$", RegexOptions.Compiled);

    /// <summary>Writes content/assets_raw/fonts/*.ttf. <paramref name="unityDataDirs"/> are folders of Unity
    /// serialized files (e.g. the importer's combined APK+OBB folder); they are searched in order. Returns the
    /// number of fonts written.</summary>
    public static int Build(IEnumerable<string> unityDataDirs, string assetStudioExe, string contentRoot, Action<string> log)
    {
        var ui = Path.Combine(contentRoot, "assets_raw");
        var outDir = Path.Combine(ui, "fonts");
        var tmp = Path.Combine(Path.GetTempPath(), "OpenMix-fonts-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8]);
        var found = new Dictionary<string, (string src, long pid, byte[] raw)>();
        try
        {
            int i = 0;
            foreach (var dir in unityDataDirs)
            {
                if (Fonts.All(f => found.ContainsKey(f.Asset))) break;
                if (!Directory.Exists(dir)) continue;
                var o = Path.Combine(tmp, (i++).ToString());
                var names = "^(" + string.Join("|", Fonts.Select(f => Regex.Escape(f.Asset))) + ")$";
                AssetExport.RunTool(assetStudioExe, log, dir, "-m", "exportRaw", "-t", "monoBehaviour",
                    "--filter-by-name", names, "--filter-with-regex", "-g", "fileName", "-f", "assetName_pathID",
                    "-o", o, "--log-level", "warning");
                if (!Directory.Exists(o)) continue;
                foreach (var sub in Directory.EnumerateDirectories(o))
                {
                    var src = Path.GetFileName(sub);
                    if (src.EndsWith("_export")) src = src[..^7];
                    foreach (var f in Directory.EnumerateFiles(sub, "*.dat", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                    {
                        var m = Exported.Match(Path.GetFileName(f));
                        if (!m.Success) continue;
                        var name = m.Groups["name"].Value;
                        if (found.ContainsKey(name) || !Fonts.Any(x => x.Asset == name)) continue;
                        var raw = File.ReadAllBytes(f);
                        if (TryParse(raw, out _)) found[name] = (src, long.Parse(m.Groups["pid"].Value), raw);
                    }
                }
            }
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (IOException) { }
        }

        int written = 0;
        foreach (var spec in Fonts)
        {
            if (!found.TryGetValue(spec.Asset, out var a)) { log($"font asset '{spec.Asset}' not found"); continue; }
            var atlas = Path.Combine(ui, spec.Atlas);
            if (!File.Exists(atlas)) { log($"{spec.Atlas} missing; skipping {spec.FileName}"); continue; }
            TryParse(a.raw, out var fa);
            var sdf = LoadAlpha(atlas, out int aw, out int ah);
            if (aw != (int)fa!.AtlasWidth || ah != (int)fa.AtlasHeight)
            { log($"{spec.Atlas} is {aw}x{ah}, font asset expects {fa.AtlasWidth}x{fa.AtlasHeight}; skipping"); continue; }
            Directory.CreateDirectory(outDir);
            int n = BuildFont(fa, sdf, aw, ah, spec, Path.Combine(outDir, spec.FileName));
            log($"{spec.FileName}: {n} glyphs (from {a.src} @{a.pid})");
            written++;
        }
        return written;
    }

    // ---------------------------------------------------------------- TMP_FontAsset (raw MonoBehaviour bytes)

    sealed class FontAsset
    {
        public string Name = "";
        public float PointSize, LineHeight, Ascender, CapHeight, Descender, SuperscriptOffset, SubscriptOffset, SubSize,
                     Underline, UnderlineThickness, Strikethrough, StrikethroughThickness, Padding, AtlasWidth, AtlasHeight;
        public long AtlasPathId;
        public List<Glyph> Glyphs = new();
    }

    record struct Glyph(int Id, float X, float Y, float Width, float Height, float XOffset, float YOffset, float XAdvance, float Scale);

    /// <summary>TMP 1.x serialized layout (Unity 5.6/2017, little-endian): MonoBehaviour header (m_GameObject,
    /// m_Enabled, m_Script, m_Name), hashCode, material, materialHashCode, fontAssetType, m_fontInfo
    /// (Name + 20 numbers), atlas, m_glyphInfoList [{id, x, y, width, height, xOffset, yOffset, xAdvance, scale}].
    /// Fields after the glyph list (kerning, fallbacks...) are not needed.</summary>
    static bool TryParse(byte[] raw, out FontAsset? fa)
    {
        fa = null;
        try
        {
            var r = new Reader(raw);
            r.PPtr(); r.Byte(); r.Align(); r.PPtr();     // m_GameObject, m_Enabled, m_Script
            var name = r.Str();                         // m_Name
            r.Int(); r.PPtr(); r.Int();                 // hashCode, material, materialHashCode
            int type = r.Int();                         // fontAssetType
            var f = new FontAsset { Name = name };
            r.Str();                                    // m_fontInfo.Name
            f.PointSize = r.Float(); r.Float(); r.Int(); // PointSize, Scale, CharacterCount
            f.LineHeight = r.Float(); r.Float();        // LineHeight, Baseline
            f.Ascender = r.Float(); f.CapHeight = r.Float(); f.Descender = r.Float(); r.Float(); // CenterLine
            f.SuperscriptOffset = r.Float(); f.SubscriptOffset = r.Float(); f.SubSize = r.Float();
            f.Underline = r.Float(); f.UnderlineThickness = r.Float();
            f.Strikethrough = r.Float(); f.StrikethroughThickness = r.Float();
            r.Float();                                  // TabWidth
            f.Padding = r.Float(); f.AtlasWidth = r.Float(); f.AtlasHeight = r.Float();
            f.AtlasPathId = r.PPtr();
            int n = r.Int();
            if (type < 0 || type > 2 || n <= 0 || n > 65535 || r.Left < n * 36) return false;
            if (!(f.PointSize > 0 && f.PointSize < 10000) || !(f.AtlasWidth >= 1 && f.AtlasWidth <= 16384) || !(f.AtlasHeight >= 1 && f.AtlasHeight <= 16384)) return false;
            for (int i = 0; i < n; i++)
                f.Glyphs.Add(new Glyph(r.Int(), r.Float(), r.Float(), r.Float(), r.Float(), r.Float(), r.Float(), r.Float(), r.Float()));
            fa = f;
            return true;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException) { return false; }
    }

    sealed class Reader(byte[] b)
    {
        int _p;
        public int Left => b.Length - _p;
        void Need(int n) { if (n < 0 || _p + n > b.Length) throw new ArgumentOutOfRangeException(nameof(b)); }
        public byte Byte() { Need(1); return b[_p++]; }
        public int Int() { Need(4); var v = BitConverter.ToInt32(b, _p); _p += 4; return v; }
        public long Long() { Need(8); var v = BitConverter.ToInt64(b, _p); _p += 8; return v; }
        public float Float() { Need(4); var v = BitConverter.ToSingle(b, _p); _p += 4; return v; }
        public void Align() { _p = (_p + 3) & ~3; }
        public long PPtr() { Int(); return Long(); }
        public string Str() { int n = Int(); Need(n); var s = Encoding.UTF8.GetString(b, _p, n); _p += n; Align(); return s; }
    }

    // ---------------------------------------------------------------- tracing

    static float[] LoadAlpha(string png, out int w, out int h)
    {
        using var im = new Bitmap(png);
        w = im.Width; h = im.Height;
        var a = new float[w * h];
        var data = im.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[data.Stride];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < w; x++) a[y * w + x] = row[x * 4 + 3] / 255f;
            }
        }
        finally { im.UnlockBits(data); }
        return a;
    }

    /// <summary>Glyph outlines in font units (y up) traced from the atlas distance field.</summary>
    static List<(double x, double y)[]> Trace(float[] sdf, int aw, int ah, Glyph g, int pad, double s)
    {
        var result = new List<(double x, double y)[]>();
        int x0 = Math.Max(0, (int)Math.Floor(g.X) - pad), y0 = Math.Max(0, (int)Math.Floor(g.Y) - pad);
        int x1 = Math.Min(aw, (int)Math.Ceiling(g.X + g.Width) + pad), y1 = Math.Min(ah, (int)Math.Ceiling(g.Y + g.Height) + pad);
        int cw = x1 - x0, ch = y1 - y0;
        if (cw <= 0 || ch <= 0) return result;
        float max = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) max = Math.Max(max, sdf[y * aw + x]);
        if (max < 0.5f) return result;

        // cubic (Catmull-Rom) upsampling; sample (i + 0.5) / Up - 0.5 in crop pixels, plus a zero border
        int W = cw * Up + 2, H = ch * Up + 2, iw = W - 2;
        var (xi, xw) = CubicTaps(cw); var (yi, yw) = CubicTaps(ch);
        var tmp = new float[ch * iw];
        for (int y = 0; y < ch; y++)
        {
            int row = (y0 + y) * aw + x0;
            for (int i = 0; i < iw; i++)
            {
                double v = 0;
                for (int k = 0; k < 4; k++) v += xw[i * 4 + k] * sdf[row + xi[i * 4 + k]];
                tmp[y * iw + i] = (float)v;
            }
        }
        var grid = new float[W * H];
        for (int j = 0; j < H - 2; j++)
            for (int k = 0; k < 4; k++)
            {
                double wk = yw[j * 4 + k]; int src = yi[j * 4 + k] * iw, dst = (j + 1) * W + 1;
                for (int i = 0; i < iw; i++) grid[dst + i] += (float)(wk * tmp[src + i]);
            }

        var raw = new List<(double x, double y)[]>();
        foreach (var c in MarchingSquares(grid, W, H, 0.5))
        {
            var simp = Simplify(c, Tol * Up);
            if (simp.Count < 4) continue;            // closed: last point repeats the first
            var pts = new (double x, double y)[simp.Count - 1];
            for (int k = 0; k < pts.Length; k++)
            {
                double row = simp[k].r - 1, col = simp[k].c - 1;
                double ax = x0 + (col + 0.5) / Up - 0.5, ay = y0 + (row + 0.5) / Up - 0.5;   // atlas pixel centres
                pts[k] = ((ax - g.X + g.XOffset) * s, (g.YOffset - (ay - g.Y)) * s);       // baseline up; rect top = yOffset
            }
            if (Math.Abs(Area(pts)) < 4) continue;
            raw.Add(pts);
        }
        // nesting depth decides direction: even = outer (clockwise), odd = hole (counter-clockwise)
        for (int i = 0; i < raw.Count; i++)
        {
            var c = raw[i];
            int depth = 0;
            for (int j = 0; j < raw.Count; j++) if (j != i && Inside(c[0], raw[j])) depth++;
            bool clockwise = Area(c) < 0;
            if ((depth % 2 == 0) != clockwise) Array.Reverse(c);
            result.Add(c);
        }
        return result;
    }

    /// <summary>Catmull-Rom taps (4 source indices, clamped, and weights) for each of n * Up output samples,
    /// sample i sitting at (i + 0.5) / Up - 0.5 in source pixels.</summary>
    static (int[] idx, double[] w) CubicTaps(int n)
    {
        int m = n * Up;
        var idx = new int[m * 4]; var w = new double[m * 4];
        for (int o = 0; o < m; o++)
        {
            double u = (o + 0.5) / Up - 0.5;
            int i = (int)Math.Floor(u); double t = u - i, t2 = t * t, t3 = t2 * t;
            double[] k = [0.5 * (-t3 + 2 * t2 - t), 0.5 * (3 * t3 - 5 * t2 + 2), 0.5 * (-3 * t3 + 4 * t2 + t), 0.5 * (t3 - t2)];
            for (int j = 0; j < 4; j++) { idx[o * 4 + j] = Math.Clamp(i - 1 + j, 0, n - 1); w[o * 4 + j] = k[j]; }
        }
        return (idx, w);
    }

    /// <summary>Closed iso-contours of a grid whose border is below the level, as (row, col) point loops with the
    /// first point repeated at the end. Saddles are resolved by the cell's mean value.</summary>
    static List<List<(double r, double c)>> MarchingSquares(float[] g, int W, int H, double level)
    {
        // edge ids: horizontal edge (r,c)-(r,c+1) = 2*(r*W+c); vertical edge (r,c)-(r+1,c) = 2*(r*W+c)+1
        var next = new Dictionary<int, int>();
        var pts = new Dictionary<int, (double r, double c)>();
        Span<int> eid = stackalloc int[4]; Span<bool> enter = stackalloc bool[4]; Span<int> cross = stackalloc int[4]; Span<bool> ins = stackalloc bool[4];
        for (int r = 0; r < H - 1; r++)
            for (int c = 0; c < W - 1; c++)
            {
                double tl = g[r * W + c], tr = g[r * W + c + 1], br = g[(r + 1) * W + c + 1], bl = g[(r + 1) * W + c];
                bool a = tl >= level, b = tr >= level, d = br >= level, e = bl >= level;
                if (a == b && b == d && d == e) continue;
                // walk the cell clockwise (TL→TR→BR→BL); crossings alternate entering / leaving the inside
                eid[0] = 2 * (r * W + c); eid[1] = 2 * (r * W + c + 1) + 1; eid[2] = 2 * ((r + 1) * W + c); eid[3] = 2 * (r * W + c) + 1;
                ins[0] = a; ins[1] = b; ins[2] = d; ins[3] = e;
                int n = 0;
                for (int k = 0; k < 4; k++)
                {
                    bool from = ins[k], to = ins[(k + 1) & 3];
                    if (from == to) continue;
                    enter[n] = to; cross[n] = eid[k]; n++;
                    if (!pts.ContainsKey(eid[k])) pts[eid[k]] = EdgePoint(g, W, eid[k], level);
                }
                if (n == 2)
                {
                    int A = enter[0] ? 0 : 1, B = 1 - A;
                    next[cross[B]] = cross[A];
                }
                else // saddle: n == 4
                {
                    bool centre = (tl + tr + br + bl) / 4 >= level;
                    for (int k = 0; k < 4; k++)
                    {
                        if (enter[k]) continue;                         // k is a leaving crossing (B)
                        int A = centre ? (k + 1) & 3 : (k + 3) & 3;     // next A (joins insides) or its own arc's A
                        next[cross[k]] = cross[A];
                    }
                }
            }

        var loops = new List<List<(double r, double c)>>();
        var seen = new HashSet<int>();
        foreach (var start in next.Keys)
        {
            if (seen.Contains(start)) continue;
            var loop = new List<(double r, double c)>();
            int cur = start;
            while (seen.Add(cur))
            {
                loop.Add(pts[cur]);
                if (!next.TryGetValue(cur, out cur)) break;
            }
            if (loop.Count >= 3) { loop.Add(loop[0]); loops.Add(loop); }
        }
        return loops;
    }

    static (double r, double c) EdgePoint(float[] g, int W, int id, double level)
    {
        int cell = id >> 1, r = cell / W, c = cell % W;
        bool vertical = (id & 1) == 1;
        double v0 = g[cell], v1 = vertical ? g[cell + W] : g[cell + 1];
        double t = (level - v0) / (v1 - v0);
        return vertical ? (r + t, c) : (r, c + t);
    }

    /// <summary>Douglas-Peucker; the endpoints (equal for a closed loop) are kept.</summary>
    static List<(double r, double c)> Simplify(List<(double r, double c)> p, double tol)
    {
        var keep = new bool[p.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, p.Count - 1));
        while (stack.Count > 0)
        {
            var (i0, i1) = stack.Pop();
            if (i1 - i0 < 2) continue;
            var (r0, c0) = p[i0]; var (r1, c1) = p[i1];
            double dr = r1 - r0, dc = c1 - c0, len = Math.Sqrt(dr * dr + dc * dc);
            int best = -1; double bd = -1;
            for (int i = i0 + 1; i < i1; i++)
            {
                double d = len < 1e-12 ? Math.Sqrt((p[i].r - r0) * (p[i].r - r0) + (p[i].c - c0) * (p[i].c - c0))
                                       : Math.Abs(dc * (p[i].r - r0) - dr * (p[i].c - c0)) / len;
                if (d > bd) { bd = d; best = i; }
            }
            if (bd > tol) { keep[best] = true; stack.Push((i0, best)); stack.Push((best, i1)); }
        }
        var o = new List<(double r, double c)>();
        for (int i = 0; i < p.Count; i++) if (keep[i]) o.Add(p[i]);
        return o;
    }

    static double Area((double x, double y)[] p)
    {
        double a = 0;
        for (int i = 0; i < p.Length; i++) { var q = p[(i + 1) % p.Length]; a += p[i].x * q.y - q.x * p[i].y; }
        return a / 2;
    }

    static bool Inside((double x, double y) pt, (double x, double y)[] poly)
    {
        bool c = false;
        for (int i = 0; i < poly.Length; i++)
        {
            var (x1, y1) = poly[i]; var (x2, y2) = poly[(i + 1) % poly.Length];
            if ((y1 > pt.y) != (y2 > pt.y) && pt.x < (x2 - x1) * (pt.y - y1) / (y2 - y1 + 1e-12) + x1) c = !c;
        }
        return c;
    }

    // ---------------------------------------------------------------- TrueType writer

    sealed class OutGlyph
    {
        public List<(int x, int y)[]> Contours = new();
        public int Advance;
        public int XMin, YMin, XMax, YMax;
        public bool Empty => Contours.Count == 0;
        public int Points => Contours.Sum(c => c.Length);
    }

    static int BuildFont(FontAsset fa, float[] sdf, int aw, int ah, Spec spec, string path)
    {
        double s = Upm / (double)fa.PointSize;
        int pad = (int)fa.Padding + 2;
        int R(double v) => (int)Math.Round(v);   // banker's rounding, like Python's round()

        var glyphs = new List<OutGlyph> { new() { Advance = Upm / 2 } };     // .notdef
        var cmap = new SortedDictionary<int, int>();
        var space = fa.Glyphs.FirstOrDefault(g => g.Id == 32);
        glyphs.Add(new OutGlyph { Advance = R((space.Id == 32 ? space.XAdvance : fa.PointSize * 0.25) * s) });
        cmap[32] = 1;
        var list = new List<Glyph>();
        var ids = new HashSet<int> { 32 };
        foreach (var g in fa.Glyphs)
            if (g.Id > 0 && g.Id <= 0x10FFFF && ids.Add(g.Id)) list.Add(g);
        var traced = new List<(double x, double y)[]>[list.Count];
        Parallel.For(0, list.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8) },
                     i => traced[i] = Trace(sdf, aw, ah, list[i], pad, s));
        for (int i = 0; i < list.Count; i++)
        {
            var g = list[i];
            var o = new OutGlyph { Advance = R(g.XAdvance * s) };
            foreach (var c in traced[i])
            {
                var pts = new List<(int x, int y)>();
                foreach (var (x, y) in c)
                {
                    var q = (R(x), R(y));
                    if (pts.Count == 0 || pts[^1] != q) pts.Add(q);
                }
                while (pts.Count > 1 && pts[^1] == pts[0]) pts.RemoveAt(pts.Count - 1);
                if (pts.Count >= 3) o.Contours.Add(pts.ToArray());
            }
            if (!o.Empty)
            {
                var all = o.Contours.SelectMany(p => p);
                o.XMin = all.Min(p => p.x); o.XMax = all.Max(p => p.x); o.YMin = all.Min(p => p.y); o.YMax = all.Max(p => p.y);
            }
            cmap[g.Id] = glyphs.Count;
            glyphs.Add(o);
        }

        int asc = R(fa.Ascender * s), desc = R(fa.Descender * s);
        if (desc >= 0) desc = -R((fa.LineHeight - fa.Ascender) * s);
        int lineGap = Math.Max(0, R(fa.LineHeight * s) - asc + desc);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var (glyf, loca) = GlyfLoca(glyphs);
        tables["glyf"] = glyf;
        tables["loca"] = loca;
        tables["cmap"] = Cmap(cmap);
        tables["hmtx"] = Hmtx(glyphs);
        tables["hhea"] = Hhea(glyphs, asc, desc, lineGap);
        tables["maxp"] = Maxp(glyphs);
        tables["OS/2"] = Os2(glyphs, cmap, fa, spec, s, asc, desc, lineGap);
        tables["post"] = Post(fa, s);
        tables["name"] = Name(spec);
        tables["head"] = Head(glyphs, spec);
        File.WriteAllBytes(path, Sfnt(tables));
        return glyphs.Count - 2;
    }

    sealed class BE
    {
        readonly MemoryStream _m = new();
        public BE U8(int v) { _m.WriteByte((byte)v); return this; }
        public BE U16(int v) { _m.WriteByte((byte)(v >> 8)); _m.WriteByte((byte)v); return this; }
        public BE I16(int v) => U16((ushort)(short)v);
        public BE U32(uint v) { U16((int)(v >> 16)); return U16((int)(v & 0xFFFF)); }
        public BE I64(long v) { U32((uint)(v >> 32)); return U32((uint)v); }
        public BE Bytes(byte[] b) { _m.Write(b); return this; }
        public BE Tag(string t) { foreach (var ch in t) _m.WriteByte((byte)ch); return this; }
        public BE Pad4() { while (_m.Length % 4 != 0) _m.WriteByte(0); return this; }
        public long Length => _m.Length;
        public byte[] ToArray() => _m.ToArray();
    }

    static (byte[] glyf, byte[] loca) GlyfLoca(List<OutGlyph> glyphs)
    {
        var g = new BE(); var l = new BE();
        foreach (var o in glyphs)
        {
            l.U32((uint)g.Length);
            if (o.Empty) continue;
            g.I16(o.Contours.Count).I16(o.XMin).I16(o.YMin).I16(o.XMax).I16(o.YMax);
            int end = -1;
            foreach (var c in o.Contours) { end += c.Length; g.U16(end); }
            g.U16(0);                                    // no instructions
            var flags = new List<byte>(); var xs = new BE(); var ys = new BE();
            int px = 0, py = 0;
            foreach (var c in o.Contours)
                foreach (var (x, y) in c)
                {
                    int dx = x - px, dy = y - py; px = x; py = y;
                    byte f = 0x01;                       // on curve
                    if (dx == 0) f |= 0x10;
                    else if (Math.Abs(dx) < 256) { f |= 0x02; if (dx > 0) f |= 0x10; xs.U8(Math.Abs(dx)); }
                    else xs.I16(dx);
                    if (dy == 0) f |= 0x20;
                    else if (Math.Abs(dy) < 256) { f |= 0x04; if (dy > 0) f |= 0x20; ys.U8(Math.Abs(dy)); }
                    else ys.I16(dy);
                    flags.Add(f);
                }
            g.Bytes(flags.ToArray()).Bytes(xs.ToArray()).Bytes(ys.ToArray()).Pad4();
        }
        l.U32((uint)g.Length);
        return (g.ToArray(), l.ToArray());
    }

    static byte[] Cmap(SortedDictionary<int, int> map)
    {
        // format 4 (BMP): one segment per run of consecutive code points with consecutive glyph ids
        var bmp = map.Where(kv => kv.Key <= 0xFFFF).ToList();
        var segs = new List<(int start, int end, int delta)>();
        foreach (var (cp, gid) in bmp)
        {
            if (segs.Count > 0 && segs[^1].end == cp - 1 && segs[^1].delta == gid - cp)
                segs[^1] = (segs[^1].start, cp, segs[^1].delta);
            else segs.Add((cp, cp, gid - cp));
        }
        segs.Add((0xFFFF, 0xFFFF, 1));
        int n = segs.Count, sr = 2 * (1 << (int)Math.Floor(Math.Log2(n)));
        var f4 = new BE();
        f4.U16(4).U16(16 + 8 * n).U16(0).U16(2 * n).U16(sr).U16((int)Math.Log2(sr / 2)).U16(2 * n - sr);
        foreach (var x in segs) f4.U16(x.end);
        f4.U16(0);
        foreach (var x in segs) f4.U16(x.start);
        foreach (var x in segs) f4.U16(x.delta & 0xFFFF);
        foreach (var _ in segs) f4.U16(0);
        var sub4 = f4.ToArray();

        // format 12 only when there are code points beyond the BMP
        byte[]? sub12 = null;
        if (map.Keys.Any(k => k > 0xFFFF))
        {
            var groups = new List<(int s, int e, int g)>();
            foreach (var (cp, gid) in map)
                if (groups.Count > 0 && groups[^1].e == cp - 1 && groups[^1].g + (cp - groups[^1].s) == gid) groups[^1] = (groups[^1].s, cp, groups[^1].g);
                else groups.Add((cp, cp, gid));
            var f12 = new BE();
            f12.U16(12).U16(0).U32((uint)(16 + 12 * groups.Count)).U32(0).U32((uint)groups.Count);
            foreach (var x in groups) f12.U32((uint)x.s).U32((uint)x.e).U32((uint)x.g);
            sub12 = f12.ToArray();
        }

        var t = new BE();
        int count = sub12 == null ? 2 : 4, off = 4 + 8 * count;
        t.U16(0).U16(count);
        t.U16(0).U16(3).U32((uint)off);                              // Unicode BMP
        if (sub12 != null) t.U16(0).U16(4).U32((uint)(off + sub4.Length));
        t.U16(3).U16(1).U32((uint)off);                              // Windows BMP
        if (sub12 != null) t.U16(3).U16(10).U32((uint)(off + sub4.Length));
        t.Bytes(sub4);
        if (sub12 != null) t.Bytes(sub12);
        return t.ToArray();
    }

    static byte[] Hmtx(List<OutGlyph> glyphs)
    {
        var t = new BE();
        foreach (var o in glyphs) t.U16(o.Advance).I16(o.Empty ? 0 : o.XMin);
        return t.ToArray();
    }

    static byte[] Hhea(List<OutGlyph> glyphs, int asc, int desc, int lineGap)
    {
        var ink = glyphs.Where(o => !o.Empty).ToList();
        int minL = ink.Count > 0 ? ink.Min(o => o.XMin) : 0, minR = ink.Count > 0 ? ink.Min(o => o.Advance - o.XMax) : 0;
        int ext = ink.Count > 0 ? ink.Max(o => o.XMax) : 0;
        return new BE().U32(0x00010000).I16(asc).I16(desc).I16(lineGap).U16(glyphs.Max(o => o.Advance))
            .I16(minL).I16(minR).I16(ext).I16(1).I16(0).I16(0).I16(0).I16(0).I16(0).I16(0).I16(0).U16(glyphs.Count).ToArray();
    }

    static byte[] Maxp(List<OutGlyph> glyphs) =>
        new BE().U32(0x00010000).U16(glyphs.Count).U16(glyphs.Max(o => o.Points)).U16(glyphs.Max(o => o.Contours.Count))
            .U16(0).U16(0).U16(2).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0).ToArray();

    static readonly (int bit, int lo, int hi)[] UnicodeRanges =
    [
        (0, 0x0000, 0x007F), (1, 0x0080, 0x00FF), (2, 0x0100, 0x017F), (3, 0x0180, 0x024F), (4, 0x0250, 0x02AF),
        (5, 0x02B0, 0x02FF), (6, 0x0300, 0x036F), (7, 0x0370, 0x03FF), (9, 0x0400, 0x04FF), (29, 0x1E00, 0x1EFF),
        (31, 0x2000, 0x206F), (32, 0x2070, 0x209F), (33, 0x20A0, 0x20CF), (35, 0x2100, 0x214F), (36, 0x2190, 0x21FF),
        (37, 0x2200, 0x22FF), (45, 0x2500, 0x257F), (46, 0x2580, 0x259F), (47, 0x25A0, 0x25FF), (48, 0x2600, 0x26FF),
        (60, 0xE000, 0xF8FF), (57, 0x10000, 0x10FFFF),
    ];

    static byte[] Os2(List<OutGlyph> glyphs, SortedDictionary<int, int> cmap, FontAsset fa, Spec spec, double s, int asc, int desc, int lineGap)
    {
        int R(double v) => (int)Math.Round(v);
        var adv = glyphs.Where(o => o.Advance > 0).Select(o => o.Advance).ToList();
        int avg = adv.Count > 0 ? R(adv.Average()) : 0;
        var ur = new uint[4];
        foreach (var cp in cmap.Keys)
            foreach (var (bit, lo, hi) in UnicodeRanges)
                if (cp >= lo && cp <= hi) ur[bit / 32] |= 1u << (bit % 32);
        int subX = R(Upm * fa.SubSize), subY = subX;
        int xh = cmap.TryGetValue('x', out var gx) && !glyphs[gx].Empty ? glyphs[gx].YMax : 0;
        int fsSel = spec.Bold ? 0x20 : 0x40;
        return new BE().U16(4).I16(avg).U16(spec.Weight).U16(5).U16(0)
            .I16(subX).I16(subY).I16(0).I16(R(-fa.SubscriptOffset * s))
            .I16(subX).I16(subY).I16(0).I16(R(fa.SuperscriptOffset * s * 0.5))
            .I16(R(fa.StrikethroughThickness * s)).I16(R(fa.Strikethrough * s)).I16(0)
            .Bytes(new byte[10]).U32(ur[0]).U32(ur[1]).U32(ur[2]).U32(ur[3]).Tag("NONE")
            .U16(fsSel).U16(Math.Min(cmap.Keys.First(), 0xFFFF)).U16(Math.Min(cmap.Keys.Last(), 0xFFFF))
            .I16(asc).I16(desc).I16(lineGap).U16(asc).U16(-desc)
            .U32(cmap.ContainsKey(0xE9) || cmap.Keys.Any(k => k < 0x80) ? 1u : 0u).U32(0)
            .I16(xh).I16(R(fa.CapHeight * s)).U16(0).U16(32).U16(0).ToArray();
    }

    static byte[] Post(FontAsset fa, double s) =>
        new BE().U32(0x00030000).U32(0).I16((int)Math.Round(fa.Underline * s)).I16((int)Math.Round(fa.UnderlineThickness * s))
            .U32(0).U32(0).U32(0).U32(0).U32(0).ToArray();

    /// <summary>RIBBI naming: "Book" and "Heavy" are their own legacy families (style Regular) under the typographic
    /// family; "Bold" stays a Bold style of its family.</summary>
    static byte[] Name(Spec spec)
    {
        bool ribbi = spec.Style is "Regular" or "Bold";
        string legacyFamily = ribbi ? spec.Family : spec.Family + " " + spec.Style, legacyStyle = ribbi ? spec.Style : "Regular";
        var recs = new List<(int id, string v)>
        {
            (1, legacyFamily), (2, legacyStyle), (3, "OpenMix: " + spec.Family + " " + spec.Style),
            (4, spec.Family + " " + spec.Style), (5, "Version 1.000"), (6, spec.Family.Replace(" ", "") + "-" + spec.Style),
        };
        if (!ribbi) { recs.Add((16, spec.Family)); recs.Add((17, spec.Style)); }
        var strings = new BE(); var t = new BE();
        t.U16(0).U16(recs.Count).U16(6 + 12 * recs.Count);
        foreach (var (id, v) in recs)
        {
            var b = Encoding.BigEndianUnicode.GetBytes(v);
            t.U16(3).U16(1).U16(0x409).U16(id).U16(b.Length).U16((int)strings.Length);
            strings.Bytes(b);
        }
        return t.Bytes(strings.ToArray()).ToArray();
    }

    static byte[] Head(List<OutGlyph> glyphs, Spec spec)
    {
        var ink = glyphs.Where(o => !o.Empty).ToList();
        long now = (long)(DateTime.UtcNow - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        return new BE().U32(0x00010000).U32(0x00010000).U32(0).U32(0x5F0F3CF5).U16(0x0001).U16(Upm).I64(now).I64(now)
            .I16(ink.Count > 0 ? ink.Min(o => o.XMin) : 0).I16(ink.Count > 0 ? ink.Min(o => o.YMin) : 0)
            .I16(ink.Count > 0 ? ink.Max(o => o.XMax) : 0).I16(ink.Count > 0 ? ink.Max(o => o.YMax) : 0)
            .U16(spec.Bold ? 1 : 0).U16(8).I16(2).I16(1).I16(0).ToArray();
    }

    static byte[] Sfnt(SortedDictionary<string, byte[]> tables)
    {
        int n = tables.Count, sr = 16 * (1 << (int)Math.Floor(Math.Log2(n)));
        var dir = new BE();
        dir.U32(0x00010000).U16(n).U16(sr).U16((int)Math.Log2(sr / 16)).U16(n * 16 - sr);
        var body = new BE();
        int offset = 12 + 16 * n, headAt = -1;
        foreach (var (tag, data) in tables)
        {
            if (tag == "head") headAt = offset + (int)body.Length;
            dir.Tag(tag).U32(Checksum(data)).U32((uint)(offset + body.Length)).U32((uint)data.Length);
            body.Bytes(data).Pad4();
        }
        var font = dir.Bytes(body.ToArray()).ToArray();
        uint adj = unchecked(0xB1B0AFBA - Checksum(font));
        font[headAt + 8] = (byte)(adj >> 24); font[headAt + 9] = (byte)(adj >> 16); font[headAt + 10] = (byte)(adj >> 8); font[headAt + 11] = (byte)adj;
        return font;
    }

    static uint Checksum(byte[] b)
    {
        uint sum = 0;
        for (int i = 0; i < b.Length; i += 4)
        {
            uint v = 0;
            for (int k = 0; k < 4; k++) v = (v << 8) | (i + k < b.Length ? b[i + k] : 0u);
            sum = unchecked(sum + v);
        }
        return sum;
    }
}
