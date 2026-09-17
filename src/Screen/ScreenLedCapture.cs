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
        public bool ColorsDoubtful;
        /// <summary>RPM where the strip switches to its redline colour, if it does.</summary>
        public int? RedlineRpm;
        public int? RedlineHighestBelow, RedlineLowestAbove;
        public string RedlineColor;
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

            var above = _samples.Where((s, i) => redline[i] == true && s.Rpm > estimate)
                                .SelectMany(s => s.Colors).Where(c => c.Hue >= 0).ToList();
            if (above.Count > 0)
            {
                var mean = new LedColor((int)above.Average(c => c.R), (int)above.Average(c => c.G), (int)above.Average(c => c.B));
                var groups = LedPalette.Group(new[] { mean }, null, mean.Hue);
                result.RedlineColor = groups.Count > 0 ? groups[0].Hex : null;
            }
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
            for (int s = 0; s < layout.LedNumber; s++)
            {
                result.MeasuredColors[s] = counts[s] == 0
                    ? new LedColor(0, 0, 0)
                    : new LedColor((int)(sums[s, 0] / counts[s]), (int)(sums[s, 1] / counts[s]), (int)(sums[s, 2] / counts[s]));
            }

            double redlineHue = -1;
            if (result.RedlineColor != null)
            {
                var above = _samples.Where(s => result.RedlineLowestAbove.HasValue && s.Rpm > result.RedlineLowestAbove.Value)
                                    .SelectMany(s => s.Colors).Where(c => c.Hue >= 0).ToList();
                if (above.Count > 0) redlineHue = Median(above.Select(c => c.Hue).ToList());
            }
            result.ColorGroups = LedPalette.Group(result.MeasuredColors, layout.IsGap, redlineHue);
            result.ColorsDoubtful = LedPalette.Doubtful(result.ColorGroups);
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
