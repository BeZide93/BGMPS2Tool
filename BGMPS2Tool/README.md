# BGMPS2Tool

Version: `v0.6.69`

`BGMPS2Tool` is a Windows tool package for rebuilding `Kingdom Hearts II Final Mix` PS2 music tracks.

It currently supports two workflows:

- `WAV -> rebuilt PS2 musicXXX.bgm + waveXXXX.wd`
- `MIDI + SF2 -> rebuilt PS2 musicXXX.bgm + waveXXXX.wd`

The new MIDI/SF2 workflow is cleaner than the legacy long-note workaround because it authors:

- a real PS2 `WD` instrument bank from the SoundFont
- a real PS2 `BGM` sequence from the MIDI

For MIDI rebuilds, the tool now writes a compact PS2 `BGM` containing only the conductor plus the actually generated playback tracks, while still enforcing conservative size limits for safety.

Current hard caps:

- authored `WD`: `980 KB`
- authored `BGM`: `48900 bytes`

The MIDI/BGM rebuild path now also trims per-track padding aggressively. The tool still writes the track-size table and, for looped builds, preserves the original KH2 track-slot structure, but it no longer pads every track back up to the original template slot length if those bytes are unused.

Short-loop handling on tricky MIDI/SF2 material has also been re-balanced again so the current loop/pitch behavior stays much closer to the good `v0.6.67` sound on tracks like `152`, while still keeping the newer ADSR controls and diagnostics.

## Included Files

- `BGMInfo.exe`
- `BGMInfo.dll`
- `BGMInfo.deps.json`
- `BGMInfo.runtimeconfig.json`
- `KhPs2Audio.Shared.dll`
- `KhPs2Audio.Shared.deps.json`
- `BGMReplaceWav.bat`
- `BGMReplaceMidiSf2.bat`
- `BGMVgmTransDiff.bat`
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
- `sf2_volume=...`
- `sf2_bank_mode=...`
- `sf2_pre_eq=...`
- `sf2_pre_lowpass_hz=...`
- `sf2_auto_lowpass=...`
- `midi_program_compaction=...`
- `adsr=...`
- `midi_pitch_bend_workaround=...`
- `midi_loop=...`
- `hold_minutes=...`
- `pre_eq=...`
- `pre_lowpass_hz=...`

Notes:

- `volume` applies to imported WAVs in the WAV workflow.
- `sf2_volume` applies only to SoundFont sample audio in the MIDI/SF2 workflow. Keep it at `1.0` if you want the closest possible `SF2 -> WD -> SF2` roundtrip fidelity.
- `sf2_bank_mode` applies only to the MIDI/SF2 workflow.
  - `used` = author only presets referenced by the current MIDI
  - `full` = author the whole SoundFont bank, including presets not referenced by the current MIDI
- `sf2_pre_eq` applies only to the MIDI/SF2 workflow. It adds the same gentle pre-conditioning curve that already exists on the WAV path, but on imported SoundFont sample data after `44100 Hz` normalization.
- `sf2_pre_lowpass_hz` applies only to the MIDI/SF2 workflow. It is a manual low-pass override for imported SoundFont sample data after normalization. Use `0` to disable the manual override.
- `sf2_auto_lowpass` applies only to the MIDI/SF2 workflow. When enabled, non-`44100 Hz` SoundFont samples are automatically low-passed near their original bandwidth after normalization so the rebuilt PS2 bank does not keep as much “empty” upscaled high-frequency noise. It is now an opt-in knob instead of the default.
- `midi_program_compaction` applies only to the MIDI/SF2 workflow.
  - `auto` = keep the current heuristic
  - `compact` = remove sparse WD table gaps and renumber authored instruments densely
  - `preserve` = keep original-style sparse program indices even if that leaves empty WD slots between real instruments
- `adsr` applies only to the MIDI/SF2 workflow.
  - `authored` = use the VGMTrans-style authored PS2 ADSR fit everywhere
  - `auto` = use the current hybrid logic and borrow template WD ADSR where that still helps
  - `template` = force template WD ADSR wherever a template match exists
