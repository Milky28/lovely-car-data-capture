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
    /// A frame the user puts over the car's rev lights in game. Whatever ends up inside it is what
    /// gets read while capturing.
    /// </summary>
    /// <remarks>
    /// The window itself is the capture region: its position is read from Win32 rather than WPF, which
    /// gives real screen pixels with no display-scaling arithmetic to get wrong, and its middle is
    /// transparent so the lights stay visible underneath.
    /// <para>
    /// The controls live in a second window beside the frame, because a game in borderless mode hides
    /// the mouse pointer whenever it has focus, and because anything drawn outside the frame's own
    /// bounds would be clipped away. For the same reason the frame can be moved and resized entirely
    /// from the keyboard.
    /// </para>
    /// <para>
    /// The border is drawn just outside the region rather than on it, so the region can be read while
    /// the frame is still up: the panel then shows what the capture would see, live, and the game can
    /// keep focus while the engine is revved.
    /// </para>
    /// </remarks>
    internal sealed class CaptureBoxWindow : Window
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect32 { public int Left, Top, Right, Bottom; }

        /// <summary>Border width in device-independent pixels; it sits outside the captured region.</summary>
        private const double BorderWidth = 2;

        private readonly Action<PixelRect> _onSave;
        private readonly Func<PixelRect, string> _test;
        private readonly System.Windows.Threading.DispatcherTimer _preview;
        private bool _reading;
        private readonly Window _panel;
        private readonly TextBlock _status;
        private readonly TextBlock _size;

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
                Left = start.X - BorderWidth;
                Top = start.Y - BorderWidth;
                Width = start.Width + BorderWidth * 2;
                Height = start.Height + BorderWidth * 2;
            }
            else
            {
                Width = 440; Height = 100;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            var grip = new Thumb
            {
                Width = 14,
                Height = 14,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = Cursors.SizeNWSE,
                Opacity = 0.8,
            };
            grip.DragDelta += (s, e) =>
            {
                Width = Math.Max(40, Width + e.HorizontalChange);
                Height = Math.Max(20, Height + e.VerticalChange);
            };

            Content = new Border
            {
                BorderBrush = Brushes.OrangeRed,
                BorderThickness = new Thickness(BorderWidth),
                Child = new Grid { Children = { grip } },
            };

            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            KeyDown += OnKey;
            LocationChanged += (s, e) => PlacePanel();
            SizeChanged += (s, e) => { PlacePanel(); ShowSize(); };
            Closed += (s, e) => _panel.Close();

            _status = new TextBlock
            {
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Width = 420,
                Margin = new Thickness(0, 0, 0, 6),
                Text = "Put the frame over the car's rev lights, just outside them. Drag it with the mouse, or move it " +
                       "with the arrow keys and resize it with Shift+arrows (hold Ctrl for bigger steps). Enter saves, " +
                       "Esc cancels. The line below updates as you rev, even while the game has focus.",
            };
            _size = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(MakeButton("Save (Enter)", (s, e) => Save()));
            buttons.Children.Add(MakeButton("Cancel (Esc)", (s, e) => Close()));

            var contents = new StackPanel { Margin = new Thickness(10) };
            contents.Children.Add(_status);
            contents.Children.Add(buttons);
            contents.Children.Add(_size);

            _panel = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 24)),
                BorderBrush = Brushes.OrangeRed,
                Title = "Lovely Car Data capture box controls",
                Content = new Border { BorderBrush = Brushes.OrangeRed, BorderThickness = new Thickness(1), Child = contents },
            };
            _panel.KeyDown += OnKey;
            _panel.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) _panel.DragMove(); };
            _panel.Loaded += (s, e) => PlacePanel();

            _preview = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _preview.Tick += (s, e) => Read();
            Closed += (s, e) => _preview.Stop();

            Loaded += (s, e) =>
            {
                _panel.Owner = this;
                _panel.Show();
                PlacePanel();
                ShowSize();
                Activate();
                Focus();
                Keyboard.Focus(this);
                _preview.Start();
                Read();
            };
        }

        private static Button MakeButton(string text, RoutedEventHandler click)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 3, 12, 3) };
            button.Click += click;
            return button;
        }

        /// <summary>Keeps the controls next to the frame: below it, or above when there's no room.</summary>
        private void PlacePanel()
        {
            if (!_panel.IsLoaded) return;
            double height = _panel.ActualHeight > 0 ? _panel.ActualHeight : 120;
            double below = Top + Height + 8;
            _panel.Left = Left;
            _panel.Top = below + height <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight
                ? below
                : Math.Max(SystemParameters.VirtualScreenTop, Top - height - 8);
        }

        private void ShowSize()
        {
            var r = Region();
            _size.Text = r.Width + " x " + r.Height + " pixels at " + r.X + ", " + r.Y;
        }

        /// <summary>Reads the region and shows what the capture would make of it right now.</summary>
        private void Read()
        {
            if (_reading) return;
            _reading = true;
            try { _status.Text = _test(Region()); }
            catch (Exception ex) { _status.Text = "Can't read the screen: " + ex.Message; }
            finally { _reading = false; }
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            int step = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 10 : 1;
            bool resize = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (e.Key)
            {
                case Key.Left: Move(-step, 0, resize); break;
                case Key.Right: Move(step, 0, resize); break;
                case Key.Up: Move(0, -step, resize); break;
                case Key.Down: Move(0, step, resize); break;
                case Key.T: Read(); break;
                case Key.Enter: Save(); break;
                case Key.Escape: Close(); break;
                default: return;
            }
            e.Handled = true;
        }

        private void Move(double dx, double dy, bool resize)
        {
            if (resize)
            {
                Width = Math.Max(40, Width + dx);
                Height = Math.Max(20, Height + dy);
            }
            else
            {
                Left += dx;
                Top += dy;
            }
            ShowSize();
        }

        /// <summary>
        /// The captured region in real screen pixels: the window minus its border, which is drawn
        /// outside what gets read. Taken from Win32 so display scaling needs no arithmetic.
        /// </summary>
        public PixelRect Region()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var r))
                return new PixelRect((int)(Left + BorderWidth), (int)(Top + BorderWidth),
                                     (int)(Width - BorderWidth * 2), (int)(Height - BorderWidth * 2));

            int width = r.Right - r.Left, height = r.Bottom - r.Top;
            double scale = ActualWidth > 0 ? width / ActualWidth : 1.0;
            int border = (int)Math.Round(BorderWidth * scale);
            return new PixelRect(r.Left + border, r.Top + border,
                                 Math.Max(1, width - border * 2), Math.Max(1, height - border * 2));
        }

        private void Save()
        {
            _onSave(Region());
            Close();
        }

    }
}
