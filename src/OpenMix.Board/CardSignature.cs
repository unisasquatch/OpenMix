using System.Numerics;

namespace OpenMix.Board;

/// <summary>
/// Card authenticity: ECDSA over secp128r1, NONEwithECDSA, with the public key
/// bundled in the app. The signed message is 12 bytes, e2 00 68 83 00 00 || the UID's low 48 bits (big-endian); with
/// NONEwithECDSA the message itself is the integer e (96 bits, shorter than the 128-bit order, so no truncation).
/// The board's A6 reply carries the raw r‖s (16 + 16 bytes). Implemented with BigInteger; no crypto package needed.
/// </summary>
public static class CardSignature
{
    static readonly BigInteger P = Hex("FFFFFFFDFFFFFFFFFFFFFFFFFFFFFFFF");
    static readonly BigInteger A = Hex("FFFFFFFDFFFFFFFFFFFFFFFFFFFFFFFC");
    static readonly BigInteger B = Hex("E87579C11079F43DD824993C2CEE5ED3");
    static readonly BigInteger N = Hex("FFFFFFFE0000000075A30D1B9038A115");
    static readonly (BigInteger X, BigInteger Y) G = (Hex("161FF7528B899B2D0C28607CA52C5B86"), Hex("CF5AC8395BAFEB13C02DA292DDED7A83"));
    /// <summary>The public key DropMix cards are signed against.</summary>
    public static readonly (BigInteger X, BigInteger Y) PublicKey = (Hex("0C957064D87786FAB95A319A9E2B79A7"), Hex("188DD3DC2DDC4EB9317D8C828B7E5C0A"));
    static readonly byte[] Prefix = { 0xE2, 0x00, 0x68, 0x83, 0x00, 0x00 };

    static BigInteger Hex(string h) => BigInteger.Parse("0" + h, System.Globalization.NumberStyles.HexNumber);
    static BigInteger Mod(BigInteger a, BigInteger m) { var r = a % m; return r.Sign < 0 ? r + m : r; }
    static BigInteger Inv(BigInteger a, BigInteger m) => BigInteger.ModPow(Mod(a, m), m - 2, m);
    static BigInteger Be(ReadOnlySpan<byte> b) => new(b, isUnsigned: true, isBigEndian: true);

    /// <summary>The 12-byte message the card's signature covers.</summary>
    public static byte[] Message(ulong uid)
    {
        var m = new byte[12];
        Prefix.CopyTo(m, 0);
        for (int i = 0; i < 6; i++) m[6 + i] = (byte)(uid >> (40 - 8 * i));
        return m;
    }

    public static bool OnCurve((BigInteger X, BigInteger Y) q) =>
        Mod(q.Y * q.Y - (q.X * q.X * q.X + A * q.X + B), P).IsZero;

    // affine point arithmetic; null = the point at infinity
    static (BigInteger X, BigInteger Y)? Add((BigInteger X, BigInteger Y)? p, (BigInteger X, BigInteger Y)? q)
    {
        if (p is not { } a) return q;
        if (q is not { } b) return p;
        BigInteger l;
        if (a.X == b.X)
        {
            if (Mod(a.Y + b.Y, P).IsZero) return null;
            l = Mod(3 * a.X * a.X + A, P) * Inv(2 * a.Y, P);
        }
        else l = Mod(b.Y - a.Y, P) * Inv(b.X - a.X, P);
        l = Mod(l, P);
        var x = Mod(l * l - a.X - b.X, P);
        return (x, Mod(l * (a.X - x) - a.Y, P));
    }

    static (BigInteger X, BigInteger Y)? Mul(BigInteger k, (BigInteger X, BigInteger Y)? p)
    {
        (BigInteger X, BigInteger Y)? r = null;
        for (int i = (int)k.GetBitLength() - 1; i >= 0; i--)
        {
            r = Add(r, r);
            if (!(k >> i).IsEven) r = Add(r, p);
        }
        return r;
    }

    /// <summary>ECDSA verify of (r, s) over the message with the given public key.</summary>
    public static bool Verify(byte[] message, BigInteger r, BigInteger s, (BigInteger X, BigInteger Y) q)
    {
        if (r <= 0 || r >= N || s <= 0 || s >= N) return false;
        var e = Be(message);
        if (message.Length * 8 > 128) e >>= message.Length * 8 - 128;     // leftmost bits of the order's length
        var w = Inv(s, N);
        var pt = Add(Mul(Mod(e * w, N), G), Mul(Mod(r * w, N), q));
        return pt is { } v && Mod(v.X, N) == r;
    }

    /// <summary>Checks a board A6 reply for a card UID. Accepts the raw r‖s (32 bytes) wherever it sits in the
    /// payload (a leading status/length byte is tolerated).</summary>
    public static bool VerifyCard(ulong uid, ReadOnlySpan<byte> sig) => VerifyCard(uid, sig, PublicKey);

    public static bool VerifyCard(ulong uid, ReadOnlySpan<byte> sig, (BigInteger X, BigInteger Y) key)
    {
        if (sig.Length < 32) return false;
        var msg = Message(uid);
        for (int off = 0; off + 32 <= sig.Length; off++)
            if (Verify(msg, Be(sig.Slice(off, 16)), Be(sig.Slice(off + 16, 16)), key)) return true;
        return false;
    }

    /// <summary>Test helper: signs with a given private key (the cards' real key is not in the app).</summary>
    public static byte[] SignForTest(ulong uid, BigInteger d, BigInteger k)
    {
        var e = Be(Message(uid));
        var r = Mod(Mul(k, G)!.Value.X, N);
        var s = Mod(Inv(k, N) * (e + r * d), N);
        var o = new byte[32];
        r.TryWriteBytes(o.AsSpan(16 - r.GetByteCount(true), r.GetByteCount(true)), out _, true, true);
        s.TryWriteBytes(o.AsSpan(32 - s.GetByteCount(true), s.GetByteCount(true)), out _, true, true);
        return o;
    }

    public static (BigInteger X, BigInteger Y) PublicFor(BigInteger d) => Mul(d, G)!.Value;
}
