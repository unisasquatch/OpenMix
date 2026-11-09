namespace OpenMix.Board;

/// <summary>Pure, transport-free implementation of the DropMix board wire protocol:
/// request builders, response-frame reassembly, A3/A4/A5 parsing, slot mapping and the
/// LED encoders. No BLE here, so it is fully unit-testable.
///
/// Framing (DATA_TX / response): byte[0] is ignored; byte[1] bit7 = "more fragments
/// follow", bits0..5 = length with total frame length = len + 2; byte[2] is the opcode
/// in the FIRST fragment only, payload starts at byte[3] (first) or byte[2]
/// (continuation). When "more" is set, the host writes <see cref="ContinueCommand"/> to
/// OP_STS to pull the next fragment. There is no CRC.</summary>
public static class BoardProtocol
{
    public const byte OpPoll = 0xA3;       // poll all slots: counts + CTI-XOR
    public const byte OpReadUids = 0xA4;   // full read with 64-bit UIDs
    public const byte OpReadCtis = 0xA5;   // full read, CTIs only
    public const byte OpSignature = 0xA6;  // request ECDSA signature for a UID

    /// <summary>Written to OP_STS (0004) to request the next response fragment.</summary>
    public static ReadOnlySpan<byte> ContinueCommand => new byte[] { 0x60, 0x00 };

    public enum ReadSize { Tiny = 4, Small = 5, Medium = 6, Large = 7 }

    /// <summary>Board hardware numbers slots in reverse: game slot s ⇔ board slot 4-s.
    /// The slot mask bit for game slot s is therefore 1 &lt;&lt; (4 - s).</summary>
    public static int BoardIndex(Slot s) => 4 - (int)s;
    public static Slot FromBoardIndex(int boardIndex) => (Slot)(4 - boardIndex);
    public static byte SlotMaskBit(Slot s) => (byte)(1 << BoardIndex(s));

    /// <summary>A3: poll request. <paramref name="slotMask"/> low 5 bits select slots
    /// (0x1F = all); the read size occupies the top 3 bits.</summary>
    public static byte[] BuildPoll(byte slotMask = 0x1F, ReadSize size = ReadSize.Tiny)
        => new byte[] { OpPoll, (byte)(((int)size << 5) | (slotMask & 0x1F)), 0, 0, 0, 0, 0, 0 };

    /// <summary>A5: read CTIs only for the given slots.</summary>
    public static byte[] BuildReadCtis(byte slotMask)
        => new byte[] { OpReadCtis, slotMask, 0, 0, 0, 0, 0, 0 };

    /// <summary>A4: read full records (incl. 64-bit UIDs) for the given slots.</summary>
    public static byte[] BuildReadUids(byte slotMask)
        => new byte[] { OpReadUids, slotMask, 0, 0, 0, 0, 0, 0 };

    /// <summary>A6: request the card signature for the low 48 bits of a UID (big-endian).</summary>
    public static byte[] BuildSignatureRequest(ulong uid)
        => new byte[]
        {
            OpSignature, 0xFF,
            (byte)(uid >> 40), (byte)(uid >> 32), (byte)(uid >> 24),
            (byte)(uid >> 16), (byte)(uid >> 8),  (byte)uid,
        };

    /// <summary>CTI = top 16 bits of the 64-bit card UID (the original's config/CardDatabase.GUIDToCTI).</summary>
    public static Cti CtiFromUid(ulong uid) => new((ushort)(uid >> 48));

    // ---- Response framing -------------------------------------------------------

    public readonly record struct Frame(byte Opcode, byte[] Payload);

    /// <summary>Reassembles a possibly-fragmented response. Feed each DATA_TX
    /// notification; when a frame completes it is returned and <see cref="NeedsContinue"/>
    /// resets. While <see cref="NeedsContinue"/> is true the caller must write
    /// <see cref="ContinueCommand"/> to OP_STS and feed the next notification.</summary>
    public sealed class FrameReassembler
    {
        private readonly List<byte> _payload = new();
        private byte _opcode;
        private bool _started;

        public bool NeedsContinue { get; private set; }

