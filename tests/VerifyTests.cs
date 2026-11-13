using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Profile;
using LovelyCarDataCapture.Repo;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static CaptureSession VerifySession(string file, string game = "ProjectMotorRacing", string car = "Verify Car")
        {
            var session = new CaptureSession(game, car);
            foreach (var f in LoadFrames(DataPath(file))) session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
            return session;
        }

        private static CarProfile VerifyFile(string file) =>
            CarProfile.Parse(ProfileComposer.Compose(VerifySession(file), new CaptureSettings(), null, When).Profile.ToJson());

        private static void VerifyAgreesWithAnotherDriveOfTheSameCar()
        {
            // Two separate drives of each car, each checked against the file made from the other.
            foreach (var pair in new[]
            {
                new[] { "pmr-mustang-gt3.frames.csv", "pmr-mustang-gt3-second.frames.csv" },
                new[] { "pmr-mustang-gt3-second.frames.csv", "pmr-mustang-gt3.frames.csv" },
                new[] { "pmr-huracan-gt3-evo2.frames.csv", "pmr-huracan-gt3-evo2-second.frames.csv" },
                new[] { "acc-lamborghini-huracan-gt3-evo2.frames.csv", "acc-lamborghini-huracan-gt3-evo2-b.frames.csv" },
            })
            {
                var result = ProfileVerification.Verify(VerifyFile(pair[0]), VerifySession(pair[1]), new CaptureSettings(), When);
                Check(result.Compared && result.Passed, pair[1] + " against " + pair[0] + ": " + result.Summary + " " + string.Join(" ", result.Differences));
                Check(result.Readings >= 20 && result.Agreeing == result.Readings, pair[1] + ": every light value compared agrees (" + result.Readings + ")");
                Check(result.Report.Any(l => l.StartsWith("Verified:", StringComparison.Ordinal)), "the report leads with the verdict");
            }
        }

        private static void VerifyNamesWhatTheFileHasWrong()
        {
            const string drive = "acc-ginetta-g55-gt4.frames.csv";
            var file = VerifyFile(drive);
            string before = file.ToJson();
            Check(ProfileVerification.Verify(file, VerifySession(drive), new CaptureSettings(), When).Passed, "the file agrees with its own drive");

            foreach (var row in file.LedRpm.Values) row[2] += 150;
            file.LedColor[1] = "#FF0000FF";
            file.RedlineBlinkInterval = 120;
            string altered = file.ToJson();
            var result = ProfileVerification.Verify(file, VerifySession(drive), new CaptureSettings(), When);
            Check(result.Compared && !result.Passed, "a wrong file is not verified");
            Check(result.Differences.Any(d => d.Contains("LED 2") && d.Contains("150 rpm earlier")), "the moved light is named with how far out it is: " + string.Join(" | ", result.Differences));
            Check(result.Differences.Any(d => d.StartsWith("LED 1:", StringComparison.Ordinal) && d.Contains("#FF0000FF")), "the wrong colour is named");
            Check(result.Differences.Any(d => d.StartsWith("Blink:", StringComparison.Ordinal)), "the blink the game doesn't have is named");
            Check(!result.Differences.Any(d => d.Contains("LED 3")), "lights the file has right are not listed");
            Check(result.Agreeing < result.Readings && result.Agreeing > 0, "the rest still count as agreeing");
            Equal(altered, file.ToJson(), "verifying changes nothing in the file");
            Check(before != altered, "the test altered the file");
        }

        private static void VerifyRefusesADifferentStrip()
        {
            var file = VerifyFile("pmr-mustang-gt3.frames.csv");
            var result = ProfileVerification.Verify(file, VerifySession("acc-ginetta-g55-gt4.frames.csv"), new CaptureSettings(), When);
            Check(!result.Compared && !result.Passed, "a strip with a different number of lights is not compared");
            Check(result.Summary.Contains("LEDs"), "and the summary says why: " + result.Summary);

            var empty = ProfileVerification.Verify(file, new CaptureSession("ProjectMotorRacing", "Verify Car"), new CaptureSettings(), When);
            Check(!empty.Compared && empty.Summary.StartsWith("Not verified", StringComparison.Ordinal), "a drive with no screen frames is not verified");
        }
    }
}
