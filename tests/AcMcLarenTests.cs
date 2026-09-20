using System;
using System.Linq;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Profile;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static void ScreenRealAcMcLarenKeepsEveryLight()
        {
            // The saved full-strip image shows four green, four red and four blue LEDs, no gaps.
            var session = new CaptureSession("AssettoCorsa", "mclaren_mp412c_gt3");
            foreach (var frame in LoadFrames(DataPath("ac-mclaren-mp412c-gt3.frames.csv")))
                session.Screen.Record(frame.Gear, frame.Rpm, frame.TimeMs, frame.Blobs);
            var screen = session.Screen.Result();
            Equal(12, screen.Layout.LedNumber, "all twelve physical lights");
            Equal(0, screen.Layout.GapCount, "no green or red LEDs mistaken for gaps");
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, When);
            var p = result.Profile;
            var thresholds = new[] { 6100, 6250, 6400, 6500, 6600, 6700, 6800, 6905, 7100, 7100, 7100, 7100 };
            for (int led = 1; led <= 12; led++)
            {
                Equal(led <= 4 ? "#FF00FF00" : led <= 8 ? "#FFFF0000" : "#FF0000FF", p.LedColor[led], "LED " + led + " color");
                foreach (var row in p.LedRpm.Values)
                    Check(Math.Abs(row[led] - thresholds[led - 1]) <= 20, "LED " + led + " retains its measured onset: " + row[led]);
            }
            Equal(p.ToJson(), ProfileComposer.Compose(session, new CaptureSettings(), null, When).Profile.ToJson(),
                "reprocessing preserves raw frame data");
            Check(!result.AtsrProblems.Any(), "no ATSR mapping problems");
        }
    }
}
