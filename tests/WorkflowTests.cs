using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Plugin;
using LovelyCarDataCapture.Repo;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static FieldInfo WorkflowField(string name) => typeof(CapturePlugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        private static CapturePlugin WorkflowPlugin(string folder)
        {
            var plugin = new CapturePlugin { Settings = new CaptureSettings { ShowOverlay = false, UseRepoFile = false, OutputFolder = folder } };
            var session = new CaptureSession("Automobilista2", "Workflow Car");
            session.Redline.Record("2", 7000, 7500, 7500, 8000, 6);
            session.Marks.MarkLed("2", 5000);
            session.Marks.MarkLed("2", 6000);
            session.Marks.MarkRedline("2", 7500);
            WorkflowField("_session").SetValue(plugin, session);
            WorkflowField("_lookup").SetValue(plugin, Task.FromResult(RepoLookup.Disabled()));
            WorkflowField("_capturing").SetValue(plugin, true);
            WorkflowField("_exportPending").SetValue(plugin, true);
            return plugin;
        }

        private static void WorkflowExportRetry()
        {
            var root = Path.Combine(Path.GetTempPath(), "capture-workflow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var plugin = WorkflowPlugin(root);
            try
            {
                var session = WorkflowField("_session").GetValue(plugin);
                plugin.Settings.TopGearForNextExport = 13;
                Check(plugin.QueueExport().Wait(5000), "preparation failure completes");
                var failed = plugin.PageState();
                Check(failed.Pending && failed.ExportFailed && !failed.Exporting && !failed.Capturing, "failure leaves a stopped retryable capture");
                Equal("Retry export", failed.ExportButton, "retry label");
                Check(failed.CanExport && !failed.CanStart, "retry available without allowing a replacement capture");
                Check(failed.ExportSummary.Contains("Top gear") && failed.ExportSummary.Contains("Workflow Car"), "persistent error explains the failure with overlay off");
                Equal("", failed.ReportPath, "failure does not offer a stale report");
                Check(File.Exists(Path.Combine(failed.EvidenceFolder, "capture.json")), "failed composition keeps the capture metadata");
                var evidenceFolder = failed.EvidenceFolder;
                plugin.StartCapture();
                Check(ReferenceEquals(session, WorkflowField("_session").GetValue(plugin)), "start preserves the unsaved session");

                plugin.Settings.TopGearForNextExport = 0;
                var blocked = Path.Combine(root, "blocked");
                File.WriteAllText(blocked, "not a folder");
                plugin.Settings.OutputFolder = blocked;
                Check(plugin.QueueExport().Wait(5000), "file failure completes");
                Check(plugin.PageState().ExportFailed && plugin.PageState().CanExport, "file failure can also be retried");

                plugin.Settings.OutputFolder = root;
                Check(plugin.QueueExport().Wait(5000), "corrected capture exports without another drive");
                var saved = plugin.PageState();
                Check(!saved.Pending && !saved.ExportFailed && !saved.Exporting && saved.CanStart, "success releases the capture for replacement");
                Check(File.Exists(saved.ReportPath), "open report points at the completed report");
                Equal(evidenceFolder, saved.EvidenceFolder, "retry stays with the original capture archive");
                Check(File.Exists(Path.Combine(saved.EvidenceFolder, "car.json")) &&
                    File.Exists(Path.Combine(saved.EvidenceFolder, "report.txt")), "successful outputs accompany the original evidence");
                Equal(Path.Combine(root, "automobilista2"), saved.OutputFolder, "folder opens the exported game's directory");
                Check(saved.ExportSummary.Contains("Saved Workflow Car") && saved.ExportDetails.Contains("RPM:"), "persistent result includes identity and value sources");
                var report = File.ReadAllText(saved.ReportPath);
                Check(report.Contains("Workflow Car") && report.Contains("5000"), "retained marks reach the report");
                Check(ReferenceEquals(session, WorkflowField("_session").GetValue(plugin)), "all attempts use the original session");
                plugin.QueueExport().Wait(5000);
                Check(!Directory.Exists(Path.Combine(saved.OutputFolder, "backups")), "a repeated stop after success does not write another export");

                // A failed later attempt must not label the old report as the new capture's report.
                WorkflowField("_exportPending").SetValue(plugin, true);
                plugin.Settings.TopGearForNextExport = 13;
                plugin.QueueExport().Wait(5000);
                Equal("", plugin.PageState().ReportPath, "failed later attempt has no report link");
                Equal(saved.ReportPath, (string)WorkflowField("_lastReportPath").GetValue(plugin), "public last-success property is preserved");
                Equal(report, File.ReadAllText(saved.ReportPath), "earlier successful report is untouched");
                plugin.ResetCapture();
                Equal("", plugin.PageState().ReportPath, "discard does not relabel an older report as the failed attempt");
            }
            finally { plugin.ResetCapture(); Directory.Delete(root, true); }
        }

        private static void WorkflowExportExcludesOtherActions()
        {
            var root = Path.Combine(Path.GetTempPath(), "capture-workflow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var plugin = WorkflowPlugin(root);
            var lookup = new TaskCompletionSource<RepoLookup>();
            WorkflowField("_lookup").SetValue(plugin, lookup.Task);
            Task exporting = null;
            try
            {
                var session = WorkflowField("_session").GetValue(plugin);
                exporting = plugin.QueueExport();
                var busy = plugin.PageState();
                Check(busy.Exporting && !busy.CanStart && !busy.CanExport && !busy.CanDiscard, "export reserves all conflicting actions synchronously");
                Check(ReferenceEquals(exporting, plugin.QueueExport()), "a repeated stop shares the same export");
                plugin.StartCapture();
                plugin.ResetCapture();
                Check(ReferenceEquals(session, WorkflowField("_session").GetValue(plugin)), "mapped start/reset cannot replace a busy session");
                lookup.SetResult(RepoLookup.Disabled());
                Check(exporting.Wait(5000), "pending lookup completes the export");
                Check(!Directory.Exists(Path.Combine(root, "automobilista2", "backups")), "only one export was written");
            }
            finally
            {
                lookup.TrySetResult(RepoLookup.Disabled());
                exporting?.Wait(5000);
                plugin.ResetCapture();
                Directory.Delete(root, true);
            }
        }

        private static void WorkflowCarChangeKeepsCapture()
        {
            foreach (bool changeGame in new[] { false, true })
            {
                var plugin = WorkflowPlugin(Path.GetTempPath());
                var session = WorkflowField("_session").GetValue(plugin);
                var lookup = WorkflowField("_lookup").GetValue(plugin);
                plugin.StartCapture();
                Check(ReferenceEquals(session, WorkflowField("_session").GetValue(plugin)), "repeated start does not clear a recording");
                var frame = new ResetTelemetry();
                typeof(GameReaderCommon.StatusDataBase).GetProperty("CarId").SetValue(frame, changeGame ? "Workflow Car" : "Another Car");
                typeof(GameReaderCommon.StatusDataBase).GetProperty("Gear").SetValue(frame, "3");
                typeof(GameReaderCommon.StatusDataBase).GetProperty("Rpms").SetValue(frame, 6100.0);
                var telemetry = new GameReaderCommon.GameData
                {
                    GameName = changeGame ? "AssettoCorsaCompetizione" : "Automobilista2",
                    NewData = frame,
                };
                typeof(GameReaderCommon.GameData).GetProperty("GameRunning").SetValue(telemetry, true);
                plugin.DataUpdate(null, ref telemetry);
                var state = plugin.PageState();
                Check(!state.Capturing && state.Pending && state.CanExport && !state.CanStart, "identity change stops and retains capture");
                Equal("Export capture", state.ExportButton, "stopped capture has an export action");
                Check(state.Message.Contains("Workflow Car") && state.Message.Contains("is kept"), "car change is visible even without the overlay");
                plugin.DataUpdate(null, ref telemetry);
                plugin.StartCapture();
                Check(ReferenceEquals(session, WorkflowField("_session").GetValue(plugin)), "new telemetry and start cannot replace pending capture");
                Check(ReferenceEquals(lookup, WorkflowField("_lookup").GetValue(plugin)), "original repo lookup retained");
                plugin.ResetCapture();
                Check(plugin.PageState().CanStart && !plugin.PageState().Pending, "explicit discard allows another capture");
            }
        }

        private static void WorkflowBoxEnablesScreenReading()
        {
            var settings = new CaptureSettings();
            Check(!CapturePlugin.ApplyCaptureBox(settings, new PixelRect(1, 2, 3, 4), true), "too-small final box is rejected");
            Check(!settings.ScreenCapture && settings.ScreenBoxWidth == 0, "invalid box leaves settings untouched");
            Check(CapturePlugin.ApplyCaptureBox(settings, new PixelRect(10, 20, 100, 30), false), "live adjustment updates geometry");
            Check(!settings.ScreenCapture, "unfinished adjustment does not enable screen reading");
            Check(CapturePlugin.ApplyCaptureBox(settings, new PixelRect(11, 22, 120, 40), true), "valid final box accepted");
            Check(settings.ScreenCapture && settings.ScreenBoxX == 11 && settings.ScreenBoxY == 22 &&
                settings.ScreenBoxWidth == 120 && settings.ScreenBoxHeight == 40, "final box enables reading and retains its exact pixels");
            Check(!CapturePlugin.ApplyCaptureBox(settings, new PixelRect(0, 0, 100, 2), true), "too-short final box is rejected");
            Check(settings.ScreenCapture && settings.ScreenBoxHeight == 40, "invalid replacement keeps a valid box");
        }

        private static void WorkflowLiveStatus()
        {
            var plugin = WorkflowPlugin(Path.GetTempPath());
            var frame = new ResetTelemetry();
            void Set(object target, string name, object value)
            {
                var property = target.GetType().GetProperty(name);
                property.SetValue(target, Convert.ChangeType(value, property.PropertyType));
            }
            Set(frame, "CarId", "Workflow Car"); Set(frame, "Gear", "2"); Set(frame, "Rpms", 5000);
            var data = new GameReaderCommon.GameData { GameName = "Automobilista2", NewData = frame };
            Set(data, "GameRunning", true);
            var screen = (ScreenCaptureLoop)WorkflowField("_screen").GetValue(plugin);
            try
            {
                plugin.DataUpdate(null, ref data);
                Check(plugin.PageState().Status.StartsWith("Recording"), "usable telemetry records normally");
                Set(data, "GamePaused", true);
                plugin.DataUpdate(null, ref data);
                Check(plugin.PageState().Status.Contains("Game paused") && !plugin.PageState().Status.StartsWith("Recording"), "pause is not presented as recording");
                Set(data, "GamePaused", false); Set(data, "GameReplay", true);
                plugin.DataUpdate(null, ref data);
                Check(plugin.PageState().Status.Contains("Replay"), "replay gives an actionable status");
                Set(data, "GameReplay", false); Set(frame, "CarId", "");
                plugin.DataUpdate(null, ref data);
                Check(plugin.PageState().Status.Contains("Leave the menu"), "missing car explains how to resume");
                Set(frame, "CarId", "Workflow Car"); Set(frame, "PitLimiterOn", 1);
                plugin.DataUpdate(null, ref data);
                Check(plugin.PageState().Status.Contains("Pit limiter active"), "pit limiter explains skipped frames");
                Set(frame, "PitLimiterOn", 0);
                plugin.DataUpdate(null, ref data);
                Check(plugin.PageState().Status.StartsWith("Recording"), "good telemetry clears the pause reason");
                WorkflowField("_lastTelemetryAt").SetValue(plugin, CaptureClock.NowMilliseconds - 2000);
                Check(plugin.PageState().Status.Contains("Waiting for telemetry"), "stale telemetry cannot look like recording");
                plugin.DataUpdate(null, ref data);
                plugin.Settings.ScreenCapture = true;
                Check(plugin.PageState().Status.Contains("Pick a capture box"), "missing reader is actionable");
                screen.Target = () => new ScreenTarget();
                // Invalid geometry is rejected before any desktop copy, so this check needs no game.
                screen.Start(new PixelRect(0, 0, 0, 0), 30);
                Check(SpinWait.SpinUntil(() => screen.Guidance.Contains("Screen capture failed"), 1000), "worker exposes acquisition errors");
                Check(plugin.PageState().Status.Contains("Screen capture failed"), "page and overlay surface the worker failure");
            }
            finally { screen.Stop(); plugin.ResetCapture(); }
        }

        private static ScreenSettingsControl WorkflowPage(CaptureSettings settings, Func<CapturePageState> state, Action<string> open, Action builder = null)
            => new ScreenSettingsControl(settings, () => { }, box => "2 lights", (save, test) => { }, () => { }, message => { },
                () => "Capture output", () => { }, () => { }, () => { }, state, open, builder ?? (() => { }),
                () => new List<AtsrCopy>(), copy => null, () => { });

        private static IEnumerable<T> WorkflowControls<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T match) yield return match;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var item in WorkflowControls<T>(child)) yield return item;
        }

        private static void WorkflowSta(Action action)
        {
            Exception error = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Check(thread.Join(10000), "settings check completes");
            if (error != null) throw error;
        }

        private static void WorkflowPageReflectsState()
        {
            WorkflowSta(() =>
            {
                var settings = new CaptureSettings();
                var state = new CapturePageState { Status = "Not capturing", Source = "Screen reading off" };
                string opened = null;
                bool builderOpened = false;
                var page = WorkflowPage(settings, () => state, path => opened = path, () => builderOpened = true);
                page.Tick();
                Button Button(string name) => WorkflowControls<Button>(page).Single(b => (string)b.Content == name);
                Check(Button("Start capture").IsEnabled && !Button("Export capture").IsEnabled, "initial buttons");
                Check(!Button("Open report").IsEnabled && !Button("Open output folder").IsEnabled, "no last-export links before an attempt");
                Check(!Button("Copy JSON + open Builder").IsEnabled, "builder requires a successful export");
                settings.ScreenCapture = true;
                settings.ScreenBoxWidth = 100;
                settings.ScreenBoxHeight = 20;
                page.Tick();
                Check(WorkflowControls<CheckBox>(page).Single(c => (string)c.Content == "Read the rev lights off the screen").IsChecked == true, "external box save updates checkbox");
                Check(WorkflowControls<TextBlock>(page).Any(t => t.Text.Contains("100 x 20")), "external box save updates dimensions");

                state = new CapturePageState { Pending = true, ExportFailed = true, ExportSummary = "Workflow Car: Export failed. Retry export.", Message = "Capture kept." };
                page.Tick();
                Check(Button("Retry export").IsEnabled && !Button("Start capture").IsEnabled, "retry is reachable from page");
                Check(WorkflowControls<TextBlock>(page).Any(t => t.Text == state.ExportSummary), "durable failure is displayed");
                state.Exporting = true;
                page.Tick();
                Check(!Button("Exporting…").IsEnabled && !Button("Discard capture").IsEnabled && !Button("Pick the lights…").IsEnabled, "conflicting controls disabled while busy");

                state = new CapturePageState { ExportSummary = "Saved Workflow Car.", ExportDetails = "RPM: captured gear 2.", ReportPath = "report.txt", OutputFolder = "capture-folder", ProfilePath = "car.json", EvidenceFolder = "evidence-folder", Game = "automobilista2" };
                page.Tick();
                Button("Open report").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Equal("report.txt", opened, "report button opens matching report");
                Button("Open output folder").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Equal("capture-folder", opened, "output button opens matching folder");
                Button("Open capture files").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Equal("evidence-folder", opened, "capture files button opens this capture's immutable inputs");
                Button("Copy JSON + open Builder").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Check(builderOpened, "builder action connected");
                Check(WorkflowControls<TextBlock>(page).Any(t => t.Text.Contains("click Import copied capture") &&
                    t.Text.Contains("paste into the text box")), "handoff explains browser activation and paste fallback");
                Check(((string)Button("Copy JSON + open Builder").ToolTip).Contains("Click Import copied capture"), "tooltip uses clipboard-import action");
                Check(WorkflowControls<TextBlock>(page).Any(t => t.Text == state.ExportDetails), "report-derived detail displayed");
            });
        }

        private static void WorkflowBuilderLaunchUrl()
        {
            const string prefix = "https://milky28.github.io/rpm-led-builder/?capture=clipboard&sim=";
            foreach (var game in new[] { "assettocorsa", "assettocorsacompetizione", "assettocorsaevo", "automobilista2",
                "f12024", "f12025", "iracing", "lmu", "projectmotorracing", "rrre" })
                Equal(prefix + game, CapturePlugin.BuilderLaunchUrl(game), "normalized game reaches the Builder");
            Equal(prefix, CapturePlugin.BuilderLaunchUrl(null), "missing game still opens clipboard import");
            Equal(prefix, CapturePlugin.BuilderLaunchUrl(""), "empty game still opens clipboard import");
            Equal(prefix + "unknown%20%26%20sim%3Dother%23%25", CapturePlugin.BuilderLaunchUrl("unknown & sim=other#%"),
                "unknown games are escaped as one parameter, not rejected or interpreted as query syntax");
        }

        private static void RenderWorkflowPages(string folder)
        {
            Directory.CreateDirectory(folder);
            WorkflowSta(() =>
            {
                var settings = new CaptureSettings { ScreenCapture = true, ScreenBoxWidth = 378, ScreenBoxHeight = 56 };
                var state = new CapturePageState();
                var page = WorkflowPage(settings, () => state, path => { });
                var states = new Dictionary<string, CapturePageState>
                {
                    ["saved"] = new CapturePageState { Source = "Rev lights read off the screen", Status = "Stopped. 1420 frames, up to 8 lights at once",
                        ExportSummary = "Saved Ginetta G55 GT4 (AssettoCorsaCompetizione). From screen capture. Read the details and report before submitting.\nRPM: captured gears 2, 3 use trusted capture values where available; final rows may combine measured, pooled, and starting-file values.",
                        ExportDetails = "RPM: captured gears 2, 3 use trusted capture values where available.\nRPM: previous local values are final for retained gears 4, 5, 6.",
                        ReportPath = "ginetta.report.txt", OutputFolder = "Capture output", EvidenceFolder = "Capture files", ProfilePath = "ginetta.json", Game = "assettocorsacompetizione" },
                    ["retry"] = new CapturePageState { Pending = true, ExportFailed = true, Source = "Rev lights read off the screen", Status = "Capture kept for export: Ginetta G55 GT4. 1420 frames, up to 8 lights at once",
                        ExportSummary = "Ginetta G55 GT4 (AssettoCorsaCompetizione): Export failed: the local override has the wrong LED count. Previous files were kept. Check the local export and overrides.", OutputFolder = "Capture output" },
                    ["busy"] = new CapturePageState { Pending = true, Exporting = true, Source = "Rev lights read off the screen", Status = "Exporting Ginetta G55 GT4…", ExportSummary = "Exporting Ginetta G55 GT4 (AssettoCorsaCompetizione)…" },
                    ["waiting"] = new CapturePageState { Capturing = true, Pending = true, Source = "Rev lights read off the screen", Status = "Pit limiter active. Turn it off before the next sweep; its light pattern is not recorded." },
                };
                foreach (var item in states)
                foreach (int width in new[] { 740, 460 })
                {
                    state = item.Value;
                    page.Tick();
                    page.Width = width;
                    page.Height = 1080;
                    page.Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
                    page.Foreground = Brushes.White;
                    // The standalone runner has no SimHub theme; supply readable inherited text.
                    foreach (var expander in WorkflowControls<Expander>(page)) expander.Foreground = Brushes.Gainsboro;
                    foreach (var check in WorkflowControls<CheckBox>(page)) check.Foreground = Brushes.Gainsboro;
                    page.Measure(new Size(width, 1080));
                    page.Arrange(new Rect(0, 0, width, 1080));
                    page.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(width, 1080, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(page);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(Path.Combine(folder, item.Key + (width == 740 ? "" : "-narrow") + ".png"))) encoder.Save(stream);
                }
            });
        }
    }
}
