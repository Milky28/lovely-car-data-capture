using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LovelyCarDataCapture.Capture;

namespace LovelyCarDataCapture.Screen
{
    /// <summary>What a screen capture learned about a car's rev lights.</summary>
    internal sealed class ScreenLedResult
    {
        public StripLayout Layout;
        public List<GearLedResult> Gears = new List<GearLedResult>();
        /// <summary>Colour measured for each slot below the redline; a black colour for gaps and unseen slots.</summary>
        public LedColor[] MeasuredColors;
        public List<LedPalette.ColorGroup> ColorGroups = new List<LedPalette.ColorGroup>();
        /// <summary>
        /// Slots never seen lit below the redline, so their own colour was never on screen: the last
        /// pair on a strip often lights exactly as the whole strip changes colour. Not the same as a gap.
        /// </summary>
        public bool[] ColorUnknown;
        public bool ColorsDoubtful;
        /// <summary>RPM where the strip switches to its redline colour, if it does.</summary>
        public int? RedlineRpm;
        public int? RedlineHighestBelow, RedlineLowestAbove;
        public string RedlineColor;
        /// <summary>What the strip actually measured above the redline, before it was matched to a colour.</summary>
        public LedColor RedlineMeasured;
        /// <summary>RPM where the strip changes colour a second time, for cars with a two-stage redline.</summary>
        public int? SecondStageRpm;
        public string SecondStageColor;
        public LedColor SecondStageMeasured;
        /// <summary>Length of one dark phase above the redline, in milliseconds; null when the lights don't blink.</summary>
        public int? BlinkIntervalMs;
        public bool BlinkSeen;
        /// <summary>RPM where the strip starts blinking, whether or not it also changes colour.</summary>
        public int? BlinkFromRpm;
        /// <summary>The redline was found from where the blinking starts, the strip keeping its own colours.</summary>
        public bool RedlineFromBlink;
        /// <summary>Redline measured in each gear on its own, for cars whose redline moves with the gear.</summary>
        public Dictionary<string, GearRedline> RedlineByGear = new Dictionary<string, GearRedline>();
        /// <summary>How far behind the revs the game draws its lights, in milliseconds; 0 when it isn't.</summary>
        public int DisplayLagMs;
        /// <summary>
        /// Set when the strip flips between its redline colour and its own colours at the limiter
        /// rather than blinking to dark: how long each own-colour phase lasts, in milliseconds.
        /// </summary>
        public int? FlashOwnMs;
        public int? FlashRedlineMs;
        public int FlashCount;
        public int Samples;
        public List<string> Notes = new List<string>();
    }

    /// <summary>Where one gear's strip turned to its redline colour.</summary>
    internal sealed class GearRedline
    {
        public int Rpm, HighestBelow, LowestAbove;
    }

    /// <summary>
    /// Reads a car's rev lights off the screen: which lights are lit at which RPM, what colour they
    /// are, where the strip turns to its redline colour and whether it blinks there.
    /// </summary>
    /// <remarks>
    /// Samples are kept and worked through at the end, because the shape of the strip is only known
    /// once the lights have all been seen lit. The RPM thresholds themselves come from
    /// <see cref="LedWindowCapture"/>, the same climb-window logic the F1 games' rev lights use.
    /// </remarks>
    internal sealed class ScreenLedCapture
    {
        /// <summary>About twenty minutes at 60 fps; capture is meant to be a few sweeps, not a race.</summary>
        public const int MaxSamples = 72000;

        private struct Sample
        {
            public string Gear;
            public int Rpm;
            public long TimeMs;
            public double[] X;
            public LedColor[] Colors;
        }

        private readonly List<Sample> _recorded = new List<Sample>();
        /// <summary>
        /// The frames <see cref="Result"/> is working on: a copy of the recording with anything that
        /// isn't a light taken out and each RPM moved back by the game's display lag.
        /// </summary>
        private List<Sample> _samples = new List<Sample>();
        private readonly StripCalibration _calibration = new StripCalibration();
        /// <summary>Per frame, whether the strip was in its redline state; null where it couldn't be told.</summary>
        private bool?[] _redline;
        /// <summary>Frames where an indicator was taken out, so a light looks dark that may really be lit.</summary>
        private bool[] _indicatorFrame = new bool[0];

        public bool HasData => _recorded.Count > 0;
        public int SampleCount => _recorded.Count;
        public int MostLightsSeen => _calibration.MostLightsInOneFrame;

        public void Record(string gear, int rpm, long timeMs, IReadOnlyList<LitBlob> blobs)
        {
            if (string.IsNullOrEmpty(gear) || rpm <= 0 || blobs == null) return;
            if (_recorded.Count >= MaxSamples) return;
            _calibration.Add((IReadOnlyCollection<LitBlob>)blobs);
            _recorded.Add(new Sample
            {
                Gear = gear,
                Rpm = rpm,
                TimeMs = timeMs,
                X = blobs.Select(b => b.CenterX).ToArray(),
                Colors = blobs.Select(b => b.Color).ToArray(),
            });
        }

        /// <summary>
        /// Writes every frame as it was recorded: time, gear, RPM and each lit light's position and
        /// colour. The same layout as the recordings in tests/data, so a capture from a real session
        /// can be replayed through changed code instead of being driven again.
        /// </summary>
        public void WriteFrames(TextWriter writer)
        {
            writer.WriteLine("frame,timeMs,gear,rpm,blobs");
            for (int i = 0; i < _recorded.Count; i++)
            {
                var s = _recorded[i];
                var blobs = string.Join(" ", s.X.Select((x, b) => ((int)Math.Round(x)).ToString(CultureInfo.InvariantCulture) + ":" +
                                                                  s.Colors[b].R + ":" + s.Colors[b].G + ":" + s.Colors[b].B));
                writer.WriteLine(string.Join(",", i.ToString(CultureInfo.InvariantCulture), s.TimeMs.ToString(CultureInfo.InvariantCulture),
                                             s.Gear, s.Rpm.ToString(CultureInfo.InvariantCulture), blobs));
            }
        }

