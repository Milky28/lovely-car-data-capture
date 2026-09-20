using System;
using System.IO;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Profile;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static void ScreenRealPmrMustang()
        {
            // The owner confirmed this exported capture looked virtually perfect in game.
            // Preserve its output as a regression reference, not a claim of exact simulator constants.
            var expected = CarProfile.Parse(File.ReadAllText(DataPath("pmr-mustang-gt3.confirmed.json")));
            var session = new CaptureSession("ProjectMotorRacing", "Mustang GT3");
            session.RecordCar("Mustang GT3", "GT3");
            session.Redline.Record("1", 0, 0, 0, 0, 6);
            foreach (var f in LoadFrames(DataPath("pmr-mustang-gt3.frames.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, When);
            var actual = result.Profile;
            Equal(expected.LedNumber, actual.LedNumber, "Mustang layout");
            Equal(expected.RedlineBlinkInterval, actual.RedlineBlinkInterval, "Mustang blink duration");
            for (int i = 0; i < expected.LedColor.Count; i++)
                Equal(expected.LedColor[i], actual.LedColor[i], "Mustang colour " + i);
            foreach (var gear in expected.GearOrder)
            {
                var row = actual.LedRpm[gear];
                // The original third-gear export averaged its dark-first onset with a much lower
                // release crossing. Raw frames show normal at 8157 and the first dark frame at
                // 8167, so protect that evidence instead of the old 8105-rpm export error.
                int expectedRedline = gear == "3" ? 8162 : expected.LedRpm[gear][0];
                Check(Math.Abs(row[0] - expectedRedline) <= 30,
                      "Mustang gear " + gear + " redline follows the observed onset: expected near " + expectedRedline + ", got " + row[0]);
                for (int i = 1; i < row.Length; i++)
                    Check(Math.Abs(row[i] - expected.LedRpm[gear][i]) <= (i == 0 ? 30 : 20),
                          "Mustang gear " + gear + " slot " + i + " preserves confirmed capture: expected " +
                          expected.LedRpm[gear][i] + ", got " + row[i]);
                for (int left = 1; left <= 5; left++)
                    Equal(row[left], row[11 - left], "Mustang mirrored pair " + left);
            }
            Equal(0, result.AtsrProblems.Count, "Mustang ATSR compatibility");
        }

        private static void ScreenRealPmrMustangDarkFirstFlash()
        {
            // The second drive exposed a dark flash before blue appeared. The old export bridged
            // that phase and started the wheel late; all ordinary LED values should stay intact.
            var before = CarProfile.Parse(File.ReadAllText(DataPath("pmr-mustang-gt3-second.before-fix.json")));
            var session = new CaptureSession("ProjectMotorRacing", "Mustang GT3");
            session.RecordCar("Mustang GT3", "GT3");
            session.Redline.Record("1", 0, 0, 0, 0, 6);
            foreach (var f in LoadFrames(DataPath("pmr-mustang-gt3-second.frames.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, When);
            var actual = result.Profile;
            Check(Math.Abs(actual.LedRpm["2"][0] - 8147) <= 10,
                  "Mustang second gear starts at its first dark flash near 8147, got " + actual.LedRpm["2"][0]);
            Check(Math.Abs(actual.LedRpm["3"][0] - 8177) <= 10,
                  "Mustang third gear starts at its first dark flash near 8177, got " + actual.LedRpm["3"][0]);
            Equal(before.LedRpm["1"][0], actual.LedRpm["1"][0], "blue-first gear keeps its measured onset");
            Equal(before.LedNumber, actual.LedNumber, "second Mustang layout");
            Equal(before.RedlineBlinkInterval, actual.RedlineBlinkInterval, "second Mustang blink duration");
            for (int i = 0; i < before.LedColor.Count; i++)
                Equal(before.LedColor[i], actual.LedColor[i], "second Mustang colour " + i);
            foreach (var gear in before.GearOrder)
                for (int i = 1; i <= before.LedNumber; i++)
                {
                    // Gear 3 measured this pair dark and lit at 6980. Accepting that exact
                    // crossing replaces the old 6970 fallback borrowed from the other gears.
                    int expected = gear == "3" && (i == 2 || i == 9) ? 6980 : before.LedRpm[gear][i];
                    Equal(expected, actual.LedRpm[gear][i], "ordinary LED in gear " + gear + " slot " + i);
                }
            Equal(0, result.AtsrProblems.Count, "second Mustang ATSR compatibility");
        }
    }
}
