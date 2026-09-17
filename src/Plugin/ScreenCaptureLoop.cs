using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Plugin
{
    /// <summary>What to record a frame against: the car being captured, and where the revs are right now.</summary>
    internal sealed class ScreenTarget
    {
        public string Gear;
        public int Rpm;
        public ScreenLedCapture Capture;
    }

    /// <summary>
    /// Reads the capture box off the screen on its own thread and feeds what it finds to the capture.
    /// </summary>
    /// <remarks>
    /// Kept off SimHub's data thread: a screen copy takes a few milliseconds, which is fine here and
    /// not fine there. The gear and RPM come from the newest telemetry frame instead of being read out
    /// of the picture, so unlike a recording there's nothing lagging behind.
    /// </remarks>
    internal sealed class ScreenCaptureLoop : IDisposable
    {
        private readonly StripDetector _detector = new StripDetector();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Thread _thread;
        private volatile bool _running;
        private PixelRect _region;
        private int _intervalMs = 33;

        /// <summary>Returns what the frame should be recorded against, or null to drop it.</summary>
        public Func<ScreenTarget> Target;

        private volatile string _status = "off";
        private volatile int _lights;
        private long _frames;

        public string Status => _status;
        public int LightsSeen => _lights;
        public long Frames => Interlocked.Read(ref _frames);

        public void Start(PixelRect region, int fps)
        {
            Stop();
            _region = region;
            _intervalMs = Math.Max(10, (int)Math.Round(1000.0 / Math.Max(1, Math.Min(60, fps))));
            _running = true;
            _status = "starting";
            _thread = new Thread(Run) { IsBackground = true, Name = "LovelyCarDataCapture screen" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            var thread = _thread;
            _thread = null;
            if (thread != null && thread.IsAlive) thread.Join(500);
            _status = "off";
        }

        public bool IsRunning => _running;

        private void Run()
        {
            using (var grabber = new ScreenGrabber())
            {
                string lastProblem = null;
                while (_running)
                {
                    long started = _clock.ElapsedMilliseconds;
                    try
                    {
                        var target = Target?.Invoke();
                        var frame = grabber.Grab(_region, out string problem);
                        if (frame == null)
                        {
                            if (problem != lastProblem)
                            {
                                lastProblem = problem;
                                SimHub.Logging.Current.Warn("[LovelyCarDataCapture] Screen capture failed: " + problem);
                            }
                            _status = "can't read the screen: " + problem;
                        }
                        else
                        {
                            lastProblem = null;
                            var blobs = _detector.Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height));
                            _lights = blobs.Count;
                            Interlocked.Increment(ref _frames);
                            if (target == null)
                            {
                                _status = "watching (" + blobs.Count + " lights) - waiting for the car";
                            }
                            else
                            {
                                target.Capture.Record(target.Gear, target.Rpm, _clock.ElapsedMilliseconds, blobs);
                                _status = "recording, " + blobs.Count + " lights lit";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _status = "error: " + ex.Message;
                        SimHub.Logging.Current.Error("[LovelyCarDataCapture] Screen capture loop", ex);
                        Thread.Sleep(1000);
                    }

                    int elapsed = (int)(_clock.ElapsedMilliseconds - started);
                    int wait = _intervalMs - elapsed;
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
        }

        /// <summary>Reads the box once and says what was found, for the Test button.</summary>
        public string Describe(PixelRect region)
        {
            using (var grabber = new ScreenGrabber())
            {
                var frame = grabber.Grab(region, out string problem);
                if (frame == null) return "Couldn't read the screen: " + problem;
                var blobs = _detector.Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height));
                if (blobs.Count == 0)
                    return "No lights found in the box. Rev the engine so some are lit, and check the box is over them.";

                var calibration = new StripCalibration();
                calibration.Add(blobs);
                var layout = calibration.Build(out _);
                var colors = string.Join(", ", Describe(blobs));
                return blobs.Count + " lights lit" +
                       (layout != null && layout.GapCount > 0 ? ", " + layout.GapCount + " gap(s) between them" : "") +
                       ": " + colors;
            }
        }

        private static IEnumerable<string> Describe(List<LitBlob> blobs)
        {
            foreach (var b in blobs) yield return b.Color.ToHex();
        }

        public void Dispose() => Stop();
    }
}