        public ScreenLedResult Result()
        {
            _samples = new List<Sample>(_recorded);
            var result = new ScreenLedResult { Samples = _samples.Count };
            if (_samples.Count == 0)
            {
                result.Notes.Add("No frames were captured.");
                return result;
            }

            // The strip is worked out again once anything that isn't a light has been taken out.
            var calibration = _calibration;
            // Reflections first: they're recognised by being lit at idle, which the next step clears.
            bool fixtures = DropFixtures(result);
            if (DropIdleAnimation(result) | fixtures)
            {
                calibration = new StripCalibration();
                foreach (var s in _samples)
                    calibration.Add(s.X.Select(x => new LitBlob { Left = (int)Math.Floor(x), Right = (int)Math.Ceiling(x) }).ToList());
            }
            result.Layout = calibration.Build(out string problem);
            if (result.Layout == null)
            {
                result.Notes.Add("The strip couldn't be made out: " + problem + ".");
                return result;
            }
            var layout = result.Layout;

            DropIndicators(layout, result);

            var lit = new bool[_samples.Count][];
            for (int i = 0; i < _samples.Count; i++)
            {
                lit[i] = new bool[layout.LedNumber];
                for (int b = 0; b < _samples[i].X.Length; b++)
                {
                    int slot = layout.SlotOf(_samples[i].X[b]);
                    if (slot >= 0) lit[i][slot] = true;
                }
            }

            // Colour of every slot in every frame, so the redline and the strip's own colours can be
            // told apart: -1 where that slot wasn't lit in that frame.
            var hues = new double[_samples.Count][];
            for (int i = 0; i < _samples.Count; i++)
            {
                hues[i] = Enumerable.Repeat(-1.0, layout.LedNumber).ToArray();
                for (int b = 0; b < _samples[i].X.Length; b++)
                {
                    int slot = layout.SlotOf(_samples[i].X[b]);
                    if (slot >= 0) hues[i][slot] = _samples[i].Colors[b].Hue;
                }
            }

            // Blinks first: a blink's dark phase looks exactly like every light switching off, and its
            // next lit phase like every light switching on, so left in they swamp the real thresholds -
            // holding the limiter, as a capture should, gives dozens of them.
            var blink = FindBlinks(lit, layout, new ScreenLedResult());

            // Then the lag, so everything after reads each frame against the revs it was showing. The
            // blinks are found again on the moved RPMs: where the blinking starts is one of them.
            RemoveDisplayLag(lit, blink, result);
            var firstLook = new ScreenLedResult();
            blink = FindBlinks(lit, layout, firstLook);
            result.BlinkSeen = firstLook.BlinkSeen;

            FindRedline(lit, hues, result);
            // Found once more with the redline known: PMR's C8.R comes back from each dark phase with only
            // some of its lights, in its blue redline colour, which isn't a full strip but is the redline.
            blink = FindBlinks(lit, layout, result, _redline);
            // A frame an indicator was taken out of can't say whether that light was lit, so it doesn't
            // count towards any threshold: kept in, the light seems to go dark and come straight back on.
            for (int i = 0; i < blink.Length && i < _indicatorFrame.Length; i++)
                if (_indicatorFrame[i]) blink[i] = true;
            // Just after a shift the screen still shows the old gear for as long as the game lags - about
            // 90 ms in PMR - while telemetry already has the new gear and its lower revs: every upshift
            // would read as the lights coming on far too low. Those frames count for nothing.
            // Once the strip is in its redline state, what it does until it's back in its own colours is
            // the redline's display, not lights switching on: PMR's C8.R sweeps its blue in 2, 4, 6, 8
            // lights and back, and each step read as LED 1 lighting again. Arriving there still counts -
            // on some cars the last lights come on exactly then.
            if (_redline != null)
                for (int i = 1; i < blink.Length && i < _redline.Length; i++)
                    if (_redline[i] == true && _redline[i - 1] != false) blink[i] = true;
            long settle = result.DisplayLagMs + ShiftSettleMs;
            long gearStart = 0;
            for (int i = 0; i < blink.Length; i++)
            {
                if (i == 0 || _samples[i].Gear != _samples[i - 1].Gear) gearStart = _samples[i].TimeMs;
                if (i > 0 && _samples[i].TimeMs - gearStart < settle) blink[i] = true;
            }
            if (!result.RedlineRpm.HasValue && result.BlinkFromRpm.HasValue)
            {
                // The strip blinks without changing colour: where the blinking starts is the redline.
                result.RedlineRpm = result.BlinkFromRpm;
                result.RedlineHighestBelow = result.BlinkFromRpm;
                result.RedlineLowestAbove = result.BlinkFromRpm;
                result.RedlineFromBlink = true;
                result.Notes.Add("The strip blinks from about " + result.BlinkFromRpm + " rpm and keeps its own colours while it does, " +
                                 "so the redline was taken from where the blinking starts.");
            }
            MeasureColors(lit, result);

            // Nothing above the redline is a light switching on: the file's format puts every light at
            // or below it. Above it a strip is changing colour, blinking or both, and a game that fades
            // its lights (ACC) leaves frames half way through the change - two lights, then seven, then
            // ten - which would read as lights going dark and coming back on at the redline. A little
            // margin keeps the last pair on cars where it lights at the redline itself.
            double overall = result.RedlineRpm.HasValue
                ? (result.RedlineLowestAbove ?? result.RedlineRpm.Value) + RedlineMarginRpm
                : double.MaxValue;
            // A gear with a redline of its own is cut off at that instead.
            double Ceiling(string gear) => result.RedlineByGear.TryGetValue(gear, out var own) ? own.LowestAbove + RedlineMarginRpm : overall;
            var window = new LedWindowCapture(layout.LedNumber);
            for (int i = 0; i < _samples.Count; i++)
                if (!blink[i] && _samples[i].Rpm <= Ceiling(_samples[i].Gear)) window.Record(_samples[i].Gear, _samples[i].Rpm, lit[i]);
            result.Gears = window.Gears.Select(window.Result).ToList();
            AverageWithSwitchOff(lit, blink, Ceiling, result);
            result.Notes.AddRange(_lagNotes);

            ReportSolidRedline(lit, result);

            int neverLit = Enumerable.Range(0, layout.LedNumber).Count(s => !layout.IsGap[s] && !lit.Any(f => f[s]));
            if (neverLit > 0) result.Notes.Add(neverLit + " light(s) were never seen lit; rev higher to reach them.");
            if (layout.GapCount > 0)
                result.Notes.Add("The strip has " + layout.GapCount + " gap(s) where the spacing leaves room but nothing ever lights.");
            return result;
        }

        /// <summary>How far from its usual colour a light has to be to count as showing something else.</summary>
        private const double IndicatorDegrees = 30;

