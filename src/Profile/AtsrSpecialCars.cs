using System.Collections.Generic;
using System.Linq;

namespace LovelyCarDataCapture.Profile
{
    /// <summary>
    /// Cars ATSR handles with built-in behaviour instead of (or on top of) the car file, as of ATSR Hub EVO
    /// (August 2026). ATSR matches the carId exactly and ignores the game, so a car in another sim with
    /// the same carId gets the same treatment (e.g. AMS2's "BMW M Hybrid V8").
    /// </summary>
    internal static class AtsrSpecialCars
    {
        /// <summary>Drawn with ATSR's own BMW LMDh light pattern: only the file's RPM values and redline are used.</summary>
        public static readonly HashSet<string> BmwLmdh = new HashSet<string>
        {
            "bmwlmdh", "BMW M Hybrid V8", "BMW M Team WRT 2024", "Hyper_BMW M Team WRT 2025", "Hyper_BMW M Team WRT 2026_15",
            "Hyper_BMW M Team WRT 2026_20", "Hyper_BMWMH Custom Team 2026_397", "Hyper_BMWMH Custom Team 2025_397",
            "Hyper_BMWMH Custom Team 2025", "Hyper_BMWMH Custom Team 2024",
        };

        /// <summary>Cars with a built-in second redline stage, by carId, with a short description of it.</summary>
        private static readonly Dictionary<string, string> SecondStage = new Dictionary<string, string>
        {
            ["Chevrolet Corvette Z06 GT3.R"] = "at 7900 rpm in #FF6495ED",
            ["amvantagegt4"] = "at 6900 rpm in #FFFF0000",
            ["amvantageevogt3"] = "Vantage GT3 style, 6750-6900 rpm by gear, in #FF0000FF",
            ["bmwlmdh"] = "at 7900 rpm in #FF0000FF",
            ["bmwm2g87"] = "Ferrari GT3 style at 7000 rpm in #FFFF0000",
            ["chevyvettez06rgt3"] = "at 7750 rpm in #FF0000FF",
            ["ferrari296gt3"] = "Ferrari GT3 style, 7450-8000 rpm by gear, in #FFFF0000",
            ["ks_ferrari_488_gt3"] = "Ferrari GT3 style at 6900 rpm in #FFFF0000",
            ["fordmustanggt4"] = "7500 rpm (10000 in R and N) in #FFFF0000",
            ["mclaren720sgt3"] = "McLaren 720S style, 6975-8000 rpm by gear, in #FF0000FF",
        };

        private static readonly (string description, string[] ids)[] LmuGroups =
        {
            ("LMU Aston Martin GT3", new[]
            {
                "Aston Martin Vantage AMR LMGT", "GT3_Heart of Racing Team 2024", "GT3_D'Station Racing 2024", "GT3_AMR GT3 Custom Team",
                "GT3_Heart of Racing Team 2025", "GT3_Racing Spirit of Léman 2025", "GT3_Heart of Racing Team 2026_23", "GT3_Heart of Racing Team 2026_27",
                "GT3_AMR GT3 Custom Team 2025", "GT3_AMR GT3 Custom Team 2025_397", "GT3_AMR GT3 Custom Team 2026_397",
            }),
            ("LMU McLaren GT3 (McLaren 720S style)", new[]
            {
                "McLaren 720S LMGT3 Evo", "GT3_United Autosports 2024", "Inception Racing 2024", "GT3_McLaren Custom Team 2024", "GT3_United Autosports 2025",
                "GT3_United Autosports 2026", "GT3_Garage 59 2026_10", "GT3_Garage 59 2026_58", "GT3_McLaren Custom Team 2025", "GT3_McLaren Custom Team 2026_397",
                "GT3_Logitech G Challenge_1", "GT3_Logitech G Challenge_2", "GT3_Logitech G Challenge_3", "GT3_Logitech G Challenge_4",
                "GT3_Logitech G Challenge_5", "GT3_Logitech G Challenge_6", "GT3_Logitech G Challenge_7", "GT3_Logitech G Challenge_8",
                "GT3_Logitech G Challenge_9", "GT3_Logitech G Challenge_10", "GT3_Logitech G Challenge_11", "GT3_Logitech G Challenge_12",
            }),
            ("LMU Cadillac hypercar", new[]
            {
                "Cadillac V-Series.R", "Hyper_Cadillac Hertz Team Jota 2025", "Hyper_Cadillac Whelen 2025", "Hyper_Cadillac WTR 2025",
                "Hyper_Cadillac Hertz Team Jota 2026_12", "Hyper_Cadillac Hertz Team Jota 2026_38", "Hyper_VLMDH Custom Team 2025", "Hyper_VLMDH Custom Team 2026_397",
            }),
            ("LMU Ford GT3", new[]
            {
                "Ford Mustang LMGT3", "GT3_Proton Racing 2024", "GT3_Mustang Custom Team 2024", "GT3_Proton Competition 2025",
                "GT3_Proton Competition 2026_77", "GT3_Proton Competition 2026_88", "GT3_Mustang Custom Team 2025", "GT3_Mustang Custom Team 2026_397",
            }),
        };

        /// <summary>Notes describing any built-in ATSR behaviour for this carId; empty when there is none.</summary>
        public static List<string> Describe(string carId)
        {
            var notes = new List<string>();
            if (string.IsNullOrEmpty(carId)) return notes;

            if (BmwLmdh.Contains(carId))
                notes.Add("ATSR draws carId '" + carId + "' with its own built-in BMW LMDh light pattern, in every game with this carId. It only uses this file's RPM values and redline; the colors and layout here are ignored.");

            if (SecondStage.TryGetValue(carId, out var stage))
                notes.Add("ATSR adds a built-in second redline stage for carId '" + carId + "' (" + stage + "), in every game with this carId. This file's redline is the first stage.");
            else
            {
                var group = LmuGroups.FirstOrDefault(g => g.ids.Contains(carId));
                if (group.ids != null)
                    notes.Add("ATSR adds a built-in second redline stage for carId '" + carId + "' (" + group.description + "), in every game with this carId. This file's redline is the first stage.");
            }
            return notes;
        }
    }
}
