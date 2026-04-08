# CHANGELOG

## v0.8.5 - 2026-04-08

### Added

- the `Advanced` tab now supports a two-stage workflow: global instrument edits plus optional per-region overrides under each instrument
- per-region controls now exist for pitch, fine-tune, Hz retune helper, loop offset, loop mode, volume, pan, and raw `ADSR1/ADSR2`

### Changed

- advanced WD saves now merge global instrument edits first and then apply local region overrides on top
- the built-in `Advanced` README text now documents the new global-vs-region editing model and the remaining sample-level loop caveats

## v0.8.4 - 2026-04-08

### Fixed

- the `Advanced` tab `README` window is now formatted as a readable help view with headings, bullet points, and a close button instead of a dense raw text block

## v0.8.3 - 2026-04-08

### Added

- new `README` button on the `Advanced` tab with built-in English notes about per-instrument editing behavior, shared-sample loop caveats, and raw ADSR risks

## v0.8.2 - 2026-04-08

### Fixed

- the new `Advanced` tab now renders instrument editor cards correctly instead of collapsing into thin line artifacts on load
- advanced instrument controls now use a stable scrollable vertical stack layout, so loaded WD instruments are visible and expandable in the GUI

## v0.8.1 - 2026-04-08

### Added

- new `Advanced` GUI tab for loading a `.wd` directly and listing every instrument slot as an expandable editor card
- per-instrument advanced WD controls for pitch/semitone offset, fine-tune cent offset, Hz retuning, loop offset, loop mode forcing, volume trim, pan shift, and raw `ADSR1/ADSR2` overrides
- new shared `WdAdvancedTooling` backend for loading WD instrument summaries and writing edited WD outputs safely

### Changed

- the GUI now includes a real WD-level advanced editing workflow in addition to rebuild, compare, and KH2 utility tools
- advanced loop edits now rewrite both region loop fields and sample-side PSX ADPCM loop markers so loop adjustments stay coherent

### Fixed

- advanced WD pitch edits now use the same SquarePS2-style `UnityKey/FineTune` encoding path as the main rebuild logic instead of a separate ad-hoc editor path
- expert loop retuning no longer has to be done manually in a hex editor when a user only wants to shift loop starts or force one-shot vs looping behavior

## v0.8.0 - 2026-04-08

### Added

- complete `BGM 0020xx Offset Tool` in the GUI Tools tab for patching program markers in a single `BGM`
- complete `Field/Battle Maker / WD Combiner` in the GUI Tools tab for merging two `WD` banks and patching the secondary `BGM`
- new CLI commands:
  - `BGMInfo offsetbgm <InputBgm> <InstrumentOffset> [OutputDir]`
  - `BGMInfo combinewd <PrimaryWd> <SecondaryWd> [OutputDir] [PrimaryBgm] [SecondaryBgm]`

### Changed

- the GUI Tools tab is no longer a placeholder area; both planned KH2 utility tools are now fully wired to the shared backend

### Fixed

- tool workflows for program-offset patching and WD combining now run through the same packaged binaries and mirrored source tree instead of existing only as planned GUI placeholders

## v0.7.4 - 2026-04-08

### Changed

- the right-side `config.ini` area is now split into separate `MIDI + SF2 Workflow` and `WAV Workflow` setting blocks
- every visible GUI config setting now has a small `i` button with a direct explanation dialog and hover tooltip

### Fixed

- GUI settings are now easier to navigate because MIDI/SF2-only and WAV-only options are no longer mixed into one long flat list

## v0.7.3 - 2026-04-08

### Added

- new `Clear Temp Preview` button on the Compare tab to delete rendered source/output preview files from `%TEMP%`

### Fixed

- preview cleanup is now available directly in the GUI instead of requiring manual temp-folder cleanup after A/B listening sessions

## v0.7.2 - 2026-04-08

### Changed

- `Show Tracklist` now renders the bundled `tracklist.txt` as a proper table using the first row as column headers instead of showing the raw tab-separated text dump

### Fixed

- bundled track metadata is now much easier to scan in the GUI because tab-separated columns are aligned into a real grid view

## v0.7.1 - 2026-04-08

### Changed

- the GUI now uses a bundled `tracklist.txt` next to the package instead of the older PDF-based track list lookup
- the track-list path picker has been replaced with a simple `Show Tracklist` button that opens the bundled text list directly

### Fixed

- GUI track metadata now loads from the shipped `tracklist.txt` file directly, so local track lookup no longer depends on the removed PDF import path
- the package and `Github` mirror no longer carry the obsolete KH2 track list PDF

## v0.7.0 - 2026-04-08

### Added

