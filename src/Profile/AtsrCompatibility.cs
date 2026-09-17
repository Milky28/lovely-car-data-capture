using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LovelyCarDataCapture.Repo;
using LovelyCarDataCapture.Util;

namespace LovelyCarDataCapture.Profile
{
    /// <summary>
    /// Checks a car file against how the ATSR SimHub plugin, the main consumer of Lovely Car Data,
    /// reads it: files are looked up as data/&lt;game&gt;/&lt;slug of carId&gt;.json, gears are taken by
    /// position (R, N, 1, 2, …), black LEDs are gaps, an LED with RPM 0 is always lit, and the
    /// layout comes from the last gear.
    /// </summary>
    internal static class AtsrCompatibility
    {
        public const string DevelopmentFolder = "_ATSR_DevelopmentData";

        /// <summary>
        /// Where ATSR's Developer Mode reads car files from: &lt;SimHub folder&gt;\_ATSR_DevelopmentData\rpm_data\&lt;slug of carId&gt;.json.
        /// There's no game subfolder.
        /// </summary>
        public static string DevelopmentFilePath(string simHubFolder, string carId) =>
            Path.Combine(simHubFolder, DevelopmentFolder, "rpm_data", Slug.Make(carId) + ".json");

        public static List<string> Check(CarProfile p, string gameName, RepoLookup lookup)
        {
            var notes = AtsrSpecialCars.Describe(p.CarId);
            var sim = Slug.Make(gameName);
            var expectedPath = sim + "/" + Slug.Make(p.CarId) + ".json";

            if (lookup != null && lookup.Status == RepoLookupStatus.Found && lookup.RelativePath != expectedPath)
                notes.Add("ATSR looks for data/" + expectedPath + ", so it never finds the repo's data/" + lookup.RelativePath +
                          ". This export is named " + Path.GetFileName(expectedPath) + "; submit it under that name and remove the old file.");

            var expectedOrder = new List<string> { "R", "N" };
            for (int i = 1; i <= p.GearOrder.Count - 2; i++) expectedOrder.Add(i.ToString(CultureInfo.InvariantCulture));
            if (p.GearOrder.Count > 0 && !p.GearOrder.SequenceEqual(expectedOrder))
                notes.Add("ATSR picks each gear's values by position, not by name, so gears must be in the order " + string.Join(", ", expectedOrder) +
                          ". This file has " + string.Join(", ", p.GearOrder) + ".");

            var gaps = LedLayout.Gaps(p);
            var alwaysLit = new List<string>();
            foreach (var gear in p.GearOrder)
            {
                var row = p.LedRpm[gear];
                if (row.All(v => v == 0)) continue;
                var leds = Enumerable.Range(1, row.Length - 1).Where(i => row[i] == 0 && !(i < gaps.Length && gaps[i])).ToList();
                if (leds.Count > 0) alwaysLit.Add(gear + " (LED " + string.Join(", ", leds) + ")");
            }
            if (alwaysLit.Count > 0)
                notes.Add("These LEDs have a color but an RPM of 0, so ATSR lights them all the time: gear " + string.Join("; ", alwaysLit) + ".");

            if (p.GearOrder.Count > 0)
            {
                var lastGear = p.GearOrder[p.GearOrder.Count - 1];
                var leds = p.LedRpm[lastGear].Skip(1).ToList();
                if (leds.Any(v => v != 0))
                {
                    var atsr = LedLayout.AtsrLayoutOf(leds);
                    var ours = LedLayout.Classify(p.LedRpm[lastGear]);
                    if (atsr == LedLayout.AtsrLayout.Rejected)
                        notes.Add("ATSR can't use this file: it reads the layout from the last gear (" + lastGear +
                                  "), whose LED values are both in increasing order and mirror-symmetric (e.g. one LED, or all at the same RPM). It would use its generic presets instead.");
                    else if (atsr == LedLayout.AtsrLayout.LeftToRight && (ours == LayoutKind.Falling || ours == LayoutKind.OutsideIn || ours == LayoutKind.InsideOut))
                        notes.Add("ATSR will show this layout as left to right: it reads the layout from the last gear (" + lastGear +
                                  ") and only recognises left to right, or mirrored rows whose two halves match exactly.");
                }
            }
            return notes;
        }
    }
}
