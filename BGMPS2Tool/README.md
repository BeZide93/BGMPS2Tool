# BGMPS2Tool

Version: `v0.6.10`

`BGMPS2Tool` is a Windows tool package for rebuilding `Kingdom Hearts II Final Mix` PS2 music tracks.

It currently supports two workflows:

- `WAV -> rebuilt PS2 musicXXX.bgm + waveXXXX.wd`
- `MIDI + SF2 -> rebuilt PS2 musicXXX.bgm + waveXXXX.wd`

The new MIDI/SF2 workflow is cleaner than the legacy long-note workaround because it authors:

- a real PS2 `WD` instrument bank from the SoundFont
- a real PS2 `BGM` sequence from the MIDI

For MIDI rebuilds, the tool now keeps the original PS2 track-slot layout and only expands track slots by a limited, conservative amount when that is needed to fit a valid sequence safely.

## Included Files

- `BGMInfo.exe`
- `BGMInfo.dll`
- `BGMInfo.deps.json`
- `BGMInfo.runtimeconfig.json`
- `KhPs2Audio.Shared.dll`
- `KhPs2Audio.Shared.deps.json`
- `BGMReplaceWav.bat`
- `BGMReplaceMidiSf2.bat`
- `config.ini`
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

## Configuration

The tool reads:

- `config.ini`

from the same folder as `BGMInfo.exe`.

Supported options:

- `volume=...`
- `hold_minutes=...`

Notes:

- `volume` applies to imported WAVs and to SoundFont sample audio before PS2 encoding.
- `hold_minutes` is mainly relevant to the older `replacewav` loop workflow.
- `hold_minutes` does not drive the actual note lengths in the MIDI/SF2 workflow, because those come from the MIDI sequence itself.

## What The Tool Does

### WAV workflow

When you give the tool a WAV like `music188.ps2.wav`, it will:

1. Find `music188.bgm` in the same folder
2. Find the matching `wave0188.wd` in the same folder
3. Build a new replacement pair
4. Write the result into an `output` folder next to the WAV

### MIDI + SF2 workflow

When you give the tool a MIDI like `music188.mid`, it will:

1. Find `music188.bgm` in the same folder
2. Find the matching `wave0188.wd` in the same folder
3. Find `wave0188.sf2` in the same folder
4. Convert the SoundFont into a new PS2 `WD` bank
5. Convert the MIDI into a new PS2 `BGM` sequence
6. Write the result into an `output` folder next to the MIDI

If no usable `.sf2` is found, the tool can fall back to the original `waveXXXX.wd` and use its existing PS2 instruments directly.

## Important Notes

- This tool is made for PS2 `KH2FM` music replacement.
- The WAV workflow still expects a `16-bit PCM WAV`.
- The MIDI/SF2 workflow expects a standard `.mid` and `.sf2`.
- The original matching `musicXXX.bgm` and `waveXXXX.wd` must still be present next to the inputs.
- The new files are written to `output`, so the original files stay untouched.
- For compatibility, the tool keeps the original PS2 header/container identity where practical, but the authored sequence/bank data is rebuilt.
- The current SoundFont importer ignores some advanced SF2 features such as filter/LFO/modulator behavior.
- The current MIDI importer ignores pitch-bend because the KH2 PS2 pitch opcode mapping is still unknown.
- If a MIDI does not match the available `.sf2`, the tool will now try to use the original `waveXXXX.wd` as the program source before failing.
- The MIDI path now keeps the original PS2 `BGM` track-slot layout for safety. Very dense MIDIs may be rejected if they cannot fit into the original `BGM` container without creating crash-prone output.

## Quick Start

WAV workflow:

- `BGMReplaceWav.bat`
- `BGMInfo.exe replacewav "C:\Path\To\music188.ps2.wav"`

MIDI/SF2 workflow:

- `BGMReplaceMidiSf2.bat`
- `BGMInfo.exe replacemidi "C:\Path\To\music188.mid"`
- `BGMInfo.exe replacemidi "C:\Path\To\music188.mid" "C:\Path\To\wave0188.sf2"`

## Output

For an input like:

- `music188.mid`
- `music188.bgm`
- `wave0188.wd`
- `wave0188.sf2`

the tool will create:

- `output\music188.bgm`
- `output\wave0188.wd`
- `output\music188.mid-sf2-manifest.json`

## Release Notes

See:

- `CHANGELOG.md`
