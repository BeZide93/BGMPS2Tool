# BGMPS2Tool

Version: `v0.4.0`

`BGMPS2Tool` is a small Windows tool package for replacing `Kingdom Hearts II Final Mix` PS2 music tracks.

It converts a custom `.wav` into a new PS2-compatible:

- `.bgm`
- `.wd`

pair for the matching track slot.

This package is focused on the practical workflow:

`custom WAV -> rebuilt PS2 musicXXX.bgm + waveXXXX.wd`

Music Tracks Locations: https://docs.google.com/spreadsheets/d/1JMAhUSeEf3r-njF2-8EBX8mUDVa0xaLs/edit#gid=1851343023

How to Input Loops:
Full Loop:

WAV must have LoopStart and LoopEnd tags
LoopStart must be equal to 0
LoopEnd must be equal to the last sample of the WAV.
Custom Loop:

WAV must have LoopStart and LoopEnd tags
LoopEnd must be equal to the last sample of the WAV.
No Loop:

WAV must not have LoopStart and LoopEnd tags.

## Included Files

- `BGMInfo.exe`
- `BGMInfo.dll`
- `BGMInfo.deps.json`
- `BGMInfo.runtimeconfig.json`
- `KhPs2Audio.Shared.dll`
- `KhPs2Audio.Shared.deps.json`
- `BGMReplaceWav.bat`
- `README.md`
- `HOWTO.md`
- `CHANGELOG.md`
- `VERSION.txt`

## Dependencies

This package is almost self-contained.

Required:

- Windows
- Microsoft `.NET 10` Runtime

Not required:

- `ffmpeg`
- `SCDInfo`
- `MultiEncoder`
- `SingleEncoder`
- any extra DLLs beyond the files included in this folder

Important:

- Keep all files from `BGMPS2Tool` together in the same folder.
- If `BGMInfo.exe` does not start, the most likely cause is a missing `.NET 10` Runtime.

## What The Tool Does

When you give the tool a WAV like `music188.ps2.wav`, it will:

1. Find `music188.bgm` in the same folder
2. Find the matching `wave0188.wd` in the same folder
3. Build a new replacement pair
4. Write the result into an `output` folder next to the WAV

## Important Notes

- This tool is made for PS2 `KH2FM` music replacement.
- The input WAV must be a `16-bit PCM WAV`.
- The WAV must be placed next to the original matching `musicXXX.bgm` and `waveXXXX.wd`.
- The new files are written to `output`, so the original files stay untouched.
- For compatibility, the tool keeps the original PS2 container structure as much as possible.
- If the original PS2 `WD` budget is small, the tool may reduce the replacement audio quality to fit the original track size.
- Long replacement songs may be downmixed to mono to stay within the original PS2 memory budget.
- If the WAV contains loop metadata, the tool can import that loop for the rebuilt PS2 track.

## Supported Loop Metadata

The tool supports loop points when the WAV contains:

- `LoopStart`
- `LoopEnd`

This is currently supported from:

- RIFF `smpl` loop data
- WAV `id3` metadata with `TXXX` tags named `LoopStart` and `LoopEnd`

The loop values must be stored as sample positions.

## Quick Start

Use the drag-and-drop batch:

`BGMReplaceWav.bat`

or use the command line:

```powershell
.\BGMInfo.exe replacewav "C:\Path\To\music188.ps2.wav"
```

## Output

For an input like:

- `music188.ps2.wav`
- `music188.bgm`
- `wave0188.wd`

the tool will create:

- `output\music188.bgm`
- `output\wave0188.wd`

## Recommended Workflow

1. Keep one folder per track.
2. Put the original `musicXXX.bgm` and `waveXXXX.wd` into that folder.
3. Put your replacement WAV in the same folder.
4. Drag the WAV onto `BGMReplaceWav.bat`.
5. Test the files from the `output` folder ingame.

## Release Notes

See:

- `CHANGELOG.md`
