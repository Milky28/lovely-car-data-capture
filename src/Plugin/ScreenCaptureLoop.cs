using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Plugin
{
    /// <summary>What to record a frame against: one capture generation and its recent telemetry.</summary>
    internal sealed class ScreenTarget
    {
        public ScreenLedCapture Capture;
        public TelemetryHistory Telemetry;
        public long SessionId;
        public string Game;
        public string Car;
        public CaptureEvidence Evidence;
        /// <summary>The capture has all the frames it can keep: nothing to read the screen for.</summary>
        public bool Full;
    }

    /// <summary>
    /// Reads the capture box off the screen on its own thread and feeds what it finds to the capture.
    /// </summary>
    /// <remarks>
    /// Kept off SimHub's data thread: a screen copy takes a few milliseconds, which is fine here and
    /// not fine there. Telemetry is sampled at the screen acquisition time, so detector runtime does not
    /// add a variable delay to the RPM paired with a frame.
    /// </remarks>
    internal sealed class ScreenCaptureLoop : IDisposable
    {
        private Thread _thread;
        private readonly object _lifecycle = new object();
        private CancellationTokenSource _stop;
        private volatile bool _running;
        private PixelRect _region;
        private int _intervalMs = 33;
        private int _fps;
        private bool _saveTransitions;
        private ScreenLedCapture _diagnosticCapture;
        private TransitionFrameRecorder _transitions;
        private string _diagnosticError;

        /// <summary>
        /// How often to look again while there's nothing to record - the game paused, in a menu or
        /// closed, or the capture full. Reading and analysing the box 60 times a second only to throw
        /// every frame away is CPU for nothing, however long a capture is left running.
        /// </summary>
        private const int IdleIntervalMs = 500;

        /// <summary>Returns what the frame should be recorded against, or null to drop it.</summary>
        public Func<ScreenTarget> Target;

        private volatile string _status = "off";
        private volatile string _guidance = "";
        private volatile int _lights;
        private long _frames;

        public string Status => _status;
        public string Guidance => _guidance;
        public int LightsSeen => _lights;
        public long Frames => Interlocked.Read(ref _frames);

        public void Start(PixelRect region, int fps, bool saveTransitions = false)
        {
            lock (_lifecycle)
            {
                Stop();
                _region = region;
                _fps = fps;
                _saveTransitions = saveTransitions;
                _diagnosticCapture = null;
                _transitions = null;
                _diagnosticError = null;
                _intervalMs = Math.Max(10, (int)Math.Round(1000.0 / Math.Max(1, Math.Min(60, fps))));
                _stop = new CancellationTokenSource();
                var token = _stop.Token;
                _running = true;
                _status = "starting";
                _guidance = "Starting screen reading…";
                _lights = 0;
                _thread = new Thread(() => Run(token)) { IsBackground = true, Name = "LovelyCarDataCapture screen" };
                _thread.Start();
            }
        }

        public void Stop()
        {
            lock (_lifecycle)
            {
                _running = false;
                _stop?.Cancel();
                // Export reads the recording next, so the last writer must actually be finished.
                // Cancellation wakes idle/error waits immediately instead of abandoning a worker.
                _thread?.Join();
                _thread = null;
                _stop?.Dispose();
                _stop = null;
                _status = "off";
                _guidance = "";
            }
        }

        public bool IsRunning => _running;

        public void ClearTransitionFrames()
        {
            lock (_lifecycle)
            {
                if (_running) throw new InvalidOperationException("Stop screen capture before clearing transition images.");
                _transitions = null;
                _diagnosticCapture = null;
                _diagnosticError = null;
            }
        }

        private void Run(CancellationToken stop)
        {
            using (var grabber = new ScreenGrabber())
            {
                var detector = new StripDetector();
                ScreenLedCapture detectorCapture = null;
                string lastProblem = null;
                while (!stop.IsCancellationRequested)
                {
                    long started = CaptureClock.NowMilliseconds;
                    try
                    {
                        var target = Target?.Invoke();
                        if (stop.IsCancellationRequested) break;
                        if (target == null || target.Full)
                        {
                            _transitions?.BreakSequence();
                            _status = target != null ? "capture full - press Stop and export" : "waiting for the car";
                            _guidance = target != null ? "Capture full. Press Stop and export; no more frames are being recorded." : "";
                            stop.WaitHandle.WaitOne(IdleIntervalMs);
                            continue;
                        }
                        var frame = grabber.Grab(_region, out string problem, out long acquiredAt);
                        if (frame == null)
                        {
                            _transitions?.BreakSequence();
                            if (problem != lastProblem)
                            {
                                lastProblem = problem;
                                SimHub.Logging.Current.Warn("[LovelyCarDataCapture] Screen capture failed: " + problem);
                            }
                            _status = "can't read the screen: " + problem;
                            _guidance = "Screen capture failed: " + problem + ". Check the capture box and use borderless or windowed mode.";
                        }
                        else
                        {
                            lastProblem = null;
                            // Learned LED positions belong to this car and capture box only.
                            if (!ReferenceEquals(detectorCapture, target.Capture))
                            {
                                detector = new StripDetector();
                                detectorCapture = target.Capture;
                            }
                            // Grabber timestamps the desktop copy, before this detector runs.
                            var blobs = detector.Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height));
                            if (stop.IsCancellationRequested) break;
                            var status = target.Telemetry.TrySample(target.SessionId, target.Game, target.Car, acquiredAt, out var telemetry);
                            if (status == TelemetrySampleStatus.Ok)
                            {
                                _lights = blobs.Count;
                                Interlocked.Increment(ref _frames);
                                target.Capture.Record(telemetry.Gear, telemetry.Rpm, acquiredAt, blobs);
                                target.Evidence?.Observe(frame, _region, _fps, acquiredAt, telemetry.Gear, telemetry.Rpm, blobs.Count, target.Capture.SampleCount);
                                RecordTransitions(target.Capture, frame, blobs, acquiredAt, telemetry.Gear, telemetry.Rpm);
                                _status = "recording, " + blobs.Count + " lights lit";
                                _guidance = "";
                            }
                            else
                            {
                                _transitions?.BreakSequence();
                                _status = "waiting for " + TelemetryStatus(status);
                                _guidance = status == TelemetrySampleStatus.GearChanged
                                    ? "Waiting for a stable gear. Hold a gear for the next sweep."
                                    : "Waiting for fresh telemetry. Check that SimHub is receiving game data.";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _transitions?.BreakSequence();
                        _status = "error: " + ex.Message;
                        _guidance = "Screen capture failed: " + ex.Message + ". Check the capture box or stop and retry.";
                        SimHub.Logging.Current.Error("[LovelyCarDataCapture] Screen capture loop", ex);
                        stop.WaitHandle.WaitOne(1000);
                    }

                    int elapsed = (int)(CaptureClock.NowMilliseconds - started);
                    int wait = _intervalMs - elapsed;
                    if (wait > 0) stop.WaitHandle.WaitOne(wait);
                }
            }
        }

        private void RecordTransitions(ScreenLedCapture capture, PixelFrame frame, List<LitBlob> blobs,
                                       long timeMs, string gear, int rpm)
        {
            if (!_saveTransitions) return;
            if (!ReferenceEquals(_diagnosticCapture, capture))
            {
                _diagnosticCapture = capture;
                _transitions = new TransitionFrameRecorder(true);
                _diagnosticError = null;
            }
            if (_diagnosticError != null) return;
            try { _transitions.Record(frame, blobs, timeMs, gear, rpm); }
            catch (Exception ex)
            {
                // Diagnostics must never prevent the ordinary RPM capture from continuing.
                _diagnosticError = "Transition images stopped: " + ex.Message;
            }
        }

        /// <summary>Called after Stop joins the writer, so PNG compression cannot delay a captured frame.</summary>
        public TransitionFrameExportResult ExportTransitions(ScreenLedCapture capture, string directory, string game, string car)
        {
            lock (_lifecycle)
            {
                if (_running) throw new InvalidOperationException("Stop screen capture before exporting transition images.");
                if (!ReferenceEquals(_diagnosticCapture, capture) || _transitions == null) return null;
                var settings = new DetectorSettings();
                var result = _transitions.Export(directory, new TransitionFrameExportMetadata
                {
                    GameName = game,
                    CarId = car,
                    Fps = _fps,
                    Region = new TransitionFrameRegion(_region.X, _region.Y, _region.Width, _region.Height),
                    DetectorSettings = typeof(DetectorSettings).GetFields().ToDictionary(f => f.Name, f => f.GetValue(settings)),
                });
                if (_diagnosticError != null) result.Errors.Add(_diagnosticError);
                return result;
            }
        }

        /// <summary>Reads the box once and says what was found, for the Test button.</summary>
        public string Describe(PixelRect region)
        {
            using (var grabber = new ScreenGrabber())
            {
                var frame = grabber.Grab(region, out string problem);
                if (frame == null) return "Couldn't read the screen: " + problem;
                var blobs = new StripDetector().Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height));
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

        private static string TelemetryStatus(TelemetrySampleStatus status)
        {
            switch (status)
            {
                case TelemetrySampleStatus.Stale: return "fresh telemetry";
                case TelemetrySampleStatus.GearChanged: return "stable gear";
                case TelemetrySampleStatus.SessionChanged: return "the current capture";
                default: return "telemetry";
            }
        }
    }
}
