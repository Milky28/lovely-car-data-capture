using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GameReaderCommon;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Profile;
using LovelyCarDataCapture.Repo;
using LovelyCarDataCapture.Util;
using LovelyCarDataCapture.Plugin;
using LovelyCarDataCapture.Screen;
using SimHub.Plugins;

namespace LovelyCarDataCapture
{
    [PluginName("Lovely Car Data Capture")]
    [PluginAuthor("Milky28")]
    [PluginDescription("Records a car's rev-light RPMs, colours and redline - read off the screen, or from telemetry in F1 and iRacing - and exports a Lovely Car Data v2.0.0 car file with a report, starting from the car's repo file when there is one. Unofficial community tool.")]
    public class CapturePlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        private const string LogPrefix = "[LovelyCarDataCapture] ";
        private static readonly TimeSpan RepoWaitOnExport = TimeSpan.FromSeconds(10);
        /// <summary>How long SimHub's shutdown may wait on GitHub for the repo file before exporting without it.</summary>
        private static readonly TimeSpan RepoWaitOnShutdown = TimeSpan.FromSeconds(3);
        /// <summary>Set while SimHub closes, so nothing opens a window on the way out.</summary>
        private volatile bool _shuttingDown;
        /// <summary>The capture has been told it's full, so it isn't said again every frame.</summary>
        private bool _fullReported;

        // Actions arrive on a different thread than DataUpdate.
        private readonly object _lock = new object();
        private readonly RepoClient _repo = new RepoClient();
        private CaptureSession _session;
        private Task<RepoLookup> _lookup;
        private volatile bool _capturing;
        private volatile string _lastExportPath = "";
        private volatile string _lastReportPath = "";
        private volatile string _lastAtsrDevPath = "";
        private volatile string _repoStatus = "";
        private volatile string _lastMark = "";
        private string _loggedRawType;
        private readonly ScreenCaptureLoop _screen = new ScreenCaptureLoop();
        private CaptureBoxWindow _box;
        private CaptureOverlay _overlay;
        // Set by DataUpdate so the screen thread knows whether this frame is worth recording (guarded by _lock).
        private bool _skipScreenFrame = true;
        // Latest gear and RPM seen by DataUpdate, for the mark actions (guarded by _lock).
        private string _currentGear;
        private int _currentRpm;

        public CaptureSettings Settings;
        public PluginManager PluginManager { get; set; }

        public void Init(PluginManager pluginManager)
        {
            Settings = this.ReadCommonSettings("CaptureSettings", () => new CaptureSettings());

            this.AttachDelegate("Capturing", () => _capturing);
            this.AttachDelegate("CarId", () => { lock (_lock) return _session?.CarId ?? ""; });
            this.AttachDelegate("GearsSeen", () => { lock (_lock) return _session == null ? "" : string.Join(",", _session.Redline.Gears.Keys.OrderBy(CarProfile.GearRank)); });
            this.AttachDelegate("LedSource", () => { lock (_lock) return DescribeSource(_session); });
            this.AttachDelegate("LedProgress", () => { lock (_lock) return DescribeProgress(_session); });
            this.AttachDelegate("RepoStatus", () => _repoStatus);
            this.AttachDelegate("LastExportPath", () => _lastExportPath);
            this.AttachDelegate("LastReportPath", () => _lastReportPath);
            this.AttachDelegate("LastAtsrDeveloperPath", () => _lastAtsrDevPath);
            this.AttachDelegate("LastMark", () => _lastMark);
            this.AttachDelegate("ScreenCaptureStatus", () => _screen.Status);
            this.AttachDelegate("ScreenLights", () => _screen.LightsSeen);

            _screen.Target = ScreenTargetNow;

            this.AddAction(actionName: "StartCapture", actionStart: (pm, _) => StartCapture());
            this.AddAction(actionName: "StopAndExport", actionStart: (pm, _) => StopAndExport());
            this.AddAction(actionName: "ResetCapture", actionStart: (pm, _) =>
            {
                lock (_lock) { _session = null; _lookup = null; _skipScreenFrame = true; }
                _repoStatus = "";
                _lastMark = "";
                Say("Capture cleared. Nothing is being recorded.");
            });
            // For games that don't report their LEDs: press as each in-game light comes on while revving slowly.
            this.AddAction(actionName: "MarkLed", actionStart: (pm, _) => Mark(redline: false));
            this.AddAction(actionName: "MarkRedline", actionStart: (pm, _) => Mark(redline: true));
            this.AddAction(actionName: "UndoMark", actionStart: (pm, _) => UndoMark());
            // Games that don't report their lights: put the box over them, then capture as usual.
            this.AddAction(actionName: "ShowCaptureBox", actionStart: (pm, _) => ShowCaptureBox());
            // Pressed in the car: the game is on screen right then, so the still is taken at once.
            this.AddAction(actionName: "PickCaptureBox", actionStart: (pm, _) => OnUiThread(() => PickFromStill(0)));
        }

