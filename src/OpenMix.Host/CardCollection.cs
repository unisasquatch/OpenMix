using System.Text.Json;

/// <summary>
/// The local card collection (CardCollection). The original registered card UIDs with Hasbro's server after the
/// board's A6 signature checked out; the port keeps them in data/collection.json instead. A card is "owned" by its
/// physical UID, so two copies of a card are two entries; the collection view counts distinct cards.
/// </summary>
public sealed class CardCollection
{
    /// <param name="Sig">The card's signature read from the board (hex): the proof of ownership checked before downloads.</param>
    public sealed record Entry(string Uid, string Cid, string? Icon, DateTime At, string? Sig = null);

    readonly Dictionary<ulong, Entry> _byUid = new();
    readonly string _file;
    readonly object _lock = new();

    public CardCollection(string dataDir)
    {
        _file = Path.Combine(dataDir, "collection.json");
        try
        {
            if (File.Exists(_file))
                foreach (var e in JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(_file)) ?? Array.Empty<Entry>())
                    if (ulong.TryParse(e.Uid, System.Globalization.NumberStyles.HexNumber, null, out var u)) _byUid[u] = e;
        }
        catch (Exception ex) { Console.WriteLine($"collection: couldn't read collection.json ({ex.Message})"); }
    }

    void Save()
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(_byUid.Values.OrderBy(e => e.At).ToArray(), new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Console.WriteLine($"collection: couldn't save ({ex.Message})"); }
    }

    public bool Owns(ulong uid) { lock (_lock) return _byUid.ContainsKey(uid); }

    /// <summary>Adds a verified card; false if that physical card was already in the collection.</summary>
    public bool Add(ulong uid, string cid, string? icon, byte[] sig)
    {
        lock (_lock)
        {
            if (_byUid.ContainsKey(uid)) return false;
            _byUid[uid] = new Entry(uid.ToString("X16"), cid, icon, DateTime.UtcNow, Convert.ToHexString(sig));
            Save();
            return true;
        }
    }

    public HashSet<string> OwnedCids() { lock (_lock) return _byUid.Values.Select(e => e.Cid).ToHashSet(); }
    public int Count { get { lock (_lock) return _byUid.Count; } }

    public void Reset() { lock (_lock) { _byUid.Clear(); Save(); } }
}