- new `BGMPS2ToolGUI.exe` Windows GUI alongside the existing batch + `config.ini` workflow
- new GUI path settings for a KH2FM exported BGM template root and a local KH2 track list source
- new direct source/output comparison player for `MIDI + SF2` preview vs authored `BGM + WD` preview
- new prepared GUI area for the future `Field/Battle maker / WD combiner / BGM 0020xx offset tool`

### Changed

- the GUI now reads and writes the same `config.ini` keys that the batch and CLI path already use, so both workflows stay aligned
- MIDI/SF2 and WAV rebuilds can now be driven from a template root through the GUI instead of requiring `musicXXX.bgm` + `waveXXXX.wd` next to every input file
- the package now ships with the GUI binary set and the bundled KH2 track list source for local track lookup

### Fixed

- the GUI rebuild path now stages template assets in a temp workspace before calling the existing rebuilders, which keeps the old converter logic intact while removing the same-folder template requirement for GUI use
- source preview now renders directly from `MIDI + SF2` with the current GUI/config settings instead of forcing you to compare only after a full rebuild

## v0.6.73 - 2026-04-08

### Changed

- MIDI/SF2 loop handling now carries a real internal `start + length + measure` descriptor instead of flattening loops immediately to KH2/WD-specific loop bytes
- sample pitch and region tuning are now split more explicitly into sample-side pitch data and region-side `overridingRootKey`, `coarseTune`, and `fineTune` components all the way to the final WD pitch write
- PSX loop prioritizing is now handled through a more generic sample-side ADPCM loop resolver, instead of only through KH2/WD-specific loop-byte fallback behavior

### Fixed

- loop manifests and authored loop decisions now expose the actual loop descriptor carried through the rebuild, which makes short-loop debugging much closer to the `VGMTrans` model
- authored MIDI/SF2 pitch diagnostics now show the separate sample and region pitch components that feed the final KH2 `UnityKey/FineTune`, which reduces guesswork when isolating retune issues
- sample-side PSX loop markers, WD loop fields, and renderer/template fallback paths now resolve through the same loop prioritizing model, which removes more format-specific loop ambiguity

## v0.6.72 - 2026-04-08

### Changed

- MIDI/SF2 authored samples now keep loop/sample metadata in sample space longer and only convert to final WD loop bytes at the later authoring stage
- sample pitch and region tuning are now carried separately much longer in the MIDI/SF2 rebuild path, instead of being folded together early into a single provisional authored pitch value
- PSX loop handling now prefers real ADPCM loop markers when available, following the same general loop-info-prioritizing idea used by `VGMTrans`

### Fixed

- authored WD pitch now stays closer to the `VGMTrans`/SquarePS2 model because sample-side tuning and region-side tuning remain separate until the final WD pitch write
- loop-aware authored samples now emit explicit PSX loop-start markers, which gives the converter and diagnostics a more reliable sample-side loop source than WD region fields alone
- template loop detection now respects sample-side PSX loop information more directly, instead of relying only on region loop bytes and playback flags

## v0.6.71 - 2026-04-08

### Changed

- MIDI/SF2 import now preserves native SoundFont sample rates instead of forcing early 44100 Hz PCM normalization
- KH2 pitch is now treated as tuning first: WD UnityKey/FineTune carries the sample-rate compensation, and PCM resampling is reserved mainly for explicit authored WD size-guard cases
- WD fine-tune encode/decode now follows the SquarePS2/VGMTrans non-linear tuning table instead of the older linear approximation

### Fixed

- non-44100 Hz SoundFont material no longer depends as heavily on early sample-speed changes just to stay in key, which reduces the low-note pitch-jump behavior on short or sensitive samples
- unnecessary UnityKey/FineTune rewrites are now closer to real KH2/SquarePS2 behavior because authored tuning uses the same fine-tune curve family that VGMTrans decodes

## v0.6.70 - 2026-04-07

### Changed

- MIDI/SF2 authored regions now keep a single canonical internal `UnityKey/FineTune` representation, matching the final WD encoding model instead of carrying temporary fine-tune values outside the WD `-50..50` cent range

### Fixed

- tiny residual pitch offsets no longer trigger unnecessary MIDI/SF2 retuning as aggressively, which helps avoid random down-pitched instruments caused by avoidable `UnityKey/FineTune` rewrites
- the MIDI/SF2 manifest now records final encoded pitch and pitch-quantization diagnostics per region, making RootKey/FineTune regressions much easier to spot

## v0.6.69 - 2026-04-07

### Fixed

- restored the simpler `v0.6.67`-style normalized loop-end trimming for MIDI/SF2 imports after the newer seam-search path caused audible regressions on `152`-style material
- restored the older short-loop pitch-compensation flow by removing the extra loop-start seam rewrite that could suppress the fine-tune corrections needed by short looped samples
- current `152` rebuilds now stay much closer to the good `v0.6.67` loop/pitch behavior while still keeping the newer ADSR modes, diagnostics, one-shot ADPCM flag fix, and the stabilized `188` metadata fix

