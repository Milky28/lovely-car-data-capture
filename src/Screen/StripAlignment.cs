using System;
using System.Collections.Generic;
using System.Linq;

namespace LovelyCarDataCapture.Screen
{
    /// <summary>
    /// Measures how far the whole strip sat from its usual place in each frame, so a capture made with
    /// head movement or camera shake on can be read as if the camera had stood still.
    /// </summary>
    /// <remarks>
    /// The camera moves every light by the same amount, so the distances between lights lit together
    /// don't change. The frames with the most lights lit give the strip's shape; every other frame is
    /// slid onto that shape. A frame showing only part of an evenly spaced strip fits in several places,
    /// one light spacing apart, so it takes the place nearest the frame next to it in time: the camera
    /// moves far less than a light spacing between two frames. That holds while lights are lit. Across a
    /// stretch with the strip dark there is nothing to follow, so the camera must not have moved more
    /// than half a light spacing by the time the first light comes back on.
    /// </remarks>
    internal sealed class StripAlignment
    {
        // A light's centre wanders this far by itself as it blooms; StripCalibration allows the same.
        private const double ReachPx = 6;
        // On a tightly packed strip the reach must stay well inside half a light spacing.
        private const double ReachShareOfPitch = 0.3;
        // A capture this steady reads correctly as it is, so it is left exactly as recorded: the fixed
        // grouping in StripCalibration copes with this much.
        public const int SteadyPx = 6;
        // The strip's shape comes from the fullest frames. A count seen in fewer frames than this share
        // of the frames with two or more lights is a reflection or a split light, not the whole strip:
        // LMU's SC63 shows 12 blobs in 10 frames of 2400. The share has to stay small, because the top
        // lights of some strips only come on at the limiter: PMR's Corvette C7.R shows all ten in 325
        // frames of 5600, and from eight of them a full strip fits two lights along just as well.
        private const double ShapeShare = 0.01;
        private const int ShapeFrames = 3;
        // A frame fixes its own place when nearly the whole strip is lit. Part of an evenly spaced strip
        // fits one spacing along just as well, so anything less follows its neighbours in time.
        private const double AnchorShare = 0.9;
        // How fast the strip may move between frames that can't fix their own place. Without a limit a
        // single stray highlight (AC's McLaren has them 16 px past its end lights) pulls the whole frame
        // onto the nearest light.
        private const double FollowFloorPx = 3;
        private const double FollowPxPerMs = 0.15;
        // ...and never as far as half a light spacing, where the next light along fits as well.
        private const double FollowShareOfPitch = 0.4;
        // A position needs repeated sightings to join the shape, as in StripCalibration.
        private const int ShapeSightings = 3;
        private const double ShapeSightingShare = 0.02;
        // ...and to sit most of a light spacing from the lights already in it.
        private const double NewLightSpacing = 0.75;

        private StripAlignment(int[] shift, int movementPx)
        {
            Shift = shift;
            MovementPx = movementPx;
        }

        /// <summary>Pixels to take off every light's position in each frame.</summary>
        public int[] Shift { get; }
        /// <summary>How far the fully lit strip ranged across the capture, ignoring the odd stray frame.</summary>
        public int MovementPx { get; }
        public bool Moved => MovementPx > SteadyPx;

