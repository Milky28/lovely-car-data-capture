using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LovelyCarDataCapture.Plugin
{
    /// <summary>
    /// The plugin's icon in SimHub's menu: a row of rev lights, drawn rather than shipped as a file so
    /// the plugin stays a single DLL.
    /// </summary>
    internal static class LedStripIcon
    {
        public static ImageSource Create()
        {
            const int size = 32;
            var brushes = new[]
            {
                Brushes.LimeGreen, Brushes.LimeGreen, Brushes.Gold, Brushes.Gold,
                Brushes.OrangeRed, Brushes.OrangeRed,
            };

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, size, size));
                double radius = 2.4;
                double gap = (size - 4) / (double)brushes.Length;
                for (int i = 0; i < brushes.Length; i++)
                {
                    double x = 2 + gap * i + gap / 2;
                    context.DrawEllipse(brushes[i], null, new Point(x, size / 2.0), radius, radius);
                }
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