        private void Mark(bool redline)
        {
            string message;
            lock (_lock)
            {
                if (!_capturing || _session == null || string.IsNullOrEmpty(_currentGear) || _currentRpm <= 0)
                {
                    message = "Start a capture and get in the car before marking.";
                }
                else if (redline)
                {
                    _session.Marks.MarkRedline(_currentGear, _currentRpm);
                    message = "Gear " + _currentGear + ": redline at " + _currentRpm + " rpm";
                }
                else
                {
                    int n = _session.Marks.MarkLed(_currentGear, _currentRpm);
                    message = "Gear " + _currentGear + ": LED step " + n + " at " + _currentRpm + " rpm";
                }
            }
            _lastMark = message;
            Say(message);
            SimHub.Logging.Current.Info(LogPrefix + "Mark: " + message);
        }

        private void UndoMark()
        {
            string removed;
            lock (_lock) removed = _session?.Marks.Undo();
            _lastMark = removed == null ? "Nothing to undo" : "Removed " + removed;
            Say(_lastMark);
            SimHub.Logging.Current.Info(LogPrefix + _lastMark);
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (!_capturing || !data.GameRunning || data.GamePaused || data.GameReplay || data.NewData == null)
            {
                lock (_lock) _skipScreenFrame = true;
                return;
            }
            var d = data.NewData;
            if (string.IsNullOrEmpty(d.CarId)) return;

            lock (_lock)
            {
                if (_session == null || _session.CarId != d.CarId || _session.GameName != data.GameName)
                {
                    if (_session != null)
                        SimHub.Logging.Current.Warn(LogPrefix + "Car changed from " + _session.CarId + " to " + d.CarId + "; the previous capture was discarded. Export before switching cars.");
                    _session = new CaptureSession(data.GameName, d.CarId);
                    _fullReported = false;
                    StartRepoLookup(data.GameName, d.CarId);
                }

                var s = _session;
                _currentGear = d.Gear;
                _currentRpm = (int)Math.Round(d.Rpms);
                s.RecordCar(d.CarModel, d.CarClass);
                // CarSettings_CurrentGearRedLineRPM resolves to SimHub's per-gear redline when enabled for this car, otherwise the car-wide redline.
                s.Redline.Record(d.Gear, d.Rpms, d.CarSettings_CurrentGearRedLineRPM, d.CarSettings_RedLineRPM,
                    Math.Max(d.CarSettings_MaxRPM, d.MaxRpm), d.CarSettings_MaxGears);

                // The pit limiter drives its own light patterns in most games.
                if (d.PitLimiterOn != 0)
                {
                    s.PitLimiterSamples++;
                    _skipScreenFrame = true;
                    return;
                }
                _skipScreenFrame = _currentRpm <= 0 || string.IsNullOrEmpty(_currentGear);

                var raw = d.GetRawDataObject();
                if (RawTelemetry.TryReadF1(raw, out var f1))
                {
                    s.F1.Record(RawTelemetry.F1GearName(f1.Gear), f1.Rpm, f1.Bits);
                }
                else if (RawTelemetry.TryReadIRacing(raw, out var perGear, out var carWide))
                {
                    s.IRacing.RecordCarWide(carWide);
                    s.IRacing.RecordGear(d.Gear, perGear);
                }
                else
                {
                    LogRawTypeOnce(raw);
                }
            }
        }