## v0.6.68 - 2026-04-07

### Added

- new `adsr=auto|authored|template` config option for the MIDI/SF2 workflow

### Changed

- `adsr=authored` is now the default MIDI/SF2 ADSR mode
- authored MIDI/SF2 ADSR now fits PS2 envelopes against the same `PSXSPU` / `RateTable` model used by `VGMTrans`, instead of relying only on the older local heuristic profile search
- `adsr=auto` keeps the hybrid policy, `adsr=authored` forces the VGMTrans-style authored ADSR path, and `adsr=template` forces template WD ADSR wherever a template match exists

### Fixed

- real one-shot authored samples no longer keep artificial ADPCM loop flags, which makes loop diagnostics and exported tooling output much closer to the intended KH2 bank behavior
- rebuilt `188`-style authored WD regions now preserve the hidden first/last-region byte correctly, restoring the good backup-identical `wave0188.wd` result on the stabilized authored path
- current `152` / `188` MIDI+SF2 ADSR behavior is much closer to the expected sound after combining stricter envelope policy routing with the new VGMTrans-style authored ADSR fit

## v0.6.67 - 2026-04-07

### Added

- new `midi_pitch_bend_workaround=0|1` config option for the MIDI/SF2 workflow
- new `midi_program_compaction=auto|compact|preserve` config option so sparse WD gaps can be preserved or removed explicitly during MIDI/SF2 rebuilds

### Changed

- when `midi_pitch_bend_workaround=0`, the converter no longer approximates pitch bend by retargeting notes or generating tuned instrument variants
- in that mode, pitch bend events are simply ignored, which is useful for comparing bank layout and sound without extra bend-driven instrument cloning
- `midi_program_compaction=compact` now forces dense instrument renumbering even when the default heuristic would otherwise preserve sparse/original-style WD slots

## v0.6.66 - 2026-04-07

### Changed

- normalized looping SoundFont samples now pull the loop end back to a clean PSX-ADPCM block boundary when possible, instead of always letting the rebuilt loop end fall on a partial tail block
- `sf2_auto_lowpass` is no longer enabled by default; it remains available as an opt-in knob, but the new loop-end stabilization is now the main fix path for glitchy normalized loops

## v0.6.65 - 2026-04-07

### Added

- new `sf2_pre_eq`, `sf2_pre_lowpass_hz`, and `sf2_auto_lowpass` config options for the MIDI/SF2 workflow

### Changed

- imported SoundFont samples can now receive the same gentle pre-conditioning concept that already existed on the WAV path, but applied after `44100 Hz` normalization
- by default, `sf2_auto_lowpass=1` now filters normalized non-`44100 Hz` SoundFont samples near their original bandwidth to reduce “empty” upscaled high-frequency noise

## v0.6.64 - 2026-04-07

### Changed

- SoundFont import now normalizes non-`44100 Hz` sample data to the PS2 target rate during import instead of relying only on root-key / fine-tune sample-rate compensation
- this is intended to reduce pitch drift and timbre instability on banks whose raw SF2 sample headers use rates such as `32000` or `32768`

## v0.6.63 - 2026-04-07

### Added

- new `sf2_bank_mode=used|full` config option for the MIDI/SF2 workflow
- `sf2_bank_mode=full` authors the full SoundFont bank into the rebuilt `WD`, including presets that are not referenced by the current MIDI

### Changed

- log output now says `converted but unused` instead of the misleading old `preserved` wording for authored SoundFont presets that are not referenced by the current MIDI
- in `sf2_bank_mode=full`, pitch-variant instrument cloning is disabled and program compaction stays off so the rebuilt `WD` remains closer to original-style program layout for pairing with existing `BGM` files
- in `sf2_bank_mode=full`, authored regions prefer the SoundFont-derived ADSR instead of defaulting back to template WD ADSRs

## v0.6.62 - 2026-04-07

### Changed

- authored `BGM` rebuilds no longer pad every track back up to the original template slot length
- the converter now keeps the track-slot structure and per-track size table, but each authored track only occupies its real encoded byte length
- this removes large runs of unnecessary `00` padding from looped and compact authored `BGM`s and reduces file size without relying on MIDI-side note thinning

## v0.6.61 - 2026-04-07

### Changed

- added a hard authored `BGM` size guard at `48900` bytes for the MIDI/SF2 workflow, mirroring the existing hard `WD` size guard
- if a MIDI rebuild would exceed that cap, the tool now stops with a clearer error that explicitly says the failure is on the `BGM` / sequence-density side and suggests practical ways to simplify the MIDI