        /// <summary>
        /// Works out each frame's shift from the light positions alone. Returns null when the capture
        /// never shows enough of the strip at once to take a shape from.
        /// </summary>
        public static StripAlignment Measure(IReadOnlyList<double[]> frames, IReadOnlyList<long> times)
        {
            int n = frames.Count;
            var sorted = new double[n][];
            for (int i = 0; i < n; i++)
            {
                sorted[i] = (double[])frames[i].Clone();
                Array.Sort(sorted[i]);
            }

            int several = sorted.Count(f => f.Length >= 2);
            if (several == 0) return null;
            double enough = Math.Max(ShapeFrames, several * ShapeShare);
            int most = 0;
            for (int count = sorted.Max(f => f.Length); count >= 2 && most == 0; count--)
                if (sorted.Count(f => f.Length == count) >= enough) most = count;
            if (most == 0) return null;

            // The frame of typical width among the fullest ones is the strip's shape to begin with.
            var fullest = Enumerable.Range(0, n).Where(i => sorted[i].Length == most).ToList();
            double[] seed = sorted[fullest.OrderBy(i => sorted[i][most - 1] - sorted[i][0]).ElementAt(fullest.Count / 2)];
            var spacings = new List<double>();
            for (int b = 1; b < seed.Length; b++) spacings.Add(seed[b] - seed[b - 1]);
            spacings.Sort();
            double pitch = spacings[spacings.Count / 2];
            double reach = Math.Min(ReachPx, pitch * ReachShareOfPitch);

            // Frames with the strip lit fix their own place.
            int needed = Math.Max(2, (int)Math.Ceiling(seed.Length * AnchorShare));
            var anchors = new double[n];
            var anchored = new bool[n];
            var ranged = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (sorted[i].Length < needed || !Anchor(sorted[i], seed, reach, needed, out double shift)) continue;
                anchors[i] = shift;
                anchored[i] = true;
                ranged.Add((int)Math.Round(shift));
            }
            if (ranged.Count == 0) return null;
            ranged.Sort();
            int trim = ranged.Count / 100;
            int movement = ranged[ranged.Count - 1 - trim] - ranged[trim];
            if (movement <= SteadyPx) return new StripAlignment(new int[n], movement);

            // Every other frame follows the nearest of those in time. Lights the fullest frames never
            // showed (a middle light that only lights alone) join the shape on the way, so frames
            // showing only those can be followed too.
            double[] shifts = FollowBothWays(sorted, times, seed, reach, pitch, anchors, anchored);
            var extra = new List<double>();
            for (int i = 0; i < n; i++)
                foreach (double x in sorted[i])
                    if (Math.Abs(seed[Nearest(seed, x - shifts[i])] - (x - shifts[i])) > reach) extra.Add(x - shifts[i]);
            extra.Sort();
            var shape = seed.ToList();
            double sightings = Math.Max(ShapeSightings, fullest.Count * ShapeSightingShare);
            for (int from = 0; from < extra.Count;)
            {
                int to = from + 1;
                while (to < extra.Count && extra[to] - extra[to - 1] <= 1 && extra[to] - extra[from] <= reach) to++;
                double center = extra.Skip(from).Take(to - from).Average();
                // Two lights bloomed into one sit halfway between them. In the shape they would let
                // every frame fit half a spacing along as well.
                // Nor does the strip grow past its ends here: a frame followed onto the wrong light looks
                // exactly like one more light a spacing beyond the last.
                if (to - from >= sightings && center > seed[0] && center < seed[seed.Length - 1] && shape.All(point => Math.Abs(point - center) >= pitch * NewLightSpacing)) shape.Add(center);
                from = to;
            }
            shape.Sort();
            // With the whole shape known, a frame lit at both ends of the strip fixes its own place too,
            // however few lights it shows: nothing that wide fits anywhere else. A strip that fills from
            // both ends is found again that way as soon as it comes out of the dark. Fewer lights than
            // that can't be trusted to: two of AC's Bayer lights fit 82 px along as well as they fit
            // where they are.
            double[] whole = shape.ToArray();
            for (int i = 0; i < n; i++)
            {
                double[] f = sorted[i];
                if (anchored[i] || f.Length < 2) continue;
                double shift = ((f[0] - whole[0]) + (f[f.Length - 1] - whole[whole.Length - 1])) / 2;
                if (Math.Abs(f[0] - shift - whole[0]) > reach || Fit(f, whole, reach, shift, out shift) < f.Length) continue;
                anchors[i] = shift;
                anchored[i] = true;
            }
            shifts = FollowBothWays(sorted, times, whole, reach, pitch, anchors, anchored);
            return new StripAlignment(shifts.Select(s => (int)Math.Round(s)).ToArray(), movement);
        }

        private static double[] FollowBothWays(double[][] frames, IReadOnlyList<long> times, double[] shape, double reach, double pitch,
                                               double[] anchors, bool[] anchored)
        {
            int n = frames.Length;
            var forward = new double[n];
            var forwardAge = new long[n];
            var backward = new double[n];
            var backwardAge = new long[n];
            Follow(frames, times, shape, reach, pitch, anchors, anchored, forward, forwardAge, 0, n, 1);
            Follow(frames, times, shape, reach, pitch, anchors, anchored, backward, backwardAge, n - 1, -1, -1);
            var shifts = new double[n];
            for (int i = 0; i < n; i++)
                shifts[i] = anchored[i] ? anchors[i] : forwardAge[i] <= backwardAge[i] ? forward[i] : backward[i];
            return shifts;
        }

