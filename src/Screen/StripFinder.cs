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
        /// <summary>How bright the lights were when lit, 0 to 255. A reflection of a strip is dimmer than the strip.</summary>
        public int Brightness;
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
        /// <summary>
        /// Every channel this bright is the white-hot middle of a light. Some games (PMR) draw a light
        /// as a small white core in a glow of its colour many times its size, and at the limiter the
        /// glows of a whole strip run together over the wheel: by colour the strip is one lit blur, by
        /// its cores it is a row of dots.
        /// </summary>
        private const int CoreFloor = 170;

        /// <summary>Share of the frames below the dividing engine speed that may be lit anyway (display lag).</summary>
        private const double LitBelowShare = 0.10;
        /// <summary>Share of the frames above it that must be lit. Under one, for a strip blinking at the limiter.</summary>
        private const double LitAboveShare = 0.60;
        /// <summary>
        /// A point lit this share of the time above its speed is no light on its own word, but it
        /// may be the last light or two of a strip found beside it: those come on just under the
        /// limiter, where the strip then blinks them dark half the time.
        /// </summary>
        private const double WeakAboveShare = 0.30;
        /// <summary>
        /// Frames a light may show behind the engine speed it belongs to. Some games (PMR) draw their
        /// lights a good part of a second late, and a quick blip in neutral is over by then: read
        /// against the revs of the same instant, such a light is lit on the way down and dark on the
        /// way up, and follows nothing.
        /// </summary>
        private const int MostFramesLate = 5;
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
        private uint[] _brightSum;       // brightest channel of each point, summed over the frames it was lit in
        // The same three for white-hot cores alone.
        private readonly List<byte[]> _coreMasks = new List<byte[]>();
        private ushort[] _coreCount;
        private uint[] _coreBright;
        // Whichever of the two Find is working on.
        private List<byte[]> _useMasks;
        private ushort[] _useCount;
        private uint[] _useBright;
        private bool[] _weak;
        private bool[] _coreFollows;
        private List<int[]> _regionBox;   // left, top, right, bottom of each region, in sampled points
        private int _width, _height, _step, _cols, _rows;

        public int Frames => _masks.Count;

        /// <summary>The engine speed of each frame, in the order they were added.</summary>
        public IReadOnlyList<double> Rpms => _rpms;
        /// <summary>Screen pixels between sampled points, each way.</summary>
        public int Step => _step;

        /// <summary>
        /// Adds one whole-screen frame and the engine speed it was taken at. Returns how many sampled
        /// points were lit in it, or -1 if the frame wasn't taken.
        /// </summary>
        public int Add(PixelFrame frame, double rpm)
        {
            if (frame == null || double.IsNaN(rpm)) return -1;
            if (_litCount == null)
            {
                _width = frame.Width;
                _height = frame.Height;
                _step = 2;
                while ((long)(_width / _step) * (_height / _step) > PointBudget) _step++;
                _cols = _width / _step;
                _rows = _height / _step;
                if (_cols == 0 || _rows == 0) return -1;
                _litCount = new ushort[_cols * _rows];
                _brightSum = new uint[_cols * _rows];
                _coreCount = new ushort[_cols * _rows];
                _coreBright = new uint[_cols * _rows];
            }
            else if (frame.Width != _width || frame.Height != _height || _masks.Count >= ushort.MaxValue) return -1;

            int litPoints = 0;
            var mask = new byte[(_cols * _rows + 7) / 8];
            var core = new byte[mask.Length];
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
                    _brightSum[cell] += (uint)max;
                    litPoints++;
                    if (min < CoreFloor) continue;
                    core[cell >> 3] |= (byte)(1 << (cell & 7));
                    _coreCount[cell]++;
                    _coreBright[cell] += (uint)max;
                }
            }
            _coreMasks.Add(core);
            _masks.Add(mask);
            _rpms.Add(rpm);
            return litPoints;
        }

        private sealed class Patch
        {
            public int Left, Top, Right, Bottom;   // sampled points, inclusive
            public int Points, WeakPoints;
            public bool Weak => WeakPoints * 2 > Points;
            public long SplitSum;
            public long BrightSum, LitFrames;
            public double Brightness => LitFrames == 0 ? 0 : BrightSum / (double)LitFrames;
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

            // Once up tells nothing apart: a lap timer's digit that happened to change on the way up
            // came on "with the revs" as surely as a light did. Twice up, with a fall between, and only
            // what really follows the engine agrees both times.
            if (ClimbsThrough((low + high) / 2) < 2)
            {
                result.Problem = "The revs went up only once. Rev up to the limiter and right back down, twice, while it looks.";
                return result;
            }

            // By colour first, then by white-hot cores alone, and the best of both.
            _useMasks = _masks; _useCount = _litCount; _useBright = _brightSum;
            Analyse(n, result.Strips, keepVerdict: true);
            _useMasks = _coreMasks; _useCount = _coreCount; _useBright = _coreBright;
            var cores = new List<FoundStrip>();
            Analyse(n, cores, keepVerdict: false);
            // The same strip found both ways is one candidate: the better-scoring box stands.
            foreach (var strip in cores)
            {
                int same = result.Strips.FindIndex(other => Overlap(other.Box, strip.Box) > 0.5);
                if (same < 0) result.Strips.Add(strip);
                else if (strip.Score > result.Strips[same].Score) result.Strips[same] = strip;
            }
            result.Strips = result.Strips.OrderByDescending(s => s.Score).ToList();
            return result;
        }

        /// <summary>Share of the smaller box that lies inside the other.</summary>
        private static double Overlap(PixelRect a, PixelRect b)
        {
            int width = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X), height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
            if (width <= 0 || height <= 0) return 0;
            return width * (double)height / Math.Min(a.Width * (double)a.Height, b.Width * (double)b.Height);
        }

        private void Analyse(int n, List<FoundStrip> strips, bool keepVerdict)
        {
            byte[] verdict = _verdict;
            ushort[] split = Splits(n);
            if (!keepVerdict)
            {
                _coreFollows = split.Select(k => k != 0).ToArray();
                _verdict = verdict;
            }
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
                double brightness = row.Average(p => p.Brightness);
                strips.Add(new FoundStrip
                {
                    Brightness = (int)Math.Round(brightness),
                    Box = new PixelRect(left, top, right - left, bottom - top),
                    Lights = row.Count,
                    Steps = steps,
                    // More lights, and lights that step up with the revs, look more like a strip;
                    // patches of very different widths look less like one. Lights with dark between
                    // them count for more than pieces of one lit run, which a rev bar caught at a few
                    // lengths can also be.
                    // And the lights themselves are the brightest thing that follows the revs: their
                    // reflection in the dash or the screen follows them just as well, only dimmer.
                    Score = (row.Count + steps) / (1 + spread) *
                            (row.Select(p => p.Region).Distinct().Count() == row.Count ? ApartBonus : 1) *
                            (brightness / 255) * (brightness / 255),
                });
            }
        }

        /// <summary>How many times, in the order the frames were taken, the revs rose through this engine speed.</summary>
        private int ClimbsThrough(double rpm) => ClimbsThrough(_rpms, rpm);

        private static int ClimbsThrough(IReadOnlyList<double> rpms, double rpm)
        {
            int climbs = 0;
            bool under = false;
            foreach (double r in rpms)
            {
                if (r < rpm) under = true;
                else if (under) { climbs++; under = false; }
            }
            return climbs;
        }

        /// <summary>Why each sampled point was or wasn't taken as following the revs, for <see cref="Map"/>.</summary>
        private byte[] _verdict;
        private const byte Follows = 1, LitAtLowRevs = 2, DarkAtHighRevs = 3, SeenOnce = 4;

        /// <summary>
        /// For every sampled point, how many of the frames (in order of engine speed) lie below the speed
        /// it comes on at, or zero when it doesn't follow the revs.
        /// </summary>
        private ushort[] Splits(int n)
        {
            int side = Math.Max(2, (int)Math.Ceiling(SideShare * n));
            // Each frame is tried against the revs of its own instant, then of one frame earlier, and
            // so on: a point is taken at the first lateness it follows the revs at.
            int tries = Math.Min(MostFramesLate, n / 8) + 1;
            var ordered = new byte[tries][][];
            var seenTwice = new bool[tries][];
            for (int late = 0; late < tries; late++)
            {
                var rpms = Enumerable.Range(0, n).Select(i => _rpms[Math.Max(0, i - late)]).ToList();
                var order = Enumerable.Range(0, n).OrderBy(i => rpms[i]).ToArray();
                ordered[late] = order.Select(i => _useMasks[i]).ToArray();
                // A point is only believed if the revs rose through the speed it comes on at twice.
                seenTwice[late] = new bool[n + 1];
                for (int k = 1; k < n; k++) seenTwice[late][k] = ClimbsThrough(rpms, rpms[order[k]]) >= 2;
            }
            var split = new ushort[_useCount.Length];
            _verdict = new byte[_useCount.Length];
            _weak = new bool[_useCount.Length];
            var lit = new bool[n];
            for (int cell = 0; cell < split.Length; cell++)
            {
                int total = _useCount[cell];
                if (total < 2 || total > n - side) continue;
                int at = cell >> 3, bit = 1 << (cell & 7);
                int weakSplit = 0;
                for (int late = 0; late < tries && split[cell] == 0; late++)
                {
                    var frames = ordered[late];
                    for (int f = 0; f < n; f++) lit[f] = (frames[f][at] & bit) != 0;

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
                    byte verdict =
                        bestBelow > Math.Max(1, (int)(LitBelowShare * best)) ? LitAtLowRevs
                        : total - bestBelow < LitAboveShare * (n - best) ? DarkAtHighRevs
                        : !seenTwice[late][best] ? SeenOnce : Follows;
                    if (late == 0 || verdict == Follows) _verdict[cell] = verdict;
                    if (verdict == Follows) split[cell] = (ushort)best;
                    else if (verdict == DarkAtHighRevs && weakSplit == 0 && seenTwice[late][best] &&
                             total - bestBelow >= WeakAboveShare * (n - best)) weakSplit = best;
                }
                if (split[cell] == 0 && weakSplit != 0)
                {
                    split[cell] = (ushort)weakSplit;
                    _weak[cell] = true;
                }
            }
            return split;
        }

        /// <summary>
        /// A picture of what <see cref="Find"/> made of the screen, one pixel per sampled point, as
        /// BGRA: green followed the revs; white did so as a white-hot core; red was lit too often at low
        /// revs; blue was dark too often at high revs; yellow would have followed but the revs passed its speed only once; grey was lit
        /// at some time and never in question. For working out why a strip was or wasn't found.
        /// </summary>
        public byte[] Map(out int width, out int height)
        {
            width = _cols;
            height = _rows;
            if (_litCount == null) return new byte[0];
            var map = new byte[_cols * _rows * 4];
            for (int cell = 0; cell < _litCount.Length; cell++)
            {
                byte b = 0, g = 0, r = 0;
                switch (_verdict != null ? _verdict[cell] : (byte)0)
                {
                    case Follows: g = 255; break;
                    case LitAtLowRevs: r = 255; break;
                    case DarkAtHighRevs: b = 255; g = 96; break;
                    case SeenOnce: r = 255; g = 255; break;
                    default: if (_litCount[cell] > 0) r = g = b = 70; break;
                }
                if (_coreFollows != null && _coreFollows[cell]) r = g = b = 255;
                int i = cell * 4;
                map[i] = b; map[i + 1] = g; map[i + 2] = r; map[i + 3] = 255;
            }
            return map;
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
                    if (_weak[cell]) patch.WeakPoints++;
                    patch.SplitSum += split[cell];
                    patch.BrightSum += _useBright[cell];
                    patch.LitFrames += _useCount[cell];
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
            // Weak patches only ever lengthen a strip: three that stand on their own word make one.
            return rows.Where(r => r.Count(p => !p.Weak) >= FewestLights && r.Count <= MostLights).ToList();
        }
    }
}
