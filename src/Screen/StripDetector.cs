using System;
using System.Collections.Generic;

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
    }

    /// <summary>
    /// Finds the lit rev lights in a frame by colour rather than at fixed positions: lights are bright
    /// and saturated, while unlit lights, the dash display and the cockpit around them are not. Small
    /// camera movement therefore doesn't need the region to be re-aimed.
    /// </summary>
    internal sealed class StripDetector
    {
        private readonly DetectorSettings _cfg;

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

            var litColumn = new bool[x1 - x0];
            for (int x = x0; x < x1; x++)
            {
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    frame.GetPixel(x, y, out int r, out int g, out int b);
                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    if (max >= _cfg.MinBrightness && max - min >= _cfg.MinSaturation) count++;
                }
                litColumn[x - x0] = count >= _cfg.MinColumnPixels;
            }

            int start = -1;
            for (int i = 0; i <= litColumn.Length; i++)
            {
                bool lit = i < litColumn.Length && litColumn[i];
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
            foreach (var blob in blobs) blob.Color = MeasureColor(frame, blob, y0, y1);
            return blobs;
        }

        /// <summary>
        /// Colour of the light's brightest pixels. The dim halo around a light in game is a blend with
        /// the dark cockpit behind it, so averaging the whole blob would drag every colour towards black.
        /// </summary>
        private LedColor MeasureColor(PixelFrame frame, LitBlob blob, int y0, int y1)
        {
            var values = new List<int>();
            for (int x = blob.Left; x <= blob.Right; x++)
                for (int y = y0; y < y1; y++)
                {
                    frame.GetPixel(x, y, out int r, out int g, out int b);
                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    if (max >= _cfg.MinBrightness && max - min >= _cfg.MinSaturation) values.Add(max);
                }
            if (values.Count == 0) return new LedColor(0, 0, 0);
            values.Sort();
            int cut = values[(int)(values.Count * 0.7)];

            long sr = 0, sg = 0, sb = 0; int n = 0;
            for (int x = blob.Left; x <= blob.Right; x++)
                for (int y = y0; y < y1; y++)
                {
                    frame.GetPixel(x, y, out int r, out int g, out int b);
                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    if (max >= _cfg.MinBrightness && max - min >= _cfg.MinSaturation && max >= cut)
                    {
                        sr += r; sg += g; sb += b; n++;
                    }
                }
            return n == 0 ? new LedColor(0, 0, 0) : new LedColor((int)(sr / n), (int)(sg / n), (int)(sb / n));
        }
    }
}