        /// <summary>
        /// Carries the shift from each frame that fixed its own place to the frames after it (or before
        /// it, going backwards), each taking the fit nearest the last one. Age is the longest the strip
        /// went unseen on the way from that fixed frame, and is huge where there hasn't been one yet: a
        /// climb out of the dark is followed back from the full strip it ends in, not forward across
        /// the dark from the sweep before.
        /// </summary>
        private static void Follow(double[][] frames, IReadOnlyList<long> times, double[] shape, double reach, double pitch,
                                   double[] anchors, bool[] anchored, double[] carried, long[] age, int from, int to, int step)
        {
            bool started = false;
            double last = 0;
            long lastAt = 0, unseen = 0;
            for (int i = from; i != to; i += step)
            {
                if (anchored[i])
                {
                    last = anchors[i];
                    lastAt = times[i];
                    unseen = 0;
                    started = true;
                }
                else if (started && frames[i].Length > 0)
                {
                    long gap = Math.Abs(times[i] - lastAt);
                    double limit = Math.Min(pitch * FollowShareOfPitch, FollowFloorPx + FollowPxPerMs * gap);
                    if (NearestFit(frames[i], shape, reach, limit, last, out double fit))
                    {
                        last = fit;
                        lastAt = times[i];
                        unseen = Math.Max(unseen, gap);
                    }
                }
                carried[i] = last;
                age[i] = started ? Math.Max(unseen, Math.Abs(times[i] - lastAt)) : long.MaxValue;
            }
        }

        /// <summary>The fit with the most lights on the shape within the limit of the expected shift, if any light fits at all.</summary>
        private static bool NearestFit(double[] frame, double[] shape, double reach, double limit, double expected, out double best)
        {
            best = expected;
            int bestCount = 0;
            double bestDistance = double.MaxValue;
            foreach (double x in frame)
                foreach (double point in shape)
                {
                    if (Math.Abs(x - point - expected) > limit + reach) continue;
                    int count = Fit(frame, shape, reach, x - point, out double refined);
                    double distance = Math.Abs(refined - expected);
                    if (distance > limit) continue;
                    if (count > bestCount || count == bestCount && distance < bestDistance)
                    {
                        bestCount = count;
                        bestDistance = distance;
                        best = refined;
                    }
                }
            return bestCount > 0;
        }

        /// <summary>
        /// True when the frame fits the shape in one place only, with enough of its lights on it. A
        /// frame showing part of an evenly spaced strip fits one spacing along just as well, and is left
        /// to its neighbours in time.
        /// </summary>
        private static bool Anchor(double[] frame, double[] shape, double reach, int needed, out double shift)
        {
            shift = 0;
            int best = 0, second = 0;
            var tried = new HashSet<int>();
            var fits = new List<KeyValuePair<double, int>>();
            foreach (double x in frame)
                foreach (double point in shape)
                {
                    if (!tried.Add((int)Math.Round(x - point))) continue;
                    int count = Fit(frame, shape, reach, x - point, out double refined);
                    fits.Add(new KeyValuePair<double, int>(refined, count));
                    if (count > best) { best = count; shift = refined; }
                }
            if (best < needed) return false;
            foreach (var fit in fits)
                if (Math.Abs(fit.Key - shift) > reach * 2 && fit.Value > second) second = fit.Value;
            return second < best;
        }

        /// <summary>How many of the frame's lights land on the shape at this shift, and the shift that centres them.</summary>
        private static int Fit(double[] frame, double[] shape, double reach, double shift, out double refined)
        {
            int count = 0;
            double error = 0;
            foreach (double x in frame)
            {
                double at = x - shift;
                double off = at - shape[Nearest(shape, at)];
                if (Math.Abs(off) > reach) continue;
                count++;
                error += off;
            }
            refined = count == 0 ? shift : shift + error / count;
            return count;
        }

        private static int Nearest(double[] shape, double x)
        {
            int at = Array.BinarySearch(shape, x);
            if (at >= 0) return at;
            at = ~at;
            if (at == 0) return 0;
            if (at == shape.Length) return shape.Length - 1;
            return shape[at] - x < x - shape[at - 1] ? at : at - 1;
        }
    }
}
