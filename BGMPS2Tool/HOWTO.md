# HOWTO

Version: `v0.6.8`

## Goal

Replace a PS2 `KH2FM` music track either:

- with a custom WAV
- or with a proper MIDI + SoundFont pair

## Requirements

- Windows
- Microsoft `.NET 10` Runtime

No other external tools are required for this package.

## Optional Configuration

Edit:

```text
config.ini
```

inside the `BGMPS2Tool` folder.

Available settings:

```ini
volume=1.0
hold_minutes=60
```

Meaning:

- `volume`: loudness multiplier for imported WAVs and SoundFont sample audio
- `hold_minutes`: minimum note hold time for looped `replacewav` builds

Notes:

- `hold_minutes` mainly affects the older WAV replacement path
- MIDI/SF2 note lengths come from the MIDI itself
- allowed `hold_minutes` range: `0.1` to `600`

## Method 1: WAV Replacement

Example input files in one folder:

```text
music188.bgm
wave0188.wd
music188.ps2.wav
```

Then:

1. drag `music188.ps2.wav` onto `BGMReplaceWav.bat`
2. or run:

```powershell
.\BGMInfo.exe replacewav "C:\Path\To\music188.ps2.wav"
```

Output:

```text
output\music188.bgm
output\wave0188.wd
```

## Method 2: MIDI + SF2 Replacement

Example input files in one folder:

```text
music188.bgm
wave0188.wd
music188.mid
wave0188.sf2
```

Then:

1. drag `music188.mid` onto `BGMReplaceMidiSf2.bat`
2. or run:

```powershell
.\BGMInfo.exe replacemidi "C:\Path\To\music188.mid"
```

If the SoundFont is not next to the MIDI under the expected `waveXXXX.sf2` name, use:

```powershell
.\BGMInfo.exe replacemidi "C:\Path\To\music188.mid" "C:\Path\To\wave0188.sf2"
```

If no usable `.sf2` is found, the tool automatically falls back to the original `waveXXXX.wd` and uses the PS2 bank directly.

Output:

```text
output\music188.bgm
output\wave0188.wd
output\music188.mid-sf2-manifest.json
```

## Naming Rules

Recommended names:

- `music188.ps2.wav`
- `music188.mid`
- `wave0188.sf2`

The tool uses the input name to find the matching `musicXXX.bgm` and `waveXXXX.wd`.

## Input Rules

### WAV path

Your WAV should be:

- `16-bit PCM`
- a standard `.wav` file

### MIDI/SF2 path

Your files should be:

- a standard `.mid`
- a standard `.sf2`

## Compatibility Notes

- The MIDI/SF2 workflow is more structured than the long-note WAV workaround.
- The current SoundFont importer converts presets, regions, key ranges, tuning, volume, pan, and loops.
- Advanced SF2 features such as modulators, filters, and LFO behavior are currently ignored.
- MIDI pitch-bend is currently ignored.
- Always test rebuilt files ingame after conversion.

## Troubleshooting

### `BGMInfo.exe` does not start

Install the Microsoft `.NET 10` Runtime and keep all package files together in the same `BGMPS2Tool` folder.

### "No matching .bgm was found next to the MIDI/WAV"

Make sure the input file is in the same folder as the correct `musicXXX.bgm`.

### "No matching .sf2 was found"

The tool now tries to fall back to the original `waveXXXX.wd` automatically.

If you want a newly authored bank instead of the original PS2 bank, place `waveXXXX.sf2` next to the MIDI, or call `replacemidi` with the SoundFont path explicitly.

### The result sounds different from the original SF2 playback

That can happen because KH2 PS2 `WD` is simpler than full SoundFont behavior. The current converter focuses on practical ingame compatibility first.
