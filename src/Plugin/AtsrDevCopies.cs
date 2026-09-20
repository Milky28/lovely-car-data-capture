using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LovelyCarDataCapture.Profile;

namespace LovelyCarDataCapture
{
    /// <summary>One file this plugin put in ATSR's local RPM folder.</summary>
    public sealed class AtsrCopy
    {
        /// <summary>File name inside the folder, which is what ATSR matches the car on.</summary>
        public string File { get; set; }
        public string Game { get; set; }
        public string CarId { get; set; }
        public DateTime Written { get; set; }

        public override string ToString() =>
            File + " - " + Game + ", " + Written.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
    }
}

namespace LovelyCarDataCapture.Plugin
{
    /// <summary>
    /// Keeps track of the exports copied to ATSR's local RPM folder, so they can be tried on the
    /// wheel straight after a drive and taken out again afterwards.
    /// </summary>
    /// <remarks>
    /// ATSR reads that folder by car id alone - rpm_data\&lt;car id&gt;.json, no game - and while a file
    /// is there it's used instead of the repo's for that car in every game. So a copy left behind keeps
    /// overriding the real data long after it was checked, and the same car id in two games (the
    /// McLaren 720S GT3 Evo in AMS2 and ACC) shares one file. The record says which files are this
    /// plugin's, which game each came from, and when.
    /// </remarks>
    internal static class AtsrDevCopies
    {
        private static readonly object Gate = new object();

        public static string Folder(string simHubFolder) =>
            Path.GetDirectoryName(AtsrCompatibility.DevelopmentFilePath(simHubFolder, null, "x"));

        /// <summary>Writes an export to the folder and records it. Returns lines for the report.</summary>
        public static List<string> Write(CaptureSettings settings, string simHubFolder, string game, string carId, string json, Encoding encoding)
        {
            var lines = new List<string>();
            var path = AtsrCompatibility.DevelopmentFilePath(simHubFolder, game, carId);
            var name = Path.GetFileName(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            lock (Gate)
            {
                var previous = settings.AtsrCopies.FirstOrDefault(c => Same(c.File, name));
                if (File.Exists(path))
                {
                    if (previous == null)
                    {
                        // Someone's own file: never lost, only moved aside under a name ATSR doesn't read.
                        var backup = path + ".before-capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                        File.Copy(path, backup);
                        lines.Add("  A file for this car was already there that this plugin didn't write; it was kept as " +
                                  Path.GetFileName(backup) + ".");
                    }
                    else if (!string.Equals(previous.Game, game, StringComparison.OrdinalIgnoreCase))
                        lines.Add("  This replaced your " + previous.Game + " copy from " +
                                  previous.Written.ToString("d MMM HH:mm", CultureInfo.InvariantCulture) +
                                  ". ATSR keeps one file per car id for every game, so only one of the two can be tried at a time.");
                }
                File.WriteAllText(path, json, encoding);
                settings.AtsrCopies.RemoveAll(c => Same(c.File, name));
                settings.AtsrCopies.Add(new AtsrCopy { File = name, Game = game, CarId = carId, Written = DateTime.Now });
            }
            return lines;
        }

        /// <summary>
        /// This plugin's copies still in the folder, newest first. Records whose file has gone are
        /// dropped, and files that match an export exactly are recognised - copies made before the
        /// record was kept.
        /// </summary>
        public static List<AtsrCopy> Current(CaptureSettings settings, string simHubFolder, string outputFolder, out bool changed)
        {
            changed = false;
            var folder = Folder(simHubFolder);
            lock (Gate)
            {
                changed |= settings.AtsrCopies.RemoveAll(c => !File.Exists(Path.Combine(folder, c.File))) > 0;
                if (Directory.Exists(folder) && Directory.Exists(outputFolder))
                {
                    foreach (var file in Directory.GetFiles(folder, "*.json"))
                    {
                        var name = Path.GetFileName(file);
                        if (settings.AtsrCopies.Any(c => Same(c.File, name))) continue;
                        var export = Directory.GetDirectories(outputFolder)
                                              .SelectMany(d => Directory.GetFiles(d, "*.json"))
                                              .FirstOrDefault(e => IsMatchingExport(e, file, name));
                        if (export == null) continue;
                        settings.AtsrCopies.Add(new AtsrCopy
                        {
                            File = name,
                            Game = Path.GetFileName(Path.GetDirectoryName(export)),
                            CarId = CarProfile.Parse(File.ReadAllText(export)).CarId,
                            Written = File.GetLastWriteTime(file),
                        });
                        changed = true;
                    }
                }
                return settings.AtsrCopies.OrderByDescending(c => c.Written).ToList();
            }
        }

        /// <summary>Moves one of this plugin's copies to the Recycle Bin. Returns null, or what went wrong.</summary>
        public static string Remove(CaptureSettings settings, string simHubFolder, AtsrCopy copy)
        {
            var path = Path.Combine(Folder(simHubFolder), copy.File);
            try
            {
                if (File.Exists(path))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                                                       Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                lock (Gate) settings.AtsrCopies.RemoveAll(c => Same(c.File, copy.File));
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static bool IsMatchingExport(string export, string devFile, string devName)
        {
            if (!File.Exists(export) || File.ReadAllText(export) != File.ReadAllText(devFile)) return false;
            try
            {
                var profile = CarProfile.Parse(File.ReadAllText(export));
                var game = Path.GetFileName(Path.GetDirectoryName(export));
                return Same(AtsrCompatibility.DevelopmentFileName(game, profile.CarId), devName);
            }
            catch { return false; }
        }
    }
}
