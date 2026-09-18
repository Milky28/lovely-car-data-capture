using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LovelyCarDataCapture.Plugin
{
    /// <summary>
    /// A small panel over the game showing what the plugin is doing: what a mapped button just did,
    /// and how the capture is going while driving.
    /// </summary>
    /// <remarks>
    /// Mapped buttons are pressed with a game in front of everything, where SimHub's own window and
    /// its log can't be seen, so without this a button press tells you nothing at all.
    /// <para>
    /// The window never takes focus: WS_EX_NOACTIVATE keeps a click on it from pulling a borderless
    /// game out of the foreground, which would drop the very lap being captured.
    /// </para>
    /// </remarks>
    internal sealed class CaptureOverlay : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int newStyle);

        /// <summary>Widest the panel gets before text wraps; narrow enough not to cover much of the track.</summary>
        private const double PanelWidth = 460;

        /// <summary>How long a message stays up after the button that caused it.</summary>
        private static readonly TimeSpan MessageTime = TimeSpan.FromSeconds(6);

        private readonly Func<string> _status;
        private readonly Action<int, int> _moved;
        private readonly TextBlock _statusText;
        private readonly TextBlock _messageText;
        private readonly DispatcherTimer _timer;
        private DateTime _messageUntil = DateTime.MinValue;
        private bool _capturing;
        private bool _suppressed;

        public CaptureOverlay(Func<string> status, Action<int, int> moved, int x, int y)
        {
            _status = status;
            _moved = moved;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Title = "Lovely Car Data Capture";

            if (x > 0 || y > 0) { Left = x; Top = y; }
            else
            {
                Left = SystemParameters.WorkArea.Left + 40;
                Top = SystemParameters.WorkArea.Top + 40;
            }

            var title = new TextBlock
            {
                Text = "Lovely Car Data Capture",
                Foreground = Brushes.OrangeRed,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
            };
            _statusText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = PanelWidth,
                Margin = new Thickness(0, 4, 0, 0),
            };
            _messageText = new TextBlock
            {
                Foreground = Brushes.Gainsboro,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = PanelWidth,
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = Visibility.Collapsed,
            };

            var stack = new StackPanel { Margin = new Thickness(12, 8, 12, 10) };
            stack.Children.Add(title);
            stack.Children.Add(_statusText);
            stack.Children.Add(_messageText);

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(215, 16, 16, 16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(160, 255, 69, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = stack,
            };

            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            LocationChanged += (s, e) => _moved((int)Left, (int)Top);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Closed += (s, e) => _timer.Stop();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
        }

        /// <summary>Shows what a button just did; it clears itself after a few seconds.</summary>
        public void Message(string text)
        {
            _messageText.Text = text;
            _messageText.Visibility = Visibility.Visible;
            _messageUntil = DateTime.UtcNow + MessageTime;
            Refresh();
        }

        public void SetCapturing(bool capturing)
        {
            _capturing = capturing;
            Refresh();
        }

        /// <summary>Keeps the panel out of sight, for a still of the screen it would otherwise be in.</summary>
        public void SetSuppressed(bool suppressed)
        {
            _suppressed = suppressed;
            Refresh();
        }

        private void Refresh()
        {
            if (DateTime.UtcNow > _messageUntil && _messageText.Visibility == Visibility.Visible)
                _messageText.Visibility = Visibility.Collapsed;

            _statusText.Text = _status();
            // Out of the way when there's nothing to say: not capturing and the last message has gone.
            bool wanted = !_suppressed && (_capturing || _messageText.Visibility == Visibility.Visible);
            if (wanted && Visibility != Visibility.Visible) Show();
            else if (!wanted && Visibility == Visibility.Visible) Hide();
        }
    }
}