- `adsr=authored` is now the default and recommended mode.
- the authored ADSR path now fits PS2 envelopes against the same `PSXSPU` / `RateTable` model used by `VGMTrans`, so it is much closer to real exported KH2 ADSR behavior than the older local heuristic was
- `midi_pitch_bend_workaround` applies only to the MIDI/SF2 workflow. When enabled, the tool approximates pitch bend by retargeting notes and, where needed, generating tuned instrument variants. When disabled, pitch bend events are ignored completely.
- `midi_loop` applies only to the MIDI/SF2 workflow. Use `1` if you want the authored PS2 `BGM` to loop instead of ending as a one-shot sequence.
- `hold_minutes` is mainly relevant to the older `replacewav` loop workflow.
- `hold_minutes` does not drive the actual note lengths in the MIDI/SF2 workflow, because those come from the MIDI sequence itself.
- `pre_eq` is a gentle pre-encode tone-shaping stage for the WAV workflow. It can help reduce metallic or brittle artifacts after aggressive PS2 downsampling and ADPCM encoding.
- `pre_lowpass_hz` applies an optional additional low-pass before PS2 encoding in the WAV workflow. Use `0` to disable it.

Optional diagnostics:

- `BGMInfo vgmtransdiff <InputMid> [InputSf2]` compares the source `MID + SF2` against a `VGMTrans` roundtrip of the authored `BGM + WD`
- this command requires `vgmtrans-cli.exe` to be available next to `BGMInfo.exe`, inside a `VGMTransExportBatch` subfolder, or in a sibling `VGMTrans-v1.3` folder

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
- the SoundFont importer now normalizes non-`44100 Hz` sample data to `44100 Hz` during import, because the PS2 rebuild path behaves more consistently when the raw sample data is already on the target rate
- the MIDI/SF2 path now also supports optional SoundFont-side pre-conditioning after that normalization:
  - `sf2_pre_eq`
  - `sf2_pre_lowpass_hz`
  - `sf2_auto_lowpass`
- the MIDI/SF2 path now also exposes ADSR mode selection through:
  - `adsr=authored`
  - `adsr=auto`
  - `adsr=template`
- `adsr=authored` is the current recommended default because it now follows the same PS2 ADSR timing model used by `VGMTrans`
- normalized looping SoundFont samples now also pull the loop end back to a clean PSX-ADPCM block boundary when possible, which is intended to reduce glitchy wraparound on rebuilt short loops
- The current MIDI importer approximates pitch-bend, but it still does not emit a fully native KH2 continuous pitch opcode.
- if you only need a converted `WD` bank for pairing with existing `BGM` files, `sf2_bank_mode=full` is useful because it converts unused SoundFont presets too instead of authoring only the presets referenced by the current MIDI
- if you specifically want to test the same MIDI/SF2 case without sparse WD table gaps, set `midi_program_compaction=compact`
- If a MIDI does not match the available `.sf2`, the tool will now try to use the original `waveXXXX.wd` as the program source before failing.
- The MIDI path now writes a compact `BGM` track table for closer `MID -> BGM -> MID` roundtrip fidelity. Very dense MIDIs are rejected once the rebuilt `BGM` would exceed the hard `48900`-byte safety cap.
- Newly authored multi-sample `WD` files now insert KH2-style 16-byte zero separators between sample chunks instead of packing all sample data directly back-to-back.

## Quick Start

WAV workflow:

- `BGMReplaceWav.bat`
- `BGMInfo.exe replacewav "C:\Path\To\music188.ps2.wav"`

MIDI/SF2 workflow:

- `BGMReplaceMidiSf2.bat`
- `BGMInfo.exe replacemidi "C:\Path\To\music188.mid"`
- `BGMInfo.exe replacemidi "C:\Path\To\music188.mid" "C:\Path\To\wave0188.sf2"`

ADSR mode examples:

- `adsr=authored` if you want the new VGMTrans-style authored ADSR path everywhere
- `adsr=auto` if you want the older hybrid behavior for comparison
- `adsr=template` if you want to force template WD ADSR wherever a match exists

Diagnostics workflow:

- `BGMVgmTransDiff.bat`
- `BGMInfo.exe vgmtransdiff "C:\Path\To\music188.mid"`

When `midi_loop=1` is enabled:

- explicit MIDI loop markers are preferred if they exist
- supported explicit markers include text markers such as `loopstart` / `loopend` and control changes `CC111` / `CC110`
- if no explicit MIDI loop markers exist, the tool writes a simple start-to-end fallback loop

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
