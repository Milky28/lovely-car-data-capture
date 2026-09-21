using System;
using System.Collections.Generic;
using System.Linq;

namespace LovelyCarDataCapture.Screen
{
    /// <summary>A rectangle of pixels to search, in frame coordinates.</summary>
    internal struct PixelRect
    {
        public PixelRect(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
        public int X, Y, Width, Height;
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    /// <summary>
    /// Pixels of one captured frame. Windows Graphics Capture hands out BGRA, image files decode to
    /// RGB or BGRA, so the channel offsets are given rather than assumed.
    /// </summary>
    internal sealed class PixelFrame
    {
        public PixelFrame(byte[] pixels, int width, int height, int stride, int bytesPerPixel, int redOffset, int blueOffset)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
            Stride = stride;
            BytesPerPixel = bytesPerPixel;
            RedOffset = redOffset;
            BlueOffset = blueOffset;
        }

        public static PixelFrame Rgb24(byte[] pixels, int width, int height) =>
            new PixelFrame(pixels, width, height, width * 3, 3, 0, 2);

        public static PixelFrame Bgra32(byte[] pixels, int width, int height, int stride = 0) =>
            new PixelFrame(pixels, width, height, stride > 0 ? stride : width * 4, 4, 2, 0);

        public readonly byte[] Pixels;
        public readonly int Width, Height, Stride, BytesPerPixel, RedOffset, BlueOffset;
        public int GreenOffset => 3 - RedOffset - BlueOffset; // the middle channel either way

        public void GetPixel(int x, int y, out int r, out int g, out int b)
        {
            int i = y * Stride + x * BytesPerPixel;
            r = Pixels[i + RedOffset];
            g = Pixels[i + GreenOffset];
            b = Pixels[i + BlueOffset];
        }
    }

    internal struct LedColor
    {
        public LedColor(int r, int g, int b) { R = r; G = g; B = b; }
        public readonly int R, G, B;

        public int Max => Math.Max(R, Math.Max(G, B));
        public int Min => Math.Min(R, Math.Min(G, B));
        /// <summary>How far from grey, relative to how bright: 0 for grey, 1 for a pure colour.</summary>
        public double Saturation => Max == 0 ? 0 : (Max - Min) / (double)Max;

        /// <summary>Hue in degrees (0 = red, 120 = green), or -1 for a grey pixel.</summary>
        public double Hue
        {
            get
            {
                int max = Max, min = Min, delta = max - min;
                if (delta <= 0) return -1;
                double h;
                if (max == R) h = 60.0 * (((G - B) / (double)delta) % 6);
                else if (max == G) h = 60.0 * ((B - R) / (double)delta + 2);
                else h = 60.0 * ((R - G) / (double)delta + 4);
                return h < 0 ? h + 360 : h;
            }
        }

        public string ToHex() => "#" + R.ToString("X2") + G.ToString("X2") + B.ToString("X2");
        public override string ToString() => "rgb(" + R + "," + G + "," + B + ")";
    }

    /// <summary>One lit light found in a frame.</summary>
    internal sealed class LitBlob
    {
        public int Left, Right;
        public double CenterX => (Left + Right) / 2.0;
        public int Width => Right - Left + 1;
        public LedColor Color;
    }

    internal sealed class DetectorSettings
    {
        /// <summary>A lit pixel is at least this bright in its strongest channel.</summary>
        public int MinBrightness = 90;
        /// <summary>…and this far from grey, which is what rules out unlit lights and white dash displays.</summary>
        public int MinSaturation = 55;
        /// <summary>Columns need this many lit pixels to count, which ignores stray bright pixels.</summary>
        public int MinColumnPixels = 3;
        /// <summary>Lit columns closer than this are the same light (anti-aliasing, dark centre pixels).</summary>
        public int MergeGap = 4;
        public int MinWidth = 6;
        /// <summary>Wider than this is glare or two lights bloomed together, not one light.</summary>
        public int MaxWidth = 40;
        /// <summary>A four-dot LED cluster is about 50 px wide in the Mercedes CLK LM capture.</summary>
        public int ClusterMaxWidth = 65;
        /// <summary>Bright cores isolate those clusters from the dashboard graphics below them.</summary>
        public int ClusterBrightness = 235;
        /// <summary>A white-hot light centre has every channel at least this bright.</summary>
        public int CoreBrightness = 235;
        /// <summary>Columns need this many white-hot pixels to be a light's centre: a rim highlight is thinner.</summary>
        public int MinCorePixels = 6;
        /// <summary>How far round a white centre to look for the glow that gives its colour.</summary>
        public int HaloReach = 8;
        /// <summary>Glow pixels this far from grey, relative to their brightness, carry the light's colour.</summary>
        public double HaloSaturation = 0.45;
    }

