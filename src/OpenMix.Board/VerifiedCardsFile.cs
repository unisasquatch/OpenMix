using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace OpenMix.Board;

/// <summary>
/// A portable save of the cards a board has signature-checked, so a collection can move between your own PCs without
/// the board. It stores only each card's UID and the signature the board returned for it. On load every signature is
/// checked again against the cards' public key and any entry that fails is dropped; which card an entry is always comes
/// from its UID, never from anything else in the file.
///
/// Limit, from DropMix's design: a signature covers the card's 48-bit serial, not the 16-bit card type above it. So the
/// file can't prove which card a serial belongs to. It can stop one signed serial counting as several cards, so each
/// serial is accepted once: a file never holds more cards than were really verified on a board.
/// </summary>
public static class VerifiedCardsFile
{
    public const string FormatName = "openmix-verified-cards";
    public const int FormatVersion = 1;
    public const ulong SerialMask = 0x0000_FFFF_FFFF_FFFFUL;

    public sealed record Entry(string Uid, string Sig, DateTime Added);
    public sealed record Doc(string Format, int Version, DateTime Saved, Entry[] Cards);
    public readonly record struct VerifiedCard(ulong Uid, byte[] Sig, DateTime Added);
    public sealed record LoadResult(List<VerifiedCard> Cards, int Rejected, string? Error);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    /// <summary>Writes the save atomically (temp file, then rename), so a crash never leaves half a file.</summary>
    public static void Save(string path, IEnumerable<VerifiedCard> cards)
    {
        var doc = new Doc(FormatName, FormatVersion, DateTime.UtcNow,
            cards.Select(c => new Entry(c.Uid.ToString("X16"), Convert.ToHexString(c.Sig), c.Added)).ToArray());
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
        File.Move(tmp, full, overwrite: true);
    }

    public static LoadResult Load(string path) => Load(path, CardSignature.PublicKey);

    /// <summary>Reads a save and keeps only entries whose signature verifies, one per serial.</summary>
    public static LoadResult Load(string path, (BigInteger X, BigInteger Y) key)
    {
        Doc? doc;
        try { doc = JsonSerializer.Deserialize<Doc>(File.ReadAllText(path), Json); }
        catch (Exception ex) { return new(new(), 0, ex.Message); }
        if (doc == null || doc.Format != FormatName) return new(new(), 0, "not an OpenMix verified-cards file");
        if (doc.Version > FormatVersion) return new(new(), 0, $"made by a newer OpenMix (format {doc.Version})");

        var ok = new List<VerifiedCard>();
        var serials = new HashSet<ulong>();
        int rejected = 0;
        foreach (var e in doc.Cards ?? Array.Empty<Entry>())
        {
            if (e == null
                || !ulong.TryParse(e.Uid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var uid)
                || !TryHex(e.Sig, out var sig)
                || !CardSignature.VerifyCard(uid, sig, key)
                || !serials.Add(uid & SerialMask))
            { rejected++; continue; }
            ok.Add(new VerifiedCard(uid, sig, e.Added));
        }
        return new(ok, rejected, null);
    }

    static bool TryHex(string? s, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(s) || s.Length % 2 != 0) return false;
        try { bytes = Convert.FromHexString(s); return true; } catch (FormatException) { return false; }
    }
}