        public void End(PluginManager pluginManager)
        {
            _shuttingDown = true;
            // A capture left running when SimHub closes would otherwise be lost with it.
            bool hasData;
            lock (_lock)
                hasData = _capturing && _session != null &&
                          (_session.Screen.HasData || _session.F1.HasData || _session.IRacing.HasData || _session.Marks.HasData);
            if (hasData)
            {
                SimHub.Logging.Current.Info(LogPrefix + "SimHub is closing with a capture running; exporting it.");
                try { StopAndExport(RepoWaitOnShutdown, onShutdown: true); }
                catch (Exception ex) { SimHub.Logging.Current.Error(LogPrefix + "Export on close failed", ex); }
            }
            _capturing = false;
            _screen.Stop();
            CloseOverlay();
            this.SaveCommonSettings("CaptureSettings", Settings);
        }

        // ---------- the panel over the game ----------
        /// <summary>
        /// Says what just happened, on screen and over the game. Mapped buttons are pressed with the
        /// game in front of everything, where neither SimHub's window nor its log can be seen.
        /// </summary>
        private void Say(string message)
        {
            if (!Settings.ShowOverlay || _shuttingDown) return;
            OnUiThread(() =>
            {
                EnsureOverlay();
                _overlay?.Message(message);
            });
        }

        private void ShowOverlay(bool capturing)
        {
            if (!Settings.ShowOverlay || _shuttingDown) return;
            OnUiThread(() =>
            {
                EnsureOverlay();
                _overlay?.SetCapturing(capturing);
            });
        }

        private void CloseOverlay() => OnUiThread(() =>
        {
            _overlay?.Close();
            _overlay = null;
        });

        private void EnsureOverlay()
        {
            if (_overlay != null) return;
            _overlay = new CaptureOverlay(OverlayStatus, (x, y) =>
            {
                Settings.OverlayX = x;
                Settings.OverlayY = y;
            }, Settings.OverlayX, Settings.OverlayY);
            _overlay.Closed += (s, e) => _overlay = null;
        }

        private static void OnUiThread(Action action)
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;
            if (app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.BeginInvoke(action);
        }

        /// <summary>The line the panel keeps up to date while driving.</summary>
        private string OverlayStatus()
        {
            lock (_lock)
            {
                if (!_capturing) return _session == null ? "Not capturing" : "Stopped. " + DescribeProgress(_session);
                if (_session == null) return "Waiting for the car…";

                var parts = new List<string>();
                parts.Add(string.IsNullOrEmpty(_currentGear) ? "gear ?" : "gear " + _currentGear);
                parts.Add(_currentRpm + " rpm");
                if (Settings.ScreenCapture && _screen.IsRunning) parts.Add(_screen.LightsSeen + " lights lit");
                var progress = DescribeProgress(_session);
                if (!string.IsNullOrEmpty(progress)) parts.Add(progress);
                else parts.Add("no LED data yet");
                return "Recording · " + string.Join(" · ", parts);
            }
        }

        private void StartCapture()
        {
            lock (_lock) { _session = null; _lookup = null; _skipScreenFrame = true; }
            _repoStatus = "";
            _capturing = true;
            StartScreenCapture();
            ShowOverlay(true);
            Say(Settings.ScreenCapture && _screen.IsRunning
                ? "Capture started, watching the rev lights on screen. Rev slowly from idle to the limiter a few times."
                : "Capture started. Drive through every gear and rev each one to the limiter.");
            SimHub.Logging.Current.Info(LogPrefix + "Capture started. Drive through every gear and rev each one to the limiter.");
        }

        private void StartScreenCapture()
        {
            if (!Settings.ScreenCapture) return;
            var box = SettingsBox();
            if (box.Width < 8 || box.Height < 4)
            {
                SimHub.Logging.Current.Warn(LogPrefix + "Reading the lights off the screen is on, but no capture box has been placed. " +
                                            "Open the plugin's page in SimHub and position it.");
                Say("No capture box has been placed yet, so the rev lights can't be read. Open the plugin's page in SimHub, or press the ShowCaptureBox button.");
                return;
            }
            _screen.Start(box, Settings.ScreenCaptureFps);
            SimHub.Logging.Current.Info(LogPrefix + "Watching " + box.Width + "x" + box.Height + " at " + box.X + "," + box.Y +
                                        " for the car's rev lights.");
        }

