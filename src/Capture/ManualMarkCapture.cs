using System.Collections.Generic;
using System.Linq;

namespace LovelyCarDataCapture.Capture
{
    /// <summary>
    /// For games that don't report their LEDs: the driver revs slowly and presses a button each time the
    /// next in-game light comes on (and once when the redline flash starts). Each press records the RPM
    /// in the current gear.
    /// </summary>
    internal sealed class ManualMarkCapture
    {
        private readonly Dictionary<string, List<int>> _leds = new Dictionary<string, List<int>>();
        private readonly Dictionary<string, int> _redlines = new Dictionary<string, int>();
        private readonly Stack<(string gear, bool redline, int previousRedline)> _history = new Stack<(string, bool, int)>();

        public bool HasData => _leds.Values.Any(l => l.Count > 0) || _redlines.Count > 0;

        public IEnumerable<string> Gears => _leds.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).Union(_redlines.Keys);

        /// <summary>Records the next LED in this gear; returns its number (1-based), or 0 if nothing was recorded.</summary>
        public int MarkLed(string gear, int rpm)
        {
            if (string.IsNullOrEmpty(gear) || rpm <= 0) return 0;
            if (!_leds.TryGetValue(gear, out var list))
            {
                list = new List<int>();
                _leds[gear] = list;
            }
            list.Add(rpm);
            _history.Push((gear, false, 0));
            return list.Count;
        }

        public bool MarkRedline(string gear, int rpm)
        {
            if (string.IsNullOrEmpty(gear) || rpm <= 0) return false;
            _history.Push((gear, true, _redlines.TryGetValue(gear, out var previous) ? previous : 0));
            _redlines[gear] = rpm;
            return true;
        }

        /// <summary>Removes the most recent mark in any gear; returns a description of what was removed, or null.</summary>
        public string Undo()
        {
            if (_history.Count == 0) return null;
            var (gear, redline, previousRedline) = _history.Pop();
            if (redline)
            {
                if (previousRedline > 0) _redlines[gear] = previousRedline; else _redlines.Remove(gear);
                return "gear " + gear + " redline mark";
            }
            var list = _leds[gear];
            list.RemoveAt(list.Count - 1);
            return "gear " + gear + " LED " + (list.Count + 1) + " mark";
        }

        public IReadOnlyList<int> LedMarks(string gear) => _leds.TryGetValue(gear, out var list) ? list : (IReadOnlyList<int>)new int[0];

        public int? Redline(string gear) => _redlines.TryGetValue(gear, out var rpm) ? rpm : (int?)null;

        public int MostLedMarks => _leds.Values.Select(l => l.Count).DefaultIfEmpty(0).Max();
    }
}