        /// <summary>
        /// Each slot's usual colour: the one it shows most often while lit. A light climbing through the
        /// strip stays lit for seconds in its own colour, where an indicator flickers - the ACC Huracán
        /// GT3 Evo2 lights its first two blue for traction control and its last two yellow for ABS, both
        /// at any RPM. Frames with several lights all one colour are left out: that's the redline, a
        /// blink coming back, or a fade (ACC) part way into one, and the limiter is held for long.
        /// </summary>
        private double[] OwnHues(StripLayout layout)
        {
            var perSlot = Enumerable.Range(0, layout.LedNumber).Select(_ => new List<double>()).ToArray();
            foreach (var sample in _samples)
            {
                var slots = sample.X.Select(layout.SlotOf).ToArray();
                var lights = Enumerable.Range(0, slots.Length).Where(b => slots[b] >= 0 && sample.Colors[b].Hue >= 0).ToList();
                if (lights.Count >= 3 && Spread(lights.Select(b => sample.Colors[b].Hue).ToList()) <= OneColourDegrees) continue;
                foreach (int b in lights) perSlot[slots[b]].Add(sample.Colors[b].Hue);
            }
            return perSlot.Select(h => h.Count == 0 ? -1 : UsualHue(h)).ToArray();
        }

        /// <summary>The middle of the most crowded 30-degree stretch of the hue circle.</summary>
        private static double UsualHue(List<double> hues)
        {
            double best = hues[0];
            int most = -1;
            foreach (var centre in hues.Where((h, i) => i % Math.Max(1, hues.Count / 200) == 0))
            {
                int count = hues.Count(h => HueDistance(h, centre) <= 15);
                if (count > most) { most = count; best = centre; }
            }
            return CircularMean(hues.Where(h => HueDistance(h, best) <= 15));
        }

        /// <summary>
        /// Takes out lights showing something other than the rev count. ACC's Huracán GT3 Evo2 turns
        /// its first two lights blue while traction control works, at whatever RPM that happens: read
        /// as rev lights, they came on 400 rpm early and looked like the strip changing to a blue
        /// redline in first gear. A light far from its usual colour is an indicator when the strip
        /// isn't full, or when it's a few lights among many still showing their own colours; when the
        /// whole strip turns one colour, or most of it changes, that's the redline and stays.
        /// </summary>
        private void DropIndicators(StripLayout layout, ScreenLedResult result)
        {
            int lights = layout.LedNumber - layout.GapCount;
            var own = OwnHues(layout);
            _indicatorFrame = new bool[_samples.Count];
            var seen = new int[layout.LedNumber];
            var colours = Enumerable.Range(0, layout.LedNumber).Select(_ => new List<LedColor>()).ToArray();
            for (int i = 0; i < _samples.Count; i++)
            {
                var s = _samples[i];
                var slots = s.X.Select(layout.SlotOf).ToArray();
                var off = Enumerable.Range(0, slots.Length)
                                    .Where(b => slots[b] >= 0 && own[slots[b]] >= 0 && s.Colors[b].Hue >= 0 &&
                                                HueDistance(s.Colors[b].Hue, own[slots[b]]) > IndicatorDegrees)
                                    .ToList();
                if (off.Count == 0) continue;
                int litNow = slots.Count(x => x >= 0);
                var huesNow = Enumerable.Range(0, slots.Length).Where(b => slots[b] >= 0 && s.Colors[b].Hue >= 0)
                                        .Select(b => s.Colors[b].Hue).ToList();
                bool full = litNow >= lights - 1;
                bool oneColour = huesNow.Count >= 2 && Spread(huesNow) <= OneColourDegrees;
                if (full && (oneColour || off.Count * 2 >= litNow)) continue;     // the redline state
                // A game that fades (ACC) catches the strip part way into or out of its redline colour:
                // several lights, all that colour. An indicator is one or two lights.
                if (!full && oneColour && off.Count == litNow && litNow >= 3) continue;

                foreach (int b in off) { seen[slots[b]]++; colours[slots[b]].Add(s.Colors[b]); }
                _indicatorFrame[i] = true;
                var keep = Enumerable.Range(0, slots.Length).Where(b => !off.Contains(b)).ToList();
                s.X = keep.Select(b => s.X[b]).ToArray();
                s.Colors = keep.Select(b => s.Colors[b]).ToArray();
                _samples[i] = s;
            }
            var used = Enumerable.Range(0, layout.LedNumber).Where(x => seen[x] >= 10).ToList();
            if (used.Count == 0) return;
            // Named per light, then grouped: one car can have two indicators, traction control in blue
            // on one end and ABS in yellow on the other.
            var named = used.Select(x =>
            {
                var c = colours[x];
                var mean = new LedColor((int)c.Average(k => k.R), (int)c.Average(k => k.G), (int)c.Average(k => k.B));
                LedPalette.Classify(mean, out string name);
                return new { Led = x + 1, Name = name };
            }).GroupBy(n => n.Name).Select(g => "LED " + string.Join(", ", g.Select(n => n.Led)) + " " + g.Key);
            result.Notes.Add("Some lights sometimes showed another colour while the rest of the strip didn't change (" +
                             string.Join("; ", named) + ") - indicators such as traction control or ABS, not the rev count. " +
                             "Those sightings were ignored (" + seen.Sum() + " in all).");
        }

        /// <summary>Below this share of the highest revs reached, a lit strip isn't counting revs.</summary>
        private const double IdleShare = 0.5;

        /// <summary>
        /// Takes out lights lit far below where any rev light comes on. PMR's C8.R plays an animation
        /// at idle - the whole strip flashing green in a pattern at 1500 rpm - which read as a blink
        /// from 1510 rpm and lights switching on at idle. Rev lights are dark there, and no car's first
        /// light is below half its revs, so anything lit under half the highest revs reached is dropped.
        /// </summary>
        private bool DropIdleAnimation(ScreenLedResult result)
        {
            int highest = _samples.Max(s => s.Rpm);
            double floor = highest * IdleShare;
            int frames = 0, lowest = int.MaxValue, top = 0;
            for (int i = 0; i < _samples.Count; i++)
            {
                var s = _samples[i];
                if (s.Rpm >= floor || s.X.Length == 0) continue;
                frames++;
                lowest = Math.Min(lowest, s.Rpm);
                top = Math.Max(top, s.Rpm);
                s.X = new double[0];
                s.Colors = new LedColor[0];
                _samples[i] = s;
            }
            if (frames < 3) return false;
            result.Notes.Add("The strip was lit at " + lowest + "-" + top + " rpm, well below where rev lights work (under half the " +
                             highest + " rpm reached) - the pit limiter, or an idle or start-up animation. Those " + frames + " frames were ignored.");
            return true;
        }

        /// <summary>Below this saturation a blob might be a reflection rather than a light.</summary>
        private const double PaleSaturation = 0.45;

        /// <summary>How near a pale blob has to be to one seen at idle to count as the same thing.</summary>
        private const int FixtureReachPx = 15;

