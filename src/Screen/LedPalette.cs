using System;
using System.Collections.Generic;
using System.Linq;

namespace LovelyCarDataCapture.Screen
{
    /// <summary>
    /// Turns colours measured off the screen into car-file colours.
    /// </summary>
    /// <remarks>
    /// A light rendered in a game is never quite its own colour: tone mapping, bloom and the cockpit's
    /// lighting wash it towards white and squeeze the hues together, so a pure green LED can measure as
    /// rgb(138,177,106), a hue of 93° rather than 120°. Matching each colour to the nearest one on its
    /// own would then call a washed red "orange".
    /// <para>
    /// What survives is the order: a strip runs green to red, and no two of its colours are the same.
    /// So the colours are named together, each one taking a different entry from the ladder and keeping
    /// them in the order they were measured in, at the smallest total distance. A washed red stays red
    /// because orange has already been spoken for by the colour above it.
    /// </para>
    /// </remarks>
    internal static class LedPalette
    {
        private sealed class Step
        {
            public Step(string name, string hex, double hue) { Name = name; Hex = hex; Hue = hue; }
            public readonly string Name, Hex;
            public readonly double Hue;
        }

        private static readonly Step[] Ladder =
        {
            new Step("red", "#FFFF0000", 0),
            new Step("orange", "#FFFF8000", 30),
            new Step("yellow", "#FFFFFF00", 60),
            new Step("green", "#FF00FF00", 120),
            new Step("cyan", "#FF00FFFF", 180),
            new Step("blue", "#FF0000FF", 240),
            new Step("purple", "#FFFF00FF", 300),
        };

        /// <summary>Hue spread within which two lights count as the same colour.</summary>
        public const double SameColorDegrees = 6.0;

        /// <summary>
        /// Gap in the sorted hues that starts a new colour. Colours a game renders next to each other
        /// (its yellow and its orange) can end up only a few degrees apart, so this is tighter than
        /// <see cref="SameColorDegrees"/>.
        /// </summary>
        public const double NeighbourDegrees = 4.5;

        /// <summary>A colour used by one or more lights on the strip.</summary>
        internal sealed class ColorGroup
        {
            public double Hue;
            public LedColor Measured;
            public string Hex;
            public string Name;
            /// <summary>Slots (0-based) using this colour.</summary>
            public List<int> Slots = new List<int>();
        }

        /// <summary>
        /// Colours a redline flash can be named as. It is named on its own rather than against the
        /// strip, so nothing competes for a rung and the scale can be finer than the strip's: two AMS2
        /// cars flash within ten degrees of each other and look plainly different, one cyan, one a
        /// lighter blue.
        /// </summary>
        private static readonly Step[] FlashColors =
        {
            new Step("red", "#FFFF0000", 0),
            new Step("orange", "#FFFF8000", 30),
            new Step("yellow", "#FFFFFF00", 60),
            new Step("green", "#FF00FF00", 120),
            new Step("spring green", "#FF00FF80", 150),
            new Step("cyan", "#FF00FFFF", 180),
            new Step("light blue", "#FF00BFFF", 195),
            new Step("dodger blue", "#FF1E90FF", 210),
            new Step("blue", "#FF0000FF", 240),
            new Step("violet", "#FF8000FF", 270),
            new Step("purple", "#FFFF00FF", 300),
        };

        /// <summary>Names one colour on its own, for the redline flash. Nothing constrains it, so it's the nearest.</summary>
        public static string Classify(LedColor color, out string name)
        {
            double hue = color.Hue;
            if (hue < 0) { name = "white"; return "#FFFFFFFF"; }
            var best = FlashColors.OrderBy(step => Distance(hue, step.Hue)).First();
            name = best.Name;
            return best.Hex;
        }