        /// <summary>Where exports go. Documents can be redirected, to OneDrive among others, so this is resolved rather than assumed.</summary>
        private string OutputFolder() => string.IsNullOrWhiteSpace(Settings.OutputFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SimHub", "LovelyCarDataCapture")
            : Settings.OutputFolder;

        private PixelRect SettingsBox() =>
            new PixelRect(Settings.ScreenBoxX, Settings.ScreenBoxY, Settings.ScreenBoxWidth, Settings.ScreenBoxHeight);

        /// <summary>
        /// What the screen thread should record its next frame against, or null to drop it: no car yet,
        /// the game paused, the pit limiter on, or a game that reports its lights properly anyway.
        /// </summary>
        private ScreenTarget ScreenTargetNow()
        {
            lock (_lock)
            {
                if (!_capturing || _session == null || _skipScreenFrame) return null;
                if (_session.F1.HasData || _session.IRacing.HasData) return null;
                if (_session.Screen.IsFull)
                {
                    if (!_fullReported)
                    {
                        _fullReported = true;
                        int minutes = (int)Math.Round(ScreenLedCapture.MaxSamples / (double)Math.Max(1, Settings.ScreenCaptureFps) / 60);
                        SimHub.Logging.Current.Warn(LogPrefix + "Capture full after " + ScreenLedCapture.MaxSamples + " frames; nothing more is recorded.");
                        Say("Capture full: about " + minutes + " minutes of frames are kept and nothing more is recorded. Press Stop and export.");
                    }
                    return new ScreenTarget { Full = true };
                }
                return new ScreenTarget { Gear = _currentGear, Rpm = _currentRpm, Capture = _session.Screen };
            }
        }

        private void ShowCaptureBox()
        {
            var app = System.Windows.Application.Current;
            if (app == null)
            {
                SimHub.Logging.Current.Warn(LogPrefix + "The capture box needs SimHub's own window; open SimHub and try again.");
                return;
            }
            app.Dispatcher.Invoke(() =>
            {
                try { ShowCaptureBoxFor(null, region => _screen.Describe(region)); }
                catch (Exception ex) { SimHub.Logging.Current.Error(LogPrefix + "Couldn't open the capture box", ex); }
            });
        }

        /// <summary>
        /// Stores the box. The window saves as it is dragged, so this runs often; the capture only
        /// picks the new box up once the window is done, which is when <paramref name="final"/> is set.
        /// </summary>
        private void SaveCaptureBox(PixelRect region, bool final)
        {
            Settings.ScreenBoxX = region.X;
            Settings.ScreenBoxY = region.Y;
            Settings.ScreenBoxWidth = region.Width;
            Settings.ScreenBoxHeight = region.Height;
            this.SaveCommonSettings("CaptureSettings", Settings);
            if (!final) return;

            SimHub.Logging.Current.Info(LogPrefix + "Capture box set to " + region.Width + "x" + region.Height + " at " + region.X + "," + region.Y + ".");
            Say("Capture box saved: " + region.Width + " x " + region.Height + " at " + region.X + ", " + region.Y + ".");
            // Already capturing: pick the new box up straight away.
            if (_capturing && Settings.ScreenCapture) StartScreenCapture();
        }

        /// <summary>
        /// Takes a still of the screen and lets the box be drawn on it. From SimHub's page the game is
        /// behind SimHub, so there's a countdown to switch to it first, shown on the panel over the game;
        /// the panel steps aside for the still itself so it can't end up covering the lights.
        /// </summary>
        private void PickFromStill(int countdown)
        {
            _box?.Close();
            EnsureOverlay();
            int left = countdown;
            void Tick()
            {
                if (left > 0)
                {
                    _overlay?.Message("Switch to the game with the rev lights in view. Taking a still in " + left + "…");
                    left--;
                    var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    wait.Tick += (s, e) => { wait.Stop(); Tick(); };
                    wait.Start();
                    return;
                }

                _overlay?.SetSuppressed(true);
                // One render pass for the panel to actually leave the screen before it's copied.
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    DesktopSnapshot shot;
                    try { shot = DesktopSnapshot.Take(); }
                    catch (Exception ex)
                    {
                        _overlay?.SetSuppressed(false);
                        Say("Couldn't take a still of the screen: " + ex.Message);
                        return;
                    }
                    _overlay?.SetSuppressed(false);
                    var picker = new SnapshotPickerWindow(shot, SettingsBox(), region => SaveCaptureBox(region, final: true));
                    picker.Show();
                    picker.Activate();
                }), System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            Tick();
        }

