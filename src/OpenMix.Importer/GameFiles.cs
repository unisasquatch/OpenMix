using System.IO.Compression;
using System.Text.RegularExpressions;

namespace OpenMix.Importer;

/// <summary>Unpacks the user's own APK and OBB (both zip files) into a work folder, rejoining Unity's
/// <c>*.splitN</c> files so tools can read the assets as single files.</summary>
public static class GameFiles
{
    /// <summary>Extract every entry under <paramref name="prefix"/> (e.g. "assets/") into <paramref name="dest"/>,
    /// keeping paths relative to the prefix. Returns the number of files written.</summary>
    public static int Extract(string zipPath, string prefix, string dest, Action<string>? log = null)
    {
        using var z = ZipFile.OpenRead(zipPath);
        int n = 0;
        foreach (var e in z.Entries)
        {
            if (e.FullName.EndsWith('/') || !e.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var outPath = SafeJoin(dest, e.FullName[prefix.Length..]);
            if (outPath == null) continue;                              // would land outside dest: skip
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            e.ExtractToFile(outPath, overwrite: true);
            n++;
        }
        log?.Invoke($"unpacked {n} files from {Path.GetFileName(zipPath)}");
        JoinSplits(dest, log);
        return n;
    }

    /// <summary><paramref name="root"/> + <paramref name="relative"/> as a full path, or null if the result would not be
    /// strictly inside root (rooted paths, drive letters, "..", or a sibling folder that merely starts with root's name).</summary>
    public static string? SafeJoin(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) return null;
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(Path.Combine(full, relative));
        return p.StartsWith(full, StringComparison.OrdinalIgnoreCase) && p.Length > full.Length ? p : null;
    }

    static readonly Regex SplitRx =new(@"^(?<name>.+)\.split(?<n>\d+)$", RegexOptions.Compiled);

    /// <summary>Concatenate <c>name.split0..N</c> into <c>name</c> (numeric order) and delete the parts.</summary>
    public static void JoinSplits(string dir, Action<string>? log = null)
    {
        var groups = Directory.EnumerateFiles(dir, "*.split*", SearchOption.AllDirectories)
            .Select(f => (f, m: SplitRx.Match(f))).Where(x => x.m.Success)
            .GroupBy(x => x.m.Groups["name"].Value);
        foreach (var g in groups)
        {
            var parts = g.OrderBy(x => int.Parse(x.m.Groups["n"].Value)).Select(x => x.f).ToList();
            using (var o = File.Create(g.Key))
                foreach (var p in parts) using (var i = File.OpenRead(p)) i.CopyTo(o);
            foreach (var p in parts) File.Delete(p);
            log?.Invoke($"joined {parts.Count} parts -> {Path.GetFileName(g.Key)}");
        }
    }
}
