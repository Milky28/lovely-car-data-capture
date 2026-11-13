using System;
using System.Collections.Generic;
using System.Linq;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Profile;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        /// <summary>
        /// Head movement as a capture sees it: every light in a frame moved by the same whole number of
        /// pixels, a slow sway with a quicker one on top and a little shake. Peak is the furthest the
        /// strip goes either way, so it ranges over about twice that.
        /// </summary>
        private static int Sway(long timeMs, double peak)
        {
            double s = timeMs / 1000.0;
            return (int)Math.Round(peak * (0.6 * Math.Sin(2 * Math.PI * s / 3.7 + 1) + 0.4 * Math.Sin(2 * Math.PI * s / 1.3 + 2))
                                   + Math.Min(2, peak * 0.1) * Math.Sin(2 * Math.PI * s / 0.11 + 1));
        }

        private static CaptureSession SwayedSession(string file, double peak)
        {
            var session = new CaptureSession("Game", "car");
            foreach (var f in LoadFrames(DataPath(file)))
            {
                int dx = peak > 0 ? Sway(f.TimeMs, peak) : 0;
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs,
                    f.Blobs.Select(b => new LitBlob { Left = b.Left + dx, Right = b.Right + dx, Color = b.Color }).ToList());
            }
            return session;
        }

        private static void CameraSwayLeavesTheCarFileUnchanged()
        {
            // One of each kind of strip: filling from both ends (Ginetta), with a middle light that only
            // lights alone (Storm), stray highlights at the ends (McLaren), a logo between the lights
            // (SC63), top lights seen only at the limiter (C7.R), four-dot clusters (CLK LM), and a
            // tightly packed strip (RaceRoom's 911 Cup).
            foreach (string file in new[]
            {
                "acc-ginetta-g55-gt4.frames.csv", "pmr-storm-gt.frames.csv", "ac-mclaren-mp412c-gt3.frames.csv",
                "lmu-lamborghini-sc63.frames.csv", "pmr-corvette-c7-r.csv", "ams2-mercedes-clk-lm.frames.csv",
                "rrre-porsche-911-gt3-cup-992.csv",
            })
            {
                string steady = ProfileComposer.Compose(SwayedSession(file, 0), new CaptureSettings(), null, When).Profile.ToJson();
                foreach (double peak in new[] { 6.0, 12.0 })
                {
                    var session = SwayedSession(file, peak);
                    var screen = session.Screen.Result();
                    Check(screen.CameraMovementPx > StripAlignment.SteadyPx, file + ": " + peak + " px of sway is noticed (" + screen.CameraMovementPx + ")");
                    Check(screen.Notes.Any(n => n.Contains("camera moved")), file + ": the report says the camera moved");
                    Equal(steady, ProfileComposer.Compose(session, new CaptureSettings(), null, When).Profile.ToJson(),
                        file + " with the strip swaying " + peak + " px either way");
                }
            }
        }

        private static void SteadyCapturesAreLeftAsRecorded()
        {
            // The Viper's outer lights sit 8 px apart depending on how far they've come on, and the
            // McLaren has stray highlights up to 16 px from its end lights. Neither is the camera.
            foreach (string file in new[] { "acc-ginetta-g55-gt4.frames.csv", "pmr-srt-viper-gts-r.frames.csv", "ac-mclaren-mp412c-gt3.frames.csv" })
            {
                var screen = SwayedSession(file, 0).Screen.Result();
                Equal(0, screen.CameraMovementPx, file + " has no camera movement to remove");
                Check(!screen.Notes.Any(n => n.Contains("camera moved")), file + ": nothing about the camera in the report");
            }
        }

        private static void AlignmentFollowsAClimbOutOfTheDark()
        {
            // Ten evenly spaced lights filling from the left, 30 px apart. Each sweep starts dark, the
            // camera having moved 12 px since the strip was last seen: the first light alone could be
            // itself or its neighbour, and only following the climb back from the full strip tells.
            var frames = new List<double[]>();
            var times = new List<long>();
            var moved = new List<int>();
            long t = 0;
            for (int sweep = 0; sweep < 4; sweep++)
            {
                int offset = sweep % 2 == 0 ? 0 : 12;
                for (int dark = 0; dark < 60; dark++) { frames.Add(new double[0]); times.Add(t += 17); moved.Add(0); }
                for (int lit = 1; lit <= 10; lit++)
                    for (int hold = 0; hold < 8; hold++)
                    {
                        int dx = offset + (hold % 2);
                        frames.Add(Enumerable.Range(0, lit).Select(i => 100.0 + i * 30 + dx).ToArray());
                        times.Add(t += 17);
                        moved.Add(dx);
                    }
            }
            var alignment = StripAlignment.Measure(frames, times);
            Check(alignment != null && alignment.Moved, "the movement is noticed");
            int reference = alignment.Shift[frames.Count - 1] - moved[frames.Count - 1];
            for (int i = 0; i < frames.Count; i++)
                if (frames[i].Length > 0)
                    Check(Math.Abs(alignment.Shift[i] - moved[i] - reference) <= 1,
                        "frame " + i + " with " + frames[i].Length + " lights: shift " + alignment.Shift[i] + " for a move of " + moved[i]);
        }
    }
}
