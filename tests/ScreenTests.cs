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

        private static void AcImageStrips()
        {
            foreach (string car in new[] { "akuro", "bayer" })
            {
                var detector = new StripDetector();
                int[] counts = car == "akuro" ? new[] { 1, 2, 3, 4, 5, 6, 7, 8, 0, 10, 0, 10 }
                                               : new[] { 5, 5, 5, 5, 5, 10, 10, 10, 10, 10, 10, 10 };
                for (int i = 0; i < counts.Length; i++)
                {
                    var frame = LoadFrame("ac-images/" + car + "-transition-" + (i + 1).ToString("0000") + "-current.png");
                    var blobs = detector.Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height));
                    Equal(counts[i], blobs.Count, car + " transition " + (i + 1));
                    if (car == "akuro" && counts[i] == 10)
                    {
                        Check(blobs.Take(4).All(b => b.Color.G > b.Color.R * 2), "Akuro green bank retained");
                        Check(blobs.Skip(8).All(b => b.Color.R > b.Color.G * 2), "Akuro red bank retained");
                    }
                    if (car == "bayer")
                        Check(blobs.Take(5).All(b => b.Width >= 15), "Bayer rectangles must not fragment into edges");
                }
            }
        }

        private static void AcAdonisPhases()
        {
            var session = new CaptureSession("AssettoCorsa", "rss_gtm_adonis_v8_evo");
            foreach (var f in LoadFrames(DataPath("ac-adonis.csv"))) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var screen = session.Screen.Result();
            Check(screen.AlternatingRedline, "Adonis has repeated full-strip colour phases");
            Equal("#FFFF0000", screen.RedlineColor, "First stable red phase is retained, not averaged purple");
            Check(!screen.BlinkSeen && !screen.BlinkIntervalMs.HasValue, "Filtered low-RPM frames are not a redline blink");
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now);
            Equal("#FFFF0000", result.Profile.LedColor[0], "Export actual red phase");
            Equal(0, result.Profile.RedlineBlinkInterval, "Solid fallback, no inferred double-speed blink");
            Check(result.Report.Any(l => l.Contains("cannot reproduce this alternation")), "Representation limit reported");
            Check(result.Report.Any(l => l.Contains("red (rgb(") && l.Contains("blue (rgb(")), "Measured phase colours reported");
            var detector = new StripDetector();
            for (int i = 5; i <= 8; i++)
            {
                var frame = LoadFrame("ac-images/adonis-transition-" + i.ToString("0000") + "-current.png");
                var blobs = detector.Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height));
                Equal(8, blobs.Count, "Adonis phase has eight lit LEDs");
                Check(blobs.All(b => i % 2 == 1 ? b.Color.R > b.Color.B : b.Color.B > b.Color.R), "Image phase is red/blue, not dark");
            }
        }

        private static void AcBayerFirstGear()
        {
            var session = new CaptureSession("AssettoCorsa", "rss_gtm_bayer_i6_evo");
            foreach (var f in LoadFrames(DataPath("ac-bayer-0909.csv"))) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var screen = session.Screen.Result();
            foreach (int slot in new[] { 6, 7, 8, 10, 11 })
            {
                var threshold = screen.Gears.Single(g => g.Gear == "1").Leds[slot];
                Check(threshold != null && threshold.Uncertainty <= 20, "Gear1 cyan has its own narrow crossing");
                Check(Math.Abs(threshold.Rpm - 5780) <= 20, "Gear1 cyan uses the observed crossing with measured display-delay correction");
            }
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now,
                File.ReadAllText(DataPath("ac-bayer-confirmed.overrides.json")));
            foreach (int slot in new[] { 7, 8, 9, 11, 12 })
            {
                Equal("#FF00FFFF", result.Profile.LedColor[slot], "Confirmed cyan is final");
                Check(result.Profile.LedRpm["1"][slot] < result.Profile.LedRpm["1"][0], "Gear1 cyan precedes redline");
                Check(Math.Abs(result.Profile.LedRpm["5"][slot] - 6705) <= 20, "Confirmed gear5 cyan is preserved within measurement tolerance");
            }
            foreach (int slot in new[] { 1, 2, 4, 5, 6 })
                Check(Math.Abs(result.Profile.LedRpm["5"][slot] - 6665) <= 20, "Confirmed gear5 green is preserved within measurement tolerance");
            Equal(0, result.Profile.RedlineBlinkInterval, "User-confirmed solid redline overrides captured artifacts");
            Check(result.Report.Any(l => l.Contains("confirmed local override 0 ms is final")), "Report identifies confirmed blink source");
            var detector = new StripDetector();
            foreach (string phase in new[] { "previous", "current", "following" })
            {
                var frame = LoadFrame("ac-images/bayer-0909-0014-" + phase + ".png");
                Equal(phase == "previous" ? 5 : 10, detector.Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height)).Count,
                    "Bayer captured pixels show cyan onset");
            }
        }

        private static void CompletedCrossingKeepsBounds()
        {
            var capture = new LedWindowCapture(2);
            capture.Record("N", 1000, new[] { false, false });
            capture.Record("N", 1090, new[] { false, false });
            capture.Record("N", 1100, new[] { true, false });
            capture.Record("N", 900, new[] { false, false });
            capture.Record("N", 1200, new[] { false, false });
            var led = capture.Result("N").Leds[0];
            Equal(1090, led.HighestOff.Value, "Completed crossing retains its own dark bound");
            Equal(1100, led.LowestOn, "Completed crossing retains its own lit bound");
            Check(!led.Inconsistent && led.ClimbWidth == 10, "Unfinished climb cannot poison valid confidence");
            capture.Record("N", 1300, new[] { true, false });
            Check(capture.Result("N").Leds[0].ClimbSpread > 150, "Contradictory completed climbs still fail repeatability");
            var contradictory = new LedWindowCapture(2);
            contradictory.Record("1", 1000, new[] { false, false });
            contradictory.Record("1", 1200, new[] { false, false });
            contradictory.Record("1", 1090, new[] { false, false });
            contradictory.Record("1", 1095, new[] { false, false });
            contradictory.Record("1", 1100, new[] { true, false });
            Check(contradictory.Result("1").Leds[0].Inconsistent, "Dark evidence before a crossing remains contradictory");
        }

        private static void LatestAcStages()
        {
            foreach (string car in new[] { "bayer", "adonis" })
            {
                var session = new CaptureSession("AssettoCorsa", "rss_gtm_" + car + (car == "bayer" ? "_i6_evo" : "_v8_evo"));
                foreach (var f in LoadFrames(DataPath(car == "bayer" ? "ac-bayer-1319.csv" : "ac-adonis-1313.csv")))
                    session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
                var screen = session.Screen.Result();
                var result = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now,
                    File.ReadAllText(DataPath("ac-" + car + "-confirmed.overrides.json")));
                if (car == "bayer")
                {
                    foreach (int i in new[] { 1,2,4,5,6 }) Check(Math.Abs(result.Profile.LedRpm["N"][i] - 5215) <= 20, "Neutral green measured crossing");
                    foreach (int i in new[] { 7,8,9,11,12 }) Check(Math.Abs(result.Profile.LedRpm["N"][i] - 5805) <= 20, "Neutral cyan measured crossing");
                    Check(result.Profile.LedRpm["N"].Skip(1).All(v => v < result.Profile.LedRpm["N"][0]), "Neutral stages precede redline");
                    Equal(0, result.Profile.RedlineBlinkInterval, "Confirmed solid Bayer preserved");
                }
                else
                {
                    var first = screen.Gears.Single(g => g.Gear == "5").Leds[0];
                    Check(first != null && first.Uncertainty <= 15 && Math.Abs(first.Rpm - 6450) <= 10, "Adonis first green actual crossing retained");
                    Check(result.Profile.LedRpm["5"][1] >= 6440, "No early borrowed first light");
                    Check(result.Profile.LedRpm["5"][5] >= 6590, "Observed yellow upper bound replaces early borrowed value");
                    Check(result.Report.Any(l => l.Contains("unmeasured upper-bound fallbacks") && l.Contains("may light late")), "Partial evidence stays explicitly uncertain");
                    Equal(140, result.Profile.RedlineBlinkInterval, "Confirmed Adonis approximation preserved");
                }
            }
        }

        private static void UnknownBackgroundNeedsOwnColor()
        {
            var capture = new ScreenLedCapture();
            for (int i = 0; i < 150; i++)
                capture.Record("5", 5500 + i, i * 20, Enumerable.Range(0, 8).Select(s => new LitBlob {
                    Left = 10 + s * 30, Right = 20 + s * 30, Color = new LedColor(185,205,250) }).ToList());
            Check(!capture.Result().ObservedOnUpperBounds.Values.Any(row => row.Any(v => v > 0)), "Pale-only unknown data cannot invent a normal-light bound");
        }

        private static void SixthGearDarkFallbacks()
        {
            foreach (string car in new[] { "bayer", "adonis" })
            {
                string id = car == "bayer" ? "rss_gtm_bayer_i6_evo" : "rss_gtm_adonis_v8_evo";
                string file = car == "bayer" ? "ac-bayer-0944.csv" : "ac-adonis-0949.csv";
                var session = new CaptureSession("AssettoCorsa", id);
                foreach (var f in LoadFrames(DataPath(file))) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
                var screen = session.Screen.Result();
                Equal(car == "bayer" ? 6628 : 6473, screen.DarkLowerBounds["6"], "Sustained raw-dark maximum");
                var normal = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now);
                var confirmed = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now,
                    File.ReadAllText(DataPath("ac-" + car + "-confirmed.overrides.json")));
                var row = confirmed.Profile.LedRpm["6"];
                var active = Enumerable.Range(1, confirmed.Profile.LedNumber).Where(i => !screen.Layout.IsGap[i - 1]).ToList();
                Equal(car == "bayer" ? 6680 : 6525, active.Min(i => row[i]), "Placeholder remains above tested darkness");
                Check(active.All(i => row[i] > screen.DarkLowerBounds["6"]), "No borrowed light appears inside observed dark range");
                Check(active.Select(i => row[i]).Distinct().Count() > 1, "Preserve ATSR last-row group ordering");
                Check(confirmed.Report.Any(l => l.Contains("unmeasured placeholders")), "Do not claim measured onsets");
                foreach (string gear in normal.Profile.GearOrder.Where(g => g != "6"))
                    SeqEqual(normal.Profile.LedRpm[gear], confirmed.Profile.LedRpm[gear], "Confirmed colour/blink override never changes another gear");
                Equal(car == "bayer" ? 0 : 140, confirmed.Profile.RedlineBlinkInterval, "Confirmed blink selection");
                if (car == "adonis")
                {
                    Equal(0, normal.Profile.RedlineBlinkInterval, "Automatic alternating fallback remains solid");
                    Check(confirmed.Report.Any(l => l.Contains("not a measured dark phase")), "Blink approximation distinguished from observation");
                    Check(confirmed.Report.Any(l => l.Contains("Without a confirmed override")), "Automatic fallback report is conditional");
                }
                // A repository/confirmed starting row is not a borrowed default and remains authoritative.
                var starting = normal.Profile.Clone();
                for (int i = 0; i < starting.LedRpm["6"].Length; i++)
                    if (starting.LedRpm["6"][i] > 0) starting.LedRpm["6"][i] -= 1000;
                var baseline = new RepoLookup { Status = RepoLookupStatus.Found, RelativePath = id + ".json", Text = starting.ToJson() };
                var kept = ProfileComposer.Compose(session, new CaptureSettings(), baseline, DateTime.Now);
                SeqEqual(starting.LedRpm["6"].Skip(1), kept.Profile.LedRpm["6"].Skip(1), "Even low starting-file LED thresholds are not inferred fallbacks");
            }
        }

        private static void DarkBoundsRequireRawEvidence()
        {
            foreach (int kind in new[] { 0, 1, 2 })
            {
                var capture = new ScreenLedCapture();
                for (int f = 0; f < 150; f++)
                {
                    int rpm = 800 + f * 10;
                    var blobs = Enumerable.Range(0, 4).Where(i => rpm >= 1000 + i * 200)
                        .Select(i => new LitBlob { Left = 10 + i * 30, Right = 20 + i * 30, Color = new LedColor(20,255,30) }).ToList();
                    capture.Record("1", rpm, f * 20, blobs);
                }
                int frames = kind == 2 ? 60 : 150;
                for (int f = 0; f < frames; f++)
                {
                    var blobs = kind == 1 ? new List<LitBlob> { new LitBlob { Left = 10, Right = 20, Color = new LedColor(40,60,255) } }
                                          : new List<LitBlob>();
                    capture.Record("6", 2000, 3000 + f * 20, blobs);
                }
                Equal(kind == 0, capture.Result().DarkLowerBounds.ContainsKey("6"), "Only sustained truly raw-empty frames establish a bound");
            }
        }

        private static void UnknownSlotsKeepEvidence()
        {
            var capture = new LedWindowCapture(2);
            capture.Record("1", 1000, new[] { false, false });
            capture.Record("1", 1050, new[] { false, false });
            capture.Record("1", 1100, new[] { false, false }, new[] { true, false });
            capture.Record("1", 1150, new[] { false, true }, new[] { true, false });
            capture.Record("1", 1600, new[] { false, true }, new[] { true, false });
            capture.Record("1", 1700, new[] { true, true });
            capture.Record("1", 2000, new[] { false, false }, new[] { true, true });
            var result = capture.Result("1");
            Equal(1125, result.Leds[1].Rpm, "Unaffected light keeps its own crossing");
            Equal(650, result.Leds[0].ClimbWidth, "Unseen interval cannot narrow an uncertain crossing");
            Check(!result.FlashStart.HasValue, "Unknown slots are not a dark redline flash");
        }

        private static void AlternatingPhasesNeedOverlap()
        {
            foreach (bool overlapping in new[] { true, false })
            {
                var capture = new ScreenLedCapture();
                for (int phase = 0; phase < 6; phase++)
                    for (int f = 0; f < 4; f++)
                    {
                        var color = phase % 2 == 0 ? new LedColor(255, 60, 70) : new LedColor(60, 70, 255);
                        int rpm = overlapping ? 7000 : phase % 2 == 0 ? 6950 : 7050;
                        capture.Record("3", rpm, (phase * 4 + f) * 20,
                            Enumerable.Range(0, 4).Select(i => new LitBlob { Left = 10 + i * 30, Right = 20 + i * 30, Color = color }).ToList());
                    }
                Equal(overlapping, capture.Result().AlternatingRedline, "Colours at separate RPM stages must not count as alternation");
            }
        }

        private static void AcMaccaLimiter()
        {
            var session = new CaptureSession("AssettoCorsa", "rss_gtm_macca_72_evo_v8");
            foreach (var f in LoadFrames(DataPath("ac-macca.csv"))) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now);
            var p = result.Profile;
            foreach (var row in p.LedRpm.Values)
                for (int i = 1; i <= p.LedNumber; i++)
                    if (!LedLayout.IsGapColor(p.LedColor[i])) Check(row[i] > 0, "Macca coloured slot has a threshold");
            Equal("#FFFFFF00", p.LedColor[6], "Macca yellow survives limiter filtering");
            Equal("#FFFF0000", p.LedColor[8], "Macca red survives limiter filtering");
            Check(p.LedRpm["1"][1] < p.LedRpm["2"][1] && p.LedRpm["2"][1] < p.LedRpm["3"][1], "Macca gear differences retained");
            Equal(0, p.RedlineBlinkInterval, "Macca first blue stage stays solid");
            Check(result.Report.Any(l => l.Contains("later limiter stage")), "Later blink representation limit reported");
        }

        private static void AcBayerBanks()
        {
            var session = new CaptureSession("AssettoCorsa", "rss_gtm_bayer_i6_evo");
            foreach (var f in LoadFrames(DataPath("ac-bayer.csv"))) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now);
            var p = result.Profile;
            foreach (int i in new[] { 1, 2, 4, 5, 6 }) Equal("#FF00FF00", p.LedColor[i], "Bayer green bank");
            Equal(p.LedRpm["3"][9], p.LedRpm["3"][10], "Bayer missing bank member uses its own gear");
            Check(p.LedRpm["3"][9] > p.LedRpm["2"][9], "Bayer gear difference retained");

            // Isolate bank logic from the old CSV's fragmented blob positions and missing widths.
            var colors = new[] { new LedColor(138,255,182), new LedColor(138,255,182), new LedColor(138,255,182),
                new LedColor(150,255,198), new LedColor(141,243,255), new LedColor(141,243,255) };
            var sr = new ScreenLedResult { Layout = new StripLayout(new double[] { 10,30,50,70,90,110 }, new bool[6], 20),
                MeasuredColors = colors, ColorGroups = LedPalette.Group(colors, new bool[6]) };
            for (int gear = 1; gear <= 3; gear++)
            {
                int onset = 4000 + gear * 500;
                var leds = Enumerable.Range(0, 6).Select(i => new LedThreshold(onset + (i < 4 ? 0 : 300),
                    onset + (i < 4 ? 0 : 300) - 5, onset + (i < 4 ? 0 : 300) + 5)).ToArray();
                if (gear == 3) leds[5] = null;
                sr.Gears.Add(new GearLedResult(gear.ToString(), leds, null, 40));
            }
            var isolated = CarProfile.Parse(@"{""carName"":""bank"",""carId"":""bank"",""ledNumber"":6,
                ""ledColor"":[""#00000000"",""#FF00FF00"",""#FF00FF00"",""#FF00FF00"",""#FF00FF00"",""#FF0000FF"",""#FF0000FF""],
                ""ledRpm"":[{""1"":[0,0,0,0,0,0,0],""2"":[0,0,0,0,0,0,0],""3"":[0,0,0,0,0,0,0]}]}");
            var apply = typeof(ProfileComposer).GetMethod("ApplyScreen", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            apply.Invoke(null, new object[] { sr, new CaptureSettings(), isolated, null, new List<string>(), new List<string>() });
            Equal(5800, isolated.LedRpm["3"][6], "Missing light follows same-gear bank instead of 5050 cross-gear median");
            Check(isolated.LedColor.Skip(1).Take(4).All(c => c == "#FF00FF00"), "Washed green bank stays green");
            Equal("#FF0000FF", isolated.LedColor[5], "Other palette bank stays blue");
        }

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

        private static void LanzoBankGaps()
        {
            foreach (string car in new[] { "lanzo-v10", "lanzo-v10-evo2" })
            {
                var session = new CaptureSession("AssettoCorsa", "rss_gtm_" + car.Replace('-', '_'));
                foreach (var f in LoadFrames(DataPath("ac-" + car + ".csv")))
                    session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
                var screen = session.Screen.Result();
                Equal(12, screen.Layout.LedNumber, car + " includes bank separators");
                Check(screen.Layout.IsGap[2] && screen.Layout.IsGap[9], car + " gaps at3/10");
                var profile = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now).Profile;
                foreach (int slot in new[] { 3, 10 })
                {
                    Check(LedLayout.IsGapColor(profile.LedColor[slot]), "ATSR black/transparent gap color");
                    Check(profile.LedRpm.Values.All(row => row[slot] == 0), "Gap never lights");
                }
            }
            var frame = LoadFrame("ac-images/lanzo-evo2-full.png");
            var calibration = new StripCalibration();
            calibration.Add(new StripDetector().Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height)));
            Equal(12, calibration.Build(out _).LedNumber, "Live Test image sees bank gaps");
        }

        private static void OrdinarySpacingIsNotBankGap()
        {
            foreach (var spacing in new[] { new[] { 30, 31, 32, 33, 34, 35, 36, 37, 38 },
                new[] { 30, 30, 42, 30, 30, 30, 30, 30, 30 },
                new[] { 30, 42, 30, 30, 30, 42, 30, 30, 30 },
                new[] { 118, 80, 82, 82, 80, 118 } })
            {
                var calibration = new StripCalibration();
                int x = 10;
                var blobs = new List<LitBlob> { new LitBlob { Left = x, Right = x + 2 } };
                foreach (int distance in spacing) { x += distance; blobs.Add(new LitBlob { Left = x, Right = x + 2 }); }
                calibration.Add(blobs);
                Equal(0, calibration.Build(out _).GapCount, "Perspective, isolated, or unmatched spacing is not a bank gap");
            }
        }

        private static void PmrVantageGt4FifthGearRedline()
        {
            var session = new CaptureSession("ProjectMotorRacing", "AMR Vantage GT4");
            foreach (var f in LoadFrames(DataPath("pmr-amr-vantage-gt4.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now);
            var p = result.Profile;
            // Fifth gear only ever left the limiter in this drive. Taken on its own that reads about
            // 100 rpm low, and the wheel started flashing before the game did.
            var others = new[] { "1", "2", "3", "4" }.Select(g => p.LedRpm[g][0]).ToList();
            Check(p.LedRpm["5"][0] >= others.Min(), "Fifth gear does not flash before the gears that saw the revs rise");
            Check(Math.Abs(p.LedRpm["5"][0] - 6975) <= 30, "Fifth gear uses the redline the other gears measured");
            Check(result.Report.Any(line => line.Contains("only saw the redline as the revs fell")),
                  "The report explains where fifth gear's redline came from");
            Check(others.Max() - others.Min() <= 80, "The gears that saw the revs rise agree");
        }

        private static void PmrVantageGt4RedlineNeedsFullStrip()
        {
            var session = new CaptureSession("ProjectMotorRacing", "AMR Vantage GT4");
            foreach (var f in LoadFrames(DataPath("pmr-amr-vantage-gt4-second.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var p = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now).Profile;
            // This car's own red and its redline red are close. In fifth the revs climb slowly, and
            // washed-out frames read as colour changes from 6273 rpm, with the last light still dark.
            // Confirmed in game: every gear flashes at about 6980.
            foreach (var gear in p.GearOrder)
                Check(Math.Abs(p.LedRpm[gear][0] - 6980) <= 40, "Gear " + gear + " redline stays at the limiter");
            Check(p.LedRpm["5"][0] >= p.LedRpm["3"][0] - 40, "Fifth gear does not flash before third");
            int lastLight = Enumerable.Range(1, p.LedNumber).Max(i => p.LedRpm["5"][i]);
            Check(p.LedRpm["5"][0] > lastLight, "The redline stays above the last light");
        }

        private static void PmrC7VioletLightsAreFound()
        {
            // Confirmed in game: lights 8, 9 and 10 come on in the same colour, and the last two only
            // at the redline. The strip's backing glows violet at that end, which joined the lights
            // into one run too wide to keep, so a frame showing nine lit read as seven. Their centres
            // blow out to rgb(255,216,255), white in two channels but not the third, which left the
            // white-centre fallback nothing to find either.
            foreach (var sample in new[] { new { Name = "0010", Count = 8 }, new { Name = "0013", Count = 9 },
                                           new { Name = "0014", Count = 10 } })
            {
                var detector = new StripDetector();
                var frame = LoadFrame("pmr-c7-images/c7-" + sample.Name + ".png");
                var blobs = detector.Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height));
                Equal(sample.Count, blobs.Count, "C7 image " + sample.Name + " lit lights");
                if (sample.Name == "0014")
                {
                    // The limiter turns every light red, where the strip's ten are unambiguous.
                    Check(blobs.All(b => b.Color.Hue < 25 || b.Color.Hue > 340), "C7 limiter flash is red");
                    var calibration = new StripCalibration();
                    calibration.Add(blobs);
                    var layout = calibration.Build(out _);
                    Equal(10, layout.LedNumber, "C7 physical lights");
                    Equal(0, layout.GapCount, "C7 strip has no bank gaps");
                    continue;
                }
                Check(blobs[0].Color.Hue > 100 && blobs[0].Color.Hue < 140, "C7 first light is green");
                Check(blobs[7].Color.Hue > 240 && blobs[7].Color.Hue < 300, "C7 light 8 is violet");
            }
        }

        private static void PmrC7RedlineOnlyLightsHaveNoOwnColour()
        {
            var session = new CaptureSession("ProjectMotorRacing", "Corvette C7.R");
            foreach (var f in LoadFrames(DataPath("pmr-corvette-c7-r.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now);
            var p = result.Profile;
            // Confirmed in game: lights 9 and 10 only come on at the redline, so neither has a colour
            // of its own to read. Light 9 was seen coloured in a single neutral frame, in the limiter's
            // purple, and took it; light 10 was never seen at all and correctly fell back.
            Equal(p.LedColor[0], p.LedColor[9], "C7 light 9 falls back to the redline colour");
            Equal(p.LedColor[0], p.LedColor[10], "C7 light 10 falls back to the redline colour");
            Check(result.Report.Any(line => line.Contains("LED 9, 10 were never seen in their own colour")),
                  "The report names both lights as unseen");
            // The lights below them are measured over thousands of frames and keep what they showed.
            foreach (int i in new[] { 1, 2, 3, 4 }) Equal("#FF00FF00", p.LedColor[i], "C7 green slot " + i);
            foreach (int i in new[] { 5, 6, 7 }) Equal("#FFFF0000", p.LedColor[i], "C7 red slot " + i);
        }

        private static void PmrMc12WashedStripIsNotARedline()
        {
            var session = new CaptureSession("ProjectMotorRacing", "MC12 GT1");
            foreach (var f in LoadFrames(DataPath("pmr-mc12-gt1.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now);
            var p = result.Profile;
            // As the last lights came on the whole strip blew out to rgb(227,248,185), its own yellow
            // at a quarter of a real redline's saturation. That read as every light leaving its own
            // colour at once, and turned the wheel yellow where the game changes nothing.
            Equal("#00000000", p.LedColor[0], "MC12 keeps its own colours at the limiter");
            Equal(0, p.RedlineBlinkInterval, "MC12 has no redline blink");
            Check(result.Report.Any(line => line.Contains("washed out into one of its own colours")),
                  "The report explains the rejected colour change");
            Check(result.Report.Any(line => line.Contains("no redline effect")), "MC12 is reported as having no redline effect");
            foreach (int i in new[] { 1, 2, 3 }) Equal("#FF00FF00", p.LedColor[i], "MC12 green slot " + i);
            Check(p.LedRpm["1"][0] > p.LedRpm["1"][6], "The redline sits above the last light");
        }

        private static void PmrAmgGt4RedCentrePair()
        {
            var session = new CaptureSession("ProjectMotorRacing", "GT4");
            foreach (var f in LoadFrames(DataPath("pmr-amg-gt4.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var p = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now).Profile;
            // The centre pair measures rgb(189,53,0) and rgb(182,65,0): two shades of the same red,
            // far enough apart in hue to be told apart and then named red and orange. Confirmed in
            // game as both red. A real orange keeps far more green than this.
            foreach (int i in new[] { 6, 7 }) Equal("#FFFF0000", p.LedColor[i], "AMG GT4 centre slot " + i);
            foreach (int i in new[] { 4, 5, 8, 9 }) Equal("#FFFFFF00", p.LedColor[i], "AMG GT4 yellow slot " + i);
            foreach (int i in new[] { 1, 2, 11, 12 }) Equal("#FF00FF00", p.LedColor[i], "AMG GT4 green slot " + i);
            Equal(12, p.LedNumber, "AMG GT4 mirrored strip");
        }

        private static void PmrR8WashedCentresAreOneLight()
        {
            // Each light is blown out to white through its middle, leaving colour only at its edges,
            // so ten lights were read as nineteen and their pale edges named cyan instead of green.
            foreach (var sample in new[] { new { Name = "0045", Count = 0 }, new { Name = "0050", Count = 2 },
                                           new { Name = "0053", Count = 5 }, new { Name = "0056", Count = 8 },
                                           new { Name = "0046", Count = 10 } })
            {
                var detector = new StripDetector();
                var frame = LoadFrame("pmr-r8-images/r8-" + sample.Name + ".png");
                var blobs = detector.Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height));
                Equal(sample.Count, blobs.Count, "R8 image " + sample.Name + " lights");
                Check(blobs.All(b => b.Width >= 20), "R8 image " + sample.Name + " keeps whole lights, not their edges");
                if (sample.Name == "0046")
                {
                    // The limiter turns the whole strip red, which is where the count is clearest.
                    Check(blobs.All(b => b.Color.Hue < 25 || b.Color.Hue > 340), "R8 limiter flash is red");
                    var calibration = new StripCalibration();
                    calibration.Add(blobs);
                    var layout = calibration.Build(out _);
                    Equal(10, layout.LedNumber, "R8 physical lights");
                    Equal(0, layout.GapCount, "R8 strip has no bank gaps");
                }
                if (sample.Name != "0053") continue;
                Check(blobs[0].Color.Hue > 100 && blobs[0].Color.Hue < 150, "R8 first light is green, not cyan");
                Check(blobs[4].Color.Hue > 40 && blobs[4].Color.Hue < 90, "R8 fifth light has turned towards yellow");
            }
        }

        private static void RrrePorscheCupOrangeBank()
        {
            var session = new CaptureSession("RRRE", "12163,Porsche 911 GT3 Cup (992)");
            foreach (var f in LoadFrames(DataPath("rrre-porsche-911-gt3-cup-992.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var p = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now).Profile;
            Equal(16, p.LedNumber, "Porsche Cup mirrored strip");
            // The middle bank really is orange. Its red channel dominates, like PMR's red shades, so
            // it used to be named red and needed a confirmed override to read correctly on the wheel.
            foreach (int i in new[] { 4, 5, 6, 11, 12, 13 }) Equal("#FFFF8000", p.LedColor[i], "Porsche Cup orange bank slot " + i);
            foreach (int i in new[] { 7, 8, 9, 10 }) Equal("#FFFF0000", p.LedColor[i], "Porsche Cup red centre slot " + i);
            foreach (int i in new[] { 1, 2, 3, 14, 15, 16 }) Equal("#FF00FF00", p.LedColor[i], "Porsche Cup green ends slot " + i);
            Equal("#00000000", p.LedColor[0], "Porsche Cup keeps its own colours at the limiter");
            Check(Math.Abs(p.RedlineBlinkInterval - 51) <= 5, "Porsche Cup measured blink");

            // Confirmed in game: the strip is mirrored, so each pair lights together.
            var row = p.LedRpm["3"];
            Check(Math.Abs(row[0] - 8810) <= 20, "Porsche Cup redline at the limiter");
            for (int i = 1; i <= 8; i++) Equal(row[i], row[17 - i], "Porsche Cup pair " + i + " lights together");
            foreach (var expected in new[] { new[] { 1, 7400 }, new[] { 4, 7885 }, new[] { 8, 8550 } })
                Check(Math.Abs(row[expected[0]] - expected[1]) <= 20, "Porsche Cup LED " + expected[0] + " threshold");
        }

        private static void RrreDmdP21NoLimiterEffect()
        {
            var session = new CaptureSession("RRRE", "1759,DMD P21");
            foreach (var f in LoadFrames(DataPath("rrre-dmd-p21.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var result = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now);
            var p = result.Profile;
            Equal(15, p.LedNumber, "DMD P21 lights");
            foreach (int i in new[] { 1, 2, 3, 4, 5 }) Equal("#FF00FF00", p.LedColor[i], "DMD P21 green start slot " + i);
            foreach (int i in new[] { 6, 7, 8, 9, 10 }) Equal("#FFFF0000", p.LedColor[i], "DMD P21 red middle slot " + i);
            foreach (int i in new[] { 11, 12, 13, 14, 15 }) Equal("#FF0000FF", p.LedColor[i], "DMD P21 blue end slot " + i);
            // This car keeps its strip lit in its own colours at the limiter, so ATSR must not flash.
            Equal("#00000000", p.LedColor[0], "DMD P21 transparent redline");
            Equal(0, p.RedlineBlinkInterval, "DMD P21 has no redline blink");
            Check(result.Report.Any(line => line.Contains("no redline effect")), "DMD P21 report explains the missing effect");

            // Green flickers on single lights are traction control, not the rev count.
            Check(result.Report.Any(line => line.Contains("indicators")), "DMD P21 indicator sightings ignored");
            var row = p.LedRpm["3"];
            Check(Math.Abs(row[0] - 7840) <= 20, "DMD P21 redline at the limiter");
            foreach (var expected in new[] { new[] { 1, 6485 }, new[] { 8, 6975 }, new[] { 15, 7455 } })
                Check(Math.Abs(row[expected[0]] - expected[1]) <= 20, "DMD P21 LED " + expected[0] + " threshold");
        }

        private static void RrreBmwDimRedPair()
        {
            var detector = new StripDetector();
            foreach (var sample in new[] { new { Name = "0010", Count = 8 }, new { Name = "0031", Count = 0 }, new { Name = "0055", Count = 10 } })
            {
                var frame = LoadFrame("rrre-bmw-" + sample.Name + ".png");
                var blobs = detector.Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height));
                Equal(sample.Count, blobs.Count, "BMW image " + sample.Name + " actual lit lights");
                if (sample.Count != 10) continue;
                Check(blobs.Skip(8).All(b => b.Color.Hue < 25 || b.Color.Hue > 340), "Recovered pair retains red color");
                var calibration = new StripCalibration();
                calibration.Add(blobs);
                var layout = calibration.Build(out _);
                Equal(12, layout.LedNumber, "BMW physical slots including bank gaps");
                Check(layout.IsGap[2] && layout.IsGap[9], "BMW gaps at3/10");
            }
            // The saved CSV has already lost the dim pixels: preserve its measured flash,
            // but do not pretend it contains the missing pair's threshold crossings.
            var session = new CaptureSession("RRRE", "11536,BMW M4 GT3");
            foreach (var f in LoadFrames(DataPath("rrre-bmw-m4-gt3.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var profile = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now).Profile;
            Equal("#00000000", profile.LedColor[0], "BMW flash keeps existing colors");
            Check(Math.Abs(profile.RedlineBlinkInterval - 51) <= 5, "BMW measured fast flash unchanged");
        }

        private static void ProtechReflectiveOffPhase()
        {
            var session = new CaptureSession("AssettoCorsa", "rss_gtm_protech_p92_f6");
            foreach (var f in LoadFrames(DataPath("ac-protech-p92-f6.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            var screen = session.Screen.Result();
            Check(!screen.AlternatingRedline, "Reflective OFF housings are not a second lit color phase");
            Check(screen.RedlineMeasured.Saturation > 0.5, "Redline color comes from emissive ON phase");
            var normal = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now);
            Check(Math.Abs(normal.Profile.RedlineBlinkInterval - 122) <= 5, "Physical dark blink is exported");
            Check(!normal.Report.Any(line => line.Contains("full strip alternates")), "No fabricated alternating-color report");
            var confirmed = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now,
                File.ReadAllText(DataPath("ac-protech-confirmed.overrides.json")));
            Equal("#FF0040FF", confirmed.Profile.LedColor[0], "Confirmed bluer redline");
            Equal(normal.Profile.RedlineBlinkInterval, confirmed.Profile.RedlineBlinkInterval, "Color-only override keeps measured blink");
            foreach (var gear in normal.Profile.GearOrder)
                SeqEqual(normal.Profile.LedRpm[gear], confirmed.Profile.LedRpm[gear], "Color override keeps all measured RPM values");
            for (int slot = 1; slot <= 16; slot++)
                Equal(normal.Profile.LedColor[slot], confirmed.Profile.LedColor[slot], "Only redline color changes");
        }

        private static void LuxBluePairStaysSeparate()
        {
            var detector = new StripDetector();
            var calibration = new StripCalibration();
            foreach (var sample in new[] { new { Name = "0006", Count = 6 }, new { Name = "0007", Count = 8 }, new { Name = "0008", Count = 7 } })
            {
                var frame = LoadFrame("ac-images/lux-" + sample.Name + ".png");
                var blobs = detector.Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height));
                Equal(sample.Count, blobs.Count, "Lux image " + sample.Name + " separate physical lights");
                calibration.Add(blobs);
                if (sample.Name == "0006") continue;
                Check(blobs.Any(b => Math.Abs(b.CenterX - 174) < 3) && blobs.Any(b => Math.Abs(b.CenterX - 204) < 3),
                    "Two blue LEDs retain their real centers instead of a merged midpoint");
                Check(blobs.Where(b => b.CenterX > 160 && b.CenterX < 217).All(b => b.Color.Hue > 210 && b.Color.Hue < 260),
                    "Both recovered LEDs remain blue");
            }
            var layout = calibration.Build(out _);
            Equal(8, layout.LedNumber, "Lux has eight physical slots");
            Equal(0, layout.GapCount, "Lux has no phantom gap before red");
        }

        private static void LatestLuxProtechFullCaptures()
        {
            foreach (string car in new[] { "lux-v8", "protech-p92-f6" })
            {
                var session = new CaptureSession("AssettoCorsa", "rss_gtm_" + car.Replace('-', '_'));
                string file = car == "lux-v8" ? "ac-lux-v8-1543.csv" : "ac-protech-p92-f6-1548.csv";
                foreach (var f in LoadFrames(DataPath(file))) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
                var result = ProfileComposer.Compose(session, new CaptureSettings(), null, DateTime.Now,
                    car == "lux-v8" ? null : File.ReadAllText(DataPath("ac-protech-confirmed.overrides.json")));
                var p = result.Profile;
                if (car == "lux-v8")
                {
                    Equal(8, p.LedNumber, "Whole Lux drive rejects merged blue midpoint");
                    var expected = new[] { "#FF00FF00", "#FF00FF00", "#FF00FF00", "#FFFFFF00", "#FFFFFF00", "#FF0000FF", "#FF0000FF", "#FFFF0000" };
                    for (int i = 0; i < expected.Length; i++) Equal(expected[i], p.LedColor[i + 1], "Lux physical color slot " + (i + 1));
                }
                else
                {
                    Equal(16, p.LedNumber, "Protech physical strip");
                    for (int slot = 7; slot <= 10; slot++) Equal("#FFFF0000", p.LedColor[slot], "Protech central red bank");
                    foreach (string gear in new[] { "N", "1", "2", "3", "4", "5" })
                        Check(Math.Abs(p.LedRpm[gear][9] - p.LedRpm[gear][8]) <= 30, "Center pair learns same normal red timing in gear " + gear);
                    Equal("#FF0040FF", p.LedColor[0], "Protech confirmed blue redline unchanged");
                    Check(Math.Abs(p.RedlineBlinkInterval - 123) <= 5, "Latest Protech physical blink retained");
                }
                Check(result.AtsrProblems.Count == 0, "Full capture remains ATSR compatible: " + string.Join("; ", result.AtsrProblems));
            }
        }

        private static void MergedMidpointRequiresExclusivePair()
        {
            foreach (bool separateMiddle in new[] { false, true })
            {
                var calibration = new StripCalibration();
                void Add(int[] xs) => calibration.Add(xs.Select(x => new LitBlob { Left = x, Right = x }).ToList());
                for (int i = 0; i < 30; i++)
                {
                    Add(new[] { 0, 30, 60, 90, 120 });
                    Add(new[] { 0, 45, 90, 120 });
                    Add(new int[0]);
                }
                if (separateMiddle) Add(new[] { 0, 30, 45, 60, 90, 120 });
                var layout = calibration.Build(out _);
                Equal(separateMiddle ? 6 : 5, layout.LedNumber, "An independently observed middle LED must survive");
                Equal(separateMiddle, layout.SlotCenters.Any(x => Math.Abs(x - 45) < 1), "Only mutually exclusive merged midpoint is removed");
            }
        }

        private static void FullChronologicalAcImageSequences()
        {
            foreach (string car in new[] { "lux-1543", "protech-1548" })
            {
                var detector = new StripDetector();
                var calibration = new StripCalibration();
                var files = Directory.GetFiles(DataPath("ac-images/" + car), "*.png")
                    .OrderBy(path => Path.GetFileName(path).Substring(0, 15))
                    .ThenBy(path => path.Contains("previous") ? 0 : path.Contains("current") ? 1 : 2).ToList();
                Equal(192, files.Count, "All saved previous/current/following frames are exercised");
                foreach (string path in files)
                {
                    var frame = LoadFrame(path);
                    calibration.Add(detector.Detect(frame, new PixelRect(0, 0, frame.Width, frame.Height)));
                }
                var layout = calibration.Build(out _);
                Equal(car == "lux-1543" ? 8 : 16, layout.LedNumber, "Full chronological image sequence " + car);
                Equal(0, layout.GapCount, "No phantom physical gaps in " + car);
            }
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
            sr.RedlineByGear["1"] = new LovelyCarDataCapture.Screen.GearRedline { Rpm = 7100, OnsetSeen = true };
            sr.RedlineByGear["2"] = new LovelyCarDataCapture.Screen.GearRedline { Rpm = 7600, OnsetSeen = true };
            var perGear = baseline.Clone();
            apply.Invoke(null, new object[] { sr, new CaptureSettings(), perGear, baseline, new List<string>(), new List<string>() });
            Equal(7100, perGear.LedRpm["1"][0], "measured redline in first gear");
            Equal(7600, perGear.LedRpm["2"][0], "measured redline in second gear");
            Equal(8000, perGear.LedRpm["3"][0], "undriven gear keeps its redline");

            // Leaving the limiter reads lower than entering it, so a gear that only saw the revs fall
            // must not pull its own redline down (PMR's Vantage GT4 flashed 100 rpm early in fifth).
            sr.RedlineByGear["1"].OnsetSeen = false;
            sr.RedlineByGear["1"].Rpm = 7100;
            var fallingOnly = baseline.Clone();
            var fallingNotes = new List<string>();
            apply.Invoke(null, new object[] { sr, new CaptureSettings(), fallingOnly, baseline, fallingNotes, new List<string>() });
            Equal(7600, fallingOnly.LedRpm["1"][0], "falling-only gear takes the redline the other gears measured");
            Equal(7600, fallingOnly.LedRpm["2"][0], "the gear that saw the revs rise keeps its own redline");
            Check(fallingNotes.Any(n => n.Contains("only saw the redline as the revs fell")), "the report explains the shared redline");
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

        private static void ScreenRealAcevoKtmKnownTopGear()
        {
            var session = new CaptureSession("AssettoCorsaEVO", "KTM X Bow GT2");
            foreach (var f in LoadFrames(DataPath("acevo-ktm-x-bow-gt2.frames.csv")))
                session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);

            var result = ProfileComposer.Compose(session, new CaptureSettings { TopGearForNextExport = 6 },
                                                 null, new DateTime(2026, 9, 19));
            var profile = result.Profile;
            SeqEqual(new[] { -1, 0, 1, 2, 3, 4, 5, 6 }, profile.GearOrder.Select(CarProfile.GearRank),
                     "the known six-speed gearbox is exported even though only three gears were driven");
            foreach (var gear in new[] { "4", "5", "6" })
                SeqEqual(profile.LedRpm["3"], profile.LedRpm[gear], "unvisited gear " + gear + " uses captured values");
            Equal(9, profile.LedNumber, "KTM LED count including the centre gap");
            Equal(0, result.AtsrProblems.Count, "six-gear KTM file works in ATSR");
            Check(result.Report.Any(l => l.Contains("Gears through 6 were requested")), "report names the supplied gear count");

            var repeat = ProfileComposer.Compose(session, new CaptureSettings(), null, new DateTime(2026, 9, 19),
                                                 previousExport: profile.ToJson()).Profile;
            foreach (var gear in new[] { "4", "5", "6" })
                SeqEqual(profile.LedRpm[gear], repeat.LedRpm[gear], "later exports retain unvisited gear " + gear);

            var shortFile = CarProfile.Parse(profile.ToJson());
            foreach (var gear in new[] { "4", "5", "6" })
            {
                shortFile.GearOrder.Remove(gear);
                shortFile.LedRpm.Remove(gear);
            }
            var fromShortRepo = ProfileComposer.Compose(session, new CaptureSettings { TopGearForNextExport = 6 },
                                                       Found(shortFile.ToJson(), "assettocorsaevo/ktm-x-bow-gt2.json"),
                                                       new DateTime(2026, 9, 19)).Profile;
            foreach (var gear in new[] { "4", "5", "6" })
                Check(fromShortRepo.LedRpm[gear].Skip(1).Any(v => v > 0),
                      "known gear " + gear + " is filled even when the repo omitted it");
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
