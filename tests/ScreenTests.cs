using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Profile;
using LovelyCarDataCapture.Repo;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Tests
{
    /// <summary>
    /// Tests for reading rev lights off the screen, run against a real recording: a two minute clip of
    /// the AMS2 Audi R8 LMS GT3 evo II revved to the limiter three times in neutral, at 5120x1440.
    /// data/ams2-audi-r8-lms-gt3-evo-ii.csv holds what the detector found in each frame of it with the
    /// RPM shown on screen at the time, and the three PNGs are single frames from the same clip.
    /// </summary>
    internal static partial class Program
    {
        /// <summary>The car's values in the repo, LED 1..12 (0 = gap), redline last.</summary>
        private static readonly int[] AudiFileRpm = { 7000, 7115, 0, 7230, 7345, 7460, 7575, 7690, 7805, 0, 7920, 8035 };
        private const int AudiFileRedline = 8150;

        private static string DataPath(string name) => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", name);

        private static PixelFrame LoadFrame(string name)
        {
            using (var bmp = new Bitmap(DataPath(name)))
            {
                var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var bytes = new byte[data.Stride * bmp.Height];
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                    return PixelFrame.Bgra32(bytes, bmp.Width, bmp.Height, data.Stride);
                }
                finally { bmp.UnlockBits(data); }
            }
        }

        private sealed class RecordedFrame
        {
            public string Gear;
            public int Rpm;
            public long TimeMs;
            public List<LitBlob> Blobs = new List<LitBlob>();
        }

        private static List<RecordedFrame> LoadRecording() => LoadFrames(DataPath("ams2-audi-r8-lms-gt3-evo-ii.csv"));

        /// <summary>
        /// Runs a saved capture's frames (--replay frames.csv [--repo-file car.json] [--game name] [--car id])
        /// back through the composer and prints the report, as if it had just been exported.
        /// </summary>
        private static void Replay(string[] args)
        {
            string Arg(string name, string fallback)
            {
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
                return fallback;
            }
            string frames = Arg("--replay", null);
            string repoFile = Arg("--repo-file", null);
            string game = Arg("--game", "Automobilista2");
            string car = Arg("--car", Path.GetFileNameWithoutExtension(frames).Replace(".frames", ""));

            var session = new CaptureSession(game, car);
            foreach (var f in LoadFrames(frames)) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var lookup = repoFile == null ? null
                : new RepoLookup { Status = RepoLookupStatus.Found, RelativePath = Path.GetFileName(repoFile), Text = File.ReadAllText(repoFile) };
            var result = ProfileComposer.Compose(session, new CaptureSettings(), lookup, DateTime.Now);
            Console.WriteLine(string.Join(Environment.NewLine, result.Report));
            Console.WriteLine();
            Console.WriteLine(result.Profile.ToJson());
        }

        private static List<RecordedFrame> LoadFrames(string path)
        {
            var frames = new List<RecordedFrame>();
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var cells = line.Split(',');
                if (cells.Length < 5) continue;
                var f = new RecordedFrame
                {
                    TimeMs = long.Parse(cells[1], CultureInfo.InvariantCulture),
                    Gear = cells[2],
                    Rpm = int.Parse(cells[3], CultureInfo.InvariantCulture),
                };
                foreach (var blob in cells[4].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = blob.Split(':');
                    int x = int.Parse(p[0], CultureInfo.InvariantCulture);
                    f.Blobs.Add(new LitBlob
                    {
                        Left = x - 9,
                        Right = x + 9,
                        Color = new LedColor(int.Parse(p[1], CultureInfo.InvariantCulture),
                                             int.Parse(p[2], CultureInfo.InvariantCulture),
                                             int.Parse(p[3], CultureInfo.InvariantCulture)),
                    });
                }
                frames.Add(f);
            }
            return frames;
        }

        private static ScreenLedResult CaptureRecording()
        {
            var capture = new ScreenLedCapture();
            foreach (var f in LoadRecording()) capture.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            return capture.Result();
        }

        // ---------- the detector, on real frames ----------
        private static void ScreenDetectorOnFrames()
        {
            var detector = new StripDetector();
            var region = new PixelRect(0, 0, 440, 100);

            Equal(0, detector.Detect(LoadFrame("strip-dark.png"), region).Count, "lights lit at idle");

            var partial = detector.Detect(LoadFrame("strip-partial.png"), region);
            Equal(7, partial.Count, "lights lit at 7667 rpm");
            Check(partial.All(b => b.Width >= 12 && b.Width <= 24), "each light is about 18px wide, got " +
                  string.Join(", ", partial.Select(b => b.Width)));
            // Green first, then yellow: hue falls from left to right along the strip.
            Check(partial[0].Color.Hue > partial[6].Color.Hue + 20,
                  "the leftmost light is greener than the last lit one (" + partial[0].Color + " vs " + partial[6].Color + ")");

            var full = detector.Detect(LoadFrame("strip-full.png"), region);
            Equal(10, full.Count, "lights lit above the redline");
            Check(full.All(b => b.Color.Hue < 30), "every light is red above the redline");
            var spacing = Enumerable.Range(1, full.Count - 1).Select(i => full[i].CenterX - full[i - 1].CenterX).ToList();
            Equal(2, spacing.Count(s => s > 40), "two gaps in the strip, spacings: " +
                  string.Join(", ", spacing.Select(s => s.ToString("0", CultureInfo.InvariantCulture))));
        }

        // ---------- the strip's shape ----------
        private static void ScreenCalibration()
        {
            var calibration = new StripCalibration();
            foreach (var f in LoadRecording()) calibration.Add(f.Blobs);
            var layout = calibration.Build(out string problem);
            Check(layout != null, "the strip was made out: " + problem);
            Equal(12, layout.LedNumber, "slots on the strip");
            Equal(2, layout.GapCount, "gaps on the strip");
            Check(layout.IsGap[2] && layout.IsGap[9], "gaps are LED 3 and LED 10, got " +
                  string.Join(", ", Enumerable.Range(0, 12).Where(i => layout.IsGap[i]).Select(i => i + 1)));
            Check(layout.Pitch > 20 && layout.Pitch < 32, "spacing is about 26px, got " + layout.Pitch.ToString("0.0", CultureInfo.InvariantCulture));
        }

        /// <summary>One frame is all the Test button in SimHub has to work with, so that has to be enough.</summary>
        private static void ScreenCalibrationFromOneFrame()
        {
            var blobs = new StripDetector().Detect(LoadFrame("strip-full.png"), new PixelRect(0, 0, 440, 100));
            var calibration = new StripCalibration();
            calibration.Add(blobs);
            var layout = calibration.Build(out string problem);
            Check(layout != null, "a single full frame is enough to see the strip: " + problem);
            Equal(12, layout.LedNumber, "slots seen in one frame");
            Equal(2, layout.GapCount, "gaps seen in one frame");
        }

        // ---------- thresholds, colours, redline ----------
        private static void ScreenThresholdsMatchRepoFile()
        {
            var result = CaptureRecording();
            Check(result.Layout != null, "the strip was made out");
            Equal(1, result.Gears.Count, "gears captured");

            var leds = result.Gears[0].Leds;
            var offsets = new List<int>();
            for (int i = 0; i < 12; i++)
            {
                if (AudiFileRpm[i] == 0) { Check(leds[i] == null, "LED " + (i + 1) + " is a gap and never lit"); continue; }
                Check(leds[i] != null, "LED " + (i + 1) + " was seen lit");
                int diff = leds[i].Rpm - AudiFileRpm[i];
                offsets.Add(diff);
                Check(Math.Abs(diff) <= 60, "LED " + (i + 1) + " measured " + leds[i].Rpm + ", repo says " + AudiFileRpm[i] + " (" + diff.ToString("+#;-#;0") + ")");
            }
            // Every value reads a little low because the RPM on screen lags the game by a frame or two.
            Check(offsets.Average() < 0 && offsets.Average() > -40, "readings sit just below the file's values, average " +
                  offsets.Average().ToString("0", CultureInfo.InvariantCulture));

            Check(result.RedlineRpm.HasValue, "the redline colour change was found");
            Check(Math.Abs(result.RedlineRpm.Value - AudiFileRedline) <= 60,
                  "redline measured " + result.RedlineRpm + ", repo says " + AudiFileRedline);
            Equal("#FFFF0000", result.RedlineColor, "redline colour");
            Check(!result.BlinkSeen, "the strip doesn't blink at the limiter");
        }

        private static void ScreenColorsAreGrouped()
        {
            var result = CaptureRecording();
            var groups = result.ColorGroups;
            Equal(4, groups.Count, "four colours were told apart");
            string ColorOf(int led) => groups.First(g => g.Slots.Contains(led - 1)).Hex;
            // The same colours the repo file states, worked out from the screen alone.
            foreach (int led in new[] { 1, 2, 4, 5 }) Equal("#FF00FF00", ColorOf(led), "LED " + led + " is green");
            foreach (int led in new[] { 6, 7 }) Equal("#FFFFFF00", ColorOf(led), "LED " + led + " is yellow");
            foreach (int led in new[] { 8, 9 }) Equal("#FFFF8000", ColorOf(led), "LED " + led + " is orange");
            foreach (int led in new[] { 11, 12 }) Equal("#FFFF0000", ColorOf(led), "LED " + led + " is red");
            Check(groups.All(g => g.Slots.All(s => s != 2 && s != 9)), "gaps have no colour");
        }

        /// <summary>
        /// A strip like the AMS2 McLaren 720S GT3 Evo: green to red over eight lights, then the whole
        /// strip turns cyan at the redline and changes again close to the limiter. The file keeps the
        /// first change; the second is reported so it isn't mistaken for the file being wrong.
        /// </summary>
        private static void ScreenTwoStageRedline()
        {
            var green = new LedColor(110, 239, 102);
            var red = new LedColor(247, 52, 41);
            var cyan = new LedColor(93, 235, 251);
            var flash = new LedColor(250, 240, 90);      // second stage: the strip goes yellow-white
            var thresholds = new[] { 6000, 6400, 6800, 7200 };
            const int redline = 7600, secondStage = 8100;

            var capture = new ScreenLedCapture();
            long time = 0;
            for (int climb = 0; climb < 3; climb++)
            {
                for (int rpm = 5000; rpm <= 8600; rpm += 20)
                {
                    var blobs = new List<LitBlob>();
                    for (int led = 0; led < 4; led++)
                    {
                        if (rpm <= thresholds[led]) continue;
                        var color = rpm > secondStage ? flash : rpm > redline ? cyan : led < 2 ? green : red;
                        blobs.Add(new LitBlob { Left = 100 + led * 30 - 9, Right = 100 + led * 30 + 9, Color = color });
                    }
                    capture.Record("3", rpm, time += 16, blobs);
                }
                for (int rpm = 8600; rpm >= 5000; rpm -= 40) capture.Record("3", rpm, time += 16, new List<LitBlob>());
            }

            var result = capture.Result();
            Check(result.RedlineRpm.HasValue, "the first colour change was found");
            Check(Math.Abs(result.RedlineRpm.Value - redline) <= 40, "the redline is about " + redline + ", got " + result.RedlineRpm);
            Equal("#FF00FFFF", result.RedlineColor, "the redline colour is the cyan the strip turns");
            Check(result.SecondStageRpm.HasValue, "the second stage was found");
            Check(Math.Abs(result.SecondStageRpm.Value - secondStage) <= 60,
                  "the second stage is about " + secondStage + ", got " + result.SecondStageRpm);
            Check(result.Notes.Any(n => n.Contains("second time")), "the report says the strip changed colour twice");
        }

        /// <summary>
        /// What a real track gives you: a couple of gears swept cleanly, and higher gears entered
        /// halfway up the rev range because a corner was coming. The half-caught gears must not be
        /// written down as if they were measured.
        /// </summary>
        private static void ScreenPartialGearsAreNotWrittenDown()
        {
            var green = new LedColor(110, 239, 102);
            var red = new LedColor(247, 52, 41);
            var cyan = new LedColor(93, 235, 251);
            var thresholds = new[] { 6000, 6400, 6800, 7200 };
            const int redline = 7600;

            var session = new CaptureSession("Automobilista2", "Some GT3");
            long time = 0;
            void Sweep(string gear, int from, int to, int step)
            {
                for (int rpm = from; rpm <= to; rpm += step)
                {
                    var blobs = new List<LitBlob>();
                    for (int led = 0; led < 4; led++)
                    {
                        if (rpm <= thresholds[led]) continue;
                        var color = rpm > redline ? cyan : led < 2 ? green : red;
                        blobs.Add(new LitBlob { Left = 100 + led * 30 - 9, Right = 100 + led * 30 + 9, Color = color });
                    }
                    session.Screen.Record(gear, rpm, time += 16, blobs);
                }
                for (int rpm = to; rpm >= from; rpm -= step * 3) session.Screen.Record(gear, rpm, time += 16, new List<LitBlob>());
            }

            foreach (var gear in new[] { "2", "3" })
                for (int climb = 0; climb < 2; climb++) Sweep(gear, 5200, 7900, 20);
            // Gear 5: picked up at 7000 with half the strip already lit, then braked.
            Sweep("5", 7000, 7300, 20);

            var lookup = new RepoLookup { Status = RepoLookupStatus.Found, RelativePath = "automobilista2/some-gt3.json", Text = FourPairJson() };
            var result = ProfileComposer.Compose(session, new CaptureSettings(), lookup, new DateTime(2026, 9, 17));
            if (_showReports) Console.WriteLine(string.Join(Environment.NewLine, result.Report));
            var p = result.Profile;

            for (int i = 0; i < 4; i++)
            {
                int led = i + 1;
                Check(Math.Abs(p.LedRpm["2"][led] - thresholds[i]) <= 40,
                      "gear 2 LED " + led + " measured " + p.LedRpm["2"][led] + ", expected about " + thresholds[i]);
            }
            Check(p.LedRpm["5"][1] < 6200, "gear 5's first light is not the 7000 rpm it was first seen lit at, it is " + p.LedRpm["5"][1]);
            Check(p.LedRpm["5"].SequenceEqual(p.LedRpm["2"]) || p.LedRpm["5"].SequenceEqual(p.LedRpm["3"]),
                  "gear 5 follows a gear that was measured right through");
            Check(p.GearOrder.All(g => p.LedRpm[g].SequenceEqual(p.LedRpm["2"])),
                  "every gear ends up with the same values, as the measured gears agreed");
            Check(result.Report.Any(l => l.Contains("same lights in every gear")), "the report says why");
            var row = p.LedRpm["2"];
            for (int i = 2; i <= 4; i++) Check(row[i] >= row[i - 1], "the lights are in order: " + string.Join(", ", row));
        }

        private static string FourPairJson() => @"{
  ""carName"": ""Some GT3"",
  ""carId"": ""Some GT3"",
  ""carClass"": ""GT3"",
  ""ledNumber"": 4,
  ""redlineBlinkInterval"": 0,
  ""ledColor"": [""#FF0000FF"",""#FF00FF00"",""#FF00FF00"",""#FFFF0000"",""#FFFF0000""],
  ""ledRpm"": [
    {
      ""R"": [7333,6000,6200,6600,7000],
      ""N"": [7333,6000,6200,6600,7000],
      ""1"": [7333,6000,6200,6600,7000],
      ""2"": [7333,6000,6200,6600,7000],
      ""3"": [7333,6000,6200,6600,7000],
      ""4"": [7333,6000,6200,6600,7000],
      ""5"": [7333,6000,6200,6600,7000]
    }
  ]
}";

        /// <summary>
        /// The McLaren 720S GT3 Evo case: the last pair of lights comes on at the same RPM as the whole
        /// strip changes colour. Their own colour is therefore never on screen, which is not the same as
        /// them being gaps, and the redline must not end up below a light that comes on beneath it.
        /// </summary>
        private static void ScreenLastPairLightsAtTheRedline()
        {
            var green = new LedColor(110, 239, 102);
            var red = new LedColor(247, 52, 41);
            var cyan = new LedColor(93, 235, 251);
            var thresholds = new[] { 6000, 6400, 6800, 7200, 7600 };
            const int redline = 7600;

            var session = new CaptureSession("Automobilista2", "Late Pair GT3");
            long time = 0;
            foreach (var gear in new[] { "2", "3" })
            {
                for (int climb = 0; climb < 2; climb++)
                {
                    for (int rpm = 5400; rpm <= 8000; rpm += 20)
                    {
                        var blobs = new List<LitBlob>();
                        for (int led = 0; led < thresholds.Length; led++)
                        {
                            if (rpm <= thresholds[led]) continue;
                            var color = rpm > redline ? cyan : led < 2 ? green : red;
                            blobs.Add(new LitBlob { Left = 100 + led * 30 - 9, Right = 100 + led * 30 + 9, Color = color });
                        }
                        session.Screen.Record(gear, rpm, time += 16, blobs);
                    }
                    for (int rpm = 8000; rpm >= 5400; rpm -= 60) session.Screen.Record(gear, rpm, time += 16, new List<LitBlob>());
                }
            }

            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 17));
            if (_showReports) Console.WriteLine(string.Join(Environment.NewLine, result.Report));
            var p = result.Profile;
            var row = p.LedRpm["2"];

            Equal(5, p.LedNumber, "five lights, none of them gaps");
            Check(row[0] >= row[5], "the redline " + row[0] + " is not below the last light at " + row[5]);
            Check(Math.Abs(row[5] - redline) <= 40, "the last light is about " + redline + ", got " + row[5]);
            Check(!LedLayout.IsGapColor(p.LedColor[5]), "the last light is not written as a gap, it is " + p.LedColor[5]);
            Equal(p.LedColor[0], p.LedColor[5], "a light only ever seen in the redline colour takes that colour");
            Check(result.Report.Any(l => l.Contains("couldn't be seen")), "the report says their colour was never visible");
        }

        /// <summary>
        /// No gear swept right through - one caught the bottom of the strip, another the top, as a track
        /// with corners in it tends to give. Between them they cover every light and agree where they
        /// overlap, which is enough to fill the whole file.
        /// </summary>
        private static void ScreenPoolsAgreeingGears()
        {
            var green = new LedColor(110, 239, 102);
            var red = new LedColor(247, 52, 41);
            var cyan = new LedColor(93, 235, 251);
            var thresholds = new[] { 6000, 6400, 6800, 7200 };
            const int redline = 7600;

            var session = new CaptureSession("Automobilista2", "Pooled GT3");
            long time = 0;
            void Sweep(string gear, int from, int to)
            {
                for (int climb = 0; climb < 2; climb++)
                {
                    for (int rpm = from; rpm <= to; rpm += 20)
                    {
                        var blobs = new List<LitBlob>();
                        for (int led = 0; led < thresholds.Length; led++)
                        {
                            if (rpm <= thresholds[led]) continue;
                            var color = rpm > redline ? cyan : led < 2 ? green : red;
                            blobs.Add(new LitBlob { Left = 100 + led * 30 - 9, Right = 100 + led * 30 + 9, Color = color });
                        }
                        session.Screen.Record(gear, rpm, time += 16, blobs);
                    }
                    for (int rpm = to; rpm >= from; rpm -= 60) session.Screen.Record(gear, rpm, time += 16, new List<LitBlob>());
                }
            }

            Sweep("2", 5600, 7000);   // the bottom of the strip, then a corner
            Sweep("3", 6600, 8000);   // picked up higher, carries on past the redline

            var lookup = new RepoLookup { Status = RepoLookupStatus.Found, RelativePath = "automobilista2/pooled-gt3.json", Text = FourPairJson() };
            var result = ProfileComposer.Compose(session, new CaptureSettings(), lookup, new DateTime(2026, 9, 17));
            if (_showReports) Console.WriteLine(string.Join(Environment.NewLine, result.Report));
            var p = result.Profile;
            var row = p.LedRpm["2"];

            for (int i = 0; i < thresholds.Length; i++)
                Check(Math.Abs(row[i + 1] - thresholds[i]) <= 40,
                      "LED " + (i + 1) + " came from whichever gear saw it: " + row[i + 1] + ", expected about " + thresholds[i]);
            Check(row[0] >= row[4], "the redline is not below the last light");
            Check(p.GearOrder.All(g => p.LedRpm[g].SequenceEqual(row)), "every gear got the pooled values");
            Check(result.Report.Any(l => l.Contains("pooled")), "the report says the gears were pooled");
        }

        /// <summary>
        /// The AMS2 BMW M8 GTE case: above the redline the strip blinks - dark 100 ms, lit 100 ms - and
        /// keeps its own colours while it does. Held at the limiter for seconds at 60 fps, the blinks'
        /// dark-then-lit edges used to outnumber the real switch-ons and every light came out at the
        /// limiter's RPM. They must be recognised as blinks, timed, and kept out of the thresholds.
        /// </summary>
        private static void ScreenBlinkingStripIsNotAThreshold()
        {
            var colours = new[] { new LedColor(110, 239, 102), new LedColor(250, 240, 90), new LedColor(252, 190, 60), new LedColor(247, 52, 41) };
            var thresholds = new[] { 6000, 6120, 6240, 6360 };
            const int redline = 6600;

            var session = new CaptureSession("Automobilista2", "Blinking GTE");
            session.RecordCar("Blinking GTE", "GTE");
            long time = 0;
            List<LitBlob> Frame(int rpm)
            {
                var blobs = new List<LitBlob>();
                bool dark = rpm > redline && (time / 100) % 2 == 1;
                if (dark) return blobs;
                for (int led = 0; led < thresholds.Length; led++)
                {
                    if (rpm <= thresholds[led]) continue;
                    blobs.Add(new LitBlob { Left = 100 + led * 30 - 9, Right = 100 + led * 30 + 9, Color = colours[led] });
                }
                return blobs;
            }

            var random = new Random(8);
            foreach (var gear in new[] { "2", "3" })
            {
                for (int climb = 0; climb < 3; climb++)
                {
                    for (int rpm = 5600; rpm < 6680; rpm += 8)
                        session.Screen.Record(gear, rpm, time += 16, Frame(rpm));
                    // Three seconds on the limiter, the revs bouncing just under it.
                    for (int f = 0; f < 190; f++)
                    {
                        int rpm = 6650 + random.Next(0, 50);
                        session.Screen.Record(gear, rpm, time += 16, Frame(rpm));
                    }
                    for (int rpm = 6680; rpm >= 5600; rpm -= 40) session.Screen.Record(gear, rpm, time += 16, Frame(rpm));
                }
            }

            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 17));
            if (_showReports) Console.WriteLine(string.Join(Environment.NewLine, result.Report));
            var p = result.Profile;
            var row = p.LedRpm["2"];

            for (int i = 0; i < thresholds.Length; i++)
                Check(Math.Abs(row[i + 1] - thresholds[i]) <= 40,
                      "LED " + (i + 1) + " is its own threshold, not the limiter: " + row[i + 1] + ", expected about " + thresholds[i]);
            Check(p.RedlineBlinkInterval >= 84 && p.RedlineBlinkInterval <= 130,
                  "the blink was timed at about 100 ms, got " + p.RedlineBlinkInterval);
            Check(Math.Abs(row[0] - redline) <= 60, "the redline is where the blinking starts, about " + redline + ", got " + row[0]);
            Equal("#00000000", p.LedColor[0], "a strip that blinks in its own colours gets a transparent redline colour");
            Check(result.Report.Any(l => l.Contains("blinks at the limiter")), "the report says the strip blinks");
        }

        // ---------- into a car file ----------
        private static void ComposeScreenIntoRepoFile()
        {
            var session = new CaptureSession("Automobilista2", "Audi R8 LMS GT3 evo II");
            foreach (var f in LoadRecording()) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);

            var lookup = new RepoLookup
            {
                Status = RepoLookupStatus.Found,
                RelativePath = "automobilista2/audi-r8-lms-gt3-evo-ii.json",
                Text = AudiRepoJson(),
            };
            var result = ProfileComposer.Compose(session, new CaptureSettings(), lookup, new DateTime(2026, 9, 17));
            if (_showReports) Console.WriteLine(string.Join(Environment.NewLine, result.Report));

            var p = result.Profile;
            Check(result.Source.Contains("screen"), "the report says where the values came from: " + result.Source);
            Equal(12, p.LedNumber, "LED count is unchanged");
            var row = p.LedRpm["N"];
            for (int i = 1; i <= 12; i++)
            {
                if (AudiFileRpm[i - 1] == 0) { Equal(0, row[i], "LED " + i + " stays a gap"); continue; }
                Check(Math.Abs(row[i] - AudiFileRpm[i - 1]) <= 60, "LED " + i + " is close to the repo value: " + row[i]);
            }
            Check(Math.Abs(row[0] - AudiFileRedline) <= 60, "the redline is close to the repo value: " + row[0]);
            Equal("#FF00FF00", p.LedColor[1], "the repo file's colours are kept");
            // The Audi's flash is red and its file says red, so nothing to report; the AMS2 cars that
            // flash cyan against a file saying blue are the reason this is checked at all.
            Check(!result.Report.Any(l => l.Contains("redline color is")), "no redline colour difference on a car where they match");
            Equal("#00000000", p.LedColor[3], "the gap colour is kept");
            Equal(0, p.RedlineBlinkInterval, "no blink was measured, so the file's 0 stays");
            Check(!p.GearOrder.Contains("3") || p.LedRpm["3"].SequenceEqual(p.LedRpm["N"]) || p.LedRpm["3"][1] == AudiFileRpm[0],
                  "gears that weren't driven keep the repo values");
            Check(result.AtsrProblems.Count == 0, "no ATSR problems: " + string.Join("; ", result.AtsrProblems));
        }

        /// <summary>A new car has no file to follow, so the strip, colours and gaps all come from the screen.</summary>
        private static void ComposeScreenNewCar()
        {
            var session = new CaptureSession("Automobilista2", "Some New Car");
            session.RecordCar("Some New Car", "GT3");
            foreach (var f in LoadRecording()) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);

            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 17));
            var p = result.Profile;
            Equal(12, p.LedNumber, "the strip's slots became the LED count");
            Equal("#00000000", p.LedColor[3], "LED 3 is written as a gap");
            Equal("#00000000", p.LedColor[10], "LED 10 is written as a gap");
            Equal("#FF00FF00", p.LedColor[1], "LED 1 is green");
            Equal("#FFFF0000", p.LedColor[12], "LED 12 is red");
            Equal(0, p.LedRpm["N"][3], "a gap has no RPM");
            Check(p.LedRpm["N"][1] > 6900 && p.LedRpm["N"][1] < 7100, "LED 1 is around 7000 rpm, got " + p.LedRpm["N"][1]);
        }

        /// <summary>Colours read off a screen are washed out, so they are matched by their order, not their hue.</summary>
        private static void ScreenPaletteNaming()
        {
            // Measured in the recording: pure green renders as rgb(138,177,106), red as rgb(190,96,60).
            var colors = new[]
            {
                new LedColor(138, 177, 106), new LedColor(138, 177, 106),
                new LedColor(0, 0, 0),
                new LedColor(187, 173, 92), new LedColor(189, 162, 85),
                new LedColor(190, 96, 60),
            };
            var gaps = new[] { false, false, true, false, false, false };
            var groups = LedPalette.Group(colors, gaps);
            Equal(4, groups.Count, "four colours were told apart");
            Equal("#FFFF0000", groups.First(g => g.Slots.Contains(5)).Hex, "the reddest colour is red");
            Equal("#FF00FF00", groups.First(g => g.Slots.Contains(0)).Hex, "the furthest colour from it is green");
            Equal("#FFFFFF00", groups.First(g => g.Slots.Contains(3)).Hex, "the one below green is yellow");
            Equal("#FFFF8000", groups.First(g => g.Slots.Contains(4)).Hex, "the one above red is orange");
            Check(groups.All(g => !g.Slots.Contains(2)), "the gap has no colour");

            // Measured in an AMS2 Cadillac V-Series.R capture, whose strip flashes blue at the limiter.
            // Anchoring the scale on the redline colour used to call every light red.
            var cadillac = new[]
            {
                new LedColor(121, 236, 117), new LedColor(121, 236, 117),
                new LedColor(254, 226, 101), new LedColor(254, 226, 101),
                new LedColor(246, 84, 46), new LedColor(246, 84, 46),
                new LedColor(254, 226, 101), new LedColor(254, 226, 101),
                new LedColor(121, 236, 117), new LedColor(121, 236, 117),
            };
            var mirrored = LedPalette.Group(cadillac, null);
            Equal(3, mirrored.Count, "the mirrored strip uses three colours");
            string Of(int led) => mirrored.First(g => g.Slots.Contains(led - 1)).Hex;
            foreach (int led in new[] { 1, 2, 9, 10 }) Equal("#FF00FF00", Of(led), "LED " + led + " is green");
            foreach (int led in new[] { 3, 4, 7, 8 }) Equal("#FFFFFF00", Of(led), "LED " + led + " is yellow");
            foreach (int led in new[] { 5, 6 }) Equal("#FFFF0000", Of(led), "LED " + led + " is red");

            // A redline flash is named on its own: nothing says it has to be red, and it gets a finer
            // scale than the strip because nothing else competes for a colour.
            Equal("#FF0000FF", LedPalette.Classify(new LedColor(90, 120, 240), out _), "a blue flash is blue");
            Equal("#FFFF0000", LedPalette.Classify(new LedColor(230, 70, 50), out _), "a red flash is red");
            // Measured above the redline in AMS2: the McLaren 720S GT3 Evo and the Cadillac V-Series.R,
            // seven degrees apart and told apart by eye as well.
            Equal("#FF00FFFF", LedPalette.Classify(new LedColor(78, 233, 249), out _), "the McLaren's flash is cyan");
            Equal("#FF00BFFF", LedPalette.Classify(new LedColor(60, 206, 248), out _), "the Cadillac's flash is the lighter blue");
        }

        /// <summary>
        /// Grabs a region of the real screen with the plugin's own grabber and reports what it finds.
        /// Used by hand (--grab x,y,w,h) to check the capture path outside SimHub.
        /// </summary>
        private static void GrabFromScreen(string spec)
        {
            var parts = spec.Split(',');
            var region = new PixelRect(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
            using (var grabber = new LovelyCarDataCapture.Plugin.ScreenGrabber())
            {
                var frame = grabber.Grab(region, out string problem);
                if (frame == null) { Console.WriteLine("Grab failed: " + problem); return; }
                var blobs = new StripDetector().Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height));
                Console.WriteLine("Grabbed " + frame.Width + "x" + frame.Height + ", " + blobs.Count + " lights:");
                foreach (var b in blobs)
                    Console.WriteLine("  x " + b.CenterX.ToString("0", CultureInfo.InvariantCulture) + " w " + b.Width + " " + b.Color + " " + b.Color.ToHex());
                var calibration = new StripCalibration();
                calibration.Add(blobs);
                var layout = calibration.Build(out string why);
                Console.WriteLine(layout == null ? "No strip: " + why
                    : "Strip: " + layout.LedNumber + " slots, " + layout.GapCount + " gaps, spacing " + layout.Pitch.ToString("0.0", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Shows the capture box on its own (--box x,y,w,h), to try it outside SimHub.</summary>
        private static void ShowCaptureBox(string spec)
        {
            var parts = spec.Split(',');
            var start = new PixelRect(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
            var thread = new System.Threading.Thread(() =>
            {
                var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnLastWindowClose };
                var window = new LovelyCarDataCapture.Plugin.CaptureBoxWindow(start,
                    region => Console.WriteLine("Saved: " + region.Width + "x" + region.Height + " at " + region.X + "," + region.Y),
                    Describe);
                window.Show();
                app.Run();
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
        }

        /// <summary>Shows the overlay panel on its own (--overlay), to see it outside SimHub.</summary>
        private static void ShowOverlay()
        {
            var thread = new System.Threading.Thread(() =>
            {
                var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                int rpm = 6200;
                var overlay = new LovelyCarDataCapture.Plugin.CaptureOverlay(
                    () => "Recording - gear 3 - " + (rpm += 37) + " rpm - 7 lights lit - 1420 frames, up to 10 lights at once",
                    (x, y) => Console.WriteLine("Moved to " + x + "," + y), 0, 0);
                overlay.SetCapturing(true);
                overlay.Message("Capture started, watching the rev lights on screen. Rev slowly from idle to the limiter a few times.");
                var quit = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                quit.Tick += (s, e) => app.Shutdown();
                quit.Start();
                app.Run();
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
        }

        /// <summary>Shows the SimHub settings page on its own (--settings), to see it outside SimHub.</summary>
        private static void ShowSettingsPage()
        {
            var thread = new System.Threading.Thread(() =>
            {
                var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnLastWindowClose };
                var settings = new CaptureSettings
                {
                    ScreenCapture = true,
                    ScreenBoxX = 2191, ScreenBoxY = 1150, ScreenBoxWidth = 664, ScreenBoxHeight = 100,
                };
                bool capturing = false;
                var control = new LovelyCarDataCapture.Plugin.ScreenSettingsControl(
                    settings, () => { }, Describe, (save, test) => { }, Console.WriteLine,
                    () => "C:" + Path.DirectorySeparatorChar + Path.Combine("Users", "jerky", "OneDrive", "Documents", "SimHub", "LovelyCarDataCapture"),
                    () => capturing = true, () => capturing = false, () => capturing,
                    () => capturing ? "Recording - gear 3 - 7450 rpm - 7 lights lit - 1420 frames" : "Not capturing");
                new System.Windows.Window
                {
                    Title = "Settings preview",
                    Topmost = true,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                    Left = 60,
                    Top = 40,
                    Width = 880,
                    Height = 1000,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)),
                    Foreground = System.Windows.Media.Brushes.White,
                    Content = control,
                }.Show();
                app.Run();
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
        }

        private static string Describe(PixelRect region)
        {
            using (var grabber = new LovelyCarDataCapture.Plugin.ScreenGrabber())
            {
                var frame = grabber.Grab(region, out string problem);
                if (frame == null) return "Couldn't read the screen: " + problem;
                var blobs = new StripDetector().Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height));
                var calibration = new StripCalibration();
                calibration.Add(blobs);
                var layout = calibration.Build(out _);
                return blobs.Count + " lights lit" +
                       (layout != null && layout.GapCount > 0 ? ", " + layout.GapCount + " gap(s) between them" : "") +
                       (blobs.Count > 0 ? ": " + string.Join(", ", blobs.Select(b => b.Color.ToHex())) : "");
            }
        }

        private static string AudiRepoJson() => @"{
  ""carName"": ""Audi R8 LMS GT3 evo II"",
  ""carId"": ""Audi R8 LMS GT3 evo II"",
  ""carClass"": ""GT3"",
  ""ledNumber"": 12,
  ""redlineBlinkInterval"": 0,
  ""ledColor"": [""#FFFF0000"",""#FF00FF00"",""#FF00FF00"",""#00000000"",""#FF00FF00"",""#FF00FF00"",""#FFFFFF00"",""#FFFFFF00"",""#FFFF8000"",""#FFFF8000"",""#00000000"",""#FFFF0000"",""#FFFF0000""],
  ""ledRpm"": [
    {
      ""R"": [8150,7000,7115,0,7230,7345,7460,7575,7690,7805,0,7920,8035],
      ""N"": [8150,7000,7115,0,7230,7345,7460,7575,7690,7805,0,7920,8035],
      ""1"": [8150,7000,7115,0,7230,7345,7460,7575,7690,7805,0,7920,8035],
      ""2"": [8150,7000,7115,0,7230,7345,7460,7575,7690,7805,0,7920,8035]
    }
  ]
}";
    }
}
