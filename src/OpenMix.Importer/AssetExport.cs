using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.RegularExpressions;

namespace OpenMix.Importer;

/// <summary>Exports the game's sprites, textures and text assets with AssetStudioModCLI (MIT) and places each one
/// the game uses at its path under the content root, following <c>Resources/content_manifest.tsv</c>. The
/// manifest lists only names and asset IDs (source serialized file + PathID) for DropMix 1.9.0, nothing from the game.</summary>
public static class AssetExport
{
    static readonly Regex Exported = new(@"^(?<name>.*) @(?<pid>-?\d+)(?<ext>\.\w+)$", RegexOptions.Compiled);

    /// <summary>Returns (placed, missing) counts.</summary>
    public static (int placed, int missing) Run(string assetStudio, string apkData, string obbData, string work, string contentRoot, Action<string> log)
    {
        // One folder holding the OBB's serialized files next to the APK's, so cross-file references resolve.
        var combined = Path.Combine(work, "combined");
        Directory.CreateDirectory(combined);
        foreach (var f in Directory.EnumerateFiles(obbData)) LinkOrCopy(f, Path.Combine(combined, Path.GetFileName(f)));
        foreach (var f in Directory.EnumerateFiles(apkData))
        {
            var dst = Path.Combine(combined, Path.GetFileName(f));
            if (!File.Exists(dst)) LinkOrCopy(f, dst);
        }

        var outDir = Path.Combine(work, "export");
        RunTool(assetStudio, log, combined, "-t", "tex2d,sprite,textAsset,font", "--image-format", "png",
                "-g", "fileName", "-f", "assetName_pathID", "-o", outDir, "--log-level", "warning");

        // index the export: "<source>_export/<name> @<pathid>.<ext>"
        var index = new Dictionary<(string, string), string>();
        foreach (var dir in Directory.EnumerateDirectories(outDir))
        {
            var src = Path.GetFileName(dir);
            if (src.EndsWith("_export")) src = src[..^7];
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var m = Exported.Match(Path.GetFileName(f));
                if (m.Success) index[(src, m.Groups["pid"].Value)] = f;
            }
        }
        log($"exported {index.Count} assets");

        int placed = 0; var missing = new List<string>();
        foreach (var (dest, src, pid) in Manifest())
        {
            if (!index.TryGetValue((src, pid), out var from)) { missing.Add(dest); continue; }
            var to = Path.Combine(contentRoot, dest.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
            placed++;
        }
        log($"placed {placed} files" + (missing.Count > 0 ? $", {missing.Count} not found (e.g. {string.Join(", ", missing.Take(3))})" : ""));

        var ui = Path.Combine(contentRoot, "assets_raw");
        CropFxPictograms(Path.Combine(ui, "obb_ui"), log);
        TintPointer(ui, log);
        CancelButton(ui, log);
        CopySystemArial(ui, log);
        return (placed, missing.Count);
    }

    public static IEnumerable<(string dest, string src, string pid)> Manifest()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("OpenMix.Importer.Resources.content_manifest.tsv")!;
        using var r = new StreamReader(s);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split('\t');
            if (p.Length >= 3) yield return (p[0], p[1], p[2]);
        }
    }

    /// <summary>FX cards show their pictogram cropped to its contents (fx_X.png → fxp_X.png, 6 px margin).</summary>
    static void CropFxPictograms(string dir, Action<string> log)
    {
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "fx_*.png").OrderBy(f => f, StringComparer.Ordinal))
        {
            using var im = new Bitmap(f);
            var box = AlphaBounds(im, 8);
            if (box == null) continue;
            const int pad = 6;
            var b = Rectangle.FromLTRB(Math.Max(0, box.Value.Left - pad), Math.Max(0, box.Value.Top - pad),
                                       Math.Min(im.Width, box.Value.Right + pad), Math.Min(im.Height, box.Value.Bottom + pad));
            using var crop = im.Clone(b, PixelFormat.Format32bppArgb);
            crop.Save(Path.Combine(dir, "fxp_" + Path.GetFileNameWithoutExtension(f)[3..] + ".png"), ImageFormat.Png);
            n++;
        }
        log($"cropped {n} FX pictograms");
    }

    static Rectangle? AlphaBounds(Bitmap im, int threshold)
    {
        int l = im.Width, t = im.Height, r = -1, btm = -1;
        var data = im.LockBits(new Rectangle(0, 0, im.Width, im.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[data.Stride];
            for (int y = 0; y < im.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < im.Width; x++)
                    if (row[x * 4 + 3] > threshold) { if (x < l) l = x; if (x > r) r = x; if (y < t) t = y; if (y > btm) btm = y; }
            }
        }
        finally { im.UnlockBits(data); }
        return r < 0 ? null : Rectangle.FromLTRB(l, t, r + 1, btm + 1);
    }

    /// <summary>The wheel's pointer is the EQ notch sprite tinted yellow.</summary>
    static void TintPointer(string ui, Action<string> log)
    {
        var src = Path.Combine(ui, "obb_ui", "wheel_pointer_notch.png");
        if (!File.Exists(src)) return;
        using var im = new Bitmap(src);
        using var o = new Bitmap(im.Width, im.Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < im.Height; y++)
            for (int x = 0; x < im.Width; x++)
            {
                var c = im.GetPixel(x, y);
                o.SetPixel(x, y, Color.FromArgb(c.A, c.R * 255 / 255, c.G * 246 / 255, c.B * 30 / 255));
            }
        o.Save(Path.Combine(ui, "obb_ui", "wheel_pointer_yellow.png"), ImageFormat.Png);
        log("tinted the wheel pointer");
    }

    /// <summary>The wheel's Spinner_Cancel glyph is one 256² cell of the FX sprite sheet (column 7, row 4 from the top).</summary>
    static void CancelButton(string ui, Action<string> log)
    {
        var sheet = Path.Combine(ui, "fx_sprite_sheet.png");
        if (!File.Exists(sheet)) return;
        using var im = new Bitmap(sheet);
        if (im.Width < 2048 || im.Height < 1280) return;
        using var cell = im.Clone(new Rectangle(1792, 1024, 256, 256), PixelFormat.Format32bppArgb);
        cell.Save(Path.Combine(ui, "obb_ui", "wheel_cancel_dm_button.png"), ImageFormat.Png);
        log("cut the wheel's cancel button");
    }

    /// <summary>The game's fallback UI font is Arial, which ships with Windows.</summary>
    static void CopySystemArial(string ui, Action<string> log)
    {
        var arial = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        if (File.Exists(arial)) { File.Copy(arial, Path.Combine(ui, "Arial.ttf"), true); log("using Windows' Arial"); }
    }

    static void LinkOrCopy(string from, string to)
    {
        if (File.Exists(to)) return;
        if (!CreateHardLink(to, from, IntPtr.Zero)) File.Copy(from, to);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    internal static void RunTool(string exe, Action<string> log, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // AssetStudioModCLI needs the .NET 9 runtime: use the private copy installed with OpenMix when present
        var dotnet = Path.Combine(AppContext.BaseDirectory, "tools", "dotnet");
        if (Directory.Exists(Path.Combine(dotnet, "host", "fxr"))) psi.Environment["DOTNET_ROOT"] = dotnet;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string? line; string last = "";
        while ((line = p.StandardOutput.ReadLine()) != null) if (line.Trim().Length > 0) last = line.Trim();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({p.ExitCode}): {err.Result.Trim()} {last}");
        if (last.Length > 0) log(last);
    }
}
