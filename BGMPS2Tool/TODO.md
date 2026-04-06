# TODO

## Current Priority

- `[MIDI+BGM]` Implement real native KH2 pitch bend instead of the current approximation.
  Right now pitch bend is handled by bend-aware note retargeting and tuned instrument variants, which works better than ignoring bends but is still not a true format-level solution.

- `[SF2+WD]` Improve SoundFont fidelity for modulators, filters, and LFO-style behavior.
  The converter already handles the core bank data well enough for many cases, but detailed SoundFont behavior is still simplified or ignored.

- `[SF2+WD]` Improve stereo fidelity.
  Some problematic banks currently need compatibility fallbacks such as pseudo-stereo collapse or centered mono authoring. This is useful for KH2 playback, but it is not always a perfect match to the original SoundFont.

- `[Loops]` Harden short-loop handling for difficult instrument samples.
  Loop behavior is much better than before, but very short looping samples are still one of the main sources of audible edge cases such as rattling, note echo, or timbre drift.

## Known Heuristics

- `[Template Matching]` Region authoring still relies on best-match template selection against the original WD.
  This is practical and often works well, but it is still a heuristic rather than a fully proven reconstruction of KH2 authoring rules.

- `[Envelope Mapping]` SF2-to-PS2 ADSR conversion is still partly heuristic.
  The tool can now preserve or reuse template ADSR data in important cases, but there is still no fully general one-to-one envelope model.

- `[Stereo Fallbacks]` Pseudo-stereo handling is still heuristic.
  Some left/right mono SoundFont layouts are collapsed or downmixed in order to avoid obviously wrong KH2 playback, but this is still a compatibility strategy, not a guaranteed universal translation.

- `[Dynamics]` Volume and expression shaping are still tuned heuristically.
  These curves were refined through testing and are useful, but they are not yet backed by a fully understood native KH2 mixer model.

- `[Loop Preparation]` Short-loop pitch compensation and loop alignment are still heuristic.
  They are effective for several known failure modes, but they are not yet a mathematically exact reproduction of KH2's original sample authoring pipeline.

## Not Yet Universal

- `[Case-Specific Fixes]` Some fixes were developed primarily around the current `152` and `188` test families.
  They are implemented in general code, but they were validated mostly against those problem shapes and should still be considered field-tested rather than universally proven.

- `[Small Piano Case]` Very fast attack preservation is currently tuned around the small single-preset piano-style case.
  This is intentionally narrower now, because applying it too broadly made larger banks sound worse.

- `[Pseudo-Stereo Banks]` Centered mono fallback for mirrored left/right SoundFont zones is not universally ideal.
  It is a good compatibility move for certain KH2 playback issues, but it can reduce width or spatial detail compared to the original SoundFont.

- `[Dense MIDIs]` Extremely dense MIDIs are still constrained by BGM container behavior and safety checks.
  The tool is much better at compact rebuilds than before, but not every dense source MIDI will author into a safe KH2 BGM equally well.

## Later

- `[Diagnostics]` Add a stronger comparison/debug mode for `SF2 -> WD` and `MIDI -> BGM`.
  The most useful next diagnostic feature would be a manifest or diff mode that explicitly reports where the authored result diverges from the source bank or sequence.

- `[Docs]` Document the current reliability boundaries clearly.
  The README should eventually distinguish between robust workflows, heuristic workflows, and experimental workflows so users know what to trust and where to expect edge cases.
