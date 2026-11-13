using System;
using System.Collections.Generic;
using System.Linq;

namespace LovelyCarDataCapture.Screen
{
    /// <summary>One row of lights the finder thinks is the car's rev strip.</summary>
    internal sealed class FoundStrip
    {
        /// <summary>Box round the lights with a little room to spare, in the pixels of the frames given.</summary>
        public PixelRect Box;
        public int Lights;
        /// <summary>How many different engine speeds the lights came on at. One means they all lit together.</summary>
        public int Steps;
        public double Score;
    }

    internal sealed class StripFinderResult
    {
        /// <summary>Best first. Empty when nothing on screen behaved like a rev strip.</summary>
        public List<FoundStrip> Strips = new List<FoundStrip>();
        /// <summary>Why nothing could be looked for at all, or null.</summary>
        public string Problem;
    }

    /// <summary>
    /// Finds the rev lights anywhere on the screen by watching what the engine does to it: a rev light
    /// is a patch that is dark at low revs and lit at high revs, and a strip is several of them in a row.
    /// </summary>
    /// <remarks>
    /// Whole-screen frames are given with the engine speed each was taken at, while the driver revs the
    /// car where it stands. Each frame is kept only as one bit per sampled point (lit or not), so a few
    /// seconds of a 4K desktop fit in a few megabytes.
    /// <para>
    /// A point belongs to a light when there is an engine speed that divides its frames cleanly: dark
    /// below it, lit above it. Lit above only has to hold most of the time, because a strip at the
    /// limiter blinks. That one rule throws out nearly everything else on a screen: the track and the
    /// cockpit don't follow the revs, a needle is lit only as it passes, and digits change every way.
    /// </para>
    /// <para>
    /// What it can't tell from a rev strip is anything else that fills with revs, such as a HUD rev bar
    /// drawn in blocks. So the answer is a proposal for the driver to accept or redraw, never a box that
    /// is saved on its own.
    /// </para>
    /// </remarks>
    internal sealed class StripFinder
    {
        /// <summary>Fewer frames than this say too little about which points follow the revs.</summary>
        public const int FewestFrames = 12;

        /// <summary>Sampled points aimed for per frame. Sets how coarsely a large desktop is sampled.</summary>
        private const int PointBudget = 2500000;

        // The same idea of "lit" as the detector's, a little stricter on brightness because a whole
        // screen holds far more dim colour than a box round the lights does. White lights count too.
        private const int LitBrightness = 110;
        private const int LitSaturation = 55;
        private const int WhiteFloor = 200;

        /// <summary>Share of the frames below the dividing engine speed that may be lit anyway (display lag).</summary>
        private const double LitBelowShare = 0.10;
        /// <summary>Share of the frames above it that must be lit. Under one, for a strip blinking at the limiter.</summary>
        private const double LitAboveShare = 0.60;
        /// <summary>Each side of the dividing speed needs this share of the frames, and at least two.</summary>
        private const double SideShare = 0.10;

        /// <summary>The revs have to move this much, in rpm and as a share of the highest seen.</summary>
        private const double RevRangeRpm = 1000, RevRangeShare = 0.25;

        private const int TallestLightPx = 150, WidestLightPx = 400;
        private const int FewestLights = 3, MostLights = 30;
        /// <summary>Share of a run of touching rev-following points that must be whole lights for any of it to count.</summary>
        private const double LightShareOfRegion = 0.7;
        /// <summary>Lights further apart than this many widths of the larger are not one strip.</summary>
        private const double WidestGapInWidths = 6;

        private const double ApartBonus = 1.5;

        private readonly List<byte[]> _masks = new List<byte[]>();
        private readonly List<double> _rpms = new List<double>();
        private ushort[] _litCount;
        private List<int[]> _regionBox;   // left, top, right, bottom of each region, in sampled points
        private int _width, _height, _step, _cols, _rows;

        public int Frames => _masks.Count;

