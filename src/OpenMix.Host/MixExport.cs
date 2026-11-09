using System.Diagnostics;
using System.Xml.Serialization;
using OpenMix.Audio;
using OpenMix.Cards;
using OpenMix.Game;

/// <summary>
/// The viewer's export button, done locally (the original rendered a video or made a share link through Hasbro's
/// servers). A mix is rendered offline exactly as playback runs it: saved key/tempo/levels, cards entering in play
/// order every 8 beats, 90 seconds in all. It writes a .wav and the mix itself (a one-mix SnapshotContainer .xml
/// another port can read) into the OS's Documents/OpenMix Exports, then shows them in the file manager.
/// </summary>
public static class MixExport
{
    const int Rate = 24000;
    public const double Seconds = 90;                     // export length

    /// <summary>Documents/OpenMix Exports for this OS: the Windows Documents folder, XDG_DOCUMENTS_DIR (or ~/Documents)
    /// on Linux, ~/Documents on macOS.</summary>
    public static string ExportDir()
    {
        string docs;
        if (OperatingSystem.IsWindows()) docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            docs = Path.Combine(home, "Documents");
            if (OperatingSystem.IsLinux())
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_DOCUMENTS_DIR");
                if (string.IsNullOrEmpty(xdg))
                {   // ~/.config/user-dirs.dirs: XDG_DOCUMENTS_DIR="$HOME/Documents"
                    var cfg = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "user-dirs.dirs");
                    try
                    {
                        var line = File.Exists(cfg) ? File.ReadLines(cfg).FirstOrDefault(l => l.StartsWith("XDG_DOCUMENTS_DIR=")) : null;
                        if (line != null) xdg = line.Split('=', 2)[1].Trim().Trim('"').Replace("$HOME", home);
                    }
                    catch { }
                }
                if (!string.IsNullOrEmpty(xdg) && xdg != home) docs = xdg;
            }
        }
        var dir = Path.Combine(docs, "OpenMix Exports");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static (string wav, string xml) Export(Snapshot m, CardDatabase db, string audioDir)
    {
        var entries = m.Slots.Select(sl => (sl, card: db.ResolveCid(sl.CardId))).Where(x => x.card != null).ToList();
        if (entries.Count == 0) throw new InvalidOperationException("None of this mix's cards are known.");

        var mix = new MixEngine(Rate);
        var dir = new MixDirector(mix, audioDir) { PinMaster = true };
        if (Enum.TryParse<MusicKey>(m.Key, true, out var k) && k != MusicKey.None) dir.SetKey((int)k, m.Mode == "Minor");
        dir.SetTempo(m.Tempo);
        for (int i = 0; i < 5; i++)
        {
            var sl = m.Slots.FirstOrDefault(x => x.Slot == i);
            dir.SetSlotVolume(i, sl == null ? 0.683 : MixUtil.SnapshotVolume(sl));
        }

        // cards at beats 0, 8, 16, ... (Menu_SnapshotViewer beatsBetweenCards); the file runs 90 s
        var outBuf = new List<float>(); var chunk = new float[2400];       // 50 ms
        int next = 0; double b0 = 0;            // beats count from the first card's entry
        while (outBuf.Count < Seconds * Rate * 2)
        {
            while (next < entries.Count && (next == 0 || mix.BeatPosition - b0 >= next * MixPlaybackMode.BeatsBetweenCards))
            {
                var (sl, card) = entries[next++];
                dir.OnSlot(sl.Slot, card!, Rules.AudioParts(card!, sl.Slot), false);
                mix.WaitIdle();                                             // offline: let the renders land
                if (next == 1) b0 = mix.BeatPosition;
            }
            mix.Read(chunk, 0, chunk.Length);
            outBuf.AddRange(chunk);
        }
        // short fade-in (no pop on the first hit) and a 2 s fade-out so the file doesn't end on a click
        var pcm = outBuf.ToArray(); int fade = Math.Min(pcm.Length / 2, Rate * 2);
        for (int f = 0, fin = Math.Min(pcm.Length / 2, Rate / 50); f < fin; f++) { float g = (float)f / fin; pcm[f * 2] *= g; pcm[f * 2 + 1] *= g; }
        for (int f = 0; f < fade; f++) { float g = 1 - (float)f / fade; int at = pcm.Length / 2 - fade + f; pcm[at * 2] *= g; pcm[at * 2 + 1] *= g; }

        string outDir = ExportDir();
        string safe = string.Concat((m.Name.Length > 0 ? m.Name : "Mix").Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c)).Trim();
        string stem = Path.Combine(outDir, $"{safe} {DateTime.Now:yyyy-MM-dd HHmmss}");
        string wav = stem + ".wav", xml = stem + ".dropmix.xml";
        WavWriter.WriteStereo16(wav, pcm, Rate);
        using (var f = File.Create(xml))
            new XmlSerializer(typeof(SnapshotContainer)).Serialize(f, new SnapshotContainer { Snapshots = new() { m } });
        return (wav, xml);
    }

    /// <summary>Show the export in the OS file manager (Explorer selects it; Linux opens the folder).</summary>
    public static void Reveal(string file)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", new[] { "-R", file });
            else Process.Start("xdg-open", new[] { Path.GetDirectoryName(file)! });
        }
        catch (Exception ex) { Console.WriteLine($"export: couldn't open the folder ({ex.Message})"); }
    }
}
