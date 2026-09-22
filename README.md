# Lovely Car Data Capture

A SimHub plugin that measures a car's rev lights while you drive and writes a
[Lovely Car Data](https://github.com/Lovely-Sim-Racing/lovely-car-data) car file: the RPM each light
comes on at, their colours, the redline and whether the strip blinks. A report explains where every
value came from.

In AMS2, ACC, LMU, PMR, AC, AC EVO and most other games it reads the lights off the screen. In F1 and iRacing it
reads them from telemetry.

*A community tool, not made by or affiliated with Lovely Sim Racing or ATSR.*

## Install

Latest published release: [v0.2.3](https://github.com/Milky28/lovely-car-data-capture/releases/tag/v0.2.3),
with a clearer capture workflow, retryable exports, capture evidence archives, RPM LED Builder clipboard
import and screen-colour fixes. See the [changelog](CHANGELOG.md) for details.

1. Download `LovelyCarDataCapture.dll` from the [latest release](https://github.com/Milky28/lovely-car-data-capture/releases/latest).
2. Close SimHub and copy the DLL into the SimHub folder (usually `C:\Program Files (x86)\SimHub`).
3. Start SimHub and enable **Lovely Car Data Capture** when asked. Its page is under
   **Additional plugins** in SimHub's left menu.

Not listed? Right-click the DLL, choose *Properties*, tick *Unblock*, and start SimHub again.

<img src="docs/images/plugin-page.png" width="720" alt="The plugin's page in SimHub, with its buttons, help sections and options">

## Your first capture

Run the game **borderless or windowed**, and turn off head movement and camera shake.

1. **Sit in the car** with the rev lights in view.
2. On the plugin's page press **Pick the lights…**, then switch to the game. Five seconds later it
   takes a still of the screen: draw a box round the lights, a little outside them, and press Enter.
   Saving a valid box turns on screen reading automatically. The top of the page shows the active
   source and whether the plugin is waiting, recording, exporting or holding a capture to export.

   <img src="docs/images/pick-the-lights.jpg" width="360" alt="A still of ACC with an orange box drawn round the dash's rev lights and a banner reporting six lit lights found">
3. Press **Start capture**.
4. **Rev slowly from idle to the limiter**, three or four times, in two or three gears. Hold the
   limiter for a second or two each time. Braking mid-sweep is fine. In a game whose wheel turns
   with yours, or that shows its lights well behind the revs (PMR), rev slowly in neutral instead,
   hands off the wheel. A panel over the game shows
   what's being recorded.

   <img src="docs/images/capturing.jpg" width="420" alt="The recording panel over ACC: gear 2, 6768 rpm, 6 lights lit, 791 frames">
5. In the **Export** step, if SimHub cannot report the car's gear count and you could not reach the
   higher gears, set **Top gear** to the car's highest forward gear (1–12). Unreached gears use
   fallback values; check the report and verify them in game. The choice resets to **Auto** after a
   successful export.
6. Press **Stop and export**.
7. Review the **Last export** area. For a successful normal car file, **Copy JSON + open Builder**
   copies the JSON and opens the Builder. Click **Import copied capture** in the browser to import
   it with the capture's game selected. If clipboard access fails, paste into the text box instead.
   Unknown games require manual **Sim folder** selection.

The plugin page keeps the main path as **Pick**, **Start**, **Export** and **Review**. Setup, help and
diagnostics stay folded away until you need them. Its live status tells you when the game is paused,
in replay or menu, on the pit limiter, missing telemetry needed for screen frames, or hitting a screen
reading error.

The latest car file and report are still written to
`Documents\SimHub\LovelyCarDataCapture\<game>\`. Each session export also keeps its own folder under
`<game>\captures\<car>\<timestamp-id>\`, with `capture.json`, the exported `car.json` and
`report.txt`, raw CSV when enabled, selection/representative crop images and optional transition
images.
Replacing an export saves its previous JSON, report and raw frames CSV in that game's `backups` folder first.
Gears not captured this time keep their previous RPM values. Confirmed color and blink adjustments
can be kept in a [local overrides file](docs/reference.md#keeping-confirmed-colors-and-blink-timing).
If an export fails, the capture stays loaded so you can fix the problem and press **Retry export**.
If the game or car changes mid-capture, recording stops and the previous capture is held until you
choose **Export capture** or **Discard capture**. Closing SimHub attempts to export any unsaved
capture that is still running or pending.

The plugin page keeps a **Last export** area for the current SimHub session, even when the overlay is
off: car, outcome, important warnings, details, and buttons to open the report or output folder.
Read the report: it says what was measured, how tightly, what was left out and why. From the
Ginetta's:

```text
Gear 3: 8/8 lights seen, 688 rising frames
  LED  1    6295  4 climbs, agreeing within 22 rpm
  LED  2    6500  4 climbs, agreeing within 6 rpm; off at 6496, the two averaged
  LED  3    6700  3 climbs, agreeing within 13 rpm
  LED  4    6895  3 climbs, agreeing within 8 rpm
  ...
Colors measured on screen:
  LED 1, 2, 7, 8: rgb(158,245,165) -> green #FF00FF00
  LED 3, 6: rgb(246,242,165) -> yellow #FFFFFF00
  Redline color rgb(255,139,153) -> #FFFF0000 from 6895 rpm
```

Games that pause when they lose focus: map **StartCapture**, **StopAndExport** and
**PickCaptureBox** to wheel buttons in SimHub's *Controls and events*. A panel over the game
confirms each press.

## Check it on your wheel

If you use ATSR, you can see the new file on your wheel straight after the drive:

1. In ATSR, switch on **Enable Local RPM Folder** (ATSR-Hub EVO > Universal Settings > RPM Settings >
   Developer Settings). You don't need to bind Force RPM Reload: the plugin presses it for you.

   <img src="docs/images/atsr-local-rpm-folder.png" width="560" alt="ATSR's Developer Settings with Enable Local RPM Folder switched on">
2. On this plugin's page, tick **Copy each export to ATSR's local RPM folder**.

Each export then goes to your wheel as soon as it's saved. While a copy is there ATSR uses it instead
of the repo's file, so remove it from the plugin's page (*Checking a file on the wheel*) once you're done.

<p align="center">
  <img src="docs/images/wheel-matches-game.jpg" width="480"
       alt="ACC's Ginetta G55 GT4 dash lights on screen, and a Conspit wheel below showing the same lights from the captured file">
  <br><em>ACC's Ginetta G55 GT4, a car new to the repo: the game's lights on screen, and the wheel below
  driven by ATSR from the file this plugin wrote.</em>
</p>

## Submit it

Use **Copy JSON + open Builder**, then click **Import copied capture** in the browser for a last look.
Check the **Sim folder** matches the game the plugin exported under, then contribute it to
[Lovely Car Data](https://github.com/Lovely-Sim-Racing/lovely-car-data) as a pull request.

## Games

| Game | How the lights are read | Tested |
| --- | --- | --- |
| AMS2, ACC, LMU, PMR, AC, AC EVO | Off the screen | Yes, against selected cars checked in game |
| RaceRoom | Off the screen | Capture and image regression tests; live wheel validation still needed |
| Other games | Off the screen | Not yet |
| F1 2021–2026, iRacing | Telemetry | Not yet in game |

## More

- [How it works](docs/how-it-works.md): what it works out from the screen, and how files are made to
  suit ATSR.
- [Reference](docs/reference.md): buttons, settings, SimHub properties and the ATSR local folder.
- [Development](docs/development.md): building, tests, and replaying a saved capture.

## License

MIT, see [LICENSE](LICENSE). Car files you contribute fall under Lovely Car Data's license.
