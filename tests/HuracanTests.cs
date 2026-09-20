using System;
using System.IO;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Profile;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static void ScreenRealPmrHuracanCenterLeds()
        {
            foreach (bool second in new[] { false, true })
            {
                string stem = "pmr-huracan-gt3-evo2" + (second ? "-second" : "");
                var before = CarProfile.Parse(File.ReadAllText(DataPath(stem +
                    (second ? ".before-fix.json" : ".confirmed.json"))));
                var session = new CaptureSession("ProjectMotorRacing", before.CarId);
                session.RecordCar(before.CarName, "GT3");
                session.Redline.Record("1", 0, 0, 0, 0, 6);
                foreach (var f in LoadFrames(DataPath(stem + ".frames.csv")))
                    session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
                var result = ProfileComposer.Compose(session, new CaptureSettings(), null, When);
                var actual = result.Profile;
                Equal(12, actual.LedNumber, stem + " layout");
                Equal(before.RedlineBlinkInterval, actual.RedlineBlinkInterval, stem + " blink");
                for (int i = 0; i < before.LedColor.Count; i++)
                    Equal(before.LedColor[i], actual.LedColor[i], stem + " colour " + i);
                foreach (string gear in before.GearOrder)
                {
                    var row = actual.LedRpm[gear];
                    for (int i = 0; i < row.Length; i++)
                    {
                        if (i == 3 || i == 10) { Equal(0, row[i], "gap stays dark"); continue; }
                        // Saved images show 6->8 lights at 8058 rpm and 8->10 at 8436 rpm.
                        // Both boundaries have the same rounded RPM on their dark and lit frames.
                        int expected = second && (i == 5 || i == 8) ? 8060 :
                                       second && (i == 6 || i == 7) ? 8435 : before.LedRpm[gear][i];
                        Check(row[i] > 0 && Math.Abs(row[i] - expected) <= 20,
                              stem + " gear " + gear + " LED " + i + " expected near " + expected + ", got " + row[i]);
                    }
                    for (int i = 1; i <= 6; i++) Equal(row[i], row[13 - i], "mirrored pair " + i);
                }
                Equal(0, result.AtsrProblems.Count, stem + " has no always-on LEDs");
            }
        }
    }
}
