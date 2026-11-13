using System;
using System.IO;
using System.Threading.Tasks;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Profile;
using LovelyCarDataCapture.Repo;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static CapturePlugin VerifyPlugin(string folder, CaptureSession session, bool verify)
        {
            var plugin = new CapturePlugin { Settings = new CaptureSettings { ShowOverlay = false, UseRepoFile = false, OutputFolder = folder } };
            WorkflowField("_session").SetValue(plugin, session);
            WorkflowField("_lookup").SetValue(plugin, Task.FromResult(RepoLookup.Disabled()));
            WorkflowField("_capturing").SetValue(plugin, true);
            WorkflowField("_exportPending").SetValue(plugin, true);
            WorkflowField("_verifying").SetValue(plugin, verify);
            return plugin;
        }

        private static void WorkflowVerifyLeavesTheFileAlone()
        {
            var root = Path.Combine(Path.GetTempPath(), "capture-verify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                const string drive = "pmr-mustang-gt3.frames.csv";
                string path = Path.Combine(root, "projectmotorracing", "verify-car.json");

                // Nothing exported yet: the drive is kept and can be exported as a capture instead.
                var first = VerifyPlugin(root, VerifySession(drive), verify: true);
                Equal("Stop and verify", first.PageState().ExportButton, "the stop button says what it will do");
                Check(first.QueueExport().Wait(20000), "verify without a file completes");
                var missing = first.PageState();
                Check(missing.ExportFailed && missing.Pending && !missing.Verifying && !File.Exists(path), "no file: nothing written, drive kept");
                Check(missing.ExportSummary.Contains("no exported file"), "and the page says why: " + missing.ExportSummary);
                Check(first.QueueExport().Wait(20000), "retry exports the drive");
                Check(File.Exists(path) && !first.PageState().Pending, "the kept drive became the car's file");
                string exported = File.ReadAllText(path);
                string report = File.ReadAllText(Path.ChangeExtension(path, ".report.txt"));

                // A second drive of the same car agrees, and every exported file is as it was.
                var second = VerifyPlugin(root, VerifySession("pmr-mustang-gt3-second.frames.csv"), verify: true);
                Check(second.QueueExport().Wait(20000), "verify completes");
                var verified = second.PageState();
                Check(!verified.Pending && !verified.ExportFailed && !verified.Verifying && verified.CanStart, "an agreeing drive is finished with");
                Check(verified.ExportSummary.Contains("Verified"), "the page gives the verdict: " + verified.ExportSummary);
                Equal(Path.ChangeExtension(path, ".verify.txt"), verified.ReportPath, "open report points at the verify report");
                Check(File.ReadAllText(verified.ReportPath).Contains("Verified:"), "the verify report is written beside the file");
                Check(File.Exists(Path.Combine(verified.EvidenceFolder, "verify.txt")), "and kept with the drive's frames");
                Equal(exported, File.ReadAllText(path), "the car file is untouched");
                Equal(report, File.ReadAllText(Path.ChangeExtension(path, ".report.txt")), "its report is untouched");
                Check(!Directory.Exists(Path.Combine(root, "projectmotorracing", "backups")), "nothing was backed up, because nothing was replaced");

                // A file that is wrong: the drive is kept so it can replace it.
                var wrong = CarProfile.Parse(exported);
                foreach (var row in wrong.LedRpm.Values) row[1] += 300;
                File.WriteAllText(path, wrong.ToJson());
                var third = VerifyPlugin(root, VerifySession("pmr-mustang-gt3-second.frames.csv"), verify: true);
                Check(third.QueueExport().Wait(20000), "verify of a wrong file completes");
                var differs = third.PageState();
                Check(differs.Pending && !differs.ExportFailed && !differs.Verifying, "a disagreeing drive is kept for export");
                Equal("Export capture", differs.ExportButton, "and the button offers to export it");
                Check(differs.ExportSummary.Contains("difference") && differs.ExportDetails.Contains("LED 1"), "the page names the difference: " + differs.ExportDetails);
                Equal(wrong.ToJson(), File.ReadAllText(path), "the wrong file is still not changed by verifying");
                Check(third.QueueExport().Wait(20000), "exporting the kept drive completes");
                Check(File.ReadAllText(path) != wrong.ToJson() && !third.PageState().Pending, "the drive replaced the file when asked to");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
