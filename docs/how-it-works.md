# How it works

The detail behind the [quick start](../README.md): where each value comes from, what the plugin
works out from the screen, and how its files are made to suit ATSR.

## What it captures

| Game | LED data | How |
| --- | --- | --- |
| F1 2021–2026 | Real rev lights | Reads the game's 15 rev-light bits and learns the RPM each light switches on, per gear, plus where the redline flash starts. |
| iRacing | Real shift lights | Reads iRacing's first-LED / shift / last-LED / blink RPMs (per gear where the car changes them). LEDs in between are spaced evenly. |
| Everything else | Lights read off the screen | Watches the car's own rev lights through a box you place over them (see *Reading the lights off the screen*). |
| Everything else | Manual marks | You press a button as each in-game light comes on (see *Marking lights by hand*). |
| Everything else, no marks | Estimate only | Uses SimHub's redline; LEDs are spread between two percentages of it. |

**Starting from the repo file:** when the car already exists in Lovely Car Data (looked up by carId
on GitHub, read-only), that file is the starting point. Only values the game actually reported are
replaced; the name, class, colors, LED layout, gaps and any extra fields are kept. For games without
LED data, the repo file's RPMs are left alone and SimHub's redline is listed in the report for comparison.

**Continuing a previous capture:** gears absent from this drive keep their RPM rows from the previous
export for this car and game, including their redlines. The car ID, LED count and gaps must match.
Newly captured gears still follow the normal measurement rules. Screen capture's
`CopyMeasuredToOtherGears` setting takes precedence when enabled. Confirmed colors and blink timing
are kept separately in a local overrides file; see the [reference](reference.md).

Special cases the report calls out:

- **F1 measurements too loose to use:** thresholds that fail the confidence check are reported but
  don't replace repo values. For a new car those LEDs stay at 0 until a slower sweep measures them.
- **F2 and other 10-LED F1 files:** the game reports 15 lights, so the file's RPMs aren't touched; the measured table is in the report to map by hand.
- **LMU:** repo files are generated from templates, so the values belong in `src_data/lmu`.
- LEDs never lit in a gear, gears not driven, and a redline flash that was never reached.

## Reading the lights off the screen

Most games don't report their rev lights, but they do draw them, so the lights can be read out of the
picture: bright saturated dots on a dark wheel, which nothing else in a cockpit looks like. Pairing
what’s lit with the RPM from telemetry at that moment gives the same thresholds the F1 games hand
over directly.

Telemetry arrivals and screen copies use the same monotonic clock. Each picture is paired with
the RPM at the estimated midpoint of its desktop copy, interpolating between nearby telemetry
updates in the same gear when available. If the next update has not arrived, a recent preceding
value is used; stale values and crossings between gears or captures are rejected. Image analysis
time is excluded from the picture's timestamp. This still measures the composited desktop, not
the game's original presentation time, so the display-delay correction below remains necessary.

