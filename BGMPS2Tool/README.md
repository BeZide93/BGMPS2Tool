# BGMPS2Tool

Version: `v0.9.22`

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

MIDI/SF2 loop preparation is now selectable through `sf2_loop_policy` in `config.ini` and the GUI config dropdown. The default is `safe`, which uses the patched `v0.9.2` loop path with deterministic pitch compensation. `advanced` keeps the recent decoded-ADPCM loop scoring experiments available for A/B tests, `auto-loop` ignores imported SF2 loop points for looped samples and searches new WD-friendly 28-sample-aligned loop points from the end of the sample, and `advanced-auto-loop` searches new 28-sample loop points near the original SF2 loop window.

In `advanced` mode, looped non-percussion MIDI/SF2 samples can also use an adaptive PSX-ADPCM encode check: the tool tests the normal feedback path plus reduced-feedback tonal-loop candidates, then keeps the candidate with the best decoded loop-wrap score. One-shots, percussion-bank samples, and the default `safe` loop path stay on the default encoder path, and the chosen feedback scale is written into each loop diagnostic manifest entry.

The MIDI/SF2 pitch path now also keeps a single canonical `UnityKey/FineTune` representation internally, so tiny residual pitch offsets are less likely to cause unnecessary retuning on random instruments.

The MIDI/SF2 import path now also preserves the original SoundFont sample rates instead of forcing everything to `44100 Hz` up front. KH2 pitch is compensated through WD tuning first, using a SquarePS2/VGMTrans-compatible fine-tune table, and PCM resampling is now reserved mainly for explicit size-guard cases.

Sample pitch and region tuning are now also kept separate much longer in the MIDI/SF2 rebuild path. That keeps the final WD pitch write closer to the way `VGMTrans` carries sample pitch, region tuning, and loop metadata independently before final conversion.

Loop handling now also uses a real internal `start + length + measure` descriptor instead of flattening loops immediately to KH2/WD-only loop bytes. That keeps loop/sample metadata much closer to the way `VGMTrans` carries loop state before final conversion.

The region-side tuning path now also keeps explicit `overridingRootKey`, `coarseTune`, and `fineTune` components until the final WD pitch write, instead of collapsing everything early into one temporary authored pitch value.

The `v0.8.7` rebuild path also corrects the SoundFont ADSR import/merge behavior and writes authored PS2 ADSR to the canonical WD pair used by the decoder:

- `ADSR1 -> 0x0C..0x0D`
- `ADSR2 -> 0x0E..0x0F`

For conservative KH2 WD compatibility testing, the packaged configuration now keeps sparse/original-style program slots with `midi_program_compaction=preserve` and the WD writer keeps at least the original template instrument count.

The `v0.8.8` rebuild path extends the Polyphone-style SoundFont import fixes beyond ADSR:

- SF2 sample/loop offsets, key/velocity ranges, tuning, `sampleModes`, and `scaleTuning` now preserve explicit set-vs-unset behavior during global/local zone merging
- looped SF2 samples keep their full `shdr.start..shdr.end` sample range internally while carrying the real loop start and loop length separately
- PSX ADPCM output still bounds looping samples to the effective SF2 loop end, because KH2/PSX playback has a loop-start flag but no separate SF2-style loop-end marker
- MIDI preset detection now follows the same track/channel-local program-state logic as BGM authoring, so VGMTrans-style MIDIs with reused channels across tracks no longer skip used programs like `0/0`, `0/2`, or `0/3`

The `v0.8.9` rebuild path adds a safer Polyphone-facing SF2 audit layer:

- SF2 generator IDs now follow the Polyphone/SF2 mapping around the risky loop/key area: `46 = keynum`, `47 = velocity`, and `50 = endloopAddrsCoarseOffset`
- `pmod` and `imod` modulator chunks are now parsed for manifest/debug inspection, but dynamic modulation is still not converted into WD playback
- each authored MIDI/SF2 manifest region now includes `Source.SoundFontDebug` with raw/resolved generators, `shdr` sample/loop header values, resolved sample/loop offsets, and parsed modulator records
- ignored SoundFont generator warnings now include readable generator names, so missing filter/LFO/mod-envelope behavior is much easier to identify during Polyphone comparisons

