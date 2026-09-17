# Lovely Car Data Capture (SimHub plugin)

Records a car's gears and LED RPMs while you drive and exports a
[Lovely Car Data](https://github.com/Lovely-Sim-Racing/lovely-car-data) v2.0.0 car file, plus a
report explaining where every value came from. It never uploads anything.

## What it captures

| Game | LED data | How |
| --- | --- | --- |
| F1 2021–2026 | Real rev lights | Reads the game's 15 rev-light bits and learns the RPM each light switches on, per gear, plus where the redline flash starts. |
| iRacing | Real shift lights | Reads iRacing's first-LED / shift / last-LED / blink RPMs (per gear where the car changes them). LEDs in between are spaced evenly. |
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
4. Map the actions to buttons in *Controls and events*: `LovelyCarDataCapture.StartCapture`, `StopAndExport`, `ResetCapture`,
   and for games without LED data `MarkLed`, `MarkRedline`, `UndoMark`.

## Use

1. Sit in the car and trigger **StartCapture**.
2. Drive through every gear, including R and N, and rev each gear smoothly up to the limiter. Hold
   the limiter a moment so the redline flash is seen. Pit-limiter time is ignored.
   - F1: several clean climbs per gear give tighter values; the report shows the range each value lies in.
   - iRacing: just engaging each gear is enough.
3. Trigger **StopAndExport**. Files go to `Documents\SimHub\LovelyCarDataCapture\<sim>\`:
   `<car>.json` and `<car>.report.txt`.
4. Read the report, then open the JSON in the RPM LED Builder (Import JSON) to check it before submitting.

Switching cars during a capture starts a new one and discards the old, so export first.

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
or `3:10+RL` for marks), `LastMark`, `RepoStatus`, `LastExportPath`, `LastReportPath`, `LastAtsrDeveloperPath`.

### Settings

Stored in SimHub's `PluginsData\Common\CapturePlugin.CaptureSettings.json` (edit it with SimHub closed):

| Setting | Default | |
| --- | --- | --- |
| `UseRepoFile` | `true` | Look the car up on GitHub and build on its file. |
| `RepoBranch` | `main` | |
| `CopyToAtsrDeveloperFolder` | `false` | Also write each export to ATSR's Developer Mode folder. |
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
- `src/Profile` – car file model, LED layouts, and merging captures into a file
- `src/Repo` – read-only GitHub lookup
- `src/Plugin` – SimHub plugin and raw telemetry readers
- `tests` – console test runner
