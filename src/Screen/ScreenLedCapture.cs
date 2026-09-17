using System;
using System.Collections.Generic;
using System.Linq;
using LovelyCarDataCapture.Capture;

namespace LovelyCarDataCapture.Screen
{
    /// <summary>What a screen capture learned about a car's rev lights.</summary>
    internal sealed class ScreenLedResult
    {
        public StripLayout Layout;
        public List<GearLedResult> Gears = new List<GearLedResult>();
        /// <summary>Colour measured for each slot below the redline; a black colour for gaps and unseen slots.</summary>
        public LedColor[] MeasuredColors;
        public List<LedPalette.ColorGroup> ColorGroups = new List<LedPalette.ColorGroup>();
        /// <summary>
        /// Slots never seen lit below the redline, so their own colour was never on screen: the last
        /// pair on a strip often lights exactly as the whole strip changes colour. Not the same as a gap.
        /// </summary>
        public bool[] ColorUnknown;
        public bool ColorsDoubtful;
        /// <summary>RPM where the strip switches to its redline colour, if it does.</summary>
        public int? RedlineRpm;
        public int? RedlineHighestBelow, RedlineLowestAbove;
        public string RedlineColor;
        /// <summary>What the strip actually measured above the redline, before it was matched to a colour.</summary>
        public LedColor RedlineMeasured;
        /// <summary>RPM where the strip changes colour a second time, for cars with a two-stage redline.</summary>
        public int? SecondStageRpm;
        public string SecondStageColor;
        public LedColor SecondStageMeasured;
        /// <summary>Length of one dark phase above the redline, in milliseconds; null when the lights don't blink.</summary>
        public int? BlinkIntervalMs;
        public bool BlinkSeen;
        public int Samples;
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Reads a car's rev lights off the screen: which lights are lit at which RPM, what colour they
    /// are, where the strip turns to its redline colour and whether it blinks there.
    /// </summary>
    /// <remarks>
    /// Samples are kept and worked through at the end, because the shape of the strip is only known
    /// once the lights have all been seen lit. The RPM thresholds themselves come from
    /// <see cref="LedWindowCapture"/>, the same climb-window logic the F1 games' rev lights use.
    /// </remarks>
    internal sealed class ScreenLedCapture
    {
        /// <summary>About twenty minutes at 60 fps; capture is meant to be a few sweeps, not a race.</summary>
        public const int MaxSamples = 72000;

        private struct Sample
        {
            public string Gear;
            public int Rpm;
            public long TimeMs;
            public double[] X;
            public LedColor[] Colors;
        }

        private readonly List<Sample> _samples = new List<Sample>();
        private readonly StripCalibration _calibration = new StripCalibration();

        public bool HasData => _samples.Count > 0;
        public int SampleCount => _samples.Count;
        public int MostLightsSeen => _calibration.MostLightsInOneFrame;

        public void Record(string gear, int rpm, long timeMs, IReadOnlyList<LitBlob> blobs)
        {
            if (string.IsNullOrEmpty(gear) || rpm <= 0 || blobs == null) return;
            if (_samples.Count >= MaxSamples) return;
            _calibration.Add((IReadOnlyCollection<LitBlob>)blobs);
            _samples.Add(new Sample
            {
                Gear = gear,
                Rpm = rpm,
                TimeMs = timeMs,
                X = blobs.Select(b => b.CenterX).ToArray(),
                Colors = blobs.Select(b => b.Color).ToArray(),
            });
        }