        /// <summary>Feed one raw notification. Returns a completed <see cref="Frame"/>
        /// or null if more fragments are required.</summary>
        public Frame? Feed(byte[] raw)
        {
            if (raw.Length < 3) return null;
            bool more = (raw[1] & 0x80) != 0;
            int len = raw[1] & 0x3F;
            int frameLen = len + 2;                 // total bytes this fragment occupies
            int end = Math.Min(frameLen, raw.Length);

            if (!_started)
            {
                _opcode = raw[2];
                _started = true;
                for (int i = 3; i < end; i++) _payload.Add(raw[i]);   // skip opcode
            }
            else
            {
                for (int i = 2; i < end; i++) _payload.Add(raw[i]);   // no opcode in continuations
            }

            NeedsContinue = more;
            if (more) return null;

            var frame = new Frame(_opcode, _payload.ToArray());
            _payload.Clear();
            _started = false;
            return frame;
        }
    }

    // ---- Response parsing -------------------------------------------------------

    /// <summary>Parse an A3 poll payload (after the opcode): 5 stack counts then 5
    /// big-endian 16-bit CTI-XORs, indexed by BOARD slot. Count bit7 = read error.</summary>
    public static SlotSummary[] ParsePoll(ReadOnlySpan<byte> payload)
    {
        var result = new SlotSummary[5];
        if (payload.Length < 15) return result;     // 5 counts + 5*2 xor
        for (int b = 0; b < 5; b++)
        {
            byte c = payload[b];
            ushort xor = (ushort)((payload[5 + b * 2] << 8) | payload[6 + b * 2]);
            result[b] = new SlotSummary(c & 0x7F, xor, (c & 0x80) != 0);
        }
        return result;
    }

    /// <summary>Parse an A5 payload: big-endian uint16 CTI per card (count = len/2).</summary>
    public static Cti[] ParseCtis(ReadOnlySpan<byte> payload)
    {
        var list = new Cti[payload.Length / 2];
        for (int i = 0; i < list.Length; i++)
            list[i] = new Cti((ushort)((payload[i * 2] << 8) | payload[i * 2 + 1]));
        return list;
    }

    /// <summary>Parse an A4 payload: 10-byte records; UID is big-endian at record
    /// offset 2..9. (The leading 2 bytes of each record are not yet identified.)</summary>
    public static ulong[] ParseUids(ReadOnlySpan<byte> payload)
    {
        var list = new ulong[payload.Length / 10];
        for (int r = 0; r < list.Length; r++)
        {
            ulong uid = 0;
            for (int k = 2; k < 10; k++) uid = (uid << 8) | payload[r * 10 + k];
            list[r] = uid;
        }
        return list;
    }

    // ---- LED encoders -----------------------------------------------------------

    private static int Q4(float v) => Math.Clamp((int)MathF.Round(v * 15f), 0, 15);

    /// <summary>IMD_LED (0006): 10 LEDs × { (flag&lt;&lt;4)|R4, (G4&lt;&lt;4)|B4 } = 20 bytes.
    /// The flag nibble's meaning is unknown and left 0.</summary>
    public static byte[] EncodeImmediateLeds(IReadOnlyList<LedColor> leds10)
    {
        var buf = new byte[20];
        for (int i = 0; i < 10; i++)
        {
            LedColor c = i < leds10.Count ? leds10[i] : LedColor.Off;
            buf[i * 2] = (byte)(Q4(c.R));                   // flag nibble = 0
            buf[i * 2 + 1] = (byte)((Q4(c.G) << 4) | Q4(c.B));
        }
        return buf;
    }

    /// <summary>LED_BRT_SEQ (0007): 5 slots × 8 brightness steps packed as nibbles
    /// (high nibble first), 4 bytes per slot = 20 bytes. Slot order is identity
    /// (RemapSlotForLights returns its argument unchanged).</summary>
    public static byte[] EncodeBrightnessSeq(float[][] perSlot8)
    {
        var buf = new byte[20];
        for (int s = 0; s < 5; s++)
            for (int i = 0; i < 8; i++)
            {
                int nib = (s < perSlot8.Length && i < perSlot8[s].Length)
                    ? Math.Clamp((int)MathF.Floor(perSlot8[s][i] * 16f), 0, 15) : 0;
                int byteIndex = s * 4 + i / 2;
                buf[byteIndex] |= (byte)(i % 2 == 0 ? nib << 4 : nib);   // high nibble first
            }
        return buf;
    }

    /// <summary>LED_TIME (0008): single step-timing byte (the game writes 0x14).</summary>
    public static byte[] LedTime(byte value = 0x14) => new[] { value };
}
