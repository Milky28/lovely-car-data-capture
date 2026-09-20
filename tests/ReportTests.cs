using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Profile;
using LovelyCarDataCapture.Screen;
using Newtonsoft.Json.Linq;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static void ReportNamesFinalLmuSources()
        {
            var full = LmuSession("N", "1", "2", "3", "4");
            var baseline = ProfileComposer.Compose(full, new CaptureSettings(), null, When).Profile;
            var previous = JObject.Parse(baseline.ToJson());
            OffsetRow(previous, "4", 777);
            var lookup = Found(baseline.ToJson(), "lmu/lamborghini-iron-lynx-2024.json");
            var overrides = File.ReadAllText(DataPath("lmu-sc63.overrides.json"));
            var result = ProfileComposer.Compose(LmuSession("1", "2", "3"), new CaptureSettings(), lookup, When,
                localOverrides: overrides, previousExport: previous.ToString());
            var report = string.Join("\n", result.Report);

            Check(report.Contains("Final value sources:"), "report has a final source summary");
            Check(report.Contains("previous local values are final for retained gears") && report.Contains("4"),
                  "report identifies retained gear 4");
            Check(report.Contains("confirmed local overrides are final for LED 6, 7, 8"),
                  "report identifies final local color overrides");
            Check(!result.Report.Any(line => line.Contains("Colors on screen suggest") &&
                  (line.Contains("LED 6 ") || line.Contains("LED 7 ") || line.Contains("LED 8 "))),
                  "confirmed overrides remove resolved color warnings");
            Check(!report.Contains("kept the repo values"),
                  "final report does not call retained or overridden values repo fallbacks");
        }

        private static void ReportNamesBlinkOverrideAsFinal()
        {
            var session = new CaptureSession("AssettoCorsaCompetizione", "mclaren_720s_gt3_evo");
            foreach (var frame in LoadFrames(DataPath("acc-mclaren-720s-gt3-evo.frames.csv")))
                session.Screen.Record(frame.Gear, frame.Rpm, frame.TimeMs, frame.Blobs);
            var lookup = Found(AccMcLarenJson(), "assettocorsacompetizione/mclaren-720s-gt3-evo.json");
            var result = ProfileComposer.Compose(session, new CaptureSettings(), lookup, When,
                localOverrides: File.ReadAllText(DataPath("acc-mclaren.overrides.json")));
            var report = string.Join("\n", result.Report);

            Equal(200, result.Profile.RedlineBlinkInterval, "override blink interval");
            Check(report.Contains("Redline blink: confirmed local override 200 ms is final."),
                  "report identifies final blink override");
            Check(!report.Contains("repo value of 250 ms remains final"),
                  "report does not claim the superseded repo blink is final");
        }

        private static void ReportDistinguishesPooledAndUnresolvedScreenValues()
        {
            var lookup = Found(TwoLedReportRepo(), "automobilista2/report-pooled.json");
            var pooled = ProfileComposer.Compose(PooledReportSession(true), new CaptureSettings(), lookup, When);
            var unresolved = ProfileComposer.Compose(PooledReportSession(false), new CaptureSettings(), lookup, When);
            var blue = JObject.Parse(TwoLedReportRepo());
            blue["ledColor"] = new JArray("#00000000", "#FF0000FF", "#FF0000FF");
            var partialOverride = ProfileComposer.Compose(PooledReportSession(true), new CaptureSettings(),
                Found(blue.ToString(), "automobilista2/report-pooled.json"), When,
                localOverrides: "{\"game\":\"automobilista2\",\"carId\":\"Report Pooled\",\"ledNumber\":2,\"ledColor\":{\"1\":\"#FF00FF00\"}}");
            var colorNote = partialOverride.Report.FirstOrDefault(line => line.Contains("Colors on screen suggest"));
            Check(colorNote != null && colorNote.Contains("LED 2 ") && !colorNote.Contains("LED 1 "),
                  "a partial override removes only the resolved color warning");

            Check(pooled.Profile.LedRpm["1"][2] != CarProfile.Parse(TwoLedReportRepo()).LedRpm["1"][2],
                  "pooled capture replaces the missing LED with another gear's value");
            Check(!pooled.Report.Any(line => line.Contains("has no distinct trusted capture value")),
                  "resolved pooled LED has no unresolved final warning");
            Check(unresolved.Report.Any(line => line.Contains("Gear 1: LED 2 has no distinct trusted capture value")),
                  "unresolved LED names its final repo fallback");
        }

        private static CaptureSession PooledReportSession(bool includeSecondGear)
        {
            var session = new CaptureSession("Automobilista2", "Report Pooled");
            long time = 0;
            foreach (var gear in includeSecondGear ? new[] { "1", "2" } : new[] { "1" })
                for (int sweep = 0; sweep < 2; sweep++)
                {
                    for (int rpm = 4500; rpm <= 7000; rpm += 20)
                    {
                        var blobs = new List<LitBlob>();
                        if (rpm > 5000) blobs.Add(new LitBlob { Left = 91, Right = 109, Color = new LedColor(0, 255, 0) });
                        if (includeSecondGear && gear == "2" && rpm > 6000)
                            blobs.Add(new LitBlob { Left = 121, Right = 139, Color = new LedColor(0, 255, 0) });
                        session.Screen.Record(gear, rpm, time += 16, blobs);
                    }
                    for (int rpm = 7000; rpm >= 4500; rpm -= 40)
                        session.Screen.Record(gear, rpm, time += 16, new List<LitBlob>());
                }
            if (!includeSecondGear)
                for (int i = 0; i < 40; i++)
                    session.Screen.Record("1", 6800, time += 16, new List<LitBlob> {
                        new LitBlob { Left = 91, Right = 109, Color = new LedColor(0, 255, 0) },
                        new LitBlob { Left = 121, Right = 139, Color = new LedColor(0, 255, 0) }
                    });
            return session;
        }

        private static string TwoLedReportRepo() => @"{
  ""carName"": ""Report Pooled"",
  ""carId"": ""Report Pooled"",
  ""carClass"": ""GT3"",
  ""ledNumber"": 2,
  ""redlineBlinkInterval"": 0,
  ""ledColor"": [""#FFFF0000"", ""#FF00FF00"", ""#FFFFFF00""],
  ""ledRpm"": [{
    ""R"": [7500, 5000, 6200],
    ""N"": [7500, 5000, 6200],
    ""1"": [7500, 5000, 6200],
    ""2"": [7500, 5000, 6300]
  }]
}";
    }
}
