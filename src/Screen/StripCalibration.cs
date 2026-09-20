using System;
using System.Collections.Generic;
using System.Linq;

namespace LovelyCarDataCapture.Screen
{
    /// <summary>Where each light sits on screen, and where the strip has gaps between them.</summary>
    internal sealed class StripLayout
    {
        public StripLayout(double[] slotCenters, bool[] isGap, double pitch)
        {
            SlotCenters = slotCenters;
            IsGap = isGap;
            Pitch = pitch;
        }

        /// <summary>Centre of every slot left to right, gaps included (their centre is where a light would sit).</summary>
        public double[] SlotCenters { get; }
        /// <summary>True where the strip has a gap: nothing was ever lit there, but the spacing leaves room.</summary>
        public bool[] IsGap { get; }
        /// <summary>Typical distance between neighbouring slots, in pixels.</summary>
        public double Pitch { get; }
        public int LedNumber => SlotCenters.Length;
        public int GapCount => IsGap.Count(g => g);

        /// <summary>Slot a lit light at this x belongs to, or -1 when it's too far from every slot.</summary>
        public int SlotOf(double x)
        {
            int best = -1;
            double bestDistance = Pitch * 0.5;
            for (int i = 0; i < SlotCenters.Length; i++)
            {
                double d = Math.Abs(SlotCenters[i] - x);
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return best;
        }

    }

    /// <summary>
    /// Works out the shape of the strip from the lights seen over a whole capture: how many there are,
    /// where they sit, and where the gaps are. The car file needs gaps as slots, because ATSR counts
    /// every slot when it maps a file onto a device's LEDs.
    /// </summary>
    internal sealed class StripCalibration
    {
        // Real LED centers drift a few pixels; AC wheel occlusions can also leave isolated centers
        // between LEDs. Only positions near a repeatedly seen center may join a light's group.
        private const double PositionReachPx = 6;
        // Lanzo's two bank separators are narrower than a missing LED pitch. Require a
        // matching mirrored pair between banks, with ordinary spacing on both sides.
        // Outlying single lights on a curved dash do not establish a bank separator.
        private const double BankGapRatio = 1.3;
        private const double OrdinarySpacingRatio = 1.15;
        private const double MatchingGapTolerance = 0.1;
        // A merged pair sits halfway across one normal spacing; a few repeated frames
        // establish the separate neighbours without assuming a maximum physical LED count.
        private const double MergedPairSpanTolerance = 0.2;
        private const double MergedMidpointTolerance = 0.1;
        private const int MergedPairFrames = 3;
        private readonly List<double> _centers = new List<double>();
        private readonly List<double[]> _observations = new List<double[]>();
        private int _frames;
        private int _mostInOneFrame;

        public void Add(IReadOnlyCollection<LitBlob> blobs)
        {
            if (blobs == null) return;
            _frames++;
            if (blobs.Count > _mostInOneFrame) _mostInOneFrame = blobs.Count;
            foreach (var b in blobs) _centers.Add(b.CenterX);
            _observations.Add(blobs.Select(b => b.CenterX).ToArray());
        }

        public int Frames => _frames;
        public int MostLightsInOneFrame => _mostInOneFrame;

        /// <summary>
        /// Groups the positions seen into lights and fills in the gaps between them. Returns null when
        /// too little was seen to tell (nothing lit, or a single light with no spacing to measure).
        /// </summary>
        public StripLayout Build(out string problem)
        {
            problem = null;
            if (_centers.Count == 0)
            {
                problem = "no lit lights were found in the capture region";
                return null;
            }

            var sorted = _centers.OrderBy(x => x).ToList();
            if (_frames >= 30)
            {
                var positions = sorted.GroupBy(x => Math.Round(x)).ToList();
                double anchorNeeded = Math.Max(3, positions.Max(g => g.Count()) * 0.02);
                var anchors = positions.Where(g => g.Count() >= anchorNeeded).Select(g => g.Key).ToList();
                // Filter before grouping: a chain of rare positions must not connect adjacent LEDs
                // and make their real positions look like empty slots in the strip.
                sorted = sorted.Where(x => anchors.Any(a => Math.Abs(x - a) <= PositionReachPx)).ToList();
                if (sorted.Count == 0)
                {
                    problem = "the lights were seen too rarely to place them";
                    return null;
                }
            }
            // Group positions that are within a few pixels of each other: one group per physical light.
            var groups = new List<List<double>> { new List<double> { sorted[0] } };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i] - groups[groups.Count - 1].Last() <= PositionReachPx) groups[groups.Count - 1].Add(sorted[i]);
                else groups.Add(new List<double> { sorted[i] });
            }

