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
        // Worker start/stop must stay outside _lock: its Target callback takes that lock too.
        private readonly object _actions = new object();
        private readonly RepoClient _repo = new RepoClient();
        private readonly TelemetryHistory _telemetry = new TelemetryHistory();
        private CaptureSession _session;
        // Incremented for every start, reset, stop, and car/game change so old frames cannot cross a session.
        private long _captureSessionId;
        private Task<RepoLookup> _lookup;
        private volatile bool _capturing;
        private bool _exporting;
        private bool _exportPending;
        private bool _exportFailed;
        private Task _exportTask;
        private volatile string _message = "";
        private string _exportSummary = "No export yet.";
        private string _exportDetails = "";
        private string _exportFolder = "";
        // The page describes the latest attempt; public Last* properties retain the last success.
        private string _exportReportPath = "";
        private string _exportEvidenceFolder = "";
        private string _exportGame = "";
        private CaptureEvidence _evidence;
        private CaptureBoxImage _selectedBoxImage;
        private string _liveGame, _liveCar;
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
        private string _captureWaitReason = "";
        private long _lastTelemetryAt;
        // Status only: allow brief gaps before asking the driver to check their game connection.
        private const int TelemetryQuietMs = 1000;

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
            this.AddAction(actionName: "StopAndExport", actionStart: (pm, _) => QueueExport());
            this.AddAction(actionName: "ResetCapture", actionStart: (pm, _) => ResetCapture());
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
            lock (_lock) removed = _capturing ? _session?.Marks.Undo() : null;
            _lastMark = removed == null ? "Nothing to undo" : "Removed " + removed;
            Say(_lastMark);
            SimHub.Logging.Current.Info(LogPrefix + _lastMark);
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // Stamp arrival before taking _lock. A busy action must not make telemetry look newer than it was.
            long telemetryAt = CaptureClock.NowMilliseconds;
            lock (_lock)
            {
                _liveGame = data.GameRunning ? data.GameName : null;
                _liveCar = data.GameRunning ? data.NewData?.CarId : null;
            }
            if (!_capturing || !data.GameRunning || data.GamePaused || data.GameReplay || data.NewData == null)
            {
                lock (_lock)
                {
                    _skipScreenFrame = true;
                    _telemetry.Clear(_captureSessionId);
                    _lastTelemetryAt = telemetryAt;
                    if (_capturing)
                        _captureWaitReason = !data.GameRunning ? "Waiting for the game. Start it and enter the car."
                            : data.GamePaused ? "Game paused. Resume the game to continue recording."
                            : data.GameReplay ? "Replay is open. Return to live driving to record."
                            : "Waiting for telemetry. Enter the car and check the game's connection to SimHub.";
                }
                return;
            }
            var d = data.NewData;
            if (string.IsNullOrEmpty(d.CarId))
            {
                lock (_lock)
                {
                    _skipScreenFrame = true;
                    _telemetry.Clear(_captureSessionId);
                    _lastTelemetryAt = telemetryAt;
                    _captureWaitReason = "Waiting for a car. Leave the menu and enter the cockpit.";
                }
                return;
            }

            lock (_lock)
            {
                // A stop or reset may have arrived while this telemetry update waited for the lock.
                if (!_capturing) return;
                if (_session != null && (_session.CarId != d.CarId || _session.GameName != data.GameName))
                {
                    _capturing = false;
                    _evidence?.Stop();
                    _captureSessionId++;
                    _telemetry.Reset(_captureSessionId);
                    _skipScreenFrame = true;
                    _currentGear = null;
                    _currentRpm = 0;
                    // The worker now idles. Export/discard joins it outside this lock.
                    var message = "Car or game changed. Recording stopped; " + _session.CarId + " (" + _session.GameName +
                        ") is kept. Export it or discard the capture before starting another.";
                    SimHub.Logging.Current.Warn(LogPrefix + message);
                    ShowOverlay(false);
                    Say(message);
                    return;
                }
                if (_session == null)
                {
                    _session = new CaptureSession(data.GameName, d.CarId);
                    _exportPending = true;
                    _captureSessionId++;
                    _telemetry.Reset(_captureSessionId);
                    _fullReported = false;
                    StartRepoLookup(data.GameName, d.CarId);
                }

                var s = _session;
                _lastTelemetryAt = telemetryAt;
                _captureWaitReason = "";
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
                    _telemetry.Clear(_captureSessionId);
                    _captureWaitReason = "Pit limiter active. Turn it off before the next sweep; its light pattern is not recorded.";
                    return;
                }
                _skipScreenFrame = _currentRpm <= 0 || string.IsNullOrEmpty(_currentGear);
                if (_skipScreenFrame)
                {
                    _telemetry.Clear(_captureSessionId);
                    _captureWaitReason = "Waiting for RPM and gear data. Start the engine and check the game's connection to SimHub.";
                }
                else
                    _telemetry.Add(_captureSessionId, data.GameName, d.CarId, d.Gear, d.Rpms, telemetryAt);

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
            // A capture left running when SimHub closes would otherwise be lost with it.
            Task exporting;
            lock (_actions)
            lock (_lock)
            {
                _shuttingDown = true;
                exporting = _exportTask;
            }
            // An export already in flight owns this session and must finish before shutdown returns.
            exporting?.GetAwaiter().GetResult();
            bool hasData;
            lock (_lock)
                hasData = _exportPending && _session != null;
            if (hasData)
            {
                SimHub.Logging.Current.Info(LogPrefix + "SimHub is closing with an unsaved capture; exporting it.");
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
            _message = message;
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
                if (_exporting) return "Exporting " + _session?.CarId + "…";
                if (!_capturing) return _session == null ? "Not capturing" :
                    (_exportPending ? "Capture kept for export: " + _session.CarId + ". " : "Stopped. ") + DescribeProgress(_session);
                if (!string.IsNullOrEmpty(_captureWaitReason)) return _captureWaitReason;
                if (_session == null) return "Waiting for the car. Enter the cockpit to begin recording.";
                if (CaptureClock.NowMilliseconds - _lastTelemetryAt > TelemetryQuietMs)
                    return "Waiting for telemetry. Check that the game is running and connected to SimHub.";
                bool screenSource = !_session.F1.HasData && !_session.IRacing.HasData && (Settings.ScreenCapture || _screen.IsRunning);
                if (screenSource && !_screen.IsRunning)
                    return "Screen reading is not running. Pick a capture box to begin.";
                if (screenSource && !string.IsNullOrEmpty(_screen.Guidance)) return _screen.Guidance;

                var parts = new List<string>();
                parts.Add(string.IsNullOrEmpty(_currentGear) ? "gear ?" : "gear " + _currentGear);
                parts.Add(_currentRpm + " rpm");
                if (screenSource) parts.Add(_screen.LightsSeen + " lights lit");
                var progress = DescribeProgress(_session);
                if (!string.IsNullOrEmpty(progress)) parts.Add(progress);
                else parts.Add("no LED data yet");
                return "Recording · " + string.Join(" · ", parts) +
                    (screenSource && _session.Screen.MostLightsSeen == 0
                        ? ". No lights seen yet. Rev until some are lit; if the count stays at zero, check the box." : "");
            }
        }

        internal void ResetCapture()
        {
            lock (_actions)
            {
                lock (_lock)
                {
                    if (_exporting || _shuttingDown) return;
                    _capturing = false;
                    _exportPending = false;
                    _exportFailed = false;
                    _captureSessionId++;
                    _telemetry.Reset(_captureSessionId);
                    _session = null;
                    _lookup = null;
                    _evidence = null;
                    _skipScreenFrame = true;
                    _currentGear = null;
                    _currentRpm = 0;
                    _captureWaitReason = "";
                    _lastTelemetryAt = CaptureClock.NowMilliseconds;
                }
                _screen.Stop();
                _screen.ClearTransitionFrames();
                _repoStatus = "";
                _lastMark = "";
                ShowOverlay(false);
                Say("Capture discarded. Nothing is being recorded.");
            }
        }

        internal void StartCapture()
        {
            lock (_actions)
            {
                lock (_lock)
                {
                    if (_exporting || _capturing || _shuttingDown) return;
                    if (_exportPending)
                    {
                        Say("An unsaved capture is kept. Export it or discard the capture before starting another.");
                        return;
                    }
                    _captureSessionId++;
                    _telemetry.Reset(_captureSessionId);
                    _session = null;
                    _lookup = null;
                    _evidence = new CaptureEvidence(Settings, _selectedBoxImage);
                    _skipScreenFrame = true;
                    _exportFailed = false;
                    _currentGear = null;
                    _currentRpm = 0;
                    _captureWaitReason = "";
                    _lastTelemetryAt = CaptureClock.NowMilliseconds;
                    _capturing = true;
                }
                _repoStatus = "";
                _screen.Stop();
                _screen.ClearTransitionFrames();
                StartScreenCapture();
                ShowOverlay(true);
                if (!Settings.ScreenCapture || _screen.IsRunning)
                    Say(Settings.ScreenCapture
                        ? "Capture started, watching the rev lights on screen. Rev slowly from idle to the limiter a few times."
                        : "Capture started. F1 and iRacing use telemetry; screen reading is off for other games.");
                SimHub.Logging.Current.Info(LogPrefix + "Capture started.");
            }
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
            _screen.Start(box, Settings.ScreenCaptureFps, Settings.SaveTransitionFrames);
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
                return new ScreenTarget
                {
                    Capture = _session.Screen,
                    Telemetry = _telemetry,
                    SessionId = _captureSessionId,
                    Game = _session.GameName,
                    Car = _session.CarId,
                    Evidence = _evidence,
                };
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
        private bool SaveCaptureBox(PixelRect region, bool final, CaptureBoxImage image = null)
        {
            lock (_actions)
            {
                lock (_lock) if (_exporting || _shuttingDown) return false;
                if (!ApplyCaptureBox(Settings, region, final))
                {
                    Say("No valid capture box was saved. Pick the lights before starting screen capture.");
                    return false;
                }
                this.SaveCommonSettings("CaptureSettings", Settings);
                if (!final) return true;
                _selectedBoxImage = image;
                if (_capturing)
                {
                    _screen.Stop();
                    _evidence?.AddSelection(image);
                    if (_session?.Screen.HasData == true)
                        _evidence?.Note("The capture box changed during recording. Strip images identify the raw frame ranges; optional transition images cover only the final box.");
                }

                SimHub.Logging.Current.Info(LogPrefix + "Capture box set to " + region.Width + "x" + region.Height + " at " + region.X + "," + region.Y + ".");
                Say("Capture box saved: " + region.Width + " x " + region.Height + " at " + region.X + ", " + region.Y + ". Screen reading is on.");
                // Already capturing: pick the new box up straight away.
                if (_capturing && Settings.ScreenCapture) StartScreenCapture();
                return true;
            }
        }

        internal static bool ApplyCaptureBox(CaptureSettings settings, PixelRect region, bool final)
        {
            if (final && (region.Width < 8 || region.Height < 4)) return false;
            settings.ScreenBoxX = region.X;
            settings.ScreenBoxY = region.Y;
            settings.ScreenBoxWidth = region.Width;
            settings.ScreenBoxHeight = region.Height;
            if (final) settings.ScreenCapture = true;
            return true;
        }

        /// <summary>
        /// Takes a still of the screen and lets the box be drawn on it. From SimHub's page the game is
        /// behind SimHub, so there's a countdown to switch to it first, shown on the panel over the game;
        /// the panel steps aside for the still itself so it can't end up covering the lights.
        /// </summary>
        private void PickFromStill(int countdown)
        {
            lock (_lock) if (_exporting) return;
            _box?.Close();
            EnsureOverlay();
            if (countdown > 0) Say("Switch to the game now: a still is taken in five seconds.");
            int left = countdown;
            void Tick()
            {
                lock (_lock) if (_exporting) return;
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
                    string game, car;
                    lock (_lock) { game = _liveGame; car = _liveCar; }
                    var shotAt = DateTime.UtcNow;
                    try { shot = DesktopSnapshot.Take(); }
                    catch (Exception ex)
                    {
                        _overlay?.SetSuppressed(false);
                        Say("Couldn't take a still of the screen: " + ex.Message);
                        return;
                    }
                    _overlay?.SetSuppressed(false);
                    var picker = new SnapshotPickerWindow(shot, SettingsBox(), region =>
                    {
                        var image = new CaptureBoxImage { Png = shot.RegionPng(region), Region = region, SelectedUtc = shotAt, Game = game, Car = car };
                        if (!SaveCaptureBox(region, final: true, image: image)) return;
                        try { shot.SaveRegion(Path.Combine(OutputFolder(), "capture-box.png"), region); }
                        catch (Exception ex) { SimHub.Logging.Current.Warn(LogPrefix + "Couldn't save capture-box.png: " + ex.Message); }
                    });
                    picker.Show();
                    picker.Activate();
                }), System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            Tick();
        }

        /// <summary>Shows the capture box, or brings the one already open back to the front.</summary>
        private void ShowCaptureBoxFor(Action<PixelRect> onSave, Func<PixelRect, string> test)
        {
            lock (_lock) if (_exporting) return;
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
                    if (SaveCaptureBox(region, final: true)) onSave?.Invoke(region);
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
                                      () => QueueExport(), ResetCapture, PageState, OpenExportPath, OpenBuilder,
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

        internal CapturePageState PageState()
        {
            lock (_lock)
            {
                string source = _session == null ? "" : DescribeSource(_session);
                if (_session == null)
                    source = Settings.ScreenCapture
                        ? (SettingsBox().Width >= 8 && SettingsBox().Height >= 4 ? "Screen reading; F1/iRacing use telemetry automatically" : "Screen reading needs a capture box")
                        : "Screen reading off; F1/iRacing telemetry or manual marks";
                return new CapturePageState
                {
                    Capturing = _capturing, Exporting = _exporting, Pending = _exportPending,
                    ExportFailed = _exportFailed, Status = OverlayStatus(), Source = source, Message = _message,
                    ExportSummary = _exportSummary, ExportDetails = _exportDetails,
                    ReportPath = _exportReportPath, OutputFolder = _exportFolder,
                    EvidenceFolder = _exportEvidenceFolder,
                    ProfilePath = string.IsNullOrEmpty(_exportReportPath) ? "" : _lastExportPath,
                    Game = _exportGame,
                };
            }
        }

        private void OpenExportPath(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    throw new IOException("The file or folder is no longer available: " + path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { Say("Couldn't open the export: " + ex.Message); }
        }

        internal const string BuilderUrl = "https://milky28.github.io/rpm-led-builder/";

        internal static string BuilderLaunchUrl(string game) =>
            BuilderUrl + "?capture=clipboard&sim=" + Uri.EscapeDataString(game ?? "");

        private void OpenBuilder()
        {
            var state = PageState();
            if (state.Exporting || string.IsNullOrEmpty(state.ProfilePath)) return;
            try { System.Windows.Clipboard.SetText(File.ReadAllText(state.ProfilePath)); }
            catch (Exception ex) { Say("Couldn't copy the car JSON: " + ex.Message + ". Open the output folder to copy it manually."); return; }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BuilderLaunchUrl(state.Game)) { UseShellExecute = true });
                Say("Car JSON copied. In RPM LED Builder, click Import copied capture.");
            }
            catch (Exception ex) { Say("Car JSON copied, but the browser couldn't open: " + ex.Message + ". Open " + BuilderLaunchUrl(state.Game) + " and click Import copied capture."); }
        }

        // Reserve the export before queueing it, so a second click or mapped action cannot race it.
        internal Task QueueExport()
        {
            lock (_actions)
            lock (_lock)
            {
                if (_shuttingDown) return _exportTask ?? Task.CompletedTask;
                if (!BeginExport()) return _exportTask ?? Task.CompletedTask;
                _exportTask = Task.Run(() => ExportCapture(RepoWaitOnExport, onShutdown: false));
                return _exportTask;
            }
        }

        private bool BeginExport()
        {
            if (_exporting) return false;
            _capturing = false;
            _evidence?.Stop();
            _captureSessionId++;
            _telemetry.Reset(_captureSessionId);
            _skipScreenFrame = true;
            if (_session == null || !_exportPending)
            {
                Say("Nothing new to export. Start a capture, then drive.");
                ShowOverlay(false);
                return false;
            }
            _exporting = true;
            _exportFailed = false;
            _exportSummary = "Exporting " + _session.CarId + " (" + _session.GameName + ")…";
            _exportGame = Slug.Make(_session.GameName);
            _exportDetails = "";
            _exportReportPath = "";
            _exportFolder = "";
            _exportEvidenceFolder = "";
            return true;
        }

        private void StopAndExport(TimeSpan repoWait, bool onShutdown)
        {
            lock (_actions)
            lock (_lock)
            {
                if (!BeginExport()) return;
            }
            ExportCapture(repoWait, onShutdown);
        }

        private void ExportCapture(TimeSpan repoWait, bool onShutdown)
        {
            try { WriteExport(repoWait, onShutdown); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error(LogPrefix + "Export failed", ex);
                ExportFailed("Export failed: " + ex.Message);
            }
            finally { lock (_lock) _exporting = false; }
        }

        private void ExportFailed(string message)
        {
            try { _evidence?.SaveFailure(_session, Settings, message); }
            catch (Exception ex) { message += " Capture notes could not be saved: " + ex.Message; }
            lock (_lock)
            {
                _exportFailed = true;
                _exportSummary = _session.CarId + " (" + _session.GameName + "): " + message;
            }
            Say(_exportSummary);
        }

        private void WriteExport(TimeSpan repoWait, bool onShutdown)
        {
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
            if (_evidence == null) _evidence = new CaptureEvidence(Settings);
            try { _evidence.SaveRaw(session, Settings, _screen, Path.Combine(OutputFolder(), Slug.Make(session.GameName))); }
            finally { lock (_lock) _exportEvidenceFolder = _evidence.Folder ?? ""; }

            RepoLookup lookup = null;
            if (lookupTask != null)
            {
                lookup = lookupTask.Wait(repoWait)
                    ? lookupTask.Result
                    : new RepoLookup { Status = RepoLookupStatus.Failed, Error = "timed out waiting for GitHub" };
            }

            string json, path, reportPath;
            ComposeResult result;
            try
            {
                // Exports keep the game's identity; ATSR may need a different development filename.
                path = Path.Combine(OutputFolder(), Slug.Make(session.GameName), Slug.Make(session.CarId) + ".json");
                lock (_lock) _exportFolder = Path.GetDirectoryName(path);
                reportPath = Path.ChangeExtension(path, ".report.txt");
                var overridesPath = Path.ChangeExtension(path, ".overrides.json");
                var localOverrides = File.Exists(overridesPath) ? File.ReadAllText(overridesPath) : null;
                var previousExport = File.Exists(path) ? File.ReadAllText(path) : null;
                // BeginExport waited for DataUpdate and disabled all writers; the screen worker is
                // joined above. Keep slow analysis outside _lock so the page can show its busy state.
                result = ProfileComposer.Compose(session, Settings, lookup, DateTime.Now, localOverrides, previousExport);
                if (localOverrides != null) result.Report.Add("Local overrides: " + overridesPath);
                result.Report.Add("Capture files: " + _evidence.Folder);
                result.Report.Add("Capture settings and build: " + Path.Combine(_evidence.Folder, "capture.json"));
                result.Report.AddRange(_evidence.Notes);
                if (onShutdown)
                    result.Report.Insert(Math.Min(2, result.Report.Count),
                        "Exported automatically: SimHub was closed with an unsaved capture." + Environment.NewLine);
                json = result.Profile.ToJson();
            }
            catch (Exception ex)
            {
                // An invalid override must not replace a previously checked export or ATSR copy.
                SimHub.Logging.Current.Error(LogPrefix + "Couldn't prepare export for " + session.CarId, ex);
                ExportFailed("Export failed: " + ex.Message + " Previous files were kept. Check the local export and overrides.");
                return;
            }

            string framesPath = null;
            if (Settings.SaveCaptureFrames && session.Screen.HasData)
            {
                framesPath = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + ".frames.csv");
                result.Report.Add("");
                result.Report.Add("Raw frames: " + framesPath);
            }

            var utf8 = new UTF8Encoding(false);
            bool copied = false;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var backup = ExportBackup.KeepPrevious(path, reportPath);
                if (backup != null) result.Report.Add("Previous export, report and raw frames backed up to: " + backup);
                File.WriteAllText(path, json, utf8);
                if (framesPath != null)
                {
                    File.Copy(_evidence.RawFramesPath, framesPath, true);
                }
                else
                {
                    // A previous capture's CSV must not masquerade as this export's raw frames.
                    // KeepPrevious has already saved it, including captures made before archives existed.
                    File.Delete(Path.ChangeExtension(path, ".frames.csv"));
                }
                copied = Settings.CopyToAtsrDeveloperFolder && CopyToAtsrDeveloperFolder(session.GameName, session.CarId, json, result.Report, utf8);
                File.WriteAllText(reportPath, string.Join(Environment.NewLine, result.Report) + Environment.NewLine, utf8);
                _evidence.SaveResult(session, Settings, json, string.Join(Environment.NewLine, result.Report) + Environment.NewLine);
                if (Settings.TopGearForNextExport != 0)
                {
                    Settings.TopGearForNextExport = 0;
                    this.SaveCommonSettings("CaptureSettings", Settings);
                }
                lock (_lock)
                {
                    _lastExportPath = path;
                    _lastReportPath = reportPath;
                    _exportReportPath = reportPath;
                    _exportPending = false;
                    _exportSummary = "Saved " + session.CarId + " (" + session.GameName + "). From " + result.Source + "." +
                        (result.AtsrProblems.Count > 0 ? " " + result.AtsrProblems.Count + " ATSR warning(s)." : "") +
                        (Settings.CopyToAtsrDeveloperFolder ? (copied ? " Copied to ATSR's local RPM folder." : " ATSR copy failed; see the report.") : "") +
                        " Read the details and report before submitting.";
                    var rpmSource = result.Summary.FirstOrDefault(line => line.StartsWith("RPM:", StringComparison.Ordinal));
                    if (rpmSource != null) _exportSummary += Environment.NewLine + rpmSource;
                    _exportDetails = string.Join(Environment.NewLine, result.Summary) + Environment.NewLine +
                        "File: " + path + Environment.NewLine + "Report: " + reportPath;
                }
                SimHub.Logging.Current.Info(LogPrefix + "Exported " + path + " (" + result.Source + "). Report: " + reportPath);
                foreach (var problem in result.AtsrProblems) SimHub.Logging.Current.Warn(LogPrefix + "ATSR: " + problem);
                Say(_exportSummary);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error(LogPrefix + "Export failed for " + path, ex);
                ExportFailed("Export failed: " + ex.Message + " Some files may already have been written; check the output folder.");
            }
        }

        // Lets the file be tried on real hardware straight away: with "Enable Local RPM Folder" on, ATSR reads this folder
        // before its own copy of the repo. Adds the outcome to the report.
        private static string SimHubFolder => Path.GetDirectoryName(typeof(CapturePlugin).Assembly.Location);

        private bool CopyToAtsrDeveloperFolder(string game, string carId, string json, List<string> report, Encoding encoding)
        {
            var devPath = AtsrCompatibility.DevelopmentFilePath(SimHubFolder, game, carId);
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
