# CHANGELOG

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