        /// <summary>Adds one whole-screen frame and the engine speed it was taken at.</summary>
        public void Add(PixelFrame frame, double rpm)
        {
            if (frame == null || double.IsNaN(rpm)) return;
            if (_litCount == null)
            {
                _width = frame.Width;
                _height = frame.Height;
                _step = 2;
                while ((long)(_width / _step) * (_height / _step) > PointBudget) _step++;
                _cols = _width / _step;
                _rows = _height / _step;
                if (_cols == 0 || _rows == 0) return;
                _litCount = new ushort[_cols * _rows];
            }
            else if (frame.Width != _width || frame.Height != _height || _masks.Count >= ushort.MaxValue) return;

            var mask = new byte[(_cols * _rows + 7) / 8];
            var pixels = frame.Pixels;
            int red = frame.RedOffset, green = frame.GreenOffset, blue = frame.BlueOffset;
            int stride = frame.Stride, size = frame.BytesPerPixel, half = _step / 2;
            for (int cy = 0; cy < _rows; cy++)
            {
                int i = (cy * _step + half) * stride + half * size;
                int cell = cy * _cols;
                for (int cx = 0; cx < _cols; cx++, cell++, i += _step * size)
                {
                    int r = pixels[i + red], g = pixels[i + green], b = pixels[i + blue];
                    int max = r > g ? (r > b ? r : b) : (g > b ? g : b);
                    if (max < LitBrightness) continue;
                    int min = r < g ? (r < b ? r : b) : (g < b ? g : b);
                    if (max - min < LitSaturation && min < WhiteFloor) continue;
                    mask[cell >> 3] |= (byte)(1 << (cell & 7));
                    _litCount[cell]++;
                }
            }
            _masks.Add(mask);
            _rpms.Add(rpm);
        }

        private sealed class Patch
        {
            public int Left, Top, Right, Bottom;   // sampled points, inclusive
            public int Points;
            public long SplitSum;
            public int LowSplit = int.MaxValue, HighSplit;
            public int Region;
            public int Width => Right - Left + 1;
            public int Height => Bottom - Top + 1;
            public double MidY => (Top + Bottom) / 2.0;
            public double Split => SplitSum / (double)Points;
        }

        public StripFinderResult Find()
        {
            var result = new StripFinderResult();
            int n = _masks.Count;
            if (n < FewestFrames)
            {
                result.Problem = "Too little of the screen was seen to look for the lights.";
                return result;
            }
            double low = _rpms.Min(), high = _rpms.Max();
            if (high <= 0)
            {
                result.Problem = "The game sent no engine speed. Sit in the car with the engine running and try again.";
                return result;
            }
            if (high - low < Math.Max(RevRangeRpm, RevRangeShare * high))
            {
                result.Problem = "The revs barely moved (" + Math.Round(low) + " to " + Math.Round(high) +
                                 " rpm). Rev the engine up to the lights and back while it looks.";
                return result;
            }

            ushort[] split = Splits(n);
            // Neighbouring points are one light only if they come on together, give or take a frame in
            // fifty. Lights that touch on screen, or share a glow, then still come apart where the strip
            // steps. Any looser and neighbouring lights run together; any tighter and a smoothly
            // filling rev bar breaks into pieces that each look like a light.
            int together = n / 50;
            var patches = Patches(split, together);
            foreach (var row in Rows(patches))
            {
                var widths = row.Select(p => (double)p.Width).ToList();
                double mean = widths.Average();
                double spread = Math.Sqrt(widths.Sum(w => (w - mean) * (w - mean)) / widths.Count) / mean;
                var splits = row.Select(p => p.Split).OrderBy(s => s).ToList();
                int steps = 1;
                for (int i = 1; i < splits.Count; i++) if (splits[i] - splits[i - 1] > together) steps++;

                // The box takes in the whole lit run each light is part of, so a light that ran into
                // its neighbour and wasn't counted is still inside.
                var runs = row.Select(p => _regionBox[p.Region]).ToList();
                int left = runs.Min(b => b[0]) * _step, right = (runs.Max(b => b[2]) + 1) * _step;
                int top = runs.Min(b => b[1]) * _step, bottom = (runs.Max(b => b[3]) + 1) * _step;
                // "A little outside them", as the driver is asked to draw it.
                int tallest = row.Max(p => p.Height) * _step;
                int margin = Math.Max(8, tallest / 2);
                left = Math.Max(0, left - margin);
                top = Math.Max(0, top - margin);
                right = Math.Min(_width, right + margin);
                bottom = Math.Min(_height, bottom + margin);
                result.Strips.Add(new FoundStrip
                {
                    Box = new PixelRect(left, top, right - left, bottom - top),
                    Lights = row.Count,
                    Steps = steps,
                    // More lights, and lights that step up with the revs, look more like a strip;
                    // patches of very different widths look less like one. Lights with dark between
                    // them count for more than pieces of one lit run, which a rev bar caught at a few
                    // lengths can also be.
                    Score = (row.Count + steps) / (1 + spread) *
                            (row.Select(p => p.Region).Distinct().Count() == row.Count ? ApartBonus : 1),
                });
            }
            result.Strips = result.Strips.OrderByDescending(s => s.Score).ToList();
            return result;
        }

