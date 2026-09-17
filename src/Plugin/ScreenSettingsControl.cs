using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Plugin
{
    /// <summary>
    /// The plugin's page in SimHub: what to do, in order, and the handful of choices worth making.
    /// </summary>
    /// <remarks>
    /// Everything else is driven by mapped buttons, but a capture box has to be put somewhere by hand,
    /// and the steps around it are easy to get wrong in ways that only show up later in the exported
    /// file, so they're spelled out here rather than left to the README.
    /// </remarks>
    internal sealed class ScreenSettingsControl : UserControl
    {
        private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(150, 150, 150));

        private readonly CaptureSettings _settings;
        private readonly Action _save;
        private readonly Func<PixelRect, string> _test;
        private readonly Action<Action<PixelRect>, Func<PixelRect, string>> _showBox;
        private readonly Action<string> _say;
        private readonly TextBlock _region = new TextBlock { Margin = new Thickness(0, 6, 0, 6), TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = Dim };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        private readonly Func<bool> _capturing;
        private readonly Func<string> _statusText;
        private readonly DispatcherTimer _timer;
        private readonly Button _start;
        private readonly Button _stop;

        public ScreenSettingsControl(CaptureSettings settings, Action save, Func<PixelRect, string> test,
                                     Action<Action<PixelRect>, Func<PixelRect, string>> showBox, Action<string> say,
                                     Func<string> outputFolder, Action start, Action stop, Func<bool> capturing,
                                     Func<string> status)
        {
            _settings = settings;
            _save = save;
            _test = test;
            _showBox = showBox;
            _say = say;

            var panel = new StackPanel { Margin = new Thickness(18, 14, 18, 24), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };

            panel.Children.Add(Heading("Lovely Car Data Capture", 20, 0));
            panel.Children.Add(Text("Records a car's rev-light RPMs while you drive and writes a Lovely Car Data car file, with a " +
                                    "report saying where every value came from. Nothing is uploaded. When the car is already in the " +
                                    "repo, that file is the starting point and only measured values replace its own."));

            panel.Children.Add(Heading("1. Map some buttons, or don't", 16, 18));
            panel.Children.Add(Text("A capture can be started and stopped from this page, so mapping is only needed for games that " +
                                    "stop running when they lose focus. In SimHub's Controls and events: StartCapture, StopAndExport, " +
                                    "ShowCaptureBox, and ResetCapture to throw away what has been recorded so far. For games whose " +
                                    "lights can't be read on screen there are also MarkLed, MarkRedline and UndoMark, pressed by hand " +
                                    "as each light comes on."));

            panel.Children.Add(Heading("2. Set the game up", 16, 18));
            panel.Children.Add(Bullets(
                "Borderless or windowed, not exclusive fullscreen: nothing outside the game can read the screen in fullscreen.",
                "A cockpit camera that stays still: turn off head movement, camera shake and motion blur.",
                "Don't change seat position or field of view once the box is placed."));

            panel.Children.Add(Heading("3. Put the box over the rev lights", 16, 18));
            panel.Children.Add(Text("Sit in the car with the lights in view and press the button below. Frame the lights with a " +
                                    "little room to spare. The game keeps the keyboard while it's in front, so use Ctrl+Alt+arrows to " +
                                    "move the frame, Ctrl+Alt+Shift+arrows to resize it, and Ctrl+Alt+Enter to finish. It saves as you " +
                                    "go, and shows what it can see while you rev."));
            panel.Children.Add(_region);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var place = new Button { Content = "Position the box…", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
            place.Click += (s, e) => _showBox(Saved, _test);
            buttons.Children.Add(place);
            var testNow = new Button { Content = "Test now", Padding = new Thickness(14, 5, 14, 5) };
            testNow.Click += (s, e) =>
            {
                var box = Box();
                _result.Text = box.Width < 8 ? "Position the box first." : _test(box);
            };
            buttons.Children.Add(testNow);
            panel.Children.Add(buttons);
            panel.Children.Add(_result);

            panel.Children.Add(Heading("4. Drive", 16, 18));
            panel.Children.Add(Bullets(
                "Start the capture, then rev from below the first light right up to the limiter, smoothly and slowly.",
                "Hold the limiter a second or two so the redline is seen.",
                "Do that a few times in one or two gears you can take cleanly. Braking mid-sweep is fine: a half-caught gear " +
                "is reported and left out rather than written down as if it were measured.",
                "Two gears measured right through that agree mean the car uses one set of lights, and the other gears follow " +
                "them. Only a car whose lights really do change per gear needs every gear swept.",
                "Pit-limiter time is ignored. F1 and iRacing are read from telemetry instead of the screen."));

            panel.Children.Add(Text("A game that keeps running while it hasn't got focus can be captured from here, with no buttons " +
                                    "mapped at all: leave the car idling, start the capture, then go and drive it.", 8));
            var captureButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            _start = new Button { Content = "Start capture", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
            _start.Click += (s, e) => { start(); Tick(); };
            captureButtons.Children.Add(_start);
            _stop = new Button { Content = "Stop and export", Padding = new Thickness(14, 5, 14, 5) };
            _stop.Click += (s, e) => { stop(); Tick(); };
            captureButtons.Children.Add(_stop);
            panel.Children.Add(captureButtons);
            panel.Children.Add(_status);

            panel.Children.Add(Heading("5. Export and check", 16, 18));
            panel.Children.Add(Text("Stop and export, from here or from a mapped button. The car file and its report are written to:"));
            panel.Children.Add(new TextBlock
            {
                Text = outputFolder(),
                Margin = new Thickness(0, 4, 0, 6),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Dim,
            });
            panel.Children.Add(Text("Read the report before submitting anything: it lists every measurement with the range it was " +
                                    "pinned down to, what was left out and why, and anything ATSR would trip over. The RPM LED " +
                                    "Builder opens the file for a last look."));

            panel.Children.Add(Heading("Options", 16, 22));
            panel.Children.Add(Check("Read the rev lights off the screen", settings.ScreenCapture,
                                     "Needed for every game except F1 and iRacing, which report their lights directly.",
                                     v => _settings.ScreenCapture = v));
            panel.Children.Add(Check("Use the measured values for gears that weren't swept", settings.CopyMeasuredToOtherGears,
                                     "For when only one gear could be swept cleanly. With two agreeing gears this happens anyway. " +
                                     "Without it, unswept gears keep the repo file's values and the file ends up saying two " +
                                     "different things.",
                                     v => _settings.CopyMeasuredToOtherGears = v));
            panel.Children.Add(Check("Start from the car's file in the repo", settings.UseRepoFile,
                                     "Looks the car up on GitHub, read-only, and keeps its name, colours, gaps and anything not measured.",
                                     v => _settings.UseRepoFile = v));
            panel.Children.Add(Check("Also write exports to ATSR's Developer Mode folder", settings.CopyToAtsrDeveloperFolder,
                                     "Lets ATSR show the file on your wheel before you submit it. Turn Developer Mode on in ATSR's " +
                                     "RPM settings, and delete the copy afterwards or ATSR keeps using it instead of the repo's file.",
                                     v => _settings.CopyToAtsrDeveloperFolder = v));
            panel.Children.Add(Check("Show the panel over the game", settings.ShowOverlay,
                                     "Says what each button press did and how the capture is going. Drag it anywhere; it never takes focus.",
                                     v => _settings.ShowOverlay = v));

            var preview = new Button
            {
                Content = "Show the panel now",
                Padding = new Thickness(14, 5, 14, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0),
            };
            preview.Click += (s, e) =>
            {
                if (_settings.ShowOverlay) _say("This is the panel. It appears over the game when you press a mapped button.");
                else _result.Text = "Tick \"Show the panel over the game\" first.";
            };
            panel.Children.Add(preview);

            panel.Children.Add(Text("Frame rate, RPM rounding and the LED count used for brand-new cars live in " +
                                    @"PluginsData\Common\CapturePlugin.CaptureSettings.json, edited with SimHub closed.", 18));

            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            ShowRegion();

            _capturing = capturing;
            _statusText = status;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _timer.Tick += (s, e) => Tick();
            Loaded += (s, e) => { _timer.Start(); Tick(); };
            Unloaded += (s, e) => _timer.Stop();
        }

        /// <summary>Keeps the buttons and the line under them in step with what the capture is doing.</summary>
        private void Tick()
        {
            bool running = _capturing();
            _start.IsEnabled = !running;
            _stop.IsEnabled = running;
            _status.Text = _statusText();
        }

        private static TextBlock Heading(string text, double size, double above) => new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, above, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        };

        private static TextBlock Text(string text, double above = 0) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, above, 0, 4),
        };

        private static StackPanel Bullets(params string[] lines)
        {
            var list = new StackPanel();
            foreach (var line in lines)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new TextBlock { Text = "•", Margin = new Thickness(0, 0, 8, 0), Foreground = Dim });
                row.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, MaxWidth = 700 });
                list.Children.Add(row);
            }
            return list;
        }

        private StackPanel Check(string label, bool value, string explanation, Action<bool> set)
        {
            var box = new CheckBox { Content = label, IsChecked = value };
            box.Checked += (s, e) => { set(true); _save(); };
            box.Unchecked += (s, e) => { set(false); _save(); };

            var group = new StackPanel { Margin = new Thickness(0, 6, 0, 10) };
            group.Children.Add(box);
            group.Children.Add(new TextBlock
            {
                Text = explanation,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Dim,
                Margin = new Thickness(22, 2, 0, 0),
                MaxWidth = 700,
            });
            return group;
        }

        private PixelRect Box() => new PixelRect(_settings.ScreenBoxX, _settings.ScreenBoxY, _settings.ScreenBoxWidth, _settings.ScreenBoxHeight);

        private void Saved(PixelRect region)
        {
            _settings.ScreenBoxX = region.X;
            _settings.ScreenBoxY = region.Y;
            _settings.ScreenBoxWidth = region.Width;
            _settings.ScreenBoxHeight = region.Height;
            _save();
            ShowRegion();
            _result.Text = _test(region);
        }

        private void ShowRegion()
        {
            var box = Box();
            _region.Text = box.Width < 8
                ? "No box placed yet."
                : "Box: " + box.Width + " x " + box.Height + " pixels at " + box.X + ", " + box.Y + ".";
        }
    }
}
