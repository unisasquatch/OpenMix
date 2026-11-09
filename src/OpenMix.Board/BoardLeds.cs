namespace OpenMix.Board;

/// <summary>The board-light protocol (see docs/BOARD.md): the IMD_LED colour encoding and the brightness patterns the
/// original app streams on LED_BRT_SEQ. The instrument colours themselves are the original's configuration and are
/// imported at setup (GameTables.BoardLights); only plain white, red and black live here.
///
/// IMD_LED: LED k (0..9) = 2 bytes, <c>[flag&lt;&lt;4 | R4][G4&lt;&lt;4 | B4]</c> with each channel
/// floor(x·15); flag = 1 once the app has set that entry (so black is <c>10 00</c>, unset <c>00 00</c>).
/// LEDs 0..4 are the Blue side of game slots 0..4, LEDs 5..9 the Red side (no slot reversal for lights).</summary>
public static class BoardLeds
{
    public static readonly (byte, byte) White = FromColour(1, 1, 1), Red = FromColour(1, 0, 0), Black = FromColour(0, 0, 0), Unset = (0x00, 0x00);

    /// <summary>A set IMD_LED entry for an RGB colour (channels 0..1).</summary>
    public static (byte, byte) FromColour(double r, double g, double b)
    {
        static int N(double x) => Math.Clamp((int)Math.Floor(x * 15), 0, 15);
        return ((byte)(0x10 | N(r)), (byte)(N(g) << 4 | N(b)));
    }

    public static byte[] Encode(IReadOnlyList<(byte, byte)> leds10)
    {
        var b = new byte[20];
        for (int k = 0; k < 10 && k < leds10.Count; k++) { b[2 * k] = leds10[k].Item1; b[2 * k + 1] = leds10[k].Item2; }
        return b;
    }

    // ---- LED_BRT_SEQ patterns: 8 steps per slot (one frame ≈ 0.8 s at LED_TIME 0x14) ----
    public static float[] Const(float v) => Enumerable.Repeat(v, 8).ToArray();
    public static readonly float[] Full = Const(1), Off = Const(0);
    public static readonly float[] Blink = Enumerable.Range(0, 8).Select(i => (float)(i % 2)).ToArray();
    public static readonly float[] Vee = Enumerable.Range(0, 8).Select(i => MathF.Abs(i / 4f - 1)).ToArray();
    public static readonly float[] SoftVee = Enumerable.Range(0, 8).Select(i => MathF.Abs(i / 4f - 1) * 0.8f).ToArray();

    /// <summary>CLASH (ClashStart): 0.4, 0.4, 1, 1, ... = 66 FF 66 FF.</summary>
    public static readonly float[] ClashPulse = Enumerable.Range(0, 8).Select(i => (i / 2) % 2 == 1 ? 1f : 0.4f).ToArray();

    /// <summary>SPINNER, 2 frames per slot: 1.0 when (i>>1) == s-1 or 7-s, else 0.2 (i = 0..15 across the frames).</summary>
    public static float[] Spinner(int slot, int frame) =>
        Enumerable.Range(frame * 8, 8).Select(i => (i >> 1) == slot - 1 || (i >> 1) == 7 - slot ? 1f : 0.2f).ToArray();

    /// <summary>VICTORY, 3 frames per slot: 0.5 + 0.5 sin(2 pi (i + 2s) / 6), i = 0..23 across the frames.</summary>
    public static float[] Victory(int slot, int frame) =>
        Enumerable.Range(frame * 8, 8).Select(i => (float)(0.5 + 0.5 * Math.Sin(2 * Math.PI * (i + 2 * slot) / 6))).ToArray();

    /// <summary>The 3-frame startup sweep, identical on every slot (pre-encoded nibbles per slot).</summary>
    public static readonly byte[][] StartupFrames =
    {
        new byte[] { 0xFF, 0xEE, 0xDD, 0xCB }, new byte[] { 0xBB, 0xCD, 0xDE, 0xEF }, new byte[] { 0xEC, 0xA8, 0x64, 0x20 },
    };

    public static byte[] Frame(IReadOnlyList<float[]> perSlot8) => BoardProtocol.EncodeBrightnessSeq(perSlot8.ToArray());

    public static byte[] Frame(byte[] sameForEverySlot4)
    {
        var b = new byte[20];
        for (int s = 0; s < 5; s++) Array.Copy(sameForEverySlot4, 0, b, s * 4, 4);
        return b;
    }
}
