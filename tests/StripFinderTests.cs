using System;
using System.Collections.Generic;
using System.Linq;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private const int FinderScreenWidth = 1920, FinderScreenHeight = 1080;

        private static void Paint(byte[] pixels, int x, int y, int width, int height, int r, int g, int b)
        {
            for (int row = Math.Max(0, y); row < Math.Min(FinderScreenHeight, y + height); row++)
                for (int col = Math.Max(0, x); col < Math.Min(FinderScreenWidth, x + width); col++)
                {
                    int i = (row * FinderScreenWidth + col) * 3;
                    pixels[i] = (byte)r; pixels[i + 1] = (byte)g; pixels[i + 2] = (byte)b;
                }
        }

        /// <summary>
        /// A cockpit as the whole screen shows it, with things on it that are not the rev lights: bright
        /// scenery that never changes, a marshal light flashing on its own, a needle that follows the
        /// revs round a dial, and a HUD rev bar that fills smoothly.
        /// </summary>
        private static byte[] BusyScreen(int frame, double rpm)
        {
            var pixels = new byte[FinderScreenWidth * FinderScreenHeight * 3];
            Paint(pixels, 0, 0, FinderScreenWidth, 200, 90, 140, 230);        // sky
            Paint(pixels, 300, 520, 500, 30, 220, 30, 30);                    // kerb
            Paint(pixels, 1500, 640, 60, 60, 250, 250, 250);                  // a white sticker
            if (frame % 3 == 0) Paint(pixels, 1700, 260, 30, 30, 255, 200, 0);   // marshal light
            int needle = (int)(1100 + 300 * rpm / 8000);
            Paint(pixels, needle, 800, 8, 40, 255, 40, 40);
            Paint(pixels, 100, 1000, (int)(400 * rpm / 8000), 12, 60, 230, 60);  // HUD rev bar
            return pixels;
        }

        /// <summary>Two slow sweeps from idle to the limiter and back, as the driver is asked to make.</summary>
        private static double FinderRpm(int frame, int frames) =>
            1200 + 6800 * Math.Abs(Math.Sin(2.18 * Math.PI * frame / frames + 0.2));

        private static StripFinderResult FindOn(Func<int, double, byte[]> screen, int frames = 64, Func<int, double> revs = null)
        {
            var finder = new StripFinder();
            for (int f = 0; f < frames; f++)
            {
                double rpm = revs != null ? revs(f) : FinderRpm(f, frames);
                finder.Add(PixelFrame.Rgb24(screen(f, rpm), FinderScreenWidth, FinderScreenHeight), rpm);
            }
            return finder.Find();
        }

        /// <summary>Ten lights from x, each 14 wide; light i comes on at 5000 + 250 i rpm and all blink at 7700.</summary>
        private static void PaintStrip(byte[] pixels, int frame, double rpm, int x, int y, int pitch)
        {
            bool blinkOff = rpm >= 7700 && frame % 2 == 0;
            for (int i = 0; i < 10; i++)
            {
                if (rpm < 5000 + 250 * i || blinkOff) Paint(pixels, x + i * pitch, y, 14, 12, 40, 40, 40);
                else if (i < 4) Paint(pixels, x + i * pitch, y, 14, 12, 40, 230, 40);
                else if (i < 8) Paint(pixels, x + i * pitch, y, 14, 12, 240, 220, 30);
                else Paint(pixels, x + i * pitch, y, 14, 12, 240, 30, 30);
            }
        }

        private static void FinderPicksTheStripOutOfABusyScreen()
        {
            var result = FindOn((f, rpm) => { var p = BusyScreen(f, rpm); PaintStrip(p, f, rpm, 820, 610, 24); return p; });
            Equal(null, result.Problem, "problem");
            Check(result.Strips.Count >= 1, "no strip found");
            var strip = result.Strips[0];
            Equal(10, strip.Lights, "lights");
            Check(strip.Steps >= 8, "the lights step up with the revs, saw " + strip.Steps + " steps");
            int right = 820 + 9 * 24 + 14;
            Check(strip.Box.X <= 820 && strip.Box.X >= 820 - 30, "box left " + strip.Box.X);
            Check(strip.Box.Right >= right && strip.Box.Right <= right + 30, "box right " + strip.Box.Right);
            Check(strip.Box.Y <= 610 && strip.Box.Y >= 610 - 30, "box top " + strip.Box.Y);
            Check(strip.Box.Bottom >= 622 && strip.Box.Bottom <= 622 + 30, "box bottom " + strip.Box.Bottom);

            // What the box holds must be what the capture then reads: all ten lights at full revs.
            var full = BusyScreen(1, 7600);
            PaintStrip(full, 1, 7600, 820, 610, 24);
            var blobs = new StripDetector().Detect(PixelFrame.Rgb24(full, FinderScreenWidth, FinderScreenHeight), strip.Box);
            Equal(10, blobs.Count, "lights the detector reads in the proposed box");
        }

        private static void FinderBoxesLightsThatTouch()
        {
            // Lights drawn edge to edge are one lit bar at full revs. They come on at different revs, so
            // most are still told apart, and the box takes in the whole run either way.
            var result = FindOn((f, rpm) => { var p = BusyScreen(f, rpm); PaintStrip(p, f, rpm, 820, 610, 14); return p; });
            Check(result.Strips.Count >= 1, "no strip found");
            var strip = result.Strips[0];
            Check(strip.Lights >= 6 && strip.Lights <= 10, "lights " + strip.Lights);
            Check(strip.Box.X <= 820 && strip.Box.X >= 820 - 30, "box left " + strip.Box.X);
            Check(strip.Box.Right >= 960 && strip.Box.Right <= 960 + 30, "box right " + strip.Box.Right);
        }

        private static void FinderTakesLightsThatComeOnTogether()
        {
            // Some cars light the whole strip at once at the shift point.
            var result = FindOn((f, rpm) =>
            {
                var p = BusyScreen(f, rpm);
                for (int i = 0; i < 5; i++)
                    if (rpm >= 7000) Paint(p, 900 + i * 30, 400, 16, 16, 60, 60, 255);
                return p;
            });
            Check(result.Strips.Count >= 1, "no strip found");
            Equal(5, result.Strips[0].Lights, "lights");
            Equal(1, result.Strips[0].Steps, "steps");
        }

        private static void FinderIgnoresWhatChangedWithTheClock()
        {
            // A lap timer: three digits that each come on part-way through the watch and stay on. They
            // came on while the revs were high, but the second sweep finds them lit at idle too.
            var result = FindOn((f, rpm) =>
            {
                var p = BusyScreen(f, rpm);
                for (int i = 0; i < 3; i++)
                    if (f >= 8 + 3 * i) Paint(p, 1300 + i * 30, 80, 16, 24, 255, 255, 255);
                return p;
            });
            Equal(null, result.Problem, "problem");
            Equal(0, result.Strips.Count, "strips found in a timer");
        }

        private static void FinderPutsTheLightsAboveTheirReflection()
        {
            // The strip, and the same strip mirrored dimly in the dash below it. Both follow the revs.
            var result = FindOn((f, rpm) =>
            {
                var p = BusyScreen(f, rpm);
                PaintStrip(p, f, rpm, 820, 610, 24);
                bool blinkOff = rpm >= 7700 && f % 2 == 0;
                for (int i = 0; i < 10; i++)
                    if (rpm >= 5000 + 250 * i && !blinkOff) Paint(p, 824 + i * 24, 700, 14, 12, 30, 120, 30);
                return p;
            });
            Check(result.Strips.Count >= 2, "both rows are candidates, found " + result.Strips.Count);
            Check(result.Strips[0].Box.Y < 620 && result.Strips[0].Box.Bottom < 700, "the lights come first, not the reflection at " + result.Strips[0].Box.Y);
            Check(result.Strips[1].Box.Y > 640, "and the reflection is the next candidate");
            Check(result.Strips[0].Brightness > result.Strips[1].Brightness, "ranked by how bright they are");
        }

        private static void FinderFollowsLightsThatShowLate()
        {
            // PMR draws its lights well behind the revs. Blipped in neutral, the strip is lit on the way
            // down and dark on the way up; three frames late here, with quick sweeps.
            var history = new List<double>();
            var result = FindOn((f, rpm) =>
            {
                history.Add(rpm);
                var p = BusyScreen(f, rpm);
                PaintStrip(p, f, history[Math.Max(0, f - 3)], 820, 610, 24);
                return p;
            }, frames: 90, revs: f => 1200 + 6800 * Math.Abs(Math.Sin(3.1 * Math.PI * f / 90 + 0.2)));
            Check(result.Strips.Count >= 1, "no strip found");
            Equal(10, result.Strips[0].Lights, "lights");
            Check(result.Strips[0].Box.X <= 820 && result.Strips[0].Box.Right >= 820 + 9 * 24 + 14, "box spans the strip");
        }

        private static void FinderSeesCoresThroughTheGlow()
        {
            // PMR's Toyota: each light is a small white core in a glow of its colour several lights
            // wide, and at the limiter the whole strip turns purple and floods the wheel. By colour
            // the strip is one blur. The last lights come on just under the limiter and then blink.
            var result = FindOn((f, rpm) =>
            {
                var p = BusyScreen(f, rpm);
                bool limiter = rpm >= 7700, off = limiter && f % 2 == 0;
                if (limiter && !off) Paint(p, 700, 520, 500, 220, 60, 30, 235);
                for (int i = 0; i < 10; i++)
                    if ((rpm >= 5000 + 290 * i || limiter) && !off) Paint(p, 820 - 30 + i * 42, 610 - 30, 82, 72, 30, 30, 220);
                for (int i = 0; i < 10; i++)
                    if ((rpm >= 5000 + 290 * i || limiter) && !off) Paint(p, 820 + i * 42, 610, 22, 22, 255, 215, 255);
                return p;
            }, frames: 110, revs: f => Math.Min(8000, 3500 + 5200 * Math.Abs(Math.Sin(3.2 * Math.PI * f / 110 + 0.2))));
            Check(result.Strips.Count >= 1, "no strip found");
            var strip = result.Strips[0];
            Equal(10, strip.Lights, "lights");
            Check(strip.Box.X <= 820 && strip.Box.X >= 820 - 40, "box left " + strip.Box.X);
            Check(strip.Box.Right >= 820 + 9 * 42 + 22 && strip.Box.Right <= 820 + 9 * 42 + 22 + 40, "box right " + strip.Box.Right);
            Check(strip.Box.Height <= 70, "the box is round the cores, not the glow: " + strip.Box.Height + " tall");
        }

        private static void FinderBoxTakesInAnEndLightThatMadeNoPatch()
        {
            // The last light comes on a sliver at a time, as a light blinking at the limiter can seem
            // to: it follows the revs but is no clean patch. It is still part of the strip.
            var result = FindOn((f, rpm) =>
            {
                var p = BusyScreen(f, rpm);
                PaintStrip(p, f, rpm, 820, 610, 24);
                for (int sliver = 0; sliver < 7; sliver++)
                    if (rpm >= 6400 + 200 * sliver) Paint(p, 820 + 10 * 24 + 2 * sliver, 610, 2, 12, 240, 30, 30);
                return p;
            }, frames: 120);
            Check(result.Strips.Count >= 1, "no strip found");
            int right = 820 + 10 * 24 + 14;
            Check(result.Strips[0].Box.Right >= right && result.Strips[0].Box.Right <= right + 30, "box right " + result.Strips[0].Box.Right + ", light ends at " + right);
        }

        private static void FinderProposesNothingWithoutLights()
        {
            var result = FindOn(BusyScreen);
            Equal(null, result.Problem, "problem");
            Equal(0, result.Strips.Count, "strips on a screen with no rev lights");
        }

        private static void FinderSaysWhenTheRevsDidNotMove()
        {
            var idle = FindOn((f, rpm) => { var p = BusyScreen(f, rpm); PaintStrip(p, f, rpm, 820, 610, 24); return p; },
                              revs: f => 1200 + 40 * (f % 3));
            Check(idle.Problem != null && idle.Problem.Contains("revs barely moved"), "idle: " + idle.Problem);
            Equal(0, idle.Strips.Count, "strips at idle");

            var noGame = FindOn(BusyScreen, revs: f => 0);
            Check(noGame.Problem != null && noGame.Problem.Contains("no engine speed"), "no game: " + noGame.Problem);

            var once = FindOn((f, rpm) => { var p = BusyScreen(f, rpm); PaintStrip(p, f, rpm, 820, 610, 24); return p; },
                              revs: f => 1200 + 100.0 * f);
            Check(once.Problem != null && once.Problem.Contains("only once"), "one climb: " + once.Problem);

            var brief = FindOn(BusyScreen, frames: 5);
            Check(brief.Problem != null, "five frames are too few to say anything");
        }
    }
}
