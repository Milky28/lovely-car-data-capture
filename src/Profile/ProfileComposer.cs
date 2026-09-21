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
        // Mirrored lights with the same onset can render a few degrees apart in PMR's dim green glow.
        private const double MirrorColourDegrees = 12;
        // A mirrored pair agreeing this closely in several gears can expose one false late onset.
        private const int MirrorRpm = 30;

        public static ComposeResult Compose(CaptureSession s, CaptureSettings cfg, RepoLookup lookup, DateTime capturedAt,
            string localOverrides = null, string previousExport = null)
        {
            if (cfg.TopGearForNextExport < 0 || cfg.TopGearForNextExport > 12)
                throw new ArgumentOutOfRangeException(nameof(cfg.TopGearForNextExport), "Top gear must be between 0 and 12.");
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
            var addedGears = new List<string>();
            for (int gear = 1; gear <= cfg.TopGearForNextExport; gear++)
            {
                string name = gear.ToString(CultureInfo.InvariantCulture);
                if (p.GearOrder.Contains(name)) continue;
                p.EnsureGear(name);
                addedGears.Add(name);
            }
            if (cfg.TopGearForNextExport > 0)
                notes.Add("Gears through " + cfg.TopGearForNextExport + " were requested for this export. Gears not driven use fallback values.");
            var screenGears = screen ? screenResult.Gears.Select(g => g.Gear) : new string[0];
            var capturedGears = (f1 ? s.F1.Gears : iracing ? s.IRacing.Gears : screen ? screenGears :
                manual ? s.Marks.Gears : s.Redline.Gears.Keys).Distinct().ToList();
            foreach (var gear in s.Redline.Gears.Keys.Concat(s.F1.Gears).Concat(s.IRacing.Gears).Concat(screenGears).Concat(manual ? s.Marks.Gears : new string[0]).Distinct().ToList())
            {
                if (baseline != null && !p.GearOrder.Contains(gear)) notes.Add("Gear " + gear + " isn't in the repo file; it was added.");
                p.EnsureGear(gear);
            }

            if (f1) ApplyF1(s, p, baseline, notes, details);
            else if (iracing) ApplyIRacing(s, p, baseline, notes, details);
            else if (screen) ApplyScreen(screenResult, cfg, p, baseline, notes, details);
            else if (manual) ApplyManualMarks(s, p, baseline, notes, details);
            else ApplyRedlineOnly(s, cfg, p, baseline, notes, details);

            var overrideResult = LocalProfileOverrides.Apply(p, s.GameName, s.CarId, localOverrides, notes);
            var previousGears = new List<string>();
            if (!(screen && cfg.CopyMeasuredToOtherGears))
            {
                previousGears = PreviousCaptureRpm.Restore(p, s.CarId, previousExport, capturedGears, notes,
                    screen && screenResult.Layout.LedNumber == p.LedNumber ? screenResult.Layout.IsGap : null);
            }
            if (screen && baseline == null) KeepFallbacksAboveObservedDark(screenResult, p, previousGears, notes);
            if (screen && baseline == null) KeepFallbacksAtObservedOn(screenResult, p, previousGears, notes);
            // A repo file can itself omit a known gear. Copy the nearest lower gear only if neither
            // the capture nor the previous export supplied its row.
            foreach (var gear in addedGears.Where(g => !p.LedRpm[g].Any(v => v > 0)))
            {
                int rank = CarProfile.GearRank(gear);
                var source = p.GearOrder.Where(g => !addedGears.Contains(g) && CarProfile.GearRank(g) >= 1 && CarProfile.GearRank(g) < rank &&
                                                    p.LedRpm[g].Any(v => v > 0)).LastOrDefault();
                if (source == null) continue;
                p.LedRpm[gear] = (int[])p.LedRpm[source].Clone();
                notes.Add("Gear " + gear + " was not captured; copied gear " + source + "'s values.");
            }
            if (screen && screenResult.Layout.LedNumber == p.LedNumber)
            {
                // ATSR needs a black color as well as RPM 0 for a gap. Do this after pooling and
                // restoring old gears, which can otherwise bring back the repo's lit gap slots.
                var gaps = Enumerable.Range(0, p.LedNumber).Where(i => screenResult.Layout.IsGap[i]).Select(i => i + 1).ToList();
                bool changed = gaps.Any(i => !LedLayout.IsGapColor(p.LedColor[i]) || p.LedRpm.Values.Any(row => row[i] != 0));
                foreach (int slot in gaps)
                {
                    p.LedColor[slot] = "#00000000";
                    foreach (var row in p.LedRpm.Values) row[slot] = 0;
                }
                if (changed) notes.Add("Screen gaps at LED " + string.Join(", ", gaps) +
                    " were set to black with RPM 0 in every gear, so ATSR leaves them off.");
            }
            if (screen)
            {
                AddFinalScreenNotes(screenResult, p, baseline, new HashSet<string>(previousGears), notes);
                AddFinalBlinkNote(screenResult, p, baseline, overrideResult, notes);
            }
            if (screen && baseline != null && screenResult.Layout.LedNumber == p.LedNumber)
                AddFinalColorNotes(screenResult, p, overrideResult, notes);
            if (screen && !cfg.CopyMeasuredToOtherGears)
                ReportUndrivenDifference(p, screenResult, notes);

            if (sim == "lmu")
                notes.Add("LMU files in the repo are generated from the templates in src_data/lmu by scripts/build_profiles.py; put these values in the matching template rather than submitting data/lmu directly.");
            if (lookup != null && lookup.SameCarId.Count > 0)
                notes.Add("Other repo files use the same carId: " + string.Join(", ", lookup.SameCarId.Select(x => "data/" + x)) + ".");

            bool captureApplied = f1 ? p.LedNumber == LedWindowCapture.F1LedCount : iracing || manual;
            if (screen) captureApplied = screenResult.Layout != null && p.LedNumber == screenResult.Layout.LedNumber;

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
            r.Add("");
            r.Add("Final value sources:");
            r.AddRange(DescribeFinalSources(p, baseline, capturedGears, previousGears, overrideResult,
                                            cfg.CopyMeasuredToOtherGears, captureApplied, screen, screenResult)
                .Select(x => "  " + x));
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
                    if (Trusted(gr.Leds[i])) row[i + 1] = gr.Leds[i].Rpm;
                }
                int lastLit = gr.Leds.Where(Trusted).Select(l => l.Rpm).DefaultIfEmpty(0).Max();
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
                var loose = gr.Leds.Select((l, i) => new { l, i }).Where(x => x.l != null && !Trusted(x.l)).Select(x => x.i + 1).ToList();
                if (loose.Count > 0)
                    notes.Add("Gear " + gr.Gear + ": LED " + string.Join(", ", loose) + " measured too loosely to use; " +
                              (baseline != null ? "kept the repo values." : "left at 0.") + " Rev slowly from below these lights again.");
            }

            FillUndrivenGears(p, baseline, results.Where(r => r.Leds.Any(Trusted)).OrderByDescending(r => r.Leds.Count(Trusted)).ThenByDescending(r => r.Samples).Select(r => r.Gear).FirstOrDefault(),
                s.F1.Gears.ToList(), notes, "F1 cars normally use the same lights in every gear.");
        }

        // ---------- rev lights read off the screen ----------
        private static void ApplyScreen(ScreenLedResult sr, CaptureSettings cfg, CarProfile p, CarProfile baseline, List<string> notes, List<string> details)
        {
            var layout = sr.Layout;
            var results = sr.Gears.OrderBy(g => CarProfile.GearRank(g.Gear)).ToList();
            bool mappable = p.LedNumber == layout.LedNumber;

            // A smaller washed-out part of a simultaneous bank takes the larger part's colour.
            // Keep the other banks' palette assignments: merging and renaming the whole ladder can
            // turn an established blue bank cyan when a spurious green shade disappears.
            foreach (var group in sr.ColorGroups.OrderBy(g => g.Slots.Count))
            {
                var same = sr.ColorGroups.Where(g => g.Slots.Count > group.Slots.Count &&
                    group.Slots.All(a => g.Slots.All(b => SameSwitchBank(sr, a, b))))
                    .OrderByDescending(g => g.Slots.Count).FirstOrDefault();
                if (same == null) continue;
                group.Hex = same.Hex;
                group.Name = same.Name;
            }
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
            if (sr.RedlineRpm.HasValue && !sr.RedlineFromBlink)
                details.Add("  Redline color " + sr.RedlineMeasured + " -> " + (sr.RedlineColor ?? "?") + " from " + sr.RedlineRpm + " rpm" +
                            (sr.RedlineHighestBelow.HasValue && sr.RedlineLowestAbove.HasValue
                                ? " (window " + sr.RedlineHighestBelow + "-" + sr.RedlineLowestAbove + ")" : ""));
            if (sr.RedlineByGear.Count > 1)
                details.Add("  Redline by gear: " + string.Join(", ", sr.RedlineByGear.OrderBy(g => g.Key, StringComparer.Ordinal)
                                .Select(g => g.Key + " " + g.Value.Rpm + " (" + g.Value.HighestBelow + "-" + g.Value.LowestAbove + ")" +
                                             (g.Value.OnsetSeen ? "" : " revs falling only"))));
            if (sr.SecondStageRpm.HasValue)
                details.Add("  Second stage " + sr.SecondStageMeasured + " -> " + (sr.SecondStageColor ?? "?") + " from " + sr.SecondStageRpm + " rpm (not in the file)");
            if (sr.BlinkSeen)
                details.Add("  Blinks from " + sr.BlinkFromRpm + " rpm, dark for about " + sr.BlinkIntervalMs + " ms at a time" +
                            (sr.RedlineFromBlink ? ", keeping the strip's own colours" : ""));
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
                    if (layout.IsGap[i]) { row[i + 1] = 0; continue; }
                    if (gr.Leds[i] == null) continue;
                    if (Trusted(gr.Leds[i])) row[i + 1] = gr.Leds[i].Rpm;
                }
                int lastLit = gr.Leds.Where(Trusted).Select(l => l.Rpm).DefaultIfEmpty(0).Max();
                // This gear's own redline where it had one: some cars move it with the gear.
                int? redline = sr.RedlineByGear.TryGetValue(gr.Gear, out var own) && own.OnsetSeen ? own.Rpm : sr.RedlineRpm;
                if (redline.HasValue && redline.Value >= lastLit) row[0] = redline.Value;
                else if (redline.HasValue)
                {
                    // Above the redline ATSR shows every light in the redline colour, so a redline below
                    // the last light would leave that light's own colour unreachable.
                    row[0] = lastLit;
                    notes.Add("Gear " + gr.Gear + ": the colour change measured at " + redline +
                              " rpm, below the last light at " + lastLit + " rpm, so the redline was set to the light. " +
                              "The two happen within a few rpm of each other on this car.");
                }
                else if (sr.SteadyAtLimiterRpm.HasValue && baseline == null)
                    // Nothing happens at the limiter: the redline goes there, transparent (see the colours),
                    // so ATSR never repaints the strip before the game would.
                    row[0] = Math.Max(sr.SteadyAtLimiterRpm.Value, lastLit);
                else if (row[0] < lastLit)
                {
                    row[0] = lastLit;
                    notes.Add("Gear " + gr.Gear + ": no redline color change was seen, so the redline was set to the last light's RPM. Hold the limiter briefly to capture it.");
                }

            }

            if (baseline == null)
                for (int left = 1; left <= layout.LedNumber / 2; left++)
                {
                    int right = layout.LedNumber + 1 - left;
                    var measured = results.Where(g => Trusted(g.Leds[left - 1]) && Trusted(g.Leds[right - 1])).ToList();
                    var matched = measured.Where(g => Math.Abs(g.Leds[left - 1].Rpm - g.Leds[right - 1].Rpm) <= MirrorRpm).ToList();
                    if (matched.Count < 3 || matched.Count < measured.Count - 1) continue;
                    int usual = Median(matched.Select(g => (g.Leds[left - 1].Rpm + g.Leds[right - 1].Rpm) / 2).ToList());
                    foreach (var g in measured.Except(matched))
                    {
                        int a = g.Leds[left - 1].Rpm, b = g.Leds[right - 1].Rpm;
                        if (Math.Abs(a - b) <= UniformRpm) continue;
                        bool leftFits = Math.Abs(a - usual) <= UniformRpm;
                        bool rightFits = Math.Abs(b - usual) <= UniformRpm;
                        if (leftFits == rightFits) continue;
                        var row = p.LedRpm[g.Gear];
                        row[leftFits ? right : left] = leftFits ? a : b;
                        if (!sr.RedlineRpm.HasValue && !sr.SteadyAtLimiterRpm.HasValue)
                            row[0] = row.Skip(1).Max();
                        notes.Add("Gear " + g.Gear + ": LED " + (leftFits ? right : left) + " was measured far from its mirrored partner and the other gears, so it uses the partner's RPM.");
                    }
                }

            // The lights come on in a fixed order, so a row that doesn't is wrong however tight its
            // windows looked: a gear caught mid-shift can produce one.
            foreach (var gr in results)
            {
                var row = p.LedRpm[gr.Gear];
                var template = TemplateRow(p, baseline, gr.Gear) ?? row;
                var order = Enumerable.Range(1, p.LedNumber).Where(i => !layout.IsGap[i - 1] && i < template.Length && template[i] > 0)
                                      .OrderBy(i => template[i]).ToList();
                bool ordered = true;
                for (int k = 1; k < order.Count; k++)
                    if (row[order[k]] > 0 && row[order[k - 1]] > 0 && row[order[k]] < row[order[k - 1]]) ordered = false;
                if (ordered || baseline == null) continue;

                p.LedRpm[gr.Gear] = (int[])baseline.LedRpm[gr.Gear].Clone();
                notes.Add("Gear " + gr.Gear + ": the lights came out in the wrong order, so this gear was left as the repo had it. " +
                          "That usually means the gear was only held briefly, or was caught mid-shift.");
            }

            ApplyScreenColors(sr, p, baseline, notes);
            if (sr.BlinkIntervalMs.HasValue && !sr.AlternatingRedline && !LaterStageBlink(sr) && (baseline == null || p.RedlineBlinkInterval == 0))
                p.RedlineBlinkInterval = sr.BlinkIntervalMs.Value;

            var bestGear = results.Where(r => r.CapturedCount > 0).OrderByDescending(r => r.CapturedCount)
                                  .ThenByDescending(r => int.TryParse(r.Gear, out int number) && number > 0)
                                  .ThenByDescending(r => r.Samples).Select(r => r.Gear).FirstOrDefault();
            var capturedGears = results.Select(r => r.Gear).ToList();
            var kept = p.GearOrder.Where(g => !capturedGears.Contains(g)).ToList();

            // Nearly every car uses one set of lights for all its gears, and a real track rarely gives
            // room to sweep any single gear from idle to the limiter. So the gears are pooled: where
            // they measured the same light they have to agree, and what one gear missed another fills
            // in. Disagreement beyond a few rpm is taken at face value - that car really does differ
            // per gear - and each gear keeps its own values.
            var byLed = new List<int>[p.LedNumber + 1];
            for (int i = 0; i <= p.LedNumber; i++) byLed[i] = new List<int>();
            bool neutralLeftOut = false;
            for (int i = 0; i < p.LedNumber; i++)
            {
                if (layout.IsGap[i]) continue;
                var measured = results.Where(r => Trusted(r.Leds[i])).ToList();
                var driven = measured.Where(r => int.TryParse(r.Gear, out int number) && number > 0).ToList();
                if (driven.Count > 0)
                {
                    neutralLeftOut |= driven.Count < measured.Count;
                    measured = driven;
                }
                byLed[i + 1].AddRange(measured.Select(r => r.Leds[i].Rpm));
            }
            if (neutralLeftOut)
                notes.Add("Driven gears measured these lights, so neutral readings were left out of their shared RPM values.");

            // When a new car's one light flickers across its switch-on point, no individual gear's
            // window is trustworthy. Matching switch-ons in several gears still locate it better
            // than RPM 0, which ATSR would light all the time.
            if (baseline == null)
                for (int led = 1; led <= p.LedNumber; led++)
                {
                    if (layout.IsGap[led - 1] || byLed[led].Count > 0) continue;
                    var tentative = results.Select(r => r.Leds[led - 1])
                                           .Where(l => l != null && l.Climbs > 0 && l.HighestOff.HasValue &&
                                                       (l.Climbs > 1 ? l.ClimbWidth <= MaxWindowRpm :
                                                        l.HighestOff.Value <= l.LowestOn &&
                                                        l.LowestOn - l.HighestOff.Value <= MaxWindowRpm))
                                           .Select(l => l.Rpm).ToList();
                    if (tentative.Count < 2 || tentative.Max() - tentative.Min() > UniformRpm) continue;
                    byLed[led].Add(Median(tentative));
                    notes.Add("LED " + led + " had imperfect switch-on readings in " + tentative.Count +
                              " gears, but they agree; their middle value was used. Check it in the game.");
                }

            // One gear alone out of line with the rest is a bad reading rather than a different car: in
            // neutral and first the revs climb so fast that a few milliseconds of display lag misjudged
            // is a hundred rpm. With three or more gears measuring a light, a minority far from the
            // middle of the others is left out; a car that really changes per gear disagrees everywhere.
            var outliers = new List<string>();
            for (int led = 1; led <= p.LedNumber; led++)
            {
                var values = byLed[led];
                if (values.Count < 3) continue;
                int middle = Median(values);
                var far = values.Where(v => Math.Abs(v - middle) > UniformRpm).ToList();
                if (far.Count == 0 || far.Count * 2 >= values.Count) continue;
                var gearsFar = results.Where(r => !layout.IsGap[led - 1] && Trusted(r.Leds[led - 1]) && far.Contains(r.Leds[led - 1].Rpm))
                                      .Select(r => r.Gear);
                outliers.Add("LED " + led + " in gear " + string.Join("/", gearsFar) + " (" + string.Join("/", far) + " against " + middle + ")");
                values.RemoveAll(v => far.Contains(v));
            }

            bool measuredTwice = byLed.Any(v => v.Count >= 2);
            bool agree = byLed.All(v => v.Count == 0 || v.Max() - v.Min() <= UniformRpm);
            var measuredLeds = Enumerable.Range(1, p.LedNumber).Where(i => byLed[i].Count > 0).ToList();

            if (bestGear != null && measuredTwice && agree)
            {
                var pooled = (int[])p.LedRpm[bestGear].Clone();
                // To 5 rpm like every other value: the middle of an even count can land between two.
                foreach (int led in measuredLeds) pooled[led] = (int)(Math.Round(Median(byLed[led]) / 5.0) * 5);
                var pairs = new List<Tuple<int, int>>();
                bool nearMirror = layout.LedNumber >= 4;
                for (int left = 1; left <= layout.LedNumber / 2; left++)
                {
                    int right = layout.LedNumber + 1 - left;
                    if (layout.IsGap[left - 1] && layout.IsGap[right - 1]) continue;
                    if (layout.IsGap[left - 1] || layout.IsGap[right - 1] || pooled[left] == 0 || pooled[right] == 0 ||
                        Math.Abs(pooled[left] - pooled[right]) > MirrorRpm ||
                        !string.Equals(p.LedColor[left], p.LedColor[right], StringComparison.OrdinalIgnoreCase))
                    { nearMirror = false; break; }
                    pairs.Add(Tuple.Create(left, right));
                }
                if (nearMirror && pairs.Count >= 2)
                {
                    foreach (var pair in pairs)
                        pooled[pair.Item1] = pooled[pair.Item2] = Math.Min(pooled[pair.Item1], pooled[pair.Item2]);
                    notes.Add("The paired lights agreed within " + MirrorRpm +
                              " rpm, so each pair was set to its earlier reading to light together on the wheel.");
                }
                int last = measuredLeds.Select(i => pooled[i]).DefaultIfEmpty(0).Max();
                // Only gears that saw the revs rise into the redline say where it starts.
                var redlines = sr.RedlineByGear.Values.Where(r => r.OnsetSeen).Select(r => r.Rpm).ToList();
                if (redlines.Count == 0) redlines = sr.RedlineByGear.Values.Select(r => r.Rpm).ToList();
                if (redlines.Count == 0 && sr.RedlineRpm.HasValue) redlines.Add(sr.RedlineRpm.Value);
                pooled[0] = Math.Max(redlines.Count > 0 ? Median(redlines) : pooled[0], last);

                // The lights can be the same in every gear while the redline isn't: the gears that had
                // their own redline keep it, and the rest keep what they had.
                var ownRedlines = results.Where(r => sr.RedlineByGear.TryGetValue(r.Gear, out var own) && own.OnsetSeen)
                                         .ToDictionary(r => r.Gear, r => p.LedRpm[r.Gear][0]);
                var fallingOnly = results.Where(r => sr.RedlineByGear.TryGetValue(r.Gear, out var own) && !own.OnsetSeen)
                                         .Select(r => r.Gear).ToList();
                if (fallingOnly.Count > 0)
                    notes.Add("Gear " + string.Join(", ", fallingOnly) + " only saw the redline as the revs fell, which reads " +
                              "lower than where the flash starts, so " + (fallingOnly.Count == 1 ? "it uses" : "they use") +
                              " the redline the other gears measured.");
                bool redlinePerGear = ownRedlines.Count >= 2 && ownRedlines.Values.Max() - ownRedlines.Values.Min() > UniformRpm;
                var before = p.GearOrder.ToDictionary(g => g, g => p.LedRpm[g][0]);
                foreach (var gear in p.GearOrder)
                {
                    // Only measured slots can replace this gear's values. An unseen light may
                    // legitimately have a different threshold in each of the repo's gears.
                    var row = p.LedRpm[gear];
                    foreach (int led in measuredLeds) row[led] = pooled[led];
                    int rl = !redlinePerGear && redlines.Count > 0 ? pooled[0]
                           : ownRedlines.TryGetValue(gear, out var measured) ? measured
                           : baseline != null ? before[gear] : pooled[0];
                    row[0] = Math.Max(rl, last);
                }
                if (redlinePerGear)
                    notes.Add("The redline moves with the gear (" + string.Join(", ", ownRedlines.OrderBy(g => g.Key, StringComparer.Ordinal)
                              .Select(g => g.Key + " " + g.Value)) + "), so each measured gear kept its own" +
                              (baseline != null ? " and the others kept the repo's." : "; the others use the middle one."));

                if (outliers.Count > 0)
                    notes.Add("Left out of the pooling as far from the other gears: " + string.Join(", ", outliers) +
                              ". The revs climb fastest in the low gears, so those are measured least precisely.");
                notes.Add("Gear " + string.Join(", ", results.Select(r => r.Gear)) + " agree within " + UniformRpm +
                          " rpm wherever they measured the same light, so this car uses the same lights in every gear: " +
                          "they were pooled and used for all of them. If this car really does differ per gear, sweep each " +
                          "gear on its own and export again.");
                var never = Enumerable.Range(1, p.LedNumber).Where(i => !layout.IsGap[i - 1] && byLed[i].Count == 0).ToList();
                if (never.Count > 0)
                    notes.Add("LED " + string.Join(", ", never) + " were never measured in any gear; " +
                              (baseline != null ? "the repo values were kept." : "they were left at 0."));
                return;
            }

            if (bestGear != null && measuredLeds.Count > 0 && !agree)
            {
                var disagreed = Enumerable.Range(1, p.LedNumber).Where(i => byLed[i].Count >= 2 && byLed[i].Max() - byLed[i].Min() > UniformRpm)
                                          .Select(i => "LED " + i + " " + string.Join("/", byLed[i]));
                notes.Add("The gears disagree about " + string.Join(", ", disagreed) + ", so each gear kept its own values. " +
                          "Either this car changes its lights per gear, or a sweep was caught mid-shift; the report's tables show which.");
            }

            // A new car has no repo value to keep. Even when gears disagree, a light measured in
            // another gear is better than RPM 0, which ATSR treats as always on.
            if (baseline == null)
            {
                var filled = new List<string>();
                foreach (var gr in results)
                {
                    var row = p.LedRpm[gr.Gear];
                    for (int led = 1; led <= p.LedNumber; led++)
                    {
                        if (layout.IsGap[led - 1] || row[led] != 0 || byLed[led].Count == 0) continue;
                        var bank = Enumerable.Range(0, p.LedNumber).Where(i => Trusted(gr.Leds[i]) && SameSwitchBank(sr, led - 1, i))
                                             .Select(i => gr.Leds[i].Rpm).ToList();
                        // A gear-dependent bank is stronger evidence than another gear's threshold.
                        row[led] = (int)(Math.Round(Median(bank.Count > 0 && bank.Max() - bank.Min() <= MirrorRpm ? bank : byLed[led]) / 5.0) * 5);
                        filled.Add("gear " + gr.Gear + " LED " + led);
                    }
                }
                if (filled.Count > 0)
                    notes.Add("Missing switch-on readings for " + string.Join(", ", filled) +
                              " were filled from a matching simultaneous bank in this gear where available, otherwise from the same lights in other gears. Check those gears in the game.");
            }

            if (baseline != null && bestGear != null && cfg.CopyMeasuredToOtherGears)
            {
                var incomplete = results.Where(r => r.Gear != bestGear && r.Leds.Where((l, i) => !layout.IsGap[i]).Any(l => !Trusted(l)))
                                        .Select(r => r.Gear).ToList();
                var filled = kept.Concat(incomplete).Distinct().ToList();
                foreach (var gear in filled) p.LedRpm[gear] = (int[])p.LedRpm[bestGear].Clone();
                if (filled.Count > 0)
                    notes.Add("Gear " + string.Join(", ", filled) + ": gear " + bestGear +
                              "'s measured values were used, because CopyMeasuredToOtherGears is on and these gears weren't " +
                              "measured right through.");
            }
            else
            {
                FillUndrivenGears(p, baseline, bestGear, capturedGears, notes,
                    "Many cars use the same lights in every gear; capture the others if they differ.");
            }
        }
        // AC's five-light banks vary by about eight hue degrees under tone mapping. Require two
        // independent gears agreeing on simultaneity before using that narrow colour tolerance.
        private const double BankHueDegrees = 10;
        private static bool LaterStageBlink(ScreenLedResult sr) => sr.SecondStageRpm.HasValue && sr.BlinkFromRpm.HasValue &&
            sr.RedlineRpm.HasValue && sr.BlinkFromRpm.Value >= sr.SecondStageRpm.Value &&
            sr.SecondStageRpm.Value > sr.RedlineRpm.Value + UniformRpm;
        private static bool SameSwitchBank(ScreenLedResult sr, int a, int b)
        {
            if (a == b || sr.Layout.IsGap[a] || sr.Layout.IsGap[b] || sr.MeasuredColors[a].Hue < 0 || sr.MeasuredColors[b].Hue < 0 ||
                HueGap(sr.MeasuredColors[a].Hue, sr.MeasuredColors[b].Hue) > BankHueDegrees) return false;
            var paired = sr.Gears.Where(g => Trusted(g.Leds[a]) && Trusted(g.Leds[b])).ToList();
            return paired.Count >= 2 && paired.All(g => Math.Abs(g.Leds[a].Rpm - g.Leds[b].Rpm) <= MirrorRpm);
        }

        private static void ReportUndrivenDifference(CarProfile p, ScreenLedResult sr, List<string> notes)
        {
            // Compare the final rows, after restoring previous captures, so the warning describes
            // the file actually written rather than repository fallbacks that were replaced.
            var best = sr.Gears.Where(g => g.CapturedCount > 0).OrderByDescending(g => g.CapturedCount)
                         .ThenByDescending(g => g.Samples).FirstOrDefault();
            if (best == null) return;
            var measured = p.LedRpm[best.Gear];
            var captured = new HashSet<string>(sr.Gears.Select(g => g.Gear));
            var differences = p.GearOrder.Where(g => !captured.Contains(g)).SelectMany(g =>
                Enumerable.Range(0, measured.Length).Where(i => measured[i] > 0 && p.LedRpm[g][i] > 0)
                    .Select(i => Math.Abs(measured[i] - p.LedRpm[g][i])));
            int worst = differences.DefaultIfEmpty(0).Max();
            if (worst > 50)
                notes.Add("Gears not captured in this drive differ by up to " + worst +
                    " rpm from gear " + best.Gear + ". This may be normal for this car; capture those gears to check their values.");
        }

        private static void AddFinalScreenNotes(ScreenLedResult sr, CarProfile p, CarProfile baseline,
                                                HashSet<string> previousGears, List<string> notes)
        {
            if (sr.Layout == null || p.LedNumber != sr.Layout.LedNumber) return;
            foreach (var gr in sr.Gears)
            {
                if (previousGears.Contains(gr.Gear) || !p.LedRpm.ContainsKey(gr.Gear)) continue;
                var unresolved = new List<int>();
                for (int i = 0; i < gr.Leds.Length; i++)
                {
                    if (sr.Layout.IsGap[i] || (gr.Leds[i] != null && Trusted(gr.Leds[i]))) continue;
                    int final = p.LedRpm[gr.Gear][i + 1];
                    bool remainsRepo = baseline != null && baseline.LedRpm.TryGetValue(gr.Gear, out var old) &&
                                       i + 1 < old.Length && final == old[i + 1];
                    bool supportedByAnotherGear = sr.Gears.Any(other => other.Gear != gr.Gear &&
                        i < other.Leds.Length && Trusted(other.Leds[i]) &&
                        Math.Abs(other.Leds[i].Rpm - final) <= 5);
                    if (baseline == null ? final == 0 : remainsRepo && !supportedByAnotherGear) unresolved.Add(i + 1);
                }
                if (unresolved.Count == 0) continue;
                notes.Add("Gear " + gr.Gear + ": LED " + string.Join(", ", unresolved) +
                          (baseline == null
                              ? " still has no trusted switch-on value and remains 0."
                              : " has no distinct trusted capture value and remains at the repo value."));
            }
        }

        // Leave a little headroom beyond the observed range; these are safe placeholders, not onsets.
        private const int DarkFallbackMarginRpm = 50;
        private static void KeepFallbacksAtObservedOn(ScreenLedResult sr, CarProfile p, List<string> previous, List<string> notes)
        {
            if (sr.Layout.LedNumber != p.LedNumber) return;
            foreach (var entry in sr.ObservedOnUpperBounds)
            {
                var gear = sr.Gears.FirstOrDefault(g => g.Gear == entry.Key);
                if (gear == null || previous.Contains(entry.Key) || !p.LedRpm.TryGetValue(entry.Key, out var row)) continue;
                var changed = new List<string>();
                for (int i = 0; i < entry.Value.Length; i++)
                {
                    if (sr.Layout.IsGap[i] || Trusted(gear.Leds[i]) || entry.Value[i] <= 0 || row[i + 1] >= entry.Value[i]) continue;
                    row[i + 1] = (int)(Math.Ceiling(entry.Value[i] / 5.0) * 5);
                    row[0] = Math.Max(row[0], row[i + 1]);
                    changed.Add((i + 1) + " at " + row[i + 1]);
                }
                if (changed.Count > 0)
                    notes.Add("Gear " + entry.Key + ": ambiguous pale background hid the dark-to-lit crossing for LED " + string.Join(", ", changed) +
                        " rpm. Earlier borrowed thresholds were replaced by this gear's first repeatedly observed ON values. " +
                        "These are unmeasured upper-bound fallbacks and may light late; the pale frames were not treated as dark. Capture another slow sweep to measure the crossings.");
            }
        }
        private static void KeepFallbacksAboveObservedDark(ScreenLedResult sr, CarProfile p, List<string> previous, List<string> notes)
        {
            if (sr.Layout.LedNumber != p.LedNumber) return;
            foreach (var bound in sr.DarkLowerBounds)
            {
                var measured = sr.Gears.FirstOrDefault(g => g.Gear == bound.Key);
                if (previous.Contains(bound.Key) || measured == null || measured.Leds.Any(Trusted) || !p.LedRpm.TryGetValue(bound.Key, out var row)) continue;
                var slots = Enumerable.Range(1, p.LedNumber).Where(i => !sr.Layout.IsGap[i - 1] && row[i] > 0).ToList();
                if (slots.Count == 0 || slots.Min(i => row[i]) > bound.Value) continue;
                int floor = (int)(Math.Ceiling((bound.Value + DarkFallbackMarginRpm) / 5.0) * 5);
                int shift = floor - slots.Min(i => row[i]);
                // Shift the borrowed row together, preserving simultaneous banks and the last-row
                // ordering ATSR uses to identify the layout. Never flatten it to one equal value.
                foreach (int i in slots) row[i] += shift;
                row[0] = Math.Max(row[0] + shift, slots.Max(i => row[i]));
                notes.Add("Gear " + bound.Key + " stayed dark through " + bound.Value + " rpm in sustained raw observations, but no switch-on was measured. " +
                    "Borrowed thresholds were moved together by " + shift + " rpm so the first is " + floor +
                    " rpm. These are unmeasured placeholders above the tested range, not measured onsets or proof the lights never appear. Capture a higher sweep to replace them.");
            }
        }

        private static void AddFinalBlinkNote(ScreenLedResult sr, CarProfile p, CarProfile baseline,
                                              LocalProfileOverrideResult overrides, List<string> notes)
        {
            if (overrides.BlinkInterval.HasValue)
            {
                if (sr.AlternatingRedline)
                    notes.Add("The confirmed " + overrides.BlinkInterval.Value + " ms on/off blink is an approximation of the observed colour alternation. " +
                        "It uses the exported redline colour and cannot show the other colour; this is not a measured dark phase.");
                if (sr.BlinkIntervalMs.HasValue)
                    notes.Add("The capture measured a blink of about " + sr.BlinkIntervalMs +
                              " ms; the confirmed local override of " + overrides.BlinkInterval.Value +
                              " ms is the final exported interval.");
                return;
            }
            if (sr.AlternatingRedline)
            {
                notes.Add("Alternating redline colours cannot be encoded as an on/off interval. The final interval is " +
                    p.RedlineBlinkInterval + " ms; no doubled or halved timing was inferred from the colour changes.");
                return;
            }
            if (sr.BlinkIntervalMs.HasValue)
            {
                if (LaterStageBlink(sr))
                {
                    notes.Add("Blinking begins in a later limiter stage, above the first colour change. The file has only one redline stage, so this later blink was not applied; the exported interval remains " + p.RedlineBlinkInterval + " ms.");
                    return;
                }
                if (baseline != null && p.RedlineBlinkInterval != sr.BlinkIntervalMs.Value)
                    notes.Add("The capture measured a blink of about " + sr.BlinkIntervalMs +
                              " ms, but the repo value of " + p.RedlineBlinkInterval + " ms remains final.");
                else
                    notes.Add("The capture measured a blink of about " + p.RedlineBlinkInterval +
                              " ms, and that value is final.");
            }
            else if (baseline != null && p.RedlineBlinkInterval > 0 && sr.RedlineRpm.HasValue)
                notes.Add("The capture did not measure a blink; the repo value of " + p.RedlineBlinkInterval +
                          " ms remains final.");
        }

        private static IEnumerable<string> DescribeFinalSources(CarProfile p, CarProfile baseline,
                                                                  IEnumerable<string> capturedGears,
                                                                  IEnumerable<string> previousGears,
                                                                  LocalProfileOverrideResult overrides,
                                                                  bool copyMeasured,
                                                                  bool captureApplied,
                                                                  bool screen, ScreenLedResult sr)
        {
            var captured = new HashSet<string>(capturedGears ?? new string[0]);
            var previous = new HashSet<string>(previousGears ?? new string[0]);
            var lines = new List<string>();
            var capturedList = p.GearOrder.Where(captured.Contains).ToList();
            var previousList = p.GearOrder.Where(previous.Contains).ToList();
            var fallback = p.GearOrder.Where(g => !captured.Contains(g) && !previous.Contains(g)).ToList();

            if (capturedList.Count > 0)
                lines.Add("RPM: captured gears " + string.Join(", ", capturedList) +
                          (captureApplied
                              ? " use trusted capture values where available; final rows may combine measured, pooled, and starting-file values."
                              : " did not supply mapped LED thresholds; starting-file or estimated values remain."));
            if (previousList.Count > 0)
                lines.Add("RPM: previous local values are final for retained gears " + string.Join(", ", previousList) + ".");
            if (fallback.Count > 0 && baseline != null)
                lines.Add("RPM: uncaptured gears " + string.Join(", ", fallback) +
                          (copyMeasured
                              ? " use the measured gear's values where configured; otherwise their repo values remain."
                              : " use pooled capture values where available; otherwise their repo values remain."));
            if (capturedList.Count == 0 && previousList.Count == 0 && baseline == null)
                lines.Add("RPM: no LED capture supplied a value; the export uses its configured defaults.");

            var effectiveColorSlots = overrides.ColorSlots.Where(i => i == 0 || !screen || sr == null ||
                sr.Layout == null || i > sr.Layout.LedNumber || !sr.Layout.IsGap[i - 1]).ToList();
            if (effectiveColorSlots.Count > 0)
                lines.Add("Colors: confirmed local overrides are final for LED " + string.Join(", ", effectiveColorSlots) +
                          "; other slots use the capture or repo result.");
            else if (overrides.ColorSlots.Count > 0)
                lines.Add("Colors: the requested local color overrides targeted physical gaps, which remain black in the final export.");
            else if (baseline != null)
                lines.Add("Colors: repo colors are the fallback where capture evidence did not change a grouping.");
            else if (screen)
                lines.Add("Colors: measured from the screen capture.");

            if (overrides.BlinkInterval.HasValue)
                lines.Add("Redline blink: confirmed local override " + overrides.BlinkInterval.Value + " ms is final.");
            else if (screen && sr != null && sr.AlternatingRedline)
                lines.Add("Redline: alternating colours are not representable; one observed colour and interval " + p.RedlineBlinkInterval + " ms are final.");
            else if (screen && sr != null && LaterStageBlink(sr))
                lines.Add("Redline blink: the later limiter blink cannot share the first colour-change threshold; interval " + p.RedlineBlinkInterval + " ms remains final.");
            else if (screen && sr != null && sr.BlinkIntervalMs.HasValue && baseline != null &&
                     p.RedlineBlinkInterval != sr.BlinkIntervalMs.Value)
                lines.Add("Redline blink: capture measured about " + sr.BlinkIntervalMs +
                          " ms; repo value " + p.RedlineBlinkInterval + " ms is final.");
            else if (screen && sr != null && sr.BlinkIntervalMs.HasValue)
                lines.Add("Redline blink: capture measured about " + p.RedlineBlinkInterval + " ms, which is final.");
            else if (baseline != null)
                lines.Add("Redline blink: repo value " + p.RedlineBlinkInterval + " ms is final where capture timing was unavailable.");
            return lines;
        }

        /// <summary>
        /// SimHub's model name for a new car without the model year ACC adds to every car ("Ginetta G55
        /// GT4 2012"). The repo keeps a year only where it tells two versions apart (the 2016 and 2018
        /// Bentleys), which the report asks to be checked. The file name comes from the car id either way.
        /// </summary>
        internal static string NewCarName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*\S)\s+(19|20)\d\d$");
            return m.Success ? m.Groups[1].Value : name;
        }

        private static int Median(List<int> values)
        {
            var v = values.OrderBy(x => x).ToList();
            return v.Count % 2 == 1 ? v[v.Count / 2] : (int)Math.Round((v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0);
        }

        private static void ApplyScreenColors(ScreenLedResult sr, CarProfile p, CarProfile baseline, List<string> notes)
        {
            var layout = sr.Layout;
            var suggested = new string[layout.LedNumber + 1];
            // Without a measured strip-wide colour change, ATSR must keep each light's own colour.
            suggested[0] = sr.RedlineFromBlink || sr.SteadyAtLimiterRpm.HasValue ? "#00000000" : sr.RedlineColor ?? "#00000000";
            for (int i = 0; i < layout.LedNumber; i++)
            {
                var group = sr.ColorGroups.FirstOrDefault(g => g.Slots.Contains(i));
                bool unknown = sr.ColorUnknown != null && sr.ColorUnknown[i];
                suggested[i + 1] = layout.IsGap[i] ? "#00000000"
                    : group?.Hex ?? (unknown ? sr.RedlineColor ?? Red : "#00000000");
            }
            if (baseline == null)
                for (int left = 1; left <= layout.LedNumber / 2; left++)
                {
                    int right = layout.LedNumber + 1 - left;
                    var a = sr.MeasuredColors[left - 1];
                    var b = sr.MeasuredColors[right - 1];
                    if (suggested[left] == suggested[right] || layout.IsGap[left - 1] || layout.IsGap[right - 1] || a.Hue < 0 || b.Hue < 0 ||
                        HueGap(a.Hue, b.Hue) > MirrorColourDegrees ||
                        !p.LedRpm.Values.Any(row => row[left] > 0 && row[right] > 0 && Math.Abs(row[left] - row[right]) <= UniformRpm))
                        continue;
                    var mean = new LedColor((a.R + b.R) / 2, (a.G + b.G) / 2, (a.B + b.B) / 2);
                    string same = LedPalette.Classify(mean, out _);
                    if (LedPalette.Classify(a, out _) != same || LedPalette.Classify(b, out _) != same) continue;
                    suggested[left] = suggested[right] = same;
                }
            if (baseline == null && sr.ColorUnknown != null && sr.ColorUnknown.Any(u => u))
                notes.Add("LED " + string.Join(", ", Enumerable.Range(0, layout.LedNumber).Where(i => sr.ColorUnknown[i]).Select(i => i + 1)) +
                          " were given the redline colour, being the only colour they were ever seen in. Check them in the game.");

            if (baseline == null)
            {
                p.LedColor = suggested.ToList();
                notes.Add("LED colors come from the screen" + (sr.ColorsDoubtful ? ", and some were hard to tell apart" : "") + "; check them in the RPM LED Builder.");
                if (sr.SteadyAtLimiterRpm.HasValue && !sr.RedlineRpm.HasValue)
                    notes.Add("The redline was put at the limiter, about " + sr.SteadyAtLimiterRpm + " rpm, with a transparent colour (#00000000), " +
                              "so ATSR keeps the strip in its own colours there, as the game does.");
                return;
            }

            // Lights the screen shows in the very same colour can't be two colours in the file. PMR's
            // Viper has its outer pair identical on screen, while its file makes one green and the other
            // sky blue. Which of the file's colours is meant is settled by the screen: the one nearest.
            foreach (var group in sr.ColorGroups)
            {
                var slots = group.Slots.Where(i => i + 1 < p.LedColor.Count && !layout.IsGap[i] &&
                                                   !(sr.ColorUnknown != null && sr.ColorUnknown[i]) &&
                                                   !LedLayout.IsGapColor(p.LedColor[i + 1])).ToList();
                var inFile = slots.Select(i => p.LedColor[i + 1]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (inFile.Count < 2 || inFile.All(c => SameColour(c, inFile[0]))) continue;
                // Neighbouring colours are what a game washes together - the AMS2 M8's deep orange and red
                // look alike on screen - so the screen can't settle those. Only colours far apart, like
                // the Viper's green and sky blue, can't be one light seen two ways.
                var hues = inFile.Select(c => TryRgb(c, out var rgb) ? rgb.Hue : -1).ToList();
                if (hues.Any(h => h < 0)) continue;
                bool farApart = hues.Any(h1 => hues.Any(h2 => HueGap(h1, h2) > DistinctColourDegrees));
                if (!farApart) continue;
                string nearest = inFile.OrderBy(c => TryRgb(c, out var rgb) && rgb.Hue >= 0 && group.Measured.Hue >= 0
                                                         ? HueGap(rgb.Hue, group.Measured.Hue) : 999).First();
                var changed = slots.Where(i => !string.Equals(p.LedColor[i + 1], nearest, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (int i in changed) p.LedColor[i + 1] = nearest;
                notes.Add("LED " + string.Join(", ", slots.Select(i => i + 1)) + " show the same colour on screen (" + group.Measured +
                          "), but the repo file had them as " + string.Join(" and ", inFile) + ". LED " +
                          string.Join(", ", changed.Select(i => i + 1)) + " were grouped as " + nearest +
                          ", the file's own colour nearest what the screen shows. Check it in the game.");
            }

        }

        // Compare only after overrides and physical gaps have settled the exported colors.
        private static void AddFinalColorNotes(ScreenLedResult sr, CarProfile p,
                                               LocalProfileOverrideResult overrides, List<string> notes)
        {
            var layout = sr.Layout;
            var different = new List<string>();
            for (int i = 0; i < layout.LedNumber; i++)
            {
                if (layout.IsGap[i] || overrides.ColorSlots.Contains(i + 1)) continue;
                if (sr.ColorUnknown != null && sr.ColorUnknown[i]) continue;
                if (LedLayout.IsGapColor(p.LedColor[i + 1]))
                {
                    different.Add("LED " + (i + 1) + " is a gap in the file but lit on screen");
                    continue;
                }
                var group = sr.ColorGroups.FirstOrDefault(g => g.Slots.Contains(i));
                if (group != null && !SameColour(p.LedColor[i + 1], group.Hex))
                    different.Add("LED " + (i + 1) + " " + p.LedColor[i + 1] + " vs " + group.Hex);
            }
            // A strip that blinks in its own colours: ATSR paints every light in the redline colour above
            // the redline unless that colour is transparent, which keeps the strip's own.
            if (sr.RedlineFromBlink && p.LedColor.Count > 0 && !LedLayout.IsGapColor(p.LedColor[0]))
                notes.Add("In the game the strip keeps its own colours while it blinks. ATSR shows every light in the redline " +
                          "color, " + p.LedColor[0] + ", above the redline; a transparent redline color (#00000000) would keep " +
                          "the strip's own colours blinking instead, as the game does.");

            // The redline colour is ledColor[0], and it was never compared: two AMS2 cars turned out to
            // flash cyan where their files say blue, and nothing said so.
            if (!overrides.ColorSlots.Contains(0) && !string.IsNullOrEmpty(sr.RedlineColor) && p.LedColor.Count > 0 && !SameColour(p.LedColor[0], sr.RedlineColor))
                notes.Add("Above the redline the strip showed " + sr.RedlineMeasured + ", nearest " + sr.RedlineColor +
                          ", where the repo file's redline color is " + p.LedColor[0] + ". It was kept; change it by hand if the game agrees with the screen.");

            if (different.Count > 0)
                notes.Add("Colors on screen suggest " + string.Join(", ", different) + ". The repo file's colors were kept" +
                          (sr.ColorsDoubtful ? ", and the measured colors were close together anyway." : "; change them by hand if the game disagrees."));
        }

        /// <summary>Compares two #AARRGGBB or #RRGGBB colors by their RGB part only.</summary>
        /// <summary>
        /// Whether two file colours name the same colour. Exact hex is too strict: a file's dark orange
        /// #FF8C00 and the capture's orange #FF8000 are the same light, and flagging them buries the
        /// differences that matter - a red the screen shows as orange - in ones that don't.
        /// </summary>
        private static bool SameColour(string a, string b)
        {
            if (!TryRgb(a, out var x) || !TryRgb(b, out var y)) return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            if (x.Hue < 0 || y.Hue < 0) return x.Hue < 0 && y.Hue < 0;          // both grey or white
            double d = Math.Abs(x.Hue - y.Hue) % 360;
            return (d > 180 ? 360 - d : d) <= 12;
        }

        /// <summary>File colours further apart than this aren't one colour a game has washed out.</summary>
        private const double DistinctColourDegrees = 45;

        private static double HueGap(double a, double b)
        {
            double d = Math.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
        }

        private static bool TryRgb(string hex, out LedColor colour)
        {
            colour = default(LedColor);
            var c = (hex ?? "").TrimStart('#');
            if (c.Length == 8) c = c.Substring(2);
            if (c.Length != 6) return false;
            try
            {
                colour = new LedColor(Convert.ToInt32(c.Substring(0, 2), 16), Convert.ToInt32(c.Substring(2, 2), 16), Convert.ToInt32(c.Substring(4, 2), 16));
                return true;
            }
            catch (FormatException) { return false; }
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

        /// <summary>
        /// How far apart a value's own evidence may be before it's treated as not measured. A light
        /// seen going on somewhere within 150 rpm is worth writing down; one pinned down no better
        /// than "somewhere in the last 600 rpm" is a guess wearing a number.
        /// </summary>
        private const int MaxWindowRpm = 150;

        /// <summary>
        /// How far two gears may disagree and still count as the same set of lights, measured twice.
        /// A game that shows its lights a frame late reads each gear late by however far the revs
        /// climb in a frame, which differs from gear to gear: LMU's SC63 put identical lights 65 rpm
        /// apart across gears 2 to 4. Cars that really change per gear differ by hundreds.
        /// </summary>
        private const int UniformRpm = 80;

        /// <summary>
        /// Whether a measurement is tight enough to write into the file. A light that was never seen
        /// dark below its value has only an upper bound: the sweep started with it already lit, which
        /// is what a gear selected halfway up the rev range gives you.
        /// </summary>
        private static bool Trusted(LedThreshold led)
        {
            if (led == null || led.Inconsistent) return false;
            // Several identical midpoints do not make a threshold precise when every climb
            // crossed it through the same wide gap. Keep both the crossing bounds and the
            // repeatability check; otherwise 5000/6000 dark, 7000 lit looks trustworthy.
            if (led.Climbs > 1)
                return led.ClimbWidth <= MaxWindowRpm && led.ClimbSpread <= MaxWindowRpm;
            if (!led.HighestOff.HasValue || led.HighestOff.Value > led.LowestOn) return false;
            return led.LowestOn - led.HighestOff.Value <= MaxWindowRpm;
        }

        // ---------- shared ----------
        /// <summary>One line of a measured-thresholds table: the value, and where it came from.</summary>
        private static string LedLine(int index, LedThreshold led)
        {
            string window;
            if (led == null) window = "never lit";
            else if (led.Climbs > 1)
                window = led.Climbs + " climbs, agreeing within " + led.ClimbSpread + " rpm; widest crossing " +
                         led.ClimbWidth + " rpm" + (Trusted(led) ? "" : " - too loose, not used");
            else if (!Trusted(led) && led.HighestOff.HasValue)
                window = "dark to " + led.HighestOff + ", lit from " + led.LowestOn + " - too wide to use";
            else if (led.Inconsistent)
                window = "seen dark at " + led.HighestOff + " after lit at " + led.LowestOn + " (check)";
            else if (led.HighestOff.HasValue)
                window = "dark to " + led.HighestOff + ", lit from " + led.LowestOn;
            else
                window = "<= " + led.LowestOn + " (no dark sample below it)";
            if (led?.FallRpm != null) window += "; off at " + led.FallRpm + ", the two averaged";
            return string.Format(CultureInfo.InvariantCulture, "  LED {0,2}  {1,6}  {2}",
                index + 1, led?.Rpm.ToString(CultureInfo.InvariantCulture) ?? "-", window);
        }

        private static CarProfile NewProfile(CaptureSession s, CaptureSettings cfg, int leds, bool f1, List<string> notes)
        {
            var p = new CarProfile
            {
                CarName = NewCarName(string.IsNullOrEmpty(s.CarModel) ? s.CarId : s.CarModel),
                CarId = s.CarId,
                CarClass = s.CarClass ?? "",
                LedNumber = leds,
                RedlineBlinkInterval = f1 ? 50 : 0,
                LedColor = DefaultColors(leds).ToList(),
            };
            int top = Math.Max(Math.Max(s.Redline.TopGear, cfg.TopGearForNextExport), s.F1.Gears.Concat(s.IRacing.Gears).Concat(s.Marks.Gears).Select(CarProfile.GearRank).Where(r => r < 1000).DefaultIfEmpty(0).Max());
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
            // Final source reporting happens after any previous-capture rows have been restored.
            if (baseline != null) return;
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
