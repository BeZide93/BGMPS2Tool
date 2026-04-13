# TODO

## Current Priority

- `[Loops]` Validate `sf2_loop_policy=safe` as the new default on the main `152` and `188` test families.
  The default path now uses the patched `v0.9.2` loop preparation plus `sf2_loop_start_content_align=1`, so non-28-aligned SF2 loop bodies are moved onto the WD loop-start block instead of looping earlier pre-loop material.

- `[Loops]` A/B test `sf2_loop_policy=advanced`, `sf2_loop_policy=auto-loop`, and `sf2_loop_policy=advanced-auto-loop` only when the safe path still has a concrete loop problem.
  `advanced` keeps the recent decoded-ADPCM scoring and adaptive feedback experiments available, `auto-loop` ignores imported SF2 loop points and searches from the end, while `advanced-auto-loop` searches near the original SF2 loop window. Treat these as diagnostic/experimental modes until more listening tests pass.

- `[SF2+WD]` Improve exact SoundFont region triggering, especially drum/percussion key-gap behavior.
  The next safe fidelity target is to avoid authoring missing drum keys from nearest neighbor regions unless a compatibility policy explicitly asks for that.

- `[SF2+WD]` Improve static SoundFont playback semantics for percussion and region selection.
  Important remaining items are `exclusiveClass`, `keynum`, `velocity`, stereo/dual drum regions, and other static note-on behavior that affects in-game drum accuracy.

- `[MIDI+BGM]` Implement real native KH2 pitch bend instead of the current approximation.
  Right now pitch bend is handled by bend-aware note retargeting and tuned instrument variants, which works better than ignoring bends but is still not a true format-level solution.

- `[SF2+WD]` Improve SoundFont fidelity for modulators, filters, and LFO-style behavior.
  The converter already handles the core bank data well enough for many cases, but detailed SoundFont behavior is still simplified or ignored.

- `[SF2+WD]` Improve stereo fidelity.
  Some problematic banks currently need compatibility fallbacks such as pseudo-stereo collapse or centered mono authoring. This is useful for KH2 playback, but it is not always a perfect match to the original SoundFont.

## Known Heuristics

- `[Template Matching]` Region authoring still relies on best-match template selection against the original WD.
  This is practical and often works well, but it is still a heuristic rather than a fully proven reconstruction of KH2 authoring rules.

- `[Envelope Mapping]` SF2-to-PS2 ADSR conversion is still partly heuristic.
  The tool can now preserve or reuse template ADSR data in important cases, but there is still no fully general one-to-one envelope model.

- `[Stereo Fallbacks]` Pseudo-stereo handling is still heuristic.
  Some left/right mono SoundFont layouts are collapsed or downmixed in order to avoid obviously wrong KH2 playback, but this is still a compatibility strategy, not a guaranteed universal translation.

- `[Dynamics]` Volume and expression shaping are still tuned heuristically.
  These curves were refined through testing and are useful, but they are not yet backed by a fully understood native KH2 mixer model.

- `[Loop Preparation]` The default safe path is intentionally conservative, while `advanced`, `auto-loop`, and explicit loop-tail tests remain heuristic.
  `safe` is closer to the patched v0.9.2 behavior and now uses config-gated loop-start content alignment by default. `advanced` uses stateful decoded PSX-ADPCM wrap scoring, `auto-loop` searches fresh 28-sample-aligned loop points, `advanced-auto-loop` searches close to the original SF2 loop window, and `sf2_loop_end_content_align=1` remains off by default as a loop-end block-alignment test. None of these is a fully proven Square/KH2 sample-authoring model yet.

- `[ADPCM Loop Scoring]` The loop-state-aware/adaptive-feedback path is now gated behind `sf2_loop_policy=advanced`.
  This keeps normal rebuilds on the quieter safe path. If advanced mode helps a specific sample, compare manifests before promoting any part of it back into the default path.

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

- `[Loop Encoder]` Add deeper Square-style loop encoder research only after more listening tests.
  The current looped-sample encoder now has a small loop-state pass, but it is still not a verified VGMTrans/Square encoder clone.

- `[Diagnostics]` Add per-region roundtrip mismatch summaries for SF2 source, authored WD, and VGMTrans-exported SF2.
  The ideal report would show region mapping, ADSR, pitch, loop start/end, decoded loop RMS, sample length, and ignored/approximated SoundFont behavior side by side.

- `[Diagnostics]` Add a stronger comparison/debug mode for `SF2 -> WD` and `MIDI -> BGM`.
  The most useful next diagnostic feature would be a manifest or diff mode that explicitly reports where the authored result diverges from the source bank or sequence.

- `[Docs]` Document the current reliability boundaries clearly.
  The README should eventually distinguish between robust workflows, heuristic workflows, and experimental workflows so users know what to trust and where to expect edge cases.
