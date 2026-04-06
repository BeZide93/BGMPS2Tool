# HOWTO

Version: `v0.6.52`

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
sf2_volume=1.0
midi_loop=0
hold_minutes=60
pre_eq=0.0
pre_lowpass_hz=0
```

Meaning:

- `volume`: loudness multiplier for imported WAVs
- `sf2_volume`: loudness multiplier for MIDI + SF2 conversion
- `midi_loop`: loops the authored MIDI/BGM sequence when set to `1`
- `hold_minutes`: minimum note hold time for looped `replacewav` builds
- `pre_eq`: optional tone shaping before PS2 encoding for the WAV workflow
- `pre_lowpass_hz`: optional extra low-pass cutoff before PS2 encoding for the WAV workflow

Notes:

- `hold_minutes` mainly affects the older WAV replacement path
- `sf2_volume=1.0` is recommended if you want the closest possible `SF2 -> WD -> SF2` roundtrip fidelity
- `midi_loop=1` is useful when you want the rebuilt PS2 `BGM` to loop ingame instead of behaving like a one-shot sequence
- if the MIDI contains explicit loop markers, `midi_loop=1` now prefers those markers
- supported explicit markers include text markers like `loopstart` / `loopend` and control changes `CC111` / `CC110`
- if the MIDI has no explicit loop markers, `midi_loop=1` falls back to a simple start-to-end loop
- MIDI/SF2 note lengths come from the MIDI itself
- allowed `hold_minutes` range: `0.1` to `600`
- allowed `pre_eq` range: `0.0` to `1.0`
- allowed `pre_lowpass_hz` range: `0` or `1000` to `20000`

Suggested starting values for harsh or metallic WAV replacements:

```ini
pre_eq=0.35
pre_lowpass_hz=10000
```

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

The MIDI workflow writes a compact PS2 `BGM` that contains only the conductor plus the actually generated playback tracks. If a MIDI only slightly exceeds the original slot sizes, the tool can still expand those slots conservatively. Very dense MIDIs can still be rejected if they exceed the safe rebuild limit.

Output:

```text
output\music188.bgm
output\wave0188.wd
output\music188.mid-sf2-manifest.json
```

## Method 3: VGMTrans Roundtrip Diagnostics

Optional, but useful for tracking down remaining fidelity differences.

Requirements:

- `vgmtrans-cli.exe` next to `BGMInfo.exe`
- or inside a `VGMTransExportBatch` folder next to the tool
- or in a sibling `VGMTrans-v1.3` folder

Then:

1. drag `music188.mid` onto `BGMVgmTransDiff.bat`
2. or run:

```powershell
.\BGMInfo.exe vgmtransdiff "C:\Path\To\music188.mid"
```

Output:

```text
output\music188.vgmtrans-roundtrip-report.json
output\vgmtrans-roundtrip\music188\...
```

The report compares:

- source `MID` vs roundtrip `MID`
- source `SF2` vs roundtrip `SF2`
- track counts, event counts, program sets, preset counts, region counts, and looping/stereo totals

This helps with diagnostics, but it is still not identical to KH2 ingame playback because `VGMTrans` reconstructs standard `MIDI + SF2` data rather than emulating KH2 directly.

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
- The MIDI workflow now keeps the original PS2 `BGM` slot layout. If a MIDI is too dense to fit safely into the original `musicXXX.bgm`, the tool will stop with a clear error instead of writing an unsafe oversized file.
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
