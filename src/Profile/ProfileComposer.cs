using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Repo;
using LovelyCarDataCapture.Screen;
using LovelyCarDataCapture.Util;

namespace LovelyCarDataCapture.Profile
{
    internal sealed class ComposeResult
    {
        public CarProfile Profile;
        public string Source;
        public List<string> Report = new List<string>();
        public List<string> AtsrProblems = new List<string>();
    }

    /// <summary>
    /// Turns a capture into a car file. When the car already exists in the repo its file is the
    /// starting point, and only values the game actually reported are replaced.
    /// </summary>
    internal static class ProfileComposer
    {
        private const string Green = "#FF00FF00";
        private const string Yellow = "#FFFFFF00";
        private const string Red = "#FFFF0000";
        private const string Blue = "#FF0000FF";

        public static ComposeResult Compose(CaptureSession s, CaptureSettings cfg, RepoLookup lookup, DateTime capturedAt)
        {
            var result = new ComposeResult();
            var notes = new List<string>();
            var details = new List<string>();
            var sim = Slug.Make(s.GameName);

            CarProfile baseline = null;
            if (lookup != null && lookup.Status == RepoLookupStatus.Found)
            {
                try { baseline = CarProfile.Parse(lookup.Text, notes); }
                catch (Exception ex) { notes.Add("The repo file couldn't be read (" + ex.Message + "), so a new file was built instead."); }
            }

            bool f1 = s.F1.HasData, iracing = !f1 && s.IRacing.HasData;
            bool screen = !f1 && !iracing && s.Screen.HasData;
            var screenResult = screen ? s.Screen.Result() : null;
            if (screen && screenResult.Layout == null)
            {
                notes.AddRange(screenResult.Notes);
                screen = false;
            }
            bool manual = !f1 && !iracing && !screen && s.Marks.HasData;
            result.Source = f1 ? "F1 rev lights (game telemetry)"
                : iracing ? "iRacing shift lights (game telemetry)"
                : screen ? "the in-game rev lights, read off the screen"
                : manual ? "manual marks (a button pressed as each in-game light came on)"
                : "SimHub redline only (this game doesn't report its LEDs)";
            if ((f1 || iracing) && s.Marks.HasData)
                notes.Add("Manual marks were ignored because this game reports its LEDs directly.");
            if (screen && s.Marks.HasData)
                notes.Add("Manual marks were ignored because the lights were read off the screen.");

            int newLeds = f1 ? LedWindowCapture.F1LedCount
                : screen ? screenResult.Layout.LedNumber
                : manual && s.Marks.MostLedMarks > 0 ? s.Marks.MostLedMarks : Math.Max(1, cfg.LedNumber);
            var p = baseline?.Clone() ?? NewProfile(s, cfg, newLeds, f1, notes);
            p.Normalize();
            var screenGears = screen ? screenResult.Gears.Select(g => g.Gear) : new string[0];
            foreach (var gear in s.Redline.Gears.Keys.Concat(s.F1.Gears).Concat(s.IRacing.Gears).Concat(screenGears).Concat(manual ? s.Marks.Gears : new string[0]).Distinct().ToList())
            {
                if (baseline != null && !p.GearOrder.Contains(gear)) notes.Add("Gear " + gear + " isn't in the repo file; it was added.");
                p.EnsureGear(gear);
            }

            if (f1) ApplyF1(s, p, baseline, notes, details);
            else if (iracing) ApplyIRacing(s, p, baseline, notes, details);
            else if (screen) ApplyScreen(screenResult, p, baseline, notes, details);
            else if (manual) ApplyManualMarks(s, p, baseline, notes, details);
            else ApplyRedlineOnly(s, cfg, p, baseline, notes, details);

            if (sim == "lmu")
                notes.Add("LMU files in the repo are generated from the templates in src_data/lmu by scripts/build_profiles.py; put these values in the matching template rather than submitting data/lmu directly.");
            if (lookup != null && lookup.SameCarId.Count > 0)
                notes.Add("Other repo files use the same carId: " + string.Join(", ", lookup.SameCarId.Select(x => "data/" + x)) + ".");

            result.Profile = p;
            var r = result.Report;
            r.Add("Lovely Car Data capture report");
            r.Add("");
            r.Add("Game:      " + s.GameName + " (sim folder: " + sim + ")");
            r.Add("Car:       " + s.CarId + (string.IsNullOrEmpty(s.CarModel) ? "" : " (" + s.CarModel + ")"));
            r.Add("Captured:  " + capturedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            r.Add("LED data:  " + result.Source);
            r.Add("Repo file: " + DescribeLookup(lookup));
            if (s.PitLimiterSamples > 0) r.Add("Skipped " + s.PitLimiterSamples + " samples with the pit limiter on.");
            r.Add("");
            r.Add("Changes compared with " + (baseline != null ? "the repo file:" : "an empty file (this is a new car):"));
            r.AddRange(DescribeChanges(baseline, p).Select(x => "  " + x));
            if (notes.Count > 0)
            {
                r.Add("");
                r.Add("Notes:");
                r.AddRange(notes.Select(n => "  - " + n));
            }
            result.AtsrProblems = AtsrCompatibility.Check(p, s.GameName, lookup);
            r.Add("");
            r.Add("ATSR compatibility:");
            r.AddRange(result.AtsrProblems.Count > 0 ? result.AtsrProblems.Select(n => "  - " + n) : new[] { "  - no problems found" });
            if (details.Count > 0)
            {
                r.Add("");
                r.AddRange(details);
            }
            return result;
        }

        // ---------- F1 ----------
        private static void ApplyF1(CaptureSession s, CarProfile p, CarProfile baseline, List<string> notes, List<string> details)
        {
            var results = s.F1.Gears.OrderBy(CarProfile.GearRank).Select(s.F1.Result).ToList();
            bool mappable = p.LedNumber == LedWindowCapture.F1LedCount;
            if (!mappable)
                notes.Add("The repo file has " + p.LedNumber + " LEDs but F1 games report " + LedWindowCapture.F1LedCount +
                          " rev lights, so its RPMs were left unchanged. The measured thresholds are listed below to map by hand.");

            details.Add("Measured rev-light thresholds (rpm). The window is the range the true value lies in: highest RPM seen dark - lowest seen lit.");
            foreach (var gr in results)
            {
                details.Add("");
                details.Add("Gear " + gr.Gear + ": " + gr.CapturedCount + "/" + gr.Leds.Length + " LEDs lit, " + gr.Samples + " rising samples" +
                            (gr.FlashStart.HasValue ? ", redline flash from " + gr.FlashStart + " rpm" : ", no redline flash seen"));
                for (int i = 0; i < gr.Leds.Length; i++)
                    details.Add(LedLine(i, gr.Leds[i]));
            }
            if (!mappable) return;

            foreach (var gr in results)
            {
                var row = p.LedRpm[gr.Gear];
                for (int i = 0; i < gr.Leds.Length; i++)
                {
                    if (gr.Leds[i] != null) row[i + 1] = gr.Leds[i].Rpm;
                }
                int lastLit = gr.Leds.Where(l => l != null).Select(l => l.Rpm).DefaultIfEmpty(0).Max();
                if (gr.FlashStart.HasValue) row[0] = gr.FlashStart.Value;
                else if (row[0] < lastLit)
                {
                    row[0] = lastLit;
                    notes.Add("Gear " + gr.Gear + ": no redline flash seen, so the redline was set to the last LED's RPM. Hold the limiter briefly to capture it.");
                }

                if (!gr.Complete)
                {
                    var missing = Enumerable.Range(0, gr.Leds.Length).Where(i => gr.Leds[i] == null).Select(i => (i + 1).ToString(CultureInfo.InvariantCulture));
                    notes.Add("Gear " + gr.Gear + ": LED " + string.Join(", ", missing) + " never lit; " +
                              (baseline != null ? "kept the repo values." : "left at 0.") + " Rev higher in this gear to capture them.");
                }
                var inconsistent = gr.Leds.Select((l, i) => new { l, i }).Where(x => x.l != null && x.l.Inconsistent).Select(x => x.i + 1).ToList();
                if (inconsistent.Count > 0)
                    notes.Add("Gear " + gr.Gear + ": LED " + string.Join(", ", inconsistent) + " switched off above an RPM where it was lit. Values may be off; see the table.");
            }

            FillUndrivenGears(p, baseline, results.Where(r => r.CapturedCount > 0).OrderByDescending(r => r.CapturedCount).ThenByDescending(r => r.Samples).Select(r => r.Gear).FirstOrDefault(),
                s.F1.Gears.ToList(), notes, "F1 cars normally use the same lights in every gear.");
        }

        // ---------- rev lights read off the screen ----------
        private static void ApplyScreen(ScreenLedResult sr, CarProfile p, CarProfile baseline, List<string> notes, List<string> details)
        {
            var layout = sr.Layout;
            var results = sr.Gears.OrderBy(g => CarProfile.GearRank(g.Gear)).ToList();
            bool mappable = p.LedNumber == layout.LedNumber;

            details.Add("Rev lights read from the screen (rpm). Each value is the middle of the range it was seen " +
                        "switching on in; several climbs that agree closely mean a reliable value. Slots marked \"gap\" " +
                        "have room on the strip but never light.");
            details.Add("Strip: " + layout.LedNumber + " slots, " + (layout.LedNumber - layout.GapCount) + " lights, " +
                        layout.GapCount + " gap(s), spacing " + layout.Pitch.ToString("0.0", CultureInfo.InvariantCulture) + " px.");
            foreach (var gr in results)
            {
                details.Add("");
                details.Add("Gear " + gr.Gear + ": " + gr.CapturedCount + "/" + (layout.LedNumber - layout.GapCount) +
                            " lights seen, " + gr.Samples + " rising frames");
                for (int i = 0; i < gr.Leds.Length; i++)
                {
                    if (layout.IsGap[i]) { details.Add(string.Format(CultureInfo.InvariantCulture, "  LED {0,2}  {1,6}  gap", i + 1, "-")); continue; }
                    details.Add(LedLine(i, gr.Leds[i]));
                }
            }

            details.Add("");
            details.Add("Colors measured on screen (a game washes colors out, so these are matched by their order, not their exact hue):");
            foreach (var g in sr.ColorGroups)
                details.Add("  LED " + string.Join(", ", g.Slots.Select(i => (i + 1).ToString(CultureInfo.InvariantCulture))) +
                            ": " + g.Measured + " -> " + g.Name + " " + g.Hex);
            if (sr.RedlineRpm.HasValue)
                details.Add("  Redline color " + sr.RedlineMeasured + " -> " + (sr.RedlineColor ?? "?") + " from " + sr.RedlineRpm + " rpm" +
                            (sr.RedlineHighestBelow.HasValue && sr.RedlineLowestAbove.HasValue
                                ? " (window " + sr.RedlineHighestBelow + "-" + sr.RedlineLowestAbove + ")" : ""));
            notes.AddRange(sr.Notes);

            if (!mappable)
            {
                notes.Add("The repo file has " + p.LedNumber + " LEDs but " + layout.LedNumber +
                          " slots were seen on screen, so its RPMs were left unchanged. The measured values are listed below; " +
                          "check the capture region covers the whole strip and nothing else.");
                return;
            }

            foreach (var gr in results)
            {
                var row = p.LedRpm[gr.Gear];
                for (int i = 0; i < gr.Leds.Length; i++)
                {
                    if (layout.IsGap[i]) row[i + 1] = 0;
                    else if (gr.Leds[i] != null) row[i + 1] = gr.Leds[i].Rpm;
                }
                int lastLit = gr.Leds.Where(l => l != null).Select(l => l.Rpm).DefaultIfEmpty(0).Max();
                if (sr.RedlineRpm.HasValue) row[0] = sr.RedlineRpm.Value;
                else if (row[0] < lastLit)
                {
                    row[0] = lastLit;
                    notes.Add("Gear " + gr.Gear + ": no redline color change was seen, so the redline was set to the last light's RPM. Hold the limiter briefly to capture it.");
                }

                if (!gr.Complete)
                {
                    var missing = Enumerable.Range(0, gr.Leds.Length).Where(i => !layout.IsGap[i] && gr.Leds[i] == null)
                                            .Select(i => (i + 1).ToString(CultureInfo.InvariantCulture)).ToList();
                    if (missing.Count > 0)
                        notes.Add("Gear " + gr.Gear + ": LED " + string.Join(", ", missing) + " never lit; " +
                                  (baseline != null ? "kept the repo values." : "left at 0.") + " Rev higher in this gear to capture them.");
                }
            }

            ApplyScreenColors(sr, p, baseline, notes);

            if (sr.BlinkIntervalMs.HasValue)
            {
                if (baseline == null || p.RedlineBlinkInterval == 0)
                {
                    p.RedlineBlinkInterval = sr.BlinkIntervalMs.Value;
                    notes.Add("The strip blinked above the redline with a dark phase of about " + sr.BlinkIntervalMs +
                              " ms, so redlineBlinkInterval was set to that. Check it looks right on the wheel.");
                }
                else if (Math.Abs(p.RedlineBlinkInterval - sr.BlinkIntervalMs.Value) > 25)
                    notes.Add("The blink measured about " + sr.BlinkIntervalMs + " ms but the repo file says " +
                              p.RedlineBlinkInterval + "; it was left alone.");
            }
            else if (sr.RedlineRpm.HasValue && baseline != null && p.RedlineBlinkInterval > 0)
                notes.Add("The repo file blinks at the redline (redlineBlinkInterval " + p.RedlineBlinkInterval +
                          ") but the lights stayed on above it in the capture. That may be ATSR's own effect rather than the game's.");

            FillUndrivenGears(p, baseline, results.Where(r => r.CapturedCount > 0).OrderByDescending(r => r.CapturedCount).ThenByDescending(r => r.Samples).Select(r => r.Gear).FirstOrDefault(),
                results.Select(r => r.Gear).ToList(), notes, "Many cars use the same lights in every gear; capture the others if they differ.");
        }

        private static void ApplyScreenColors(ScreenLedResult sr, CarProfile p, CarProfile baseline, List<string> notes)
        {
            var layout = sr.Layout;
            var suggested = new string[layout.LedNumber + 1];
            suggested[0] = sr.RedlineColor ?? Red;
            for (int i = 0; i < layout.LedNumber; i++)
            {
                var group = sr.ColorGroups.FirstOrDefault(g => g.Slots.Contains(i));
                suggested[i + 1] = layout.IsGap[i] ? "#00000000" : group?.Hex ?? "#00000000";
            }

            if (baseline == null)
            {
                p.LedColor = suggested.ToList();
                notes.Add("LED colors come from the screen" + (sr.ColorsDoubtful ? ", and some were hard to tell apart" : "") + "; check them in the RPM LED Builder.");
                return;
            }

            // The repo file's colors are left alone: a game's own rendering can't settle a color the
            // file already states. Only a different grouping is worth reporting.
            var mismatched = new List<string>();
            for (int i = 0; i < layout.LedNumber && i + 1 < p.LedColor.Count; i++)
            {
                bool fileGap = LedLayout.IsGapColor(p.LedColor[i + 1]);
                if (fileGap != layout.IsGap[i])
                    mismatched.Add("LED " + (i + 1) + " is " + (fileGap ? "a gap in the file but lit on screen" : "lit in the file but never lit on screen"));
            }
            if (mismatched.Count > 0)
                notes.Add("Gaps differ from the repo file: " + string.Join("; ", mismatched) + ".");

            var different = new List<string>();
            for (int i = 0; i < layout.LedNumber && i + 1 < p.LedColor.Count; i++)
            {
                if (layout.IsGap[i] || LedLayout.IsGapColor(p.LedColor[i + 1])) continue;
                if (!SameRgb(p.LedColor[i + 1], suggested[i + 1])) different.Add("LED " + (i + 1) + " " + p.LedColor[i + 1] + " vs " + suggested[i + 1]);
            }
            if (different.Count > 0)
                notes.Add("Colors on screen suggest " + string.Join(", ", different) + ". The repo file's colors were kept" +
                          (sr.ColorsDoubtful ? ", and the measured colors were close together anyway." : "; change them by hand if the game disagrees."));
        }

        /// <summary>Compares two #AARRGGBB or #RRGGBB colors by their RGB part only.</summary>
        private static bool SameRgb(string a, string b)
        {
            string Rgb(string c)
            {
                c = (c ?? "").TrimStart('#');
                return c.Length >= 8 ? c.Substring(2, 6).ToUpperInvariant() : c.ToUpperInvariant();
            }
            return Rgb(a) == Rgb(b);
        }

        // ---------- manual marks ----------
        private static void ApplyManualMarks(CaptureSession s, CarProfile p, CarProfile baseline, List<string> notes, List<string> details)
        {
            var marks = s.Marks;
            var gaps = LedLayout.Gaps(p);
            var marked = marks.Gears.OrderBy(CarProfile.GearRank).ToList();

            details.Add("Manual marks (rpm, in the order the lights came on):");
            foreach (var gear in marked)
            {
                var leds = marks.LedMarks(gear).ToList();
                var redline = marks.Redline(gear);
                details.Add("  Gear " + gear + ": " + (leds.Count > 0 ? string.Join(", ", leds) : "no LED marks") + (redline.HasValue ? "; redline " + redline : ""));

                var sorted = leds.OrderBy(v => v).ToList();
                if (!sorted.SequenceEqual(leds))
                    notes.Add("Gear " + gear + ": the LED marks weren't in increasing order, so they were sorted. Check for a double press.");

                var row = p.LedRpm[gear];
                if (sorted.Count > 0)
                {
                    // Which LEDs light together, lowest RPM first, taken from the repo file so its layout,
                    // gaps and mirroring are kept. A new file is simply left to right.
                    var stages = Stages(TemplateRow(p, baseline, gear), gaps, p.LedNumber);
                    if (stages.Count == sorted.Count)
                    {
                        for (int k = 0; k < stages.Count; k++)
                            foreach (var led in stages[k]) row[led] = sorted[k];
                    }
                    else if (baseline != null)
                    {
                        notes.Add("Gear " + gear + ": " + sorted.Count + " LED marks, but the repo file lights up in " + stages.Count +
                                  " steps, so this gear was left unchanged. Mark every step, lowest RPM first.");
                    }
                    else
                    {
                        for (int k = 0; k < Math.Min(stages.Count, sorted.Count); k++)
                            foreach (var led in stages[k]) row[led] = sorted[k];
                        notes.Add("Gear " + gear + ": " + sorted.Count + " LED marks for " + stages.Count + " LEDs. Check the values in the RPM LED Builder.");
                    }
                }

                if (redline.HasValue) row[0] = redline.Value;
                else if (baseline == null && sorted.Count > 0)
                {
                    row[0] = sorted[sorted.Count - 1];
                    notes.Add("Gear " + gear + ": no redline mark, so the redline was set to the last LED's RPM.");
                }
            }

            notes.Add("Manual marks include your reaction time, so values can be slightly high. Revving very slowly keeps that small.");
            if (baseline == null)
                notes.Add("New car: the LEDs were laid out left to right. If the real strip is mirrored or has gaps, fix the layout in the RPM LED Builder.");

            var best = marked.Where(g => marks.LedMarks(g).Count > 0).OrderByDescending(g => marks.LedMarks(g).Count).FirstOrDefault();
            FillUndrivenGears(p, baseline, best, marked, notes, "Many cars use the same lights in every gear; mark the others if they differ.");
        }

        private static int[] TemplateRow(CarProfile p, CarProfile baseline, string gear)
        {
            if (baseline == null) return null;
            if (baseline.LedRpm.TryGetValue(gear, out var own) && own.Skip(1).Any(v => v > 0)) return own;
            return baseline.GearOrder.Select(g => baseline.LedRpm[g]).FirstOrDefault(r => r.Skip(1).Any(v => v > 0));
        }

        /// <summary>Groups of LED indexes that light at the same RPM, lowest first. Without a template, one LED per step.</summary>
        private static List<List<int>> Stages(int[] template, bool[] gaps, int ledNumber)
        {
            var active = Enumerable.Range(1, ledNumber).Where(i => !(i < gaps.Length && gaps[i])).ToList();
            if (template == null) return active.Select(i => new List<int> { i }).ToList();
            return active.Where(i => i < template.Length && template[i] > 0)
                .GroupBy(i => template[i]).OrderBy(g => g.Key)
                .Select(g => g.ToList()).ToList();
        }

        // ---------- iRacing ----------
        private static void ApplyIRacing(CaptureSession s, CarProfile p, CarProfile baseline, List<string> notes, List<string> details)
        {
            var ir = s.IRacing;
            var gaps = LedLayout.Gaps(p);
            var layouts = p.GearOrder.Select(g => LedLayout.Classify(p.LedRpm[g])).Where(l => l != LayoutKind.Irregular && l != LayoutKind.Trivial).ToList();
            var commonLayout = layouts.Count > 0 ? layouts.GroupBy(l => l).OrderByDescending(g => g.Count()).First().Key : LayoutKind.Rising;

            // Cars whose lights don't change per gear: the car-wide values hold for every gear.
            bool carWideForAll = ir.CarWide != null && ir.SameInAllGears &&
                                 ir.Gears.All(g => Same(ir.ForGear(g), ir.CarWide));

            details.Add("iRacing shift-light RPMs (first LED / shift / last LED / blink):");
            if (ir.CarWide != null) details.Add("  Car-wide (session info): " + Describe(ir.CarWide));

            var untouched = new List<string>();
            foreach (var gear in p.GearOrder)
            {
                var v = ir.ForGear(gear);
                string from = "telemetry in gear";
                if (v == null && (carWideForAll || baseline == null) && ir.CarWide != null) { v = ir.CarWide; from = "car-wide values"; }
                if (v == null) { untouched.Add(gear); continue; }

                var existing = p.LedRpm[gear];
                var layout = LedLayout.Classify(existing);
                if (layout == LayoutKind.Irregular || layout == LayoutKind.Trivial) layout = commonLayout;
                int redline = (int)Math.Round(v.Blink > 0 ? v.Blink : v.Last);
                p.LedRpm[gear] = LedLayout.Generate(p.LedNumber, gaps, layout, redline, v.First, v.Last);
                details.Add("  Gear " + gear + ": " + Describe(v) + " (" + from + ")");
            }

            notes.Add("iRacing reports the first and last LED and the blink RPM. LEDs in between were spaced evenly" +
                      (baseline != null ? ", following the repo file's layout and gaps." : ", left to right."));
            if (untouched.Count > 0)
                notes.Add("Gear " + string.Join(", ", untouched) + " not driven and this car's lights change per gear, so " +
                          (baseline != null ? "the repo values were kept." : "they were left at 0.") + " Select those gears to capture them.");
        }

        private static bool Same(ShiftLightValues a, ShiftLightValues b) =>
            a != null && b != null && a.First == b.First && a.Shift == b.Shift && a.Last == b.Last && a.Blink == b.Blink;

        private static string Describe(ShiftLightValues v) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0} / {1:0} / {2:0} / {3:0}", v.First, v.Shift, v.Last, v.Blink);