        /// <summary>
        /// Takes out bright pale things that aren't lights. LMU's SC63 has a pale-blue reflection on
        /// the wheel between two of its lights that just clears the detector's test for colour, and it
        /// wanders a little as the wheel moves. A saturation cut alone can't be used: a washed-out
        /// green recorded through a video encoder is barely more saturated. What gives the reflection
        /// away is that it's there at idle, far below where any light comes on. So pale blobs seen at
        /// idle mark where the reflection is, and pale blobs there are dropped at every RPM; strongly
        /// coloured ones are left alone, so a pit limiter's lights at idle stay in.
        /// </summary>
        private bool DropFixtures(ScreenLedResult result)
        {
            int lowest = _samples.Min(s => s.Rpm), highest = _samples.Max(s => s.Rpm);
            double idle = lowest + (highest - lowest) * 0.1;
            int idleFrames = _samples.Count(s => s.Rpm <= idle);
            var pale = _samples.Where(s => s.Rpm <= idle)
                               .SelectMany(s => s.X.Where((x, b) => s.Colors[b].Saturation < PaleSaturation))
                               .ToList();
            if (pale.Count < Math.Max(5, idleFrames * 0.2)) return false;

            bool Fixture(double x) => pale.Count(p => Math.Abs(p - x) <= FixtureReachPx) >= 5;
            int dropped = 0;
            for (int i = 0; i < _samples.Count; i++)
            {
                var s = _samples[i];
                var keep = Enumerable.Range(0, s.X.Length).Where(b => s.Colors[b].Saturation >= PaleSaturation || !Fixture(s.X[b])).ToList();
                if (keep.Count == s.X.Length) continue;
                dropped += s.X.Length - keep.Count;
                s.X = keep.Select(b => s.X[b]).ToArray();
                s.Colors = keep.Select(b => s.Colors[b]).ToArray();
                _samples[i] = s;
            }
            if (dropped == 0) return false;
            result.Notes.Add("Something pale and bright in the box was lit even at idle, so it isn't a light - a reflection or a " +
                             "display. It was ignored (" + dropped + " sightings). A tighter box round the lights avoids it.");
            return true;
        }

        /// <summary>Hue change that counts as a light no longer showing its own colour.</summary>
        private const double RedlineHueShift = 12.0;

        /// <summary>How close together every lit light's hue has to be for the strip to count as one colour.</summary>
        private const double OneColourDegrees = 8.0;

        /// <summary>Average of hues the right way round the circle: 359 and 1 average to 0, not 180.</summary>
        private static double CircularMean(IEnumerable<double> hues)
        {
            double x = 0, y = 0;
            foreach (var h in hues)
            {
                x += Math.Cos(h * Math.PI / 180);
                y += Math.Sin(h * Math.PI / 180);
            }
            double mean = Math.Atan2(y, x) * 180 / Math.PI;
            return mean < 0 ? mean + 360 : mean;
        }

        /// <summary>How long after a gear change, beyond the display lag, before frames count again.</summary>
        private const int ShiftSettleMs = 50;

        /// <summary>Longest run of dark frames a redline crossing may be looked for across.</summary>
        private const int DarkBridgeMs = 200;

        /// <summary>Widest RPM step between the two frames either side of a redline crossing that still pins it down.</summary>
        private const int MaxCrossingStepRpm = 150;

        /// <summary>
        /// Fewest flips between the redline colour and the strip's own before it counts as a flash. LMU's
        /// SC63 gave 76; revs bouncing on a limiter right at the redline give a handful.
        /// </summary>
        private const int MinFlashes = 10;

        /// <summary>How long the strip has to be clear of its redline state on the near side of a crossing for it to count.</summary>
        private const int ClearRunMs = 200;

        private static double HueDistance(double a, double b)
        {
            double d = Math.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
        }

        /// <summary>Widest gap between any two hues, the short way round - red sits at both ends of the scale.</summary>
        private static double Spread(List<double> hues)
        {
            double widest = 0;
            for (int a = 0; a < hues.Count; a++)
                for (int b = a + 1; b < hues.Count; b++)
                    widest = Math.Max(widest, HueDistance(hues[a], hues[b]));
            return widest;
        }

