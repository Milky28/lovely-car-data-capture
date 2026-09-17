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

        /// <summary>Which slots are lit in one frame, index 0 = leftmost slot.</summary>
        public bool[] LitSlots(IEnumerable<LitBlob> blobs)
        {
            var lit = new bool[LedNumber];
            foreach (var b in blobs)
            {
                int slot = SlotOf(b.CenterX);
                if (slot >= 0) lit[slot] = true;
            }
            return lit;
        }
    }

    /// <summary>
    /// Works out the shape of the strip from the lights seen over a whole capture: how many there are,
    /// where they sit, and where the gaps are. The car file needs gaps as slots, because ATSR counts
    /// every slot when it maps a file onto a device's LEDs.
    /// </summary>
    internal sealed class StripCalibration
    {
        private readonly List<double> _centers = new List<double>();
        private int _frames;
        private int _mostInOneFrame;

        public void Add(IReadOnlyCollection<LitBlob> blobs)
        {
            if (blobs == null) return;
            _frames++;
            if (blobs.Count > _mostInOneFrame) _mostInOneFrame = blobs.Count;
            foreach (var b in blobs) _centers.Add(b.CenterX);
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
            // Group positions that are within a few pixels of each other: one group per physical light.
            var groups = new List<List<double>> { new List<double> { sorted[0] } };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i] - groups[groups.Count - 1].Last() <= 6) groups[groups.Count - 1].Add(sorted[i]);
                else groups.Add(new List<double> { sorted[i] });
            }

            // Over a capture, a position seen only once or twice is noise. A handful of frames - the
            // Test button reads one - has nothing to sift, so take what's there.
            int heaviest = groups.Max(g => g.Count);
            double needed = _frames >= 30 ? Math.Max(3, heaviest * 0.02) : 1;
            var lights = groups.Where(g => g.Count >= needed)
                               .Select(g => g.Average())
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

            var centers = new List<double> { lights[0] };
            var gaps = new List<bool> { false };
            for (int i = 1; i < lights.Count; i++)
            {
                int steps = (int)Math.Round((lights[i] - lights[i - 1]) / pitch);
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