        // ---------- games without LED data ----------
        private static void ApplyRedlineOnly(CaptureSession s, CaptureSettings cfg, CarProfile p, CarProfile baseline, List<string> notes, List<string> details)
        {
            if (baseline != null)
            {
                notes.Add("This game doesn't report its LEDs, so the repo file's RPMs were kept. SimHub's redline is listed below for comparison.");
                details.Add("SimHub redline vs the repo file's redline (RL):");
                foreach (var gear in p.GearOrder)
                {
                    var simhub = s.Redline.ResolveRedline(gear, out var source);
                    details.Add(string.Format(CultureInfo.InvariantCulture, "  Gear {0}: SimHub {1:0} ({2}), file {3}", gear, simhub, source, p.LedRpm[gear][0]));
                }
                return;
            }

            notes.Add("This game doesn't report its LEDs, so the LED RPMs are an estimate: evenly spaced from " +
                      cfg.FirstLedPercent.ToString(CultureInfo.InvariantCulture) + "% to " + cfg.LastLedPercent.ToString(CultureInfo.InvariantCulture) +
                      "% of SimHub's redline. Check them against the game.");
            details.Add("Redline per gear:");
            foreach (var gear in p.GearOrder)
            {
                var redline = s.Redline.ResolveRedline(gear, out var source);
                p.LedRpm[gear] = Breakpoints(redline, p.LedNumber, cfg);
                details.Add(string.Format(CultureInfo.InvariantCulture, "  Gear {0}: {1:0} rpm ({2})", gear, redline, source));
            }
        }

