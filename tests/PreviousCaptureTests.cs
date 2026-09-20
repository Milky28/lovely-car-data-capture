using System;
using System.Collections.Generic;
using System.Linq;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Profile;
using Newtonsoft.Json.Linq;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static void PreviousCaptureKeepsUndrivenRows()
        {
            var full = LmuSession("N", "1", "2", "3", "4");
            var baseline = ProfileComposer.Compose(full, new CaptureSettings(), null, When).Profile;
            var previous = JObject.Parse(baseline.ToJson());
            foreach (var gear in new[] { "N", "3", "4" })
                OffsetRow(previous, gear, 777);
            previous["redlineBlinkInterval"] = 17;
            var previousColors = (JArray)previous["ledColor"];
            for (int i = 0; i < previousColors.Count; i++) previousColors[i] = "#FF123456";

            var recapture = LmuSession("1", "2", "3");
            var settings = new CaptureSettings { CopyMeasuredToOtherGears = false };
            var lookup = Found(baseline.ToJson(), "lmu/lamborghini-iron-lynx-2024.json");
            var without = ProfileComposer.Compose(recapture, settings, lookup, When).Profile;
            var with = ProfileComposer.Compose(recapture, settings, lookup, When, previousExport: previous.ToString()).Profile;

            Check(with.GearOrder.Contains("N") && with.GearOrder.Contains("4"), "previous gears were restored");
            SeqEqual(Row(previous, "N"), with.LedRpm["N"], "undriven neutral row is retained");
            SeqEqual(Row(previous, "4"), with.LedRpm["4"], "undriven fourth gear row is retained");
            SeqEqual(without.LedRpm["3"], with.LedRpm["3"], "a captured gear still uses the new measurement");
            for (int i = 0; i <= with.LedNumber; i++)
                Equal(without.LedColor[i], with.LedColor[i], "previous colors are not carried into LED " + i);
            Equal(without.RedlineBlinkInterval, with.RedlineBlinkInterval, "previous blink interval is not carried");

            var copied = new CaptureSettings { CopyMeasuredToOtherGears = true };
            var copyWithout = ProfileComposer.Compose(recapture, copied, lookup, When).Profile;
            var copyWith = ProfileComposer.Compose(recapture, copied, lookup, When, previousExport: previous.ToString()).Profile;
            Check(copyWith.LedRpm["4"].SequenceEqual(copyWithout.LedRpm["4"]),
                  "CopyMeasuredToOtherGears opts out of previous-row restoration");
            Equal(copyWithout.ToJson(), copyWith.ToJson(), "copy-measured behavior is unchanged by previous export");
        }

        private static void PreviousCaptureRejectsInvalidRows()
        {
            var full = LmuSession("N", "1", "2", "3", "4");
            var baseline = ProfileComposer.Compose(full, new CaptureSettings(), null, When).Profile;
            var recapture = LmuSession("1", "2", "3");
            var settings = new CaptureSettings();
            var lookup = Found(baseline.ToJson(), "lmu/lamborghini-iron-lynx-2024.json");
            var without = ProfileComposer.Compose(recapture, settings, lookup, When).Profile;

            var variants = new List<JObject>();
            var wrongId = JObject.Parse(baseline.ToJson());
            wrongId["carId"] = "another car";
            variants.Add(wrongId);
            var wrongCount = JObject.Parse(baseline.ToJson());
            wrongCount["ledNumber"] = baseline.LedNumber - 1;
            variants.Add(wrongCount);
            var wrongGap = JObject.Parse(baseline.ToJson());
            ((JArray)wrongGap["ledColor"])[1] = "#00000000";
            variants.Add(wrongGap);
            var shortRow = JObject.Parse(baseline.ToJson());
            ((JArray)((JObject)((JArray)shortRow["ledRpm"])[0])["4"]).RemoveAt(baseline.LedNumber);
            variants.Add(shortRow);

            foreach (var previous in variants.Take(3))
            {
                var result = ProfileComposer.Compose(recapture, settings, lookup, When, previousExport: previous.ToString());
                Equal(without.ToJson(), result.Profile.ToJson(), "invalid previous export is ignored");
                Check(result.Report.Any(line => line.IndexOf("previous", StringComparison.OrdinalIgnoreCase) >= 0),
                      "invalid previous export is explained");
            }

            var shortRowResult = ProfileComposer.Compose(recapture, settings, lookup, When, previousExport: variants[3].ToString());
            Check(shortRowResult.Profile.LedRpm["4"].SequenceEqual(without.LedRpm["4"]),
                  "malformed undriven row is not reused");
            SeqEqual(Row(variants[3], "N"), shortRowResult.Profile.LedRpm["N"],
                     "other valid previous rows remain reusable");
            Check(shortRowResult.Report.Any(line => line.IndexOf("previous", StringComparison.OrdinalIgnoreCase) >= 0),
                  "malformed previous row is explained");
        }

        private static CaptureSession LmuSession(params string[] gears)
        {
            var wanted = new HashSet<string>(gears);
            var session = new CaptureSession("LMU", "Lamborghini Iron Lynx 2024");
            foreach (var frame in LoadFrames(DataPath("lmu-lamborghini-sc63.frames.csv")))
                if (wanted.Contains(frame.Gear)) session.Screen.Record(frame.Gear, frame.Rpm, frame.TimeMs, frame.Blobs);
            return session;
        }

        private static int[] Row(JObject profile, string gear) =>
            ((JArray)((JObject)((JArray)profile["ledRpm"])[0])[gear]).Select(v => (int)v).ToArray();

        private static void OffsetRow(JObject profile, string gear, int amount)
        {
            var row = (JArray)((JObject)((JArray)profile["ledRpm"])[0])[gear];
            for (int i = 0; i < row.Count; i++) if ((int)row[i] != 0) row[i] = (int)row[i] + amount;
        }
    }
}