    /// <summary>
    /// Finds the lit rev lights in a frame by colour rather than at fixed positions: lights are bright
    /// and saturated, while unlit lights, the dash display and the cockpit around them are not. Small
    /// camera movement therefore doesn't need the region to be re-aimed.
    /// </summary>
    internal sealed class StripDetector
    {
        private readonly DetectorSettings _cfg;
        // The LMU logo's blue-grey strokes span about 55-60 channel levels; lit LEDs carry stronger colour.
        private const int PositionSaturation = 90;
        // Include the edge/glow around a learned light and allow a few pixels of cockpit movement.
        private const int PositionPadding = 8;
        // AC's small rectangular LEDs clip two channels to white, with the third above 200.
        private const int WashedCoreBrightness = 200;
        // Thin RRRE LEDs retain strong colour at one edge but wash out through the centre.
        // Weaker columns may extend nearby strong support; they cannot start a light.
        private const double EdgeSaturationFraction = 0.7;
        private const int StrongEdgeColumns = 2;
        private const double EdgeHueTolerance = 30;
        private PixelRect _positionRegion;
        private int _frameWidth, _frameHeight;
        private int[] _top, _bottom, _maskTop, _maskBottom;

        public StripDetector(DetectorSettings settings = null)
        {
            _cfg = settings ?? new DetectorSettings();
        }

        public List<LitBlob> Detect(PixelFrame frame, PixelRect region)
        {
            int x0 = Math.Max(0, region.X), x1 = Math.Min(frame.Width, region.Right);
            int y0 = Math.Max(0, region.Y), y1 = Math.Min(frame.Height, region.Bottom);
            var blobs = new List<LitBlob>();
            if (x1 <= x0 || y1 <= y0) return blobs;
            LearnPositions(frame, new PixelRect(x0, y0, x1 - x0, y1 - y0));

            blobs = DetectColored(frame, x0, x1, y0, y1, _cfg.MinBrightness, _cfg.MaxWidth, out bool smeared);
            // A bright four-dot cluster spans more than one ordinary LED, while a lit dashboard
            // underneath joins the ordinary colour columns into one smear. The cluster's brightest
            // pixels stay separate, even when none is white enough for the white-core detector.
            if (smeared || blobs.Count == 0)
            {
                var clusters = DetectColored(frame, x0, x1, y0, y1, _cfg.ClusterBrightness, _cfg.ClusterMaxWidth, out bool clusterSmear);
                var cores = DetectCores(frame, x0, x1, y0, y1);
                // Mixed strips can have white-centred yellow lights beside saturated green/red ones.
                // Prefer the complete bright-colour strip when its glow has separated cleanly.
                if (!clusterSmear && clusters.Count > cores.Count && (clusters.Count >= 3 || smeared)) return clusters;
                if (cores.Count > 0) return cores;
                if (clusters.Count >= 3 || smeared && clusters.Count > 0) return clusters;
                if (smeared) return cores;
            }
            return blobs;
        }

