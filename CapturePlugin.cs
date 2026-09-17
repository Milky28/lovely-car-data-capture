using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GameReaderCommon;
using SimHub.Plugins;

namespace LovelyCarDataCapture
{
    [PluginName("Lovely Car Data Capture")]
    [PluginAuthor("Lovely Car Data contributors")]
    [PluginDescription("Records car id, gears and redline RPM from live telemetry and exports a starter Lovely Car Data v2.0.0 profile.")]
    public class CapturePlugin : IPlugin, IDataPlugin
    {
        private const string LogPrefix = "[LovelyCarDataCapture] ";

        // Actions arrive on a different thread than DataUpdate.
        private readonly object _lock = new object();
        private CaptureSession _session;
        private volatile bool _capturing;
        private string _lastExportPath = "";

        public CaptureSettings Settings;
        public PluginManager PluginManager { get; set; }

        public void Init(PluginManager pluginManager)
        {
            Settings = this.ReadCommonSettings("CaptureSettings", () => new CaptureSettings());

            this.AttachDelegate("Capturing", () => _capturing);
            this.AttachDelegate("CarId", () => { lock (_lock) return _session?.CarId ?? ""; });
            this.AttachDelegate("GearsSeen", () => { lock (_lock) return _session == null ? "" : string.Join(",", _session.Gears.Keys); });
            this.AttachDelegate("LastExportPath", () => _lastExportPath);

            this.AddAction(actionName: "StartCapture", actionStart: (pm, _) => StartCapture());
            this.AddAction(actionName: "StopAndExport", actionStart: (pm, _) => StopAndExport());
            this.AddAction(actionName: "ResetCapture", actionStart: (pm, _) => { lock (_lock) _session = null; });
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (!_capturing || !data.GameRunning || data.GamePaused || data.GameReplay || data.NewData == null) return;
            var d = data.NewData;
            if (string.IsNullOrEmpty(d.CarId)) return;

            lock (_lock)
            {
                if (_session == null || _session.CarId != d.CarId)
                {
                    _session = new CaptureSession(data.GameName, d.CarId);
                }
                _session.Record(d);
            }
        }

        public void End(PluginManager pluginManager)
        {
            this.SaveCommonSettings("CaptureSettings", Settings);
        }

        private void StartCapture()
        {
            lock (_lock) _session = null;
            _capturing = true;
            SimHub.Logging.Current.Info(LogPrefix + "Capture started - drive through every gear up to the limiter.");
        }

        private void StopAndExport()
        {
            _capturing = false;

            string json, path;
            var notes = new List<string>();
            lock (_lock)
            {
                if (_session == null)
                {
                    SimHub.Logging.Current.Warn(LogPrefix + "Nothing captured - start a capture and drive first.");
                    return;
                }
                json = CarProfileBuilder.Build(_session, Settings, notes);
                var root = string.IsNullOrWhiteSpace(Settings.OutputFolder)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SimHub", "LovelyCarDataCapture")
                    : Settings.OutputFolder;
                path = Path.Combine(root, CarProfileBuilder.Slugify(_session.GameName), CarProfileBuilder.Slugify(_session.CarId) + ".json");
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, json, new UTF8Encoding(false));
                _lastExportPath = path;
                SimHub.Logging.Current.Info(LogPrefix + "Exported " + path + Environment.NewLine + string.Join(Environment.NewLine, notes));
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error(LogPrefix + "Export failed for " + path, ex);
            }
        }
    }
}
