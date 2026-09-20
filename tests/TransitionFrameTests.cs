using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using LovelyCarDataCapture.Plugin;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static void TransitionRecorderCopiesFrameOwnership()
        {
            var folder = TempTransitionFolder();
            try
            {
                var recorder = new TransitionFrameRecorder(true);
                var source = new byte[] { 30, 20, 10, 255 };
                recorder.Record(PixelFrame.Bgra32(source, 1, 1), new List<LitBlob>(), 10, "1", 5000);
                source[0] = 200;
                recorder.Record(PixelFrame.Bgra32(new byte[] { 0, 0, 255, 255 }, 1, 1),
                                new[] { Blob(255, 0, 0) }, 20, "1", 6000);
                recorder.Record(PixelFrame.Bgra32(new byte[] { 0, 255, 0, 255 }, 1, 1),
                                new[] { Blob(255, 0, 0) }, 30, "1", 7000);

                var result = recorder.Export(folder, Metadata());
                Equal(3, result.ImagesWritten, "three transition images written");
                using (var image = new Bitmap(Path.Combine(folder, "transition-0001-previous.png")))
                {
                    Equal(10, image.GetPixel(0, 0).R, "previous image owns its original red channel");
                    Equal(30, image.GetPixel(0, 0).B, "previous image owns its original blue channel");
                }
            }
            finally { DeleteTransitionFolder(folder); }
        }

        private static void TransitionRecorderSelectsNeighborsAndColors()
        {
            var folder = TempTransitionFolder();
            try
            {
                var recorder = new TransitionFrameRecorder(true);
                recorder.Record(Frame(1), new[] { Blob(255, 0, 0) }, 100, "2", 7000);
                // Same count, different colour: this must still be a transition.
                recorder.Record(Frame(2), new[] { Blob(255, 255, 0) }, 200, "2", 7100);
                recorder.Record(Frame(3), new[] { Blob(255, 255, 0) }, 300, "2", 7200);
                var result = recorder.Export(folder, Metadata());
                var json = JObject.Parse(File.ReadAllText(result.MetadataPath));
                var transition = json["transitions"][0];
                Equal(100L, (long)transition["previous"]["timeMs"], "previous timestamp");
                Equal(200L, (long)transition["current"]["timeMs"], "current timestamp");
                Equal(300L, (long)transition["following"]["timeMs"], "following timestamp");
                Equal("2", (string)transition["current"]["gear"], "gear metadata");
                Equal(7100, (int)transition["current"]["rpm"], "RPM metadata");
                Equal("transition-0001-current.png", (string)transition["current"]["image"], "image mapping");
                Equal("Automobilista2", (string)json["metadata"]["gameName"], "capture metadata");
                Equal(60, (int)json["metadata"]["fps"], "FPS metadata");
                Equal(2, (int)json["metadata"]["region"]["width"], "region metadata");
                Check(json["metadata"]["detectorSettings"]["minBrightness"] != null, "detector settings metadata");
            }
            finally { DeleteTransitionFolder(folder); }
        }

        private static void TransitionRecorderBoundsLongCapture()
        {
            var folder = TempTransitionFolder();
            try
            {
                var repeated = new TransitionFrameRecorder(true);
                repeated.Record(Frame(0), new List<LitBlob>(), 0, "N", 4000);
                for (int i = 1; i < 200; i++)
                {
                    var blobs = new List<LitBlob>();
                    if (i % 2 != 0) blobs.Add(Blob(0, 255, 0));
                    repeated.Record(Frame(i), blobs, i, "N", 4000 + i);
                }
                Check(!repeated.LimitReached, "pair sampling is separate from the global limit");
                Equal(4, repeated.SavedTransitions, "repeated directed state changes are sampled");
                Check(repeated.SamplingSkipped > 0, "repeated state changes report sampling skips");

                var recorder = new TransitionFrameRecorder(true);
                long time = 0;
                for (int gear = 1; gear <= 4; gear++)
                {
                    recorder.Record(Frame(0), new List<LitBlob>(), time++, gear.ToString(), 4000);
                    for (int count = 1; count <= 32; count++)
                    {
                        recorder.Record(Frame(count), Blobs(count), time++, gear.ToString(), 4000 + count);
                    }
                    // Give the last saved transition in this gear its following frame before shifting.
                    recorder.Record(Frame(32), Blobs(32), time++, gear.ToString(), 4032);
                }
                Check(recorder.LimitReached, "long capture reports its global diagnostic limit");
                Equal(TransitionFrameRecorder.MaxTransitions, recorder.SavedTransitions, "saved transition bound");
                var result = recorder.Export(folder, Metadata());
                Equal(TransitionFrameRecorder.MaxSavedFrames, result.ImagesWritten, "saved image bound");
                Equal(24, result.Transitions.Count(t => t.Current.Gear == "1"), "gear 1 quota");
                Equal(24, result.Transitions.Count(t => t.Current.Gear == "2"), "gear 2 quota");
                Equal(16, result.Transitions.Count(t => t.Current.Gear == "3"), "gear 3 gets remaining global space");
                Equal(0, result.Transitions.Count(t => t.Current.Gear == "4"), "global bound stops later gear only after earlier quotas");
                var manifest = JObject.Parse(File.ReadAllText(result.MetadataPath));
                Equal(2, (int)manifest["sampling"]["maxOccurrencesPerPair"], "pair sampling metadata");
                Equal(24, (int)manifest["sampling"]["maxTransitionsPerGear"], "per gear sampling metadata");
                Check((int)manifest["sampling"]["boundSkipped"] > 0, "global sampling bound metadata");
            }
            finally { DeleteTransitionFolder(folder); }
        }

        private static void TransitionRecorderSamplingResetAndBreak()
        {
            var recorder = new TransitionFrameRecorder(true);
            RecordPair(recorder, 0);
            recorder.BreakSequence();
            RecordPair(recorder, 10);
            recorder.BreakSequence();
            RecordPair(recorder, 20);
            Equal(2, recorder.SavedTransitions, "break keeps sampling quotas");
            Check(recorder.SamplingSkipped > 0, "break does not erase pair quotas");

            recorder.Reset();
            RecordPair(recorder, 30);
            Equal(1, recorder.SavedTransitions, "reset clears sampling quotas");
            Equal(0, recorder.SamplingSkipped, "reset clears sampling skip counts");
        }

        private static void TransitionRecorderBreaksSequence()
        {
            var recorder = new TransitionFrameRecorder(true);
            recorder.Record(Frame(1), new List<LitBlob>(), 10, "1", 5000);
            recorder.Record(Frame(2), new[] { Blob(255, 0, 0) }, 20, "1", 6000);
            recorder.BreakSequence();
            recorder.Record(Frame(3), new[] { Blob(255, 255, 0) }, 30, "1", 7000);
            Equal(1, recorder.TransitionsSeen, "break does not create a stale transition");
            Equal(1, recorder.SavedTransitions, "break keeps the incomplete transition for export");
            var folder = TempTransitionFolder();
            try
            {
                var exported = recorder.Export(folder, Metadata());
                Equal(2, exported.ImagesWritten, "available images survive an interrupted transition");
                Equal(1, exported.IncompleteTransitions, "missing following frame is reported");
                Equal(0, exported.Errors.Count, "an interrupted sequence is not an export error");
                recorder.Reset();
                Equal(0, recorder.SavedTransitions, "reset discards retained images");
                Equal(0, recorder.TransitionsSeen, "reset discards old transition counts");
            }
            finally { DeleteTransitionFolder(folder); }
        }

        private static void TransitionRecorderOffDoesNotWrite()
        {
            var folder = Path.Combine(Path.GetTempPath(), "lovely-transition-off-" + Guid.NewGuid().ToString("N"));
            try
            {
                var recorder = new TransitionFrameRecorder(false);
                recorder.Record(Frame(1), new[] { Blob(255, 0, 0) }, 10, "1", 5000);
                var result = recorder.Export(folder, Metadata());
                Check(!result.Enabled, "transition diagnostics are off");
                Check(!Directory.Exists(folder), "disabled recorder does no disk work");
            }
            finally { DeleteTransitionFolder(folder); }
        }

        private static void TransitionRecorderMemoryBound()
        {
            var folder = TempTransitionFolder();
            try
            {
                var recorder = new TransitionFrameRecorder(true);
                // Dimensions exceed the 64 MiB budget, but the backing test buffer stays tiny.
                var oversized = new PixelFrame(new byte[4], 4097, 4097, 4, 4, 2, 0);
                recorder.Record(oversized, new List<LitBlob>(), 10, "1", 5000);
                Check(recorder.LimitReached, "pixel memory limit is reported");
                var result = recorder.Export(folder, Metadata());
                Equal(0, result.ImagesWritten, "oversized frame is not exported");
            }
            finally { DeleteTransitionFolder(folder); }
        }

        private static PixelFrame Frame(int value)
        {
            byte pixel = (byte)value;
            return PixelFrame.Bgra32(new[] { pixel, pixel, pixel, (byte)255 }, 1, 1);
        }

        private static LitBlob Blob(int r, int g, int b)
        {
            return new LitBlob { Left = 0, Right = 0, Color = new LedColor(r, g, b) };
        }

        private static List<LitBlob> Blobs(int count)
        {
            var blobs = new List<LitBlob>();
            for (int i = 0; i < count; i++) blobs.Add(Blob(0, 255, 0));
            return blobs;
        }

        private static void RecordPair(TransitionFrameRecorder recorder, long time)
        {
            recorder.Record(Frame(1), new List<LitBlob>(), time, "1", 5000);
            recorder.Record(Frame(2), new[] { Blob(255, 0, 0) }, time + 1, "1", 6000);
            recorder.Record(Frame(3), new[] { Blob(255, 0, 0) }, time + 2, "1", 6000);
        }

        private static TransitionFrameExportMetadata Metadata()
        {
            return new TransitionFrameExportMetadata
            {
                GameName = "Automobilista2",
                CarId = "test-car",
                Fps = 60,
                Region = new TransitionFrameRegion(10, 20, 2, 3),
                DetectorSettings = new Dictionary<string, object> { { "minBrightness", 90 } },
            };
        }

        private static string TempTransitionFolder()
        {
            string path = Path.Combine(Path.GetTempPath(), "lovely-transition-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTransitionFolder(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch { }
        }
    }
}
