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
        /// Irregular/Trivial layouts are treated as Rising. Mirrored layouts pair LEDs by physical
        /// position (1 with N, 2 with N-1, …) so the row reads the same from both ends, which is what
        /// ATSR checks for.
        /// </summary>
        public static int[] Generate(int ledNumber, bool[] isGap, LayoutKind layout, int redline, double first, double last)
        {
            var row = new int[ledNumber + 1];
            row[0] = redline;
            var active = Enumerable.Range(1, ledNumber).Where(i => isGap == null || i >= isGap.Length || !isGap[i]).ToList();
            int n = active.Count;
            if (n == 0) return row;

            bool mirrored = layout == LayoutKind.OutsideIn || layout == LayoutKind.InsideOut;
            int PairOf(int led) => Math.Min(led - 1, ledNumber - led);
            var pairs = mirrored ? active.Select(PairOf).Distinct().OrderBy(x => x).ToList() : null;
            int stages = mirrored ? pairs.Count : n;
            for (int p = 0; p < n; p++)
            {
                int s;
                switch (layout)
                {
                    case LayoutKind.Falling: s = n - 1 - p; break;
                    case LayoutKind.OutsideIn: s = pairs.IndexOf(PairOf(active[p])); break;
                    case LayoutKind.InsideOut: s = stages - 1 - pairs.IndexOf(PairOf(active[p])); break;
                    default: s = p; break;
                }
                double t = stages > 1 ? s / (double)(stages - 1) : 1.0;
                row[active[p]] = (int)Math.Round(first + (last - first) * t);
            }
            return row;
        }

        /// <summary>
        /// Gaps in the physical strip: LEDs whose color is black (RGB 000000) at any alpha. ATSR never
        /// lights those, whatever their RPM values.
        /// </summary>
        public static bool[] Gaps(CarProfile profile)
        {
            var gaps = new bool[profile.LedNumber + 1];
            for (int i = 1; i <= profile.LedNumber && i < profile.LedColor.Count; i++) gaps[i] = IsGapColor(profile.LedColor[i]);
            return gaps;
        }

        public static bool IsGapColor(string color)
        {
            if (string.IsNullOrEmpty(color) || color[0] != '#') return false;
            var hex = color.Substring(1);
            if (hex.Length == 8) hex = hex.Substring(2);
            return hex.Length == 6 && hex == "000000";
        }

        public enum AtsrLayout { LeftToRight, SideToCenter, Rejected }

        /// <summary>
        /// ATSR's rule, applied to the last gear's LED values (gaps included): an exact mirror is side-to-centre,
        /// anything else left-to-right, and a row that is both non-decreasing (ignoring 0s) and an exact mirror
        /// makes ATSR drop the file and use its generic presets.
        /// </summary>
        public static AtsrLayout AtsrLayoutOf(IReadOnlyList<int> ledValues)
        {
            bool ascending = true;
            for (int i = 1; i < ledValues.Count; i++)
            {
                if (ledValues[i] != 0 && ledValues[i] < ledValues[i - 1]) ascending = false;
            }
            int half = ledValues.Count / 2;
            bool symmetric = true;
            for (int j = 0; j < half; j++)
            {
                if (ledValues[j] != ledValues[ledValues.Count - 1 - j]) symmetric = false;
            }
            if (ascending && symmetric) return AtsrLayout.Rejected;
            return symmetric ? AtsrLayout.SideToCenter : AtsrLayout.LeftToRight;
        }
    }
}