            // Over a capture, a position seen only once or twice is noise. A handful of frames - the
            // Test button reads one - has nothing to sift, so take what's there.
            int heaviest = groups.Max(g => g.Count);
            double needed = _frames >= 30 ? Math.Max(3, heaviest * 0.02) : 1;
            var kept = groups.Where(g => g.Count >= needed).ToList();

            // Two positions far closer together than the strip's spacing are one light seen two ways:
            // PMR's Viper puts its outer lights 8 px apart depending on how far they've come on, with
            // the lights 42 px apart. A fixed few pixels can't tell that from two lights.
            if (kept.Count >= 3)
            {
                var between = new List<double>();
                for (int i = 1; i < kept.Count; i++) between.Add(kept[i].Average() - kept[i - 1].Average());
                double typical = Median(between.Where(d => d > 12).ToList());
                if (typical > 0)
                {
                    var merged = new List<List<double>> { new List<double>(kept[0]) };
                    for (int i = 1; i < kept.Count; i++)
                    {
                        if (kept[i].Average() - merged[merged.Count - 1].Average() < typical * 0.4) merged[merged.Count - 1].AddRange(kept[i]);
                        else merged.Add(new List<double>(kept[i]));
                    }
                    kept = merged;
                    // Bloom can alternate between two real LEDs and their merged midpoint.
                    // Require repeated simultaneous neighbours and no coexistence with the midpoint:
                    // a genuinely separate light must not be discarded just for close spacing.
                    for (int i = kept.Count - 2; i > 0; i--)
                    {
                        double left = kept[i - 1].Average(), middle = kept[i].Average(), right = kept[i + 1].Average();
                        if (Math.Abs((right - left) - typical) > typical * MergedPairSpanTolerance ||
                            Math.Abs(middle - (left + right) / 2) > typical * MergedMidpointTolerance) continue;
                        int pairs = 0;
                        bool coexists = false;
                        foreach (var frame in _observations)
                        {
                            bool l = frame.Any(x => Math.Abs(x - left) <= PositionReachPx);
                            bool m = frame.Any(x => Math.Abs(x - middle) <= PositionReachPx);
                            bool r = frame.Any(x => Math.Abs(x - right) <= PositionReachPx);
                            if (m && (l || r)) { coexists = true; break; }
                            if (l && r) pairs++;
                        }
                        if (!coexists && pairs >= MergedPairFrames) kept.RemoveAt(i);
                    }
                }
            }
            var lights = kept.Select(g => g.Average())
                             .OrderBy(x => x)
                             .ToList();
            if (lights.Count == 0)
            {
                problem = "the lights were seen too rarely to place them";
                return null;
            }
            if (lights.Count == 1)
            {
                // A single light: no spacing to measure, so it's the whole strip.
                return new StripLayout(new[] { lights[0] }, new[] { false }, 1);
            }

            var spacings = new List<double>();
            for (int i = 1; i < lights.Count; i++) spacings.Add(lights[i] - lights[i - 1]);
            double pitch = Median(spacings.Where(s => s <= Median(spacings) * 1.4).ToList());
            var bankGaps = Enumerable.Range(0, spacings.Count).Where(i => spacings[i] >= pitch * BankGapRatio).ToList();
            bool pairedBankGaps = bankGaps.Count == 2 && bankGaps[0] + bankGaps[1] == spacings.Count - 1 &&
                bankGaps[0] > 0 && bankGaps[1] < spacings.Count - 1 &&
                bankGaps[1] - bankGaps[0] > 1 &&
                Math.Abs(spacings[bankGaps[0]] - spacings[bankGaps[1]]) <= pitch * MatchingGapTolerance &&
                spacings.Where((s, i) => !bankGaps.Contains(i)).All(s => s <= pitch * OrdinarySpacingRatio);

            var centers = new List<double> { lights[0] };
            var gaps = new List<bool> { false };
            for (int i = 1; i < lights.Count; i++)
            {
                int steps = (int)Math.Round((lights[i] - lights[i - 1]) / pitch);
                if (pairedBankGaps && bankGaps.Contains(i - 1)) steps = Math.Max(2, steps);
                if (steps < 1) steps = 1;
                for (int s = 1; s < steps; s++)
                {
                    centers.Add(lights[i - 1] + (lights[i] - lights[i - 1]) * s / steps);
                    gaps.Add(true);
                }
                centers.Add(lights[i]);
                gaps.Add(false);
            }
            return new StripLayout(centers.ToArray(), gaps.ToArray(), pitch);
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            var v = values.OrderBy(x => x).ToList();
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0;
        }
    }
}
