# Development

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

## Packaging a local release

Set the version in `LovelyCarDataCapture.csproj`, update `CHANGELOG.md`, then run:

```powershell
./tools/package-release.ps1
```

The script builds the plugin and test runner, runs the complete regression suite, checks the DLL's
version, and writes `artifacts/<version>/`: a standalone DLL, install ZIP with documentation, source
ZIP including regression fixtures, release notes, test results and SHA-256 manifests. It includes the
selected working-tree source files, even when uncommitted. Local review captures, build dependencies
and Git metadata are excluded. It does not commit, tag, install or publish anything.

## Replaying a capture

Every screen capture writes `<car>.frames.csv` next to its export (unless *Keep each capture's raw
frames* is off). Replaying it runs a changed version of the plugin over the same drive:

```bash
tests/bin/Release/net48/LovelyCarDataCapture.Tests.exe --replay <car>.frames.csv --repo-file <repo car>.json --game <game> --car <car id>
```

That's how every fix to the screen reading was checked, and the captures in `tests/data` are replayed
by the tests the same way.

These CSVs contain detected light positions and colours, not the original pixels. They test the
analysis and profile composition; changes to pixel detection also need image fixtures. The PMR
Mustang GT3 recording and its `.confirmed.json` preserve an export the owner checked in game. Its
test checks that tuning keeps that result close, without treating it as exact simulator constants.

For a new pixel-detection problem, enable **Keep images around light changes** before capturing.
The diagnostic export contains the selected box's pixels around detected count/colour changes,
plus their timestamps, gear, RPM, original blobs, capture region, frame rate and detector settings
in `transition-frames.json`. The recorder keeps up to two examples of each directed count/colour
change per gear, with at most 24 transitions per gear and 64 overall. This reduces repeated limiter
flashes and leaves room for later gears. These limits apply only to diagnostic images; the raw
frame CSV and RPM analysis still use the full capture. PNG compression and file writing happen
after capture stops. Inspect
those images or pass them through `StripDetector` when adjusting the detector; they are a bounded
selection of transitions, not a complete recording suitable for the CSV replay command. A completely
missed change may not trigger an image. Repositioning the box or restarting screen capture starts a
new image buffer, so the diagnostic export describes the most recent box configuration.

`StripDetector` also learns per-column vertical LED bounds from strong colour. Replay images in
capture order with one detector per capture to exercise that state; a fresh detector is appropriate
for a one-shot preview. The SC63 image fixtures check a curved strip above a logo, including dark
and redline phases. CSV replay cannot validate this pixel filter or repair earlier false blobs.

Older CSVs retain their original timestamps and RPMs. Replaying them validates analysis changes,
but testing live telemetry alignment requires a fresh capture: old exports cannot recover the
telemetry arrival times that were not recorded.

## Tuning it on a recording

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

## Layout

- `src/Capture` – per-game capture logic (no SimHub types, unit tested)
- `src/Screen` – reading rev lights out of the picture (no SimHub types, unit tested)
- `src/Profile` – car file model, LED layouts, and merging captures into a file
- `src/Repo` – read-only GitHub lookup
- `src/Plugin` – SimHub plugin, raw telemetry readers, screen grabbing and the capture box window
- `tests` – console test runner. `tests/data` holds real captures from AMS2, ACC and LMU, each checked
  against the car in game, which the tests replay
- `tools/offline` – turns a screen recording into test data
