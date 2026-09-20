# Reference

Buttons, settings and properties, and the ATSR local folder in detail. The plugin's own page in
SimHub covers the same ground with hover help.

## Buttons you can map

In SimHub's *Controls and events*, all under `LovelyCarDataCapture.`:

| Action | Does |
| --- | --- |
| `StartCapture` | Starts recording the car you're in. |
| `StopAndExport` | Stops and writes the car file, its report and the raw frames. |
| `ResetCapture` | Stops recording and throws away what's been recorded. StartCapture begins again. |
| `PickCaptureBox` | Takes a still of the screen straight away and lets you draw the box on it. |
| `ShowCaptureBox` | Opens the live frame over the game. |
| `MarkLed` / `MarkRedline` / `UndoMark` | Marking lights by hand, for games whose lights can't be read. |

Every one of them can also be done from the plugin's page, which is enough for a game that keeps
running while it hasn't got focus.

## Trying a file in ATSR before submitting

Switch on **Enable Local RPM Folder** in ATSR once (ATSR-Hub EVO > Universal Settings > RPM Settings >
Developer Settings), then tick **Copy each export to ATSR's local RPM folder** on this plugin's settings
page. Each export is then also written to `<SimHub folder>\_ATSR_DevelopmentData\rpm_data\<carId>.json`,
where ATSR looks before the repo, and the plugin presses ATSR's **Force RPM Reload** action
(`ATSRHubMain.ForceRPMReload`) so the wheel shows the new file straight after the drive. The reload only
acts while a car is loaded; if the lights don't change, press Force RPM Reload or re-enter the car.

While a copy is there ATSR uses it instead of the repo's file for that car **in every game**: the
folder is keyed by car id alone, so a car in two games (the McLaren 720S GT3 Evo in AMS2 and ACC)
shares one file. The settings page's **Checking a file on the wheel** section lists the plugin's
copies with the game and time each came from, and removes them (to the Recycle Bin) once checked. The
report says when an export replaced another game's copy, and a file there that the plugin didn't write
is kept alongside as `<carId>.json.before-capture-<time>` rather than overwritten.

LMU reports the car `Lamborghini Iron Lynx 2024`, while ATSR canonicalizes it as `Lamborghini SC63`.
For this one known case the local test copy is therefore named `lamborghini-sc63.json`; the exported
Lovely Car Data file keeps the game's original car id.

## Keeping confirmed colors and blink timing

Local adjustments go in `<car>.overrides.json` beside the exported `<car>.json`, inside that game's
output folder. Only explicitly listed colors and blink timing are kept across captures. Editing those
fields in the generated car file alone does not preserve them on the next export.

For example, `lmu/lamborghini-iron-lynx-2024.overrides.json` keeps the SC63's confirmed yellow group:

```json
{
  "game": "lmu",
  "carId": "Lamborghini Iron Lynx 2024",
  "ledNumber": 10,
  "ledColor": { "6": "#FFFFFF00", "7": "#FFFFFF00", "8": "#FFFFFF00" }
}
```

`game` is the export's game folder name, `carId` is the game's raw ID, and `ledNumber` must match the
profile. Color keys are LED numbers (0 is the redline color); values use `#AARRGGBB`. An optional
`"redlineBlinkInterval": 200` keeps a 200 ms interval; zero disables blinking. These values take
precedence over both the repository and new measurements, and the report lists what was applied.
Remove an entry or the override file to resume normal capture behavior. No plugin setting is needed.
Detected screen gaps always remain black with RPM 0 in every gear, including retained gears;
repository colors or local color overrides cannot turn those empty positions into lit LEDs.

An invalid file or mismatched identity/LED count stops export before replacing existing car or ATSR
files. Correct the override and press **Stop and export** again while the session is still loaded.
Overrides are local preferences and are not part of a Lovely Car Data submission.

## Export backups

Before replacing an existing car file or report, the plugin copies their original bytes together to
`<game>/backups/<car>/<UTC timestamp>-<unique suffix>/` inside the output folder. The new report gives
the backup location. If either copy fails, the export stops before replacing the files or updating
ATSR. The first export needs no backup; if only one old file exists, that file is still saved.

Backups contain the JSON and report, not raw frames, transition images or overrides. They are not
deleted automatically. To restore a capture, copy its JSON and report back to their original game
folder. ATSR's development copy must also be replaced and reloaded if you want that backup on the wheel.

## Capturing additional gears

