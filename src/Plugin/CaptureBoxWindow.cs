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
    /// <para>
    /// A game in front takes the keyboard with it, so plain arrow keys and Enter only reach this window
    /// when it has focus. The same moves are registered as system-wide hotkeys on Ctrl+Alt, which reach
    /// it whatever is in front, and the region is saved as it changes so nothing depends on a keypress
    /// landing here at all.
    /// </para>
    /// </remarks>
    internal sealed class CaptureBoxWindow : Window
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WmHotkey = 0x0312;
        private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004;
        private const uint VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28, VkReturn = 0x0D;
        /// <summary>Hotkey steps are coarser than the arrow keys: they're for getting close, not fine work.</summary>
        private const int HotkeyStep = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect32 { public int Left, Top, Right, Bottom; }

        /// <summary>Border width in device-independent pixels; it sits outside the captured region.</summary>
        private const double BorderWidth = 2;

        private readonly Action<PixelRect> _onSave;
        private readonly Func<PixelRect, string> _test;
        private readonly System.Windows.Threading.DispatcherTimer _preview;
        private readonly System.Windows.Threading.DispatcherTimer _autoSave;
        private readonly PixelRect _started;
        private bool _reading;
        private bool _hotkeys;
        private readonly Window _panel;
        private readonly TextBlock _status;
        private readonly TextBlock _size;

        public CaptureBoxWindow(PixelRect start, Action<PixelRect> onSave, Func<PixelRect, string> test)
        {
            _onSave = onSave;
            _test = test;
            _started = start;

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
            LocationChanged += (s, e) => { PlacePanel(); Changed(); };
            SizeChanged += (s, e) => { PlacePanel(); ShowSize(); Changed(); };
            Closed += (s, e) => _panel.Close();

            _status = new TextBlock
            {
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Width = 420,
                Margin = new Thickness(0, 0, 0, 6),
                Text = "Put the frame over the car's rev lights, just outside them. With the game in front, use " +
                       "Ctrl+Alt+arrows to move it and Ctrl+Alt+Shift+arrows to resize; Ctrl+Alt+Enter finishes. " +
                       "Click this panel first and plain arrows work too, a pixel at a time. It saves as you go.",
            };
            _size = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(MakeButton("Done (Ctrl+Alt+Enter)", (s, e) => Save()));
            buttons.Children.Add(MakeButton("Undo changes", (s, e) => Revert()));

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

            // Saved a moment after the frame stops moving, so dragging it doesn't write the settings
            // file on every pixel.
            _autoSave = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _autoSave.Tick += (s, e) => { _autoSave.Stop(); _onSave(Region()); ShowSize(); };
            Closed += (s, e) => { _preview.Stop(); _autoSave.Stop(); };

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
            _size.Text = r.Width + " x " + r.Height + " pixels at " + r.X + ", " + r.Y +
                         (_autoSave.IsEnabled ? " - saving…" : " - saved") +
                         (_hotkeys ? "" : "  (Ctrl+Alt shortcuts are taken by something else)");
        }

        private void Changed()
        {
            if (!IsLoaded) return;
            _autoSave.Stop();
            _autoSave.Start();
        }

        /// <summary>Puts the frame back where it was when it opened, and saves that.</summary>
        private void Revert()
        {
            if (_started.Width < 8) { Close(); return; }
            Left = _started.X - BorderWidth;
            Top = _started.Y - BorderWidth;
            Width = _started.Width + BorderWidth * 2;
            Height = _started.Height + BorderWidth * 2;
            _onSave(Region());
            ShowSize();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var source = (HwndSource)PresentationSource.FromVisual(this);
            source.AddHook(OnWindowMessage);
            var handle = source.Handle;
            // Ctrl+Alt so a game's own controls are left alone; Shift on top of that resizes.
            _hotkeys = RegisterHotKey(handle, 1, ModControl | ModAlt, VkLeft)
                       & RegisterHotKey(handle, 2, ModControl | ModAlt, VkRight)
                       & RegisterHotKey(handle, 3, ModControl | ModAlt, VkUp)
                       & RegisterHotKey(handle, 4, ModControl | ModAlt, VkDown)
                       & RegisterHotKey(handle, 5, ModControl | ModAlt | ModShift, VkLeft)
                       & RegisterHotKey(handle, 6, ModControl | ModAlt | ModShift, VkRight)
                       & RegisterHotKey(handle, 7, ModControl | ModAlt | ModShift, VkUp)
                       & RegisterHotKey(handle, 8, ModControl | ModAlt | ModShift, VkDown)
                       & RegisterHotKey(handle, 9, ModControl | ModAlt, VkReturn);
            Closed += (s, args) => { for (int id = 1; id <= 9; id++) UnregisterHotKey(handle, id); };
        }

        private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != WmHotkey) return IntPtr.Zero;
            switch (wParam.ToInt32())
            {
                case 1: Move(-HotkeyStep, 0, false); break;
                case 2: Move(HotkeyStep, 0, false); break;
                case 3: Move(0, -HotkeyStep, false); break;
                case 4: Move(0, HotkeyStep, false); break;
                case 5: Move(-HotkeyStep, 0, true); break;
                case 6: Move(HotkeyStep, 0, true); break;
                case 7: Move(0, -HotkeyStep, true); break;
                case 8: Move(0, HotkeyStep, true); break;
                case 9: Save(); break;
                default: return IntPtr.Zero;
            }
            handled = true;
            return IntPtr.Zero;
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
        /// The region as it was last read, which stays valid after the window is gone: Win32 has no
        /// rectangle to give once the handle is destroyed.
        /// </summary>
        public PixelRect LastRegion { get; private set; }

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
            LastRegion = new PixelRect(r.Left + border, r.Top + border,
                                       Math.Max(1, width - border * 2), Math.Max(1, height - border * 2));
            return LastRegion;
        }

        private void Save()
        {
            _autoSave.Stop();
            _onSave(Region());
            Close();
        }

    }
}
