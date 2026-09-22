using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Plugin;
using LovelyCarDataCapture.Screen;
using Newtonsoft.Json.Linq;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static PixelFrame EvidenceFrame()
        {
            var pixels = new byte[8 * 4 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = 255; }
            return PixelFrame.Bgra32(pixels, 8, 4);
        }

        private static void EvidenceKeepsCaptureInputs()
        {
            var root = Path.Combine(Path.GetTempPath(), "capture-evidence-" + Guid.NewGuid().ToString("N"));
            var settings = new CaptureSettings { ScreenCapture = true, LedNumber = 6 };
            var session = new CaptureSession("Automobilista2", "Evidence Car");
            var frame = EvidenceFrame();
            var box = new PixelRect(20, 30, 8, 4);
            var png = new DesktopSnapshot { Frame = frame }.RegionPng(new PixelRect(0, 0, 8, 4));
            var selected = new CaptureBoxImage { Png = png, Region = box, SelectedUtc = DateTime.UtcNow, Game = session.GameName, Car = session.CarId };
            var evidence = new CaptureEvidence(settings, selected);
            evidence.AddSelection(new CaptureBoxImage { Png = png, Region = box, Game = session.GameName, Car = "Another Car" });
            using (var screen = new ScreenCaptureLoop())
            try
            {
                session.Screen.Record("2", 5000, 100, new List<LitBlob>());
                evidence.Observe(frame, box, 30, 100, "2", 5000, 2, 1);
                // The grabber reuses its pixel buffer. A dimmer frame must not replace the lit example.
                for (int i = 0; i < frame.Pixels.Length; i += 4) { frame.Pixels[i] = 255; frame.Pixels[i + 2] = 0; }
                session.Screen.Record("2", 4900, 200, new List<LitBlob>());
                evidence.Observe(frame, box, 30, 200, "2", 4900, 1, 2);
                var moved = new PixelRect(40, 50, 8, 4);
                session.Screen.Record("2", 5100, 300, new List<LitBlob>());
                evidence.Observe(frame, moved, 60, 300, "2", 5100, 3, 3);
                settings.LedNumber = 3;
                evidence.SaveRaw(session, settings, screen, root);
                string folder = evidence.Folder;
                var raw = File.ReadAllBytes(evidence.RawFramesPath);
                var rawWritten = File.GetLastWriteTimeUtc(evidence.RawFramesPath);
                var manifest = JObject.Parse(File.ReadAllText(Path.Combine(folder, "capture.json")));
                Equal(6, (int)manifest["StartSettings"]["LedNumber"], "settings at start are a snapshot");
                Equal(3, (int)manifest["ExportSettings"]["LedNumber"], "settings used for export recorded separately");
                Equal(session.CarId, (string)manifest["CarId"], "evidence identity");
                Check(!string.IsNullOrEmpty((string)manifest["PluginVersion"]) && Guid.TryParse((string)manifest["BuildId"], out _), "version and exact build recorded");
                Equal(1, manifest["Selections"].Count(), "another car's selection is not attributed to this one");
                Equal(2, manifest["ScreenImages"].Count(), "box movement creates separate evidence");
                Equal(2, (int)manifest["ScreenImages"][0]["LastFrame"], "first region's raw frame range");
                Equal(3, (int)manifest["ScreenImages"][1]["FirstFrame"], "second region's raw frame range");
                using (var original = new System.Drawing.Bitmap(Path.Combine(folder, "strip-01.png")))
                    Equal(255, (int)original.GetPixel(0, 0).R, "recorded pixels are owned, not a reused grabber buffer");
                using (var second = new System.Drawing.Bitmap(Path.Combine(folder, "strip-02.png")))
                    Equal(255, (int)second.GetPixel(0, 0).B, "new box keeps its own pixels");
                evidence.SaveFailure(session, settings, "test failure");
                evidence.SaveRaw(session, settings, screen, root);
                Equal(folder, evidence.Folder, "retry keeps the same folder");
                Check(raw.SequenceEqual(File.ReadAllBytes(evidence.RawFramesPath)), "retry keeps original CSV bytes");
                Equal(rawWritten, File.GetLastWriteTimeUtc(evidence.RawFramesPath), "retry does not rewrite the original recording");
                evidence.SaveResult(session, settings, "{}", "capture report");
                manifest = JObject.Parse(File.ReadAllText(Path.Combine(folder, "capture.json")));
                Equal("exported", (string)manifest["Outcome"], "completed export outcome");
                Equal("capture report", File.ReadAllText(Path.Combine(folder, "report.txt")), "report kept with the evidence");

                var next = new CaptureEvidence(settings);
                next.SaveRaw(session, settings, screen, root);
                Check(next.Folder != folder && File.Exists(Path.Combine(folder, "car.json")), "another capture cannot replace this capture's files");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void EvidenceHonorsLimitsAndRetention()
        {
            var root = Path.Combine(Path.GetTempPath(), "capture-evidence-" + Guid.NewGuid().ToString("N"));
            var settings = new CaptureSettings { SaveCaptureFrames = false };
            var session = new CaptureSession("Automobilista2", "Limits");
            session.Screen.Record("1", 5000, 10, new List<LitBlob>());
            var evidence = new CaptureEvidence(settings);
            var frame = EvidenceFrame();
            for (int i = 0; i < CaptureEvidence.MaxImages + 5; i++)
                evidence.Observe(frame, new PixelRect(i, 0, 8, 4), 30, 10 + i, "1", 5000, 1, i + 1);
            using (var screen = new ScreenCaptureLoop())
            try
            {
                evidence.SaveRaw(session, settings, screen, root);
                Check(evidence.RawFramesPath == null, "disabled raw frames are not saved");
                Equal(CaptureEvidence.MaxImages, Directory.GetFiles(evidence.Folder, "strip-*.png").Length, "box image count bounded");
                Check(evidence.Notes.Any(n => n.Contains("image limit")), "limit visible in metadata");
                settings.SaveCaptureFrames = true;
                evidence.SaveRaw(session, settings, screen, root);
                Check(File.Exists(evidence.RawFramesPath), "enabling retention before retry saves the retained raw recording");

                var large = new CaptureEvidence(settings);
                large.AddSelection(new CaptureBoxImage { Png = new byte[CaptureEvidence.MaxImageBytes + 1] });
                Check(large.Notes.Any(n => n.Contains("image limit")), "oversized selection bounded before retention");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void EvidenceRecoversInvalidOutputFolder()
        {
            var root = Path.Combine(Path.GetTempPath(), "capture-evidence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var blocked = Path.Combine(root, "file-not-folder");
            File.WriteAllText(blocked, "blocks directory creation");
            var evidence = new CaptureEvidence(new CaptureSettings());
            var session = new CaptureSession("Automobilista2", "Recovery");
            using (var screen = new ScreenCaptureLoop())
            try
            {
                bool failed = false;
                try { evidence.SaveRaw(session, new CaptureSettings(), screen, blocked); }
                catch (IOException) { failed = true; }
                Check(failed, "invalid output rejected");
                evidence.SaveRaw(session, new CaptureSettings(), screen, root);
                Check(File.Exists(Path.Combine(evidence.Folder, "capture.json")), "corrected output folder permits retry");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
