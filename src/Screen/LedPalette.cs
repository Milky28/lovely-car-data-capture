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
        /// <summary>PMR's repeated green, red and blue lights keep their colour when one channel is clearly dominant.</summary>
        private const double PrimaryChannelDominance = 1.6;

        /// <summary>
        /// Dominance that settles the question on its own, whatever the hue ladder had to call the
        /// colour. A game's orange keeps a strong second channel - RaceRoom's Porsche Cup measures
        /// rgb(229,130,20), green at 0.57 of red - while a warm red does not: PMR's AMG GT4 centre
        /// pair measures rgb(189,53,0) and rgb(182,65,0), green at 0.28 and 0.36 of red.
        /// </summary>
        private const double StrongChannelDominance = 2.2;

        /// <summary>PMR's paired yellow lights can differ in hue while red and green remain this close.</summary>
        private const double YellowChannelBalance = 0.08;

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

        /// <summary>
        /// How far a group's hue may sit from the ladder colour it was named, before that name counts
        /// as forced by the need to keep colours apart rather than measured.
        /// </summary>
        private const double NamedHueDegrees = 12.0;

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
        public static List<ColorGroup> Group(IList<LedColor> perSlot, IList<bool> isGap, IList<LedColor> rawPerSlot = null)
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
            // Red sits on both ends of the scale: a group just below 360 and one just above 0 are one.
            if (groups.Count > 1 && lights[0].Item2 + 360 - lights[lights.Count - 1].Item2 <= NeighbourDegrees)
            {
                groups[0].Slots.AddRange(groups[groups.Count - 1].Slots);
                groups.RemoveAt(groups.Count - 1);
            }
            // Neighbours that read alike over every frame stay together, whatever their clean readings
            // say. The first light is only ever clean when it's alone on the strip, without the bloom
            // of the rest: PMR's AMR Vantage GT4 lights 1 and 2 both read 93 over every frame, but
            // 109 and 94 when clean, and were split into green and yellow.
            if (rawPerSlot != null)
            {
                var order = lights.Select(l => l.Item1).OrderBy(s => s).ToList();
                for (int k = 1; k < order.Count; k++)
                {
                    int a = order[k - 1], b = order[k];
                    if (Enumerable.Range(a + 1, b - a - 1).Any(s => isGap == null || s >= isGap.Count || !isGap[s])) continue;
                    if (rawPerSlot[a].Hue < 0 || rawPerSlot[b].Hue < 0 ||
                        Distance(rawPerSlot[a].Hue, rawPerSlot[b].Hue) > SameColorDegrees) continue;
                    var ga = groups.First(g => g.Slots.Contains(a));
                    var gb = groups.First(g => g.Slots.Contains(b));
                    if (ga == gb) continue;
                    ga.Slots.AddRange(gb.Slots);
                    groups.Remove(gb);
                }
            }
            foreach (var g in groups)
            {
                g.Hue = CircularMean(g.Slots.Select(s => perSlot[s].Hue));
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

        private static double CircularMean(IEnumerable<double> hues)
        {
            double x = 0, y = 0;
            foreach (var h in hues)
            {
                x += Math.Cos(h * Math.PI / 180);
                y += Math.Sin(h * Math.PI / 180);
            }
            double mean = Math.Atan2(y, x) * 180 / Math.PI;
            return mean < 0 ? mean + 360 : mean;
        }

        /// <summary>
        /// Gives each group a different colour from the ladder, in hue order, at the smallest total
        /// distance. The ladder is a circle as well, so it's tried from every starting colour: a strip
        /// of blue, yellow and red (LMU's SC63) runs blue-red-yellow round the circle from where it's
        /// cut, and read from red first the blue would have to be named red. With more groups than the
        /// ladder has entries there's nothing to share out, so each takes its nearest.
        /// </summary>
        private static void Name(List<ColorGroup> groups)
        {
            if (groups.Count > Ladder.Length)
            {
                foreach (var g in groups) g.Hex = Classify(g.Measured, out g.Name);
                return;
            }

            Step[] bestLadder = null;
            int[] bestPicks = null;
            double bestCost = double.MaxValue;
            for (int start = 0; start < Ladder.Length; start++)
            {
                var ladder = Ladder.Skip(start).Concat(Ladder.Take(start)).ToArray();
                double cost = Assign(groups, ladder, out int[] picks);
                if (cost < bestCost - 1e-9) { bestCost = cost; bestLadder = ladder; bestPicks = picks; }
            }
            for (int i = 0; i < groups.Count; i++)
            {
                groups[i].Name = bestLadder[bestPicks[i]].Name;
                groups[i].Hex = bestLadder[bestPicks[i]].Hex;
                // PMR renders same-colour LEDs as separate shades. The distinct-colour ladder then
                // calls green yellow, red orange, and blue cyan despite a clear dominant channel.
                // Only step in when the name the ladder had to give is nowhere near the measured hue:
                // a real orange (RaceRoom's Porsche Cup, hue 31) has a dominant red channel too, and
                // was being called red.
                var measured = groups[i].Measured;
                if (Distance(groups[i].Hue, bestLadder[bestPicks[i]].Hue) <= NamedHueDegrees &&
                    !StronglyDominant(measured)) continue;
                if (measured.G > measured.R * PrimaryChannelDominance && measured.G > measured.B * PrimaryChannelDominance)
                {
                    groups[i].Name = "green";
                    groups[i].Hex = "#FF00FF00";
                }
                else if (measured.R > measured.G * PrimaryChannelDominance && measured.R > measured.B * PrimaryChannelDominance)
                {
                    groups[i].Name = "red";
                    groups[i].Hex = "#FFFF0000";
                }
                else if (measured.B > measured.R * PrimaryChannelDominance && measured.B > measured.G * PrimaryChannelDominance)
                {
                    groups[i].Name = "blue";
                    groups[i].Hex = "#FF0000FF";
                }
                else if (Math.Abs(measured.R - measured.G) <= Math.Max(measured.R, measured.G) * YellowChannelBalance &&
                         Math.Min(measured.R, measured.G) > measured.B * PrimaryChannelDominance)
                {
                    groups[i].Name = "yellow";
                    groups[i].Hex = "#FFFFFF00";
                }
            }
        }

        /// <summary>Cheapest in-order assignment of groups to a ladder; returns its total distance.</summary>
        private static double Assign(List<ColorGroup> groups, Step[] Ladder, out int[] picks)
        {

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

            picks = new int[n];
            int at = 0;
            for (int i = 0; i < n; i++)
            {
                picks[i] = pick[i, at];
                at = picks[i] + 1;
            }
            return best[0, 0];
        }

        /// <summary>True when one primary channel is far enough ahead to name the colour by itself.</summary>
        private static bool StronglyDominant(LedColor c) =>
            c.R > c.G * StrongChannelDominance && c.R > c.B * StrongChannelDominance ||
            c.G > c.R * StrongChannelDominance && c.G > c.B * StrongChannelDominance ||
            c.B > c.R * StrongChannelDominance && c.B > c.G * StrongChannelDominance;

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
