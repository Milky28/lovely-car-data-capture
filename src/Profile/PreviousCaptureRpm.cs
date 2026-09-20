using System;
using System.Collections.Generic;
using System.Linq;

namespace LovelyCarDataCapture.Profile
{
    internal static class PreviousCaptureRpm
    {
        // The caller reads only this car's export inside this game's output folder, never ATSR's
        // shared folder. Keep whole undriven rows, not old colors or individual uncertain readings.
        public static List<string> Restore(CarProfile profile, string carId, string json, IEnumerable<string> capturedGears, List<string> notes,
            bool[] observedGaps = null)
        {
            if (json == null) return new List<string>();
            var previous = CarProfile.Parse(json);
            if (!string.Equals(previous.CarId, carId, StringComparison.OrdinalIgnoreCase) ||
                previous.LedNumber != profile.LedNumber || previous.LedColor.Count != profile.LedColor.Count ||
                !CompatibleGaps(previous, profile, observedGaps))
            {
                notes.Add("Previous local RPMs were not reused because the car identity, LED count or gap positions changed.");
                return new List<string>();
            }
            var captured = new HashSet<string>(capturedGears);
            var kept = new List<string>();
            foreach (var gear in previous.GearOrder.Where(g => !captured.Contains(g)))
            {
                var row = previous.LedRpm[gear];
                if (CarProfile.GearRank(gear) >= 1000 || row.Length != profile.LedNumber + 1 || row.Any(v => v < 0))
                {
                    notes.Add("Previous local RPMs for gear " + gear + " were invalid and were not reused.");
                    continue;
                }
                profile.EnsureGear(gear);
                profile.LedRpm[gear] = (int[])row.Clone();
                kept.Add(gear);
            }
            if (kept.Count > 0)
                notes.Add("Kept the previous local export's RPMs, including redline, for gear " + string.Join(", ", kept) +
                    ". These gears were not captured in this drive; their previous values replace the fallbacks described above.");
            return kept;
        }

        private static bool CompatibleGaps(CarProfile previous, CarProfile profile, bool[] observedGaps)
        {
            var oldGaps = LedLayout.Gaps(previous);
            var newGaps = LedLayout.Gaps(profile);
            // A current screen gap will be made black in every row after restoration. Accept an
            // older file's incorrect color there without discarding its other RPM measurements.
            return Enumerable.Range(1, profile.LedNumber).All(i => oldGaps[i] == newGaps[i] ||
                (observedGaps != null && observedGaps.Length == profile.LedNumber && observedGaps[i - 1]));
        }
    }
}