## v0.6.60 - 2026-04-07

### Fixed

- authored BGM playback tracks now emit safer explicit startup controller state by default when volume or expression are otherwise missing from the MIDI-derived event stream
- the first authored note-on and note-off in each playback track are now always written in explicit form instead of relying on KH2 to share implicit previous key/velocity state with the converter's renderer assumptions

## v0.6.59 - 2026-04-07

### Fixed

- when an authored instrument has no exact original WD instrument slot to borrow metadata from, the writer now picks the best-matching template region from the whole original bank instead of blindly reusing region 0
- this keeps hidden KH2 region bytes much closer to plausible original values for remapped SoundFont presets such as sparse GM-style programs

## v0.6.58 - 2026-04-06

### Fixed

- compacted/authored WD instruments now keep track of the correct original template instrument slot when copying KH2 region metadata, instead of reusing the compacted destination index as the template source
- this keeps hidden region bytes closer to the original bank structure even when MIDI programs are remapped into a denser authored PS2 instrument table

## v0.6.57 - 2026-04-06

### Changed

- when a MIDI preset has to fall back to a different SoundFont preset, bend-aware pitch-variant instrument cloning is now disabled for that build so the authored KH2 bank stays simpler and more compatible
- fallback-resolved MIDI presets now get their own authored PS2 instrument slot instead of aliasing directly onto the resolved preset's slot, which keeps bank/program mappings unambiguous inside the rebuilt WD/BGM pair

## v0.6.56 - 2026-04-06

### Changed

- narrowed the sparse-program preservation path so it now keeps original-style program indices only for presets actually referenced by the MIDI, instead of authoring the entire SoundFont bank when bend-aware pitch variants are active

## v0.6.55 - 2026-04-06

### Changed

- disabled sparse-program compaction whenever bend-aware pitch-variant instruments are needed, so those cases keep more stable original-style program indices instead of being remapped into a dense compact bank

## v0.6.54 - 2026-04-06

### Fixed

- `midi_loop=1` now preserves the original KH2 `BGM` slot layout instead of compacting the authored looped `BGM`
- authored looped `BGM`s now keep the original header byte at `0x09` (`0x05` in KH2 templates) while updating only the real track-count byte at `0x08`
- loop markers now follow the original KH2 pattern more closely by writing `Loop Begin` / `Loop End` only on the first authored playback track
- fixed the loop-end byte order so the authored track now ends as `... delta 03 00 00` instead of accidentally terminating the track before the `Loop End` opcode

## v0.6.53 - 2026-04-06

### Fixed

- `midi_loop=1` now writes the chosen loop range onto all authored playback tracks instead of only the first playback track, which is much closer to the multi-track KH2 loop behavior needed ingame

## v0.6.52 - 2026-04-06

### Fixed

- `midi_loop=1` now prefers explicit loop markers from the source MIDI itself before writing KH2 loop markers into the authored `BGM`
- supported explicit MIDI loop markers now include common text markers such as `loopstart` / `loopend` and control-change markers `CC111` / `CC110`
- when no explicit MIDI loop markers exist, the authored `BGM` now falls back to a simple start-to-end loop instead of reusing the template loop from the original KH2 `BGM`
- loop-begin markers are now written at the first event at or after the loop start tick, not only at the first note-on event

## v0.6.51 - 2026-04-06

### Added

- a new `BGMInfo vgmtransdiff <InputMid> [InputSf2]` diagnostics command that rebuilds the current MIDI+SF2 pair, roundtrips the authored `BGM + WD` through `vgmtrans-cli`, and writes a structured JSON comparison report for source versus roundtrip MIDI/SF2 data
- a matching `BGMVgmTransDiff.bat` helper in the tool package for drag-and-drop diagnostics

## v0.6.50 - 2026-04-06

### Fixed

- the fast-attack envelope override is now limited to the small single-preset piano-style SF2 case, so larger older `152` banks keep their varied template WD ADSRs instead of collapsing back to generic `000F/5FC0` envelopes that can reintroduce note echo

## v0.6.49 - 2026-04-06

### Fixed

- short-loop pitch compensation is now re-enabled whenever a MIDI+SF2 bank contains genuinely very short looping samples, even if the overall bank is larger, which restores the earlier anti-echo fix for larger `152`-style instrument sets

## v0.6.48 - 2026-04-06

### Fixed

- very fast MIDI+SF2 attack envelopes now keep their authored PS2 ADSR instead of always being overwritten by the template WD envelope, which restores the sharper piano transient needed by the current `152` test bank

## v0.6.47 - 2026-04-06

### Fixed