        public ScreenLedResult Result()
        {
            var result = new ScreenLedResult { Samples = _samples.Count };
            if (_samples.Count == 0)
            {
                result.Notes.Add("No frames were captured.");
                return result;
            }

            result.Layout = _calibration.Build(out string problem);
            if (result.Layout == null)
            {
                result.Notes.Add("The strip couldn't be made out: " + problem + ".");
                return result;
            }
            var layout = result.Layout;

            var lit = new bool[_samples.Count][];
            for (int i = 0; i < _samples.Count; i++)
            {
                lit[i] = new bool[layout.LedNumber];
                for (int b = 0; b < _samples[i].X.Length; b++)
                {
                    int slot = layout.SlotOf(_samples[i].X[b]);
                    if (slot >= 0) lit[i][slot] = true;
                }
            }

            // Colour of every slot in every frame, so the redline and the strip's own colours can be
            // told apart: -1 where that slot wasn't lit in that frame.
            var hues = new double[_samples.Count][];
            for (int i = 0; i < _samples.Count; i++)
            {
                hues[i] = Enumerable.Repeat(-1.0, layout.LedNumber).ToArray();
                for (int b = 0; b < _samples[i].X.Length; b++)
                {
                    int slot = layout.SlotOf(_samples[i].X[b]);
                    if (slot >= 0) hues[i][slot] = _samples[i].Colors[b].Hue;
                }
            }

            FindRedline(lit, hues, result);
            MeasureColors(lit, result);

            var window = new LedWindowCapture(layout.LedNumber);
            for (int i = 0; i < _samples.Count; i++) window.Record(_samples[i].Gear, _samples[i].Rpm, lit[i]);
            result.Gears = window.Gears.Select(window.Result).ToList();

            MeasureBlink(lit, result);

            int neverLit = Enumerable.Range(0, layout.LedNumber).Count(s => !layout.IsGap[s] && !lit.Any(f => f[s]));
            if (neverLit > 0) result.Notes.Add(neverLit + " light(s) were never seen lit; rev higher to reach them.");
            if (layout.GapCount > 0)
                result.Notes.Add("The strip has " + layout.GapCount + " gap(s) where the spacing leaves room but nothing ever lights.");
            return result;
        }

        /// <summary>Hue change that counts as a light no longer showing its own colour.</summary>
        private const double RedlineHueShift = 12.0;

        /// <summary>
        /// Above the redline a strip stops showing its own colours and turns one colour, usually red.
        /// Looking for "every lit light is the same colour" isn't enough, because early in a climb only
        /// the green end is lit. So each light's own colour is learned from the frames where the strip
        /// isn't full - it can't be in its redline state there - and the redline is where the lights
        /// that have a known colour stop showing it. The switch-on and switch-off RPMs bracket the
        /// value, and their midpoint cancels most of the lag between the game and the sample.
        /// </summary>
        private void FindRedline(bool[][] lit, double[][] hues, ScreenLedResult result)
        {
            var layout = result.Layout;
            int lights = layout.LedNumber - layout.GapCount;
            var ownHue = new double[layout.LedNumber];
            var ownCount = new int[layout.LedNumber];
            for (int i = 0; i < _samples.Count; i++)
            {
                if (lit[i].Count(v => v) >= lights) continue;   // full strip: may be the redline
                for (int s = 0; s < layout.LedNumber; s++)
                {
                    if (hues[i][s] < 0) continue;
                    ownHue[s] += hues[i][s];
                    ownCount[s]++;
                }
            }
            for (int s = 0; s < layout.LedNumber; s++) ownHue[s] = ownCount[s] > 0 ? ownHue[s] / ownCount[s] : -1;
            if (ownCount.Count(c => c > 0) < 2)
            {
                result.Notes.Add("The lights were never seen partly lit, so their own colours couldn't be told from the redline colour. Rev up slowly from idle.");
                return;
            }

            // A frame is in the redline state when the lights whose colour is known have all left it.
            var redline = new bool?[_samples.Count];
            for (int i = 0; i < _samples.Count; i++)
            {
                int known = 0, changed = 0;
                for (int s = 0; s < layout.LedNumber; s++)
                {
                    if (ownHue[s] < 0 || hues[i][s] < 0) continue;
                    known++;
                    if (Math.Abs(hues[i][s] - ownHue[s]) > RedlineHueShift) changed++;
                }
                if (known < 2) continue;
                // A majority, not all of them: lights that are already the redline colour don't change.
                redline[i] = changed * 2 > known;
            }

            var risingAt = new List<double>();
            var fallingAt = new List<double>();
            for (int i = 1; i < _samples.Count; i++)
            {
                if (!redline[i].HasValue || !redline[i - 1].HasValue) continue;
                if (_samples[i].Gear != _samples[i - 1].Gear) continue;
                if (redline[i].Value && !redline[i - 1].Value && _samples[i].Rpm > _samples[i - 1].Rpm)
                    risingAt.Add((_samples[i].Rpm + _samples[i - 1].Rpm) / 2.0);
                if (!redline[i].Value && redline[i - 1].Value && _samples[i].Rpm < _samples[i - 1].Rpm)
                    fallingAt.Add((_samples[i].Rpm + _samples[i - 1].Rpm) / 2.0);
            }
            if (risingAt.Count == 0 && fallingAt.Count == 0)
            {
                result.Notes.Add("The strip never changed colour, so no redline colour change was seen. " +
                                 "Either this car doesn't have one, or the limiter was never reached.");
                return;
            }

            double? up = risingAt.Count > 0 ? Median(risingAt) : (double?)null;
            double? down = fallingAt.Count > 0 ? Median(fallingAt) : (double?)null;
            double estimate = up.HasValue && down.HasValue ? (up.Value + down.Value) / 2 : (up ?? down.Value);
            result.RedlineRpm = RoundTo(estimate, 5);
            result.RedlineHighestBelow = (int)Math.Round(Math.Min(up ?? estimate, down ?? estimate));
            result.RedlineLowestAbove = (int)Math.Round(Math.Max(up ?? estimate, down ?? estimate));

            FindSecondStage(redline, estimate, result);

            // Only the first stage's own frames: averaging in a second stage would give a colour the
            // strip never shows, halfway between the two.
            double ceiling = result.SecondStageRpm ?? double.MaxValue;
            var above = _samples.Where((s, i) => redline[i] == true && s.Rpm > estimate && s.Rpm < ceiling)
                                .SelectMany(s => s.Colors).Where(c => c.Hue >= 0).ToList();
            if (above.Count > 0)
            {
                var mean = new LedColor((int)above.Average(c => c.R), (int)above.Average(c => c.G), (int)above.Average(c => c.B));
                result.RedlineColor = LedPalette.Classify(mean, out _);
                result.RedlineMeasured = mean;
            }
        }

