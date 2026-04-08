# BGMPS2Tool

Version: `v0.8.6`

<picture>
 <source media="(prefers-color-scheme: dark)" srcset="https://github.com/BeZide93/BGMPS2Tool/blob/main/Icon.png">
 <source media="(prefers-color-scheme: light)" srcset="https://github.com/BeZide93/BGMPS2Tool/blob/main/Icon.png">
 <img alt="YOUR-ALT-TEXT" src="https://github.com/BeZide93/BGMPS2Tool/blob/main/Icon.png">
</picture>

`BGMPS2Tool` is a Windows tool package for rebuilding `Kingdom Hearts II Final Mix` PS2 music tracks.

It currently supports two workflows:

- `WAV -> rebuilt PS2 musicXXX.bgm + waveXXXX.wd`
- `MIDI + SF2 -> rebuilt PS2 musicXXX.bgm + waveXXXX.wd`

It now also includes a real Windows GUI alongside the existing `config.ini` + drag-and-drop batch workflow.

The GUI Tools tab is now also fully functional and includes:

- a `BGM 0020xx Offset Tool` for patching program markers in a single `BGM`
- a `Field/Battle Maker / WD Combiner` for merging a secondary `WD` into a primary one and patching the secondary `BGM` to match

The GUI now also includes an `Advanced` tab for loading a `.wd` directly and applying both instrument-level expert edits and optional per-region overrides such as pitch/semitone shifts, Hz retuning, loop offsets, loop mode forcing, volume trim, pan shift, and raw `ADSR1/ADSR2` overrides.
It also now includes a small `README` button on that tab with workflow notes and safety caveats for advanced WD editing.

The new MIDI/SF2 workflow is cleaner than the legacy long-note workaround because it authors:

- a real PS2 `WD` instrument bank from the SoundFont
- a real PS2 `BGM` sequence from the MIDI

For MIDI rebuilds, the tool now writes a compact PS2 `BGM` containing only the conductor plus the actually generated playback tracks, while still enforcing conservative size limits for safety.

Current hard caps:

- authored `WD`: `980 KB`
- authored `BGM`: `48900 bytes`

The MIDI/BGM rebuild path now also trims per-track padding aggressively. The tool still writes the track-size table and, for looped builds, preserves the original KH2 track-slot structure, but it no longer pads every track back up to the original template slot length if those bytes are unused.

Short-loop handling on tricky MIDI/SF2 material has also been re-balanced again so the current loop/pitch behavior stays much closer to the good `v0.6.67` sound on tracks like `152`, while still keeping the newer ADSR controls and diagnostics.

The MIDI/SF2 pitch path now also keeps a single canonical `UnityKey/FineTune` representation internally, so tiny residual pitch offsets are less likely to cause unnecessary retuning on random instruments.

The MIDI/SF2 import path now also preserves the original SoundFont sample rates instead of forcing everything to `44100 Hz` up front. KH2 pitch is compensated through WD tuning first, using a SquarePS2/VGMTrans-compatible fine-tune table, and PCM resampling is now reserved mainly for explicit size-guard cases.

Sample pitch and region tuning are now also kept separate much longer in the MIDI/SF2 rebuild path. That keeps the final WD pitch write closer to the way `VGMTrans` carries sample pitch, region tuning, and loop metadata independently before final conversion.

Loop handling now also uses a real internal `start + length + measure` descriptor instead of flattening loops immediately to KH2/WD-only loop bytes. That keeps loop/sample metadata much closer to the way `VGMTrans` carries loop state before final conversion.

The region-side tuning path now also keeps explicit `overridingRootKey`, `coarseTune`, and `fineTune` components until the final WD pitch write, instead of collapsing everything early into one temporary authored pitch value.

## Included Files

- `BGMInfo.exe`
- `BGMInfo.dll`
- `BGMInfo.deps.json`
- `BGMInfo.runtimeconfig.json`
- `BGMPS2ToolGUI.exe`
- `BGMPS2ToolGUI.dll`
- `BGMPS2ToolGUI.deps.json`
- `BGMPS2ToolGUI.runtimeconfig.json`
- `KhPs2Audio.Shared.dll`
- `KhPs2Audio.Shared.deps.json`
- `BGMReplaceWav.bat`
- `BGMReplaceMidiSf2.bat`
- `BGMVgmTransDiff.bat`
- `BGMPS2Tool.ico`
- `config.ini`
- `tracklist.txt`
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
- See `CREDITS.md` for project thanks and acknowledgements.

## Configuration

The tool reads:

- `config.ini`

from the same folder as `BGMInfo.exe`.

The GUI reads and writes the same `config.ini`, so the batch workflow and the GUI stay in sync.

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
- `sf2_pre_eq` applies only to the MIDI/SF2 workflow. It adds the same gentle pre-conditioning curve that already exists on the WAV path, but now on imported SoundFont sample data at the preserved stored sample rate.
- `sf2_pre_lowpass_hz` applies only to the MIDI/SF2 workflow. It is a manual low-pass override for imported SoundFont sample data before PS2 encoding. Use `0` to disable the manual override.
- `sf2_auto_lowpass` applies only to the MIDI/SF2 workflow. When enabled, samples that are explicitly resampled are automatically low-passed near their original bandwidth so the rebuilt PS2 bank does not keep as much empty upscaled high-frequency noise. It is now an opt-in knob instead of the default.
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