- mirrored left/right mono SoundFont zones that still resist stereo pairing are now mixed down to a single centered authored region in the MIDI+SF2 path, which provides a reliable fallback for banks like the current `152` piano set that otherwise sound hard-left in KH2

## v0.6.46 - 2026-04-06

### Fixed

- pseudo-stereo pairing in the MIDI+SF2 authoring pass now uses a much looser match, so left/right mono piano layers with slightly different loop or envelope metadata can still collapse into one authored stereo region instead of staying hard-left

## v0.6.45 - 2026-04-06

### Fixed

- pseudo-stereo left/right SoundFont zone pairs are now also collapsed during the actual MIDI+SF2 authoring pass, so panned mono piano layers like the current `152` case no longer stay as competing separate WD regions

## v0.6.44 - 2026-04-06

### Fixed

- SoundFont region normalization now collapses mirrored left/right mono zone pairs into a single authored stereo region when the source SF2 uses panned mono layers instead of linked stereo samples
- this directly targets cases like the new `152` piano bank where KH2 would otherwise pick the left zone and sound heavily left-panned in-game

## v0.6.43 - 2026-04-06

### Fixed

- MIDI tracks without an explicit pan controller now emit a centered pan by default instead of relying on KH2 playback defaults
- very small imported SF2 attack values are now clamped to a hard attack when authoring PS2 ADSR, which helps sharp piano-style sounds keep their bite

## v0.6.42 - 2026-04-06

### Fixed

- one-shot `MIDI + SF2` rebuilds (`midi_loop=0`) now use a larger but still bounded expanded-`BGM` safety cap, so denser non-looping songs like the newer `152` case can build successfully

## v0.6.41 - 2026-04-06

### Fixed

- the conservative expanded-`BGM` size cap was raised for denser MIDI imports, so larger but still reasonable `MIDI + SF2` rebuilds do not fail prematurely
- this helps newer `152`-style MIDI/SF2 combos author successfully when the `WD` path already resolves correctly

## v0.6.40 - 2026-04-06

### Fixed

- MIDI + SF2 conversion now resolves missing SoundFont presets per requested bank/program instead of immediately falling back to the original WD
- percussion presets such as missing `128/x` drum kits now prefer a bank `128` fallback before dropping to melodic bank `0`, which helps newer MIDI/SF2 combos author successfully

## v0.6.39 - 2026-04-06

### Fixed

- removed the forced note-off injection right before the MIDI loop end because it prevented KH2-style looping in practice
- looped MIDI rebuilds still constrain all playback tracks to the template loop window, but now leave the loop end marker sequence closer to the original KH2 pattern

## v0.6.38 - 2026-04-06

### Fixed

- looped MIDI rebuilds now trim every authored playback track to the same template loop window instead of only constraining the first playback track
- active notes are force-released at the loop end tick before the sequence restarts, which helps prevent stuck notes and canon-like overlap on loop

## v0.6.37 - 2026-04-06

### Fixed

- `midi_loop` now places the authored loop end on the original KH2 template end tick instead of always writing it at the last emitted event
- playback events at or beyond the template loop end are trimmed so the generated `BGM` follows the original loop window more faithfully

## v0.6.36 - 2026-04-06

### Fixed

- `midi_loop` now reuses the original KH2 template loop placement instead of guessing from the first note
- the loop writer now targets the same leading playback track pattern seen in the original `152` and `188` BGM files

## v0.6.35 - 2026-04-06

### Fixed

- `midi_loop` now writes a real KH2-style loop pair with `0x02` for loop begin and `0x03` for loop end
- loop markers are now emitted on all authored playback tracks instead of only writing a partial end marker to a single track

## v0.6.34 - 2026-04-05

### Added

- new `midi_loop` config option for the MIDI + SF2 workflow
- when enabled, the authored BGM writes a KH2-style loop marker on the first playback track instead of ending as a one-shot sequence

## v0.6.33 - 2026-04-05

### Fixed

- pitch-bend fallback now uses fine-tuned instrument variants per bend-active preset instead of only jumping between semitone-rounded note keys
- only presets that are actually used on pitch-bend channels get these tuned variants, so larger banks are not inflated unnecessarily

## v0.6.32 - 2026-04-05

### Fixed

- short-loop pitch compensation is now only enabled for small simple SF2 banks instead of being applied to larger complex multi-region banks
- this keeps tiny waveform-style sets stable without degrading larger KH2-style arrangements

## v0.6.31 - 2026-04-05

### Fixed

- SF2 pitch compensation now picks an adaptive reference sample-rate per SoundFont instead of forcing all files through the same base rate
- this keeps large KH2-style banks near their native ~32768 Hz behavior while still allowing smaller 32000 Hz SoundFonts to stay correct

## v0.6.30 - 2026-04-05

### Fixed

