using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Screen;
using LovelyCarDataCapture.Util;
using Newtonsoft.Json;

namespace LovelyCarDataCapture.Plugin
{
    internal sealed class CaptureBoxImage
    {
        public byte[] Png;
        public PixelRect Region;
        public DateTime SelectedUtc;
        public string Game, Car;
    }

    /// <summary>One capture's original inputs, kept independently of the replaceable car export.</summary>
    /// <remarks>Stop the screen worker before adding selections or saving its retained images.</remarks>
    internal sealed class CaptureEvidence
    {
        // Crops are usually tiny. Bound unusually large selections and repeated box changes too.
        internal const int MaxImages = 8;
        internal const int MaxImageBytes = 16 * 1024 * 1024;
        private readonly string _id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private DateTime? _stoppedUtc;
        private readonly object _startSettings;
        private readonly List<CaptureBoxImage> _selections = new List<CaptureBoxImage>();
        private readonly List<ScreenImage> _images = new List<ScreenImage>();
        private readonly List<string> _notes = new List<string>();
        private long _imageBytes;
        private bool _rawSaved;
        private string _framesFile;
        private string _transitionsFile;
        public string Folder { get; private set; }
        public string RawFramesPath => _framesFile == null ? null : Path.Combine(Folder, _framesFile);
        public IEnumerable<string> Notes => _notes;

        public CaptureEvidence(CaptureSettings settings, CaptureBoxImage selection = null)
        {
            _startSettings = SettingsSnapshot(settings);
            AddSelection(selection);
        }

        public void Stop() { if (!_stoppedUtc.HasValue) _stoppedUtc = DateTime.UtcNow; }

        public void AddSelection(CaptureBoxImage selection)
        {
            if (selection == null || _selections.Contains(selection)) return;
            if (_selections.Count + _images.Count >= MaxImages || _imageBytes + selection.Png.Length > MaxImageBytes)
            { Note("Some box images were not kept because the image limit was reached."); return; }
            _selections.Add(selection);
            _imageBytes += selection.Png.Length;
        }

        // Called only by the screen worker. Retain a lit example rather than just the first idle frame.
        public void Observe(PixelFrame frame, PixelRect region, int fps, long timeMs, string gear, int rpm, int lights, int frameNumber)
        {
            var image = _images.LastOrDefault();
            if (image == null || !SameBox(image.Region, region) || image.Fps != fps)
            {
                if (_selections.Count + _images.Count >= MaxImages)
                { Note("Some box images were not kept because the image limit was reached."); return; }
                image = new ScreenImage { Region = region, Fps = fps, FirstFrame = frameNumber };
                _images.Add(image);
            }
            image.LastFrame = frameNumber;
            if (image.Frame != null && image.Lights >= lights) return;
            int previousBytes = image.Frame?.Pixels.Length ?? 0;
            if (_imageBytes - previousBytes + frame.Pixels.Length > MaxImageBytes)
            { Note("Some box images were not kept because the image limit was reached."); return; }
            var pixels = (byte[])frame.Pixels.Clone();
            image.Frame = new PixelFrame(pixels, frame.Width, frame.Height, frame.Stride, frame.BytesPerPixel, frame.RedOffset, frame.BlueOffset);
            image.TimeMs = timeMs; image.Gear = gear; image.Rpm = rpm; image.Lights = lights;
            _imageBytes += pixels.Length - previousBytes;
        }

        public void Note(string text) { if (!_notes.Contains(text)) _notes.Add(text); }

