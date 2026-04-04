# CHANGELOG

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
