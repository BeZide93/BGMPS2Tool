# HOWTO

Version: `v0.4.0`

## Goal

Replace a PS2 `KH2FM` music track with your own WAV.

## Requirements

- Windows
- Microsoft `.NET 10` Runtime

No other external tools are required for this package.

## Example

You want to replace:

- `music188.bgm`
- `wave0188.wd`

with your custom song:

- `music188.ps2.wav`

## Folder Layout

Put these files in the same folder:

```text
music188.bgm
wave0188.wd
music188.ps2.wav
```

`BGMPS2Tool` itself can stay in its own folder anywhere on your PC.

## Method 1: Drag And Drop

1. Open the `BGMPS2Tool` folder.
2. Drag `music188.ps2.wav` onto `BGMReplaceWav.bat`.
3. Wait until the process is finished.

The tool will create:

```text
output\music188.bgm
output\wave0188.wd
```

inside the folder where your WAV is stored.

## Method 2: Command Line

Open PowerShell in the `BGMPS2Tool` folder and run:

```powershell
.\BGMInfo.exe replacewav "C:\Path\To\music188.ps2.wav"
```

## Naming Rules

The WAV filename must allow the tool to find the matching PS2 music file.

Recommended names:

- `music188.wav`
- `music188.ps2.wav`
- `music059.wav`
- `music059.ps2.wav`

## Input Rules

Your WAV should be:

- `16-bit PCM`
- a standard `.wav` file

If your WAV comes from a DAW, export it as `16-bit PCM WAV`.

## Optional Loop Metadata

If your WAV includes loop metadata, the tool can import it.

Supported loop sources:

- WAV `smpl` loop chunk
- WAV `id3` `TXXX` tags:
  - `LoopStart`
  - `LoopEnd`

The values must be stored in samples.

If loop metadata is present, the rebuilt PS2 music will try to use that loop instead of playing only once.

## Output Rules

The tool does not overwrite the originals.

It writes the rebuilt files into:

```text
output
```

next to the input WAV.

## Compatibility Notes

- The tool tries to preserve the original PS2 `BGM` and `WD` structure.
- If the original track has a small memory budget, the replacement audio may be resampled to fit.
- Some tracks may end up mono instead of stereo if that is required for PS2 compatibility.
- Always test the files ingame after rebuilding.

## Typical Full Example

Input folder:

```text
D:\KH2\music188\
  music188.bgm
  wave0188.wd
  music188.ps2.wav
```

After processing:

```text
D:\KH2\music188\
  output\
    music188.bgm
    wave0188.wd
```

## Troubleshooting

### `BGMInfo.exe` does not start

Install the Microsoft `.NET 10` Runtime and keep all package files together in the same `BGMPS2Tool` folder.

### "No matching .bgm was found next to the WAV"

Make sure the WAV is in the same folder as the correct `musicXXX.bgm`.

### The game crashes or freezes

Use the files from the `output` folder only.

### The music starts but sounds different from the source WAV

That can happen because PS2 tracks have limited audio memory and the tool may need to reduce the sample rate to fit the original track budget.
