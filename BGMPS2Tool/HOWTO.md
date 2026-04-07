# HOWTO

Version: `v0.6.69`

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
sf2_bank_mode=used
sf2_pre_eq=0.0
sf2_pre_lowpass_hz=0
sf2_auto_lowpass=0
midi_program_compaction=auto
adsr=authored
midi_pitch_bend_workaround=1
midi_loop=0
hold_minutes=60
pre_eq=0.0
pre_lowpass_hz=0
```

Meaning:

- `volume`: loudness multiplier for imported WAVs
- `sf2_volume`: loudness multiplier for MIDI + SF2 conversion
- `sf2_bank_mode`: whether to author only MIDI-used presets or the full SoundFont bank
- `sf2_pre_eq`: optional tone shaping for imported SoundFont sample data after `44100 Hz` normalization
- `sf2_pre_lowpass_hz`: optional manual low-pass cutoff for imported SoundFont sample data after normalization
- `sf2_auto_lowpass`: auto low-pass non-`44100 Hz` SoundFont samples near their original bandwidth after normalization
- `midi_program_compaction`: controls whether sparse MIDI program numbers stay sparse in the authored WD or get renumbered densely
- `adsr`: controls whether MIDI/SF2 ADSR uses the VGMTrans-style authored path, the hybrid auto path, or template WD ADSR
- `midi_pitch_bend_workaround`: enables or disables the current pitch bend approximation system for the MIDI/SF2 workflow
- `midi_loop`: loops the authored MIDI/BGM sequence when set to `1`
- `hold_minutes`: minimum note hold time for looped `replacewav` builds
- `pre_eq`: optional tone shaping before PS2 encoding for the WAV workflow
- `pre_lowpass_hz`: optional extra low-pass cutoff before PS2 encoding for the WAV workflow

Notes:

- `hold_minutes` mainly affects the older WAV replacement path
- `sf2_volume=1.0` is recommended if you want the closest possible `SF2 -> WD -> SF2` roundtrip fidelity
- `sf2_bank_mode=used` is the normal mode for MIDI-driven rebuilds
- `sf2_bank_mode=full` is useful if you mainly want the `SF2 -> WD` conversion, including unused presets, for pairing with existing `BGM` files
- the authored ADSR path now follows the same `PSXSPU` / `RateTable` timing model used by `VGMTrans`, so it is much closer to real KH2 export ADSR behavior than the older heuristic
- `sf2_pre_eq` is the SF2-side equivalent of the existing WAV `pre_eq`
- `sf2_pre_lowpass_hz` is a manual override if you already know the rough bandwidth you want to keep
- `sf2_auto_lowpass=0` is now the safer default; turn it on only if a bank really benefits from it
- normalized looping SoundFont samples now also try to pull the loop end back to a cleaner PSX-ADPCM block boundary automatically
- short-loop MIDI/SF2 handling was re-balanced again after a regression on `152`-style material, so loop/pitch behavior is now much closer to the good `v0.6.67` result while keeping the newer ADSR and diagnostics improvements
- `midi_program_compaction=auto` keeps the current heuristic
- `midi_program_compaction=compact` removes empty sparse WD slots and renumbers real instruments densely
- `midi_program_compaction=preserve` keeps original-style sparse program indices and any resulting WD table gaps
- `adsr=authored` is now the default and recommended mode
- `adsr=authored` uses the VGMTrans-style PS2 ADSR fit for every authored region
- `adsr=auto` keeps the hybrid logic and still borrows template WD ADSR where that is considered helpful
- `adsr=template` forces template WD ADSR wherever a template match exists
- `midi_pitch_bend_workaround=1` is the current default
- `midi_pitch_bend_workaround=0` is useful for testing whether bend-driven note retargeting / tuned instrument cloning is causing layout or sound problems
- `midi_loop=1` is useful when you want the rebuilt PS2 `BGM` to loop ingame instead of behaving like a one-shot sequence
- if the MIDI contains explicit loop markers, `midi_loop=1` now prefers those markers
- supported explicit markers include text markers like `loopstart` / `loopend` and control changes `CC111` / `CC110`
- if the MIDI has no explicit loop markers, `midi_loop=1` falls back to a simple start-to-end loop
- looped MIDI rebuilds now preserve the original KH2 `BGM` slot layout for better ingame compatibility
- MIDI/SF2 note lengths come from the MIDI itself
- allowed `hold_minutes` range: `0.1` to `600`
- allowed `pre_eq` range: `0.0` to `1.0`
- allowed `pre_lowpass_hz` range: `0` or `1000` to `20000`
- allowed `sf2_pre_eq` range: `0.0` to `1.0`
- allowed `sf2_pre_lowpass_hz` range: `0` or `1000` to `20000`

Suggested starting values for harsh or metallic WAV replacements:

```ini
pre_eq=0.35
pre_lowpass_hz=10000
```

Suggested starting values for noisy or “empty” upscaled SoundFont banks:

```ini
sf2_pre_eq=0.15
sf2_pre_lowpass_hz=0
sf2_auto_lowpass=1
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

