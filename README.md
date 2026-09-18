# Lovely Car Data Capture

A SimHub plugin that measures a car's rev lights while you drive and writes a
[Lovely Car Data](https://github.com/Lovely-Sim-Racing/lovely-car-data) car file: the RPM each light
comes on at, their colours, the redline and whether the strip blinks. A report explains where every
value came from.

In AMS2, ACC, LMU and most other games it reads the lights off the screen. In F1 and iRacing it
reads them from telemetry.

*A community tool, not made by or affiliated with Lovely Sim Racing or ATSR.*

## Install

1. Download `LovelyCarDataCapture.dll` from the [latest release](https://github.com/Milky28/lovely-car-data-capture/releases/latest).
2. Close SimHub and copy the DLL into the SimHub folder (usually `C:\Program Files (x86)\SimHub`).
3. Start SimHub and enable **Lovely Car Data Capture** when asked. Its page is under
   **Additional plugins** in SimHub's left menu.

Not listed? Right-click the DLL, choose *Properties*, tick *Unblock*, and start SimHub again.

## Your first capture

Run the game **borderless or windowed**, and turn off head movement and camera shake.

1. **Sit in the car** with the rev lights in view.
2. On the plugin's page press **Pick the lights…**, then switch to the game. Five seconds later it
   takes a still of the screen: draw a box round the lights, a little outside them, and press Enter.
3. Press **Start capture**.
4. **Rev slowly from idle to the limiter**, three or four times, in two or three gears. Hold the
   limiter for a second or two each time. Braking mid-sweep is fine.
5. Press **Stop and export**.

The car file and its report are written to `Documents\SimHub\LovelyCarDataCapture\<game>\`.
Read the report: it says what was measured, what was left out and why.

Games that pause when they lose focus: map **StartCapture**, **StopAndExport** and
**PickCaptureBox** to wheel buttons in SimHub's *Controls and events*. A panel over the game
confirms each press.

## Check it on your wheel

If you use ATSR, you can see the new file on your wheel straight after the drive:

1. In ATSR, switch on **Enable Local RPM Folder** (ATSR-Hub EVO > Universal Settings > RPM Settings >
   Developer Settings).
2. On this plugin's page, tick **Copy each export to ATSR's local RPM folder**.

Each export then goes to your wheel as soon as it's saved. While a copy is there ATSR uses it instead
of the repo's file, so remove it from the plugin's page (*Checking a file on the wheel*) once you're done.

## Submit it

Open the file in the [RPM LED Builder](https://milky28.github.io/rpm-led-builder/) for a last look,
then contribute it to [Lovely Car Data](https://github.com/Lovely-Sim-Racing/lovely-car-data) as a
pull request.

## Games

| Game | How the lights are read | Tested |
| --- | --- | --- |
| AMS2, ACC, LMU | Off the screen | Yes, against cars checked in game |
| AC, AC EVO, RaceRoom, PMR and others | Off the screen | Not yet |
| F1 2021–2026, iRacing | Telemetry | Not yet in game |

## More

- [How it works](docs/how-it-works.md): what it works out from the screen, and how files are made to
  suit ATSR.
- [Reference](docs/reference.md): buttons, settings, SimHub properties and the ATSR local folder.
- [Development](docs/development.md): building, tests, and replaying a saved capture.

## License

MIT, see [LICENSE](LICENSE). Car files you contribute fall under Lovely Car Data's license.
