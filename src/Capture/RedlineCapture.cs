using System;
using System.Collections.Generic;
using System.Linq;

namespace LovelyCarDataCapture.Capture
{
    internal sealed class GearRedline
    {
        public double PeakRpm;
        public double RedlineRpm;
    }

    /// <summary>
    /// Game-independent capture from SimHub's normalised data: gears driven, peak RPM, and the
    /// redline SimHub has for the car. It knows nothing about the game's LEDs.
    /// </summary>
    internal sealed class RedlineCapture
    {
        public int MaxGears { get; private set; }
        public double CarRedlineRpm { get; private set; }
        public double CarMaxRpm { get; private set; }
        public Dictionary<string, GearRedline> Gears { get; } = new Dictionary<string, GearRedline>();

        // Runs on SimHub's telemetry hot path: no allocations beyond first sight of a gear, no throwing.
        public void Record(string gear, double rpm, double gearRedlineRpm, double carRedlineRpm, double maxRpm, int maxGears)
        {
            if (maxGears > MaxGears) MaxGears = maxGears;
            if (carRedlineRpm > 0) CarRedlineRpm = carRedlineRpm;
            if (maxRpm > CarMaxRpm) CarMaxRpm = maxRpm;

            if (string.IsNullOrEmpty(gear)) return;
            if (!Gears.TryGetValue(gear, out var g))
            {
                g = new GearRedline();
                Gears[gear] = g;
            }
            if (rpm > g.PeakRpm) g.PeakRpm = rpm;
            if (gearRedlineRpm > 0) g.RedlineRpm = gearRedlineRpm;
        }

        public int TopGear
        {
            get
            {
                int top = MaxGears;
                foreach (var key in Gears.Keys)
                {
                    if (int.TryParse(key, out var n) && n > top) top = n;
                }
                return top;
            }
        }

        public double ResolveRedline(string gear, out string source)
        {
            if (Gears.TryGetValue(gear, out var g) && g.RedlineRpm > 0) { source = "SimHub redline, sampled in gear"; return g.RedlineRpm; }
            if (CarRedlineRpm > 0) { source = "SimHub car redline, gear not driven"; return CarRedlineRpm; }
            if (CarMaxRpm > 0) { source = "max RPM fallback"; return CarMaxRpm; }

            var peak = Gears.Values.Select(x => x.PeakRpm).DefaultIfEmpty(0).Max();
            source = peak > 0 ? "highest observed RPM fallback" : "NO RPM DATA - fill in manually";
            return peak;
        }
    }
}