        /// <summary>
        /// Some cars change colour twice: once at the redline and again close to the limiter. The file
        /// format keeps one redline, so this is only reported, but it explains a strip that doesn't
        /// look like the file on the wheel.
        /// </summary>
        private void FindSecondStage(bool?[] redline, double redlineRpm, ScreenLedResult result)
        {
            var aboveRedline = new List<int>();
            for (int i = 0; i < _samples.Count; i++)
                if (redline[i] == true && _samples[i].Rpm > redlineRpm) aboveRedline.Add(i);
            if (aboveRedline.Count < 30) return;

            // The first stage's colour, taken from the frames just above the redline.
            var lowest = aboveRedline.OrderBy(i => _samples[i].Rpm).Take(Math.Max(10, aboveRedline.Count / 5)).ToList();
            var firstHues = lowest.SelectMany(i => _samples[i].Colors).Select(c => c.Hue).Where(h => h >= 0).ToList();
            if (firstHues.Count == 0) return;
            double firstStage = Median(firstHues);

            var changedAt = new List<double>();
            var changedColors = new List<LedColor>();
            int previous = -1;
            foreach (int i in aboveRedline)
            {
                var hues = _samples[i].Colors.Select(c => c.Hue).Where(h => h >= 0).ToList();
                if (hues.Count < 3) { previous = i; continue; }
                bool changed = Math.Abs(Median(hues) - firstStage) > RedlineHueShift;
                if (changed)
                {
                    changedColors.AddRange(_samples[i].Colors.Where(c => c.Hue >= 0));
                    if (previous >= 0 && _samples[previous].Rpm < _samples[i].Rpm &&
                        Math.Abs(Median(_samples[previous].Colors.Select(c => c.Hue).Where(h => h >= 0).DefaultIfEmpty(firstStage).ToList()) - firstStage) <= RedlineHueShift)
                        changedAt.Add((_samples[i].Rpm + _samples[previous].Rpm) / 2.0);
                }
                previous = i;
            }
            if (changedAt.Count == 0 || changedColors.Count == 0) return;

            result.SecondStageRpm = RoundTo(Median(changedAt), 5);
            result.SecondStageMeasured = new LedColor((int)changedColors.Average(c => c.R),
                                                      (int)changedColors.Average(c => c.G),
                                                      (int)changedColors.Average(c => c.B));
            result.SecondStageColor = LedPalette.Classify(result.SecondStageMeasured, out _);
            result.Notes.Add("The strip changed colour a second time at about " + result.SecondStageRpm +
                             " rpm (" + result.SecondStageMeasured + "). A car file holds one redline, so only the first is in it; " +
                             "ATSR adds a second stage itself for some cars.");
        }

