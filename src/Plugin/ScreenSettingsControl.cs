using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Plugin
{
    /// <summary>
    /// The plugin's page in SimHub: the buttons a capture actually needs, then everything else folded
    /// away behind them.
    /// </summary>
    /// <remarks>
    /// The detail earns its place - these steps are easy to get wrong in ways that only show up later
    /// in the exported file - but read end to end it's a wall of text, and none of it is needed on the
    /// second capture. So what you press sits at the top, what it's for sits under it in a line, and
    /// the reasons are one click away.
    /// </remarks>
    internal sealed class ScreenSettingsControl : UserControl
    {
        private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(150, 150, 150));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(255, 120, 60));

        private readonly CaptureSettings _settings;
        private readonly Action _save;
        private readonly Func<PixelRect, string> _test;
        private readonly Action<Action<PixelRect>, Func<PixelRect, string>> _showBox;
        private readonly Action<string> _say;
        private readonly Func<bool> _capturing;
        private readonly Func<string> _statusText;
        private readonly DispatcherTimer _timer;

        private readonly TextBlock _region = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Dim };
        private readonly TextBlock _result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = Dim, MaxWidth = 620 };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
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
            _capturing = capturing;
            _statusText = status;

            var panel = new StackPanel { Margin = new Thickness(18, 14, 18, 24), MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Left };

            panel.Children.Add(new TextBlock
            {
                Text = "Lovely Car Data Capture",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2),
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Reads a car's rev lights while you drive and writes a car file, with a report saying where every value came from.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Dim,
                Margin = new Thickness(0, 0, 0, 14),
            });

            // ---- what you actually press ----
            var box = new Button { Content = "Position the box…", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
            box.Click += (s, e) => _showBox(Saved, _test);
            var testNow = new Button { Content = "Test", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
            testNow.Click += (s, e) =>
            {
                var region = Box();
                _result.Text = region.Width < 8 ? "Position the box first." : _test(region);
            };
            _start = new Button { Content = "Start capture", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
            _start.Click += (s, e) => { start(); Tick(); };
            _stop = new Button { Content = "Stop and export", Padding = new Thickness(14, 6, 14, 6) };
            _stop.Click += (s, e) => { stop(); Tick(); };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(box);
            buttons.Children.Add(testNow);
            buttons.Children.Add(_start);
            buttons.Children.Add(_stop);

            var actions = new StackPanel();
            actions.Children.Add(buttons);
            actions.Children.Add(new Border { Margin = new Thickness(0, 8, 0, 0), Child = _region });
            actions.Children.Add(_status);
            actions.Children.Add(_result);
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 120, 60)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 10, 12, 12),
                Child = actions,
            });

            panel.Children.Add(new TextBlock
            {
                Text = "In short: frame the rev lights, start the capture, rev slowly to the limiter a few times, stop and export.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 14, 0, 8),
            });

            // ---- the detail, folded away ----
            panel.Children.Add(Section("Before the first capture", true,
                Bullet("The game must run borderless or windowed. Nothing outside it can read the screen in exclusive fullscreen."),
                Bullet("The cockpit camera has to stay still: turn off head movement, camera shake and motion blur, and leave seat position and field of view alone once the box is placed."),
                Bullet("Sit in the car with the lights in view before pressing Position the box, and frame them with a little room to spare."),
                Bullet("The game keeps the keyboard while it's in front, so move the frame with Ctrl+Alt+arrows, resize it with Ctrl+Alt+Shift+arrows, and finish with Ctrl+Alt+Enter. It saves as you go and shows what it can see while you rev.")));

            panel.Children.Add(Section("Driving a capture worth having", false,
                Bullet("Rev from below the first light right up to the limiter, smoothly and slowly, and hold the limiter a second or two so the redline is seen."),
                Bullet("A few climbs in two or three gears is plenty. Braking mid-sweep is fine: a half-caught light is reported and left out rather than written down as if it were measured."),
                Bullet("Gears are pooled. Where two of them saw the same light they have to agree, and what one missed another fills in, so no single gear needs sweeping end to end."),
                Bullet("Pit-limiter time is ignored, and F1 and iRacing are read from telemetry instead of the screen.")));

            panel.Children.Add(Section("Where the files go", false,
                Paragraph("Stop and export writes the car file and its report to:"),
                new TextBlock
                {
                    Text = outputFolder(),
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = Dim,
                    Margin = new Thickness(0, 4, 0, 6),
                },
                Paragraph("Read the report before submitting anything: it lists every measurement with the range it was pinned down to, what was left out and why, and anything ATSR would trip over. The RPM LED Builder opens the file for a last look.")));

            panel.Children.Add(Section("Buttons you can map", false,
                Paragraph("All of this works from here, so mapping is only needed for a game that stops running when it loses focus. In SimHub's Controls and events:"),
                Bullet("StartCapture, StopAndExport, ShowCaptureBox to place the frame, and ResetCapture to throw away what's been recorded."),
                Bullet("MarkLed, MarkRedline and UndoMark, for games whose lights can't be read on screen: pressed by hand as each light comes on.")));

            // ---- options ----
            panel.Children.Add(new TextBlock
            {
                Text = "Options",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 18, 0, 2),
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Hover any of these for what it does.",
                Foreground = Dim,
                Margin = new Thickness(0, 0, 0, 6),
            });
            panel.Children.Add(Check("Read the rev lights off the screen", settings.ScreenCapture,
                                     "Needed for every game except F1 and iRacing, which report their lights directly.",
                                     v => _settings.ScreenCapture = v));
            panel.Children.Add(FrameRate());
            panel.Children.Add(Check("Use the measured values for gears that weren't swept", settings.CopyMeasuredToOtherGears,
                                     "For when only one gear could be swept cleanly. Two gears that agree are pooled anyway. Without " +
                                     "this, unswept gears keep the repo file's values and the file ends up saying two different things.",
                                     v => _settings.CopyMeasuredToOtherGears = v));
            panel.Children.Add(Check("Keep each capture's raw frames", settings.SaveCaptureFrames,
                                     "Writes <car>.frames.csv next to the export: every frame's RPM and the lights seen in it. " +
                                     "It lets a capture be checked again later, by a newer version of the plugin or when a value " +
                                     "looks odd, without driving it again. A few MB for a long capture.",
                                     v => _settings.SaveCaptureFrames = v));
            panel.Children.Add(Check("Start from the car's file in the repo", settings.UseRepoFile,
                                     "Looks the car up on GitHub, read-only, and keeps its name, colours, gaps and anything not measured.",
                                     v => _settings.UseRepoFile = v));
            panel.Children.Add(Check("Also write exports to ATSR's Developer Mode folder", settings.CopyToAtsrDeveloperFolder,
                                     "Lets ATSR show the file on your wheel before you submit it. Turn Developer Mode on in ATSR's RPM " +
                                     "settings, and delete the copy afterwards or ATSR keeps using it instead of the repo's file.",
                                     v => _settings.CopyToAtsrDeveloperFolder = v));
            panel.Children.Add(Check("Show the panel over the game", settings.ShowOverlay,
                                     "Says what each button press did and how the capture is going. Drag it anywhere; it never takes focus.",
                                     v => _settings.ShowOverlay = v, "Show it now", () =>
                                     {
                                         if (_settings.ShowOverlay) _say("This is the panel. It appears over the game when you press a mapped button.");
                                         else _result.Text = "Tick \"Show the panel over the game\" first.";
                                     }));

            panel.Children.Add(new TextBlock
            {
                Text = @"RPM rounding and the LED count for brand-new cars live in PluginsData\Common\CapturePlugin.CaptureSettings.json, edited with SimHub closed.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Dim,
                Margin = new Thickness(0, 14, 0, 0),
            });

            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            ShowRegion();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _timer.Tick += (s, e) => Tick();
            Loaded += (s, e) => { _timer.Start(); Tick(); };
            Unloaded += (s, e) => _timer.Stop();
        }

        /// <summary>A heading that folds its detail away. The first one starts open, so there's somewhere to begin.</summary>
        private static Expander Section(string header, bool open, params UIElement[] contents)
        {
            var inside = new StackPanel { Margin = new Thickness(6, 6, 0, 8) };
            foreach (var item in contents) inside.Children.Add(item);
            return new Expander
            {
                Header = header,
                IsExpanded = open,
                Margin = new Thickness(0, 2, 0, 2),
                Content = inside,
            };
        }

        private static TextBlock Paragraph(string text) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
            MaxWidth = 630,
        };

        private static StackPanel Bullet(string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = "•", Margin = new Thickness(0, 0, 8, 0), Foreground = Accent });
            row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 610 });
            return row;
        }

        /// <summary>
        /// A choice as one line, with the why on hover. Five explanations stacked under five checkboxes
        /// read as a wall; five lines read as a list, and the reason is still there when it's wanted.
        /// </summary>
        private StackPanel Check(string label, bool value, string explanation, Action<bool> set,
                                 string buttonLabel = null, Action buttonAction = null)
        {
            var box = new CheckBox
            {
                Content = label,
                IsChecked = value,
                ToolTip = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 },
                VerticalAlignment = VerticalAlignment.Center,
            };
            box.Checked += (s, e) => { set(true); _save(); };
            box.Unchecked += (s, e) => { set(false); _save(); };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(box);
            if (buttonLabel != null)
            {
                var button = new Button { Content = buttonLabel, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
                button.Click += (s, e) => buttonAction();
                row.Children.Add(button);
            }
            return row;
        }

        /// <summary>
        /// How often the box is read. The one setting here with a cost as well as a benefit, so the
        /// hover says both: when 60 is worth it, and what it takes.
        /// </summary>
        private StackPanel FrameRate()
        {
            var choice = new ComboBox { Width = 70, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (int fps in new[] { 30, 60 }) choice.Items.Add(fps);
            choice.SelectedItem = _settings.ScreenCaptureFps >= 45 ? 60 : 30;
            choice.SelectionChanged += (s, e) =>
            {
                _settings.ScreenCaptureFps = (int)choice.SelectedItem;
                _save();
            };

            var label = new TextBlock { Text = "Frames read per second", VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 4),
                ToolTip = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 380,
                    Text = "30 is enough for most cars: a slow sweep crosses each light over many frames.\n\n" +
                           "Use 60 for a car whose lights blink at the limiter, so each dark flash spans enough frames " +
                           "to time it, and for anything that revs very quickly. It costs roughly twice the CPU while a " +
                           "capture runs, and applies from the next capture started.",
                },
            };
            row.Children.Add(label);
            row.Children.Add(choice);
            return row;
        }

        /// <summary>Keeps the buttons and the lines under them in step with what the capture is doing.</summary>
        private void Tick()
        {
            bool running = _capturing();
            _start.IsEnabled = !running;
            _stop.IsEnabled = running;
            _status.Text = _statusText();
            _status.Foreground = running ? Accent : Dim;
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
            var region = Box();
            _region.Text = region.Width < 8
                ? "No capture box placed yet."
                : "Capture box: " + region.Width + " x " + region.Height + " pixels at " + region.X + ", " + region.Y + ".";
        }
    }
}