The MIDI workflow writes a compact PS2 `BGM` that contains only the conductor plus the actually generated playback tracks. If a MIDI only slightly exceeds the original slot sizes, the tool can still expand those slots conservatively. Very dense MIDIs are rejected once the rebuilt `BGM` would exceed the hard `48900`-byte safety cap.

The tool also trims unnecessary per-track `00` padding from authored `BGM`s. For looped rebuilds it still keeps the original KH2 track-slot structure, but each track now only occupies its real encoded byte length instead of being padded back up to the full template slot length.

If you set:

```ini
sf2_bank_mode=full
```

the tool authors the full SoundFont bank into the rebuilt `WD`, not just the presets used by the current MIDI. In that mode:

- unused presets are still converted
- log output says `converted but unused`
- with `adsr=auto`, SoundFont-derived / authored ADSR is still preferred in cases where template reuse is known to be a bad fit
- pitch-variant cloning is disabled to keep the bank layout simpler for pairing with existing `BGM` files
- if you want to hear the same MIDI/SF2 case without sparse WD gaps, keep `sf2_bank_mode=used` and set `midi_program_compaction=compact`

If you want to compare ADSR modes directly, try:

```ini
adsr=authored
adsr=auto
adsr=template
```

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

## Method 4: VGMTrans Export Batch

Optional, but useful when you want a quick `WD -> SF2` and `BGM -> MIDI` export for comparison work.

This helper is shipped separately from the main `BGMPS2Tool` package.

Look for:

```text
VGMTrans-v1.3\VGMTransExportKh2.bat
```

or, in the GitHub upload folder:

```text
Github\VGMTransExportBatch\
```

Usage:

1. drag a KH2 `musicXXX.bgm` **or** `waveXXXX.wd` file onto `VGMTransExportKh2.bat`
2. the batch automatically looks for the matching partner file in the same folder
3. it then runs `vgmtrans-cli` and exports the reconstructed files

Output:

```text
vgmtrans-output\BGM XXX.mid
vgmtrans-output\BGM XXX.sf2
vgmtrans-output\BGM XXX.dls
```

Notes:

- you can drag either the `.bgm`, the `.wd`, or both
- for standard KH2 names like `music152.bgm` + `wave0152.wd`, the batch auto-pairs correctly
- this export is excellent for structure checks and roundtrip comparisons
- it is **not** guaranteed to sound identical to KH2 ingame playback, because `VGMTrans` reconstructs standard `MIDI + SF2` data instead of emulating KH2 directly

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
- non-`44100 Hz` SoundFont sample data is now normalized to `44100 Hz` during import before PS2 encoding
- after that normalization, the MIDI/SF2 path can optionally apply SoundFont-side pre-conditioning with `sf2_pre_eq`, `sf2_pre_lowpass_hz`, and `sf2_auto_lowpass`
- for normalized looping SoundFont samples, the importer now also trims the loop end back to a clean ADPCM block boundary when that can be done safely
- Advanced SF2 features such as modulators, filters, and LFO behavior are currently ignored.
- MIDI pitch-bend is currently approximated, not yet written as a true native KH2 pitch opcode.
- The MIDI workflow now keeps the original PS2 `BGM` slot layout.
- Hard safety caps currently are:
  - authored `WD`: `980 KB`
  - authored `BGM`: `48900 bytes`
- If a MIDI is too dense and the rebuilt `BGM` would exceed `48900` bytes, the tool now stops with a clear error instead of writing an ingame-silent oversized sequence.
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

### "The authored BGM would be ... bytes, but the current hard KH2 BGM safety cap is 48900 bytes."

This means the `WD` side already fit, but the `BGM` / sequence side is too dense for the current safe ingame limit.

Practical fixes:

- remove duplicate or repeated notes
- thin doubled chord or backing layers
- reduce overly dense controller data
- reduce very dense pitch-bend automation
- simplify the heaviest MIDI tracks first
