using System;
using System.Linq;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static CaptureCoverage CoverageOf(string file)
        {
            var coverage = new CaptureCoverage();
            foreach (var f in LoadFrames(DataPath(file))) coverage.Record(f.Gear, f.Rpm, f.Blobs.Count);
            return coverage;
        }

        /// <summary>Thirty frames a second: idle, a climb through ten lights to the limiter, a hold there, and back down.</summary>
        private static void Sweep(CaptureCoverage coverage, string gear, int lights = 10, int always = 0, bool blink = true, int reach = 8000)
        {
            for (int f = 0; f < 30; f++) coverage.Record(gear, 2000, always);
            for (int rpm = 2000; rpm <= reach; rpm += 50)
                coverage.Record(gear, rpm, always + Math.Max(0, Math.Min(lights, (rpm - 5000) / 250 + 1)));
            if (reach >= 8000)
                for (int f = 0; f < 30; f++) coverage.Record(gear, 8000 - f % 3 * 20, blink && f % 4 < 2 ? always : always + lights);
            for (int rpm = reach; rpm >= 2000; rpm -= 200)
                coverage.Record(gear, rpm, always + Math.Max(0, Math.Min(lights, (rpm - 5000) / 250 + 1)));
        }

        private static void CoverageCountsFullSweepsByGear()
        {
            var coverage = new CaptureCoverage();
            Equal("", coverage.Describe(), "nothing driven");
            Sweep(coverage, "1");
            Check(coverage.Covered("1"), "one full sweep covers a gear");
            Equal(10, coverage.WholeStrip, "whole strip");
            // The limiter blinking the strip dark and lit is one sweep, not a dozen.
            Equal(1, coverage.FullSweeps("1"), "sweeps counted through the limiter's blink");

            // Second gear is short-shifted at seven lights: driven, not covered.
            Sweep(coverage, "2", reach: 6500);
            Check(!coverage.Covered("2"), "a sweep that stops short of the whole strip doesn't cover the gear");
            Check(!coverage.AllDrivenCovered, "so not every gear is covered");
            Equal("full sweeps: 1 ✓  2 –", coverage.Describe(), "the panel's line");
            Sweep(coverage, "2");
            Check(coverage.AllDrivenCovered, "until it is taken to the limiter");

            // Reverse is never asked for, and sitting in neutral isn't a gear left undone.
            for (int f = 0; f < 100; f++) coverage.Record(f < 50 ? "R" : "N", 1500, 0);
            Equal("1 2", string.Join(" ", coverage.Gears), "gears reported");
            Check(coverage.AllDrivenCovered, "neutral and reverse don't undo it");
        }

        private static void CoverageHoldsWithSomethingAlwaysLitInTheBox()
        {
            // A reflection that never goes out: the strip is at its darkest with one thing lit.
            var coverage = new CaptureCoverage();
            Sweep(coverage, "3", always: 1);
            Sweep(coverage, "3", always: 1);
            Check(coverage.Covered("3"), "a strip that never reads fully dark is still swept");
            Equal(11, coverage.WholeStrip, "whole strip as the box shows it");
        }

        private static void CoverageAgreesWithSavedDrives()
        {
            // Revved in the pits, then three gears each taken to the limiter once.
            var mclaren = CoverageOf("acc-mclaren-720s-gt3-evo.frames.csv");
            Equal(10, mclaren.WholeStrip, "McLaren whole strip");
            Check(mclaren.AllDrivenCovered, "McLaren: " + mclaren.Describe());
            Equal("N 1 2 3", string.Join(" ", mclaren.Gears), "McLaren gears, with the neutral sweep made in the pits");

            // Revved in the pits in neutral, then first gear only to pull away.
            var viper = CoverageOf("pmr-srt-viper-gts-r.frames.csv");
            Check(viper.Covered("N") && !viper.Covered("1"), "Viper: " + viper.Describe());

            // Neutral shows more in the box than the strip does on track; the track sets the goal.
            var sc63 = CoverageOf("lmu-lamborghini-sc63.frames.csv");
            Equal(10, sc63.WholeStrip, "SC63 whole strip");
            Check(sc63.AllDrivenCovered, "SC63: " + sc63.Describe());
        }
    }
}
