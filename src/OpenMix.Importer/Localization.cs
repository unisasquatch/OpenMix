using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenMix.Importer;

/// <summary>Builds data/loc/strings.json (13 languages) from the string literals in the user's own
/// <c>global-metadata.dat</c>. <c>Resources/loc_manifest.tsv</c> lists only which literal (by index) each language's
/// key uses; the text itself comes from the user's file.</summary>
public static class Localization
{
    public static int Build(string metadataPath, string dataDir, Action<string> log)
    {
        var d = File.ReadAllBytes(metadataPath);
        if (BitConverter.ToUInt32(d, 0) != 0xFAB11BAF) throw new InvalidDataException("global-metadata.dat isn't IL2CPP metadata");
        int litOff = BitConverter.ToInt32(d, 8), litBytes = BitConverter.ToInt32(d, 12), dataOff = BitConverter.ToInt32(d, 16);
        int count = litBytes / 8;
        string Literal(int i)
        {
            if (i < 0 || i >= count) throw new InvalidDataException($"string literal {i} out of range");
            uint len = BitConverter.ToUInt32(d, litOff + i * 8); int at = BitConverter.ToInt32(d, litOff + i * 8 + 4);
            return Encoding.UTF8.GetString(d, dataOff + at, (int)len);
        }

        var langs = new Dictionary<string, Dictionary<string, string>>();
        int n = 0;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("OpenMix.Importer.Resources.loc_manifest.tsv")!)
        using (var r = new StreamReader(s))
        {
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split('\t');
                if (!langs.TryGetValue(p[0], out var t)) langs[p[0]] = t = new();
                t[p[1]] = Literal(int.Parse(p[2]));
                n++;
            }
        }
        var outDir = Path.Combine(dataDir, "loc");
        Directory.CreateDirectory(outDir);
        var json = JsonSerializer.Serialize(langs, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(Path.Combine(outDir, "strings.json"), json, new UTF8Encoding(false));
        log($"strings: {langs.Count} languages, {n} entries");
        return n;
    }
}
