using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LovelyCarDataCapture;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Profile;
using LovelyCarDataCapture.Repo;
using LovelyCarDataCapture.Util;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static int _failed, _passed;
        private static bool _showReports;

        private static int Main(string[] args)
        {
            string repoData = null;
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--repo-data") repoData = args[i + 1];
            _showReports = args.Contains("--show-reports");
            if (args.Contains("--overlay")) { ShowOverlay(); return 0; }
            if (args.Contains("--settings")) { ShowSettingsPage(); return 0; }
            if (args.Contains("--replay")) { Replay(args); return 0; }
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--box") { ShowCaptureBox(args[i + 1]); return 0; }
                if (args[i] == "--still") { ShowStillPicker(args[i + 1]); return 0; }
                if (args[i] != "--grab") continue;
                GrabFromScreen(args[i + 1]);
                return 0;
            }

            Run("Slug follows the README examples", SlugExamples);
            Run("CarProfile round-trips the repo layout", ProfileRoundTrip);
            Run("CarProfile keeps extra keys and _schemaVersion first", ProfileExtraKeys);
            Run("LedLayout classifies layouts", LayoutClassify);
            Run("LedLayout generates mirrored rows around gaps", LayoutGenerate);
            Run("F1 capture finds thresholds and the redline flash", F1Thresholds);
            Run("F1 capture reports LEDs never lit", F1Partial);
            Run("Compose F1 into a 15-LED repo file", ComposeF1Existing);
            Run("Compose F1 leaves a 10-LED repo file's RPMs alone", ComposeF1TenLeds);
            Run("Compose F1 new car", ComposeF1New);
            Run("Compose iRacing follows the repo file's gaps and mirroring", ComposeIRacingMirrored);
            Run("Compose iRacing per-gear lights keep undriven gears", ComposeIRacingPerGear);
            Run("Compose without LED data keeps the repo file", ComposeRedlineOnlyExisting);
            Run("Gaps are black LED colors, as in ATSR", GapColors);
            Run("Mirrored rows are symmetric by position, so ATSR sees them as mirrored", MirroredMatchesAtsr);
            Run("ATSR layout rule", AtsrLayoutRule);
            Run("ATSR compatibility checks", AtsrChecks);
            Run("ATSR Developer Mode path", AtsrDevelopmentPath);
            Run("ATSR built-in car behaviour is reported", AtsrSpecialCarNotes);
            Run("Manual marks follow the repo file's gaps", ManualMarksWithGaps);
            Run("Manual marks follow mirrored and grouped LEDs", ManualMarksMirrored);
            Run("Manual marks with the wrong count leave the gear alone", ManualMarksWrongCount);
            Run("Manual marks build a new car", ManualMarksNewCar);
            Run("Manual mark undo", ManualMarkUndo);
            Run("Screen detector finds the lights in real frames", ScreenDetectorOnFrames);
            Run("Screen calibration finds the strip's gaps", ScreenCalibration);
            Run("Screen calibration works from a single frame", ScreenCalibrationFromOneFrame);
            Run("Screen capture matches the AMS2 Audi's repo values", ScreenThresholdsMatchRepoFile);
            Run("Screen colors are grouped and named", ScreenColorsAreGrouped);
            Run("Screen capture reports a two-stage redline", ScreenTwoStageRedline);
            Run("Screen capture drops half-caught gears", ScreenPartialGearsAreNotWrittenDown);
            Run("Screen capture handles a light that comes on at the redline", ScreenLastPairLightsAtTheRedline);
            Run("Screen capture pools gears that agree", ScreenPoolsAgreeingGears);
            Run("Screen capture keeps a blinking strip out of the thresholds", ScreenBlinkingStripIsNotAThreshold);
            Run("A real blinking M8 capture reads correctly", ScreenRealBlinkingM8);
            Run("A real ACC capture reads correctly through the fade", ScreenRealAccFades);
            Run("A real LMU capture reads correctly past a reflection", ScreenRealLmuSc63);
            Run("A real new ACC car comes out as a whole file", ScreenRealNewAccCar);
            Run("A real ACC capture sees past traction control and ABS lights", ScreenRealAccIndicators);
            Run("Screen detector finds white-hot lights by their glow (PMR)", ScreenDetectorWhiteCores);
            Run("A real PMR capture reads through an 80 ms display lag", ScreenRealPmrLag);
            Run("Screen palette naming follows the colors' order", ScreenPaletteNaming);
            Run("Compose a screen capture into the repo file", ComposeScreenIntoRepoFile);
            Run("Compose a screen capture for a new car", ComposeScreenNewCar);
            if (repoData != null) Run("Every repo file round-trips (" + repoData + ")", () => RepoRoundTrip(repoData));
            if (args.Contains("--live-repo")) Run("RepoClient finds cars on GitHub", LiveRepo);

            Console.WriteLine();
            Console.WriteLine(_failed == 0 ? $"All {_passed} tests passed." : $"{_failed} failed, {_passed} passed.");
            return _failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("PASS  " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("FAIL  " + name + Environment.NewLine + "      " + ex.Message.Replace("\n", "\n      "));
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
        }

        private static void SeqEqual(IEnumerable<int> expected, IEnumerable<int> actual, string what)
        {
            var e = expected.ToArray();
            var a = actual.ToArray();
            if (!e.SequenceEqual(a)) throw new Exception($"{what}:\n  expected [{string.Join(",", e)}]\n  got      [{string.Join(",", a)}]");
        }

        // ---------- fixtures (copied from lovely-car-data) ----------
        private const string AcuraGt3 = "{\n  \"carName\": \"Acura NSX GT3 EVO22\",\n  \"carId\": \"acuransxevo22gt3\",\n  \"carClass\": \"GT3\",\n  \"ledNumber\": 12,\n  \"redlineBlinkInterval\": 250,\n  \"ledColor\": [\"#FFFF0000\",\"#FF00FF00\",\"#FF00FF00\",\"#00000000\",\"#FF00FF00\",\"#FF00FF00\",\"#FFFFFF00\",\"#FFFFFF00\",\"#FFFF8000\",\"#FFFF8000\",\"#00000000\",\"#FFFF0000\",\"#FFFF0000\"],\n  \"ledRpm\": [\n    {\n      \"R\": [7280,6570,6640,0,6710,6780,6850,6920,6990,7060,0,7130,7200],\n      \"N\": [7280,6570,6640,0,6710,6780,6850,6920,6990,7060,0,7130,7200],\n      \"1\": [7375,5375,5575,0,5775,5975,6175,6375,6575,6775,0,6975,7175],\n      \"2\": [7375,5625,5800,0,5975,6150,6325,6500,6675,6850,0,7025,7200],\n      \"3\": [7375,6375,6475,0,6575,6675,6775,6875,6975,7075,0,7175,7275],\n      \"4\": [7375,6545,6628,0,6710,6795,6877,6960,7043,7126,0,7208,7292],\n      \"5\": [7325,6655,6722,0,6788,6855,6923,6990,7056,7126,0,7191,7258],\n      \"6\": [7450,6950,7000,0,7050,7100,7150,7200,7250,7300,0,7350,7400]\n    }\n  ]\n}\n";

        private static string F1File(int leds, string carId = "Ferrari")
        {
            var colors = string.Join(",", Enumerable.Repeat("\"#FF00FF00\"", leds + 1));
            var row = "[" + string.Join(",", new[] { 12000 }.Concat(Enumerable.Range(0, leds).Select(i => 10000 + i * 100))) + "]";
            var gears = string.Join(",\n", new[] { "R", "N", "1", "2", "3", "4", "5", "6", "7", "8" }.Select(g => "      \"" + g + "\": " + row));
            return "{\n  \"carName\": \"Ferrari\",\n  \"carId\": \"" + carId + "\",\n  \"carClass\": \"FER\",\n  \"ledNumber\": " + leds +
                   ",\n  \"redlineBlinkInterval\": 50,\n  \"ledColor\": [" + colors + "],\n  \"ledRpm\": [\n    {\n" + gears + "\n    }\n  ]\n}\n";
        }

        private static RepoLookup Found(string text, string path) => new RepoLookup { Status = RepoLookupStatus.Found, RelativePath = path, Text = text };

        private static ComposeResult Compose(CaptureSession s, RepoLookup lookup)
        {
            var result = ProfileComposer.Compose(s, new CaptureSettings(), lookup, When);
            if (_showReports)
            {
                Console.WriteLine("      ----- report -----");
                foreach (var line in result.Report) Console.WriteLine("      " + line);
                Console.WriteLine("      ----- json -----");
                Console.WriteLine("      " + result.Profile.ToJson().Replace("\n", "\n      "));
            }
            return result;
        }

        private static readonly DateTime When = new DateTime(2026, 9, 17, 12, 0, 0);

        // ---------- tests ----------
        private static void SlugExamples()
        {
            Equal("aix-racing-24", Slug.Make("AIX Racing 24"), "AIX");
            Equal("dams-23", Slug.Make("Dams ‘23"), "Dams");
            Equal("alpine-a110-gt4", Slug.Make("alpine_a110_gt4"), "alpine");
            Equal("algarve-pro-racing-2024", Slug.Make("Algarve Pro Racing 2024"), "Algarve");
            Equal("assettocorsacompetizione", Slug.Make("AssettoCorsaCompetizione"), "sim");
        }

        private static void ProfileRoundTrip()
        {
            Equal(AcuraGt3, CarProfile.Parse(AcuraGt3).ToJson(), "Acura GT3 JSON");
        }

        private static void ProfileExtraKeys()
        {
            var text = "{\n  \"_schemaVersion\": \"v2.0.0\",\n  \"carName\": \"X\",\n  \"carId\": \"x\",\n  \"carClass\": \"GT3\",\n  \"ledNumber\": 1,\n  \"redlineBlinkInterval\": 0,\n  \"ledColor\": [\"#00000000\",\"#FF00FF00\"],\n  \"ledRpm\": [\n    {\n      \"1\": [7000,6000]\n    }\n  ],\n  \"carSettings\": {\n    \"TC\": {\n      \"min\": 0,\n      \"max\": 12\n    }\n  }\n}\n";
            Equal(text, CarProfile.Parse(text).ToJson(), "JSON with extras");
        }

        private static void LayoutClassify()
        {
            Equal(LayoutKind.Rising, LedLayout.Classify(new[] { 9000, 1, 0, 2, 3 }), "rising with gap");
            Equal(LayoutKind.OutsideIn, LedLayout.Classify(new[] { 6400, 5500, 5700, 5900, 5900, 5700, 5500 }), "outside in");
            Equal(LayoutKind.InsideOut, LedLayout.Classify(new[] { 6400, 6000, 5000, 6000 }), "inside out");
            Equal(LayoutKind.Irregular, LedLayout.Classify(new[] { 8600, 7080, 8435, 8425, 8495 }), "irregular");
        }

        private static void LayoutGenerate()
        {
            var gaps = new[] { false, false, false, true, false, false, false };
            SeqEqual(new[] { 7000, 6000, 6500, 0, 7000, 6500, 6000 }, LedLayout.Generate(6, gaps, LayoutKind.OutsideIn, 7000, 6000, 7000), "mirrored with gap (5 active LEDs, 3 stages)");
            SeqEqual(new[] { 8000, 7000, 7500, 8000 }, LedLayout.Generate(3, null, LayoutKind.Rising, 8000, 7000, 8000), "rising");
        }

        // Simulates the F1 game: LED i lights once RPM reaches on[i] and only goes dark again 150 rpm
        // below it (hysteresis); the whole strip flashes above `flash`. Samples are uneven, climbs have
        // small throttle dips, and the car bounces on the limiter.
        private static void SimulateF1(LedWindowCapture capture, string gear, int[] on, int flash, int from, int to, int seed)
        {
            const int hysteresis = 150;
            var rnd = new Random(seed);
            var lit = new bool[on.Length];
            int frame = 0;
            void Sample(int rpm)
            {
                int bits = 0;
                for (int i = 0; i < on.Length; i++)
                {
                    if (rpm >= on[i]) lit[i] = true;
                    else if (rpm < on[i] - hysteresis) lit[i] = false;
                    if (lit[i]) bits |= 1 << i;
                }
                if (rpm >= flash && (frame++ / 3) % 2 == 1) bits = 0;
                capture.Record(gear, rpm, bits);
            }
            for (int pass = 0; pass < 3; pass++)
            {
                for (int rpm = from; rpm <= to; rpm += 30 + rnd.Next(60))
                {
                    Sample(rpm);
                    if (rnd.Next(12) == 0) { rpm -= 60 + rnd.Next(80); Sample(rpm); } // lift: lights stay on through the dip
                }
                for (int k = 0; k < 20; k++) Sample(to - rnd.Next(40)); // bouncing on the limiter
                for (int rpm = to; rpm >= from; rpm -= 80 + rnd.Next(80)) Sample(rpm);
            }
        }

        private static int[] TrueThresholds() => Enumerable.Range(0, 15).Select(i => 10500 + i * 90).ToArray();

        private static void F1Thresholds()
        {
            var cap = new LedWindowCapture(LedWindowCapture.F1LedCount);
            var on = TrueThresholds();
            SimulateF1(cap, "3", on, 11900, 9000, 12100, seed: 1);
            var r = cap.Result("3");
            Check(r.Complete, "all 15 LEDs should be captured");
            for (int i = 0; i < 15; i++)
            {
                var led = r.Leds[i];
                Check(!led.Inconsistent, $"LED {i + 1} marked inconsistent");
                Check(Math.Abs(led.Rpm - on[i]) <= 60, $"LED {i + 1}: expected ~{on[i]}, got {led.Rpm} (window {led.HighestOff}-{led.LowestOn})");
                Check(led.HighestOff < on[i] && led.LowestOn >= on[i], $"LED {i + 1}: window {led.HighestOff}-{led.LowestOn} should contain {on[i]}");
            }
            Check(r.FlashStart.HasValue && Math.Abs(r.FlashStart.Value - 11900) <= 100, "flash start ~11900, got " + r.FlashStart);
        }

        private static void F1Partial()
        {
            var cap = new LedWindowCapture(LedWindowCapture.F1LedCount);
            SimulateF1(cap, "4", TrueThresholds(), 11900, 9000, 11000, seed: 2);
            var r = cap.Result("4");
            Equal(6, r.CapturedCount, "LEDs lit below 11000 rpm (10500..10950)");
            Check(r.FlashStart == null, "no flash expected");
        }

        private static CaptureSession F1Session(params (string gear, int to)[] drives)
        {
            var s = new CaptureSession("F12025", "Ferrari");
            s.RecordCar("Ferrari", "FER");
            int seed = 10;
            foreach (var (gear, to) in drives)
            {
                SimulateF1(s.F1, gear, TrueThresholds(), 11900, 9000, to, seed++);
                s.Redline.Record(gear, to, 0, 0, 12500, 8);
            }
            return s;
        }

        private static void ComposeF1Existing()
        {
            var s = F1Session(("3", 12100), ("4", 11000));
            var baseline = F1File(15);
            var result = Compose(s, Found(baseline, "f12025/ferrari.json"));
            var p = result.Profile;
            var old = CarProfile.Parse(baseline);

            var g3 = p.LedRpm["3"];
            Check(Math.Abs(g3[0] - 11900) <= 100, "gear 3 redline from flash, got " + g3[0]);
            for (int i = 0; i < 15; i++) Check(Math.Abs(g3[i + 1] - TrueThresholds()[i]) <= 60, $"gear 3 LED {i + 1} = {g3[i + 1]}");

            var g4 = p.LedRpm["4"];
            for (int i = 6; i < 15; i++) Equal(old.LedRpm["4"][i + 1], g4[i + 1], $"gear 4 LED {i + 1} kept from repo");
            Check(Math.Abs(g4[1] - 10500) <= 60, "gear 4 LED 1 captured");
            Equal(old.LedRpm["4"][0], g4[0], "gear 4 redline kept (last LED not captured, repo value is higher)");
            SeqEqual(old.LedRpm["7"], p.LedRpm["7"], "undriven gear 7 unchanged");
            Equal(old.LedColor.Count, p.LedColor.Count, "colors kept");
            Equal("FER", p.CarClass, "class kept");

            var report = string.Join("\n", result.Report);
            Check(report.Contains("data/f12025/ferrari.json"), "report names the repo file");
            Check(report.Contains("Gear 4: LED 7, 8, 9, 10, 11, 12, 13, 14, 15 never lit"), "report lists LEDs never lit:\n" + report);
            Check(report.Contains("gear 3: RL 12000->"), "report shows gear 3 changes");
            Check(!report.Contains("(check)"), "no LED flagged inconsistent despite throttle dips:\n" + report);
        }

        private static void ComposeF1TenLeds()
        {
            var s = F1Session(("3", 12100));
            var baseline = F1File(10, "AIX Racing 24");
            var result = Compose(s, Found(baseline, "f12025/aix-racing-24.json"));
            Equal(baseline, result.Profile.ToJson(), "10-LED file unchanged");
            var report = string.Join("\n", result.Report);
            Check(report.Contains("has 10 LEDs but F1 games report 15"), "explains the mismatch");
            Check(report.Contains("LED 15"), "includes the measured table");
        }

        private static void ComposeF1New()
        {
            var s = F1Session(("2", 12100), ("3", 12100));
            var result = Compose(s, new RepoLookup { Status = RepoLookupStatus.NotInRepo, RelativePath = "f12025/ferrari.json" });
            var p = result.Profile;
            Equal(15, p.LedNumber, "15 LEDs");
            Equal(50, p.RedlineBlinkInterval, "F1 blink default");
            SeqEqual(new[] { -1, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, p.GearOrder.Select(CarProfile.GearRank), "gear order R,N,1..8");
            var report = string.Join("\n", result.Report);
            var copied = report.Contains("copied gear 2's values") ? "2" : report.Contains("copied gear 3's values") ? "3" : null;
            Check(copied != null, "report says which gear was copied:\n" + report);
            foreach (var g in new[] { "R", "N", "1", "4", "5", "6", "7", "8" }) SeqEqual(p.LedRpm[copied], p.LedRpm[g], "undriven gear " + g + " copies gear " + copied);
            Check(p.LedRpm["7"].Skip(1).All(v => v > 0), "no zeros left");
        }

        private static void ComposeIRacingMirrored()
        {
            // Mirrored strip with a centre gap, like many GT cars in the repo.
            var baseline = "{\n  \"carName\": \"Car\",\n  \"carId\": \"car\",\n  \"carClass\": \"GT3\",\n  \"ledNumber\": 7,\n  \"redlineBlinkInterval\": 250,\n  \"ledColor\": [\"#FFFF0000\",\"#FF00FF00\",\"#FF00FF00\",\"#FFFFFF00\",\"#00000000\",\"#FFFFFF00\",\"#FF00FF00\",\"#FF00FF00\"],\n  \"ledRpm\": [\n    {\n      \"1\": [7000,5000,5500,6000,0,6000,5500,5000],\n      \"2\": [7000,5000,5500,6000,0,6000,5500,5000]\n    }\n  ]\n}\n";
            var s = new CaptureSession("IRacing", "car");
            var values = new ShiftLightValues { First = 6000, Shift = 7000, Last = 7000, Blink = 7200 };
            s.IRacing.RecordCarWide(values);
            s.IRacing.RecordGear("1", values);
            s.Redline.Record("1", 7000, 0, 0, 7500, 2);
            var p = Compose(s, Found(baseline, "iracing/car.json")).Profile;
            SeqEqual(new[] { 7200, 6000, 6500, 7000, 0, 7000, 6500, 6000 }, p.LedRpm["1"], "gear 1 mirrored around the gap");
            SeqEqual(p.LedRpm["1"], p.LedRpm["2"], "same lights in every gear, so undriven gear 2 uses them too");
        }

        private static void ComposeIRacingPerGear()
        {
            var s = new CaptureSession("IRacing", "acuransxevo22gt3");
            s.IRacing.RecordCarWide(new ShiftLightValues { First = 6000, Shift = 7200, Last = 7200, Blink = 7375 });
            s.IRacing.RecordGear("1", new ShiftLightValues { First = 5400, Shift = 7175, Last = 7175, Blink = 7375 });
            s.IRacing.RecordGear("2", new ShiftLightValues { First = 5600, Shift = 7200, Last = 7200, Blink = 7375 });
            var result = Compose(s, Found(AcuraGt3, "iracing/acuransxevo22gt3.json"));
            var p = result.Profile;
            var old = CarProfile.Parse(AcuraGt3);
            // 10 active LEDs (3 and 10 are gaps), evenly spaced 5400 → 7175; RL = blink.
            SeqEqual(new[] { 7375, 5400, 5597, 0, 5794, 5992, 6189, 6386, 6583, 6781, 0, 6978, 7175 }, p.LedRpm["1"], "gear 1");
            SeqEqual(old.LedRpm["5"], p.LedRpm["5"], "undriven gear 5 kept (lights differ per gear)");
            Check(string.Join("\n", result.Report).Contains("Gear R, N, 3, 4, 5, 6 not driven"), "report names undriven gears");
        }

        private static void ComposeRedlineOnlyExisting()
        {
            var s = new CaptureSession("AssettoCorsaCompetizione", "acuransxevo22gt3");
            s.Redline.Record("3", 7300, 7400, 7400, 7600, 6);
            var result = Compose(s, Found(AcuraGt3, "assettocorsacompetizione/x.json"));
            Equal(AcuraGt3, result.Profile.ToJson(), "file unchanged");
            Check(string.Join("\n", result.Report).Contains("Gear 3: SimHub 7400"), "comparison listed");
        }

        private static void GapColors()
        {
            Check(LedLayout.IsGapColor("#00000000"), "transparent black is a gap");
            Check(LedLayout.IsGapColor("#FF000000"), "opaque black is a gap");
            Check(LedLayout.IsGapColor("#000000"), "6-digit black is a gap");
            Check(!LedLayout.IsGapColor("#FF00FF00"), "green isn't");
            Check(!LedLayout.IsGapColor("#00FF0000"), "transparent red isn't");
            var p = CarProfile.Parse(AcuraGt3);
            var gaps = LedLayout.Gaps(p);
            SeqEqual(new[] { 3, 10 }, Enumerable.Range(1, p.LedNumber).Where(i => gaps[i]), "Acura GT3 gaps");
        }

        private static void MirroredMatchesAtsr()
        {
            // The builder's example strip: 12 LEDs with gaps at 3 and 10.
            var gaps = new bool[13];
            gaps[3] = gaps[10] = true;
            var row = LedLayout.Generate(12, gaps, LayoutKind.OutsideIn, 7200, 5300, 7100);
            SeqEqual(new[] { 7200, 5300, 5750, 0, 6200, 6650, 7100, 7100, 6650, 6200, 0, 5750, 5300 }, row, "mirrored row");
            Equal(LedLayout.AtsrLayout.SideToCenter, LedLayout.AtsrLayoutOf(row.Skip(1).ToList()), "ATSR layout");
        }

        private static void AtsrLayoutRule()
        {
            Equal(LedLayout.AtsrLayout.Rejected, LedLayout.AtsrLayoutOf(new[] { 7700 }), "1 LED (iRacing stock cars)");
            Equal(LedLayout.AtsrLayout.Rejected, LedLayout.AtsrLayoutOf(new[] { 6000, 6000, 6000, 6000 }), "all equal");
            Equal(LedLayout.AtsrLayout.SideToCenter, LedLayout.AtsrLayoutOf(new[] { 5500, 5700, 5900, 6100, 6300, 6300, 6100, 5900, 5700, 5500 }), "ACC mirrored");
            Equal(LedLayout.AtsrLayout.LeftToRight, LedLayout.AtsrLayoutOf(new[] { 5300, 5500, 0, 5700, 5900 }), "rising with gap");
            Equal(LedLayout.AtsrLayout.LeftToRight, LedLayout.AtsrLayoutOf(new[] { 5500, 5700, 5900, 5910, 5700, 5500 }), "nearly mirrored");
        }

        private static void AtsrChecks()
        {
            var misnamed = CarProfile.Parse(F1File(15, "201"));
            var notes = AtsrCompatibility.Check(misnamed, "F12025", Found("", "f12025/221.json"));
            Check(notes.Any(n => n.Contains("looks for data/f12025/201.json") && n.Contains("data/f12025/221.json")), "file name mismatch:\n" + string.Join("\n", notes));

            var alwaysLit = CarProfile.Parse(AcuraGt3);
            alwaysLit.LedRpm["R"][1] = 0;
            notes = AtsrCompatibility.Check(alwaysLit, "IRacing", null);
            Check(notes.Any(n => n.Contains("lights them all the time: gear R (LED 1)")), "always lit:\n" + string.Join("\n", notes));
            Check(!notes.Any(n => n.Contains("LED 3") || n.Contains("LED 10")), "gaps at 3 and 10 aren't reported as always lit");

            var order = CarProfile.Parse(AcuraGt3);
            order.GearOrder.Remove("R");
            order.GearOrder.Add("R");
            notes = AtsrCompatibility.Check(order, "IRacing", null);
            Check(notes.Any(n => n.Contains("by position")), "gear order:\n" + string.Join("\n", notes));

            var stockCar = CarProfile.Parse("{\"carName\":\"Monte Carlo\",\"carId\":\"stockcars-chevymontecarlo03\",\"carClass\":\"NXT\",\"ledNumber\":1,\"redlineBlinkInterval\":0,\"ledColor\":[\"#00000000\",\"#FFFFFF00\"],\"ledRpm\":[{\"R\":[9800,7700],\"N\":[9800,7700],\"1\":[9800,7700]}]}");
            notes = AtsrCompatibility.Check(stockCar, "IRacing", null);
            Check(notes.Any(n => n.Contains("ATSR can't use this file")), "rejected layout:\n" + string.Join("\n", notes));

            Equal(0, AtsrCompatibility.Check(CarProfile.Parse(AcuraGt3), "IRacing", Found(AcuraGt3, "iracing/acuransxevo22gt3.json")).Count, "clean file has no problems");
        }

        private static void AtsrDevelopmentPath()
        {
            Equal(@"C:\SimHub\_ATSR_DevelopmentData\rpm_data\ferrari-296-gt3.json",
                AtsrCompatibility.DevelopmentFilePath(@"C:\SimHub", "Ferrari 296 GT3"), "path");
        }

        private static void AtsrSpecialCarNotes()
        {
            var bmw = AtsrSpecialCars.Describe("BMW M Hybrid V8");
            Check(bmw.Count == 1 && bmw[0].Contains("BMW LMDh light pattern"), "AMS2 BMW M Hybrid V8:\n" + string.Join("\n", bmw));
            var both = AtsrSpecialCars.Describe("bmwlmdh");
            Equal(2, both.Count, "iRacing bmwlmdh has the pattern and a second stage");
            var lmu = AtsrSpecialCars.Describe("GT3_Racing Spirit of Léman 2025");
            Check(lmu.Count == 1 && lmu[0].Contains("LMU Aston Martin GT3"), "LMU group with an accent:\n" + string.Join("\n", lmu));
            Equal(0, AtsrSpecialCars.Describe("acuransxevo22gt3").Count, "ordinary car");

            var p = CarProfile.Parse(F1File(15, "BMW M Hybrid V8"));
            Check(AtsrCompatibility.Check(p, "Automobilista2", null).Any(n => n.Contains("BMW LMDh")), "included in the compatibility check");
        }

        private static CaptureSession MarkSession(string carId, params (string gear, int[] leds, int? redline)[] gears)
        {
            var s = new CaptureSession("Automobilista2", carId);
            foreach (var (gear, leds, redline) in gears)
            {
                foreach (var rpm in leds) s.Marks.MarkLed(gear, rpm);
                if (redline.HasValue) s.Marks.MarkRedline(gear, redline.Value);
                s.Redline.Record(gear, 7000, 0, 0, 7500, 6);
            }
            return s;
        }

        private static void ManualMarksWithGaps()
        {
            // Acura GT3: 12 LEDs, gaps at 3 and 10, so 10 steps left to right.
            var marks = new[] { 6400, 6500, 6600, 6700, 6800, 6900, 7000, 7100, 7200, 7300 };
            var s = MarkSession("acuransxevo22gt3", ("3", marks, 7400));
            var result = Compose(s, Found(AcuraGt3, "automobilista2/acuransxevo22gt3.json"));
            SeqEqual(new[] { 7400, 6400, 6500, 0, 6600, 6700, 6800, 6900, 7000, 7100, 0, 7200, 7300 }, result.Profile.LedRpm["3"], "gear 3");
            SeqEqual(CarProfile.Parse(AcuraGt3).LedRpm["4"], result.Profile.LedRpm["4"], "unmarked gear 4 kept");
            Check(result.Source.StartsWith("manual marks"), "source: " + result.Source);
        }

        private static void ManualMarksMirrored()
        {
            // Mirrored strip, 7 LEDs with a centre gap: 3 steps.
            var mirrored = "{\n  \"carName\": \"Car\",\n  \"carId\": \"car\",\n  \"carClass\": \"GT3\",\n  \"ledNumber\": 7,\n  \"redlineBlinkInterval\": 250,\n  \"ledColor\": [\"#FFFF0000\",\"#FF00FF00\",\"#FF00FF00\",\"#FFFFFF00\",\"#00000000\",\"#FFFFFF00\",\"#FF00FF00\",\"#FF00FF00\"],\n  \"ledRpm\": [\n    {\n      \"1\": [7000,5000,5500,6000,0,6000,5500,5000],\n      \"2\": [7000,5000,5500,6000,0,6000,5500,5000]\n    }\n  ]\n}\n";
            var s = MarkSession("car", ("2", new[] { 6100, 6400, 6800 }, null));
            var p = Compose(s, Found(mirrored, "automobilista2/car.json")).Profile;
            SeqEqual(new[] { 7000, 6100, 6400, 6800, 0, 6800, 6400, 6100 }, p.LedRpm["2"], "gear 2 mirrored, redline kept");
            Equal(LedLayout.AtsrLayout.SideToCenter, LedLayout.AtsrLayoutOf(p.LedRpm["2"].Skip(1).ToList()), "still mirrored for ATSR");
        }

        private static void ManualMarksWrongCount()
        {
            var s = MarkSession("acuransxevo22gt3", ("3", new[] { 6400, 6500, 6600 }, null));
            var result = Compose(s, Found(AcuraGt3, "automobilista2/acuransxevo22gt3.json"));
            SeqEqual(CarProfile.Parse(AcuraGt3).LedRpm["3"], result.Profile.LedRpm["3"], "gear 3 unchanged");
            Check(string.Join("\n", result.Report).Contains("3 LED marks, but the repo file lights up in 10 steps"), "explained:\n" + string.Join("\n", result.Report));
        }

        private static void ManualMarksNewCar()
        {
            var s = MarkSession("newcar", ("2", new[] { 6000, 6200, 6400, 6600, 6800 }, 7000), ("3", new[] { 6100, 6300, 6500, 6700, 6900 }, null));
            var result = Compose(s, new RepoLookup { Status = RepoLookupStatus.NotInRepo, RelativePath = "automobilista2/newcar.json" });
            var p = result.Profile;
            Equal(5, p.LedNumber, "LED count from the marks");
            SeqEqual(new[] { 7000, 6000, 6200, 6400, 6600, 6800 }, p.LedRpm["2"], "gear 2");
            SeqEqual(new[] { 6900, 6100, 6300, 6500, 6700, 6900 }, p.LedRpm["3"], "gear 3, redline from last LED");
            SeqEqual(p.LedRpm["2"], p.LedRpm["5"], "unmarked gear copies the best marked gear");
            Check(result.AtsrProblems.Count == 0, "no ATSR problems:\n" + string.Join("\n", result.AtsrProblems));
        }

        private static void ManualMarkUndo()
        {
            var m = new ManualMarkCapture();
            m.MarkLed("3", 6000);
            m.MarkLed("3", 6200);
            m.MarkRedline("3", 7000);
            Equal("gear 3 redline mark", m.Undo(), "undo redline");
            Check(m.Redline("3") == null, "redline removed");
            Equal("gear 3 LED 2 mark", m.Undo(), "undo LED");
            SeqEqual(new[] { 6000 }, m.LedMarks("3"), "one mark left");
        }

        // Reads the public repo on GitHub; opt-in because it needs the network.
        private static void LiveRepo()
        {
            var client = new RepoClient();
            var acura = client.FindAsync("IRacing", "acuransxevo22gt3", "main").Result;
            Equal(RepoLookupStatus.Found, acura.Status, "acura status (" + acura.Error + ")");
            Equal("iracing/acuransxevo22gt3.json", acura.RelativePath, "acura path");
            Equal("acuransxevo22gt3", CarProfile.Parse(acura.Text).CarId, "acura carId");

            // File name doesn't match the carId: must be found by carId.
            var ferrari = client.FindAsync("F12025", "201", "main").Result;
            Equal(RepoLookupStatus.Found, ferrari.Status, "F1 201 status (" + ferrari.Error + ")");
            Equal("f12025/221.json", ferrari.RelativePath, "F1 201 path");

            var unknown = client.FindAsync("IRacing", "no_such_car_xyz", "main").Result;
            Equal(RepoLookupStatus.NotInRepo, unknown.Status, "unknown status");
            Equal("iracing/no-such-car-xyz.json", unknown.RelativePath, "unknown path");

            var bad = client.FindAsync("IRacing", "acuransxevo22gt3", "no-such-branch-xyz").Result;
            Equal(RepoLookupStatus.Failed, bad.Status, "missing branch fails cleanly");
            Console.WriteLine("      missing branch error: " + bad.Error);
        }

        private static void RepoRoundTrip(string dataDir)
        {
            var files = Directory.GetFiles(dataDir, "*.json", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) != Path.GetFileName(dataDir.TrimEnd('\\', '/')))
                .ToList();
            Check(files.Count > 0, "no car files found under " + dataDir);
            var mismatched = new List<string>();
            var atsr = new Dictionary<string, int>();
            foreach (var f in files)
            {
                var text = File.ReadAllText(f).Replace("\r\n", "\n");
                if (!text.EndsWith("\n")) text += "\n";
                var parsed = CarProfile.Parse(text);
                var rel = f.Substring(dataDir.Length).TrimStart('\\', '/').Replace('\\', '/');
                foreach (var problem in AtsrCompatibility.Check(parsed, Path.GetFileName(Path.GetDirectoryName(f)), Found(text, rel)))
                {
                    var kind = problem.Contains("looks for") ? "file name" : problem.Contains("all the time") ? "always lit" :
                        problem.Contains("can't use") ? "rejected" : problem.Contains("left to right") ? "shown left to right" : problem.Contains("built-in") ? "built-in ATSR behaviour" : "other";
                    atsr[kind] = atsr.TryGetValue(kind, out var n) ? n + 1 : 1;
                }
                // Files whose lists don't match ledNumber can't round-trip unchanged; the builder reports those separately.
                if (parsed.LedColor.Count != parsed.LedNumber + 1 || parsed.LedRpm.Values.Any(r => r.Length != parsed.LedNumber + 1)) continue;
                if (parsed.ToJson() != text) mismatched.Add(f.Substring(dataDir.Length).TrimStart('\\', '/'));
            }
            Check(mismatched.Count <= files.Count / 20, $"{mismatched.Count} of {files.Count} files differ, e.g. {string.Join(", ", mismatched.Take(5))}");
            Console.WriteLine($"      {files.Count} files; {mismatched.Count} differ only in formatting (e.g. {string.Join(", ", mismatched.Take(3))})");
            Console.WriteLine("      ATSR problems: " + string.Join(", ", atsr.OrderBy(k => k.Key).Select(k => k.Key + " " + k.Value)));
        }
    }
}