        // ---------- shared ----------
        /// <summary>One line of a measured-thresholds table: the value, and where it came from.</summary>
        private static string LedLine(int index, LedThreshold led)
        {
            string window;
            if (led == null) window = "never lit";
            else if (led.Climbs > 1)
                window = led.Climbs + " climbs, each within " + led.ClimbSpread + " rpm";
            else if (led.Inconsistent)
                window = "seen dark at " + led.HighestOff + " after lit at " + led.LowestOn + " (check)";
            else if (led.HighestOff.HasValue)
                window = "dark to " + led.HighestOff + ", lit from " + led.LowestOn;
            else
                window = "<= " + led.LowestOn + " (no dark sample below it)";
            return string.Format(CultureInfo.InvariantCulture, "  LED {0,2}  {1,6}  {2}",
                index + 1, led?.Rpm.ToString(CultureInfo.InvariantCulture) ?? "-", window);
        }

        private static CarProfile NewProfile(CaptureSession s, CaptureSettings cfg, int leds, bool f1, List<string> notes)
        {
            var p = new CarProfile
            {
                CarName = string.IsNullOrEmpty(s.CarModel) ? s.CarId : s.CarModel,
                CarId = s.CarId,
                CarClass = s.CarClass ?? "",
                LedNumber = leds,
                RedlineBlinkInterval = f1 ? 50 : 0,
                LedColor = DefaultColors(leds).ToList(),
            };
            int top = Math.Max(s.Redline.TopGear, s.F1.Gears.Concat(s.IRacing.Gears).Concat(s.Marks.Gears).Select(CarProfile.GearRank).Where(r => r < 1000).DefaultIfEmpty(0).Max());
            p.GearOrder.Add("R");
            p.GearOrder.Add("N");
            for (int i = 1; i <= top; i++) p.GearOrder.Add(i.ToString(CultureInfo.InvariantCulture));
            p.Normalize();

            notes.Add("New car: carName and carClass come from SimHub (\"" + p.CarName + "\", \"" + p.CarClass + "\"). Check them against the repo's naming, and set the LED colors" +
                      (f1 ? "." : " and redlineBlinkInterval."));
            return p;
        }

