using System;
using System.Collections.Generic;
using System.Linq;

namespace LovelyCarDataCapture.Screen
{
    /// <summary>
    /// Turns colours measured off the screen into car-file colours.
    /// </summary>
    /// <remarks>
    /// A light rendered in a game is never its own colour: tone mapping, bloom and the cockpit's
    /// lighting wash it towards white and squeeze the hues together, so a pure green LED can measure
    /// as rgb(138,177,106) and read as a hue of 93° instead of 120°. Absolute matching would call that
    /// yellow-green. What survives is the order and the spacing of the hues, so the strip's own
    /// colours are placed on that scale: the redline colour is taken as red, the furthest colour from
    /// it as green when it's far enough away, and everything between is stretched to fit. The result
    /// is a suggestion to check against the game, not a measurement.
    /// </remarks>
    internal static class LedPalette
    {
        private static readonly Tuple<string, string, double>[] Ladder =
        {
            Tuple.Create("red", "#FFFF0000", 0.0),
            Tuple.Create("orange", "#FFFF8000", 30.0),
            Tuple.Create("yellow", "#FFFFFF00", 60.0),
            Tuple.Create("green", "#FF00FF00", 120.0),
            Tuple.Create("blue", "#FF0000FF", 240.0),
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
        /// Groups the measured colours and names them. <paramref name="redlineHue"/> is the hue the
        /// whole strip turns above the redline, or -1 when no colour change was seen.
        /// </summary>
        public static List<ColorGroup> Group(IList<LedColor> perSlot, IList<bool> isGap, double redlineHue)
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

            groups = groups.OrderBy(g => g.Hue).ToList();
            double anchor = redlineHue >= 0 ? redlineHue : groups[0].Hue;
            double spread = groups[groups.Count - 1].Hue - anchor;
            // Far-apart colours: assume the furthest is green and stretch to match. Otherwise fall back
            // to the compression seen in practice (about two thirds of the real hue range).
            double scale = spread > 35 ? 120.0 / spread : 1.5;

            foreach (var g in groups)
            {
                double corrected = (g.Hue - anchor) * scale;
                var best = Ladder.OrderBy(l => Math.Abs(l.Item3 - corrected)).First();
                g.Name = best.Item1;
                g.Hex = best.Item2;
            }
            return groups;
        }

        /// <summary>True when two groups landed on the same colour, or sit so close that the split is doubtful.</summary>
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
