# CHANGELOG

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