        private static void FillUndrivenGears(CarProfile p, CarProfile baseline, string bestGear, List<string> driven, List<string> notes, string why)
        {
            var undriven = p.GearOrder.Where(g => !driven.Contains(g)).ToList();
            if (undriven.Count == 0 || bestGear == null) return;
            if (baseline != null)
            {
                notes.Add("Gear " + string.Join(", ", undriven) + " not captured; kept the repo values.");
                return;
            }
            foreach (var g in undriven) p.LedRpm[g] = (int[])p.LedRpm[bestGear].Clone();
            notes.Add("Gear " + string.Join(", ", undriven) + " not captured; copied gear " + bestGear + "'s values. " + why);
        }

        private static IEnumerable<string> DescribeChanges(CarProfile before, CarProfile after)
        {
            var lines = new List<string>();
            if (before == null)
            {
                lines.Add("new file with " + after.LedNumber + " LEDs and gears " + string.Join(", ", after.GearOrder));
                return lines;
            }
            foreach (var gear in after.GearOrder)
            {
                if (!before.LedRpm.TryGetValue(gear, out var old)) { lines.Add("gear " + gear + ": added"); continue; }
                var now = after.LedRpm[gear];
                var changed = Enumerable.Range(0, now.Length).Where(i => i >= old.Length || old[i] != now[i]).ToList();
                if (changed.Count == 0) continue;
                lines.Add("gear " + gear + ": " + string.Join(", ", changed.Select(i =>
                    (i == 0 ? "RL" : "LED " + i) + " " + (i < old.Length ? old[i].ToString(CultureInfo.InvariantCulture) : "-") + "->" + now[i].ToString(CultureInfo.InvariantCulture))));
            }
            if (lines.Count == 0) lines.Add("no RPM changes");
            return lines;
        }

