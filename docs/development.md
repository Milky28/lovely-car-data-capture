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

## Replaying a capture

Every screen capture writes `<car>.frames.csv` next to its export (unless *Keep each capture's raw
frames* is off). Replaying it runs a changed version of the plugin over the same drive:

```bash
tests/bin/Release/net48/LovelyCarDataCapture.Tests.exe --replay <car>.frames.csv --repo-file <repo car>.json --game <game> --car <car id>
```

That's how every fix to the screen reading was checked, and the captures in `tests/data` are replayed
by the tests the same way.

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
