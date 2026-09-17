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
using SimHub.Plugins;

namespace LovelyCarDataCapture
{
    [PluginName("Lovely Car Data Capture")]
    [PluginAuthor("Lovely Car Data contributors")]
    [PluginDescription("Records car id, gears and LED RPMs from live telemetry (real rev/shift lights for F1 and iRacing) and exports a Lovely Car Data v2.0.0 car file, starting from the car's current repo file when there is one.")]
    public class CapturePlugin : IPlugin, IDataPlugin
    {
        private const string LogPrefix = "[LovelyCarDataCapture] ";
        private static readonly TimeSpan RepoWaitOnExport = TimeSpan.FromSeconds(10);

        // Actions arrive on a different thread than DataUpdate.
        private readonly object _lock = new object();
        private readonly RepoClient _repo = new RepoClient();
        private CaptureSession _session;
        private Task<RepoLookup> _lookup;
        private volatile bool _capturing;
        private volatile string _lastExportPath = "";
        private volatile string _lastReportPath = "";
        private volatile string _repoStatus = "";
        private string _loggedRawType;

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

            this.AddAction(actionName: "StartCapture", actionStart: (pm, _) => StartCapture());
            this.AddAction(actionName: "StopAndExport", actionStart: (pm, _) => StopAndExport());
            this.AddAction(actionName: "ResetCapture", actionStart: (pm, _) => { lock (_lock) { _session = null; _lookup = null; } _repoStatus = ""; });
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (!_capturing || !data.GameRunning || data.GamePaused || data.GameReplay || data.NewData == null) return;
            var d = data.NewData;
            if (string.IsNullOrEmpty(d.CarId)) return;

            lock (_lock)
            {
                if (_session == null || _session.CarId != d.CarId || _session.GameName != data.GameName)
                {
                    if (_session != null)
                        SimHub.Logging.Current.Warn(LogPrefix + "Car changed from " + _session.CarId + " to " + d.CarId + "; the previous capture was discarded. Export before switching cars.");
                    _session = new CaptureSession(data.GameName, d.CarId);
                    StartRepoLookup(data.GameName, d.CarId);
                }

                var s = _session;
                s.RecordCar(d.CarModel, d.CarClass);
                // CarSettings_CurrentGearRedLineRPM resolves to SimHub's per-gear redline when enabled for this car, otherwise the car-wide redline.
                s.Redline.Record(d.Gear, d.Rpms, d.CarSettings_CurrentGearRedLineRPM, d.CarSettings_RedLineRPM,
                    Math.Max(d.CarSettings_MaxRPM, d.MaxRpm), d.CarSettings_MaxGears);

                // The pit limiter drives its own light patterns in most games.
                if (d.PitLimiterOn != 0) { s.PitLimiterSamples++; return; }

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
            this.SaveCommonSettings("CaptureSettings", Settings);
        }

        private void StartCapture()
        {
            lock (_lock) { _session = null; _lookup = null; }
            _repoStatus = "";
            _capturing = true;
            SimHub.Logging.Current.Info(LogPrefix + "Capture started. Drive through every gear and rev each one to the limiter.");
        }

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

        private void StopAndExport()
        {
            _capturing = false;

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
                return;
            }

            RepoLookup lookup = null;
            if (lookupTask != null)
            {
                lookup = lookupTask.Wait(RepoWaitOnExport)
                    ? lookupTask.Result
                    : new RepoLookup { Status = RepoLookupStatus.Failed, Error = "timed out waiting for GitHub" };
            }

            string json, path, reportPath;
            ComposeResult result;
            // The session is no longer written to (capturing is off), but DataUpdate may still be mid-frame.
            lock (_lock)
            {
                result = ProfileComposer.Compose(session, Settings, lookup, DateTime.Now);
                json = result.Profile.ToJson();
                var root = string.IsNullOrWhiteSpace(Settings.OutputFolder)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SimHub", "LovelyCarDataCapture")
                    : Settings.OutputFolder;
                var fileName = lookup?.Status == RepoLookupStatus.Found
                    ? Path.GetFileNameWithoutExtension(lookup.RelativePath)
                    : Slug.Make(session.CarId);
                path = Path.Combine(root, Slug.Make(session.GameName), fileName + ".json");
                reportPath = Path.Combine(root, Slug.Make(session.GameName), fileName + ".report.txt");
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var utf8 = new UTF8Encoding(false);
                File.WriteAllText(path, json, utf8);
                File.WriteAllText(reportPath, string.Join(Environment.NewLine, result.Report) + Environment.NewLine, utf8);
                _lastExportPath = path;
                _lastReportPath = reportPath;
                SimHub.Logging.Current.Info(LogPrefix + "Exported " + path + " (" + result.Source + "). Report: " + reportPath);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error(LogPrefix + "Export failed for " + path, ex);
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
            return "SimHub redline only";
        }

        // e.g. "1:15/15 2:15/15 3:9/15" for F1, gear list for iRacing.
        private static string DescribeProgress(CaptureSession s)
        {
            if (s == null) return "";
            if (s.F1.HasData)
                return string.Join(" ", s.F1.Gears.OrderBy(CarProfile.GearRank).Select(g => g + ":" + s.F1.Result(g).CapturedCount + "/" + F1RevLightCapture.LedCount));
            if (s.IRacing.HasData)
                return (s.IRacing.CarWide != null ? "car-wide ✓ " : "") + string.Join(" ", s.IRacing.Gears.OrderBy(CarProfile.GearRank));
            return "";
        }
    }
}