        private void LearnPositions(PixelFrame frame, PixelRect region)
        {
            if (_top == null || _frameWidth != frame.Width || _frameHeight != frame.Height ||
                _positionRegion.X != region.X || _positionRegion.Y != region.Y ||
                _positionRegion.Width != region.Width || _positionRegion.Height != region.Height)
            {
                _positionRegion = region;
                _frameWidth = frame.Width;
                _frameHeight = frame.Height;
                _top = Enumerable.Repeat(-1, region.Width).ToArray();
                _bottom = new int[region.Width];
                _maskTop = new int[region.Width];
                _maskBottom = new int[region.Width];
            }

            // Learn from strong colour before applying the mask: a new light on the curved strip
            // must be allowed to establish its own height. Pale lettering never trains the mask.
            for (int x = region.X; x < region.Right; x++)
            {
                int column = x - region.X, top = int.MaxValue, bottom = -1, count = 0;
                for (int y = region.Y; y < region.Bottom; y++)
                {
                    if (_top[column] >= 0 &&
                        (y < _top[column] - PositionPadding || y > _bottom[column] + PositionPadding)) continue;
                    frame.GetPixel(x, y, out int r, out int g, out int b);
                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    if (max < _cfg.MinBrightness || max - min < Math.Max(PositionSaturation, _cfg.MinSaturation)) continue;
                    top = Math.Min(top, y);
                    bottom = y;
                    count++;
                }
                if (count < _cfg.MinColumnPixels) continue;
                _top[column] = _top[column] < 0 ? top : Math.Min(_top[column], top);
                _bottom[column] = Math.Max(_bottom[column], bottom);
            }

            // Between observed columns, follow their local height instead of imposing one straight
            // crop across the strip. Outside them, leave room for lights that have not lit yet.
            int previous = -1;
            for (int column = 0; column < region.Width; column++)
            {
                _maskTop[column] = region.Y;
                _maskBottom[column] = region.Bottom - 1;
                if (_top[column] < 0) continue;
                _maskTop[column] = _top[column] - PositionPadding;
                _maskBottom[column] = _bottom[column] + PositionPadding;
                if (previous >= 0)
                    for (int gap = previous + 1; gap < column; gap++)
                    {
                        double fraction = (gap - previous) / (double)(column - previous);
                        _maskTop[gap] = (int)Math.Floor(_top[previous] + fraction * (_top[column] - _top[previous])) - PositionPadding;
                        _maskBottom[gap] = (int)Math.Ceiling(_bottom[previous] + fraction * (_bottom[column] - _bottom[previous])) + PositionPadding;
                    }
                previous = column;
            }
        }

        private bool AtLedHeight(int x, int y)
        {
            int column = x - _positionRegion.X;
            return y >= _maskTop[column] && y <= _maskBottom[column];
        }