## GUI Workflow

The GUI is additional to the old batch flow and does not replace it.

The GUI can:

- point at a KH2FM BGM export root such as `_Extracted KH2FM\\export\\@KH2\\bgm`
- resolve `musicXXX.bgm` + `waveXXXX.wd` templates by filename, even when the MIDI/SF2 or WAV lives somewhere else
- expose all current `config.ini` options through controls
- show track number, name, and description from the bundled `tracklist.txt`
- play a direct `MIDI + SF2` source preview for comparison
- play a rendered `BGM + WD` output preview for comparison
- use the `Tools` tab for the `BGM 0020xx Offset Tool` and `Field/Battle Maker / WD Combiner`
- use the `Advanced` tab to inspect a `WD` bank instrument-by-instrument, then optionally drill down into individual regions for finer per-split edits without manual hex editing

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
- the GUI removes that same-folder requirement when you provide a template root directory
- The new files are written to `output`, so the original files stay untouched.
- For compatibility, the tool keeps the original PS2 header/container identity where practical, but the authored sequence/bank data is rebuilt.
- The current SoundFont importer ignores some advanced SF2 features such as filter/LFO/modulator behavior.
- the SoundFont importer now preserves native SF2 sample rates during import and compensates KH2 pitch through WD tuning instead of forcing early `44100 Hz` normalization
- sample pitch and region tuning are now kept separate until the final WD pitch write, instead of being folded together early in the authored region build
- loop metadata is now also carried internally as `start + length + measure` and only converted to final PS2 loop bytes late in the authoring path
- region tuning is now preserved more explicitly as sample pitch plus `overridingRootKey`, `coarseTune`, and `fineTune`, instead of being collapsed early into one temporary authored pitch value
- the MIDI/SF2 path now also supports optional SoundFont-side pre-conditioning on that preserved-rate sample data:
  - `sf2_pre_eq`
  - `sf2_pre_lowpass_hz`
  - `sf2_auto_lowpass`
- the MIDI/SF2 path now also exposes ADSR mode selection through:
  - `adsr=authored`
  - `adsr=auto`
  - `adsr=template`
- `adsr=authored` is the current recommended default because it now follows the same PS2 ADSR timing model used by `VGMTrans`
- WD fine-tune encoding now follows the SquarePS2/VGMTrans non-linear table instead of the older linear approximation, so `UnityKey/FineTune` is much closer to real KH2 tuning behavior
- loop/sample metadata now also prefers real PSX ADPCM loop markers through a generic PSX loop resolver, instead of relying only on WD region fields
- short looping MIDI/SF2 material now stays much closer to the stable `v0.6.67` behavior again; the tool avoids the more aggressive seam-search path that caused audible regressions on `152`-style content
- The current MIDI importer approximates pitch-bend, but it still does not emit a fully native KH2 continuous pitch opcode.
- if you only need a converted `WD` bank for pairing with existing `BGM` files, `sf2_bank_mode=full` is useful because it converts unused SoundFont presets too instead of authoring only the presets referenced by the current MIDI
- if you specifically want to test the same MIDI/SF2 case without sparse WD table gaps, set `midi_program_compaction=compact`
- If a MIDI does not match the available `.sf2`, the tool will now try to use the original `waveXXXX.wd` as the program source before failing.
- The MIDI path now writes a compact `BGM` track table for closer `MID -> BGM -> MID` roundtrip fidelity. Very dense MIDIs are rejected once the rebuilt `BGM` would exceed the hard `48900`-byte safety cap.
- Newly authored multi-sample `WD` files now insert KH2-style 16-byte zero separators between sample chunks instead of packing all sample data directly back-to-back.

## Quick Start

GUI workflow:

- launch `BGMPS2ToolGUI.exe`
- set your KH2FM template root, for example `_Extracted KH2FM\\export\\@KH2\\bgm`
- use `Show Tracklist` to inspect the bundled `tracklist.txt` in a formatted table view
- use `Clear Temp Preview` on the Compare tab to delete rendered preview WAVs from `%TEMP%`
- the right-side settings area is split into separate `MIDI + SF2` and `WAV` config blocks, and each setting now has a small `i` info button with an explanation
- use the `Tools` tab for direct `BGM` program offsetting and `WD` combining without leaving the GUI
- use the `Advanced` tab when you want a direct `.wd` instrument editor with per-instrument pitch, Hz, loop, pan, volume, and raw ADSR controls

CLI additions:

- `BGMInfo offsetbgm <InputBgm> <InstrumentOffset> [OutputDir]`
- `BGMInfo combinewd <PrimaryWd> <SecondaryWd> [OutputDir] [PrimaryBgm] [SecondaryBgm]`
- choose your `MIDI + SF2` or `WAV`
- adjust the same settings that also live in `config.ini`
- rebuild and use the built-in source/output preview player for comparison

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


