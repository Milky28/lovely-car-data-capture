namespace LovelyCarDataCapture.Capture
{
    /// <summary>Everything captured for one car in one game.</summary>
    internal sealed class CaptureSession
    {
        public CaptureSession(string gameName, string carId)
        {
            GameName = gameName ?? "";
            CarId = carId ?? "";
        }

        public string GameName { get; }
        public string CarId { get; }
        public string CarModel { get; private set; }
        public string CarClass { get; private set; }

        public RedlineCapture Redline { get; } = new RedlineCapture();
        public LedWindowCapture F1 { get; } = new LedWindowCapture(LedWindowCapture.F1LedCount);
        public IRacingShiftLightCapture IRacing { get; } = new IRacingShiftLightCapture();
        public ManualMarkCapture Marks { get; } = new ManualMarkCapture();
        public Screen.ScreenLedCapture Screen { get; } = new Screen.ScreenLedCapture();

        /// <summary>Samples skipped because the pit limiter was on (it drives its own LED patterns).</summary>
        public int PitLimiterSamples { get; set; }

        public void RecordCar(string carModel, string carClass)
        {
            if (!string.IsNullOrEmpty(carModel)) CarModel = carModel;
            if (!string.IsNullOrEmpty(carClass)) CarClass = carClass;
        }
    }
}

namespace LovelyCarDataCapture
{
    public class CaptureSettings
    {
        public int LedNumber { get; set; } = 12;
        public double FirstLedPercent { get; set; } = 72.0;
        public double LastLedPercent { get; set; } = 97.5;
        public int RoundRpmTo { get; set; } = 25;
        public string OutputFolder { get; set; } = "";
        /// <summary>Look the car up in the Lovely Car Data repo and build on its current file.</summary>
        public bool UseRepoFile { get; set; } = true;
        public string RepoBranch { get; set; } = "main";
        /// <summary>
        /// Also write the export to ATSR's Developer Mode folder (&lt;SimHub&gt;\_ATSR_DevelopmentData\rpm_data)
        /// so it can be tried on real hardware before submitting.
        /// </summary>
        public bool CopyToAtsrDeveloperFolder { get; set; }
    }
}