        private static string DescribeLookup(RepoLookup lookup)
        {
            if (lookup == null) return "not checked";
            switch (lookup.Status)
            {
                case RepoLookupStatus.Found: return "data/" + lookup.RelativePath + " (used as the starting point)";
                case RepoLookupStatus.NotInRepo: return "not in the repo; would be data/" + lookup.RelativePath;
                case RepoLookupStatus.Failed: return "lookup failed (" + lookup.Error + "); built a new file";
                default: return "lookup turned off in settings";
            }
        }

        // Index 0 is the redline (RL) value; LEDs 1..N spread evenly between FirstLedPercent and LastLedPercent of redline.
        private static int[] Breakpoints(double redline, int leds, CaptureSettings cfg)
        {
            var row = new int[leds + 1];
            row[0] = RoundTo(redline, cfg.RoundRpmTo);
            for (int i = 1; i <= leds; i++)
            {
                double t = leds == 1 ? 1.0 : (i - 1) / (double)(leds - 1);
                double pct = cfg.FirstLedPercent + (cfg.LastLedPercent - cfg.FirstLedPercent) * t;
                row[i] = RoundTo(redline * pct / 100.0, cfg.RoundRpmTo);
            }
            return row;
        }

        private static IEnumerable<string> DefaultColors(int leds)
        {
            yield return Blue;
            for (int i = 1; i <= leds; i++)
            {
                int band = (i - 1) * 3 / leds;
                yield return band == 0 ? Green : band == 1 ? Yellow : Red;
            }
        }

        private static int RoundTo(double value, int step)
        {
            if (step <= 1) return (int)Math.Round(value);
            return (int)(Math.Round(value / step) * step);
        }
    }
}
