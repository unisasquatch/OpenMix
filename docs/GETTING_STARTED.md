# Getting Started: Add Your Own Song Stems

Out of the box, OpenMix plays each card's MIDI parts through the game's instruments, plus
voice lines and sound effects. It does **not** play DropMix's recorded song stems: those ship
encrypted, and OpenMix can't read them and includes no way to decrypt them.

If you have plain `.ogg` or `.wav` stems of your own, OpenMix will play them in place of the
encrypted ones. This guide shows where to put them so OpenMix picks them up.

> You supply the stems. OpenMix doesn't provide them, download them, or decrypt the game's.
> Use audio you made or otherwise have the right to use.

## Before you start

1. Finish setup, and download your cards' content: **Settings → Card Content → Download My
   Collection**. That creates a folder for each owned song, with the patch files that list the
   stems the song expects. Until you do this, there's nowhere to put stems yet.
2. Have your stem files ready — one audio file per part of a song.

## Where things live

The content root is:

```
%LOCALAPPDATA%\OpenMix\content
```

Paste that into the address bar of File Explorer to open it. Each song sits under:

```
content\audio_unpacked\<SongId>\<SongId>\
    patches\   .fusion text files that name the stems the song wants
    samples\   where the audio stems go  (you add files here)
    midi\      the note data             (leave this alone)
```

`<SongId>` is the game's internal name for the song, for example `1977_LOOP`. These are codes,
not song titles, so you may need to open a patch (next step) to tell which song a folder is.

## Find the stem names a song wants

Open any `.fusion` file in the song's `patches\` folder with Notepad — they're plain text.
Look for lines like:

```
(sample_path "../samples/1977_LOOP_D_104(120).mogg")
```

Each `sample_path` line names one stem, written as a `.mogg` in the `samples\` folder. OpenMix
never reads that `.mogg`. Instead it looks in the same folder for a file of the **same name**
ending in `.ogg` — or, if there's no `.ogg`, `.wav`.

So for the line above, OpenMix will play:

```
samples\1977_LOOP_D_104(120).ogg      (or .wav)
```

A song can have more than one `.fusion` file, and each can name several stems. Gather every
`sample_path` across all the patches in that song's `patches\` folder — those are the stems the
song can use.

## Place your stems

1. Open the song's `samples\` folder. If it isn't there, create a folder named exactly
   `samples` next to `patches` and `midi`.
2. Copy your stem into `samples\`, and rename it to match the name from the `sample_path`
   line **exactly** — same spelling, same capitalization, every character inside the
   parentheses — but with a `.ogg` or `.wav` extension instead of `.mogg`:

   ```
   1977_LOOP_D_104(120).mogg   ->   1977_LOOP_D_104(120).ogg
   ```

3. Repeat for each stem the patches name. You don't have to supply them all — OpenMix plays
   the ones it finds and simply stays silent for the parts it can't.

If you have a stem in both formats, prefer `.ogg`: OpenMix checks for `.ogg` first, then `.wav`.

## Tips for stems that fit the mix

- **Format:** standard Ogg Vorbis (`.ogg`) or PCM WAV (`.wav`), mono or stereo.
- **One part per file.** A stem is a single part of the song (a loop, a lead, a drum line),
  not a full mixed track. Each `sample_path` is one part.
- **Pitch and tempo.** OpenMix time-stretches each stem to the current mix tempo using the
  tempo stored in the patch, and may pitch it to match the card. For a stem to sit in time
  with everything else, it should be the same phrase, at the tempo the patch expects. Any
  audio will still play — it just may not line up if it's a different part or tempo.
- **Names must match exactly.** A mismatched name (a missing space, different case, dropped
  parenthesis) means OpenMix won't find the file and that part stays silent.

## Check it worked

1. Restart OpenMix, or re-open the song, so it re-reads the patches.
2. Play the card. You should now hear your stem where before there was only the MIDI
   instruments and effects.
3. If you hear nothing new: re-check the filename against the `sample_path` line character for
   character, confirm the file is really inside `samples\`, and confirm the extension is
   `.ogg` or `.wav`.

## Good to know

- Your stems are files OpenMix only *reads* at play time; they change nothing the game shipped.
- Re-downloading your collection rewrites the patch and MIDI files but skips the encrypted
  samples and never deletes files it didn't download, so your own `.ogg`/`.wav` stems stay
  put. (A stem is only overwritten if you later download one with the identical name, which
  won't happen for your own files.)
- To remove a stem, just delete the `.ogg`/`.wav` you added. OpenMix falls back to playing the
  card without it.
