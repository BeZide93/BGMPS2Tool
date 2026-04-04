# Pre-Encode EQ Notes

This note explains the two optional `replacewav` settings:

```ini
pre_eq=0.35
pre_lowpass_hz=10000
```

These settings are applied before the WAV is downsampled and encoded to PS2 ADPCM.

## pre_eq

`pre_eq` is a gentle tone-shaping stage.

It does three things at once:

- adds a small low-end boost
- reduces some harsh upper-mid energy
- slightly tames the very top end

The goal is to make long PS2 music replacements sound less thin, metallic, or brittle after heavy downsampling and ADPCM encoding.

### Practical effect

- lower values keep the sound closer to the original WAV
- higher values make the sound warmer and softer
- very high values can make the result too dull

Suggested range:

- `0.20` to `0.50`

## pre_lowpass_hz

`pre_lowpass_hz` applies an additional low-pass filter before PS2 encoding.

This reduces very high frequencies before the audio is compressed into the PS2 format.

That can help reduce:

- hiss
- harshness
- metallic or "shimmery" artifacts

### Practical effect

- higher values keep more brightness
- lower values remove more harsh high-end content
- values that are too low can make the result sound muffled

Suggested range:

- `9000` to `12000`

Use `0` to disable the extra low-pass stage.

## How they work together

- `pre_eq` changes the overall tone balance
- `pre_lowpass_hz` removes extra top-end energy before encoding

In practice:

- `pre_eq` helps when the result sounds thin or metallic
- `pre_lowpass_hz` helps when the result sounds too sharp or hissy

## Recommended starting point

```ini
pre_eq=0.35
pre_lowpass_hz=10000
```

This is a balanced test setting for harsh or metallic `replacewav` results.

## If the result is still not right

If the output is still too metallic:

```ini
pre_eq=0.45
pre_lowpass_hz=9000
```

If the output becomes too dull:

```ini
pre_eq=0.20
pre_lowpass_hz=11000
```

## Important note

If both values are disabled:

```ini
pre_eq=0.0
pre_lowpass_hz=0
```

then no extra pre-filtering is applied, and the WAV path behaves the same as before this specific pre-EQ/low-pass feature was added.
