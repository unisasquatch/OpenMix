using System.Xml.Serialization;
using OpenMix.Audio;

/// <summary>
/// Saved mixes ("snapshots"), kept in the original's own format: an XmlSerializer <c>SnapshotContainer</c>.
/// The player's mixes are saved to data/mixes/snapshots.xml in that format. OpenMix ships no default mixes; a
/// data/mixes/snapshots_default.xml is read only if one is present.
/// </summary>
public sealed class MixStore
{
    readonly string _userPath, _defaultPath;
    readonly object _lock = new();
    List<Snapshot> _user = new(), _defaults = new();

    public MixStore(string dataDir)
    {
        var dir = Path.Combine(dataDir, "mixes"); Directory.CreateDirectory(dir);
        _userPath = Path.Combine(dir, "snapshots.xml");
        _defaultPath = Path.Combine(dir, "snapshots_default.xml");
        _defaults = Read(_defaultPath);
        foreach (var s in _defaults) s.Source = "kShared";
        _user = Read(_userPath);
    }

    static List<Snapshot> Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            using var f = File.OpenRead(path);
            return ((SnapshotContainer?)new XmlSerializer(typeof(SnapshotContainer)).Deserialize(f))?.Snapshots ?? new();
        }
        catch (Exception ex) { Console.WriteLine($"mixes: couldn't read {Path.GetFileName(path)} ({ex.Message})"); return new(); }
    }

    void WriteUser()
    {
        var tmp = _userPath + ".tmp";
        using (var f = File.Create(tmp))
            new XmlSerializer(typeof(SnapshotContainer)).Serialize(f, new SnapshotContainer { Snapshots = _user });
        File.Move(tmp, _userPath, overwrite: true);
    }

    /// <summary>The player's mixes (newest first), then the defaults. Ids: "u{index}" / "d{index}".</summary>
    public List<(string Id, Snapshot S, bool Default)> All()
    {
        lock (_lock)
        {
            var list = _user.Select((s, i) => ($"u{i}", s, false)).OrderByDescending(x => x.s.Time).ToList();
            list.AddRange(_defaults.Select((s, i) => ($"d{i}", s, true)));
            return list;
        }
    }

    public Snapshot? Get(string id)
    {
        lock (_lock)
        {
            if (id.Length < 2 || !int.TryParse(id[1..], out int i)) return null;
            var l = id[0] == 'u' ? _user : id[0] == 'd' ? _defaults : null;
            return l != null && i >= 0 && i < l.Count ? l[i] : null;
        }
    }

    public string Add(Snapshot s)
    {
        lock (_lock) { _user.Add(s); WriteUser(); return $"u{_user.Count - 1}"; }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (id.Length < 2 || id[0] != 'u' || !int.TryParse(id[1..], out int i) || i < 0 || i >= _user.Count) return false;
            _user.RemoveAt(i); WriteUser(); return true;
        }
    }

    public bool Rename(string id, string name)
    {
        lock (_lock)
        {
            if (id.Length < 2 || id[0] != 'u' || !int.TryParse(id[1..], out int i) || i < 0 || i >= _user.Count) return false;
            _user[i].Name = name; WriteUser(); return true;
        }
    }

    public int UserCount { get { lock (_lock) return _user.Count; } }
}

[XmlRoot("SnapshotContainer")]
public sealed class SnapshotContainer
{
    [XmlArray("mSnapshots"), XmlArrayItem("Snapshot")] public List<Snapshot> Snapshots { get; set; } = new();
}

public sealed class Snapshot
{
    [XmlElement("mTime")] public DateTime Time { get; set; }
    [XmlArray("mSlots"), XmlArrayItem("SnapshotSlot")] public List<SnapshotSlot> Slots { get; set; } = new();
    [XmlElement("mName")] public string Name { get; set; } = "";
    [XmlElement("mKey")] public string Key { get; set; } = "C";        // MusicKey enum name (C, Cs, D, ...)
    [XmlElement("mMode")] public string Mode { get; set; } = "Major";
    [XmlElement("mTempo")] public double Tempo { get; set; } = 120;
    [XmlElement("mSnapshotSource")] public string Source { get; set; } = "kLocal";
}

public sealed class SnapshotSlot
{
    [XmlElement("mCardId")] public string CardId { get; set; } = "";
    [XmlElement("mSlot")] public int Slot { get; set; }
    [XmlElement("mLevel")] public float Level { get; set; } = 0.68235296f;
    [XmlElement("mLevel1")] public float Level1 { get; set; }
    [XmlElement("mVersion")] public int Version { get; set; }
    [XmlElement("mMixLinkState", IsNullable = true)] public string? MixLinkState { get; set; }
}

public static class MixUtil
{

    /// <summary>Playback level for a saved slot (the snapshot accessor): v0 (shipped) = clamp(max(L,L1), min, 1);
    /// v1 = the side's level scaled so the louder side lands at clamp(max, min, 1). min = SnapshotManager.minimumSnapshotVolume
    /// (imported); without it the saved level plays as is.</summary>
    public static double SnapshotVolume(SnapshotSlot sl)
    {
        double Min = OpenMix.Cards.GameTables.Snapshots?.MinimumVolume ?? 0;
        double max = Math.Max(sl.Level, sl.Level1);
        if (sl.Version == 0) return Math.Clamp(max, Min, 1);
        if (max < 1e-3) return Min;
        return Math.Clamp(sl.Level * Math.Clamp(max, Min, 1) / max, 0, 1);
    }

    public static string KeyLabel(Snapshot m)
    {
        string k = Enum.TryParse<OpenMix.Cards.MusicKey>(m.Key, true, out var kk) && kk != OpenMix.Cards.MusicKey.None
            ? MixDirector.KeyName((int)kk) : m.Key;
        return $"{k} {(m.Mode == "Minor" ? "MINOR" : "MAJOR")}";
    }
}
