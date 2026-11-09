# OpenMix

An unofficial, fan-made Windows player for **DropMix** boards and cards.

DropMix's app is gone from the stores, but the boards and cards still work. OpenMix lets you keep playing on a Windows PC:

- It connects to your DropMix board over Bluetooth.
- It plays the mix on the PC's speakers.
- It turns phones on the same Wi-Fi into controllers.

> OpenMix is not affiliated with, endorsed by, or connected to Hasbro or Harmonix. "DropMix" is a trademark of
> its owners and is used here only to say what OpenMix works with.

## What's included, and what isn't

The repository and installer contain OpenMix's code and **none of the game's art, music, sounds or text, and none of
the settings or data tables the game kept in its data files**. That covers scene and prefab settings, config assets,
level and round tables, and metadata tables. All of these are read from your copy during setup. To find them, the code
contains identifiers: asset names and ID numbers, string-table indexes, scene object paths, and the names of the
game's settings fields (see "Projects" below).

How the game plays is re-implemented as OpenMix's own code: the rules, scoring, timing, board protocol and light
animations. Where the original app had a behaviour written into its program rather than stored as data, OpenMix's
code reproduces that behaviour, including the numbers it uses. Those numbers are part of the rules, not data files
from the game. `docs/PROVENANCE.md` lists each one, why it counts as program logic, and OpenMix's own design choices, such as MixGrid's settings.

OpenMix builds its content from **your own copy** of DropMix version **1.9.0**, with its `main.21` expansion file (OBB):

1. **Setup: your app and expansion file.** OpenMix copies the `.apk` and `.obb` from your Android phone over USB
   (adb), or you pick files you already have. It extracts the menus, sounds, voice lines, text in 13 languages,
   fonts, and each mode's settings from them, using AssetStudio and Il2CppDumper (both bundled).
2. **Your board and cards.** OpenMix opens on Add to Collection until you add at least one of your cards. The
   board checks each card's signature first, and OpenMix keeps that signature as proof you own the card.
3. **Card content, when you choose.** As in the original app, nothing downloads until you pick
   **Settings → Card Content → Download My Collection**. OpenMix first asks the game's card server for the download size
   and shows it to you. Only after you confirm does it download art and music for the cards in your collection, one
   file at a time. Both requests send the server the ID numbers of your cards, as the original app did. Only this PC can
   start or delete downloads, not phones on your network. You can delete the downloaded content there too.

Where your data goes:
- **Your PC:** everything OpenMix builds or downloads is stored in `%LOCALAPPDATA%\OpenMix`.
- **Your Wi-Fi:** phones on your network can open the game page to play.
- **The internet:** OpenMix contacts nothing except the card server, and only after you pick Download My Collection.

Ownership checks are part of normal use. They are not tamper-proof copy protection. A card's signature covers its
serial number but not which card it is; that's how DropMix cards were designed.

### Song audio

DropMix's song stems are encrypted. **OpenMix does not decrypt them and does not include any way to.** It plays
the parts it can read: MIDI, sampler instruments, voice lines and sound effects. It also plays standard
`.ogg`/`.wav` stems when they're present in a song's folder.

## Requirements

- Windows 10 (1903) or 11, 64-bit, with Bluetooth LE
- Microsoft Edge WebView2 (already installed on Windows 11)
- A DropMix board and cards, and the DropMix 1.9.0 app (with its OBB) on an Android phone or as files

## Install

Download `OpenMix-Setup-<version>.exe` from this repository's Releases page and run it. It installs for your
Windows account only and doesn't need administrator rights. The optional firewall rule, which lets phones join,
asks for approval.

The installer isn't code-signed, so Windows SmartScreen may say it's from an unknown publisher. Choose
**More info → Run anyway** if you downloaded it from this repository's Releases page.

## Build

```bash
dotnet build OpenMix.sln -c Release
dotnet run -c Release --project src/OpenMix.Board.TestConsole -- --selftest
```

Import from files without starting the game:

```bash
OpenMix --import --apk game.apk --obb game.obb --assetstudio <AssetStudioModCLI.exe> --il2cppdumper <Il2CppDumper.exe> [--content <dir>]
```

Build the installer. This needs Inno Setup 6 plus the exact releases of [AssetStudioModCLI](https://github.com/aelurum/AssetStudio/releases)
0.20.0 and [Il2CppDumper](https://github.com/Perfare/Il2CppDumper/releases) 6.7.46; the script checks their hashes.

```bash
powershell -ExecutionPolicy Bypass -File installer/build.ps1 -AssetStudio <folder> -Il2CppDumper <folder>
```

Debug builds also accept `--board-sim` (a simulated board) and `--no-gate`, for development. Release builds don't.

## Projects

| Project | What it is |
|---|---|
| `OpenMix.Board` | Bluetooth LE driver and protocol for the DropMix board (see `docs/BOARD.md`) |
| `OpenMix.Cards` | Card database and deck catalogue |
| `OpenMix.Game` | The game modes and rules |
| `OpenMix.Audio` | The mix engine: MIDI, sampler, time-stretch, effects |
| `OpenMix.Importer` | Builds the game content from the user's own APK and OBB, and downloads card content on request |
| `OpenMix.Host` | The app: window, phone web app, board link, first-run setup |

`src/OpenMix.Importer/Resources` holds two manifests and nothing else:
- `content_manifest.tsv` lists, for each image or text file OpenMix uses, which asset in the game's files holds it (file name and asset ID).
- `loc_manifest.tsv` lists, for each translated text, its index in the game's string table.

The content itself is read from your copy during setup.

MixGrid was an unreleased test mode in the game, and its level order isn't in the game files. OpenMix lists its
levels by the numbers in their own names.

## License

GPL-3.0. See `LICENSE`. Third-party components and their licences are listed in `THIRD_PARTY.md`.