        /// <summary>Colour of each slot while the strip is showing its own colours, below the redline.</summary>
        private void MeasureColors(bool[][] lit, ScreenLedResult result)
        {
            var layout = result.Layout;
            var sums = new long[layout.LedNumber, 3];
            var counts = new int[layout.LedNumber];
            double ceiling = result.RedlineHighestBelow ?? result.RedlineRpm ?? double.MaxValue;

            for (int i = 0; i < _samples.Count; i++)
            {
                if (_samples[i].Rpm >= ceiling) continue;
                for (int b = 0; b < _samples[i].X.Length; b++)
                {
                    int slot = layout.SlotOf(_samples[i].X[b]);
                    if (slot < 0) continue;
                    var c = _samples[i].Colors[b];
                    if (c.Hue < 0) continue;
                    sums[slot, 0] += c.R; sums[slot, 1] += c.G; sums[slot, 2] += c.B;
                    counts[slot]++;
                }
            }

            result.MeasuredColors = new LedColor[layout.LedNumber];
            result.ColorUnknown = new bool[layout.LedNumber];
            for (int s = 0; s < layout.LedNumber; s++)
            {
                result.MeasuredColors[s] = counts[s] == 0
                    ? new LedColor(0, 0, 0)
                    : new LedColor((int)(sums[s, 0] / counts[s]), (int)(sums[s, 1] / counts[s]), (int)(sums[s, 2] / counts[s]));
                result.ColorUnknown[s] = counts[s] == 0 && !layout.IsGap[s] && lit.Any(f => f[s]);
            }
            var unknown = Enumerable.Range(0, layout.LedNumber).Where(s => result.ColorUnknown[s]).ToList();
            if (unknown.Count > 0)
                result.Notes.Add("LED " + string.Join(", ", unknown.Select(s => s + 1)) +
                                 " only ever lit at or above the redline, where the whole strip has already changed colour, " +
                                 "so their own colour couldn't be seen.");

            result.ColorGroups = LedPalette.Group(result.MeasuredColors, layout.IsGap);
            result.ColorsDoubtful = LedPalette.Doubtful(result.ColorGroups);

            // The flash is usually one of the strip's own colours. Naming it against them rather than
            // on its own keeps a washed red red, where alone it would read as orange; a flash that
            // matches nothing on the strip, like the blue some cars use, still gets named for itself.
            if (result.RedlineMeasured.Hue >= 0)
            {
                var same = result.ColorGroups.FirstOrDefault(g => Math.Abs(g.Hue - result.RedlineMeasured.Hue) <= LedPalette.SameColorDegrees);
                if (same != null) result.RedlineColor = same.Hex;
            }
            if (result.ColorsDoubtful)
                result.Notes.Add("Some colours are too close to tell apart on screen; check them against the game.");
        }

        /// <summary>Above the redline, a blinking strip goes fully dark in some frames. Its dark phase is the interval.</summary>
        private void MeasureBlink(bool[][] lit, ScreenLedResult result)
        {
            if (!result.RedlineRpm.HasValue) return;
            var dark = new List<int>();
            long darkFrom = -1;
            long lastTime = -1;
            int above = 0;
            for (int i = 0; i < _samples.Count; i++)
            {
                if (_samples[i].Rpm <= result.RedlineRpm.Value) { darkFrom = -1; continue; }
                above++;
                bool anyLit = lit[i].Any(v => v);
                if (!anyLit && darkFrom < 0) darkFrom = _samples[i].TimeMs;
                if (anyLit && darkFrom >= 0)
                {
                    dark.Add((int)(_samples[i].TimeMs - darkFrom));
                    darkFrom = -1;
                }
                lastTime = _samples[i].TimeMs;
            }
            result.BlinkSeen = dark.Count > 1;
            if (result.BlinkSeen)
                result.BlinkIntervalMs = (int)Math.Round(Median(dark.Select(d => (double)d).ToList()));
            else if (above > 30)
                result.Notes.Add("The lights stayed on above the redline in all " + above +
                                 " frames there, so the strip doesn't blink (redlineBlinkInterval 0).");
            if (lastTime < 0) result.Notes.Add("The redline was never held long enough to check for blinking.");
        }

        private static double Median(List<double> values)
        {
            var v = values.OrderBy(x => x).ToList();
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0;
        }

        private static int RoundTo(double value, int step) => (int)(Math.Round(value / step) * step);
    }
}
