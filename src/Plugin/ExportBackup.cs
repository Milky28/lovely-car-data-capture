using System;
using System.Globalization;
using System.IO;

namespace LovelyCarDataCapture.Plugin
{
    internal static class ExportBackup
    {
        // Keep the original files together, byte for byte. Any failure propagates before the
        // exporter replaces files or updates ATSR, so a failed backup cannot lose a good drive.
        public static string KeepPrevious(string profilePath, string reportPath)
        {
            var framesPath = Path.ChangeExtension(profilePath, ".frames.csv");
            if (!File.Exists(profilePath) && !File.Exists(reportPath) && !File.Exists(framesPath)) return null;
            var folder = Path.Combine(Path.GetDirectoryName(profilePath), "backups",
                Path.GetFileNameWithoutExtension(profilePath),
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(folder);
            if (File.Exists(profilePath)) File.Copy(profilePath, Path.Combine(folder, Path.GetFileName(profilePath)));
            if (File.Exists(reportPath)) File.Copy(reportPath, Path.Combine(folder, Path.GetFileName(reportPath)));
            // Older captures have no evidence folder yet; preserve their only raw recording too.
            if (File.Exists(framesPath)) File.Copy(framesPath, Path.Combine(folder, Path.GetFileName(framesPath)));
            return folder;
        }
    }
}
