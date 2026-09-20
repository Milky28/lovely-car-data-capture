using System;
using System.Collections.Generic;
using System.Linq;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static void ConfidenceRejectsWideRepeatableCrossing()
        {
            var capture = new LedWindowCapture(1);
            for (int sweep = 0; sweep < 3; sweep++)
            {
                capture.Record("1", 4000, new[] { false });
                capture.Record("1", 5000, new[] { false });
                capture.Record("1", 6000, new[] { false });
                capture.Record("1", 7000, new[] { true });
            }

            var measured = capture.Result("1").Leds[0];
            Equal(3, measured.Climbs, "wide crossing keeps all three climbs");
            Equal(0, measured.ClimbSpread, "wide crossing has repeatable midpoints");
            Equal(1000, measured.ClimbWidth, "wide crossing keeps its per-climb width");
            Equal(1000, measured.Uncertainty.Value, "wide crossing keeps its raw bounds");
            Equal(6500, measured.Rpm, "wide crossing midpoint");

            var session = new CaptureSession("F12025", "wide-crossing");
            for (int sweep = 0; sweep < 3; sweep++)
            {
                session.F1.Record("1", 4000, 0);
                session.F1.Record("1", 5000, 0);
                session.F1.Record("1", 6000, 0);
                session.F1.Record("1", 7000, 1);
            }
            var result = Compose(session, null);
            Equal(0, result.Profile.LedRpm["1"][1], "new F1 car does not write a wide crossing");
            Check(result.Report.Any(line => line.Contains("measured too loosely to use")),
                  "report explains why the wide crossing was rejected");
        }

        private static void ConfidenceRejectsWideScreenFallback()
        {
            var session = new CaptureSession("Automobilista2", "wide-screen-crossing");
            long time = 0;
            foreach (var gear in new[] { "1", "2" })
                for (int sweep = 0; sweep < 3; sweep++)
                {
                    ScreenFrame(session, gear, 4000, false, ref time);
                    ScreenFrame(session, gear, 5000, false, ref time);
                    ScreenFrame(session, gear, 6000, false, ref time);
                    ScreenFrame(session, gear, 7000, true, ref time);
                }

            var result = Compose(session, null);
            Check(result.Profile.LedRpm.Values.All(row => row[1] == 0),
                  "new screen car does not smuggle a wide crossing through the fallback");
            Check(!result.Report.Any(line => line.Contains("imperfect switch-on readings")),
                  "wide crossing is absent from the fallback note");
        }

        private static void ConfidenceAcceptsNarrowOverlappingCrossings()
        {
            var session = new CaptureSession("F12025", "narrow-crossing");
            for (int sweep = 0; sweep < 3; sweep++)
            {
                session.F1.Record("1", 4000, 0);
                session.F1.Record("1", 5000 + sweep * 50, 0);
                session.F1.Record("1", 5100 + sweep * 50, 1);
            }

            var result = Compose(session, null);
            Equal(5100, result.Profile.LedRpm["1"][1], "narrow overlapping crossings remain trusted");
            Check(!result.Report.Any(line => line.Contains("LED 1 measured too loosely")),
                  "narrow overlapping crossings are not reported as loose");
        }

        private static void ConfidenceAcceptsExactSingleCrossing()
        {
            var session = new CaptureSession("F12025", "exact-crossing");
            session.F1.Record("1", 4000, 0);
            session.F1.Record("1", 5000, 0);
            session.F1.Record("1", 5000, 1);

            var result = Compose(session, null);
            Equal(5000, result.Profile.LedRpm["1"][1], "an exact dark-to-lit crossing remains trusted");
            Check(!result.Report.Any(line => line.Contains("LED 1 measured too loosely")),
                  "an exact crossing is not reported as inconsistent");
        }

        private static void ConfidenceRejectsOpposingLagResiduals()
        {
            var capture = new ScreenLedCapture();
            var rises = new[] { 5000, 5200, 5400, 5600 };
            var falls = new[] { 4600, 4900, 5300, 5700 };
            long time = 0;
            for (int sweep = 0; sweep < 3; sweep++)
            {
                for (int rpm = 4000; rpm <= 7000; rpm += 100)
                    RecordLagFrame(capture, rises, falls, rpm, true, ref time);
                for (int rpm = 7000; rpm >= 4000; rpm -= 100)
                    RecordLagFrame(capture, rises, falls, rpm, false, ref time);
            }

            var result = capture.Result();
            Equal(0, result.DisplayLagMs, "opposing residuals do not create a false display lag");
        }

        private static void ScreenFrame(CaptureSession session, string gear, int rpm, bool lit, ref long time)
        {
            session.Screen.Record(gear, rpm, time += 50, lit ? new List<LitBlob>
            {
                new LitBlob { Left = 91, Right = 109, Color = new LedColor(0, 255, 0) }
            } : new List<LitBlob>());
        }

        private static void RecordLagFrame(ScreenLedCapture capture, int[] rises, int[] falls, int rpm,
                                           bool climbing, ref long time)
        {
            var blobs = new List<LitBlob>();
            for (int led = 0; led < rises.Length; led++)
            {
                int threshold = climbing ? rises[led] : falls[led];
                if (rpm <= threshold) continue;
                blobs.Add(new LitBlob
                {
                    Left = 91 + led * 30,
                    Right = 109 + led * 30,
                    Color = new LedColor(0, 255, 0),
                });
            }
            capture.Record("N", rpm, time += 50, blobs);
        }
    }
}