- very short looping SF2 samples now have their loop period re-aligned to PSX ADPCM block boundaries, and the resulting pitch change is compensated back into WD root-note tuning
- this especially improves tiny waveform-style loops whose apparent pitch was previously being skewed by 28-sample block quantization

## v0.6.29 - 2026-04-05

### Fixed

- SF2 sample-rate pitch compensation now targets a 32 kHz KH2-style base instead of 44.1 kHz, which avoids over-transposing authored WD instruments built from 32 kHz SoundFont samples

## v0.6.28 - 2026-04-05

### Fixed

- sparse MIDI program layouts are now compacted into dense PS2 WD instrument indices instead of creating huge hole-filled instrument tables
- this especially improves SF2 sets that use scattered program numbers such as `27`, `28`, `31`, `83`, `89`, `93`, `104`

## v0.6.27 - 2026-04-05

### Fixed

- authored `MID + SF2 -> WD` output is now capped to a hard maximum of 980 KB
- when an authored WD would exceed the cap, the converter now proportionally reduces the effective sample-rate budget and compensates pitch in the region tuning so the file stays compatible instead of writing an oversized WD

## v0.6.26 - 2026-04-05

### Fixed

- MIDI channel volume and expression now use separate, softer attenuation curves so quieter backing layers keep more body and low-end instead of collapsing too far toward silence
- SF2-driven mixes preserve more bass and backing presence while still keeping secondary layers under control on the PS2 path

## v0.6.25 - 2026-04-05

### Fixed

- looping instrument samples no longer insert duplicated PCM at the loop point just to force block alignment
- PSX ADPCM encoding now uses loop-aware lookahead across the loop boundary, which improves short sustained instrument loops and reduces rattling or buzzy loop artifacts

## v0.6.24 - 2026-04-05

### Fixed

- MIDI pitch-bend is now approximated during `MID + SF2 -> BGM + WD` conversion by bend-aware note retargeting with sustain-pedal support
- the converter now honors the standard MIDI pitch-bend range RPN when it is present, instead of treating all pitch-bend movement as fully unsupported

## v0.6.23 - 2026-04-05

### Fixed

- MIDI/SF2 WD authoring now falls back to the best matching original template region per instrument when an exact region-layout match is not available
- improved ADSR reuse for cases like `music152`, where a partial region-layout mismatch previously caused too many authored regions to fall back to the generic envelope

## v0.6.22 - 2026-04-05

### Fixed

- MIDI/SF2 WD authoring now reuses the original PS2 ADSR values from the template `waveXXXX.wd` whenever the authored region layout matches the original region layout
- improved sustained instrument fidelity for roundtrip-heavy cases like `music152`, where the original KH2 envelope shape is a better match than the generic SF2-to-PS2 ADSR approximation

## v0.6.21 - 2026-04-05

### Fixed

- MIDI/SF2 looping samples are now prepared so their PS2 loop start lands on a clean ADPCM block boundary without simply truncating the original loop point downward
- improved fidelity for short looping instrument samples where even a small loop-start shift could noticeably change the sustained timbre after `SF2 -> WD -> SF2`

## v0.6.20 - 2026-04-05

### Changed

- MIDI/SF2 conversion now uses its own `sf2_volume` config key instead of reusing the global WAV `volume` setting

### Fixed

- prevented WAV-specific gain settings such as `volume=3.0` from unintentionally coloring `SF2 -> WD -> SF2` roundtrip tests and reducing fidelity for cases like `music152`

## v0.6.19 - 2026-04-05

### Changed

- MIDI/SF2 conversion now writes a compact PS2 `BGM` containing only the conductor plus the actually generated playback tracks, instead of preserving every original silent track slot
- generated MIDI playback tracks now stay in stable authored order in the compact `BGM` layout instead of being indirectly shaped by the original slot table

### Fixed

- improved `MID -> BGM -> MID` roundtrip fidelity for cases like `music152`, where VGMTrans should now see a track count much closer to the input MIDI instead of an inflated 22-track export caused by preserved silent KH2 slots

## v0.6.18 - 2026-04-05

### Changed

- MIDI/SF2 conversion now authors the full SoundFont preset bank into the PS2 `WD` instead of only the presets directly used by the MIDI sequence

### Fixed

- improved `SF2 -> WD -> SF2` roundtrip fidelity for cases like `music152`, where VGMTrans should see the full authored bank rather than a minimal subset of only the active presets

## v0.6.17 - 2026-04-05

### Fixed

- corrected authored `WD` files for sparse MIDI/SF2 program usage, so conversions that only reference a higher program number like `program 5` now write a valid KH2 instrument table instead of truncating the instrument count

## v0.6.16 - 2026-04-05

### Changed

