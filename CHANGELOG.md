# CHANGELOG

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
