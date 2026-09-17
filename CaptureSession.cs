using System;
using System.Collections.Generic;
using GameReaderCommon;

namespace LovelyCarDataCapture
{
    public class CaptureSettings
    {
        public int LedNumber { get; set; } = 12;
        public double FirstLedPercent { get; set; } = 72.0;
        public double LastLedPercent { get; set; } = 97.5;
        public int RoundRpmTo { get; set; } = 25;
        public string OutputFolder { get; set; } = "";
    }

    internal sealed class GearCapture
    {
        public double PeakRpm;
        public double RedlineRpm;
    }

    internal sealed class CaptureSession
    {
        public string GameName { get; }
        public string CarId { get; }
        public string CarModel { get; private set; }
        public string CarClass { get; private set; }
        public int MaxGears { get; private set; }
        public double CarRedlineRpm { get; private set; }
        public double CarMaxRpm { get; private set; }
        public Dictionary<string, GearCapture> Gears { get; } = new Dictionary<string, GearCapture>();

        public CaptureSession(string gameName, string carId)
        {
            GameName = gameName ?? "";
            CarId = carId ?? "";
        }

        // CarSettings_CurrentGearRedLineRPM resolves to SimHub's per-gear redline when enabled for this car, otherwise the car-wide redline.
        public void Record(StatusDataBase d) => Record(
            d.CarModel, d.CarClass, d.Gear, d.Rpms,
            d.CarSettings_CurrentGearRedLineRPM, d.CarSettings_RedLineRPM,
            Math.Max(d.CarSettings_MaxRPM, d.MaxRpm), d.CarSettings_MaxGears);

        // Runs on SimHub's telemetry hot path: no allocations beyond first sight of a gear, no throwing.
        public void Record(string carModel, string carClass, string gear, double rpm,
            double gearRedlineRpm, double carRedlineRpm, double maxRpm, int maxGears)
        {
            if (!string.IsNullOrEmpty(carModel)) CarModel = carModel;
            if (!string.IsNullOrEmpty(carClass)) CarClass = carClass;
            if (maxGears > MaxGears) MaxGears = maxGears;
            if (carRedlineRpm > 0) CarRedlineRpm = carRedlineRpm;
            if (maxRpm > CarMaxRpm) CarMaxRpm = maxRpm;

            if (string.IsNullOrEmpty(gear)) return;
            if (!Gears.TryGetValue(gear, out var g))
            {
                g = new GearCapture();
                Gears[gear] = g;
            }
            if (rpm > g.PeakRpm) g.PeakRpm = rpm;
            if (gearRedlineRpm > 0) g.RedlineRpm = gearRedlineRpm;
        }
    }
}
