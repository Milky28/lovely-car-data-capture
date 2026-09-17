using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Plugin
{
    /// <summary>
    /// The plugin's page in SimHub. Everything else is driven by mapped buttons; this exists because
    /// a capture box has to be put somewhere by hand, and typing screen coordinates is no way to do it.
    /// </summary>
    internal sealed class ScreenSettingsControl : UserControl
    {
        private readonly CaptureSettings _settings;
        private readonly Action _save;
        private readonly Func<PixelRect, string> _test;
        private readonly Action<Action<PixelRect>, Func<PixelRect, string>> _showBox;
        private readonly TextBlock _region = new TextBlock { Margin = new Thickness(0, 4, 0, 4) };
        private readonly TextBlock _result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.Gray };

        public ScreenSettingsControl(CaptureSettings settings, Action save, Func<PixelRect, string> test,
                                     Action<Action<PixelRect>, Func<PixelRect, string>> showBox)
        {
            _settings = settings;
            _save = save;
            _test = test;
            _showBox = showBox;

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = "Reading the rev lights off the screen",
                FontSize = 18,
                Margin = new Thickness(0, 0, 0, 8),
            });
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Text = "For games that don't report their rev lights, the plugin can watch them on screen instead. " +
                       "Put the box over the car's lights, then capture as usual: rev slowly from idle to the limiter " +
                       "a few times. The game must run in borderless or windowed mode, and the camera must stay still.",
            });

            var enabled = new CheckBox
            {
                Content = "Read the rev lights off the screen while capturing",
                IsChecked = settings.ScreenCapture,
                Margin = new Thickness(0, 0, 0, 8),
            };
            enabled.Checked += (s, e) => { _settings.ScreenCapture = true; _save(); };
            enabled.Unchecked += (s, e) => { _settings.ScreenCapture = false; _save(); };
            panel.Children.Add(enabled);

            panel.Children.Add(_region);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var place = new Button { Content = "Position the box…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            place.Click += (s, e) => _showBox(Saved, _test);
            buttons.Children.Add(place);

            var testNow = new Button { Content = "Test now", Padding = new Thickness(12, 4, 12, 4) };
            testNow.Click += (s, e) =>
            {
                var box = Box();
                _result.Text = box.Width < 8 ? "Position the box first." : _test(box);
            };
            buttons.Children.Add(testNow);
            panel.Children.Add(buttons);
            panel.Children.Add(_result);

            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 16, 0, 0),
                Foreground = Brushes.Gray,
                Text = "F1 and iRacing report their lights directly, so screen reading is skipped for them. " +
                       "The pit limiter is ignored, as it drives its own patterns.",
            });

            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            ShowRegion();
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
                ? "No box set yet."
                : "Box: " + box.Width + " x " + box.Height + " pixels at " + box.X + ", " + box.Y + ".";
        }
    }
}
