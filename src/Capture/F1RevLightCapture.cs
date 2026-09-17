using System;
using System.Collections.Generic;
using System.Linq;

namespace LovelyCarDataCapture.Capture
{
    /// <summary>
    /// Learns the RPM at which each of the F1 games' 15 rev lights switches on, per gear, from the
    /// UDP rev-lights bit field (bit 0 = leftmost LED).
    /// </summary>
    /// <remarks>
    /// Only climbs are used: runs of samples where RPM doesn't fall. Within a climb, an LED seen dark
    /// raises its "highest dark" RPM, and the first sample it's lit after being dark in that same climb
    /// lowers its "switch-on" RPM. Requiring the dark sample first means a light still on from before a
    /// small dip (switch-off hysteresis) never counts. The true threshold lies between the two values.
    /// Frames where the whole strip goes dark above the last LED's threshold are the redline flash,
    /// not a threshold, and mark where the flash starts.
    /// </remarks>
    internal sealed class F1RevLightCapture
    {
        public const int LedCount = 15;

        private sealed class GearState
        {
            public readonly int[] LowestOn = Enumerable.Repeat(int.MaxValue, LedCount).ToArray();
            public readonly int[] HighestOff = Enumerable.Repeat(-1, LedCount).ToArray();
            /// <summary>Seen dark earlier in the current climb.</summary>
            public readonly bool[] DarkInClimb = new bool[LedCount];
            public int PreviousRpm = -1;
            public int FlashStart = int.MaxValue;
            public int Samples;
        }

        private readonly Dictionary<string, GearState> _gears = new Dictionary<string, GearState>();

        public bool HasData => _gears.Values.Any(g => g.LowestOn.Any(v => v != int.MaxValue));

        public void Record(string gear, int rpm, int bits)
        {
            if (string.IsNullOrEmpty(gear) || rpm <= 0) return;
            if (!_gears.TryGetValue(gear, out var g))
            {
                g = new GearState();
                _gears[gear] = g;
            }

            if (bits == 0 && rpm >= FullStripRpm(g))
            {
                // Whole strip dark where it should be fully lit: the redline flash.
                if (rpm < g.FlashStart) g.FlashStart = rpm;
                g.PreviousRpm = rpm;
                return;
            }

            bool climbing = g.PreviousRpm >= 0 && rpm >= g.PreviousRpm;
            g.PreviousRpm = rpm;
            if (!climbing)
            {
                // A new climb may start here: remember which LEDs are dark at its bottom.
                for (int i = 0; i < LedCount; i++) g.DarkInClimb[i] = (bits & (1 << i)) == 0;
                return;
            }

            g.Samples++;
            for (int i = 0; i < LedCount; i++)
            {
                if ((bits & (1 << i)) == 0)
                {
                    g.DarkInClimb[i] = true;
                    if (rpm > g.HighestOff[i]) g.HighestOff[i] = rpm;
                }
                else if (g.DarkInClimb[i])
                {
                    if (rpm < g.LowestOn[i]) g.LowestOn[i] = rpm;
                    g.DarkInClimb[i] = false;
                }
            }
        }

        // RPM at which every LED has been seen lit, or int.MaxValue until then. Loop instead of LINQ: hot path.
        private static int FullStripRpm(GearState g)
        {
            int max = 0;
            for (int i = 0; i < LedCount; i++)
            {
                if (g.LowestOn[i] == int.MaxValue) return int.MaxValue;
                if (g.LowestOn[i] > max) max = g.LowestOn[i];
            }
            return max;
        }

        public IEnumerable<string> Gears => _gears.Keys;

        public F1GearResult Result(string gear)
        {
            if (!_gears.TryGetValue(gear, out var g)) return null;
            var leds = new LedThreshold[LedCount];
            for (int i = 0; i < LedCount; i++)
            {
                int on = g.LowestOn[i], off = g.HighestOff[i];
                if (on == int.MaxValue) { leds[i] = null; continue; }
                if (off >= 0 && off < on)
                    leds[i] = new LedThreshold(RoundTo((on + off) / 2.0, 5), off, on);
                else
                    leds[i] = new LedThreshold(on, off >= 0 ? off : (int?)null, on) { Inconsistent = off >= on };
            }
            return new F1GearResult(gear, leds, g.FlashStart == int.MaxValue ? (int?)null : RoundTo(g.FlashStart, 5), g.Samples);
        }

        private static int RoundTo(double value, int step) => (int)(Math.Round(value / step) * step);
    }

    internal sealed class LedThreshold
    {
        public LedThreshold(int rpm, int? highestOff, int lowestOn)
        {
            Rpm = rpm;
            HighestOff = highestOff;
            LowestOn = lowestOn;
        }

        public int Rpm { get; }
        /// <summary>Highest RPM seen with the LED dark while revving up, if any.</summary>
        public int? HighestOff { get; }
        /// <summary>Lowest RPM seen with the LED lit while revving up.</summary>
        public int LowestOn { get; }
        /// <summary>Width of the window the true threshold lies in; null when no dark sample below it was seen.</summary>
        public int? Uncertainty => HighestOff.HasValue && HighestOff < LowestOn ? LowestOn - HighestOff : null;
        /// <summary>The LED was seen dark above an RPM where it was also seen lit.</summary>
        public bool Inconsistent { get; set; }
    }

    internal sealed class F1GearResult
    {
        public F1GearResult(string gear, LedThreshold[] leds, int? flashStart, int samples)
        {
            Gear = gear;
            Leds = leds;
            FlashStart = flashStart;
            Samples = samples;
        }

        public string Gear { get; }
        /// <summary>Index 0 = leftmost LED; null where the LED was never seen lit.</summary>
        public LedThreshold[] Leds { get; }
        public int? FlashStart { get; }
        public int Samples { get; }
        public int CapturedCount => Leds.Count(l => l != null);
        public bool Complete => CapturedCount == Leds.Length;
    }
}