The `v0.9.0` rebuild path improves MIDI/SF2 bank compatibility for SoundFonts that store CC0/MSB variants as direct SF2 banks:

- exact SoundFont bank/program matches are still preferred first
- if a combined MIDI bank such as `128` was produced from `CC0=1, CC32=0`, the importer can now try direct SF2 bank `1` before falling back to percussion or bank `0`
- conversion and preview now share the same preset fallback order, preventing cases where `128/56` was incorrectly authored as drums or bank-0 program `56` instead of a valid `1/56` SF2 preset

The `v0.9.1` rebuild path adds Polyphone/VLC-nearer static SF2 tone handling:

- `initialFilterFc` is now imported with SoundFont preset/instrument merge behavior and converted from absolute cents to Hz
- static per-region low-pass filters are baked into the PCM before PSX ADPCM encoding, so WD output keeps darker/filtered SF2 regions instead of exporting every sample bright/raw
- authored sample identity and manifest output include the filter bake parameters, allowing the same source sample to be duplicated only when two regions really need different tone

The `v0.9.2` rebuild path fixes GM drum-channel handling:

- MIDI channel 10, internally `channel == 9`, now resolves bank-0 program notes through SoundFont percussion bank `128` when available
- this keeps GM-style drum parts such as `128/0` key `39` clap from being authored as melodic `0/0 Piano`
- MIDI/SF2 source preview and WD authoring now use the same percussion preset decision

The `v0.9.4` rebuild path keeps the safe `v0.9.2` loop sound by default while retaining the useful diagnostics from the loop-alignment experiment:

- default MIDI/SF2 loop preparation is back on the conservative `v0.9.2` behavior after the experimental 28-sample start/end search proved too risky for some material
- loop diagnostics are still written into the MIDI/SF2 manifest, including original/effective loop points, start/end shifts, before/after continuity error, and micro-crossfade status
- `sf2_loop_micro_crossfade=1` remains available as a disabled-by-default test option for difficult loop clicks, but the packaged default is `0`
- as of `v0.9.19`, that optional micro-crossfade path first tests a copied PCM candidate and only keeps it when the decoded PSX-ADPCM loop-wrap score improves, so enabling the switch no longer blindly rewrites loop tails
- as of `v0.9.20`, `sf2_loop_tail_wrap_fill=1` can fill a looped sample's final partial 28-sample ADPCM frame from the loop start instead of leaving that partial tail as encoder zero-padding
- as of `v0.9.21`, `sf2_loop_start_content_align=1` is the new safe-mode default; it keeps the WD loop-start block aligned but moves the actual SF2 loop body content onto that block so playback does not repeatedly jump back into earlier pre-loop waveform material
- as of `v0.9.22`, `sf2_loop_end_content_align=1` can prepend a tiny silent prefix so the original SF2 loop end lands on a 28-sample WD block, and `sf2_loop_policy=advanced-auto-loop` searches auto-loop candidates closest to the original SF2 loop window
- WD pitch writing uses the shared Square/WD fine-tune table path consistently and exposes raw WD pitch bytes plus quantization error in the manifest

The `v0.9.6` rebuild path tightens the newer MIDI/SF2 fixes while restoring explicit pitch-bend control:

- pitch-bend retargeting is now config-controlled again through `midi_pitch_bend_workaround`, and it only runs when the input MIDI actually contains bend events
- percussion paths are protected from bend key-retargeting so drum kits like `128/0` do not shift to wrong instruments on bend activity
- `delayVolEnv` (`generator 33`) is now consumed by the import envelope path (delay + hold timing), instead of being ignored
- constant-source SF2 modulators are now baked statically into supported generator targets, while dynamic modulators still remain debug/audit-only for WD playback

The `v0.9.7` rebuild path improves loop diagnostics and short-loop candidate selection:

- short-loop candidates are now scored after an actual PSX-ADPCM encode/decode roundtrip, so the chosen loop point is judged against the decoded in-game-style loop transition rather than only against pre-encode PCM continuity
- conversion logs and MIDI/SF2 manifests now expose the final decoded-ADPCM loop RMS, making loop-click/pitch problems easier to separate from SF2, pitch, ADSR, or region-mapping issues
- this is intentionally not a full global ADPCM encoder rewrite; loop-specific predictor/state optimization remains a later controlled-risk task

The `v0.9.8` rebuild path tightens that loop scoring around the PSX-ADPCM predictor state:

- the loop-start block is now decoded again using the predictor history from the loop end, which better models the actual stateful hardware wrap instead of comparing only the first-pass decoded loop-start samples
- short-loop candidate scoring now uses this stateful-wrap RMS first and also penalizes decoded state mismatch, reducing cases where a loop looks numerically smooth but still clicks, hisses, or rasps when the ADPCM predictor history changes at the wrap
- conversion logs and MIDI/SF2 manifests now distinguish decoded ADPCM stateful-wrap RMS from decoded state-mismatch RMS

The `v0.9.9` rebuild path extends the loop fix to longer SF2 loops such as the `152` trumpet case:

- long loop ends are now selected from nearby 28-sample-aligned candidates using the actual decoded ADPCM wrap score instead of falling into implicit tail-padding whenever the SF2 loop end is not block-aligned
- looped ADPCM samples get a loop-only predictor-state encode pass so the repeated loop body is judged against the loop-end history; one-shot samples still use the old encoder path
- `sf2_loop_micro_crossfade=0` now fully disables crossfade behavior, so micro-crossfade remains an explicit opt-in test knob

The `v0.9.10` rebuild path adds safer loop-end candidate visibility and bridge testing:

- MIDI/SF2 manifests now list the loop-end candidates considered for each prepared loop, including the selected strategy and the PCM/decoded-ADPCM scoring values
- non-28-aligned SF2 loop ends can test a tiny `bridge_tail` candidate that fills only the missing ADPCM alignment samples, instead of forcing the solver to choose only an earlier trim point
- the long-loop score now balances PCM seam continuity and decoded state mismatch more strongly, which is intended to reduce trumpet-style clicks or buzz that survive a low decoded-wrap score

The `v0.9.11` rebuild path adds a safety guard for audible loop-trim regressions:

- if the numerically best long-loop candidate cuts the loop end back aggressively, the solver can prefer a nearby forward `bridge_tail` candidate when it keeps the score close and lowers decoded state mismatch
- this specifically prevents the `152` trumpet case from defaulting to the harsher `trim -43` loop-end choice

The `v0.9.12` rebuild path tightens medium-short loop pitch handling:

- short-loop tail smoothing now covers the full 512-sample pitch-safe loop range, so medium-short loops such as `0/24 Nylon` also get pitch compensation when ADPCM block alignment extends the loop
- this removes the hidden tail-padding path for `A001100.WAV`, which is a likely source of steady loop-period hum

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
- the packaged `v0.8.7` config uses `midi_program_compaction=preserve` for safer WD layout comparisons against original KH2 banks
- `adsr` applies only to the MIDI/SF2 workflow.
  - `authored` = use the VGMTrans-style authored PS2 ADSR fit everywhere
  - `auto` = use the current hybrid logic and borrow template WD ADSR where that still helps
  - `template` = force template WD ADSR wherever a template match exists
- `adsr=authored` is now the default and recommended mode.
- the authored ADSR path now fits PS2 envelopes against the same `PSXSPU` / `RateTable` model used by `VGMTrans`, so it is much closer to real exported KH2 ADSR behavior than the older local heuristic was
- SoundFont volume-envelope generators are now merged with SF2-style global/local override behavior before being combined into the final effective region ADSR
- authored ADSR is written to the canonical WD words `0x0C..0x0D` and `0x0E..0x0F`, and the WD reader / renderer / Advanced tab decode the same pair
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
- The current SoundFont importer still ignores advanced dynamic SF2 features such as LFO-driven filters, modulators, and filter Q/resonance.
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
