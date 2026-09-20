# Changelog

## 0.2.1 - 2026-09-20

### Capture accuracy

- Recover washed-out LED centres and dim coloured edges while rejecting reflective unlit housings.
- Preserve narrow gaps between LED banks and separate real lights from merged glow positions.
- Keep normal rev-light colours separate from later limiter animations, and avoid inventing an on/off blink for alternating colour phases.
- Preserve supported LED crossings when other positions show indicators or an ambiguous pale background.
- Keep completed crossing bounds when a later climb is unfinished, and use matching simultaneous banks for missing readings where supported.
- Constrain borrowed RPM rows with sustained raw dark evidence or repeatedly observed lit values, reporting these as unmeasured fallbacks.

### Exports

- Add **Top gear for next export** (Auto or 1-12) for cars whose gear count is unavailable. Include unreached gears using the existing fallback rules and reset the choice after a successful export.

### Validation and known limits

- Add raw-capture and image regressions for AC, AC EVO and RaceRoom, alongside the existing regression suite.
- Image and replay checks do not establish live wheel accuracy for every car. Unmeasured fallback values still need an in-game check.
- Alternating limiter colours cannot be represented by the output format; a solid colour or an explicit local blink override remains an approximation.

## 0.2.0 - 2026-09-19

### Capture accuracy

- Align screenshots with timestamped telemetry and reject stale or cross-gear matches.
- Improve display-lag confidence checks and reject poorly measured or inconsistent LED crossings.
- Detect redline flashes that begin with a dark phase without delaying their onset.
- Learn LED heights across curved strips, keeping dashboard logos out of the capture.
- Prevent isolated noise positions from merging adjacent LEDs into false gaps.
- Keep real LEDs beside pale reflections, and export physical gaps as black with zero RPM in every gear.
- Improve mirrored-light timing and handling of lights that switch within the same rounded RPM sample.

### Exports and diagnostics

- Preserve previous RPM rows for gears absent from a new capture.
- Support per-car, per-game color and blink overrides without freezing newly measured RPMs.
- Back up the previous JSON and report before replacing an export; abort if the backup fails.
- Describe final exported values separately from raw measurement diagnostics.
- Write the LMU Lamborghini Iron Lynx 2024 development profile under ATSR's canonical SC63 filename.
- Add optional transition images, with limits on repeated flashes, images per gear and memory use.
- Bound captures at 72,000 frames, reduce idle polling and export an active capture when SimHub closes.

### Validation and known limits

- Expanded real-capture regressions across AMS2, ACC, LMU, PMR, AC and AC EVO.
- Live wheel checks now include selected AC and AC EVO cars. This is not validation of every car.
- RaceRoom, F1 and iRacing still need live validation.
- The SC63 filename mapping is one observed ATSR compatibility case, not a general LMU identity resolver.
- ATSR cannot reproduce every game-specific limiter effect. Independent blink timers can differ in phase.
- Color and blink overrides are local JSON files; there is no editor for them in the plugin page yet.
