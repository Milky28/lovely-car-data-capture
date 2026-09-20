using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using LovelyCarDataCapture.Screen;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace LovelyCarDataCapture.Plugin
{
    /// <summary>Optional diagnostics written around changes in the detected strip state.</summary>
    internal sealed class TransitionFrameRecorder
    {
        // A long race can produce thousands of changes. These limits keep diagnostics useful and bounded.
        internal const int MaxTransitions = 64;
        internal const int MaxSavedFrames = MaxTransitions * 3;
        internal const long MaxBytes = 64L * 1024 * 1024;
        // Limiter flashes repeat rapidly; two examples per directed state change leave room for later gears.
        internal const int MaxOccurrencesPerPair = 2;
        // A single gear should not consume the global diagnostic buffer during a long capture.
        internal const int MaxTransitionsPerGear = 24;

        private readonly bool _enabled;
        private readonly List<Transition> _transitions = new List<Transition>();
        private readonly List<string> _errors = new List<string>();
        private readonly Dictionary<string, int> _pairOccurrences = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _gearOccurrences = new Dictionary<string, int>();
        private Snapshot _previous;
        private Transition _pending;
        private string _previousState;
        private string _previousGear;
        private long _retainedBytes;
        private int _sequence;
        private bool _hasState;
        private bool _limitReached;
        private bool _pixelCaptureStopped;
        private int _transitionsSeen;
        private int _samplingSkipped;
        private int _pairQuotaSkipped;
        private int _gearQuotaSkipped;
        private int _boundSkipped;

        public TransitionFrameRecorder(bool enabled) { _enabled = enabled; }

        public bool Enabled => _enabled;
        public int TransitionsSeen => _transitionsSeen;
        public int SavedTransitions => _transitions.Count;
        public bool LimitReached => _limitReached;
        public int SamplingSkipped => _samplingSkipped;

        /// <summary>
        /// Records only a one-frame rolling copy, plus copies retained by detected transitions. The
        /// screen grabber reuses its byte buffer, so every retained frame owns its pixel bytes.
        /// </summary>
        public void Record(PixelFrame frame, IEnumerable<LitBlob> blobs, long timeMs, string gear, int rpm)
        {
            if (!_enabled || frame == null) return;

            string currentGear = gear ?? "";
            if (_hasState && !string.Equals(_previousGear, currentGear, StringComparison.Ordinal)) BreakSequence();

            string state = State(blobs);
            Snapshot current = _pixelCaptureStopped ? null : TryCopy(frame, blobs, timeMs, gear, rpm, ++_sequence, state);

            // A transition's following frame is the next valid image, and is retained before the
            // current frame can become the previous frame for another transition.
            if (_pending != null && current != null)
            {
                _pending.Following = current;
                Retain(current);
                _pending = null;
                if (_transitions.Count >= MaxTransitions) _pixelCaptureStopped = true;
            }

            bool changed = _hasState && !string.Equals(_previousState, state, StringComparison.Ordinal);
            if (changed)
            {
                _transitionsSeen++;
                if (current != null && _previous != null)
                {
                    if (CanSample(currentGear, _previousState, state))
                    {
                        var transition = new Transition(_previous, current, _previousState, state);
                        Retain(_previous);
                        Retain(current);
                        _transitions.Add(transition);
                        _pending = transition;
                        if (_transitions.Count >= MaxTransitions) _limitReached = true;
                    }
                }
                else
                {
                    _limitReached = true;
                    _boundSkipped++;
                    if (_transitions.Count >= MaxTransitions) _pixelCaptureStopped = true;
                }
            }

            if (current != null)
            {
                Release(_previous);
                _previous = current;
            }
            else if (!_pixelCaptureStopped)
            {
                // Never pair a later state with an image from before a copy failure.
                Release(_previous);
                _previous = null;
                _pixelCaptureStopped = true;
            }
            _previousState = state;
            _previousGear = currentGear;
            _hasState = true;
        }

        private bool CanSample(string gear, string stateBefore, string stateAfter)
        {
            if (_transitions.Count >= MaxTransitions)
            {
                _limitReached = true;
                _boundSkipped++;
                _pixelCaptureStopped = true;
                return false;
            }

            int gearCount = _gearOccurrences.TryGetValue(gear, out var count) ? count : 0;
            if (gearCount >= MaxTransitionsPerGear)
            {
                _samplingSkipped++;
                _gearQuotaSkipped++;
                return false;
            }

            string key = PairKey(gear, stateBefore, stateAfter);
            int pairCount = _pairOccurrences.TryGetValue(key, out count) ? count : 0;
            if (pairCount >= MaxOccurrencesPerPair)
            {
                _samplingSkipped++;
                _pairQuotaSkipped++;
                return false;
            }

            _pairOccurrences[key] = pairCount + 1;
            _gearOccurrences[gear] = gearCount + 1;
            return true;
        }

        private static string PairKey(string gear, string stateBefore, string stateAfter) =>
            (gear ?? "") + "\u001f" + (stateBefore ?? "") + "\u001f" + (stateAfter ?? "");

        /// <summary>Clears a recorder so it can be reused after a restarted capture.</summary>
        public void Reset()
        {
            foreach (var transition in _transitions)
            {
                Release(transition.Previous);
                Release(transition.Current);
                Release(transition.Following);
            }
            Release(_previous);
            _transitions.Clear();
            _errors.Clear();
            _pairOccurrences.Clear();
            _gearOccurrences.Clear();
            _previous = null;
            _pending = null;
            _previousState = null;
            _previousGear = null;
            _retainedBytes = 0;
            _sequence = 0;
            _hasState = false;
            _limitReached = false;
            _pixelCaptureStopped = false;
            _transitionsSeen = 0;
            _samplingSkipped = 0;
            _pairQuotaSkipped = 0;
            _gearQuotaSkipped = 0;
            _boundSkipped = 0;
        }

        /// <summary>Ends the current image sequence without discarding transitions already captured.</summary>
        public void BreakSequence()
        {
            _pending = null;
            Release(_previous);
            _previous = null;
            _previousState = null;
            _previousGear = null;
            _hasState = false;
        }

        /// <summary>Writes PNGs and one replay-friendly JSON manifest. Disk work happens here, never in Record.</summary>
        public TransitionFrameExportResult Export(string directory, TransitionFrameExportMetadata metadata)
        {
            var result = new TransitionFrameExportResult
            {
                Enabled = _enabled,
                LimitReached = _limitReached,
                SamplingSkipped = _samplingSkipped,
                BoundSkipped = _boundSkipped,
            };
            result.Errors.AddRange(_errors);
            if (!_enabled) return result;
            if (string.IsNullOrWhiteSpace(directory))
            {
                result.Errors.Add("No transition-frame export folder was supplied.");
                return result;
            }

            try { Directory.CreateDirectory(directory); }
            catch (Exception ex)
            {
                AddResultError(result, "Couldn't create the transition-frame folder: " + ex.Message);
                return result;
            }

            int number = 0;
            foreach (var transition in _transitions)
            {
                number++;
                if (transition.Following == null) result.IncompleteTransitions++;
                var exported = new TransitionFrameExportTransition
                {
                    Number = number,
                    StateBefore = transition.StateBefore,
                    StateAfter = transition.StateAfter,
                    Previous = ExportFrame(transition.Previous, number, "previous", directory, result),
                    Current = ExportFrame(transition.Current, number, "current", directory, result),
                    Following = ExportFrame(transition.Following, number, "following", directory, result),
                };
                result.Transitions.Add(exported);
            }

            var document = new ExportDocument
            {
                FormatVersion = 1,
                Metadata = metadata ?? new TransitionFrameExportMetadata(),
                TransitionsObserved = _transitionsSeen,
                TransitionsSaved = result.Transitions.Count,
                IncompleteTransitions = result.IncompleteTransitions,
                LimitReached = _limitReached,
                MaxTransitions = MaxTransitions,
                MaxSavedFrames = MaxSavedFrames,
                MaxBytes = MaxBytes,
                Errors = result.Errors,
                Sampling = new SamplingInfo
                {
                    MaxOccurrencesPerPair = MaxOccurrencesPerPair,
                    MaxTransitionsPerGear = MaxTransitionsPerGear,
                    TransitionsSkipped = _samplingSkipped,
                    PairQuotaSkipped = _pairQuotaSkipped,
                    GearQuotaSkipped = _gearQuotaSkipped,
                    BoundSkipped = _boundSkipped,
                },
                Transitions = result.Transitions,
            };
            string manifest = Path.Combine(directory, "transition-frames.json");
            try
            {
                var json = JsonConvert.SerializeObject(document, Formatting.Indented, new JsonSerializerSettings
                {
                    ContractResolver = new CamelCasePropertyNamesContractResolver(),
                });
                File.WriteAllText(manifest, json, new UTF8Encoding(false));
                result.MetadataPath = manifest;
            }
            catch (Exception ex)
            {
                AddResultError(result, "Couldn't write transition-frame metadata: " + ex.Message);
            }
            return result;
        }

        private TransitionFrameExportFrame ExportFrame(Snapshot snapshot, int number, string role, string directory,
                                                       TransitionFrameExportResult result)
        {
            if (snapshot == null) return null;
            var info = new TransitionFrameExportFrame(snapshot.Sequence, snapshot.TimeMs, snapshot.Gear, snapshot.Rpm, snapshot.Blobs);
            string name = "transition-" + number.ToString("D4") + "-" + role + ".png";
            string path = Path.Combine(directory, name);
            try
            {
                SavePng(snapshot, path);
                info.Image = name;
                result.ImagesWritten++;
            }
            catch (Exception ex)
            {
                AddResultError(result, "Couldn't write " + name + ": " + ex.Message);
            }
            return info;
        }

        private Snapshot TryCopy(PixelFrame frame, IEnumerable<LitBlob> blobs, long timeMs, string gear,
                                 int rpm, int sequence, string state)
        {
            if (frame.Width <= 0 || frame.Height <= 0 || frame.BytesPerPixel < 3 || frame.Pixels == null)
            {
                AddError("A transition frame had invalid pixel data.");
                return null;
            }

            long bytes = (long)frame.Width * frame.Height * 4;
            if (bytes <= 0 || bytes > MaxBytes || _retainedBytes - ReleasablePreviousBytes() + bytes > MaxBytes)
            {
                _limitReached = true;
                _pixelCaptureStopped = true;
                AddError("The transition-frame memory limit was reached.");
                return null;
            }

            try
            {
                var pixels = new byte[(int)bytes];
                for (int y = 0; y < frame.Height; y++)
                {
                    for (int x = 0; x < frame.Width; x++)
                    {
                        int source = y * frame.Stride + x * frame.BytesPerPixel;
                        int target = (y * frame.Width + x) * 4;
                        pixels[target] = frame.Pixels[source + frame.BlueOffset];
                        pixels[target + 1] = frame.Pixels[source + frame.GreenOffset];
                        pixels[target + 2] = frame.Pixels[source + frame.RedOffset];
                        pixels[target + 3] = 255;
                    }
                }
                _retainedBytes += bytes;
                return new Snapshot(pixels, frame.Width, frame.Height, sequence, timeMs, gear ?? "", rpm,
                                    Blobs(blobs), state);
            }
            catch (Exception ex)
            {
                _limitReached = true;
                _pixelCaptureStopped = true;
                AddError("Couldn't retain a transition frame: " + ex.Message);
                return null;
            }
        }

        private void AddError(string message)
        {
            if (_errors.Count < 4 && !_errors.Contains(message)) _errors.Add(message);
        }

        private static void AddResultError(TransitionFrameExportResult result, string message)
        {
            if (result.Errors.Count < 8 && !result.Errors.Contains(message)) result.Errors.Add(message);
        }

        private long ReleasablePreviousBytes() => _previous != null && _previous.References == 1 ? _previous.Pixels.LongLength : 0;

        private void Retain(Snapshot snapshot)
        {
            if (snapshot != null) snapshot.References++;
        }

        private void Release(Snapshot snapshot)
        {
            if (snapshot == null || snapshot.References == 0) return;
            if (--snapshot.References == 0) _retainedBytes -= snapshot.Pixels.LongLength;
        }

        private static string State(IEnumerable<LitBlob> blobs)
        {
            var ordered = (blobs ?? Enumerable.Empty<LitBlob>()).Where(b => b != null).OrderBy(b => b.CenterX).ToList();
            var state = new StringBuilder(ordered.Count * 3 + 8).Append(ordered.Count);
            foreach (var blob in ordered)
            {
                int bucket = blob.Color.Hue < 0 ? -1 : (int)Math.Floor((blob.Color.Hue + 22.5) / 45.0) % 8;
                state.Append(':').Append(bucket);
            }
            return state.ToString();
        }

        private static List<BlobInfo> Blobs(IEnumerable<LitBlob> blobs)
        {
            return (blobs ?? Enumerable.Empty<LitBlob>()).Where(b => b != null).OrderBy(b => b.CenterX)
                .Select(b => new BlobInfo(b)).ToList();
        }

        private static void SavePng(Snapshot snapshot, string path)
        {
            using (var bitmap = new Bitmap(snapshot.Width, snapshot.Height, PixelFormat.Format32bppArgb))
            {
                var rect = new Rectangle(0, 0, snapshot.Width, snapshot.Height);
                BitmapData data = null;
                try
                {
                    data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    for (int y = 0; y < snapshot.Height; y++)
                        Marshal.Copy(snapshot.Pixels, y * snapshot.Width * 4, IntPtr.Add(data.Scan0, y * data.Stride), snapshot.Width * 4);
                }
                finally
                {
                    if (data != null) bitmap.UnlockBits(data);
                }
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        private sealed class Snapshot
        {
            public Snapshot(byte[] pixels, int width, int height, int sequence, long timeMs, string gear, int rpm,
                            List<BlobInfo> blobs, string state)
            {
                Pixels = pixels; Width = width; Height = height; Sequence = sequence; TimeMs = timeMs;
                Gear = gear; Rpm = rpm; Blobs = blobs; State = state; References = 1;
            }
            public readonly byte[] Pixels;
            public readonly int Width, Height, Sequence;
            public readonly long TimeMs;
            public readonly string Gear, State;
            public readonly int Rpm;
            public readonly List<BlobInfo> Blobs;
            public int References;
        }

        private sealed class Transition
        {
            public Transition(Snapshot previous, Snapshot current, string stateBefore, string stateAfter)
            {
                Previous = previous; Current = current; StateBefore = stateBefore; StateAfter = stateAfter;
            }
            public readonly Snapshot Previous, Current;
            public Snapshot Following;
            public readonly string StateBefore, StateAfter;
        }

        private sealed class BlobInfo
        {
            public BlobInfo(LitBlob blob)
            {
                Left = blob.Left; Right = blob.Right;
                Red = blob.Color.R; Green = blob.Color.G; Blue = blob.Color.B;
            }
            public int Left { get; set; }
            public int Right { get; set; }
            public int Red { get; set; }
            public int Green { get; set; }
            public int Blue { get; set; }
        }

        private sealed class ExportDocument
        {
            public int FormatVersion { get; set; }
            public TransitionFrameExportMetadata Metadata { get; set; }
            public int TransitionsObserved { get; set; }
            public int TransitionsSaved { get; set; }
            public int IncompleteTransitions { get; set; }
            public bool LimitReached { get; set; }
            public int MaxTransitions { get; set; }
            public int MaxSavedFrames { get; set; }
            public long MaxBytes { get; set; }
            public List<string> Errors { get; set; }
            public SamplingInfo Sampling { get; set; }
            public List<TransitionFrameExportTransition> Transitions { get; set; }
        }

        private sealed class SamplingInfo
        {
            public int MaxOccurrencesPerPair { get; set; }
            public int MaxTransitionsPerGear { get; set; }
            public int TransitionsSkipped { get; set; }
            public int PairQuotaSkipped { get; set; }
            public int GearQuotaSkipped { get; set; }
            public int BoundSkipped { get; set; }
        }
    }

    internal sealed class TransitionFrameExportMetadata
    {
        public string GameName { get; set; } = "";
        public string CarId { get; set; } = "";
        public int Fps { get; set; }
        public TransitionFrameRegion Region { get; set; } = new TransitionFrameRegion();
        public Dictionary<string, object> DetectorSettings { get; set; } = new Dictionary<string, object>();
    }

    internal sealed class TransitionFrameRegion
    {
        public TransitionFrameRegion() { }
        public TransitionFrameRegion(int x, int y, int width, int height)
        {
            X = x; Y = y; Width = width; Height = height;
        }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    internal sealed class TransitionFrameExportResult
    {
        public bool Enabled { get; internal set; }
        public bool LimitReached { get; internal set; }
        public string MetadataPath { get; internal set; }
        public int ImagesWritten { get; internal set; }
        public int IncompleteTransitions { get; internal set; }
        public int SamplingSkipped { get; internal set; }
        public int BoundSkipped { get; internal set; }
        public List<TransitionFrameExportTransition> Transitions { get; } = new List<TransitionFrameExportTransition>();
        public List<string> Errors { get; } = new List<string>();
        public string Report => !Enabled ? "Transition diagnostics are off." :
            "Transition diagnostics: " + Transitions.Count + " record(s), " + ImagesWritten + " image(s)" +
            (LimitReached ? "; capture limit reached." : ".") +
            (SamplingSkipped > 0 ? " " + SamplingSkipped + " repetitive transition(s) skipped by sampling." : "") +
            (IncompleteTransitions > 0 ? " " + IncompleteTransitions + " transition(s) had no following frame." : "") +
            (Errors.Count > 0 ? " " + Errors.Count + " output error(s)." : "");
    }

    internal sealed class TransitionFrameExportTransition
    {
        public int Number { get; internal set; }
        public string StateBefore { get; internal set; }
        public string StateAfter { get; internal set; }
        public TransitionFrameExportFrame Previous { get; internal set; }
        public TransitionFrameExportFrame Current { get; internal set; }
        public TransitionFrameExportFrame Following { get; internal set; }
    }

    internal sealed class TransitionFrameExportFrame
    {
        public TransitionFrameExportFrame() { }
        internal TransitionFrameExportFrame(int sequence, long timeMs, string gear, int rpm, object blobs)
        {
            Sequence = sequence; TimeMs = timeMs; Gear = gear; Rpm = rpm; Blobs = blobs;
        }
        public string Image { get; internal set; }
        public int Sequence { get; internal set; }
        public long TimeMs { get; internal set; }
        public string Gear { get; internal set; }
        public int Rpm { get; internal set; }
        public object Blobs { get; internal set; }
    }
}
