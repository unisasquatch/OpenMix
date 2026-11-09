# Where OpenMix's numbers come from

OpenMix follows one rule (see README, "What's included, and what isn't"):

- **Data the original game stored in its files** is read from the user's copy during setup and never kept in this repository. That covers scene and prefab settings, config assets, text tables, FMOD bank mixer settings and metadata array tables.
- **Behaviour the original app wrote into its program code** is re-implemented in OpenMix's code. Any numbers that behaviour uses count as rules, not data.

This page records the program-logic values that stay in code, and why each one is program logic and not data.

How each claim was checked, and what you can check yourself:
- **No serialized field holds the value.** You can check this: run an import with `--keep-work` and look in the work folder at
  `scene/` (every serialized scene object, dumped to JSON) and `il2cpp/out/dump.cs` (the game's type and field declarations,
  made from your own copy). A static or private unserialized field never appears in the scene dumps.
- **The value isn't a metadata array table either.** Array tables appear in `global-metadata.dat` as field default data,
  which is how OpenMix imports the score chimes and coin-flip curve. Ruling that out for a value means reading the
  original's compiled code, to see whether it stores literals or copies an array table. That inspection was done for every
  row below. It needs a disassembler on the user's own `libil2cpp.so`; neither the repository nor the import produces it.

| Value in OpenMix | Where it lives in OpenMix | Why it's program logic |
|---|---|---|
| Board battery thresholds (low, critical, force-disconnect, display floor; with and without cards) and the reading window (6 readings, 4 flagged) | `src/OpenMix.Host/BoardBattery.cs` | In the original these are `static readonly` integer fields of the battery monitor class. Its static constructor stores integer literals into them; it uses no array initializer data. Unity never serializes static fields. The battery monitor scene object serializes only two editor/debug fields, a fake battery level and a debug-display flag. |
| Board light brightness patterns (startup sweep, pulses, chase, wave) and the colour order of the startup and victory layouts | `src/OpenMix.Board/BoardLeds.cs`, `src/OpenMix.Host/BoardLights.cs` | In the original the board light controller allocates private, unserialized arrays in its constructor and fills them at run time from code: literal brightness levels, computed steps and the instrument colour fields of the game config. It uses no array initializer data. Its scene object serializes only the LED latency factor, and the slot-light objects serialize only their side and slot number. OpenMix imports those, along with the light colours from the game config. |
| The card types each slot accepts (Lead/Loop, Loop, Loop/Beat, Beat, Beat/Bass) | `src/OpenMix.Game/Rules.cs`, fallback copy in `wwwroot/board.js` | In the original these are `static readonly` arrays on the game-config class, one per slot (with single- and dual-colour variants). Its static constructor creates each array and stores enum literals element by element; it uses no array initializer data. The game-config scene object doesn't serialize them. |
| Clash wheel spin timing and decay | `src/OpenMix.Game/ClashMode.cs`, `wwwroot/orig.js` | These are literals in the wheel's update code. The wheel's slice values, weights and colours are serialized, and OpenMix imports them. |
| Finale tempo climb step (+1 BPM per bar) | `src/OpenMix.Audio/MixDirector.cs` | This is a literal in the original's game-over code. The tempo ceiling it climbs to is serialized, and OpenMix imports it. |
| Lead-part ducking (−9 dB, 2 s ramp) | `src/OpenMix.Audio/MixEngine.cs` | These are literals in code. The bar positions derive from the loop length, which is serialized and imported. The game's audio object also serializes four "card ducking" fields (ducked/unducked level, duck/unduck time). The original's code only sets them in its constructor and never reads them, so they have no effect in the original. OpenMix imports them along with the rest of that object but doesn't use them. |
| Puzzle grid length (8 cells), hard-mode pulses, Wild spawn weight, spin, reveal and shuffle delays | `src/OpenMix.Game/LanesMode.cs` | These are compiled constants or literals. No serialized field holds them. Everything the Puzzle scene objects serialize is imported into `lanes_config.json`. |
| A sampler patch's default voice limit when the patch doesn't set one | `src/OpenMix.Audio/FusionPatch.cs` | This is a per-patch default in the patch loader. The shared voice pool limit is serialized and imported. |
| Score-meter range (VU), light floor | `src/OpenMix.Host/VuMeters.cs`, `BoardLights.cs` | These are literals in the meter and light code. |

**OpenMix's own design and calibration.** These were never the original's data:
- the UI layout and colours that don't match a serialized colour;
- the duplicated 320 Hz EQ stage in the mix bus, which was measured against the real app;
- **MixGrid's settings** (`src/OpenMix.Game/MixGridMode.cs`). MixGrid was an unreleased test mode, and the game files don't
  include its scene object, its level list, its request and hand tuning tables, or its tile-property prefabs. OpenMix chose
  all of these itself:
  - the level order (built from the level files' own numbering);
  - hand size, deck size, playlists and minimum path length;
  - explosions off;
  - the random request weights and points;
  - hand weights;
  - what each tile property does;
  - how "NX POINTS FOR …" level descriptions become point multipliers;
  - stars for single-round levels;
  - the reveal, goal and reshuffle delays;
  - the End level button.
  
  The only MixGrid content from the original is the level files, which are imported. The 7×7 grid size matches those files.