- MIDI/SF2 program mapping now preserves original program numbers as WD instrument indices whenever possible instead of compacting presets into a reordered instrument list
- authored and parsed WD regions now carry velocity range metadata so future SF2 conversions can preserve velocity splits more faithfully when the source SoundFont actually uses them

### Fixed

- corrected cases where MIDI/SF2 conversions could sound like instruments were shuffled because program `n` was authored into a different WD instrument index than expected
- improved internal WD region parsing/rendering consistency for authored velocity-aware regions

## v0.6.15 - 2026-04-05

### Changed

- authored `WD` sample offsets now point to the KH2-style 16-byte zero lead-in that belongs to the next sample chunk instead of grouping that padding with the previous sample

### Fixed

- aligned multi-sample authored `WD` layout more closely with original KH2 sample spacing semantics
- adjusted replacement-sample loop offsets so they stay correct when stored with the KH2-style 16-byte zero lead-in

## v0.6.14 - 2026-04-05

### Changed

- authored PS2 ADPCM blocks now follow the KH2-style flag pattern more closely: `02` throughout the sample stream and `03` on the final block before the 16-byte separator
- fully silent authored blocks are now emitted as KH2-style `0C 02` silent blocks inside the sample stream

### Fixed

- corrected cases where long silent regions inside authored samples appeared as raw zero-filled blocks instead of valid KH2-style silent sample blocks
- stopped relying on the old internal assumption that ADPCM flag byte `02` directly means a logical loop

## v0.6.13 - 2026-04-05

### Changed

- empty or fully silent authored PS2 ADPCM blocks are now written as KH2-style `0C 02` silent blocks instead of raw zero-filled blocks inside the sample stream

### Fixed

- improved compatibility with KH2-style sample layout expectations in long silent sections inside authored `WD` samples
- reduced cases where large silent authored sample regions looked like detached raw zero data instead of valid PS2 sample blocks

## v0.6.12 - 2026-04-05

### Changed

- authored multi-sample PS2 `WD` files now insert KH2-style 16-byte zero separators between sample chunks instead of packing all samples directly back-to-back

### Fixed

- improved layout compatibility of authored `WD` files with tools and workflows that expect the original KH2 sample spacing convention

## v0.6.11 - 2026-04-05

### Changed

- added optional pre-encode WAV conditioning controls for the `replacewav` path: `pre_eq` and `pre_lowpass_hz`

### Fixed

- made it possible to tame brittle, metallic, or overly harsh long-song replacements before PS2 ADPCM encoding without affecting the MIDI/SF2 path

## v0.6.10 - 2026-04-05

### Changed

- the `MIDI + SF2 -> BGM + WD` path now keeps the conservative original-slot rebuild, but can expand individual PS2 `BGM` track slots by a limited safe amount when a sequence only barely exceeds the original slot sizes

### Fixed

- restored previously working MIDI replacements that started failing after the strict `v0.6.9` slot-fit check
- reduced redundant MIDI controller and program events before PS2 track authoring so more sequences fit without hitting the safety limit

## v0.6.9 - 2026-04-05

### Changed

- the `MIDI + SF2 -> BGM + WD` path now writes MIDI sequences back into the original PS2 `BGM` slot layout instead of emitting arbitrarily large replacement `BGM` files

### Fixed

- prevented crash-prone oversized MIDI rebuilds by rejecting sequences that do not fit safely into the original PS2 `BGM` track slots
- reduced MIDI track size by using more compact KH2 note opcodes when key and velocity state can be reused

## v0.6.8 - 2026-04-04

### Changed

- improved the `WAV -> BGM + WD` replacement path with a better downsampling chain, a more phase-robust mono downmix, and a higher-quality PSX ADPCM block search

### Fixed

- reduced the thin, metallic, or brittle character that could appear in long music replacements when they had to fit a tight original PS2 `WD` budget

## v0.6.7 - 2026-04-04

### Changed

- `replacemidi` now falls back to the original `waveXXXX.wd` when no usable `.sf2` is available for the MIDI

### Fixed

- prevented failed MIDI rebuilds when the selected or auto-detected `.sf2` is missing required presets but the original PS2 `WD` can still be used directly

## v0.6.6 - 2026-04-04

### Changed

- the `MIDI + SF2 -> BGM + WD` converter now stops with a clear error when the MIDI references SoundFont presets that do not exist in the selected `.sf2`

### Fixed

- prevented silent generation of effectively empty or unusable PS2 outputs when a MIDI/SF2 preset mismatch occurs

## v0.6.5 - 2026-04-04

### Changed

- MIDI `CC7` volume and `CC11` expression are now authored to the PS2 path with a stronger concave attenuation curve instead of being copied linearly

### Fixed

