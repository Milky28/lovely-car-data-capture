# Changelog

## 0.2.3 - 2026-09-21

### Workflow

- Turn on screen reading automatically when a valid capture box is saved, and show the active source
  and capture/export state at the top of the plugin page, including paused, replay/menu, pit-limiter,
  telemetry-gap and screen-error status.
- Keep a pending capture when the game or car changes, after a failed export, or until shutdown tries
  to export it. The page now offers export, retry and discard actions around that retained capture,
  and disables conflicting actions while exporting.
- Add a session-only **Last export** area with the car, outcome, key warnings, details, and buttons to
  open the report or output folder, independent of the overlay.
- Arrange the main page as Pick, Record, Export and Review, with setup and diagnostic controls folded away.
- Add **Copy JSON + open Builder** for successful car exports. The JSON stays on the clipboard;
  the launch URL supplies the game. Click **Import copied capture** in the browser, with a paste
  fallback if clipboard access is unavailable. The owner verified the deployed Builder handoff.
- Keep stable per-session evidence folders under `<game>/captures/<car>/<timestamp-id>/`, reused by
  retry export, with `capture.json` metadata, outputs, optional raw CSV, `transitions/` and up to
  eight selection/representative crop images within 16 MiB.
- Back up the latest raw frames CSV alongside the latest JSON and report before replacing them.

### Colours

- Read a light where two colours meet while the light above it is still dark. Some games draw the
  strip's glow as one gradient, so beside a lit neighbour the light took on its colour. PMR's
  Corvette C7.R, NSX GT3 Evo 22, MC12 GT1, R8 LMS GT4 Evo, Camaro ZL-1 GT4.R and Corvette C8.R now
  come out as they look in game, with no local overrides.
- Stop warning that colours are too close to tell apart when they end up as the same colour, or when
  a strip's red sits either side of pure red. The note appeared on most cars; it now appears only
  where two different colours really are close.

### Development

- `tools/replay-all.ps1` replays every saved capture and lists what a change did to each car.
- Add regression coverage for capture/export states, retry, retained evidence and the Builder URL contract.

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
