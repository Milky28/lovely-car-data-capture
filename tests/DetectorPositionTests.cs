using System;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static void DetectorKeepsCurvedLedsAboveLogo()
        {
            var detector = new StripDetector();
            var full = LoadFrame("lmu-sc63-curve-full.png");
            var region = new PixelRect(0, 0, full.Width, full.Height);
            for (int i = 0; i < 8; i++)
                Equal(10, detector.Detect(full, region).Count, "all ten curved LEDs calibrate");

            var yellow = detector.Detect(LoadFrame("lmu-sc63-curve-yellow.png"), region);
            Equal(8, yellow.Count, "outer LEDs and raised centre LEDs remain visible");
            Check(yellow[3].Color.Saturation > 0.9, "logo does not wash out the blue LED above it");
            Check(yellow[5].Color.Hue > 45 && yellow[5].Color.Hue < 75, "yellow group retains its colour");

            var two = detector.Detect(LoadFrame("lmu-sc63-curve-two-logo.png"), region);
            Equal(2, two.Count, "logo below the unlit fourth LED is ignored");
            var three = detector.Detect(LoadFrame("lmu-sc63-curve-three-logo.png"), region);
            Equal(3, three.Count, "logo cannot create an early fourth threshold");
            Equal(10, detector.Detect(full, region).Count, "dark slots still light again after filtering");
        }

        private static void DetectorKeepsCalibrationWhileStripIsDark()
        {
            var detector = new StripDetector();
            var full = LoadFrame("lmu-sc63-wide-full.png");
            var region = new PixelRect(0, 0, full.Width, full.Height);
            for (int i = 0; i < 8; i++) detector.Detect(full, region);
            var dark = LoadFrame("lmu-sc63-wide-logo-only.png");
            for (int i = 0; i < 12; i++)
                Equal(0, detector.Detect(dark, region).Count, "dark strip does not relearn the logo as a light");
            var red = detector.Detect(LoadFrame("lmu-sc63-wide-red-logo.png"), region);
            Equal(10, red.Count, "redline colour change retains ten LEDs and excludes the extra logo blob");
            foreach (var blob in red)
                Check(blob.Color.R > blob.Color.B * 2, "redline colour stays red");
        }

        private static void DetectorLearnsLaterLightsAndResetsForRegion()
        {
            var detector = new StripDetector();
            var region = new PixelRect(0, 0, 220, 120);
            Equal(2, detector.Detect(PositionFrame(2, 0), region).Count, "first lights start calibration");
            Equal(5, detector.Detect(PositionFrame(5, 0), region).Count, "later lights establish their own height");
            Equal(5, detector.Detect(PositionFrame(5, 4), region).Count, "small camera movement keeps the lights");

            // The new box belongs to another view; old positions must not hide its LEDs.
            var movedRegion = new PixelRect(0, 12, 220, 108);
            Equal(5, detector.Detect(PositionFrame(5, 25), movedRegion).Count, "a changed box clears old positions");
            Equal(5, new StripDetector().Detect(PositionFrame(5, 25), region).Count, "new captures start independently");
        }

        private static PixelFrame PositionFrame(int lights, int offset)
        {
            var pixels = new byte[220 * 120 * 3];
            var tops = new[] { 45, 25, 15, 25, 45 };
            for (int led = 0; led < lights; led++)
                for (int x = 20 + led * 40; x < 32 + led * 40; x++)
                    for (int y = tops[led] + offset; y < tops[led] + offset + 12; y++)
                    {
                        int index = (y * 220 + x) * 3;
                        pixels[index] = 20;
                        pixels[index + 1] = 30;
                        pixels[index + 2] = 230;
                    }
            return PixelFrame.Rgb24(pixels, 220, 120);
        }
    }
}