        private List<LitBlob> DetectColored(PixelFrame frame, int x0, int x1, int y0, int y1,
                                            int minBrightness, int maxWidth, out bool smeared)
        {
            var litColumn = new bool[x1 - x0];
            var weakColumn = new bool[x1 - x0];
            var strongColumn = new bool[x1 - x0];
            var washedColumn = new bool[x1 - x0];
            var columnHue = new double[x1 - x0];
            for (int x = x0; x < x1; x++)
            {
                int count = 0, saturated = 0, weak = 0, washed = 0, red = 0, green = 0, blue = 0;
                for (int y = y0; y < y1; y++)
                {
                    if (!AtLedHeight(x, y)) continue;
                    frame.GetPixel(x, y, out int r, out int g, out int b);
                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    if (max >= minBrightness && max - min >= _cfg.MinSaturation * EdgeSaturationFraction)
                    { weak++; red += r; green += g; blue += b; }
                    if (max >= minBrightness && max - min >= _cfg.MinSaturation &&
                        (max - min) / (double)max >= _cfg.HaloSaturation) saturated++;
                    if (max >= _cfg.CoreBrightness && min >= WashedCoreBrightness) washed++;
                    if (max >= minBrightness && (max - min >= _cfg.MinSaturation ||
                        max >= _cfg.CoreBrightness && min >= WashedCoreBrightness &&
                        HasColoredNeighbour(frame, x, y, x0, x1))) count++;
                }
                litColumn[x - x0] = count >= _cfg.MinColumnPixels;
                washedColumn[x - x0] = washed >= _cfg.MinCorePixels;
                weakColumn[x - x0] = weak >= _cfg.MinColumnPixels;
                strongColumn[x - x0] = saturated >= _cfg.MinColumnPixels;
                columnHue[x - x0] = weak == 0 ? -1 : new LedColor(red / weak, green / weak, blue / weak).Hue;
            }

            for (int x = 0; x < litColumn.Length; x++)
            {
                if (!weakColumn[x]) continue;
                int end = x;
                while (end + 1 < weakColumn.Length && weakColumn[end + 1]) end++;
                var run = Enumerable.Range(x, end - x + 1).ToList();
                var seeds = run.Where(nx => strongColumn[nx]).ToList();
                // Recover a whole connected light, not an arbitrary radius around a colour edge.
                // Weak housings without their own strong seeds cannot borrow a nearby LED's seeds.
                if (run.Count <= maxWidth && seeds.Count >= StrongEdgeColumns && run.All(nx =>
                {
                    double hue = Math.Abs(columnHue[seeds[0]] - columnHue[nx]);
                    return Math.Min(hue, 360 - hue) <= EdgeHueTolerance;
                })) foreach (int nx in run) litColumn[nx] = true;
                x = end;
            }

            // A light blown out to white through its middle leaves colour only at its two edges.
            // The white belongs to that light, so the edges are one blob: PMR's R8 LMS GT4 Evo read
            // ten green and yellow lights as nineteen. LEDs are separated by dark housing, never by
            // white, so this cannot join two of them.
            bool WashedBetween(int from, int to)
            {
                if (to - from <= 1) return false;
                for (int x = from + 1; x < to; x++) if (!washedColumn[x - x0]) return false;
                return true;
            }

            var blobs = new List<LitBlob>();
            int start = -1;
            for (int i = 0; i <= litColumn.Length; i++)
            {
                bool lit = i < litColumn.Length && litColumn[i];
                if (lit && start < 0) start = i;
                if (!lit && start >= 0)
                {
                    int left = start + x0, right = i - 1 + x0;
                    // A short valley may separate two complete LEDs whose combined glow is wider
                    // than one light. Keep them separate even in the wider cluster fallback.
                    var previous = blobs.Count > 0 ? blobs[blobs.Count - 1] : null;
                    bool washedCentre = previous != null && WashedBetween(previous.Right, left) &&
                                        right - previous.Left + 1 <= maxWidth;
                    if (previous != null && (washedCentre ||
                        left - previous.Right <= _cfg.MergeGap &&
                        (right - previous.Left + 1 <= _cfg.MaxWidth ||
                         previous.Width < _cfg.MinWidth || right - left + 1 < _cfg.MinWidth)))
                        previous.Right = right;
                    else
                        blobs.Add(new LitBlob { Left = left, Right = right });
                    start = -1;
                }
            }

            smeared = blobs.Any(b => b.Width > maxWidth);
            blobs.RemoveAll(b => b.Width < _cfg.MinWidth || b.Width > maxWidth);
            foreach (var blob in blobs) blob.Color = MeasureColor(frame, blob, y0, y1, minBrightness);
            return blobs;
        }

        // Tone mapping can wash the middle of a small rectangular LED completely white. Keep that
        // bridge only beside coloured pixels on the same scanline, never across the dark LED gap.
        private bool HasColoredNeighbour(PixelFrame frame, int x, int y, int x0, int x1)
        {
            for (int nx = Math.Max(x0, x - _cfg.HaloReach); nx < Math.Min(x1, x + _cfg.HaloReach + 1); nx++)
            {
                frame.GetPixel(nx, y, out int r, out int g, out int b);
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max >= _cfg.MinBrightness && max - min >= Math.Max(PositionSaturation, _cfg.MinSaturation)) return true;
            }
            return false;
        }

        /// <summary>Lights found by their white-hot centres, each coloured by the glow around it.</summary>
        private List<LitBlob> DetectCores(PixelFrame frame, int x0, int x1, int y0, int y1)
        {
            var core = new bool[x1 - x0];
            for (int x = x0; x < x1; x++)
            {
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    if (!AtLedHeight(x, y)) continue;
                    frame.GetPixel(x, y, out int r, out int g, out int b);
                    if (Math.Min(r, Math.Min(g, b)) >= _cfg.CoreBrightness) count++;
                }
                core[x - x0] = count >= _cfg.MinCorePixels;
            }

