using System.Text.Json;
using OpenMix.Board;

/// <summary>
/// The local card collection (CardCollection). The original registered card UIDs with Hasbro's server after the
/// board's A6 signature checked out; the port keeps them in data/collection.json instead. A card is "owned" by its
/// physical UID, so two copies of a card are two entries; the collection view counts distinct cards.
///
/// With a save file (UseSaveFile), the collection is also mirrored to a portable verified-cards file, and cards in that
/// file are added at start after their signatures are checked again (see VerifiedCardsFile).
/// </summary>
public sealed class CardCollection
{
    /// <param name="Sig">The card's signature read from the board (hex): the proof of ownership checked before downloads.</param>
    public sealed record Entry(string Uid, string Cid, string? Icon, DateTime At, string? Sig = null);

    readonly Dictionary<ulong, Entry> _byUid = new();
    readonly string _file;
    readonly object _lock = new();
    string? _saveFile;

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

    /// <summary>The portable save in use, if any.</summary>
    public string? SaveFile => _saveFile;

    /// <summary>Uses a portable verified-cards save: adds the cards in it whose signatures verify (each card worked out
    /// from its UID by resolve; unknown cards are skipped), then keeps the save up to date with the collection.</summary>
    /// <param name="testKey">Tests only: verify against this public key instead of the cards' real one.</param>
    public (int Added, int Rejected) UseSaveFile(string path, Func<ulong, (string Cid, string? Icon)?> resolve,
        (System.Numerics.BigInteger X, System.Numerics.BigInteger Y)? testKey = null)
    {
        int added = 0, rejected = 0;
        lock (_lock)
        {
            _saveFile = path;
            if (File.Exists(path))
            {
                var r = testKey is { } k ? VerifiedCardsFile.Load(path, k) : VerifiedCardsFile.Load(path);
                if (r.Error != null) Console.WriteLine($"collection: couldn't use {path} ({r.Error})");
                rejected = r.Rejected;
                // a signed serial counts once across the whole collection too
                var serials = _byUid.Keys.Select(u => u & VerifiedCardsFile.SerialMask).ToHashSet();
                foreach (var c in r.Cards)
                {
                    if (_byUid.ContainsKey(c.Uid)) continue;
                    if (!serials.Add(c.Uid & VerifiedCardsFile.SerialMask) || resolve(c.Uid) is not { } card) { rejected++; continue; }
                    _byUid[c.Uid] = new Entry(c.Uid.ToString("X16"), card.Cid, card.Icon, c.Added, Convert.ToHexString(c.Sig));
                    added++;
                }
                Console.WriteLine($"collection: save {path}: {added} cards added, {rejected} rejected (collection {_byUid.Count})");
            }
            Save();
        }
        return (added, rejected);
    }

    void Save()
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(_byUid.Values.OrderBy(e => e.At).ToArray(), new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Console.WriteLine($"collection: couldn't save ({ex.Message})"); }
        if (_saveFile == null) return;
        try
        {
            var cards = new List<VerifiedCardsFile.VerifiedCard>();
            foreach (var (uid, e) in _byUid.OrderBy(p => p.Value.At))
                if (e.Sig is { Length: > 0 } hex && TryHex(hex, out var sig)) cards.Add(new(uid, sig, e.At));
            VerifiedCardsFile.Save(_saveFile, cards);
        }
        catch (Exception ex) { Console.WriteLine($"collection: couldn't write the save {_saveFile} ({ex.Message})"); }
    }

    static bool TryHex(string s, out byte[] b)
    {
        try { b = Convert.FromHexString(s); return true; } catch (FormatException) { b = Array.Empty<byte>(); return false; }
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

    /// <summary>Empties the collection. The portable save is emptied too, after a copy of it is kept beside it
    /// (verified-cards.before-reset.json), so a reset can't lose the only copy of your verified cards.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (_saveFile != null && File.Exists(_saveFile) && _byUid.Count > 0)
            {
                try { File.Copy(_saveFile, Path.ChangeExtension(_saveFile, ".before-reset.json"), overwrite: true); }
                catch (Exception ex) { Console.WriteLine($"collection: couldn't back up the save before reset ({ex.Message})"); }
            }
            _byUid.Clear(); Save();
        }
    }
}