        /// <summary>
        /// Above the redline a strip stops showing its own colours and turns one colour, usually red.
        /// Looking for "every lit light is the same colour" isn't enough, because early in a climb only
        /// the green end is lit. So each light's own colour is learned from the frames where the strip
        /// isn't full - it can't be in its redline state there - and the redline is where the lights
        /// that have a known colour stop showing it. The switch-on and switch-off RPMs bracket the
        /// value, and their midpoint cancels most of the lag between the game and the sample.
        /// </summary>
        private void FindRedline(bool[][] lit, double[][] hues, ScreenLedResult result)
        {
            var layout = result.Layout;
            int lights = layout.LedNumber - layout.GapCount;
            var ownHue = OwnHues(layout);
            if (ownHue.Count(h => h >= 0) < 2)
            {
                result.Notes.Add("The lights were never seen partly lit, so their own colours couldn't be told from the redline colour. Rev up slowly from idle.");
                return;
            }

            // A frame is in the redline state when the strip shows one colour although its lights
            // normally show several - or, failing that, when most lights have left their own colour.
            // The first test catches a redline colour that most of the strip already has: the AMS2 BMW
            // M8 GTE turns every light the deep orange of six of its ten, so only the four green ones
            // visibly change, too few for a majority.
            var redline = new bool?[_samples.Count];
            for (int i = 0; i < _samples.Count; i++)
            {
                // The redline is at or above the last light, so the strip is full there. Fewer lights
                // changing colour is something else - an indicator like ACC's blue traction control.
                int litNow = lit[i].Count(v => v);
                bool partial = litNow < lights - 1;
                int known = 0, changed = 0;
                var now = new List<double>();
                var own = new List<double>();
                for (int s = 0; s < layout.LedNumber; s++)
                {
                    if (ownHue[s] < 0 || hues[i][s] < 0) continue;
                    known++;
                    now.Add(hues[i][s]);
                    own.Add(ownHue[s]);
                    if (HueDistance(hues[i][s], ownHue[s]) > RedlineHueShift) changed++;
                }
                if (known < 2) continue;
                bool oneColourNow = Spread(now) <= OneColourDegrees;
                bool severalOwnColours = Spread(own) > RedlineHueShift;
                if (partial)
                {
                    // Several lights, every one out of its own colour and all the same: a game that
                    // fades (ACC) caught part way into or out of its redline colour. Anything else short
                    // of a full strip is below the redline - one or two lights in another colour are an
                    // indicator like traction control.
                    redline[i] = known >= 3 && changed == known && oneColourNow;
                    continue;
                }
                redline[i] = (oneColourNow && severalOwnColours && changed > 0) || changed * 2 > known;
            }

            _redline = redline;

            // Only crossings with a clear run on the near side count. Some strips flip between the
            // redline colour and their own at the limiter (LMU's SC63, every 50 ms or so), and each flip
            // would otherwise read as the redline being crossed wherever the limiter had the revs.
            bool Clear(int from, int step)
            {
                for (int j = from; j >= 0 && j < _samples.Count; j += step)
                {
                    if (_samples[j].Gear != _samples[from].Gear || Math.Abs(_samples[j].TimeMs - _samples[from].TimeMs) > ClearRunMs) break;
                    if (redline[j] == true) return false;
                }
                return true;
            }

            var risingAt = new List<double>();
            var fallingAt = new List<double>();
            var risingByGear = new Dictionary<string, List<double>>();
            var fallingByGear = new Dictionary<string, List<double>>();
            void Add(Dictionary<string, List<double>> byGear, string gear, double rpm)
            {
                if (!byGear.TryGetValue(gear, out var list)) byGear[gear] = list = new List<double>();
                list.Add(rpm);
            }
            for (int i = 1; i < _samples.Count; i++)
            {
                if (!redline[i].HasValue) continue;
                // The frame before, looking past dark ones: PMR's C8.R goes from its own colours through a
                // dark frame straight into a blinking blue, never showing the two side by side.
                int p = i - 1;
                while (p >= 0 && !redline[p].HasValue && _samples[p].Gear == _samples[i].Gear &&
                       _samples[i].TimeMs - _samples[p].TimeMs <= DarkBridgeMs) p--;
                if (p < 0 || !redline[p].HasValue || _samples[i].TimeMs - _samples[p].TimeMs > DarkBridgeMs) continue;
                if (_samples[i].Gear != _samples[p].Gear) continue;
                // The middle of two frames far apart in RPM says little: leaving the limiter, the revs
                // can drop 800 rpm between frames, which put the ACC Huracán's 4th gear redline 150 low.
                if (Math.Abs(_samples[i].Rpm - _samples[p].Rpm) > MaxCrossingStepRpm) continue;
                double at = (_samples[i].Rpm + _samples[p].Rpm) / 2.0;
                if (redline[i].Value && !redline[p].Value && _samples[i].Rpm > _samples[p].Rpm && Clear(p, -1))
                {
                    risingAt.Add(at);
                    Add(risingByGear, _samples[i].Gear, at);
                }
                if (!redline[i].Value && redline[p].Value && _samples[i].Rpm < _samples[p].Rpm && Clear(i, 1))
                {
                    fallingAt.Add(at);
                    Add(fallingByGear, _samples[i].Gear, at);
                }
            }
            foreach (var gear in risingByGear.Keys.Union(fallingByGear.Keys))
            {
                double? gUp = risingByGear.TryGetValue(gear, out var r) ? Median(r) : (double?)null;
                double? gDown = fallingByGear.TryGetValue(gear, out var f) ? Median(f) : (double?)null;
                double g = gUp.HasValue && gDown.HasValue ? (gUp.Value + gDown.Value) / 2 : (gUp ?? gDown.Value);
                result.RedlineByGear[gear] = new GearRedline
                {
                    Rpm = RoundTo(g, 5),
                    HighestBelow = (int)Math.Round(Math.Min(gUp ?? g, gDown ?? g)),
                    LowestAbove = (int)Math.Round(Math.Max(gUp ?? g, gDown ?? g)),
                };
            }
            FindFlash(redline, result);
            if (risingAt.Count == 0 && fallingAt.Count == 0)
            {
                // A strip that blinks has shown where its redline is anyway; saying the limiter was never
                // reached would contradict the next note.
                if (!result.BlinkSeen)
                    result.Notes.Add("The strip never changed colour, so no redline colour change was seen. " +
                                     "Either this car doesn't have one, or the limiter was never reached.");
                return;
            }

            double? up = risingAt.Count > 0 ? Median(risingAt) : (double?)null;
            double? down = fallingAt.Count > 0 ? Median(fallingAt) : (double?)null;
            double estimate = up.HasValue && down.HasValue ? (up.Value + down.Value) / 2 : (up ?? down.Value);
            result.RedlineRpm = RoundTo(estimate, 5);
            result.RedlineHighestBelow = (int)Math.Round(Math.Min(up ?? estimate, down ?? estimate));
            result.RedlineLowestAbove = (int)Math.Round(Math.Max(up ?? estimate, down ?? estimate));

            FindSecondStage(redline, estimate, result);

            // Only the first stage's own frames: averaging in a second stage would give a colour the
            // strip never shows, halfway between the two.
            double ceiling = result.SecondStageRpm ?? double.MaxValue;
            var above = _samples.Where((s, i) => redline[i] == true && s.Rpm > estimate && s.Rpm < ceiling)
                                .SelectMany(s => s.Colors).Where(c => c.Hue >= 0).ToList();
            if (above.Count > 0)
            {
                var mean = new LedColor((int)above.Average(c => c.R), (int)above.Average(c => c.G), (int)above.Average(c => c.B));
                result.RedlineColor = LedPalette.Classify(mean, out _);
                result.RedlineMeasured = mean;
            }
        }

        /// <summary>
        /// Some cars change colour twice: once at the redline and again close to the limiter. The file
        /// format keeps one redline, so this is only reported, but it explains a strip that doesn't
        /// look like the file on the wheel.
        /// </summary>
        private void FindSecondStage(bool?[] redline, double redlineRpm, ScreenLedResult result)
        {
            var aboveRedline = new List<int>();
            for (int i = 0; i < _samples.Count; i++)
                if (redline[i] == true && _samples[i].Rpm > redlineRpm) aboveRedline.Add(i);
            if (aboveRedline.Count < 30) return;

            // The first stage's colour, taken from the frames just above the redline.
            var lowest = aboveRedline.OrderBy(i => _samples[i].Rpm).Take(Math.Max(10, aboveRedline.Count / 5)).ToList();
            var firstHues = lowest.SelectMany(i => _samples[i].Colors).Select(c => c.Hue).Where(h => h >= 0).ToList();
            if (firstHues.Count == 0) return;
            double firstStage = CircularMean(firstHues);

            var changedAt = new List<double>();
            var changedColors = new List<LedColor>();
            int previous = -1;
            foreach (int i in aboveRedline)
            {
                var hues = _samples[i].Colors.Select(c => c.Hue).Where(h => h >= 0).ToList();
                if (hues.Count < 3) { previous = i; continue; }
                bool changed = HueDistance(CircularMean(hues), firstStage) > RedlineHueShift;
                if (changed)
                {
                    changedColors.AddRange(_samples[i].Colors.Where(c => c.Hue >= 0));
                    if (previous >= 0 && _samples[previous].Rpm < _samples[i].Rpm &&
                        HueDistance(CircularMean(_samples[previous].Colors.Select(c => c.Hue).Where(h => h >= 0).DefaultIfEmpty(firstStage)), firstStage) <= RedlineHueShift)
                        changedAt.Add((_samples[i].Rpm + _samples[previous].Rpm) / 2.0);
                }
                previous = i;
            }
            if (changedAt.Count == 0 || changedColors.Count == 0) return;

            result.SecondStageRpm = RoundTo(Median(changedAt), 5);
            result.SecondStageMeasured = new LedColor((int)changedColors.Average(c => c.R),
                                                      (int)changedColors.Average(c => c.G),
                                                      (int)changedColors.Average(c => c.B));
            result.SecondStageColor = LedPalette.Classify(result.SecondStageMeasured, out _);
            result.Notes.Add("The strip changed colour a second time at about " + result.SecondStageRpm +
                             " rpm (" + result.SecondStageMeasured + "). A car file holds one redline, so only the first is in it; " +
                             "ATSR adds a second stage itself for some cars.");
        }

