# Notes for coding agents

SimHub plugin (.NET Framework 4.8, C#, WPF) that measures a car's rev lights and writes
[Lovely Car Data](https://github.com/Lovely-Sim-Racing/lovely-car-data) v2.0.0 car files plus a
report. Screen reading (AMS2, ACC, LMU, PMR, others) is the main path; F1 and iRacing read telemetry.
User-facing docs: `README.md` (quick start), `docs/how-it-works.md`, `docs/reference.md`,
`docs/development.md`. Read `docs/how-it-works.md` before changing detection logic.

## Build and test

```bash
dotnet build -c Release
dotnet build tests -c Release
tests/bin/Release/net48/LovelyCarDataCapture.Tests.exe
```

- References SimHub's DLLs from `C:\Program Files (x86)\SimHub\` (override with `SIMHUB_INSTALL_PATH`).
  SimHub must be installed to build.
- The test runner is a console app (`tests/Program.cs` lists every test; `tests/ScreenTests.cs` holds
  the screen ones). All must pass before any commit (121 at v0.2.1). There is no xUnit/NUnit.
  `--filter <text>` runs only tests whose name contains the text, for quick iteration.
- Replay a saved drive: `... Tests.exe --replay <car>.frames.csv --repo-file <repo car>.json --game <game> --car <id>`.

## Installing for the user to test

Copy `bin/Release/net48/LovelyCarDataCapture.dll` to `C:\Program Files (x86)\SimHub\` **only when
SimHub is closed** (ask the user to close it; the file is locked otherwise). Exports land in
`%USERPROFILE%\OneDrive\Documents\SimHub\LovelyCarDataCapture\<game>\` (Documents is redirected to
OneDrive on this machine), each with a `.frames.csv` of the raw drive.

## How the screen pipeline fits together

`src/Screen/ScreenLedCapture.cs` `Result()` runs, in this order (order matters; several past
regressions came from reordering):

```
DropFixtures -> DropIdleAnimation -> StripCalibration -> DropIndicators -> lit/hues
-> FindBlinks (scratch) -> RemoveDisplayLag -> FindBlinks -> FindRedline (per gear)
-> FindBlinks(with redline) -> exclude indicator / redline-state / gear-settle frames
-> RedlineFromBlink -> MeasureColors -> per-gear windows -> AverageWithSwitchOff
-> lag notes -> ReportSolidRedline
```

- `_recorded` is the raw frames; `_samples` is the working copy each pass filters. Never mutate `_recorded`.
- Display lag is measured in **milliseconds** (0-250 ms, 2 ms steps; tight gap limit first, widened
  to 1000 rpm only if nothing fits - PMR needs that). Don't go back to an RPM offset.
- `src/Screen/StripDetector.cs`: colour-column blobs, with a white-hot-core fallback (PMR) when the
  colour pass smears or finds nothing.
- `src/Profile/ProfileComposer.cs` merges a capture with the repo file: pools gears agreeing within
  80 rpm (drops an outlier gear), rounds to 5, per-gear redlines, colour-group unification (45° rule),
  transparent redline for cars with no limiter effect.
- Outputs must read correctly in **ATSR** (the main consumer of these files). `src/Profile/AtsrCompatibility.cs`
  encodes its rules; ATSR's dev folder is `<SimHub>\_ATSR_DevelopmentData\rpm_data\<slug(carId)>.json`
  and the plugin fires `ATSRHubMain.ForceRPMReload`. ATSR was studied by decompiling it for
  interoperability only; that code is not in this repo and must not be added.

Tuning constants live as named fields at the top of each class, each with a comment saying why.
Every value was set against a real drive; if you change one, all replay tests must still pass.

## Regression data

`tests/data` holds real captures from AMS2, ACC, LMU, PMR, AC, AC EVO and RaceRoom, plus AC
screenshot fixtures in `tests/data/ac-images` (see its README for provenance). Files named
`*confirmed*` hold values the owner checked in game. A fix for a new car should add its `.frames.csv` there and a
test asserting the values the user confirmed in game. Expected accuracy against confirmed files is
about ±10-20 rpm; 40-60 in neutral/1st or on the last light before the redline.

## Style

- Comments explain *why*, in plain English, like the existing ones. Match the surrounding code.
- Report text (`result.Report`) is read by users: plain sentences, no jargon.
- Keep `src/Capture`, `src/Screen`, `src/Profile` free of SimHub types so they stay testable.

## Working rules from the owner (Milky28)

- Don't push, tag, release, open PRs or post anywhere without the owner's explicit go-ahead.
- Don't post on Discord or contact ATSR's developer.
- Commit messages: plain summary line, no "Generated with" footer in PR bodies to the upstream repo.

## Current state (2026-09-20)

- v0.2.1 is the published release (https://github.com/Milky28/lovely-car-data-capture/releases/tag/v0.2.1).
  `main` has later documentation commits; published assets were deliberately left as released.
- `tools/package-release.ps1` builds, tests and packages a release into `artifacts/<version>/`. It
  does not commit, tag, install or publish.
- `review/` is untracked local material (captures, calibration trials, a nested Lovely Car Data
  checkout). Never stage it or clean it away. Tests don't depend on it.
- Checked in game: selected AMS2, ACC, LMU, PMR, AC and AC EVO cars. RaceRoom has replay/image tests
  but no live wheel check. F1 and iRacing (telemetry) are untested in game.
- Upstream PRs from the owner's fork (clone at `C:\Users\jerky\Documents\LovelyCarData`, remote
  `fork`): Lovely-Sim-Racing/lovely-car-data#48, #49, #50 and #51, all awaiting review. The owner is
  holding further submissions (including the LMU SC63 fix: LEDs 6-8 yellow together ~7400, 9-10 red
  together ~7675) until these are approved. Don't prepare or suggest new upstream PRs until then.
- Don't assume the DLL installed in SimHub matches the latest build.
- Companion tool: RPM LED Builder, `C:\Users\jerky\Documents\rpm-led-builder` (single `index.html`,
  GitHub Pages at https://milky28.github.io/rpm-led-builder/). Separate repo; confirm which project
  is meant before editing.
