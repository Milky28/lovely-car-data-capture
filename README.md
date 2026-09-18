# Lovely Car Data Capture (SimHub plugin)

Records a car's gears and LED RPMs while you drive and exports a
[Lovely Car Data](https://github.com/Lovely-Sim-Racing/lovely-car-data) v2.0.0 car file, plus a
report explaining where every value came from. It never uploads anything.

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

Special cases the report calls out:

- **F2 and other 10-LED F1 files:** the game reports 15 lights, so the file's RPMs aren't touched; the measured table is in the report to map by hand.
- **LMU:** repo files are generated from templates, so the values belong in `src_data/lmu`.
- LEDs never lit in a gear, gears not driven, and a redline flash that was never reached.

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

### Trying a file in ATSR before submitting

Set `CopyToAtsrDeveloperFolder` to `true`. Each export is then also written to
`<SimHub folder>\_ATSR_DevelopmentData\rpm_data\<carId>.json`, which ATSR reads when **Developer
Mode** is on in its RPM settings. Toggle Developer Mode (or re-enter the car) to make ATSR reload it;
if the lights still don't change, restart SimHub. Delete the copy afterwards or ATSR keeps using it
instead of the repo's file.

## Install

1. Build (below) or take `LovelyCarDataCapture.dll` from `bin/Release/net48/`.
2. Copy the DLL into the SimHub folder (default `C:\Program Files (x86)\SimHub`) and restart SimHub.
3. Enable **Lovely Car Data Capture** when SimHub asks.
4. Optionally map the actions to buttons in *Controls and events*: `LovelyCarDataCapture.StartCapture`,
   `StopAndExport`, `ResetCapture`, `ShowCaptureBox`, and for games without LED data `MarkLed`,
   `MarkRedline`, `UndoMark`. A capture can also be started and stopped from the plugin's own page,
   which is enough for a game that keeps running while it hasn't got focus.

## Use

1. Sit in the car and trigger **StartCapture**, from a mapped button or the plugin's page.
2. Drive through every gear, including R and N, and rev each gear smoothly up to the limiter. Hold
   the limiter a moment so the redline flash is seen. Pit-limiter time is ignored.
   - F1: several clean climbs per gear give tighter values; the report shows the range each value lies in.
   - iRacing: just engaging each gear is enough.
3. Trigger **StopAndExport**. Files go to `Documents\SimHub\LovelyCarDataCapture\<sim>\`:
   `<car>.json` and `<car>.report.txt`. (With OneDrive's folder redirection that is under `OneDrive\Documents`.)
4. Read the report, then open the JSON in the RPM LED Builder (Import JSON) to check it before submitting.

Switching cars during a capture starts a new one and discards the old, so export first.

### Reading the lights off the screen

Most games don't report their rev lights, but they do draw them, so the lights can be read out of the
picture: bright saturated dots on a dark wheel, which nothing else in a cockpit looks like. Pairing
what's lit with the RPM from telemetry at that moment gives the same thresholds the F1 games hand
over directly.

**Setting it up:**

1. In SimHub's left menu, open **Additional plugins → Lovely Car Data Capture** (SimHub puts every
   plugin's page in that group). That page walks through the whole process, holds the options below,
   and shows where exports are written; tick *Read the rev lights off the screen* to start.
2. Sit in the car with the lights visible, then press **Position the box…**. An orange frame appears
   with its controls just below it.
3. Put the frame around the rev lights, a little outside them. With the game in front it keeps the
   keyboard, so use the system-wide shortcuts: **Ctrl+Alt+arrows** move the frame, **Ctrl+Alt+Shift+arrows**
   resize it, **Ctrl+Alt+Enter** finishes. Click the panel first and plain arrows work too, a pixel at a
   time. The panel shows what the capture sees as you rev, so you can watch the count while adjusting.
4. The box is **saved as you move it** - the panel says so - and *Undo changes* puts it back where it
   started. Nothing depends on a keypress reaching the right window.
5. Capture as usual. Rev slowly from idle to the limiter a few times, holding the limiter a moment.

The box can also be opened from a wheel button: map `LovelyCarDataCapture.ShowCaptureBox`.

**A game in borderless mode hides the mouse pointer and takes the keyboard while it has focus.** That's
why the frame has system-wide shortcuts and saves itself as it moves, and why its reading stays live
while the game is in front: the border sits just outside the region being read, so nothing has to be
hidden to take a reading. Alt-tab brings the pointer back if you want it.

**What it works out** from a few sweeps:

- **How many lights there are and where the gaps are**, from the spacing between them. A wider space
  than the rest is a slot that never lights, and the car file needs those slots too.
- **The RPM each light switches on at.** Every climb gives a window between the last frame the light
  was dark and the first it was lit; the value is the middle of that window, and the median across
  climbs, so one bad frame doesn't move it. A light only ever seen already lit, or pinned down no
  better than 150 rpm, is reported instead of written: braking mid-sweep costs you that gear, not the
  capture.
- **Whether the car uses one set of lights for every gear.** Two gears measured right through that
  agree are taken as evidence it does, and the gears a track gives no room to sweep follow them.
- **Each light's colour**, matched by the order of the colours rather than their exact hue: a game
  washes its lights towards white, so a pure green LED can measure as `rgb(138,177,106)`.
- **Where the strip turns to its redline colour, and whether it blinks there.** A blink is recognised
  by its shape - a short fully dark gap with the whole strip lit on both sides - and timed from it.
  Those frames are kept out of the light thresholds, where every blink would otherwise look like all
  the lights switching on at once. A strip that blinks without changing colour gets its redline from
  where the blinking starts. A car that changes
  colour a second time near the limiter gets that reported too: a car file holds one redline, so only
  the first is written, and ATSR adds a second stage itself for some cars.

Each light's own colour is learned while the strip is only partly lit, because it can't be in its
redline state then. That's also why sweeps have to start below the first light.

**What it needs:**

- **Borderless or windowed mode.** Exclusive fullscreen hands its frames straight to the display, where
  nothing else can read them.
- **A camera that doesn't move.** Turn off head movement and camera shake; a few pixels of drift are
  fine, but a moving cockpit isn't. VR won't work at all.
- **The same lights every time.** Don't change seat position or field of view mid-capture.

`ScreenCaptureStatus` and `ScreenLights` show what the capture thread is seeing while you drive.
F1 and iRacing report their lights properly, so screen reading is skipped there, and pit-limiter
frames are ignored as in the rest of the plugin.

#### Tuning it on a recording

The same detection runs offline, which is how it was built and how to check a change:

```bash
python tools/offline/analyze_recording.py glyphs clip.mkv --rpm-box 1426,620,144,60 --led-box 2320,1100,440,100
python tools/offline/analyze_recording.py run clip.mkv --rpm-box 1426,620,144,60 --led-box 2320,1100,440,100 --labels <digits from glyphs.png> --out car.csv
```

The first step writes the digit shapes of an on-screen RPM readout so you can tell it what they are;
the second writes a CSV of every frame. `tests/data` holds one made this way, from an AMS2 Audi R8
LMS GT3 evo II, and the tests check the values it produces against that car's file in the repo. Note
that a recording reads the RPM off the screen, where a SimHub overlay lags the game by a frame or two
and so reads about 15 rpm low while the revs climb; live capture takes RPM from telemetry and doesn't.

### Watching it while you drive

Mapped buttons are pressed with the game covering everything, so the plugin shows a small panel over it:
what the button just did, and how the capture is going. It appears when a capture starts, on every
button press, and for a few seconds after. Drag it anywhere - it stays put and never takes focus, so it
can't pull the game out of the foreground.

- **StartCapture:** says whether the rev lights are being watched, or asks for a capture box if none is set.
- **While driving:** gear, RPM, lights lit and how much has been captured so far.
- **MarkLed / MarkRedline / UndoMark:** the step and the RPM recorded.
- **StopAndExport:** where the file went, what the values came from, and whether the report has ATSR warnings.

Untick *Show the panel over the game* on the plugin's page to turn it off; `ShowOverlay` in the settings
file does the same.

### Marking lights by hand

For games that don't report their LEDs (AMS2, LMU, ACC, AC, PMR, RaceRoom, …):

1. Start a capture and select a gear.
2. Rev very slowly (a throttle axis you can set precisely helps). Each time the next in-game light, or
   pair/group of lights, comes on, press **MarkLed**. When the redline flash starts, press **MarkRedline**.
3. `UndoMark` removes the last press. Repeat for other gears if their lights differ.
4. Stop and export.

With a repo file, marks fill its LED layout step by step (gaps, mirrored pairs and grouped LEDs are
kept); a gear whose number of marks doesn't match the file's steps is left unchanged. For a new car the
LEDs are laid out left to right, one per mark. Marks include your reaction time, so rev slowly.

### SimHub properties

`LovelyCarDataCapture.Capturing`, `CarId`, `GearsSeen`, `LedSource`, `LedProgress` (e.g. `3:15/15 4:9/15`,
or `3:10+RL` for marks), `LastMark`, `RepoStatus`, `LastExportPath`, `LastReportPath`, `LastAtsrDeveloperPath`,
`ScreenCaptureStatus`, `ScreenLights`.

### Settings

Stored in SimHub's `PluginsData\Common\CapturePlugin.CaptureSettings.json` (edit it with SimHub closed):

| Setting | Default | |
| --- | --- | --- |
| `UseRepoFile` | `true` | Look the car up on GitHub and build on its file. |
| `RepoBranch` | `main` | |
| `CopyToAtsrDeveloperFolder` | `false` | Also write each export to ATSR's Developer Mode folder. |
| `ScreenCapture` | `false` | Read the rev lights off the screen while capturing. |
| `ScreenBoxX` / `Y` / `Width` / `Height` | *(unset)* | The box being watched, in screen pixels. Set it with *Position the box…*. |
| `ScreenCaptureFps` | `30` | Frames read per second, 30 or 60. Use 60 for a car whose lights blink at the limiter, so each dark flash spans enough frames to time, or one that revs very quickly; it roughly doubles the CPU a capture uses. Also on the plugin's page. |
| `CopyMeasuredToOtherGears` | `false` | Put the measured values into gears that weren't driven, instead of keeping the repo file's. Most cars use the same lights in every gear. |
| `ShowOverlay` | `true` | Show the panel over the game saying what the plugin is doing. |
| `SaveCaptureFrames` | `true` | Write `<car>.frames.csv` next to each screen-capture export: every frame's RPM and the lights seen. Replay it with `LovelyCarDataCapture.Tests.exe --replay <car>.frames.csv --repo-file <repo car>.json` to check a capture again without driving it. |
| `OverlayX` / `OverlayY` | *(top left)* | Where that panel sits; drag it to move it. |
| `OutputFolder` | *(Documents\SimHub\LovelyCarDataCapture)* | |
| `LedNumber` | `12` | LED count for new cars in games without LED data. |
| `FirstLedPercent` / `LastLedPercent` | `72` / `97.5` | Estimate spread for games without LED data. |
| `RoundRpmTo` | `25` | Rounding for estimates. |

## Build and test

Requires the .NET SDK and SimHub installed (its DLLs are referenced, not copied). Set
`SIMHUB_INSTALL_PATH` if SimHub isn't in the default folder.

```bash
dotnet build -c Release
dotnet build tests -c Release
tests/bin/Release/net48/LovelyCarDataCapture.Tests.exe
```

Test runner options:

- `--repo-data <path to lovely-car-data/data>` round-trips every car file in a local clone.
- `--live-repo` checks the GitHub lookup (needs internet).
- `--show-reports` prints the reports and JSON the compose tests produce.

## Layout

- `src/Capture` – per-game capture logic (no SimHub types, unit tested)
- `src/Screen` – reading rev lights out of the picture (no SimHub types, unit tested)
- `src/Profile` – car file model, LED layouts, and merging captures into a file
- `src/Repo` – read-only GitHub lookup
- `src/Plugin` – SimHub plugin, raw telemetry readers, screen grabbing and the capture box window
- `tests` – console test runner, with a real recording in `tests/data`
- `tools/offline` – turns a screen recording into that test data