        /// <summary>Colour of each slot while the strip is showing its own colours, below the redline.</summary>
        private void MeasureColors(bool[][] lit, ScreenLedResult result)
        {
            var layout = result.Layout;
            var sums = new long[layout.LedNumber, 3];
            var counts = new int[layout.LedNumber];
            double ceiling = result.RedlineHighestBelow ?? result.RedlineRpm ?? double.MaxValue;

            for (int i = 0; i < _samples.Count; i++)
            {
                if (_samples[i].Rpm >= ceiling || (_redline != null && _redline[i] == true)) continue;
                for (int b = 0; b < _samples[i].X.Length; b++)
                {
                    int slot = layout.SlotOf(_samples[i].X[b]);
                    if (slot < 0) continue;
                    var c = _samples[i].Colors[b];
                    if (c.Hue < 0) continue;
                    sums[slot, 0] += c.R; sums[slot, 1] += c.G; sums[slot, 2] += c.B;
                    counts[slot]++;
                }
            }

            result.MeasuredColors = new LedColor[layout.LedNumber];
            result.ColorUnknown = new bool[layout.LedNumber];
            for (int s = 0; s < layout.LedNumber; s++)
            {
                result.MeasuredColors[s] = counts[s] == 0
                    ? new LedColor(0, 0, 0)
                    : new LedColor((int)(sums[s, 0] / counts[s]), (int)(sums[s, 1] / counts[s]), (int)(sums[s, 2] / counts[s]));
                result.ColorUnknown[s] = counts[s] == 0 && !layout.IsGap[s] && lit.Any(f => f[s]);
            }
            var unknown = Enumerable.Range(0, layout.LedNumber).Where(s => result.ColorUnknown[s]).ToList();
            if (unknown.Count > 0)
                result.Notes.Add("LED " + string.Join(", ", unknown.Select(s => s + 1)) +
                                 " only ever lit at or above the redline, where the whole strip has already changed colour, " +
                                 "so their own colour couldn't be seen.");

            result.ColorGroups = LedPalette.Group(result.MeasuredColors, layout.IsGap);
            result.ColorsDoubtful = LedPalette.Doubtful(result.ColorGroups);

            // The flash is usually one of the strip's own colours. Naming it against them rather than
            // on its own keeps a washed red red, where alone it would read as orange; a flash that
            // matches nothing on the strip, like the blue some cars use, still gets named for itself.
            if (result.RedlineMeasured.Hue >= 0)
            {
                var same = result.ColorGroups.FirstOrDefault(g => Math.Abs(g.Hue - result.RedlineMeasured.Hue) <= LedPalette.SameColorDegrees);
                if (same != null) result.RedlineColor = same.Hex;
            }
            if (result.ColorsDoubtful)
                result.Notes.Add("Some colours are too close to tell apart on screen; check them against the game.");
        }

        /// <summary>Longest dark gap that still counts as one blink rather than the lights genuinely going off.</summary>
        private const int MaxBlinkMs = 700;

        /// <summary>Fewest dark frames in a row that make a blink.</summary>
        private const int MinBlinkFrames = 2;

        /// <summary>Fewest blinks before the strip counts as blinking. Holding the limiter gives dozens.</summary>
        private const int MinBlinks = 3;

        /// <summary>How far above the redline a frame can still count towards a light's threshold.</summary>
        private const int RedlineMarginRpm = 50;

        /// <summary>Frames a game's fade can spend between a dark strip and a full one.</summary>
        private const int FadeFrames = 2;

        /// <summary>
        /// Finds the frames where the strip is dark because it's blinking. The signature doesn't depend
        /// on knowing the redline: a short run of fully dark frames with the whole strip lit on both
        /// sides of it. The lights never all go off between one frame and the next for any other reason.
        /// </summary>
        private bool[] FindBlinks(bool[][] lit, StripLayout layout, ScreenLedResult result, bool?[] redline = null)
        {
            int n = _samples.Count;
            var blink = new bool[n];
            int lights = layout.LedNumber - layout.GapCount;
            if (lights < 2) return blink;
            var count = lit.Select(f => f.Count(v => v)).ToArray();
            // One light missed in a frame shouldn't hide a full strip.
            bool Full(int i) => count[i] >= lights - 1 || (redline != null && redline[i] == true && count[i] >= 2);

            var darkLengths = new List<double>();
            var onsets = new List<double>();
            long lastBlink = long.MinValue;
            int k = 0;
            while (k < n)
            {
                if (count[k] != 0) { k++; continue; }
                int start = k;
                while (k < n && count[k] == 0 && _samples[k].Gear == _samples[start].Gear) k++;
                int end = k;                        // first frame with anything lit
                if (start == 0 || end >= n) continue;

                // A full strip on each side, allowing a frame or two of fade in between: a game that
                // fades its lights catches some of them part way on.
                int before = -1, after = -1;
                for (int j = start - 1; j >= 0 && j >= start - 1 - FadeFrames && _samples[j].Gear == _samples[start].Gear; j--)
                    if (Full(j)) { before = j; break; }
                for (int j = end; j < n && j <= end + FadeFrames && _samples[j].Gear == _samples[start].Gear; j++)
                    if (Full(j)) { after = j; break; }
                long length = _samples[end].TimeMs - _samples[start].TimeMs;
                if (before < 0 || after < 0 || length > MaxBlinkMs) continue;
                // One dark frame is a dropped or half-drawn frame, not a blink: the ACC Huracán GT3 Evo2
                // showed two of them at the limiter and doesn't blink. Real blinks are dark for several.
                if (end - start < MinBlinkFrames) continue;

                // The fade frames go with the blink: they're no more a light switching than the dark is.
                for (int f = before + 1; f < after; f++) blink[f] = true;
                darkLengths.Add(length);

                // The first blink of each visit to the limiter says where blinking starts. Later ones
                // only say where the limiter holds the revs, which is higher.
                if (lastBlink == long.MinValue || _samples[start].TimeMs - lastBlink > 1000)
                    onsets.Add((_samples[before].Rpm + _samples[start].Rpm) / 2.0);
                lastBlink = _samples[after].TimeMs;
            }

            result.BlinkSeen = darkLengths.Count >= MinBlinks;
            if (!result.BlinkSeen) return blink;

            result.BlinkIntervalMs = (int)Math.Round(Median(darkLengths));
            // A blink cycle isn't timed to the revs, so the first dark frame of a visit can come up to one
            // lit phase after the threshold was crossed. The earliest visits are the closest; the lowest
            // quarter rather than the very lowest, so one odd frame can't decide it.
            onsets.Sort();
            result.BlinkFromRpm = RoundTo(onsets[onsets.Count / 4], 5);
            result.Notes.Add("The strip blinks at the limiter: dark for about " + result.BlinkIntervalMs + " ms at a time, " +
                             darkLengths.Count + " blinks seen. Those frames were kept out of the light thresholds.");
            return blink;
        }