The [quick start](../README.md#your-first-capture) places the box on a still of the screen with
**Pick the lights…**, which works in every game. **Adjust live…** puts a frame over the running game
instead, reading the lights as you rev. With the
game in front it keeps the keyboard, so it uses system-wide shortcuts: **Ctrl+Alt+arrows** move the
frame, **Ctrl+Alt+Shift+arrows** resize it, **Ctrl+Alt+Enter** finishes. The box is saved as you move
it and *Undo changes* puts it back. Some games, ACC among them, keep even those keys and the pointer to
themselves; use the still there. `LovelyCarDataCapture.ShowCaptureBox` opens the frame from a button.
When you keep a box from **Pick the lights…**, its selected pixels are saved as `capture-box.png` in
the export folder for debugging. Picking another box replaces that image.

**A game in borderless mode hides the mouse pointer and takes the keyboard while it has focus.** That's
why the frame has system-wide shortcuts and saves itself as it moves, and why its reading stays live
while the game is in front: the border sits just outside the region being read, so nothing has to be
hidden to take a reading. Alt-tab brings the pointer back if you want it.

**What it works out** from a few sweeps:

- **How many lights there are and where the gaps are**, from the spacing between them. A wider space
  than the rest is a slot that never lights, and the car file needs those slots too.
  Positions need repeated sightings before nearby samples can join them. This keeps occasional
  dashboard or wheel obstructions from linking adjacent LEDs into one group and creating false gaps.
- **The RPM each light switches on at.** Every climb gives a window between the last frame the light
  was dark and the first it was lit; the value is the middle of that window, and the median across
  climbs, so one bad frame doesn't move it. Once a light switches on, missed lit frames higher in the
  same climb cannot create a second threshold; it is measured again only after the revs fall below
  its switch-on RPM. A light only ever seen already lit, or pinned down no
  better than 150 rpm, is reported instead of written: braking mid-sweep costs you that gear, not the
  capture. Repeated climbs must also have narrow individual crossing windows: repeating the same
  wide window does not turn its midpoint into a precise measurement.
- **Whether the car uses one set of lights for every gear.** Gears that agree within 80 rpm wherever
  they measured the same light are pooled, and the gears a track gives no room to sweep follow them. A
  single gear far from the rest (neutral and first climb fastest, so measure worst) is left out and
  named. The redline is measured per gear, since some cars move it with the gear.
  For a new car whose gears disagree, a light missed in one gear uses the same light's measured RPM
  from other gears rather than an always-on zero. The report identifies those filled values.
  If a light has imperfect switch-on windows in several gears but their measured onsets agree, a new
  car uses their middle value and flags it for an in-game check.
  Readings from driven gears take priority over neutral when both measured the same light. When every
  left/right pair is the same color and switches within 30 rpm, pairs use their earlier reading so
  ATSR lights both sides together. If the game does not report its gear count, you can set the known
  top gear for the next export; unvisited higher gears use fallback values and are listed in the file.
- **How far behind the revs the game draws its lights**, in milliseconds: the delay that makes each
  light's switching on (rising revs) and off (falling revs) agree. Every frame is read against the revs
  that long before it, which cancels a game's fade (ACC) as well as its render lag. If the two still
  disagree widely after fitting, the delay is rejected rather than shifting every threshold. The
  fit measures the size of the disagreements so opposite errors cannot cancel, and requires enough
  supporting light readings to avoid choosing a delay that only fits a small accidental subset.
- **What isn't a rev light**: a pale reflection lit even at idle, indicators using some of the
  lights - ACC's traction control (blue) and ABS (yellow) - and anything lit far below where rev
  lights work, like PMR's pit limiter flashing the strip at idle, are recognised and left out.
- **Where the LEDs sit vertically.** As strongly coloured lights appear, their vertical bounds are
  learned across the box. Detection and colour measurement then follow those heights, including a
  curved strip, so pale lettering below known lights cannot become another LED. Positions stay
  learned while the lights are dark and reset for a new capture or box. Start with a slow full sweep:
  areas that have not shown a light yet still use the ordinary detector. Keep camera movement off;
  this is not a tracker for large movements or arbitrary brightly coloured dashboard graphics.
- **Lights drawn white-hot**: PMR draws a lit light as a white centre with only a coloured glow,
  on a rim faintly coloured itself. When the usual colour test sees a smear or nothing, lights are
  found by their white centres instead and coloured from their own glow.
- **Each light's colour**, matched by the order of the colours rather than their exact hue: a game
  washes its lights towards white, so a pure green LED can measure as `rgb(138,177,106)`.
- **Where the strip turns to its redline colour, and whether it blinks there.** A blink is recognised
  by its shape - a short fully dark gap with the whole strip lit on both sides - and timed from it.
  Those frames are kept out of the light thresholds, where every blink would otherwise look like all
  the lights switching on at once. When a gear's first colour-changing flash starts dark, its onset
  is measured between the last normal frame and the first confirmed dark frame. The following lit
  phase supplies the colour; waiting for it would make the wheel start late. Later blink cycles,
  isolated dropped frames and gaps crossing a gear change are not used for that onset.
  A strip that blinks without changing colour gets its redline from
  where the blinking starts. A car that changes
  colour a second time near the limiter gets that reported too: a car file holds one redline, so only
  the first is written, and ATSR adds a second stage itself for some cars. So does a strip that flashes
  between its redline colour and its own (LMU's SC63), which ATSR can't show. Whatever the strip
  does once it's in its redline state - PMR's C8.R sweeps blue in 2, 4, 6, 8 lights - isn't taken for
  lights switching on. A strip held at the limiter with nothing changing has no redline effect: a new
  car's file gets a transparent redline at the limiter, so ATSR leaves the lights alone too. If no
  redline change was captured, the fallback redline also stays transparent rather than repainting
  every light red.

Each light's own colour is the one it shows most while lit, leaving out moments when the whole strip is
one colour (the redline, a blink, a fade). Sweeps have to start below the first light, so each light is
seen switching on.

**What it needs:**

- **Borderless or windowed mode.** Exclusive fullscreen hands its frames straight to the display, where
  nothing else can read them.
- **A camera that doesn't move.** Turn off head movement and camera shake; a few pixels of drift are
  fine, but a moving cockpit isn't. VR won't work at all.
- **The same lights every time.** Don't change seat position or field of view mid-capture.

`ScreenCaptureStatus` and `ScreenLights` show what the capture thread is seeing while you drive.
F1 and iRacing report their lights properly, so screen reading is skipped there, and pit-limiter
frames are ignored as in the rest of the plugin.

## Marking lights by hand

For games that don't report their LEDs (AMS2, LMU, ACC, AC, PMR, RaceRoom, …):

1. Start a capture and select a gear.
2. Rev very slowly (a throttle axis you can set precisely helps). Each time the next in-game light, or
   pair/group of lights, comes on, press **MarkLed**. When the redline flash starts, press **MarkRedline**.
3. `UndoMark` removes the last press. Repeat for other gears if their lights differ.
4. Stop and export.

With a repo file, marks fill its LED layout step by step (gaps, mirrored pairs and grouped LEDs are
kept); a gear whose number of marks doesn't match the file's steps is left unchanged. For a new car the
LEDs are laid out left to right, one per mark. Marks include your reaction time, so rev slowly.

## ATSR compatibility

The ATSR SimHub plugin is the main consumer of these files, so every report ends with an
**ATSR compatibility** section based on how ATSR reads them:

- It looks files up as `data/<game>/<carId cleaned up like the README's rule>.json`, not through the
  manifest, so exports are always named that way. The report says when the repo's file is named
  differently (ATSR never finds those).
- It takes each gear's values by position: R, N, 1, 2, …
- An LED lights when RPM is above its value; a colored LED with value 0 is always lit.
- Any black LED color (RGB `000000`, any alpha) is a gap that never lights.
- It reads the layout from the last gear: an exact mirror is "sides to centre", anything else "left
  to right", and a row that is both in increasing order and a mirror (one LED, or all equal) makes
  ATSR ignore the file.
- Some cars have built-in behaviour in ATSR, matched by carId in any game: its own BMW LMDh light
  pattern (only the file's RPM values are used) or an extra redline stage (several iRacing GT cars and
  LMU Aston Martin, McLaren, Cadillac and Ford liveries). The report names them. This is why editing
  AMS2's "BMW M Hybrid V8" file changes nothing on the wheel.
