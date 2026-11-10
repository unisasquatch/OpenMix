using OpenMix.Board;

namespace OpenMix.Board.TestConsole;

/// <summary>Hardware-free checks of the pure protocol layer (framing, poll parsing,
/// CTI-XOR add/remove tracking, LED encoding). Run with <c>-- --selftest</c>.</summary>
public static class SelfTest
{
    private static int _pass, _fail;

    private static void Check(bool ok, string what)
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}"); }
    }

    public static int Run()
    {
        Console.WriteLine("protocol self-test");

        // request builders & slot mapping
        var poll = BoardProtocol.BuildPoll();
        Check(poll[0] == 0xA3 && poll[1] == 0x9F && poll.Length == 8, "BuildPoll => A3 9F (tiny, all slots)");
        Check(BoardProtocol.SlotMaskBit(Slot.Slot0) == 0x10 && BoardProtocol.SlotMaskBit(Slot.Slot4) == 0x01,
            "slot mask: game0=>bit4, game4=>bit0");
        Check(BoardProtocol.BoardIndex(Slot.Slot0) == 4 && BoardProtocol.FromBoardIndex(4) == Slot.Slot0,
            "board/game slot mapping is a 4-s mirror");
        Check(BoardProtocol.CtiFromUid(0x1234_0000_0000_0000UL).Value == 0x1234, "CTI = top 16 bits of UID");

        var sig = BoardProtocol.BuildSignatureRequest(0x0000_8899_AABB_CCDDUL);
        // low 48 bits, big-endian => 88 99 AA BB CC DD
        // the original app's light packets (the original's board lights, see docs/BOARD.md)
        Check(Convert.ToHexString(BoardLeds.Encode(new[] { BoardLeds.White, BoardLeds.Red, BoardLeds.Black, BoardLeds.Unset, BoardLeds.FromColour(0.5, 0.25, 0.99) }))
              == "1FFF1F0010000000173E" + new string('0', 20), "IMD_LED colour encoding = floor(x*15) nibbles with the set flag");
        Check(Convert.ToHexString(BoardLeds.Frame(new[] { BoardLeds.Vee, BoardLeds.Blink, BoardLeds.Full, BoardLeds.Off, BoardLeds.SoftVee })) == "FC84048C0F0F0F0FFFFFFFFF00000000C9630369", "LED_BRT_SEQ VEE/BLINK/FULL/OFF/SOFTV = app bytes");
        Check(Convert.ToHexString(BoardLeds.Frame(Enumerable.Repeat(BoardLeds.ClashPulse, 5).ToList()))[..8] == "66FF66FF", "CLASH pulse = app bytes 66 FF 66 FF");
        var vic = Convert.ToHexString(BoardLeds.Frame(Enumerable.Range(0, 5).Select(sl => BoardLeds.Victory(sl, 0)).ToList()));
        Check(vic.Replace('7', '8') == "8EE7118EE7118EE8118EE8118EE8118EE8118EE7".Replace('7', '8'), "VICTORY frame 0 = app nibbles (0.5 rounds to 7 or 8)");
        Check(Convert.ToHexString(BoardLeds.Frame(Enumerable.Range(0, 5).Select(sl => BoardLeds.Spinner(sl, 0)).ToList()))[..8] == "33333333", "SPINNER slot 0 frame 0 = all 0.2");
        // card signature (secp128r1): the app key is on the curve; sign/verify round trip with a throwaway key
        Check(CardSignature.OnCurve(CardSignature.PublicKey), "card public key lies on secp128r1");
        var tk = System.Numerics.BigInteger.Parse("1234567890ABCDEF1122334455", System.Globalization.NumberStyles.HexNumber);
        var tq = CardSignature.PublicFor(tk);
        var tsig = CardSignature.SignForTest(0x0123_0000_1122_3344UL, tk, 987654321);
        Check(CardSignature.OnCurve(tq) && CardSignature.VerifyCard(0x0123_0000_1122_3344UL, tsig, tq), "ECDSA sign/verify round trip");
        Check(!CardSignature.VerifyCard(0x0123_0000_1122_3345UL, tsig, tq), "ECDSA rejects another UID");
        Check(CardSignature.VerifyCard(0x0123_0000_1122_3344UL, new byte[] { 0x20 }.Concat(tsig).ToArray(), tq), "ECDSA finds r||s after a leading byte");
        Check(!CardSignature.VerifyCard(0x0123_0000_1122_3344UL, tsig), "ECDSA rejects a foreign key's signature");
        Check(sig[0] == 0xA6 && sig[1] == 0xFF && sig[2] == 0x88 && sig[3] == 0x99 && sig[7] == 0xDD,
            "A6 signature request carries low 48 bits big-endian");

        // single-frame reassembly: A3 with 15-byte payload
        var r1 = new BoardProtocol.FrameReassembler();
        var raw = new byte[18];
        raw[0] = 0x00; raw[1] = 0x10; raw[2] = 0xA3;           // len=16, no "more"
        for (int i = 0; i < 15; i++) raw[3 + i] = (byte)(i + 1);
        var f1 = r1.Feed(raw);
        Check(f1 is { } ff && ff.Opcode == 0xA3 && ff.Payload.Length == 15 && ff.Payload[0] == 1 && ff.Payload[14] == 15,
            "single-fragment frame parses opcode + 15-byte payload");

        // two-fragment reassembly: A5 carrying 6 payload bytes across 2 fragments
        var r2 = new BoardProtocol.FrameReassembler();
        var frag1 = new byte[] { 0x00, 0x80 | 0x04, 0xA5, 0x11, 0x22, 0x33 };  // more=1, len=4 => opcode + 3 bytes
        var frag2 = new byte[] { 0x00, 0x04, 0x44, 0x55, 0x66 };               // more=0, len=4 => 3 bytes (no opcode wait)
        var mid = r2.Feed(frag1);
        Check(mid == null && r2.NeedsContinue, "fragment 1 requests continue");
        var f2 = r2.Feed(frag2);
        Check(f2 is { } gg && gg.Opcode == 0xA5 && gg.Payload.Length == 6
              && gg.Payload[0] == 0x11 && gg.Payload[3] == 0x44 && gg.Payload[5] == 0x66,
            "two-fragment frame concatenates payload (6 bytes)");

        // poll parse: board slot 4 has 1 card, xor 0x1234
        var payload = new byte[15];
        payload[4] = 0x01;                  // count for board slot 4
        payload[5 + 4 * 2] = 0x12; payload[6 + 4 * 2] = 0x34;
        var sums = BoardProtocol.ParsePoll(payload);
        Check(sums[4].Count == 1 && sums[4].CtiXor == 0x1234 && !sums[4].ReadError, "ParsePoll reads count + BE xor");

        // CTI parse
        var ctis = BoardProtocol.ParseCtis(new byte[] { 0x00, 0x05, 0xAB, 0xCD });
        Check(ctis.Length == 2 && ctis[0].Value == 0x0005 && ctis[1].Value == 0xABCD, "ParseCtis (BE uint16 each)");

        // UID parse (A4 10-byte record)
        var rec = new byte[10]; rec[2] = 0x12; rec[3] = 0x34; rec[9] = 0xEF;
        var uids = BoardProtocol.ParseUids(rec);
        Check(uids.Length == 1 && (uids[0] >> 48) == 0x1234 && (uids[0] & 0xFF) == 0xEF, "ParseUids (UID BE at offset 2..9)");

        // slot tracker: add 0x1234, add 0x00FF, remove top
        var t = new SlotTracker();
        SlotSummary[] Poll(int boardSlot, int count, ushort xor)
        {
            var s = new SlotSummary[5];
            for (int i = 0; i < 5; i++) s[i] = new SlotSummary(0, 0, false);
            s[boardSlot] = new SlotSummary(count, xor, false);
            return s;
        }
        int bi = BoardProtocol.BoardIndex(Slot.Slot0);   // 4
        var e1 = t.Update(Poll(bi, 1, 0x1234), out _);
        Check(e1.Count == 1 && e1[0].Added && e1[0].Cti.Value == 0x1234 && e1[0].Slot == Slot.Slot0,
            "tracker: first card placed => add 0x1234");
        var e2 = t.Update(Poll(bi, 2, (ushort)(0x1234 ^ 0x00FF)), out _);
        Check(e2.Count == 1 && e2[0].Added && e2[0].Cti.Value == 0x00FF, "tracker: second card => add 0x00FF via XOR");
        var e3a = t.Update(Poll(bi, 1, 0x1234), out _);
        Check(e3a.Count == 0, "tracker: a lower count waits one poll (debounce)");
        var e3 = t.Update(Poll(bi, 1, 0x1234), out _);
        Check(e3.Count == 1 && !e3[0].Added && e3[0].Cti.Value == 0x00FF, "tracker: confirmed pop => remove 0x00FF");
        t.Update(Poll(bi, 0, 0), out _);
        var e4 = t.Update(Poll(bi, 0, 0), out _);
        Check(e4.Count == 1 && !e4[0].Added && e4[0].Cti.Value == 0x1234, "tracker: empty slot => remove 0x1234");
        // flicker: a one-poll drop that comes back is ignored
        t.Update(Poll(bi, 1, 0x1234), out _);
        var fl1 = t.Update(Poll(bi, 0, 0), out _); var fl2 = t.Update(Poll(bi, 1, 0x1234), out _);
        Check(fl1.Count == 0 && fl2.Count == 0, "tracker: one-poll flicker is ignored");
        // read error: no event, no resync
        var errPoll = Poll(bi, 0, 0); errPoll[bi] = new SlotSummary(0, 0, true);
        var er = t.Update(errPoll, out var needErr);
        Check(er.Count == 0 && needErr.Count == 0, "tracker: read error => no event, no resync");
        // seeding after a reconnect: same contents => no events
        var t2 = new SlotTracker(); t2.Seed(Slot.Slot0, new[] { new Cti(0x1234) });
        Check(t2.Update(Poll(bi, 1, 0x1234), out _).Count == 0, "tracker: seeded contents produce no events");
        t.Update(Poll(bi, 0, 0), out _); t.Update(Poll(bi, 0, 0), out _);
        // automatic read size
        Check(DropMixBoard.AutoReadSize(3, 2) == BoardProtocol.ReadSize.Tiny && DropMixBoard.AutoReadSize(6, 5) == BoardProtocol.ReadSize.Small
              && DropMixBoard.AutoReadSize(30, 8) == BoardProtocol.ReadSize.Medium && DropMixBoard.AutoReadSize(50, 12) == BoardProtocol.ReadSize.Large,
              "read size: Tiny/Small/Medium/Large by card counts");

        // ambiguous delta => needs full read, then ApplyFullRead diffs
        t.Update(Poll(bi, 3, 0x0999), out var need);
        Check(need.Contains(Slot.Slot0), "tracker: multi-card jump flags full read");
        var e5 = t.ApplyFullRead(Slot.Slot0, new[] { new Cti(1), new Cti(2), new Cti(3) });
        Check(e5.Count == 3 && e5.All(x => x.Added), "ApplyFullRead emits adds for new contents");

        // LED encoders
        var leds = BoardProtocol.EncodeImmediateLeds(new[] { new LedColor(1, 0, 0) });
        Check(leds.Length == 20 && leds[0] == 0x0F && leds[1] == 0x00, "IMD_LED: full red => 0F 00");
        var seq = BoardProtocol.EncodeBrightnessSeq(new[] { new[] { 1f, 0f, 1f, 0f, 1f, 0f, 1f, 0f } });
        Check(seq.Length == 20 && seq[0] == 0xF0 && seq[1] == 0xF0, "LED_BRT_SEQ: nibbles high-first");

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