        /// <summary>
        /// For every sampled point, how many of the frames (in order of engine speed) lie below the speed
        /// it comes on at, or zero when it doesn't follow the revs.
        /// </summary>
        private ushort[] Splits(int n)
        {
            var order = Enumerable.Range(0, n).OrderBy(i => _rpms[i]).ToArray();
            var ordered = order.Select(i => _masks[i]).ToArray();
            int side = Math.Max(2, (int)Math.Ceiling(SideShare * n));
            var split = new ushort[_litCount.Length];
            var lit = new bool[n];
            for (int cell = 0; cell < split.Length; cell++)
            {
                int total = _litCount[cell];
                if (total < 2 || total > n - side) continue;
                int at = cell >> 3, bit = 1 << (cell & 7);
                for (int f = 0; f < n; f++) lit[f] = (ordered[f][at] & bit) != 0;

                int below = 0, best = 0, bestScore = int.MinValue, bestBelow = 0;
                for (int k = 0; k <= n - 2; k++)
                {
                    // k frames lie below the dividing speed; below counts the lit ones among them.
                    if (k >= side)
                    {
                        int above = total - below;
                        int score = 2 * above - (n - k) - 4 * below;
                        if (score > bestScore) { bestScore = score; best = k; bestBelow = below; }
                    }
                    if (lit[k]) below++;
                }
                if (best == 0) continue;
                if (bestBelow > Math.Max(1, (int)(LitBelowShare * best))) continue;
                if (total - bestBelow < LitAboveShare * (n - best)) continue;
                split[cell] = (ushort)best;
            }
            return split;
        }

        /// <summary>Numbers every run of touching points that follow the revs, whatever speed each comes on at.</summary>
        private int[] Regions(ushort[] split, List<int> sizes)
        {
            var region = new int[split.Length];
            var stack = new Stack<int>();
            sizes.Add(0);   // region numbers start at one
            _regionBox = new List<int[]> { null };
            for (int start = 0; start < split.Length; start++)
            {
                if (split[start] == 0 || region[start] != 0) continue;
                int id = sizes.Count, size = 0;
                var box = new[] { int.MaxValue, int.MaxValue, -1, -1 };
                region[start] = id;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int cell = stack.Pop();
                    size++;
                    int x = cell % _cols, y = cell / _cols;
                    if (x < box[0]) box[0] = x;
                    if (y < box[1]) box[1] = y;
                    if (x > box[2]) box[2] = x;
                    if (y > box[3]) box[3] = y;
                    foreach (int next in Around(cell))
                    {
                        if (region[next] != 0 || split[next] == 0) continue;
                        region[next] = id;
                        stack.Push(next);
                    }
                }
                sizes.Add(size);
                _regionBox.Add(box);
            }
            return region;
        }

