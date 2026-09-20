using System;
using System.IO;
using System.Linq;
using LovelyCarDataCapture.Plugin;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static void ExportBackupsKeepPreviousFiles()
        {
            var root = Path.Combine(Path.GetTempPath(), "capture-backup-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var profile = Path.Combine(root, "car.json");
                var report = Path.Combine(root, "car.report.txt");
                Check(ExportBackup.KeepPrevious(profile, report) == null, "first export needs no backup");
                var bytes = new byte[] { 0xef, 0xbb, 0xbf, 123, 125, 13, 10 };
                File.WriteAllBytes(profile, bytes);
                File.WriteAllText(report, "Previously checked report\r\n");
                var first = ExportBackup.KeepPrevious(profile, report);
                Check(File.ReadAllBytes(Path.Combine(first, "car.json")).SequenceEqual(bytes), "original JSON bytes preserved");
                Equal(File.ReadAllText(report), File.ReadAllText(Path.Combine(first, "car.report.txt")), "report preserved together");
                File.WriteAllText(profile, "second capture");
                var second = ExportBackup.KeepPrevious(profile, report);
                Check(first != second, "successive exports have distinct backups");
                Check(File.ReadAllBytes(Path.Combine(first, "car.json")).SequenceEqual(bytes), "later exports do not overwrite earlier backup");
                Equal("second capture", File.ReadAllText(Path.Combine(second, "car.json")), "second backup contains second profile");
                File.Delete(profile);
                var reportOnly = ExportBackup.KeepPrevious(profile, report);
                Check(File.Exists(Path.Combine(reportOnly, "car.report.txt")), "orphan report is still backed up");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void ExportBackupFailureKeepsOriginals()
        {
            var root = Path.Combine(Path.GetTempPath(), "capture-backup-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var profile = Path.Combine(root, "car.json");
                var report = Path.Combine(root, "car.report.txt");
                File.WriteAllText(profile, "checked profile");
                File.WriteAllText(report, "checked report");
                File.WriteAllText(Path.Combine(root, "backups"), "blocks backup folder creation");
                bool failed = false;
                try { ExportBackup.KeepPrevious(profile, report); }
                catch (IOException) { failed = true; }
                Check(failed, "backup failure aborts before replacements");
                Equal("checked profile", File.ReadAllText(profile), "profile survives backup failure");
                Equal("checked report", File.ReadAllText(report), "report survives backup failure");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