        /// <summary>Groups the measured colours by hue and names them.</summary>
        public static List<ColorGroup> Group(IList<LedColor> perSlot, IList<bool> isGap)
        {
            var lights = new List<Tuple<int, double>>();
            for (int i = 0; i < perSlot.Count; i++)
            {
                if (isGap != null && i < isGap.Count && isGap[i]) continue;
                if (perSlot[i].Hue >= 0) lights.Add(Tuple.Create(i, perSlot[i].Hue));
            }
            if (lights.Count == 0) return new List<ColorGroup>();

            // Split where the hues step apart rather than growing groups slot by slot: with a running
            // mean, whether two close colours end up together would depend on the order they're seen in.
            lights = lights.OrderBy(l => l.Item2).ToList();
            var groups = new List<ColorGroup>();
            ColorGroup current = null;
            double previous = 0;
            foreach (var light in lights)
            {
                if (current == null || light.Item2 - previous > NeighbourDegrees)
                {
                    current = new ColorGroup();
                    groups.Add(current);
                }
                current.Slots.Add(light.Item1);
                previous = light.Item2;
            }
            foreach (var g in groups)
            {
                g.Hue = g.Slots.Average(s => perSlot[s].Hue);
                g.Measured = new LedColor((int)g.Slots.Average(s => perSlot[s].R),
                                          (int)g.Slots.Average(s => perSlot[s].G),
                                          (int)g.Slots.Average(s => perSlot[s].B));
                g.Slots.Sort();
            }

            var ordered = InStripOrder(groups);
            Name(ordered);
            return ordered;
        }

        /// <summary>
        /// The groups in the order the ladder runs, red first. Hue is a circle and red sits across its
        /// join: ACC's red measures 358°, which sorted plainly comes after green and gets named purple.
        /// So the circle is cut at its widest empty stretch - the side of it the strip doesn't use.
        /// </summary>
        private static List<ColorGroup> InStripOrder(List<ColorGroup> groups)
        {
            var sorted = groups.OrderBy(g => g.Hue).ToList();
            if (sorted.Count < 2) return sorted;
            int cutAfter = sorted.Count - 1;                 // default: the join itself, no rotation
            double widest = sorted[0].Hue + 360 - sorted[sorted.Count - 1].Hue;
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                double gap = sorted[i + 1].Hue - sorted[i].Hue;
                if (gap > widest) { widest = gap; cutAfter = i; }
            }
            return sorted.Skip(cutAfter + 1).Concat(sorted.Take(cutAfter + 1)).ToList();
        }

        /// <summary>
        /// Gives each group a different colour from the ladder, in hue order, at the smallest total
        /// distance. With more groups than the ladder has entries there's nothing to share out, so each
        /// takes its nearest.
        /// </summary>
        private static void Name(List<ColorGroup> groups)
        {
            if (groups.Count > Ladder.Length)
            {
                foreach (var g in groups) g.Hex = Classify(g.Measured, out g.Name);
                return;
            }

            int n = groups.Count, m = Ladder.Length;
            // best[i, j]: cheapest way to name groups i.. using ladder entries j..
            var best = new double[n + 1, m + 1];
            var pick = new int[n + 1, m + 1];
            for (int j = 0; j <= m; j++) best[n, j] = 0;
            for (int i = n - 1; i >= 0; i--)
            {
                best[i, m] = double.MaxValue / 4;
                for (int j = m - 1; j >= 0; j--)
                {
                    double take = Distance(groups[i].Hue, Ladder[j].Hue) + best[i + 1, j + 1];   // Distance goes the short way round
                    double skip = best[i, j + 1];
                    if (take <= skip) { best[i, j] = take; pick[i, j] = j; }
                    else { best[i, j] = skip; pick[i, j] = pick[i, j + 1]; }
                }
            }

            int at = 0;
            for (int i = 0; i < n; i++)
            {
                int chosen = pick[i, at];
                groups[i].Name = Ladder[chosen].Name;
                groups[i].Hex = Ladder[chosen].Hex;
                at = chosen + 1;
            }
        }

        /// <summary>Distance between two hues the short way round the circle.</summary>
        private static double Distance(double a, double b)
        {
            double d = Math.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
        }

        /// <summary>True when two groups sit so close that the split between them is doubtful.</summary>
        public static bool Doubtful(List<ColorGroup> groups)
        {
            for (int i = 1; i < groups.Count; i++)
            {
                if (groups[i].Hex == groups[i - 1].Hex) return true;
                if (groups[i].Hue - groups[i - 1].Hue < 10) return true;
            }
            return false;
        }
    }
}
