using System;
using System.Collections.Generic;
using System.Linq;

namespace LovelyCarDataCapture.Profile
{
    internal enum LayoutKind { Rising, Falling, OutsideIn, InsideOut, Irregular, Trivial }

    internal static class LedLayout
    {
        // Same classification as the RPM LED Builder. Gaps (0) are ignored.
        // Rising/Falling: one end to the other; OutsideIn/InsideOut: mirrored halves meeting in (or starting from) the middle.
        public static LayoutKind Classify(IReadOnlyList<int> row)
        {
            var vals = row.Skip(1).Where(v => v > 0).ToList();
            if (vals.Count < 2) return LayoutKind.Trivial;
            var dirs = new List<int>();
            for (int i = 1; i < vals.Count; i++)
            {
                if (vals[i] != vals[i - 1]) dirs.Add(vals[i] > vals[i - 1] ? 1 : -1);
            }
            if (dirs.Count == 0) return LayoutKind.Trivial;
            int turns = 0;
            for (int j = 1; j < dirs.Count; j++) if (dirs[j] != dirs[j - 1]) turns++;
            if (turns == 0) return dirs[0] > 0 ? LayoutKind.Rising : LayoutKind.Falling;
            if (turns == 1) return dirs[0] > 0 ? LayoutKind.OutsideIn : LayoutKind.InsideOut;
            return LayoutKind.Irregular;
        }

        /// <summary>
        /// Builds [redline, LED1..N] with thresholds spread evenly from <paramref name="first"/> to
        /// <paramref name="last"/> across the active LEDs, following <paramref name="layout"/>.
        /// Irregular/Trivial layouts are treated as Rising.
        /// </summary>
        public static int[] Generate(int ledNumber, bool[] isGap, LayoutKind layout, int redline, double first, double last)
        {
            var row = new int[ledNumber + 1];
            row[0] = redline;
            var active = Enumerable.Range(1, ledNumber).Where(i => isGap == null || i >= isGap.Length || !isGap[i]).ToList();
            int n = active.Count;
            if (n == 0) return row;

            bool mirrored = layout == LayoutKind.OutsideIn || layout == LayoutKind.InsideOut;
            int stages = mirrored ? (n + 1) / 2 : n;
            for (int p = 0; p < n; p++)
            {
                int s;
                switch (layout)
                {
                    case LayoutKind.Falling: s = n - 1 - p; break;
                    case LayoutKind.OutsideIn: s = Math.Min(p, n - 1 - p); break;
                    case LayoutKind.InsideOut: s = stages - 1 - Math.Min(p, n - 1 - p); break;
                    default: s = p; break;
                }
                double t = stages > 1 ? s / (double)(stages - 1) : 1.0;
                row[active[p]] = (int)Math.Round(first + (last - first) * t);
            }
            return row;
        }

        /// <summary>LEDs that are 0 in every gear are gaps in the physical strip.</summary>
        public static bool[] Gaps(CarProfile profile)
        {
            var gaps = new bool[profile.LedNumber + 1];
            for (int i = 1; i <= profile.LedNumber; i++)
            {
                gaps[i] = profile.GearOrder.Count > 0 && profile.GearOrder.All(g =>
                    !profile.LedRpm.TryGetValue(g, out var row) || i >= row.Length || row[i] <= 0);
            }
            return gaps;
        }
    }
}
