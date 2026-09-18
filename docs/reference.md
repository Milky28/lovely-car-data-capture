# Reference

Buttons, settings and properties, and the ATSR local folder in detail. The plugin's own page in
SimHub covers the same ground with hover help.

## Buttons you can map

In SimHub's *Controls and events*, all under `LovelyCarDataCapture.`:

| Action | Does |
| --- | --- |
| `StartCapture` | Starts recording the car you're in. |
| `StopAndExport` | Stops and writes the car file, its report and the raw frames. |
| `ResetCapture` | Throws away what's been recorded. |
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
| `ShowOverlay` | `true` | Show the panel over the game saying what the plugin is doing. |
| `SaveCaptureFrames` | `true` | Write `<car>.frames.csv` next to each screen-capture export: every frame's RPM and the lights seen. Replay it with `LovelyCarDataCapture.Tests.exe --replay <car>.frames.csv --repo-file <repo car>.json` to check a capture again without driving it. |
| `OverlayX` / `OverlayY` | *(top left)* | Where that panel sits; drag it to move it. |
| `OutputFolder` | *(Documents\SimHub\LovelyCarDataCapture)* | |
| `LedNumber` | `12` | LED count for new cars in games without LED data. |
| `FirstLedPercent` / `LastLedPercent` | `72` / `97.5` | Estimate spread for games without LED data. |
| `RoundRpmTo` | `25` | Rounding for estimates. |
