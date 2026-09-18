using System;
using System.Collections.Generic;
using System.Linq;

namespace LovelyCarDataCapture.Capture
{
    /// <summary>
    /// Learns the RPM at which each LED of a strip switches on, per gear, from samples of which LEDs
    /// are lit. Used for the F1 games' 15 rev-light bits and for lights read off the screen.
    /// </summary>
    /// <remarks>
    /// Only climbs are used: runs of samples where RPM doesn't fall. Within a climb, an LED seen dark
    /// raises its "highest dark" RPM, and the first sample it's lit after being dark in that same climb
    /// lowers its "switch-on" RPM. Requiring the dark sample first means a light still on from before a
    /// small dip (switch-off hysteresis) never counts. The true threshold lies between the two values.
    /// Frames where the whole strip goes dark above the last LED's threshold are the redline flash,
    /// not a threshold, and mark where the flash starts.
    /// </remarks>
    internal sealed class LedWindowCapture
    {
        /// <summary>Rev lights the F1 games report, whatever the car's own dash shows.</summary>
        public const int F1LedCount = 15;

        private readonly int _ledCount;

        public LedWindowCapture(int ledCount)
        {
            if (ledCount < 1) throw new ArgumentOutOfRangeException(nameof(ledCount));
            _ledCount = ledCount;
        }

        public int LedCount => _ledCount;

        private sealed class GearState
        {
            public GearState(int leds)
            {
                LowestOn = Enumerable.Repeat(int.MaxValue, leds).ToArray();
                HighestOff = Enumerable.Repeat(-1, leds).ToArray();
                DarkInClimb = new bool[leds];
                OffInClimb = Enumerable.Repeat(-1, leds).ToArray();
                ClimbMidpoints = Enumerable.Range(0, leds).Select(_ => new List<double>()).ToArray();
            }

            public readonly int[] LowestOn;
            public readonly int[] HighestOff;
            /// <summary>Seen dark earlier in the current climb.</summary>
            public readonly bool[] DarkInClimb;
            /// <summary>One switch-on window per climb, per LED: the midpoints are what the value is taken from.</summary>
            public readonly List<double>[] ClimbMidpoints;
            /// <summary>Highest RPM the LED has been seen dark at in the climb under way.</summary>
            public readonly int[] OffInClimb;
            public int PreviousRpm = -1;
            public int FlashStart = int.MaxValue;
            public int Samples;
        }

        private readonly Dictionary<string, GearState> _gears = new Dictionary<string, GearState>();

        public bool HasData => _gears.Values.Any(g => g.LowestOn.Any(v => v != int.MaxValue));

        /// <summary>Records a sample from a bit field, bit 0 = leftmost LED (the F1 games' format).</summary>
        public void Record(string gear, int rpm, int bits)
        {
            var lit = new bool[_ledCount];
            for (int i = 0; i < _ledCount; i++) lit[i] = (bits & (1 << i)) != 0;
            Record(gear, rpm, lit);
        }

        /// <summary>Records a sample from a lit/dark flag per LED, index 0 = leftmost.</summary>
        public void Record(string gear, int rpm, bool[] lit)
        {
            if (string.IsNullOrEmpty(gear) || rpm <= 0 || lit == null) return;
            if (!_gears.TryGetValue(gear, out var g))
            {
                g = new GearState(_ledCount);
                _gears[gear] = g;
            }

            bool anyLit = false;
            for (int i = 0; i < _ledCount && i < lit.Length; i++) if (lit[i]) { anyLit = true; break; }

            if (!anyLit && rpm >= FullStripRpm(g))
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
                for (int i = 0; i < _ledCount; i++)
                {
                    g.DarkInClimb[i] = !(i < lit.Length && lit[i]);
                    g.OffInClimb[i] = -1;
                }
                return;
            }