            var blobs = new List<LitBlob>();
            int start = -1;
            for (int i = 0; i <= core.Length; i++)
            {
                bool lit = i < core.Length && core[i];
                if (lit && start < 0) start = i;
                if (!lit && start >= 0)
                {
                    int left = start + x0, right = i - 1 + x0;
                    if (blobs.Count > 0 && left - blobs[blobs.Count - 1].Right <= _cfg.MergeGap)
                        blobs[blobs.Count - 1].Right = right;
                    else
                        blobs.Add(new LitBlob { Left = left, Right = right });
                    start = -1;
                }
            }
            blobs.RemoveAll(b => b.Width < _cfg.MinWidth || b.Width > _cfg.MaxWidth);
            foreach (var blob in blobs) blob.Color = MeasureGlow(frame, blob, x0, x1, y0, y1);
            blobs.RemoveAll(b => b.Color.Hue < 0);     // white with no coloured glow: a highlight, not a light
            return blobs;
        }

        /// <summary>
        /// Colour of the glow round a white-hot centre: its most strongly coloured pixels, within a few
        /// pixels of the centre, so the neighbours' glow and a tinted surface don't get a say.
        /// </summary>
        private LedColor MeasureGlow(PixelFrame frame, LitBlob blob, int x0, int x1, int y0, int y1)
        {
            int top = int.MaxValue, bottom = int.MinValue;
            for (int x = blob.Left; x <= blob.Right; x++)
                for (int y = y0; y < y1; y++)
                {
                    if (!AtLedHeight(x, y)) continue;
                    frame.GetPixel(x, y, out int r, out int g, out int b);
                    if (Math.Min(r, Math.Min(g, b)) < _cfg.CoreBrightness) continue;
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            if (top > bottom) return new LedColor(0, 0, 0);

            var glow = new List<Tuple<double, int, int, int>>();
            for (int x = Math.Max(x0, blob.Left - _cfg.HaloReach); x <= Math.Min(x1 - 1, blob.Right + _cfg.HaloReach); x++)
                for (int y = Math.Max(y0, top - _cfg.HaloReach); y <= Math.Min(y1 - 1, bottom + _cfg.HaloReach); y++)
                {
                    if (!AtLedHeight(x, y)) continue;
                    frame.GetPixel(x, y, out int r, out int g, out int b);
                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    if (max < _cfg.MinBrightness) continue;
                    double saturation = (max - min) / (double)max;
                    if (saturation >= _cfg.HaloSaturation) glow.Add(Tuple.Create(saturation, r, g, b));
                }
            if (glow.Count == 0) return new LedColor(0, 0, 0);
            var strongest = glow.OrderByDescending(p => p.Item1).Take(Math.Max(1, glow.Count * 3 / 10)).ToList();
            return new LedColor((int)strongest.Average(p => p.Item2), (int)strongest.Average(p => p.Item3), (int)strongest.Average(p => p.Item4));
        }

        /// <summary>
        /// Colour of the light's brightest pixels. The dim halo around a light in game is a blend with
        /// the dark cockpit behind it, so averaging the whole blob would drag every colour towards black.
        /// </summary>
        private LedColor MeasureColor(PixelFrame frame, LitBlob blob, int y0, int y1, int minBrightness)
        {
            var values = new List<int>();
            for (int x = blob.Left; x <= blob.Right; x++)
                for (int y = y0; y < y1; y++)
                {
                    if (!AtLedHeight(x, y)) continue;
                    frame.GetPixel(x, y, out int r, out int g, out int b);
                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    if (max >= minBrightness && max - min >= _cfg.MinSaturation) values.Add(max);
                }
            if (values.Count == 0) return new LedColor(0, 0, 0);
            values.Sort();
            int cut = values[(int)(values.Count * 0.7)];

            long sr = 0, sg = 0, sb = 0; int n = 0;
            for (int x = blob.Left; x <= blob.Right; x++)
                for (int y = y0; y < y1; y++)
                {
                    if (!AtLedHeight(x, y)) continue;
                    frame.GetPixel(x, y, out int r, out int g, out int b);
                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    if (max >= minBrightness && max - min >= _cfg.MinSaturation && max >= cut)
                    {
                        sr += r; sg += g; sb += b; n++;
                    }
                }
            return n == 0 ? new LedColor(0, 0, 0) : new LedColor((int)(sr / n), (int)(sg / n), (int)(sb / n));
        }
    }
}
