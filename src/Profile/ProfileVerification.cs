using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Profile
{
    /// <summary>What a second drive found when it was checked against a car file.</summary>
    internal sealed class VerifyResult
    {
        /// <summary>False when the drive couldn't be compared with the file at all.</summary>
        public bool Compared;
        /// <summary>Light switch-on readings the drive measured tightly enough to compare.</summary>
        public int Readings;
        /// <summary>...and how many of them the file agrees with.</summary>
        public int Agreeing;
        /// <summary>Everything the drive saw differently from the file, one line each.</summary>
        public List<string> Differences = new List<string>();
        /// <summary>Gears and lights the file has that this drive never measured.</summary>
        public List<string> NotChecked = new List<string>();
        public bool Passed => Compared && Readings > 0 && Differences.Count == 0;
        public string Summary = "";
        public List<string> Report = new List<string>();
    }

    /// <summary>
    /// Checks a car file against a second drive: where the drive puts each light, the colours it saw,
    /// the redline and the blink, each beside what the file says. It changes nothing.
    /// </summary>
    /// <remarks>
    /// The drive is turned into the car file it would export by itself, with no repo file, previous
    /// export or overrides behind it, and that is what the file is compared with. The same judgement
    /// is then applied to both: neutral readings left out where driven gears measured the light,
    /// mirrored pairs lit together, a gear that disagrees with the rest dropped. Comparing each raw
    /// reading instead flags a file against the very capture it was made from in 17 of the 56 captures
    /// in tests/data.
    /// </remarks>
    internal static class ProfileVerification
    {
        // A capture measures a light to within 10-20 rpm, 40-60 in neutral, first gear and on the last
        // light before the redline, and this compares two of them.
        public const int AgreeRpm = 60;
        // The redline is read from where the colour changes, between two frames at the limiter.
        public const int RedlineAgreeRpm = 100;
        // A blink's dark phase is two to six frames long, so it can't be timed closer than a frame or two.
        private const int BlinkAgreeMs = 35;

        public static VerifyResult Verify(CarProfile file, CaptureSession session, CaptureSettings cfg, DateTime verifiedAt)
        {
            var result = new VerifyResult();
            var differences = result.Differences;
            var lines = new List<string>();
            var drive = session.Screen.HasData ? session.Screen.Result() : null;
            var layout = drive?.Layout;
            if (drive == null)
            {
                result.Summary = "Not verified: a verify drive reads the rev lights off the screen, and none were recorded.";
            }
            else if (layout == null)
            {
                result.Summary = "Not verified: the rev lights couldn't be made out in this drive.";
                lines.AddRange(drive.Notes.Select(n => "  - " + n));
            }
            else if (layout.LedNumber != file.LedNumber)
            {
                result.Summary = "Not verified: the file has " + file.LedNumber + " LEDs but " + layout.LedNumber +
                                 " were seen on screen. Check the capture box covers the whole strip and nothing else.";
            }
            else
            {
                result.Compared = true;
                var own = ProfileComposer.Compose(session, cfg, null, verifiedAt).Profile;
                for (int i = 1; i <= file.LedNumber; i++)
                {
                    bool fileGap = Gap(file, i), driveGap = Gap(own, i);
                    if (driveGap && !fileGap) differences.Add("LED " + i + " never lit in this drive, but the file lights it.");
                    if (!driveGap && fileGap) differences.Add("LED " + i + " lit in this drive, but the file leaves it as a gap.");
                }

                // Only gears this drive measured for itself: the rest of its rows are fallbacks.
                var measured = drive.Gears.Where(g => g.Leds.Any(ProfileComposer.Trusted)).OrderBy(g => CarProfile.GearRank(g.Gear)).ToList();
                foreach (var gear in measured)
                {
                    if (!file.LedRpm.TryGetValue(gear.Gear, out var row))
                    {
                        differences.Add("Gear " + gear.Gear + " was driven, but the file has no row for it.");
                        continue;
                    }
                    var mine = own.LedRpm[gear.Gear];
                    lines.Add("");
                    lines.Add("Gear " + gear.Gear + ":");
                    for (int i = 1; i <= file.LedNumber; i++)
                    {
                        if (Gap(file, i) || Gap(own, i) || mine[i] <= 0 || row[i] <= 0) continue;
                        int diff = mine[i] - row[i];
                        bool agrees = Math.Abs(diff) <= AgreeRpm;
                        result.Readings++;
                        if (agrees) result.Agreeing++;
                        else differences.Add("Gear " + gear.Gear + ", LED " + i + ": this drive puts it at " + mine[i] + " rpm, " +
                                             Math.Abs(diff) + " rpm " + (diff > 0 ? "later" : "earlier") + " than the file's " + row[i] + ".");
                        lines.Add(string.Format(CultureInfo.InvariantCulture, "  LED {0,2}  file {1,6}  drive {2,6}  {3}{4}{5}", i, row[i], mine[i],
                            diff.ToString("+0;-0;0", CultureInfo.InvariantCulture), agrees ? "" : "  DIFFERENT",
                            ProfileComposer.Trusted(gear.Leds[i - 1]) ? "" : "  (taken from other gears)"));
                    }
                    bool redlineSeen = drive.RedlineByGear.TryGetValue(gear.Gear, out var seen) ? seen.OnsetSeen
                        : drive.RedlineRpm.HasValue || drive.SteadyAtLimiterRpm.HasValue;
                    if (redlineSeen && row[0] > 0 && mine[0] > 0)
                    {
                        int diff = mine[0] - row[0];
                        bool agrees = Math.Abs(diff) <= RedlineAgreeRpm;
                        if (!agrees) differences.Add("Gear " + gear.Gear + ": this drive puts the redline at " + mine[0] + " rpm, " + Math.Abs(diff) + " rpm " +
                                                     (diff > 0 ? "later" : "earlier") + " than the file's " + row[0] + ".");
                        lines.Add(string.Format(CultureInfo.InvariantCulture, "  Redline file {0,6}  drive {1,6}  {2}{3}", row[0], mine[0],
                            diff.ToString("+0;-0;0", CultureInfo.InvariantCulture), agrees ? "" : "  DIFFERENT"));
                    }
                }
                var undriven = file.GearOrder.Where(g => CarProfile.GearRank(g) >= 1 && measured.All(m => m.Gear != g)).ToList();
                if (undriven.Count > 0) result.NotChecked.Add("gear " + string.Join(", ", undriven) + " (no light measured there)");

                lines.Add("");
                lines.Add("Colours:");
                var unknown = new List<int>();
                for (int i = 1; i <= file.LedNumber; i++)
                {
                    if (Gap(file, i) || Gap(own, i)) continue;
                    if (drive.ColorUnknown != null && drive.ColorUnknown[i - 1]) { unknown.Add(i); continue; }
                    bool same = ProfileComposer.SameColour(file.LedColor[i], own.LedColor[i]);
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "  LED {0,2}  file {1}  drive {2}{3}", i, file.LedColor[i], own.LedColor[i], same ? "" : "  DIFFERENT"));
                    if (!same) differences.Add("LED " + i + ": this drive reads its colour as " + own.LedColor[i] + ", but the file has " + file.LedColor[i] + ".");
                }
                if (unknown.Count > 0) result.NotChecked.Add("the colour of LED " + string.Join(", ", unknown) + " (only seen lit above the redline)");

                bool reachedRedline = drive.RedlineRpm.HasValue || drive.SteadyAtLimiterRpm.HasValue || drive.BlinkSeen;
                if (reachedRedline)
                {
                    bool sameColour = LedLayout.IsGapColor(file.LedColor[0]) == LedLayout.IsGapColor(own.LedColor[0]) &&
                                      ProfileComposer.SameColour(file.LedColor[0], own.LedColor[0]);
                    lines.Add("  Redline  file " + file.LedColor[0] + "  drive " + own.LedColor[0] + (sameColour ? "" : "  DIFFERENT"));
                    if (!sameColour) differences.Add("Redline colour: this drive reads it as " + own.LedColor[0] + ", but the file has " + file.LedColor[0] + ".");

                    int mine = own.RedlineBlinkInterval, theirs = file.RedlineBlinkInterval;
                    bool sameBlink = (mine > 0) == (theirs > 0) && Math.Abs(mine - theirs) <= BlinkAgreeMs;
                    lines.Add("");
                    lines.Add("Blink: file " + theirs + " ms, drive " + mine + " ms" + (sameBlink ? "" : "  DIFFERENT"));
                    if (!sameBlink) differences.Add(mine > 0 && theirs > 0
                        ? "Blink: dark for about " + mine + " ms at a time in this drive, but the file has " + theirs + " ms."
                        : mine > 0 ? "Blink: the strip blinked at the limiter (about " + mine + " ms), but the file has no blink."
                        : "Blink: the strip didn't blink at the limiter, but the file blinks every " + theirs + " ms.");
                }
                else result.NotChecked.Add("the redline colour and blink (the limiter wasn't reached)");

                result.Summary = result.Readings == 0
                    ? "Not verified: no light was measured tightly enough to compare. Rev slowly from idle to the limiter a few times."
                    : differences.Count == 0
                        ? "Verified: all " + result.Readings + " light values agree with the file" +
                          (result.NotChecked.Count > 0 ? "; some of the file wasn't checked." : ".")
                        : differences.Count + " difference(s) from the file; " + result.Agreeing + " of " + result.Readings + " light values agree.";
            }

            var report = result.Report;
            report.Add("Lovely Car Data verify report");
            report.Add("");
            report.Add("Game:      " + session.GameName);
            report.Add("Car:       " + session.CarId);
            report.Add("Verified:  " + verifiedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            report.Add("");
            report.Add(result.Summary);
            if (differences.Count > 0)
            {
                report.Add("");
                report.Add("Differences (a light counts as different more than " + AgreeRpm + " rpm from the file, the redline more than " + RedlineAgreeRpm + "):");
                report.AddRange(differences.Select(d => "  - " + d));
            }
            if (result.NotChecked.Count > 0)
            {
                report.Add("");
                report.Add("Not checked in this drive:");
                report.AddRange(result.NotChecked.Select(n => "  - " + n));
            }
            if (result.Compared)
            {
                report.Add("");
                report.Add("Each value below is what this drive would have exported by itself, beside the file's.");
            }
            report.AddRange(lines);
            if (result.Compared && drive.Notes.Count > 0)
            {
                report.Add("");
                report.Add("Notes from reading this drive:");
                report.AddRange(drive.Notes.Select(n => "  - " + n));
            }
            return result;
        }

        /// <summary>A slot the file never lights: black, with no RPM in any gear.</summary>
        private static bool Gap(CarProfile p, int slot) =>
            LedLayout.IsGapColor(p.LedColor[slot]) && p.LedRpm.Values.All(row => row[slot] == 0);
    }
}
