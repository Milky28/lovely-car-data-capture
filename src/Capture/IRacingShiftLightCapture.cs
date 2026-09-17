using System.Collections.Generic;
using System.Linq;

namespace LovelyCarDataCapture.Capture
{
    /// <summary>iRacing's shift-light RPMs for one gear (or the whole car).</summary>
    internal sealed class ShiftLightValues
    {
        public double First;
        public double Shift;
        public double Last;
        public double Blink;

        public bool IsValid => First > 0 && Last >= First;

        public ShiftLightValues Copy() => (ShiftLightValues)MemberwiseClone();
    }

    /// <summary>
    /// Records iRacing's shift-light RPMs. The PlayerCarSL* telemetry values follow the current gear
    /// on cars whose lights change per gear; DriverCarSL* from the session info is the car-wide set.
    /// </summary>
    internal sealed class IRacingShiftLightCapture
    {
        private readonly Dictionary<string, ShiftLightValues> _gears = new Dictionary<string, ShiftLightValues>();

        public ShiftLightValues CarWide { get; private set; }

        public bool HasData => CarWide != null || _gears.Count > 0;

        public IEnumerable<string> Gears => _gears.Keys;

        public void RecordCarWide(ShiftLightValues values)
        {
            if (values != null && values.IsValid) CarWide = values.Copy();
        }

        public void RecordGear(string gear, ShiftLightValues values)
        {
            if (string.IsNullOrEmpty(gear) || values == null || !values.IsValid) return;
            _gears[gear] = values.Copy();
        }

        public ShiftLightValues ForGear(string gear) => _gears.TryGetValue(gear, out var v) ? v : null;

        /// <summary>True when every recorded gear has the same values, i.e. the car doesn't change lights per gear.</summary>
        public bool SameInAllGears => _gears.Values.Select(v => (v.First, v.Shift, v.Last, v.Blink)).Distinct().Count() <= 1;
    }
}
