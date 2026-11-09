# The physical DropMix board (2026-11-05)

Verified on hardware: board "DropMix M00608", firmware `C3410_CSR_GTR_PR03`, Windows 11 with an
Intel Bluetooth adapter.

## Running
```
dotnet run --project src/OpenMix.Host -- --board            # keep scanning, connect, reconnect on drop
dotnet run --project src/OpenMix.Host -- --board-trace      # same, with raw BLE traffic logged
dotnet run --project src/OpenMix.Board.TestConsole -- --scan            # list BLE advertisers (board flagged)
dotnet run --project src/OpenMix.Board.TestConsole -- <content>/data/cards.csv --trace   # print card events
```
- The board takes one connection at a time. **Close DropMix on the phone** before the PC can see it
  (a connected board stops advertising).
- Pairing is done from code (`DropMixBoard.EnsurePairedAsync`, Just Works). Pairing it to the PC
  removed the phone's bond once; the phone app re-pairs by itself.
- `/api/info` → `boardStatus` {state: scanning | searching | connected | lost, name, firmware, battery}.
- If the link drops, the host lifts every card the board had reported, so the reconnect re-reads them
  without placing them twice.

## Protocol corrections found on hardware
- DATA_TX (0003), OP_STS (0004) and BTN_STS (0005) are **Indicate**, not Notify; the driver picks
  whichever the characteristic offers. REQ_OP/IMD_LED/LED_BRT_SEQ/LED_TIME are Write (with response).
- A3 poll reply seen: `81 10 A3` + 5 counts + 5 BE CTI-XORs (byte 0 = 0x81). Card events, slot
  mapping (board slot i = game slot 4−i) and the EQ button (`BTN_STS 01`) all matched the notes.
- The phone app requests high connection priority; on Windows the driver asks for
  `BluetoothLEPreferredConnectionParameters.ThroughputOptimized`. Without it, 20-byte light writes took
  65–193 ms and the lights visibly lagged; with it, 20–31 ms. Slot polling runs at ≈ the app's 0.53 s.

## Robustness (2026-11-05, from the audit)
- **Link loss pauses, doesn't clear**: the table keeps its cards; a running game pauses; the next connection seeds the
  slot tracker with the last known stacks so only real changes register; the game resumes on reconnect.
- **Remove cards to start**: Clash/Party/Puzzle refuse to start while cards are on the board ("REMOVE CARDS TO
  START", RemoveAll lights — all red, blinking) — also stops a rematch from leaving untracked cards behind.
- **Ignore the board** outside play: placements in setup/lobby phases are dropped (lifts still apply); polling is
  suspended during the wheel, coin flip and Clash start and resumes afterwards (cards placed then are read after).
- **Read size** follows the stacks (kAutomatic): Tiny < 10 cards & stacks < 4; Small stacks 4–6 or 10–24 cards;
  Medium < 45 & stacks < 10; else Large.
- **Debounce**: a lower count must repeat on the next poll before it's a lift (one-poll flicker ignored); a read
  error (count bit 7) produces no event and no resync.

## Lights (`src/OpenMix.Board/BoardLeds.cs`, `src/OpenMix.Host/BoardLights.cs`)
Observed on the phone (logcat, `UnityBluetoothLE`): LED_TIME written once after connect, IMD_LED only a
handful of times, LED_BRT_SEQ continuously every ~0.83 s (684 writes in ~10 min).
- **IMD_LED** (0006): LED k = `[flag<<4 | R4][G4<<4 | B4]`, channels floor(x·15), flag 1 = set (black
  = `10 00`). LEDs 0–4 = Blue side of game slots 0–4, 5–9 = Red side; no slot reversal for lights.
  Instrument colours (lead, loop, beat, bass, fx, the three wild pairs, collection-owned) are ClashConfig's
  `*LEDColor` / `collectionOwnedColor` fields, imported at setup into `data/game_tables.json`; white `1F FF`, red `1F 00`.
  Startup: Blue lead, loop, loop, beat, beat / Red loop, loop, beat, beat, bass.
  Normal (Freestyle studioMode): card → its instrument colour on both sides, empty → black; any slot
  silenced → silenced red, others black. Re-sent only on change, ≤ every 0.5 s.
- **LED_BRT_SEQ** (0007): every 0.8 s, 5 slots × 8 nibbles (floor(v·16) clamped). Startup sweep
  `FFEEDDCB`, `BBCDDEEF`, `ECA86420`. Normal: empty off; card not yet sounding full; silenced blink
  (`0F0F0F0F`); transition V (`FC84048C`); sounding → `Lerp(0.07, 1, meter·1.5)` sampled at
  timeline + (−1.5·(audioLatency − 0.1) − 0.25) s across the next 0.8 s. Nothing sent while all slots are empty.
- **Meters**: precomputed per card, OBB TextAssets (`Resource/device/obb_text`, copied to
  `assets_raw/meters`, local only): SourceCID name (`HMX_0001_Bass`), Wilds per slot
  (`LIC_0075_Wild_<slot>`); 64 values per bar.
- Live tuning: `POST /api/board/lights {"offset": s}` shifts the lights (+ = earlier). Default 0 is
  in time with the music on this PC.

## Clash light scenes (`ClashMode.Lights` → `LightCue`, rendered in `BoardLights`)
LEDs a scene doesn't set stay unset (`00 00`). Normal mode in Clash lights a slot only on its
**owner's** side (studioMode is off in games); Party lights both sides; Lanes only the Red side.
| scene | when | colours | brightness |
|---|---|---|---|
| Spinner (coin flip) | first 2.4 s of a game (the table's FIRST UP spin) | both sides: red where vulnerable, else white | SPINNER chase, 2 frames |
| ClashStart | next 4 s (5 frames) | first player's side white | CLASH `66 FF 66 FF` |
| Spinner (attack) | Wheel of Misfortune spinning | target side red where its cards sit, else white; attacker side black | SPINNER |
| TurnChange | 2.4 s (3 frames) after a turn ends | incoming player's side white | VEE |
| Victory | game over | winner side lead, loop, fx, beat, bass; loser black | VICTORY wave, 3 frames |
Not ported: deck/collection registration modes, RemoveAllCards popup, the error-slot blink (Lanes).
