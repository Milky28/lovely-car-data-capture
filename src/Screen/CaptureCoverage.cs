using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LovelyCarDataCapture.Screen
{
    /// <summary>
    /// Keeps count, while the driver is still driving, of what the capture has enough of: how many
    /// times each gear has been revved from a dark strip to the whole strip.
    /// </summary>
    /// <remarks>
    /// The real judgement of a capture is made at export, and takes too long to run every second over
    /// a drive. This is the cheap version for the panel over the game: one small step per frame, from
    /// nothing but the gear, the engine speed and how many lights were lit.
    /// <para>
    /// A sweep is a climb in one gear that starts with the strip dark and takes the revs up. It is a
    /// full sweep if it reached the whole strip, and the whole strip is the most lights two sweeps have
    /// reached and held for a few frames (the most any has, while there are only one or two), so a
    /// highlight or an indicator read as a light in one sweep doesn't move the goal out of reach. The
    /// strip going dark at high revs doesn't end a sweep: that is the limiter blinking it. A sweep is
    /// counted once it is over, when the revs come back down or the gear changes, or once the whole
    /// strip has been held lit for a moment, so a gear isn't called covered the instant its last light
    /// first shows. Neutral counts as a gear: revving in the pits is a capture too.
    /// </para>
    /// <para>
    /// Dark means as few lights as the box ever shows, not none: a reflection or a pit light that is
    /// always in the box would otherwise keep the strip from ever looking dark.
    /// </para>
    /// </remarks>
    internal sealed class CaptureCoverage
    {
        /// <summary>
        /// Full sweeps of a gear that make it covered. One is what a good capture has had so far; the
        /// verify drive is the second look.
        /// </summary>
        public const int SweepsWanted = 1;

        /// <summary>A count of lit lights has to be seen in this many frames to be the box at its darkest.</summary>
        private const int FramesForDarkest = 10;

        /// <summary>A climb has to raise the revs this far from where the strip was last dark.</summary>
        private const int FewestRpmClimbed = 300;
        /// <summary>The strip dark this far under the sweep's highest revs means the revs came back down.</summary>
        private const int RpmBackDown = 200;
        private const int MostLights = 64;
        /// <summary>
        /// Frames a count of lights must be seen in to be a sweep's peak. A highlight that passes
        /// through the box for a frame or two then doesn't add a light to the strip.
        /// </summary>
        private const int FramesAtPeak = 5;
        /// <summary>Frames a climb still under way must have spent at its peak to count already.</summary>
        private const int FramesHeld = 15;

        private sealed class Sweep { public string Gear; public int Peak; }

        private readonly List<Sweep> _sweeps = new List<Sweep>();
        private readonly List<string> _gears = new List<string>();
        private string _gear;
        private bool _darkSeen, _climbing;
        private int _darkRpm, _topRpm;
        private readonly int[] _framesAt = new int[MostLights + 1];
        private readonly int[] _framesEver = new int[MostLights + 1];
        private int _darkest = int.MaxValue;

        // Frames arrive on the screen thread while the panel and the telemetry thread read the counts.
        private readonly object _gate = new object();

        public void Record(string gear, int rpm, int lit)
        {
            lock (_gate) Step(gear, rpm, lit);
        }

        private void Step(string gear, int rpm, int lit)
        {
            if (gear != _gear)
            {
                Close();
                _gear = gear;
                _darkSeen = false;
                if (Counted(gear) && !_gears.Contains(gear))
                {
                    _gears.Add(gear);
                    _gears.Sort((a, b) => Number(a).CompareTo(Number(b)));
                }
            }
            if (!Counted(gear)) return;
            lit = Math.Min(lit, MostLights);

            if (++_framesEver[lit] == FramesForDarkest && lit < _darkest) _darkest = lit;

            if (lit <= _darkest)
            {
                if (_climbing)
                {
                    if (rpm >= _topRpm - RpmBackDown) return;   // blinking at the limiter
                    Close();
                }
                _darkRpm = _darkSeen ? Math.Min(_darkRpm, rpm) : rpm;
                _darkSeen = true;
                return;
            }
            if (!_climbing)
            {
                // Lights already on when the gear went in say nothing about where they come on.
                if (!_darkSeen) return;
                _climbing = true;
                _topRpm = rpm;
                Array.Clear(_framesAt, 0, _framesAt.Length);
            }
            _framesAt[lit]++;
            if (rpm > _topRpm) _topRpm = rpm;
        }

        private void Close()
        {
            if (!_climbing) return;
            _climbing = false;
            _darkSeen = false;
            int peak = MostLights;
            while (peak > 0 && _framesAt[peak] < FramesAtPeak) peak--;
            if (peak > 0 && _topRpm - _darkRpm >= FewestRpmClimbed) _sweeps.Add(new Sweep { Gear = _gear, Peak = peak });
        }

        private IEnumerable<Sweep> SweepsSoFar()
        {
            foreach (var sweep in _sweeps) yield return sweep;
            if (!_climbing || _topRpm - _darkRpm < FewestRpmClimbed) yield break;
            int peak = MostLights;
            while (peak > 0 && _framesAt[peak] < FramesAtPeak) peak--;
            if (peak > 0 && _framesAt[peak] >= FramesHeld) yield return new Sweep { Gear = _gear, Peak = peak };
        }

        /// <summary>How many lights the whole strip has, as far as the sweeps so far show. Zero before any.</summary>
        public int WholeStrip { get { lock (_gate) return Whole(); } }

        private int Whole()
        {
            {
                // Sweeps made driving set the goal when there are any. Sitting in the pits in neutral
                // the box can hold a pit-limiter or warning light the strip never shows on track.
                var sweeps = SweepsSoFar().ToList();
                if (sweeps.Any(s => s.Gear != "N")) sweeps.RemoveAll(s => s.Gear == "N");
                var peaks = sweeps.Select(s => s.Peak).OrderByDescending(p => p).ToList();
                if (peaks.Count == 0) return 0;
                // What the export takes out of the box (a logo, an indicator) is still counted here,
                // so one sweep can show more "lights" than the strip has. Once there are enough
                // sweeps to compare, the goal is what two of them reached.
                return peaks.Count >= 3 ? peaks[1] : peaks[0];
            }
        }

        /// <summary>
        /// The gears to report, lowest first: the forward gears driven, and neutral when it was swept
        /// or is all there is. Every capture sits in neutral for a while; that isn't a gear left undone.
        /// </summary>
        public IReadOnlyList<string> Gears { get { lock (_gate) return Reported(); } }

        private List<string> Reported() =>
            _gears.Where(g => g != "N" || _gears.Count == 1 || Full(g) >= SweepsWanted).ToList();

        public int FullSweeps(string gear) { lock (_gate) return Full(gear); }

        private int Full(string gear)
        {
            int whole = Whole();
            return whole == 0 ? 0 : SweepsSoFar().Count(s => s.Gear == gear && s.Peak >= whole);
        }

        public bool Covered(string gear) => FullSweeps(gear) >= SweepsWanted;

        /// <summary>Every gear driven so far is covered. Says nothing of gears not driven yet.</summary>
        public bool AllDrivenCovered
        {
            get { lock (_gate) return _gears.Count > 0 && Reported().All(g => Full(g) >= SweepsWanted); }
        }

        /// <summary>For the panel: "full sweeps: 1 ✓  2 ✓  3 –".</summary>
        public string Describe()
        {
            lock (_gate)
            {
                if (_gears.Count == 0) return "";
                if (Whole() == 0) return "no full sweep yet";
                return "full sweeps: " + string.Join("  ", Reported().Select(g => g + (Full(g) >= SweepsWanted ? " ✓" : " –")));
            }
        }

        private static bool Counted(string gear) => Number(gear) >= 0;

        /// <summary>Neutral is 0, reverse and anything unreadable are below it.</summary>
        private static int Number(string gear) =>
            gear == "N" ? 0 : int.TryParse(gear, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : -1;
    }
}