        public void SaveRaw(CaptureSession session, CaptureSettings settings, ScreenCaptureLoop screen, string gameFolder)
        {
            Stop();
            if (Folder == null || !Directory.Exists(Folder)) Folder = Path.Combine(gameFolder, "captures", Slug.Make(session.CarId), _id);
            Directory.CreateDirectory(Folder);
            if (_framesFile == null && settings.SaveCaptureFrames && session.Screen.HasData)
            {
                var filename = Slug.Make(session.CarId) + ".frames.csv";
                var temporary = Path.Combine(Folder, filename + ".pending");
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                    session.Screen.WriteFrames(writer);
                File.Move(temporary, Path.Combine(Folder, filename));
                _framesFile = filename;
            }
            if (_rawSaved) { SaveManifest(session, settings, "awaiting export", null); return; }
            if (_framesFile == null)
                Note(settings.SaveCaptureFrames ? "No screen frames were recorded for this capture." : "Raw frame retention was off at the first export attempt.");

            int number = 0;
            foreach (var selection in _selections)
            {
                // An old box may be reused for another car. Never label its old cockpit as this car.
                if (selection.Game != session.GameName || selection.Car != session.CarId)
                { Note("A selection image was omitted because its car or game was unknown or different. Recorded crop images belong to this capture."); continue; }
                File.WriteAllBytes(Path.Combine(Folder, "selection-" + (++number).ToString("00") + ".png"), selection.Png);
            }
            for (int i = 0; i < _images.Count; i++)
            {
                var image = _images[i];
                if (image.Frame == null) continue;
                image.File = "strip-" + (i + 1).ToString("00") + ".png";
                new DesktopSnapshot { Frame = image.Frame }.SaveRegion(Path.Combine(Folder, image.File),
                    new PixelRect(0, 0, image.Frame.Width, image.Frame.Height));
            }
            if (_images.Count == 0 && number == 0) Note("No capture-box image was available for this capture.");
            try
            {
                var transitions = screen.ExportTransitions(session.Screen, Path.Combine(Folder, "transitions"), session.GameName, session.CarId);
                if (transitions != null)
                {
                    Note(transitions.Report);
                    foreach (var error in transitions.Errors) Note(error);
                    if (transitions.MetadataPath != null) _transitionsFile = "transitions/transition-frames.json";
                }
            }
            catch (Exception ex) { Note("Transition images could not be saved: " + ex.Message); }
            SaveManifest(session, settings, "awaiting export", null);
            _rawSaved = true;
        }

        public void SaveResult(CaptureSession session, CaptureSettings settings, string json, string report)
        {
            File.WriteAllText(Path.Combine(Folder, "car.json"), json, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Folder, "report.txt"), report, new UTF8Encoding(false));
            SaveManifest(session, settings, "exported", null);
        }

        public void SaveFailure(CaptureSession session, CaptureSettings settings, string error)
        {
            if (Folder != null) SaveManifest(session, settings, "export failed", error);
        }

        private void SaveManifest(CaptureSession session, CaptureSettings settings, string outcome, string error)
        {
            var assembly = typeof(CaptureEvidence).Assembly;
            int number = 0;
            var manifest = new
            {
                FormatVersion = 1, CaptureId = _id, StartedUtc = _startedUtc, StoppedUtc = _stoppedUtc,
                session.GameName, session.CarId, PluginVersion = assembly.GetName().Version.ToString(),
                BuildId = assembly.ManifestModule.ModuleVersionId.ToString(), Outcome = outcome, Error = error,
                StartSettings = _startSettings, ExportSettings = SettingsSnapshot(settings),
                Frames = _framesFile, Profile = outcome == "exported" ? "car.json" : null,
                Report = outcome == "exported" ? "report.txt" : null, Transitions = _transitionsFile,
                Selections = _selections.Where(s => s.Game == session.GameName && s.Car == session.CarId).Select(s => new
                { File = "selection-" + (++number).ToString("00") + ".png", s.SelectedUtc, Box = Region(s.Region) }).ToArray(),
                ScreenImages = _images.Select(i => new { i.File, Box = Region(i.Region), i.Fps, i.FirstFrame, i.LastFrame,
                    i.TimeMs, i.Gear, i.Rpm, DetectedLights = i.Lights }).ToArray(),
                Notes = _notes.ToArray(),
            };
            File.WriteAllText(Path.Combine(Folder, "capture.json"), JsonConvert.SerializeObject(manifest, Formatting.Indented), new UTF8Encoding(false));
        }

        private static object SettingsSnapshot(CaptureSettings s) => new
        {
            s.ScreenCapture, s.ScreenCaptureFps, Box = Region(new PixelRect(s.ScreenBoxX, s.ScreenBoxY, s.ScreenBoxWidth, s.ScreenBoxHeight)),
            s.SaveCaptureFrames, s.SaveTransitionFrames, s.UseRepoFile, s.RepoBranch, s.CopyMeasuredToOtherGears,
            s.TopGearForNextExport, s.LedNumber, s.FirstLedPercent, s.LastLedPercent, s.RoundRpmTo, s.CopyToAtsrDeveloperFolder,
        };
        private static object Region(PixelRect r) => new { r.X, r.Y, r.Width, r.Height };
        private static bool SameBox(PixelRect a, PixelRect b) => a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;
        private sealed class ScreenImage
        {
            public PixelFrame Frame;
            public PixelRect Region;
            public int Fps, FirstFrame, LastFrame, Rpm, Lights;
            public long TimeMs;
            public string Gear, File;
        }
    }
}
