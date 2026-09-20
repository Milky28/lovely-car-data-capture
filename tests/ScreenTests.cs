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
        private static void ScreenDetectorWhiteCores()
        {
            // PMR's Viper: lit lights are white-hot with a coloured glow, on a faintly blue carbon rim
            // that passes the ordinary colour test, so that test sees one long smear.
            var detector = new StripDetector();
            var region = new PixelRect(0, 0, 400, 140);
            Equal(0, detector.Detect(LoadFrame("pmr-viper-idle.png"), region).Count, "nothing lit at idle, the rim included");
            var partial = detector.Detect(LoadFrame("pmr-viper-partial.png"), region);
            Equal(2, partial.Count, "the outer pair lights first");
            Check(partial.All(b => b.Color.Hue > 190 && b.Color.Hue < 230), "the outer pair is blue: " + string.Join(", ", partial.Select(b => b.Color)));
            var full = detector.Detect(LoadFrame("pmr-viper-full.png"), region);
            Equal(8, full.Count, "all eight at the limiter");
            string Name(LitBlob b) => b.Color.Hue < 25 || b.Color.Hue > 340 ? "red" : b.Color.Hue < 75 ? "yellow" : b.Color.Hue < 160 ? "green" : "blue";
            Equal("blue green yellow red red yellow green blue", string.Join(" ", full.Select(Name)), "colours from the outside in");
        }

        private static void ScreenRealPmrLag()
        {
            // PMR's Viper, revved in neutral: its lights show about 80 ms behind the revs, four times the
            // other games, and its outer pair turns up at two positions 8 px apart.
            var session = new CaptureSession("ProjectMotorRacing", "SRT Viper GTS-R");
            foreach (var f in LoadFrames(DataPath("pmr-srt-viper-gts-r.frames.csv"))) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var screen = session.Screen.Result();
            Equal(8, screen.Layout.LedNumber, "eight lights, not ten");
            Check(screen.DisplayLagMs >= 60 && screen.DisplayLagMs <= 100, "the lag is about 80 ms, got " + screen.DisplayLagMs);
            var n = screen.Gears.First(g => g.Gear == "N");
            // The repo's file: 5287, 5676, 6080, 6492 from the outside in.
            var file = new[] { 5287, 5676, 6080, 6492, 6492, 6080, 5676, 5287 };
            for (int i = 0; i < 8; i++)
                Check(n.Leds[i] != null && Math.Abs(n.Leds[i].Rpm - file[i]) <= 30, "LED " + (i + 1) + " is about " + file[i] + ", got " + n.Leds[i]?.Rpm);

            // The file makes the outer pair green and sky blue; on screen they're one colour, a blue.
            var lookup = new RepoLookup { Status = RepoLookupStatus.Found, RelativePath = "projectmotorracing/srt-viper-gts-r.json", Text = PmrViperJson() };
            var composed = ProfileComposer.Compose(session, new CaptureSettings(), lookup, new DateTime(2026, 9, 18));
            if (_showReports) Console.WriteLine(string.Join(Environment.NewLine, composed.Report));
            Equal("#FF87CEEB", composed.Profile.LedColor[1], "LED 1 takes the file's sky blue, like LED 8");
            Equal("#FF87CEEB", composed.Profile.LedColor[8], "LED 8 keeps its sky blue");
            Equal("#FF00FF00", composed.Profile.LedColor[2], "LED 2 stays green");
        }

        private static void ScreenRealPmrC8()
        {
            // PMR's C8.R in neutral: the pit limiter flashes the strip green at idle, and at the limiter
            // it sweeps blue in 2, 4, 6, 8 lights with dark phases between.
            var session = new CaptureSession("ProjectMotorRacing", "Corvette C8.R");
            foreach (var f in LoadFrames(DataPath("pmr-corvette-c8-r-neutral.frames.csv"))) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var screen = session.Screen.Result();
            Check(screen.Notes.Any(n => n.Contains("pit limiter")), "the idle flashing was set aside");
            Equal(12, screen.Layout.LedNumber, "12 slots");
            Check(screen.Layout.IsGap[2] && screen.Layout.IsGap[9], "gaps at LED 3 and 10");
            Equal("#FF0000FF", screen.RedlineColor, "the strip turns blue at the redline");
            Check(screen.BlinkSeen && screen.BlinkFromRpm > 7000, "it blinks at the limiter, not at idle: from " + screen.BlinkFromRpm);
            var n = screen.Gears.First(g => g.Gear == "N");
            var slots = new[] { 0, 1, 3, 4, 5, 6, 7, 8, 10, 11 };
            Check(slots.All(i => n.Leds[i] != null && n.Leds[i].ClimbSpread <= 150), "every light pinned down within 150 rpm");
            var values = slots.Select(i => n.Leds[i].Rpm).ToList();
            Check(values.Zip(values.Skip(1), (a, b) => b >= a).All(x => x), "the lights come on left to right: " + string.Join(",", values));
            Check(values[0] > 6000 && values.Last() < 7800, "between 6000 and the redline: " + string.Join(",", values));
        }

        private static void ScreenRealNoRedlineEffect()
        {
            // PMR's Audi R8 (LMP900): five lights, held at the limiter with nothing changing.
            var session = new CaptureSession("ProjectMotorRacing", "R8 (LMP900)");
            foreach (var f in LoadFrames(DataPath("pmr-r8-lmp900.frames.csv"))) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var p = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 18)).Profile;
            Equal("#00000000", p.LedColor[0], "no redline effect: a transparent redline colour");
            Check(p.LedRpm["N"][0] > 7800, "the redline is at the limiter, not the last light: " + p.LedRpm["N"][0]);
            Equal(0, p.RedlineBlinkInterval, "no blink");
            var file = new[] { 6160, 6505, 6840, 7205, 7550 };
            for (int i = 0; i < 5; i++)
                Check(Math.Abs(p.LedRpm["N"][i + 1] - file[i]) <= 20, "LED " + (i + 1) + " is about " + file[i] + ", got " + p.LedRpm["N"][i + 1]);
        }

        private static void ScreenCaptureStopsWhenFull()
        {
            var capture = new ScreenLedCapture();
            var blob = new LitBlob { Left = 10, Right = 20, Color = new LedColor(0, 255, 0) };
            for (int i = 0; i < ScreenLedCapture.MaxSamples; i++) capture.Record("3", 5000, i * 17, new[] { blob });
            Check(capture.IsFull, "full at MaxSamples frames");
            capture.Record("3", 5000, 0, new[] { blob });
            Equal(ScreenLedCapture.MaxSamples, capture.SampleCount, "nothing more is kept once full");
        }

        private static string PmrViperJson() => @"{
  ""carName"": ""SRT Viper GTS-R"",
  ""carId"": ""SRT Viper GTS-R"",
  ""carClass"": ""GTE"",
  ""ledNumber"": 8,
  ""redlineBlinkInterval"": 0,
  ""ledColor"": [""#00000000"",""#FF00FF00"",""#FF00FF00"",""#FFFFFF00"",""#FFFF0000"",""#FFFF0000"",""#FFFFFF00"",""#FF00FF00"",""#FF87CEEB""],
  ""ledRpm"": [
    {
      ""R"": [6600,5287,5676,6080,6492,6492,6080,5676,5287],
      ""N"": [6600,5287,5676,6080,6492,6492,6080,5676,5287],
      ""1"": [6600,5287,5676,6080,6492,6492,6080,5676,5287],
      ""2"": [6600,5287,5676,6080,6492,6492,6080,5676,5287],
      ""3"": [6600,5287,5676,6080,6492,6492,6080,5676,5287],
      ""4"": [6600,5287,5676,6080,6492,6492,6080,5676,5287],
      ""5"": [6600,5287,5676,6080,6492,6492,6080,5676,5287],
      ""6"": [6600,5287,5676,6080,6492,6492,6080,5676,5287],
      ""7"": [6600,5287,5676,6080,6492,6492,6080,5676,5287],
      ""8"": [6600,5287,5676,6080,6492,6492,6080,5676,5287]
    }
  ]
}";

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

        private static void ScreenPoolingPreservesUnmeasuredValues()
        {
            var baseline = CarProfile.Parse(@"{""carName"":""audit"",""carId"":""audit"",""ledNumber"":2,
                ""ledColor"":[""#FFFF0000"",""#FF00FF00"",""#FFFFFF00""],
                ""ledRpm"":[{""1"":[7000,4900,6000],""2"":[7500,4900,6500],""3"":[8000,4900,6700]}]}");
            var sr = new ScreenLedResult
            {
                Layout = new StripLayout(new double[] { 10, 30 }, new bool[2], 20),
                Gears = new List<GearLedResult>
                {
                    new GearLedResult("1", new[] { new LedThreshold(5000, 4990, 5010), null }, null, 20),
                    new GearLedResult("2", new[] { new LedThreshold(5000, 4990, 5010), null }, null, 20),
                },
            };
            // Supply the measured result directly: the second light was visible for calibration,
            // but never seen switching on. This test is about merging, not detecting that light.
            var apply = typeof(ProfileComposer).GetMethod("ApplyScreen", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            foreach (var measuredRedline in new int?[] { null, 7200 })
            {
                sr.RedlineRpm = measuredRedline;
                var p = baseline.Clone();
                var notes = new List<string>();
                apply.Invoke(null, new object[] { sr, new CaptureSettings(), p, baseline, notes, new List<string>() });
                foreach (var gear in p.GearOrder)
                {
                    Equal(5000, p.LedRpm[gear][1], "measured LED pooled in gear " + gear);
                    Equal(baseline.LedRpm[gear][2], p.LedRpm[gear][2], "unmeasured LED preserved in gear " + gear);
                    Equal(measuredRedline ?? baseline.LedRpm[gear][0], p.LedRpm[gear][0], "redline in gear " + gear);
                }
                Check(notes.Any(n => n.Contains("pooled")), "pooling was exercised");
            }
            sr.RedlineByGear["1"] = new LovelyCarDataCapture.Screen.GearRedline { Rpm = 7100 };
            sr.RedlineByGear["2"] = new LovelyCarDataCapture.Screen.GearRedline { Rpm = 7600 };
            var perGear = baseline.Clone();
            apply.Invoke(null, new object[] { sr, new CaptureSettings(), perGear, baseline, new List<string>(), new List<string>() });
            Equal(7100, perGear.LedRpm["1"][0], "measured redline in first gear");
            Equal(7600, perGear.LedRpm["2"][0], "measured redline in second gear");
            Equal(8000, perGear.LedRpm["3"][0], "undriven gear keeps its redline");
        }

        private static void ScreenRestartWaitsForWorker()
        {
            using (var entered = new System.Threading.ManualResetEventSlim())
            using (var release = new System.Threading.ManualResetEventSlim())
            using (var restarted = new System.Threading.ManualResetEventSlim())
            using (var loop = new LovelyCarDataCapture.Plugin.ScreenCaptureLoop())
            {
                int calls = 0;
                loop.Target = () =>
                {
                    if (System.Threading.Interlocked.Increment(ref calls) == 1)
                    {
                        entered.Set();
                        release.Wait();
                    }
                    else restarted.Set();
                    return null; // Exercise the worker without taking a desktop screenshot.
                };
                System.Threading.Tasks.Task restart = null;
                try
                {
                    loop.Start(new PixelRect(0, 0, 20, 10), 30);
                    Check(entered.Wait(3000), "first worker entered its callback");
                    restart = System.Threading.Tasks.Task.Run(() => loop.Start(new PixelRect(0, 0, 20, 10), 30));
                    Check(!restart.Wait(800), "restart must not abandon a worker after 500 ms");
                    Check(!restarted.IsSet, "second worker must not start while the first is blocked");
                    release.Set();
                    Check(restart.Wait(3000), "restart completes when the old writer finishes");
                    Check(restarted.Wait(3000), "new worker started");
                    loop.Stop();
                    Check(!loop.IsRunning, "worker stopped");
                    Equal("off", loop.Status, "stopped status stays off");
                }
                finally
                {
                    release.Set();
                    restart?.Wait(3000);
                    loop.Stop();
                }
            }
        }

        private sealed class ResetTelemetry : GameReaderCommon.StatusDataBase
        {
            public override object GetRawDataObject() => null;
        }

        private static void ScreenDetectorMercedesClusters()
        {
            // The saved capture-box image includes the tachometer graphic below seven four-dot clusters.
            var blobs = new StripDetector().Detect(LoadFrame("ams2-mercedes-clk-lm-lit.png"), new PixelRect(0, 0, 660, 170));
            Equal(7, blobs.Count, "seven clusters, not individual dots or the tachometer");
            var centers = new[] { 52, 170, 250, 332, 413, 494, 612 };
            for (int i = 0; i < centers.Length; i++)
            {
                Check(Math.Abs(blobs[i].CenterX - centers[i]) <= 5, "cluster " + (i + 1) + " position");
                Check(blobs[i].Color.R > blobs[i].Color.G && blobs[i].Color.G > blobs[i].Color.B,
                      "cluster " + (i + 1) + " remains orange");
            }

            // Early in a sweep only one cluster is lit. The tachometer still smears the ordinary pass.
            var pixels = new byte[80 * 25 * 3];
            for (int x = 0; x < 80; x++)
                for (int y = 20; y < 25; y++)
                {
                    int p = (y * 80 + x) * 3;
                    pixels[p] = 180; pixels[p + 1] = 100; pixels[p + 2] = 40;
                }
            for (int x = 10; x < 60; x++)
                for (int y = 3; y < 15; y++)
                {
                    int p = (y * 80 + x) * 3;
                    pixels[p] = 255; pixels[p + 1] = 140; pixels[p + 2] = 45;
                }
            var first = new StripDetector().Detect(PixelFrame.Rgb24(pixels, 80, 25), new PixelRect(0, 0, 80, 25));
            Equal(1, first.Count, "the first cluster is found before the rest light up");
        }

        private static void ScreenRealPanozGearDisplay()
        {
            var session = new CaptureSession("Automobilista2", "Panoz Esperante GTR-1");
            foreach (var f in LoadFrames(DataPath("ams2-panoz-esperante-gtr-1.frames.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var screen = session.Screen.Result();
            Equal(13, screen.Layout.LedNumber, "five lights on each side and three centre gaps");
            Equal(3, screen.Layout.GapCount, "gear display is a gap");
            Check(screen.Notes.Any(n => n.Contains("display between the rev lights")), "report explains the ignored display");
            var profile = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 19)).Profile;
            Equal("#00000000", profile.LedColor[7], "centre gear display is not a rev light");
            Equal(0, profile.LedRpm["2"][7], "centre slot has no RPM threshold");
            Check(Math.Abs(profile.LedRpm["2"][1] - 5500) <= 20, "first rev light remains measured");
            Check(Math.Abs(profile.LedRpm["2"][13] - 5500) <= 20, "mirrored first rev light remains measured");
        }

        private static void ScreenRealCorvetteGearDisplay()
        {
            var session = new CaptureSession("Automobilista2", "Chevrolet Corvette C5-R");
            foreach (var f in LoadFrames(DataPath("ams2-corvette-c5-r.frames.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var screen = session.Screen.Result();
            Equal(14, screen.Layout.LedNumber, "five lights on each side and four center gaps");
            Equal(4, screen.Layout.GapCount, "gear display is excluded");
            Check(screen.Notes.Any(n => n.Contains("display between the rev lights")), "report explains the excluded display");
            var profile = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 19)).Profile;
            for (int led = 6; led <= 9; led++)
            {
                Equal(0, profile.LedRpm["N"][led], "center gap " + led + " has no RPM threshold");
                Equal("#00000000", profile.LedColor[led], "center gap " + led + " has no light color");
            }
            Check(Math.Abs(profile.LedRpm["N"][1] - 5800) <= 20, "first rev light remains measured");
        }

        private static void ScreenRealListerNoStripRedline()
        {
            var session = new CaptureSession("Automobilista2", "Lister Storm GTM");
            foreach (var f in LoadFrames(DataPath("ams2-lister-storm-gtm.frames.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var profile = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 19)).Profile;
            Equal(5, profile.LedNumber, "five rev lights");
            Equal("#00000000", profile.LedColor[0], "no measured strip-wide redline colour keeps the individual LED colours");
            Equal("#FF00FF00", profile.LedColor[1], "outer LED stays green");
            Equal("#FF00FFFF", profile.LedColor[2], "inner LED stays cyan");
            Equal("#FFFF8000", profile.LedColor[3], "center LED is orange");
            Check(Math.Abs(profile.LedRpm["N"][3] - 6090) <= 10, "center light comes on near 6090 rpm");
        }

        private static void ScreenRealPmrStormFlicker()
        {
            var session = new CaptureSession("ProjectMotorRacing", "Storm GT");
            foreach (var f in LoadFrames(DataPath("pmr-storm-gt.frames.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 19));
            var p = result.Profile;
            Equal(5, p.LedNumber, "five rev lights");
            var expected = new[] { 5500, 6090, 6695, 6090, 5500 };
            foreach (var gear in p.GearOrder)
                for (int i = 0; i < expected.Length; i++)
                    Check(Math.Abs(p.LedRpm[gear][i + 1] - expected[i]) <= 20,
                          "gear " + gear + " LED " + (i + 1) + " near " + expected[i]);
            var colors = new[] { "#00000000", "#FF00FF00", "#FFFFFF00", "#FFFF0000", "#FFFFFF00", "#FF00FF00" };
            for (int i = 0; i < colors.Length; i++) Equal(colors[i], p.LedColor[i], "color " + i);
            Equal(0, result.AtsrProblems.Count, "no LED stays lit at idle");
        }

        private static void ScreenRealPmrStormMirroredOutlier()
        {
            var session = new CaptureSession("ProjectMotorRacing", "Storm GT");
            foreach (var f in LoadFrames(DataPath("pmr-storm-gt-second.frames.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var screen = session.Screen.Result();
            Check(screen.DisplayLagMs >= 8 && screen.DisplayLagMs <= 20, "the game draws the lights about 12 ms after telemetry");
            var p = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 19)).Profile;
            Check(Math.Abs(p.LedRpm["4"][2] - p.LedRpm["4"][4]) <= 5,
                  "gear-4 yellow pair stays within 5 rpm despite one false late onset");
            Check(Math.Abs(p.LedRpm["4"][4] - 6090) <= 20, "yellow pair lights near 6090 rpm");
            Equal(p.LedRpm["4"][3], p.LedRpm["4"][0], "fallback redline follows the last real light");
        }

        private static void ScreenRealPmrS7MissingGears()
        {
            var session = new CaptureSession("ProjectMotorRacing", "S7R");
            foreach (var f in LoadFrames(DataPath("pmr-s7r.frames.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 19));
            var p = result.Profile;
            Equal(4, p.LedNumber, "four rev lights");
            Check(Math.Abs(p.LedRpm["3"][2] - 5700) <= 30, "third gear uses the second light measured in other gears");
            Check(Math.Abs(p.LedRpm["4"][4] - 6490) <= 30, "fourth gear uses the last light measured in other gears");
            Equal("#FF00FF00", p.LedColor[1], "first S7 light is green, not cyan");
            Equal(0, result.AtsrProblems.Count, "no LED stays lit at idle");

            var close = new CaptureSession("ProjectMotorRacing", "S7R");
            foreach (var f in LoadFrames(DataPath("pmr-s7r-close-seat.frames.csv")))
                close.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var closer = ProfileComposer.Compose(close, new CaptureSettings(), null, new DateTime(2026, 9, 19));
            Equal("#FF00FF00", closer.Profile.LedColor[1], "first light stays green at the closer camera position");
            Equal(0, closer.AtsrProblems.Count, "closer capture also has no lights stuck on");
        }

        private static void ScreenRealPmrAstonGte()
        {
            var session = new CaptureSession("ProjectMotorRacing", "AMR Vantage GTE");
            foreach (var f in LoadFrames(DataPath("pmr-amr-vantage-gte.frames.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 19));
            var p = result.Profile;
            Equal(8, p.LedNumber, "eight rev lights");
            foreach (var gear in p.GearOrder)
                Check(p.LedRpm[gear][1] >= 5750 && p.LedRpm[gear][1] <= 5830, "gear " + gear + " first light near 5800 rpm");
            var colors = new[] { "#00000000", "#FF00FF00", "#FF00FF00", "#FFFF0000", "#FFFF0000",
                                 "#FF0000FF", "#FF0000FF", "#FF0000FF", "#FF0000FF" };
            for (int i = 0; i < colors.Length; i++) Equal(colors[i], p.LedColor[i], "Aston color " + i);
            Equal(0, result.AtsrProblems.Count, "no LED stays lit at idle");
        }

        private static void ScreenRealPmrBmwGt3Pairs()
        {
            var session = new CaptureSession("ProjectMotorRacing", "M4 GT3 EVO");
            foreach (var f in LoadFrames(DataPath("pmr-m4-gt3-evo.frames.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 19));
            var p = result.Profile;
            Equal(12, p.LedNumber, "12 positions, including two gaps");
            foreach (var gear in p.GearOrder)
            {
                var row = p.LedRpm[gear];
                for (int left = 1; left <= 6; left++) Equal(row[left], row[13 - left], "gear " + gear + " pair " + left);
                Check(Math.Abs(row[2] - 6045) <= 20, "gear " + gear + " second green pair ignores neutral's late reading");
                Check(Math.Abs(row[4] - 6340) <= 20, "gear " + gear + " first yellow pair near 6340 rpm");
                Check(Math.Abs(row[5] - 6685) <= 20, "gear " + gear + " second yellow pair near 6685 rpm");
            }
            Equal(0, result.AtsrProblems.Count, "ATSR recognizes the symmetric layout");

            var repeat = new CaptureSession("ProjectMotorRacing", "M4 GT3 EVO");
            foreach (var f in LoadFrames(DataPath("pmr-m4-gt3-evo-second.frames.csv")))
                repeat.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            Equal(0, repeat.Screen.Result().DisplayLagMs, "a 154 rpm residual rejects the false 246 ms delay");
            var again = ProfileComposer.Compose(repeat, new CaptureSettings(), null, new DateTime(2026, 9, 19));
            var second = again.Profile;
            Equal("#FFFFFF00", second.LedColor[5], "second capture's first yellow light stays yellow");
            Equal("#FFFFFF00", second.LedColor[8], "second capture's paired yellow stays yellow");
            foreach (var gear in second.GearOrder)
            {
                var row = second.LedRpm[gear];
                for (int left = 1; left <= 6; left++) Equal(row[left], row[13 - left], "second capture gear " + gear + " pair " + left);
                Check(Math.Abs(row[4] - 6330) <= 35, "second capture first yellow pair remains near the confirmed RPM");
            }
            Equal(0, again.AtsrProblems.Count, "second capture keeps ATSR compatibility");
        }

        private static void ScreenRealMercedesClusters()
        {
            var session = new CaptureSession("Automobilista2", "Mercedes-Benz CLK LM");
            foreach (var f in LoadFrames(DataPath("ams2-mercedes-clk-lm.frames.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var result = session.Screen.Result();
            Equal(7, result.Layout.LedNumber, "each four-dot cluster is one light");
            Check(result.SteadyAtLimiterRpm.HasValue, "the held limiter has no light effect");
            var profile = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 19)).Profile;
            Equal("#00000000", profile.LedColor[0], "the strip keeps its own color at the limiter");
            Check(Math.Abs(profile.LedRpm["N"][0] - 9240) <= 30, "redline near 9240 rpm");
            var expected = new[] { 8670, 8125, 8230, 8340, 8450, 8560, 8670 };
            for (int i = 0; i < expected.Length; i++)
                Check(Math.Abs(profile.LedRpm["N"][i + 1] - expected[i]) <= 20,
                      "cluster " + (i + 1) + " near " + expected[i] + " rpm");
        }

        private static void SnapshotSavesCaptureBoxPixels()
        {
            var pixels = new byte[4 * 2 * 4];
            // BGRA red at screen coordinate 101, 201, surrounded by black.
            pixels[(4 + 1) * 4 + 2] = 255;
            pixels[(4 + 1) * 4 + 3] = 255;
            var shot = new LovelyCarDataCapture.Plugin.DesktopSnapshot
            {
                Frame = PixelFrame.Bgra32(pixels, 4, 2), Left = 100, Top = 200,
            };
            string path = Path.Combine(Path.GetTempPath(), "capture-box-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                shot.SaveRegion(path, new PixelRect(101, 201, 2, 1));
                using (var image = new Bitmap(path))
                {
                    Equal(2, image.Width, "saved width");
                    Equal(1, image.Height, "saved height");
                    Equal(System.Drawing.Color.FromArgb(255, 255, 0, 0), image.GetPixel(0, 0), "selected pixel");
                }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        private static void ResetStopsCapture()
        {
            var plugin = new CapturePlugin { Settings = new CaptureSettings { ShowOverlay = false } };
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var capturing = typeof(CapturePlugin).GetField("_capturing", flags);
            var session = typeof(CapturePlugin).GetField("_session", flags);
            var screen = (LovelyCarDataCapture.Plugin.ScreenCaptureLoop)typeof(CapturePlugin).GetField("_screen", flags).GetValue(plugin);
            capturing.SetValue(plugin, true);
            session.SetValue(plugin, new CaptureSession("Automobilista2", "audit"));
            try
            {
                screen.Start(new PixelRect(0, 0, 20, 10), 30);
                plugin.ResetCapture();
                Equal(false, (bool)capturing.GetValue(plugin), "reset disarms telemetry capture");
                Check(session.GetValue(plugin) == null, "reset clears the session");
                Check(!screen.IsRunning, "reset stops the screen worker");
                // SimHub alone can set these properties in production; provide a live car frame here.
                var frame = new ResetTelemetry();
                typeof(GameReaderCommon.StatusDataBase).GetProperty("CarId").SetValue(frame, "audit");
                typeof(GameReaderCommon.StatusDataBase).GetProperty("Gear").SetValue(frame, "1");
                typeof(GameReaderCommon.StatusDataBase).GetProperty("Rpms").SetValue(frame, 5000.0);
                var data = new GameReaderCommon.GameData { GameName = "Automobilista2", NewData = frame };
                typeof(GameReaderCommon.GameData).GetProperty("GameRunning").SetValue(data, true);
                plugin.DataUpdate(null, ref data);
                Check(session.GetValue(plugin) == null, "new telemetry does not restart a reset capture");
            }
            finally { screen.Stop(); }
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

        /// <summary>
        /// A real capture of the AMS2 BMW M8 GTE, at 60 fps with the limiter held: a mirrored strip with
        /// gaps that blinks at the limiter, every light turning the deep orange of the middle pair. The
        /// blinks once swamped every threshold, and the colour change went unseen because most of the
        /// strip already had the redline colour, so the report claimed the lights kept their own colours
        /// while blinking. Checked by eye in the car: they all blink in the one colour.
        /// </summary>
        private static void ScreenRealBlinkingM8()
        {
            var capture = new ScreenLedCapture();
            foreach (var f in LoadFrames(DataPath("ams2-bmw-m8-gte.frames.csv"))) capture.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var result = capture.Result();

            // The repo's values, confirmed by this capture: mirrored, with gaps at LED 3 and 10.
            var file = new[] { 6000, 6120, 0, 6240, 6360, 6480, 6480, 6360, 6240, 0, 6120, 6000 };
            Equal(12, result.Layout.LedNumber, "slots on the strip");
            Check(result.Layout.IsGap[2] && result.Layout.IsGap[9], "gaps at LED 3 and 10");
            var gear = result.Gears.First(g => g.Gear == "3");
            for (int i = 0; i < 12; i++)
            {
                if (file[i] == 0) continue;
                Check(gear.Leds[i] != null && Math.Abs(gear.Leds[i].Rpm - file[i]) <= 20,
                      "LED " + (i + 1) + " is about " + file[i] + ", not the limiter: " + (gear.Leds[i]?.Rpm.ToString() ?? "none"));
            }
            Check(result.BlinkSeen && result.BlinkIntervalMs >= 85 && result.BlinkIntervalMs <= 125,
                  "the blink is timed at about 100 ms, got " + result.BlinkIntervalMs);
            Check(result.RedlineRpm.HasValue && Math.Abs(result.RedlineRpm.Value - 6600) <= 30,
                  "the redline is about 6600, got " + result.RedlineRpm);
            Check(!result.RedlineFromBlink, "the colour change was seen, so the redline didn't have to come from the blink");
            Check(!result.Notes.Any(n => n.Contains("keeps its own colours")), "no claim that the lights keep their own colours");
        }

        /// <summary>
        /// A real capture from ACC, the second game: the McLaren 720S GT3 EVO. ACC fades its lights rather
        /// than switching them, which leaves frames part way through every change - two lights, then
        /// seven, then ten - and reads every light a frame or so late. It turns the strip blue at the
        /// redline and blinks it, and its red measures 358 degrees, across the join in the hue circle.
        /// Each of those once broke the capture: every light came out at the limiter, the blinks went
        /// uncounted, and the red was named purple.
        /// </summary>
        private static void ScreenRealAccFades()
        {
            var session = new CaptureSession("AssettoCorsaCompetizione", "mclaren_720s_gt3_evo");
            foreach (var f in LoadFrames(DataPath("acc-mclaren-720s-gt3-evo.frames.csv"))) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var screen = session.Screen.Result();

            Equal(12, screen.Layout.LedNumber, "slots on the strip");
            Check(screen.Layout.IsGap[2] && screen.Layout.IsGap[9], "gaps at LED 3 and 10");
            Check(screen.BlinkSeen && screen.Notes.Any(n => n.Contains("blinks at the limiter")), "the blinks were recognised through the fade");
            Equal("#FF0000FF", screen.RedlineColor, "the strip turns blue at the redline");
            string ColourOf(int led) => screen.ColorGroups.First(g => g.Slots.Contains(led - 1)).Hex;
            Equal("#FFFF0000", ColourOf(11), "a red at 358 degrees is red, not purple");
            Equal("#FF00FF00", ColourOf(1), "LED 1 is green");

            // What lands in the file, against the repo's values - evenly 200 rpm apart, which this bears out.
            var lookup = new RepoLookup { Status = RepoLookupStatus.Found, RelativePath = "assettocorsacompetizione/mclaren-720s-gt3-evo.json", Text = AccMcLarenJson() };
            var composed = ProfileComposer.Compose(session, new CaptureSettings(), lookup, new DateTime(2026, 9, 17));
            if (_showReports) Console.WriteLine(string.Join(Environment.NewLine, composed.Report));
            var row = composed.Profile.LedRpm["3"];
            var file = new[] { 5300, 5500, 0, 5700, 5900, 6100, 6300, 6500, 6700, 0, 6900, 7100 };
            var offsets = new List<int>();
            for (int i = 0; i < 12; i++)
            {
                if (file[i] == 0) { Equal(0, row[i + 1], "LED " + (i + 1) + " stays a gap"); continue; }
                offsets.Add(row[i + 1] - file[i]);
                Check(Math.Abs(row[i + 1] - file[i]) <= 50, "LED " + (i + 1) + " is about " + file[i] + ", not the limiter: " + row[i + 1]);
            }
            Check(Math.Abs(offsets.Average()) <= 20, "no fade lag left over: the values sit " +
                  offsets.Average().ToString("0", CultureInfo.InvariantCulture) + " rpm from the file on average");
            Check(composed.Report.Any(l => l.Contains("ms after its revs")), "the report says why the values were corrected");
        }

        private static void ScreenRealLmuSc63()
        {
            var session = new CaptureSession("LMU", "Lamborghini Iron Lynx 2024");
            foreach (var f in LoadFrames(DataPath("lmu-lamborghini-sc63.frames.csv"))) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var screen = session.Screen.Result();

            Equal(10, screen.Layout.LedNumber, "slots on the strip, the pale reflection between LED 3 and 4 not among them");
            Check(screen.Notes.Any(n => n.Contains("lit even at idle")), "the reflection was recognised and dropped");
            string ColourOf(int led) => screen.ColorGroups.First(g => g.Slots.Contains(led - 1)).Hex;
            Equal("#FF0000FF", ColourOf(1), "LED 1 is blue, although the strip's colours run blue-red-yellow round the hue circle");
            Equal("#FFFFFF00", ColourOf(7), "LED 7 is yellow");
            Equal("#FFFF0000", ColourOf(10), "LED 10 is red");
            Equal("#FFFF0000", screen.RedlineColor, "the strip turns red at the redline");
            Check(!screen.SecondStageRpm.HasValue, "red at 359 and at 0 degrees is one stage, not two");

            // The file's redline moves with the gear: 7775 in 1st, 7875 in 2nd, 7925 from 3rd.
            Check(Math.Abs(screen.RedlineByGear["1"].Rpm - 7775) <= 40, "1st's redline is about 7775, got " + screen.RedlineByGear["1"].Rpm);
            Check(Math.Abs(screen.RedlineByGear["2"].Rpm - 7875) <= 40, "2nd's redline is about 7875, got " + screen.RedlineByGear["2"].Rpm);
            Check(Math.Abs(screen.RedlineByGear["4"].Rpm - 7925) <= 40, "4th's redline is about 7925, got " + screen.RedlineByGear["4"].Rpm);
            Check(screen.FlashOwnMs.HasValue && screen.FlashOwnMs < 150, "the flash between red and the strip's own colours at the limiter was seen");

            var composed = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 17));
            if (_showReports) Console.WriteLine(string.Join(Environment.NewLine, composed.Report));
            var row = composed.Profile.LedRpm["3"];
            Check(Math.Abs(row[6] - row[8]) <= 10 && Math.Abs(row[9] - row[10]) <= 10, "LEDs 6-8 and 9-10 light as groups: " + string.Join(",", row));
            Check(row[9] - row[8] > 150, "the red pair comes well after the yellow three");
            // The file's values, one per group of lights: read in milliseconds of display lag, every gear lands on them.
            var file = new[] { 6130, 6380, 6635, 6900, 7155, 7400, 7400, 7400, 7675, 7675 };
            for (int i = 0; i < 10; i++)
                Check(Math.Abs(row[i + 1] - file[i]) <= 25, "LED " + (i + 1) + " is about " + file[i] + ", got " + row[i + 1]);
            Check(screen.DisplayLagMs > 5 && screen.DisplayLagMs < 60, "LMU draws its lights a frame or so late: " + screen.DisplayLagMs + " ms");
            Check(composed.Profile.LedRpm["1"][0] < composed.Profile.LedRpm["3"][0] - 100, "1st keeps its lower redline");
        }

        private static void ScreenRealNewAccCar()
        {
            var session = new CaptureSession("AssettoCorsaCompetizione", "ginetta_g55_gt4");
            session.RecordCar("Ginetta G55 GT4 2012", "GT4");
            foreach (var f in LoadFrames(DataPath("acc-ginetta-g55-gt4.frames.csv"))) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var composed = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 18));
            if (_showReports) Console.WriteLine(string.Join(Environment.NewLine, composed.Report));
            var p = composed.Profile;

            Equal("Ginetta G55 GT4", p.CarName, "the model year is dropped from a new car's name");
            Equal(8, p.LedNumber, "eight lights");
            // Lit from the outside in, a pair every 200 rpm, the middle pair with the redline.
            var expected = new[] { 6900, 6300, 6500, 6700, 6900, 6900, 6700, 6500, 6300 };
            foreach (var gear in p.GearOrder)
                for (int i = 0; i < expected.Length; i++)
                    Check(Math.Abs(p.LedRpm[gear][i] - expected[i]) <= 15, "gear " + gear + " value " + i + " is about " + expected[i] + ", got " + p.LedRpm[gear][i]);
            Check(p.LedRpm["3"].Skip(1).All(v => v % 5 == 0), "pooled values are whole multiples of 5");
            Equal("#FF00FF00", p.LedColor[1], "the outer pair is green");
            Equal("#FFFFFF00", p.LedColor[3], "the third pair is yellow");
            Equal("#FFFF0000", p.LedColor[0], "the redline is red");
        }

        private static void ScreenRealAccIndicators()
        {
            var session = new CaptureSession("AssettoCorsaCompetizione", "lamborghini_huracan_gt3_evo2");
            foreach (var f in LoadFrames(DataPath("acc-lamborghini-huracan-gt3-evo2.frames.csv"))) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var screen = session.Screen.Result();

            // Traction control lights LEDs 1-2 blue and ABS lights 11-12 yellow, at any RPM.
            Check(screen.Notes.Any(n => n.Contains("LED 1, 2 blue") && n.Contains("LED 11, 12 yellow")), "both indicators were recognised and named");
            Check(!screen.BlinkSeen, "two single dark frames at the limiter aren't a blink");
            Equal("#FF0000FF", screen.RedlineColor, "the strip turns blue at the redline");
            string ColourOf(int led) => screen.ColorGroups.First(g => g.Slots.Contains(led - 1)).Hex;
            Equal("#FFFF0000", ColourOf(12), "LED 12 is red, not the ABS light's yellow");
            Equal("#FF00FF00", ColourOf(1), "LED 1 is green, not the traction control's blue");
            Check(screen.RedlineByGear.Values.All(g => Math.Abs(g.Rpm - 8000) <= 90), "no gear's redline comes from the traction control: " +
                  string.Join(", ", screen.RedlineByGear.Select(g => g.Key + " " + g.Value.Rpm)));

            var composed = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 18));
            if (_showReports) Console.WriteLine(string.Join(Environment.NewLine, composed.Report));
            Equal(0, composed.Profile.RedlineBlinkInterval, "the new file doesn't blink");

            // A second drive, where 4th gear left the limiter with an 800 rpm drop between two frames.
            var again = new CaptureSession("AssettoCorsaCompetizione", "lamborghini_huracan_gt3_evo2");
            foreach (var f in LoadFrames(DataPath("acc-lamborghini-huracan-gt3-evo2-b.frames.csv"))) again.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var second = again.Screen.Result();
            Check(Math.Abs(second.RedlineByGear["4"].Rpm - 8000) <= 30, "4th gear's redline is about 8000, got " + second.RedlineByGear["4"].Rpm);
            Check(!second.FlashOwnMs.HasValue, "revs bouncing on the limiter aren't a flash");
            var againFile = ProfileComposer.Compose(again, new CaptureSettings(), null, new DateTime(2026, 9, 18)).Profile;
            Check(againFile.GearOrder.All(g => Math.Abs(againFile.LedRpm[g][0] - 8000) <= 30), "every gear's redline is about 8000");
            var row = composed.Profile.LedRpm["3"];
            var file = new[] { 5700, 6000, 0, 6300, 6600, 6800, 7000, 7200, 7400, 0, 7600, 7800 };
            for (int i = 0; i < 12; i++)
                Check(Math.Abs(row[i + 1] - file[i]) <= 50, "LED " + (i + 1) + " is about " + file[i] + ", got " + row[i + 1]);
        }

        private static string AccMcLarenJson() => @"{
  ""carName"": ""McLaren 720S GT3 EVO"",
  ""carId"": ""mclaren_720s_gt3_evo"",
  ""carClass"": ""GT3"",
  ""ledNumber"": 12,
  ""redlineBlinkInterval"": 250,
  ""ledColor"": [""#FF0000FF"",""#FF00FF00"",""#FF00FF00"",""#00000000"",""#FF00FF00"",""#FF00FF00"",""#FFFFFF00"",""#FFFFFF00"",""#FFFF8000"",""#FFFF8000"",""#00000000"",""#FFFF0000"",""#FFFF0000""],
  ""ledRpm"": [
    {
      ""R"": [7200,5300,5500,0,5700,5900,6100,6300,6500,6700,0,6900,7100],
      ""N"": [7200,5300,5500,0,5700,5900,6100,6300,6500,6700,0,6900,7100],
      ""1"": [7200,5300,5500,0,5700,5900,6100,6300,6500,6700,0,6900,7100],
      ""2"": [7200,5300,5500,0,5700,5900,6100,6300,6500,6700,0,6900,7100],
      ""3"": [7200,5300,5500,0,5700,5900,6100,6300,6500,6700,0,6900,7100]
    }
  ]
}";

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
                    settings, () => { }, Describe, (save, test) => { }, () => { }, Console.WriteLine,
                    () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SimHub", "LovelyCarDataCapture"),
                    () => capturing = true, () => capturing = false, () => capturing,
                    () => capturing ? "Recording - gear 3 - 7450 rpm - 7 lights lit - 1420 frames" : "Not capturing",
                    () => new List<AtsrCopy>
                    {
                        new AtsrCopy { File = "ginetta-g55-gt4.json", Game = "AssettoCorsaCompetizione", Written = new DateTime(2026, 9, 18, 8, 36, 0) },
                        new AtsrCopy { File = "lamborghini-iron-lynx-2024.json", Game = "LMU", Written = new DateTime(2026, 9, 17, 23, 7, 0) },
                    },
                    copy => null, () => { });
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

        /// <summary>Opens the still picker on its own (--still x,y,w,h starts it on that box), to try it outside SimHub.</summary>
        private static void ShowStillPicker(string spec)
        {
            var parts = spec.Split(',');
            var start = new PixelRect(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
            var thread = new System.Threading.Thread(() =>
            {
                var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnLastWindowClose };
                var shot = LovelyCarDataCapture.Plugin.DesktopSnapshot.Take();
                Console.WriteLine("Still: " + shot.Frame.Width + "x" + shot.Frame.Height + " at " + shot.Left + "," + shot.Top);
                new LovelyCarDataCapture.Plugin.SnapshotPickerWindow(shot, start,
                    region => Console.WriteLine("Kept: " + region.X + "," + region.Y + "," + region.Width + "," + region.Height)).Show();
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
