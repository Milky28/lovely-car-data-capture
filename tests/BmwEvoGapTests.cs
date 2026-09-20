using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Profile;
using Newtonsoft.Json.Linq;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static void ScreenRealAcevoBmwKeepsGapSlotsOff()
        {
            var before = BmwEvoBeforeFix();
            var lookup = Found(before.ToJson(), "assettocorsaevo/bmw-m4-gt3-evo.json");
            var session = BmwEvoSession("1", "2", "3", "4", "5", "6");
            var result = ProfileComposer.Compose(session, new CaptureSettings(), lookup, When);
            var actual = result.Profile;

            Equal(12, actual.LedNumber, "BMW EVO strip slots");
            AssertBmwEvoGapsOff(result, before, "full capture");
            Check(!result.AtsrProblems.Any(p => p.IndexOf("always", StringComparison.OrdinalIgnoreCase) >= 0),
                  "full capture has no ATSR always-on LEDs: " + string.Join("; ", result.AtsrProblems));

            var copied = ProfileComposer.Compose(session,
                new CaptureSettings { CopyMeasuredToOtherGears = true }, lookup, When);
            AssertBmwEvoGapsOff(copied, before, "copy-measured capture");
        }

        private static void ScreenRealAcevoBmwRepeatedCaptureKeepsUndrivenGear()
        {
            var before = BmwEvoBeforeFix();
            var lookup = Found(before.ToJson(), "assettocorsaevo/bmw-m4-gt3-evo.json");
            var first = ProfileComposer.Compose(BmwEvoSession("1", "2", "3", "4", "5", "6"),
                new CaptureSettings(), lookup, When);

            var previous = JObject.Parse(first.Profile.ToJson());
            var previousGear4 = (JArray)((JObject)((JArray)previous["ledRpm"])[0])["4"];
            previousGear4[0] = 8123;
            previousGear4[1] = 6123;

            var repeated = ProfileComposer.Compose(BmwEvoSession("1", "2", "3", "5", "6"),
                new CaptureSettings(), lookup, When, previousExport: previous.ToString());
            var gear4 = repeated.Profile.LedRpm["4"];
            Equal(8123, gear4[0], "retained gear 4 redline");
            Equal(6123, gear4[1], "retained gear 4 non-gap LED");
            AssertBmwEvoGapsOff(repeated, before, "repeated capture");
            Check(!repeated.AtsrProblems.Any(p => p.IndexOf("always", StringComparison.OrdinalIgnoreCase) >= 0),
                  "repeated capture has no ATSR always-on LEDs: " + string.Join("; ", repeated.AtsrProblems));
        }

        private static CarProfile BmwEvoBeforeFix() =>
            CarProfile.Parse(File.ReadAllText(DataPath("acevo-bmw-m4-gt3-evo.before-fix.json")));

        private static CaptureSession BmwEvoSession(params string[] gears)
        {
            var wanted = new HashSet<string>(gears);
            var session = new CaptureSession("AssettoCorsaEvo", "BMW M4 GT3 Evo");
            foreach (var frame in LoadFrames(DataPath("acevo-bmw-m4-gt3-evo.frames.csv")))
                if (wanted.Contains(frame.Gear)) session.Screen.Record(frame.Gear, frame.Rpm, frame.TimeMs, frame.Blobs);
            return session;
        }

        private static void AssertBmwEvoGapsOff(ComposeResult result, CarProfile before, string label)
        {
            var p = result.Profile;
            foreach (int slot in new[] { 3, 10 })
            {
                Check(LedLayout.IsGapColor(p.LedColor[slot]), label + " LED " + slot + " is black");
                foreach (var gear in p.GearOrder)
                    Equal(0, p.LedRpm[gear][slot], label + " gear " + gear + " LED " + slot + " RPM");
            }
            foreach (int slot in Enumerable.Range(1, p.LedNumber).Except(new[] { 3, 10 }))
                Equal(before.LedColor[slot], p.LedColor[slot], label + " LED " + slot + " keeps its repo color");
        }
    }
}