- reduced cases where quieter background channels stayed too far forward in the rebuilt PS2 mix
- reduced cases where hiss-like or noisy secondary layers became too prominent compared to the main arrangement

## v0.6.4 - 2026-04-04

### Changed

- SoundFont `reverb send` is now interpreted during `MIDI + SF2 -> BGM + WD` conversion and used to rebalance overly dry background layers

### Fixed

- removed the stale `Ignored SoundFont generator 16 during conversion` warning from current rebuilds
- reduced cases where reverb-heavy background layers sounded too forward or too dry in the rebuilt PS2 mix

## v0.6.3 - 2026-04-04

### Changed

- SoundFont velocity-zone selection now follows the actual note velocities used by the MIDI instead of always resolving zones around a fixed reference velocity of `100`

### Fixed

- reduced cases where sharper or noisier high-velocity SoundFont layers became too dominant in the rebuilt PS2 mix

## v0.6.2 - 2026-04-04

### Changed

- SoundFont sample-rate differences are now compensated in the authored PS2 root-note tuning instead of being treated like native `44.1 kHz` material

### Fixed

- reduced the global "too high" pitch shift that could happen with lower-rate SF2 source samples
- improved low-end retention for instruments whose original SoundFont samples were authored below `44.1 kHz`

## v0.6.1 - 2026-04-04

### Changed

- SoundFont volume-envelope generators now map into authored PS2 `WD` ADSR values instead of using one fixed fallback envelope for every region
- linked stereo SoundFont samples are now preserved as layered stereo `WD` regions instead of being folded down to mono during conversion

### Fixed

- removed the old false-positive warnings for ignored SoundFont generators `34..38`
- improved source-timbre retention for `MIDI + SF2 -> BGM + WD` rebuilds

## v0.6.0 - 2026-04-04

### Added

- real `MIDI + SF2 -> BGM + WD` conversion path
- `BGMInfo replacemidi <InputMid> [InputSf2]`
- `BGMReplaceMidiSf2.bat`
- conversion manifest output for MIDI/SF2 rebuilds

### Changed

- `BGMPS2Tool` now supports both the legacy WAV workflow and the cleaner MIDI/SF2 workflow
- `volume` from `config.ini` now also applies to SoundFont sample audio before PS2 encoding

### Notes

- the MIDI/SF2 workflow currently ignores advanced SoundFont modulators, filters, and LFO behavior
- MIDI pitch-bend is currently ignored because the KH2 PS2 pitch opcode mapping is still unknown

## v0.5.2 - 2026-04-04

### Added

- `hold_minutes` setting in `config.ini`

### Changed

- loop note hold time can now be adjusted without code changes

## v0.5.1 - 2026-04-04

### Changed

- removed the `size` setting from `config.ini`
- `volume` remains the only supported user-facing configuration option

### Fixed

- prevented incompatible rebuilds caused by manual `WD` size scaling

## v0.5.0 - 2026-04-03

### Added

- `config.ini` support in the tool folder
- `volume` option for scaling imported WAV loudness before PS2 encoding
- `size` option for scaling the rebuilt `WD` sample budget

### Changed

- rebuilt `WD` output size can now be controlled without code changes
- package documentation now explains the new configuration options

### Notes

- `size > 1.0` can improve quality by allowing a larger `waveXXXX.wd`
- `size > 1.0` can also increase compatibility risk because the rebuilt `WD` may exceed the original size

## v0.4.0 - 2026-03-28

### Added

- standalone `BGMPS2Tool` package layout
- drag-and-drop workflow through `BGMReplaceWav.bat`
- English `README.md`
- English `HOWTO.md`
- WAV loop metadata import from:
  - RIFF `smpl`
  - WAV `id3` `TXXX` tags `LoopStart` / `LoopEnd`

### Changed

- improved PS2 compatibility for rebuilt `musicXXX.bgm + waveXXXX.wd`
- preserves original PS2 container structure more conservatively
- keeps rebuilt files in an `output` folder instead of overwriting originals
- improved handling for long replacement songs inside original PS2 memory budgets
- better loop handling for `music188`

### Fixed

- fixed KH2FM freeze/crash cases caused by incompatible replacement builds
- fixed fade-to-silence behavior after a few seconds on rebuilt BGM tracks
- fixed PS2 ADPCM loop flag behavior to better match KH2FM expectations
- fixed WAV loop metadata cases where loop tags are stored in a `44.1 kHz` sample basis even when the WAV itself is `48 kHz`
- fixed early loop restarts on imported replacement tracks

### Notes

- This release is framework-dependent and requires Microsoft `.NET 10` Runtime.
- No external tools such as `ffmpeg`, `SCDInfo`, `MultiEncoder`, or `SingleEncoder` are required for `BGMPS2Tool`.