        /// <summary>Fewest switch-off readings a light needs before they're trusted to move its value.</summary>
        private const int MinFalls = 3;

        /// <summary>Widest gap between switching on and off that's still display lag, not two different things.</summary>
        private const int MaxLagRpm = 200;

        /// <summary>
        /// Moves each light to the middle of where it switched on and where it switched off.
        /// </summary>
        /// <remarks>
        /// A light is only seen lit once it's bright enough on screen. A game that switches its lights
        /// instantly (AMS2) is seen on the frame it happens, and switching on and off agree to within a
        /// few rpm. One that fades them in (ACC) is seen a frame or so late on the way up and a frame
        /// or so early on the way down, so every light reads high rising and low falling, by the same
        /// amount. The middle of the two is where it really switches, and for an instant game it's no
        /// different from the rising value alone.
        /// </remarks>
        private void AverageWithSwitchOff(bool[][] lit, bool[] blink, Func<string, double> ceiling, ScreenLedResult result)
        {
            var falls = new Dictionary<string, List<double>[]>();
            for (int i = 1; i < _samples.Count; i++)
            {
                var a = _samples[i - 1];
                var b = _samples[i];
                if (a.Gear != b.Gear || blink[i] || blink[i - 1] || a.Rpm > ceiling(a.Gear) || b.Rpm > ceiling(b.Gear) || b.Rpm >= a.Rpm) continue;
                if (!falls.TryGetValue(b.Gear, out var perSlot))
                {
                    perSlot = Enumerable.Range(0, result.Layout.LedNumber).Select(_ => new List<double>()).ToArray();
                    falls[b.Gear] = perSlot;
                }
                for (int s = 0; s < result.Layout.LedNumber; s++)
                    if (lit[i - 1][s] && !lit[i][s]) perSlot[s].Add((a.Rpm + b.Rpm) / 2.0);
            }

            var gaps = new List<int>();
            foreach (var gear in result.Gears)
            {
                if (!falls.TryGetValue(gear.Gear, out var perSlot)) continue;
                for (int s = 0; s < gear.Leds.Length; s++)
                {
                    var rise = gear.Leds[s];
                    if (rise == null || perSlot[s].Count < MinFalls) continue;
                    double fall = Median(perSlot[s]);
                    double lag = rise.Rpm - fall;
                    if (lag < 0 || lag > MaxLagRpm) continue;      // a stray reading, not the same edge seen twice
                    gear.Leds[s] = rise.WithFall(RoundTo((rise.Rpm + fall) / 2, 5), (int)Math.Round(fall));
                }
            }
        }

        /// <summary>Longest display lag looked for, in milliseconds.</summary>
        private const int MaxLagMs = 250;

        /// <summary>
        /// Widest gap between a light's switch-on and switch-off that still counts as the same light
        /// seen late. PMR shows its lights about 80 ms behind the revs, which in neutral, with the revs
        /// falling fast, is 550 rpm.
        /// </summary>
        private const int MaxSwitchGapRpm = 1000;

        private readonly List<string> _lagNotes = new List<string>();

        /// <summary>
        /// Moves every frame's RPM back to what the revs were when the screen was drawn.
        /// </summary>
        /// <remarks>
        /// The screen shows the lights a little after the telemetry has moved on: the game renders a
        /// frame or so behind, and a game that fades its lights in (ACC) is seen later still. A fixed
        /// delay costs a different number of rpm in every gear - in first the revs climb several times
        /// faster than in fourth - so it's measured in time. With the right delay, where a light is seen
        /// switching on as the revs rise and off as they fall agree; with too little, on reads high and
        /// off reads low. So the delay is the one that makes them agree, found by trying them.
        /// </remarks>
        private void RemoveDisplayLag(bool[][] lit, bool[] blink, ScreenLedResult result)
        {
            _lagNotes.Clear();
            int best = 0, lights = 0;
            double bestGap = 0, noLagGap = 0;
            // A tight limit on how far on and off may disagree first, which keeps odd switches out. Only
            // a game far behind its revs (PMR, about 80 ms) needs it wider, and then nothing passes it.
            foreach (int limit in new[] { MaxLagRpm * 2, MaxSwitchGapRpm })
            {
                best = FindLag(lit, blink, limit, out lights, out bestGap, out noLagGap);
                if (best > 0) break;
            }
            if (best == 0) return;
            // How far off the raw readings were, for the report: every light, however far apart.
            var unshifted = SwitchGaps(lit, blink, Shifted(0), int.MaxValue);
            if (unshifted.Count > 0) noLagGap = Median(unshifted);

            var moved = Shifted(best);
            for (int i = 0; i < _samples.Count; i++)
            {
                var s = _samples[i];
                s.Rpm = (int)Math.Round(moved[i]);
                _samples[i] = s;
            }
            result.DisplayLagMs = best;
            _lagNotes.Add("The game shows its lights about " + best + " ms after its revs, so every light read about " +
                          (int)Math.Round(noLagGap / 2) + " rpm high switching on and as much low switching off. Each frame " +
                          "was read against the revs " + best + " ms earlier, which brings the two within " +
                          (int)Math.Round(bestGap) + " rpm (" + lights + " lights seen both ways).");
        }

