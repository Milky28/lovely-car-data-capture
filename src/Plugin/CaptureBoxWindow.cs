using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Plugin
{
    /// <summary>
    /// A frame the user drags over the car's rev lights in game. Whatever ends up inside it is what
    /// gets read while capturing.
    /// </summary>
    /// <remarks>
    /// The window itself is the capture region: its position is taken from Win32 rather than from WPF,
    /// which gives real screen pixels with no display-scaling arithmetic to get wrong. The middle is
    /// transparent so the lights stay visible underneath while it's being placed.
    /// </remarks>
    internal sealed class CaptureBoxWindow : Window
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect32 { public int Left, Top, Right, Bottom; }

        private readonly TextBlock _status;
        private readonly Action<PixelRect> _onSave;
        private readonly Func<PixelRect, string> _test;

        public CaptureBoxWindow(PixelRect start, Action<PixelRect> onSave, Func<PixelRect, string> test)
        {
            _onSave = onSave;
            _test = test;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Title = "Lovely Car Data capture box";
            if (start.Width >= 8 && start.Height >= 4)
            {
                Left = start.X; Top = start.Y; Width = start.Width; Height = start.Height;
            }
            else
            {
                Width = 440; Height = 100;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            _status = new TextBlock
            {
                Foreground = Brushes.White,
                Margin = new Thickness(4, 2, 4, 2),
                TextWrapping = TextWrapping.Wrap,
                Text = "Drag over the rev lights, resize from the bottom right. Enter saves, Esc cancels.",
            };

            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            bar.Children.Add(Button("Save", (s, e) => Save()));
            bar.Children.Add(Button("Test", (s, e) => RunTest()));
            bar.Children.Add(Button("Cancel", (s, e) => Close()));

            var caption = new StackPanel { Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)) };
            caption.Children.Add(_status);
            caption.Children.Add(bar);

            var grip = new Thumb { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE };
            grip.DragDelta += (s, e) =>
            {
                Width = Math.Max(40, Width + e.HorizontalChange);
                Height = Math.Max(20, Height + e.VerticalChange);
            };

            var layout = new Grid();
            // The caption sits above the frame so it never covers the lights being framed.
            layout.Children.Add(new Border
            {
                BorderBrush = Brushes.OrangeRed,
                BorderThickness = new Thickness(2),
                Child = new Grid { Children = { grip } },
            });
            var captionHost = new Border
            {
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, -58, 0, 0),
                Child = caption,
            };
            layout.Children.Add(captionHost);
            Content = layout;

            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) Save();
                else if (e.Key == Key.Escape) Close();
            };
        }

        private static Button Button(string text, RoutedEventHandler click)
        {
            var button = new Button { Content = text, Margin = new Thickness(4, 0, 0, 4), Padding = new Thickness(10, 2, 10, 2) };
            button.Click += click;
            return button;
        }

        /// <summary>The region in real screen pixels, taken from the window itself.</summary>
        public PixelRect Region()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero && GetWindowRect(handle, out var r))
                return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return new PixelRect((int)Left, (int)Top, (int)Width, (int)Height);
        }

        private void Save()
        {
            _onSave(Region());
            Close();
        }

        /// <summary>
        /// Hides the frame for a moment and reads the region, so the count shown is what the capture
        /// would see rather than this window's own border.
        /// </summary>
        private void RunTest()
        {
            var region = Region();
            Visibility = Visibility.Hidden;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                string message;
                try { message = _test(region); }
                catch (Exception ex) { message = "Test failed: " + ex.Message; }
                Visibility = Visibility.Visible;
                _status.Text = message;
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
}