            g.Samples++;
            for (int i = 0; i < _ledCount; i++)
            {
                if (!(i < lit.Length && lit[i]))
                {
                    g.DarkInClimb[i] = true;
                    g.OffInClimb[i] = rpm;
                    if (rpm > g.HighestOff[i]) g.HighestOff[i] = rpm;
                }
                else if (g.DarkInClimb[i])
                {
                    if (rpm < g.LowestOn[i]) g.LowestOn[i] = rpm;
                    if (g.OffInClimb[i] >= 0) g.ClimbMidpoints[i].Add((g.OffInClimb[i] + rpm) / 2.0);
                    g.DarkInClimb[i] = false;
                }
            }
        }

        // RPM at which every LED has been seen lit, or int.MaxValue until then. Loop instead of LINQ: hot path.
        private int FullStripRpm(GearState g)
        {
            int max = 0;
            for (int i = 0; i < _ledCount; i++)
            {
                if (g.LowestOn[i] == int.MaxValue) return int.MaxValue;
                if (g.LowestOn[i] > max) max = g.LowestOn[i];
            }
            return max;
        }

        public IEnumerable<string> Gears => _gears.Keys;

        public GearLedResult Result(string gear)
        {
            if (!_gears.TryGetValue(gear, out var g)) return null;
            var leds = new LedThreshold[_ledCount];
            for (int i = 0; i < _ledCount; i++)
            {
                int on = g.LowestOn[i], off = g.HighestOff[i];
                if (on == int.MaxValue) { leds[i] = null; continue; }
                var climbs = g.ClimbMidpoints[i];
                if (climbs.Count > 0)
                {
                    // The middle climb rather than the widest window: one bad frame (a light bloomed
                    // into its neighbour, a missed RPM reading) then moves the value by nothing.
                    leds[i] = new LedThreshold(RoundTo(Median(climbs), 5), off >= 0 ? off : (int?)null, on)
                    {
                        Inconsistent = off >= 0 && off >= on && climbs.Count < 2,
                        Climbs = climbs.Count,
                        ClimbSpread = (int)Math.Round(climbs.Max() - climbs.Min()),
                    };
                }
                else if (off >= 0 && off < on)
                    leds[i] = new LedThreshold(RoundTo((on + off) / 2.0, 5), off, on);
                else
                    leds[i] = new LedThreshold(on, off >= 0 ? off : (int?)null, on) { Inconsistent = off >= on };
            }
            return new GearLedResult(gear, leds, g.FlashStart == int.MaxValue ? (int?)null : RoundTo(g.FlashStart, 5), g.Samples);
        }

        private static int RoundTo(double value, int step) => (int)(Math.Round(value / step) * step);

        private static double Median(List<double> values)
        {
            var v = values.OrderBy(x => x).ToList();
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0;
        }
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
        /// <summary>Climbs the value was measured on; more than one makes it a median rather than a single window.</summary>
        public int Climbs { get; set; }
        /// <summary>How far apart those climbs put the value, in RPM.</summary>
        public int ClimbSpread { get; set; }
        /// <summary>Where the light was seen switching off as the revs fell, when that was used; null otherwise.</summary>
        public int? FallRpm { get; private set; }

        /// <summary>
        /// The same measurement, its value moved to the middle of switching on and switching off. The
        /// rest is kept, so the checks on how tightly it was measured still apply.
        /// </summary>
        public LedThreshold WithFall(int rpm, int fallRpm) => new LedThreshold(rpm, HighestOff, LowestOn)
        {
            Inconsistent = Inconsistent,
            Climbs = Climbs,
            ClimbSpread = ClimbSpread,
            FallRpm = fallRpm,
        };

        /// <summary>RPM taken off for display lag measured on the strip's other lights, when this one had no switch-off of its own.</summary>
        public int LagTaken { get; private set; }

        public LedThreshold WithLagTaken(int rpm, int taken) => new LedThreshold(rpm, HighestOff, LowestOn)
        {
            Inconsistent = Inconsistent,
            Climbs = Climbs,
            ClimbSpread = ClimbSpread,
            LagTaken = taken,
        };
    }

    internal sealed class GearLedResult
    {
        public GearLedResult(string gear, LedThreshold[] leds, int? flashStart, int samples)
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