        /// <summary>Tries each delay in turn and returns the one where switching on and off agree best.</summary>
        private int FindLag(bool[][] lit, bool[] blink, int limit, out int lights, out double bestGap, out double noLagGap)
        {
            int best = 0;
            lights = 0;
            bestGap = double.MaxValue;
            noLagGap = double.NaN;
            for (int lag = 0; lag <= MaxLagMs; lag += 2)
            {
                var gaps = SwitchGaps(lit, blink, Shifted(lag), limit);
                if (gaps.Count < MinFalls) continue;   // too few lights seen both ways to tell at this delay
                double signed = Median(gaps), gap = Math.Abs(signed);
                if (double.IsNaN(noLagGap)) noLagGap = signed;
                if (gap < bestGap - 0.5) { bestGap = gap; best = lag; lights = gaps.Count; }
                // Past the delay where they agree, on and off only drift further apart the other way; a
                // wide search mustn't find a chance agreement among a few lights much later.
                else if (signed < 0) break;
            }
            if (double.IsNaN(noLagGap)) noLagGap = 0;
            return best;
        }

        /// <summary>Each frame's RPM as it was <paramref name="lagMs"/> earlier, within the same gear.</summary>
        private double[] Shifted(int lagMs)
        {
            var rpm = new double[_samples.Count];
            for (int i = 0; i < _samples.Count; i++)
            {
                double when = _samples[i].TimeMs - lagMs;
                int j = i;
                while (j > 0 && _samples[j - 1].Gear == _samples[i].Gear && _samples[j].TimeMs > when) j--;
                if (_samples[j].TimeMs >= when || j == i) { rpm[i] = _samples[j].Rpm; continue; }
                // Between frame j and j+1.
                var a = _samples[j];
                var b = _samples[j + 1];
                double f = b.TimeMs == a.TimeMs ? 0 : (when - a.TimeMs) / (double)(b.TimeMs - a.TimeMs);
                rpm[i] = a.Rpm + (b.Rpm - a.Rpm) * f;
            }
            return rpm;
        }

        /// <summary>
        /// For every light seen switching both ways in a gear, how much higher it switched on than off,
        /// against the given RPMs.
        /// </summary>
        private List<double> SwitchGaps(bool[][] lit, bool[] blink, double[] rpm, int limit)
        {
            var rises = new Dictionary<string, List<double>>();
            var falls = new Dictionary<string, List<double>>();
            int slots = lit.Length > 0 ? lit[0].Length : 0;
            for (int i = 1; i < _samples.Count; i++)
            {
                if (_samples[i].Gear != _samples[i - 1].Gear || blink[i] || blink[i - 1]) continue;
                // Only ordinary frames: a whole strip changing at once is a blink, a flash or the limiter.
                int before = lit[i - 1].Count(v => v), after = lit[i].Count(v => v);
                if (Math.Abs(after - before) > 3) continue;
                bool rising = rpm[i] > rpm[i - 1], falling = rpm[i] < rpm[i - 1];
                double at = (rpm[i] + rpm[i - 1]) / 2;
                for (int s = 0; s < slots; s++)
                {
                    string key = _samples[i].Gear + "|" + s;
                    if (rising && !lit[i - 1][s] && lit[i][s]) Add(rises, key, at);
                    if (falling && lit[i - 1][s] && !lit[i][s]) Add(falls, key, at);
                }
            }
            var gaps = new List<double>();
            foreach (var key in rises.Keys)
            {
                if (!falls.TryGetValue(key, out var down) || down.Count < 2 || rises[key].Count < 2) continue;
                double gap = Median(rises[key]) - Median(down);
                if (Math.Abs(gap) <= limit) gaps.Add(gap);    // further apart is two different things
            }
            return gaps;

            void Add(Dictionary<string, List<double>> into, string key, double value)
            {
                if (!into.TryGetValue(key, out var list)) into[key] = list = new List<double>();
                list.Add(value);
            }
        }

        /// <summary>Longest phase of a flash between the redline colour and the strip's own colours.</summary>
        private const int MaxFlashPhaseMs = 300;

        /// <summary>
        /// Finds a strip that flips between its redline colour and its own colours at the limiter,
        /// instead of blinking to dark. ATSR has no way to show that, so it's only reported.
        /// </summary>
        private void FindFlash(bool?[] redline, ScreenLedResult result)
        {
            // Runs of the same state in the same gear, frames that couldn't be told skipped.
            var runs = new List<Tuple<bool, int, string>>();      // state, first frame, gear
            for (int i = 0; i < _samples.Count; i++)
            {
                if (!redline[i].HasValue) continue;
                var last = runs.Count > 0 ? runs[runs.Count - 1] : null;
                if (last == null || last.Item1 != redline[i].Value || last.Item3 != _samples[i].Gear)
                    runs.Add(Tuple.Create(redline[i].Value, i, _samples[i].Gear));
            }
            var own = new List<double>();
            var red = new List<double>();
            for (int k = 1; k + 1 < runs.Count; k++)
            {
                if (runs[k - 1].Item3 != runs[k].Item3 || runs[k + 1].Item3 != runs[k].Item3) continue;
                double length = _samples[runs[k + 1].Item2].TimeMs - _samples[runs[k].Item2].TimeMs;
                if (length > MaxFlashPhaseMs) continue;
                (runs[k].Item1 ? red : own).Add(length);
            }
            if (own.Count < MinFlashes) return;
            result.FlashCount = own.Count;
            result.FlashOwnMs = (int)Math.Round(Median(own));
            if (red.Count > 0) result.FlashRedlineMs = (int)Math.Round(Median(red));
            result.Notes.Add("At the limiter the strip flashes between the redline colour and its own colours, about " +
                             result.FlashOwnMs + " ms in its own colours" +
                             (result.FlashRedlineMs.HasValue ? " and " + result.FlashRedlineMs + " ms in the redline colour" : "") +
                             " (" + own.Count + " flashes seen). ATSR can't show that: its strip either stays in the redline " +
                             "colour or blinks it off, so nothing in the file was changed for it.");
        }

        /// <summary>A strip that stays lit above the redline says so, so a file claiming it blinks can be checked.</summary>
        private void ReportSolidRedline(bool[][] lit, ScreenLedResult result)
        {
            if (result.BlinkSeen || result.FlashOwnMs.HasValue || !result.RedlineRpm.HasValue) return;
            int above = _samples.Count(s => s.Rpm > result.RedlineRpm.Value);
            if (above > 30)
                result.Notes.Add("The lights stayed on above the redline in all " + above +
                                 " frames there, so the strip doesn't blink (redlineBlinkInterval 0).");
            else
                result.Notes.Add("The redline was never held long enough to check for blinking.");
        }

        private static double Median(List<double> values)
        {
            var v = values.OrderBy(x => x).ToList();
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0;
        }

        private static int RoundTo(double value, int step) => (int)(Math.Round(value / step) * step);
    }
}
