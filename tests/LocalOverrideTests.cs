using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LovelyCarDataCapture.Capture;
using LovelyCarDataCapture.Profile;
using Newtonsoft.Json.Linq;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static void LocalOverridesSurviveRecapture()
        {
            foreach (bool lmu in new[] { true, false })
            {
                var game = lmu ? "LMU" : "AssettoCorsaCompetizione";
                var car = lmu ? "Lamborghini Iron Lynx 2024" : "mclaren_720s_gt3_evo";
                var session = new CaptureSession(game, car);
                foreach (var f in LoadFrames(DataPath(lmu ? "lmu-lamborghini-sc63.frames.csv" : "acc-mclaren-720s-gt3-evo.frames.csv")))
                    session.Screen.Record(f.Gear, f.Rpm, f.TimeMs, f.Blobs);
                var baseline = lmu ? ProfileComposer.Compose(session, new CaptureSettings(), null, When).Profile
                                   : CarProfile.Parse(AccMcLarenJson());
                if (lmu)
                {
                    for (int i = 6; i <= 8; i++) baseline.LedColor[i] = "#FFFF0000";
                    baseline.LedRpm["3"][1] -= 100;
                }
                var lookup = Found(baseline.ToJson(), lmu ? "lmu/lamborghini-iron-lynx-2024.json" : "assettocorsacompetizione/mclaren-720s-gt3-evo.json");
                var plain = ProfileComposer.Compose(session, new CaptureSettings(), lookup, When).Profile;
                var json = File.ReadAllText(DataPath(lmu ? "lmu-sc63.overrides.json" : "acc-mclaren.overrides.json"));
                for (int capture = 0; capture < 2; capture++)
                {
                    var result = ProfileComposer.Compose(session, new CaptureSettings(), lookup, When, json);
                    var p = result.Profile;
                    foreach (var gear in plain.GearOrder)
                        Check(plain.LedRpm[gear].SequenceEqual(p.LedRpm[gear]), "overrides leave newly measured RPMs intact");
                    if (lmu)
                    {
                        for (int i = 0; i <= p.LedNumber; i++)
                            Equal(i >= 6 && i <= 8 ? "#FFFFFF00" : plain.LedColor[i], p.LedColor[i], "only confirmed LED colors change");
                        Check(p.LedRpm["3"][1] != baseline.LedRpm["3"][1], "new capture updates the repo threshold");
                        Equal(plain.RedlineBlinkInterval, p.RedlineBlinkInterval, "color override does not change blink");
                    }
                    else
                    {
                        Equal(200, p.RedlineBlinkInterval, "confirmed ACC interval wins over repo");
                        Check(p.LedColor.SequenceEqual(plain.LedColor), "blink override does not change colors");
                    }
                    Check(result.Report.Any(line => line.Contains("Applied confirmed local overrides")), "report identifies applied overrides");
                }
            }
        }

        private static void LocalOverridesRejectWrongOrInvalidFiles()
        {
            var valid = JObject.Parse(File.ReadAllText(DataPath("acc-mclaren.overrides.json")));
            var variants = new List<JObject>();
            void Bad(string key, JToken value)
            {
                var copy = (JObject)valid.DeepClone();
                copy[key] = value;
                variants.Add(copy);
            }
            Bad("game", "automobilista2");
            Bad("carId", "another-car");
            Bad("ledNumber", 10);
            Bad("ledColor", new JObject { ["13"] = "#FFFFFF00" });
            Bad("ledColor", new JObject { ["6"] = "yellow" });
            Bad("ledColor", new JArray("#FFFFFF00"));
            Bad("redlineBlinkInterval", -1);
            Bad("redlineBlinkInterval", 200.5);
            Bad("ledRpm", new JObject());
            foreach (var json in variants.Select(v => v.ToString()).Concat(new[] { "{broken" }))
            {
                var profile = CarProfile.Parse(AccMcLarenJson());
                var original = profile.ToJson();
                var notes = new List<string>();
                bool rejected = false;
                try { LocalProfileOverrides.Apply(profile, "AssettoCorsaCompetizione", profile.CarId, json, notes); }
                catch (Exception) { rejected = true; }
                Check(rejected, "invalid overrides rejected before export");
                Equal(original, profile.ToJson(), "rejection makes no partial changes");
                Equal(0, notes.Count, "rejection does not claim success");
            }
            var unchanged = CarProfile.Parse(AccMcLarenJson());
            var before = unchanged.ToJson();
            LocalProfileOverrides.Apply(unchanged, "AssettoCorsaCompetizione", unchanged.CarId, null, new List<string>());
            Equal(before, unchanged.ToJson(), "missing override keeps existing behavior");
            valid["redlineBlinkInterval"] = 0;
            LocalProfileOverrides.Apply(unchanged, "AssettoCorsaCompetizione", unchanged.CarId, valid.ToString(), new List<string>());
            Equal(0, unchanged.RedlineBlinkInterval, "explicit zero disables blink");
        }
    }
}