        /// <summary>Shows the capture box, or brings the one already open back to the front.</summary>
        private void ShowCaptureBoxFor(Action<PixelRect> onSave, Func<PixelRect, string> test)
        {
            if (_box != null)
            {
                _box.Activate();
                _box.Focus();
                return;
            }
            _box = new CaptureBoxWindow(SettingsBox(), region => SaveCaptureBox(region, final: false), test);
            _box.Closed += (s, e) =>
            {
                var window = _box;
                _box = null;
                if (window != null)
                {
                    var region = window.LastRegion;
                    SaveCaptureBox(region, final: true);
                    onSave?.Invoke(region);
                }
            };
            _box.Show();
            _box.Activate();
        }

        // ---------- SimHub's settings page ----------
        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager) =>
            new ScreenSettingsControl(Settings, () => this.SaveCommonSettings("CaptureSettings", Settings),
                                      region => _screen.Describe(region), ShowCaptureBoxFor, () => PickFromStill(5), Say, OutputFolder,
                                      StartCapture,
                                      // Exporting waits on GitHub, so it can't run on the thread drawing this page.
                                      () => Task.Run(() => StopAndExport()),
                                      () => _capturing, OverlayStatus,
                                      AtsrCopiesNow, RemoveAtsrCopy,
                                      () => System.Diagnostics.Process.Start("explorer.exe", "\"" + AtsrDevCopies.Folder(SimHubFolder) + "\""));

        public string LeftMenuTitle => "Lovely Car Data Capture";

        public System.Windows.Media.ImageSource PictureIcon => LedStripIcon.Create();

        private void StartRepoLookup(string gameName, string carId)
        {
            if (!Settings.UseRepoFile)
            {
                _lookup = Task.FromResult(RepoLookup.Disabled());
                _repoStatus = "Repo lookup off";
                return;
            }
            _repoStatus = "Checking Lovely Car Data…";
            var branch = string.IsNullOrWhiteSpace(Settings.RepoBranch) ? "main" : Settings.RepoBranch.Trim();
            _lookup = _repo.FindAsync(gameName, carId, branch).ContinueWith(t =>
            {
                var result = t.Result;
                _repoStatus = result.Status == RepoLookupStatus.Found ? "In repo: data/" + result.RelativePath
                    : result.Status == RepoLookupStatus.NotInRepo ? "New car (not in repo)"
                    : "Repo lookup failed: " + result.Error;
                return result;
            }, TaskScheduler.Default);
        }

        private void StopAndExport() => StopAndExport(RepoWaitOnExport, onShutdown: false);

        private void StopAndExport(TimeSpan repoWait, bool onShutdown)
        {
            _capturing = false;
            _screen.Stop();
            ShowOverlay(false);

            CaptureSession session;
            Task<RepoLookup> lookupTask;
            lock (_lock)
            {
                session = _session;
                lookupTask = _lookup;
            }
            if (session == null)
            {
                SimHub.Logging.Current.Warn(LogPrefix + "Nothing captured - start a capture and drive first.");
                Say("Nothing was captured. Press StartCapture, then drive.");
                ShowOverlay(false);
                return;
            }
            Say("Exporting " + session.CarId + "…");

            RepoLookup lookup = null;
            if (lookupTask != null)
            {
                lookup = lookupTask.Wait(repoWait)
                    ? lookupTask.Result
                    : new RepoLookup { Status = RepoLookupStatus.Failed, Error = "timed out waiting for GitHub" };
            }

            string json, path, reportPath;
            ComposeResult result;
            // The session is no longer written to (capturing is off), but DataUpdate may still be mid-frame.
            lock (_lock)
            {
                result = ProfileComposer.Compose(session, Settings, lookup, DateTime.Now);
                if (onShutdown)
                    result.Report.Insert(Math.Min(2, result.Report.Count),
                        "Exported automatically: SimHub was closed while this capture was still running." + Environment.NewLine);
                json = result.Profile.ToJson();
                var root = OutputFolder();
                // Always named after the carId: that's the file name ATSR looks for, even when the repo's file is named differently.
                var fileName = Slug.Make(session.CarId);
                path = Path.Combine(root, Slug.Make(session.GameName), fileName + ".json");
                reportPath = Path.Combine(root, Slug.Make(session.GameName), fileName + ".report.txt");
            }

            string framesPath = null;
            if (Settings.SaveCaptureFrames && session.Screen.HasData)
            {
                framesPath = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + ".frames.csv");
                result.Report.Add("");
                result.Report.Add("Raw frames: " + framesPath);
            }

            var utf8 = new UTF8Encoding(false);
            bool copied = Settings.CopyToAtsrDeveloperFolder && CopyToAtsrDeveloperFolder(session.GameName, session.CarId, json, result.Report, utf8);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, json, utf8);
                File.WriteAllText(reportPath, string.Join(Environment.NewLine, result.Report) + Environment.NewLine, utf8);
                if (framesPath != null)
                {
                    lock (_lock)
                        using (var writer = new StreamWriter(framesPath, false, utf8))
                            session.Screen.WriteFrames(writer);
                }
                _lastExportPath = path;
                _lastReportPath = reportPath;
                SimHub.Logging.Current.Info(LogPrefix + "Exported " + path + " (" + result.Source + "). Report: " + reportPath);
                foreach (var problem in result.AtsrProblems) SimHub.Logging.Current.Warn(LogPrefix + "ATSR: " + problem);
                Say("Saved " + Path.GetFileName(path) + " to " + Path.GetDirectoryName(path) + "." +
                    " From " + result.Source + "." +
                    (result.AtsrProblems.Count > 0 ? " " + result.AtsrProblems.Count + " ATSR warning(s) in the report." : "") +
                    (copied ? " Also copied to ATSR's local RPM folder and ATSR told to reload: with Enable Local RPM Folder on, the wheel shows it now." : "") +
                    " Read the report before submitting.");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error(LogPrefix + "Export failed for " + path, ex);
                Say("Export failed: " + ex.Message);
            }
        }

        // Lets the file be tried on real hardware straight away: with "Enable Local RPM Folder" on, ATSR reads this folder
        // before its own copy of the repo. Adds the outcome to the report.
        private static string SimHubFolder => Path.GetDirectoryName(typeof(CapturePlugin).Assembly.Location);

        private bool CopyToAtsrDeveloperFolder(string game, string carId, string json, List<string> report, Encoding encoding)
        {
            var devPath = AtsrCompatibility.DevelopmentFilePath(SimHubFolder, carId);
            report.Add("");
            try
            {
                var notes = AtsrDevCopies.Write(Settings, SimHubFolder, game, carId, json, encoding);
                this.SaveCommonSettings("CaptureSettings", Settings);
                _lastAtsrDevPath = devPath;
                report.Add("Copied to ATSR's local RPM folder: " + devPath);
                report.AddRange(notes);
                report.Add(ReloadAtsr()
                    ? "  ATSR was told to reload (its Force RPM Reload), so the wheel shows this file now if \"Enable Local RPM Folder\" is on"
                    : "  ATSR couldn't be told to reload; press its Force RPM Reload, or re-enter the car. The file is only read if");
                report.Add("  (" + AtsrLocalFolderSetting + ").");
                report.Add("  While it's there ATSR uses it instead of the repo's file for this car in every game. Remove it from");
                report.Add("  the plugin's settings page (Checking a file on the wheel) once it's checked.");
                SimHub.Logging.Current.Info(LogPrefix + "Copied to ATSR's local RPM folder: " + devPath);
                return true;
            }
            catch (Exception ex)
            {
                report.Add("Couldn't copy to ATSR's local RPM folder (" + devPath + "): " + ex.Message);
                SimHub.Logging.Current.Error(LogPrefix + "Copy to ATSR's local RPM folder failed: " + devPath, ex);
                return false;
            }
        }

        private List<AtsrCopy> AtsrCopiesNow()
        {
            var copies = AtsrDevCopies.Current(Settings, SimHubFolder, OutputFolder(), out bool changed);
            if (changed) this.SaveCommonSettings("CaptureSettings", Settings);
            return copies;
        }

        private string RemoveAtsrCopy(AtsrCopy copy)
        {
            var error = AtsrDevCopies.Remove(Settings, SimHubFolder, copy);
            this.SaveCommonSettings("CaptureSettings", Settings);
            if (error == null)
            {
                SimHub.Logging.Current.Info(LogPrefix + "Removed from ATSR's local RPM folder: " + copy.File);
                ReloadAtsr();      // back to the repo's file, if that car is the one loaded
            }
            return error;
        }

        /// <summary>Where the switch that makes ATSR read the local folder lives.</summary>
        internal const string AtsrLocalFolderSetting =
            "ATSR-Hub EVO > Universal Settings > RPM Settings > Developer Settings > Enable Local RPM Folder";

        /// <summary>ATSR's own "Force RPM Reload" action, as SimHub names it: its plugin class, then the action.</summary>
        private const string AtsrReloadAction = "ATSRHubMain.ForceRPMReload";

        /// <summary>
        /// Presses ATSR's Force RPM Reload, which fetches the loaded car's file again, the local folder
        /// first. It only does anything while a car is loaded - which, straight after a drive, it is.
        /// </summary>
        private bool ReloadAtsr()
        {
            try
            {
                if (PluginManager == null) return false;
                PluginManager.TriggerAction(AtsrReloadAction);
                SimHub.Logging.Current.Info(LogPrefix + "Asked ATSR to reload its RPM data.");
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(LogPrefix + "Couldn't trigger " + AtsrReloadAction + ": " + ex.Message);
                return false;
            }
        }

        private void LogRawTypeOnce(object raw)
        {
            var name = raw?.GetType().FullName ?? "(none)";
            if (name == _loggedRawType) return;
            _loggedRawType = name;
            SimHub.Logging.Current.Info(LogPrefix + "No LED data readable from this game's raw data (" + name + "); using SimHub's redline only.");
        }

        private static string DescribeSource(CaptureSession s)
        {
            if (s == null) return "";
            if (s.F1.HasData) return "F1 rev lights";
            if (s.IRacing.HasData) return "iRacing shift lights";
            if (s.Screen.HasData) return "Rev lights read off the screen";
            if (s.Marks.HasData) return "Manual marks";
            return "SimHub redline only";
        }

        // e.g. "1:15/15 2:15/15 3:9/15" for F1, gear list for iRacing.
        private static string DescribeProgress(CaptureSession s)
        {
            if (s == null) return "";
            if (s.F1.HasData)
                return string.Join(" ", s.F1.Gears.OrderBy(CarProfile.GearRank).Select(g => g + ":" + s.F1.Result(g).CapturedCount + "/" + LedWindowCapture.F1LedCount));
            if (s.IRacing.HasData)
                return (s.IRacing.CarWide != null ? "car-wide ✓ " : "") + string.Join(" ", s.IRacing.Gears.OrderBy(CarProfile.GearRank));
            if (s.Screen.HasData)
                return s.Screen.SampleCount + " frames, up to " + s.Screen.MostLightsSeen + " lights at once";
            if (s.Marks.HasData)
                return string.Join(" ", s.Marks.Gears.OrderBy(CarProfile.GearRank).Select(g => g + ":" + s.Marks.LedMarks(g).Count + (s.Marks.Redline(g).HasValue ? "+RL" : "")));
            return "";
        }
    }
}
