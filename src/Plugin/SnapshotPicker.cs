using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LovelyCarDataCapture.Screen;
using Brushes = System.Windows.Media.Brushes;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using WpfPoint = System.Windows.Point;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace LovelyCarDataCapture.Plugin
{
    /// <summary>A copy of the whole desktop, in real screen pixels, with where its top left corner sits.</summary>
    internal sealed class DesktopSnapshot
    {
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        private const int XVirtualScreen = 76, YVirtualScreen = 77, CxVirtualScreen = 78, CyVirtualScreen = 79;

        public PixelFrame Frame;
        public int Left, Top;

        /// <summary>
        /// Copies every monitor at once. The same measuring stick as the box itself: both come from the
        /// same process's view of the screen, so they agree whatever the display scaling is.
        /// </summary>
        public static DesktopSnapshot Take()
        {
            int left = GetSystemMetrics(XVirtualScreen), top = GetSystemMetrics(YVirtualScreen);
            int width = GetSystemMetrics(CxVirtualScreen), height = GetSystemMetrics(CyVirtualScreen);
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                    g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
                var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var bytes = new byte[data.Stride * height];
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                    return new DesktopSnapshot { Frame = PixelFrame.Bgra32(bytes, width, height, data.Stride), Left = left, Top = top };
                }
                finally { bitmap.UnlockBits(data); }
            }
        }

        /// <summary>Keep the exact pixels the detector saw inside the accepted capture box.</summary>
        public void SaveRegion(string path, PixelRect region)
        {
            int x = Math.Max(0, region.X - Left), y = Math.Max(0, region.Y - Top);
            int width = Math.Min(region.Width, Frame.Width - x), height = Math.Min(region.Height, Frame.Height - y);
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(region));
            var source = BitmapSource.Create(Frame.Width, Frame.Height, 96, 96, PixelFormats.Bgra32, null, Frame.Pixels, Frame.Stride);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(new CroppedBitmap(source, new Int32Rect(x, y, width, height))));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            using (var file = File.Create(path)) png.Save(file);
        }
    }

    /// <summary>
    /// Picks the capture box on a still of the screen instead of over the running game.
    /// </summary>
    /// <remarks>
    /// Some games keep the keyboard even from system-wide shortcuts, and most hide the pointer while
    /// they're in front, which leaves the live frame with no way to be moved. A still has neither
    /// problem: it's this window that's in front, the pointer works, and the lights don't move while
    /// the box is drawn round them. What the detector finds inside the box is shown as it's drawn.
    /// </remarks>
    internal sealed class SnapshotPickerWindow : Window
    {
        private readonly DesktopSnapshot _shot;
        private readonly Action<PixelRect> _onSave;
        private readonly Canvas _canvas = new Canvas();
        private readonly WpfRectangle _selection;
        private readonly TextBlock _found;
        private WpfPoint? _anchor;
        private Rect _rect = Rect.Empty;

        public SnapshotPickerWindow(DesktopSnapshot shot, PixelRect current, Action<PixelRect> onSave)
        {
            _shot = shot;
            _onSave = onSave;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            Title = "Lovely Car Data - pick the rev lights";
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            Cursor = Cursors.Cross;

            var frame = shot.Frame;
            var image = new System.Windows.Controls.Image
            {
                Source = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride),
                Stretch = Stretch.Fill,
            };

            _selection = new WpfRectangle
            {
                Stroke = Brushes.OrangeRed,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 255, 69, 0)),
                Visibility = Visibility.Collapsed,
            };
            _canvas.Background = Brushes.Transparent;   // so the whole still takes the mouse
            _canvas.Children.Add(_selection);

            _found = new TextBlock { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            var hint = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
            hint.Children.Add(new TextBlock
            {
                Text = "Draw a box round the rev lights, a little outside them. Arrows nudge it, Shift+arrows resize it. " +
                       "Enter or double-click keeps it, Esc cancels.",
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 560,
            });
            hint.Children.Add(_found);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var keep = new Button { Content = "Keep this box", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
            keep.Click += (s, e) => Accept();
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 3, 12, 3) };
            cancel.Click += (s, e) => Close();
            buttons.Children.Add(keep);
            buttons.Children.Add(cancel);
            hint.Children.Add(buttons);

            var banner = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(225, 20, 20, 20)),
                BorderBrush = Brushes.OrangeRed,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 24, 0, 0),
                Child = hint,
                Cursor = Cursors.Arrow,
            };

            var layers = new Grid();
            layers.Children.Add(image);
            layers.Children.Add(_canvas);
            layers.Children.Add(banner);
            Content = layers;

            _canvas.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2 && !_rect.IsEmpty) { Accept(); return; }
                _anchor = e.GetPosition(_canvas);
                _canvas.CaptureMouse();
                Show(new Rect(_anchor.Value, _anchor.Value));
            };
            _canvas.MouseMove += (s, e) =>
            {
                if (_anchor.HasValue && e.LeftButton == MouseButtonState.Pressed) Show(new Rect(_anchor.Value, e.GetPosition(_canvas)));
            };
            _canvas.MouseLeftButtonUp += (s, e) =>
            {
                _anchor = null;
                _canvas.ReleaseMouseCapture();
                Read();
            };
            KeyDown += OnKey;

            Loaded += (s, e) =>
            {
                // Start from the box already saved, if there is one, so a small correction stays small.
                if (current.Width >= 8 && current.Height >= 4)
                {
                    double scale = Scale();
                    Show(new Rect((current.X - _shot.Left) / scale, (current.Y - _shot.Top) / scale,
                                  current.Width / scale, current.Height / scale));
                    Read();
                }
                else _found.Text = "Nothing picked yet.";
                Activate();
                Focus();
            };
        }

        /// <summary>Screen pixels per unit of this window, which is drawn at whatever the display scaling is.</summary>
        private double Scale() => ActualWidth > 0 ? _shot.Frame.Width / ActualWidth : 1.0;

        private void Show(Rect rect)
        {
            _rect = rect;
            _selection.Visibility = Visibility.Visible;
            Canvas.SetLeft(_selection, rect.X);
            Canvas.SetTop(_selection, rect.Y);
            _selection.Width = rect.Width;
            _selection.Height = rect.Height;
        }

        /// <summary>The box in real screen pixels.</summary>
        private PixelRect Region()
        {
            double scale = Scale();
            return new PixelRect((int)Math.Round(_rect.X * scale) + _shot.Left, (int)Math.Round(_rect.Y * scale) + _shot.Top,
                                 (int)Math.Round(_rect.Width * scale), (int)Math.Round(_rect.Height * scale));
        }

        /// <summary>What the capture would see in the box, read off the still.</summary>
        private void Read()
        {
            if (_rect.IsEmpty || _rect.Width < 4 || _rect.Height < 4) { _found.Text = "Nothing picked yet."; return; }
            var region = Region();
            var inStill = new PixelRect(region.X - _shot.Left, region.Y - _shot.Top, region.Width, region.Height);
            List<LitBlob> blobs = new StripDetector().Detect(_shot.Frame, inStill);
            _found.Text = region.Width + " x " + region.Height + " pixels. " +
                          (blobs.Count == 0
                              ? "No lit lights in the box - that's fine if none were on when the still was taken."
                              : blobs.Count + " lit light(s) found: " + string.Join(", ", blobs.Select(b => b.Color.ToHex())));
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); return; }
            if (e.Key == Key.Enter) { Accept(); return; }
            if (_rect.IsEmpty) return;
            double step = ((Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 10 : 1) / Scale();
            bool resize = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            double dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
            double dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
            if (dx == 0 && dy == 0) return;
            var r = _rect;
            if (resize) r = new Rect(r.X, r.Y, Math.Max(4, r.Width + dx), Math.Max(4, r.Height + dy));
            else r.Offset(dx, dy);
            Show(r);
            Read();
            e.Handled = true;
        }

        private void Accept()
        {
            if (_rect.IsEmpty || _rect.Width < 4 || _rect.Height < 4) { _found.Text = "Draw a box first."; return; }
            _onSave(Region());
            Close();
        }
    }
}
