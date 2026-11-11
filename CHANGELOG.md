# Changelog

## 0.1.1

### Gameplay

- **Board-player join.** In Clash, Party and Puzzle you can now join from the
  physical board alone — place a deck on a lobby slot to take a seat (no phone
  needed), and lift the deck to leave or switch sides. Phone players still show
  the name they typed; board players stay anonymous, as in the original game.
- **MixGrid is now a timed match game.** Drop a card onto a colour-matching pad
  and it clears every matching request across the whole grid (its colour, its
  power, its instruments; wildcards clear anything), then the grid refills. You
  hold two cards and have seven seconds per drop — run out of time or cards and
  you lose; reach the level's score goal to win. Filling all five pads once per
  game clears a whole request type for a bonus. The physical board drives it,
  and there's a pause button to leave a level.

### Collection

- **Preview a card's sound** from its detail view, played through the mix engine
  at the card's own tempo and key.
- **Disable a card** so it is never shuffled into the virtual deck — it stays in
  your collection and still plays from the physical board.
- The virtual deck now draws only cards you have scanned in or imported audio
  for; everything else stays owned and board-playable.

### Display

- **Fullscreen** (F11, or Settings → Fullscreen).
- The collection, settings and mixes screens use the full width on ultrawide
  (21:9 / 32:9) monitors; the game board stays 16:9.
- The phone's slot VU meters now animate with the music.
- Fixed the Add-to-Collection "get my cards" button falling off the bottom.

### Setup & audio

- Import DropMix straight from an Android phone over USB (adb) from the setup
  screen, with live phone detection.
- Added a Getting Started guide for placing your own song stems.
- Plays a `.wav` stem where OpenMix looks for the `.ogg`.
- Loads a card's imported samples from either `audio_unpacked` folder layout.

## 0.1.0

- First release. OpenMix is an asset-free Windows port of DropMix: it rebuilds
  the game data from your own APK/OBB and scanned cards, and plays your own
  song stems.