If SimHub does not report the car's gear count and the track is too short to reach the higher gears,
choose **Top gear for next export** on the plugin's page before stopping the capture. The export includes
every forward gear through that number. Unreached gears use the usual captured or starting-file
fallbacks, and the report names the manual count. The choice returns to **Auto** after a successful
export so it cannot carry into the next car.

Each export reads the previous car file from the same game's output folder. Gears absent from the
current capture keep their previous RPM rows, including redline. Gears captured now use the normal
measurement and repository fallback rules. Colors and blink timing still use the overrides above.
The report lists which gears came from the previous export.

The car ID, LED count and gap positions must match. Incompatible profiles or invalid RPM rows are
skipped with a report note; unreadable JSON stops the export so the existing file is kept. With
**CopyMeasuredToOtherGears** on, screen captures retain that setting's behavior instead of restoring
previous gear rows. Move the previous export out of the output folder to start again from the repo.

## How long a capture runs

A capture runs until *Stop and export*, *ResetCapture* or SimHub closes. It keeps up to 72,000 screen
frames - 20 minutes at 60 frames a second, 40 at 30 - which is far more than a capture needs. When
it's full, the panel over the game says so and nothing more is recorded. While there's nothing to
record (the game paused, in a menu or closed) the plugin looks at the screen only twice a second.
Closing SimHub with a capture still running exports it, waiting up to three seconds for the repo
file; the report says it was exported that way.

## Watching it while you drive

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

## SimHub properties

`LovelyCarDataCapture.Capturing`, `CarId`, `GearsSeen`, `LedSource`, `LedProgress` (e.g. `3:15/15 4:9/15`,
or `3:10+RL` for marks), `LastMark`, `RepoStatus`, `LastExportPath`, `LastReportPath`, `LastAtsrDeveloperPath`,
`ScreenCaptureStatus`, `ScreenLights`.

## Settings

Stored in SimHub's `PluginsData\Common\CapturePlugin.CaptureSettings.json` (edit it with SimHub closed):

| Setting | Default | |
| --- | --- | --- |
| `UseRepoFile` | `true` | Look the car up on GitHub and build on its file. |
| `RepoBranch` | `main` | |
| `CopyToAtsrDeveloperFolder` | `false` | Also write each export to ATSR's local RPM folder and have ATSR reload. |
| `AtsrCopies` | `[]` | The plugin's own copies in that folder, kept so they can be listed and removed. |
| `ScreenCapture` | `false` | Read the rev lights off the screen while capturing. |
| `ScreenBoxX` / `Y` / `Width` / `Height` | *(unset)* | The box being watched, in screen pixels. Set it with *Pick the lights…* or *Adjust live…*. |
| `ScreenCaptureFps` | `30` | Frames read per second, 30 or 60. Use 60 for a car whose lights blink at the limiter, so each dark flash spans enough frames to time, or one that revs very quickly; it roughly doubles the CPU a capture uses. Also on the plugin's page. |
| `CopyMeasuredToOtherGears` | `false` | Put the measured values into gears that weren't driven, instead of keeping the repo file's. Most cars use the same lights in every gear. |
| `TopGearForNextExport` | `0` (Auto) | Highest forward gear to include in one export when SimHub cannot supply the count. Select 1–12 on the plugin page; resets after a successful export. |
| `ShowOverlay` | `true` | Show the panel over the game saying what the plugin is doing. |
| `SaveCaptureFrames` | `true` | Write `<car>.frames.csv` next to each screen-capture export: every frame's RPM and the lights seen. Replay it with `LovelyCarDataCapture.Tests.exe --replay <car>.frames.csv --repo-file <repo car>.json` to check a capture again without driving it. |
| `SaveTransitionFrames` | `false` | Keep cropped images before, during and after changes in the detected lights. Enable before starting a diagnostic capture. Export writes PNGs and a JSON manifest in a separate `<car>.transitions-...` folder. Keeps up to two examples of each change per gear, at most 24 transitions per gear and 64 overall (up to 192 images), within 64 MiB of retained pixels. The report identifies skipped repeats, limits or missing images. |
| `OverlayX` / `OverlayY` | *(top left)* | Where that panel sits; drag it to move it. |
| `OutputFolder` | *(Documents\SimHub\LovelyCarDataCapture)* | |
| `LedNumber` | `12` | LED count for new cars in games without LED data. |
| `FirstLedPercent` / `LastLedPercent` | `72` / `97.5` | Estimate spread for games without LED data. |
| `RoundRpmTo` | `25` | Rounding for estimates. |
