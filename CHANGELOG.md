# Changelog

## 0.2.2 - 2026-09-21

Screen reading fixes, every one found by checking a capture against the car in game.

### Lights the detector missed or split

- Read a light that blows out to white through its middle as one light. Colour survives only at its
  edges, so PMR's R8 LMS GT4 Evo was read as 19 lights instead of 10, its green named cyan, and its
  limiter flash missed. Lights are separated by dark housing, so this cannot join two of them.
- Find a light whose blown-out centre keeps its colour. PMR's Corvette C7.R sits its top lights on a
  violet backing that joins them into one run too wide to keep, and their centres are white in two
  channels only, so a frame with nine lights lit was read as seven.
- Give back sightings dropped as an indicator where the light stayed lit through the drift. A light
  reads greener alone than with a yellow neighbour lit, and PMR's Ford GTLM GTE lost every sighting of
  lights 2 and 7 over 450 rpm that way, exporting them as 0, which lights them from idle on the wheel.
  Only the switch-on windows use those frames back; colours and the redline still ignore them.

### Colours

- Read a genuine orange LED bank as orange instead of red. The correction that keeps PMR's red, green
  and blue shades from being renamed now only applies when the name is far from the measured hue.
- Let a strongly dominant channel name a colour whatever the hue ladder had to call it. Two shades of
  the same warm red were being named red and orange, which gave PMR's AMG GT4 an orange centre pair.
  A game's orange keeps far more green than a warm red, so it is still read as orange.
- Need several sightings before a light's colour counts as its own. PMR's Corvette C7.R lights 9 and
  10 only come on at the redline, and light 9 took the limiter's purple from a single frame. Both now
  fall back to the redline colour, and the report names them.

### Redline

- Keep a gear that only saw the revs fall out of the limiter from setting its own redline. Leaving the
  limiter reads lower than entering it, which made the wheel flash early in that gear. The report says
  which gears this applied to.
- Measure the redline from crossings with the whole strip lit where there are any. A car whose own red
  is close to its redline red could otherwise read a washed-out frame lower down as the redline, which
  put PMR's Vantage GT4 about 130 rpm early in fifth gear.
- Ignore a pale colour change that is just the strip blowing out into one of its own colours as the
  last lights come on. PMR's MC12 GT1 was given a yellow redline that turned the whole wheel yellow
  where the game changes nothing; it now exports a transparent redline at the limiter.

### Tests and notes

- Add RaceRoom regressions from confirmed drives: the Porsche 911 GT3 Cup (992) orange bank and the
  DMD P21, which keeps its own colours at the limiter. Project Motor Racing regressions cover the
  Vantage GT4, AMG GT4, MC12 GT1, Corvette C7.R, R8 LMS GT4 Evo and Ford GTLM GTE.
- `--filter <text>` runs only the tests whose name contains that text.
- Colours a game blends along a continuous gradient still need a confirmed override on some cars:
  where two bands meet, the light on the boundary measures between them.

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