        private IEnumerable<int> Around(int cell)
        {
            int x = cell % _cols, y = cell / _cols;
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = y + dy;
                if (ny < 0 || ny >= _rows) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx;
                    if (nx < 0 || nx >= _cols || (dx == 0 && dy == 0)) continue;
                    yield return ny * _cols + nx;
                }
            }
        }

        private List<Patch> Patches(ushort[] split, int together)
        {
            var sizes = new List<int>();
            int[] region = Regions(split, sizes);
            var lightPoints = new int[sizes.Count];

            var patches = new List<Patch>();
            var seen = new bool[split.Length];
            var stack = new Stack<int>();
            for (int start = 0; start < split.Length; start++)
            {
                if (split[start] == 0 || seen[start]) continue;
                var patch = new Patch { Left = int.MaxValue, Top = int.MaxValue, Right = -1, Bottom = -1, Region = region[start] };
                seen[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int cell = stack.Pop();
                    int x = cell % _cols, y = cell / _cols;
                    patch.Points++;
                    patch.SplitSum += split[cell];
                    if (split[cell] < patch.LowSplit) patch.LowSplit = split[cell];
                    if (split[cell] > patch.HighSplit) patch.HighSplit = split[cell];
                    if (x < patch.Left) patch.Left = x;
                    if (x > patch.Right) patch.Right = x;
                    if (y < patch.Top) patch.Top = y;
                    if (y > patch.Bottom) patch.Bottom = y;
                    foreach (int next in Around(cell))
                    {
                        if (seen[next] || split[next] == 0 || Math.Abs(split[next] - split[cell]) > together) continue;
                        seen[next] = true;
                        stack.Push(next);
                    }
                }
                // A light comes on all at once. A patch that comes on bit by bit is part of something
                // that fills with the revs: a HUD rev bar, or the far end of a needle's sweep.
                if (patch.HighSplit - patch.LowSplit > together) continue;
                // One stray point or a sliver is noise; anything light-sized covers several each way.
                if (patch.Width < 2 || patch.Points < 4) continue;
                if (patch.Height * _step > TallestLightPx || patch.Width * _step > WidestLightPx) continue;
                // A light is mostly filled in. A thin outline or a scatter of points is something else.
                if (patch.Points < 0.4 * patch.Width * patch.Height) continue;
                lightPoints[patch.Region] += patch.Points;
                patches.Add(patch);
            }
            // The odd piece of a filling bar does come on at once, where the revs skipped past it. What
            // gives it away is the rest of the bar it is part of, so lights are taken only from runs of
            // points that are nearly all lights.
            return patches.Where(p => lightPoints[p.Region] >= LightShareOfRegion * sizes[p.Region]).ToList();
        }

        /// <summary>Patches of about one height, side by side at about one level, a few widths apart at most.</summary>
        private static List<List<Patch>> Rows(List<Patch> patches)
        {
            var rows = new List<List<Patch>>();
            foreach (var patch in patches.OrderBy(p => p.Left))
            {
                List<Patch> home = null;
                int nearest = int.MaxValue;
                foreach (var row in rows)
                {
                    // Against the row's last light, not its first, so a strip that curves is followed round.
                    var last = row[row.Count - 1];
                    int tall = Math.Max(last.Height, patch.Height), small = Math.Min(last.Height, patch.Height);
                    if (tall > 2 * small + 1) continue;
                    if (Math.Abs(last.MidY - patch.MidY) > 0.75 * tall) continue;
                    int gap = patch.Left - last.Right - 1;
                    if (gap < -1 || gap > WidestGapInWidths * Math.Max(last.Width, patch.Width)) continue;
                    if (gap < nearest) { nearest = gap; home = row; }
                }
                if (home == null) rows.Add(home = new List<Patch>());
                home.Add(patch);
            }
            return rows.Where(r => r.Count >= FewestLights && r.Count <= MostLights).ToList();
        }
    }
}
