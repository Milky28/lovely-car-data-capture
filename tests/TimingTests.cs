using LovelyCarDataCapture.Capture;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static void TimingHistoryInterpolatesAtAcquisitionTime()
        {
            var history = new TelemetryHistory();
            history.Reset(7);
            history.Add(7, "Game", "Car", "3", 5000, 1000);
            history.Add(7, "Game", "Car", "3", 6000, 1100);

            TelemetrySample sample;
            Equal(TelemetrySampleStatus.Ok, history.TrySample(7, "Game", "Car", 1050, out sample), "interpolation status");
            Equal("3", sample.Gear, "interpolated gear");
            Equal(5500, sample.Rpm, "interpolated RPM");
            Equal(1050L, sample.TimeMs, "sample time");
            // Telemetry may advance while the detector works. It must not move the picture's RPM.
            history.Add(7, "Game", "Car", "3", 7000, 1200);
            Equal(TelemetrySampleStatus.Ok, history.TrySample(7, "Game", "Car", 1050, out sample), "delayed analysis status");
            Equal(5500, sample.Rpm, "detector latency does not shift the acquisition RPM");
            Check(sample.Interpolated, "bracketed acquisition is interpolated");
        }

        private static void TimingHistoryRejectsGearAndSessionCrossings()
        {
            var history = new TelemetryHistory();
            history.Reset(7);
            history.Add(7, "Game", "Car", "3", 5000, 1000);
            history.Add(7, "Game", "Car", "4", 6000, 1100);

            TelemetrySample sample;
            Equal(TelemetrySampleStatus.GearChanged, history.TrySample(7, "Game", "Car", 1050, out sample), "gear crossing status");
            Equal(TelemetrySampleStatus.SessionChanged, history.TrySample(6, "Game", "Car", 1050, out sample), "session crossing status");
        }

        private static void TimingHistoryDropsStaleFramesWithoutWaiting()
        {
            var history = new TelemetryHistory();
            history.Reset(7);
            history.Add(7, "Game", "Car", "3", 5000, 1000);

            TelemetrySample sample;
            Equal(TelemetrySampleStatus.Ok, history.TrySample(7, "Game", "Car", 1150, out sample), "recent sample status");
            Equal(5000, sample.Rpm, "recent sample RPM");
            Equal(150L, sample.TelemetryAgeMs, "held sample preserves telemetry age");
            Check(!sample.Interpolated, "a preceding value is not labelled interpolated");
            Equal(TelemetrySampleStatus.Stale, history.TrySample(7, "Game", "Car", 1000 + TelemetryHistory.MaxAgeMs + 1, out sample), "stale sample status");

            history.Reset(8);
            Equal(TelemetrySampleStatus.NoTelemetry, history.TrySample(8, "Game", "Car", 1200, out sample), "reset clears history");
            history.Add(8, "Game", "Car", "3", 6000, 1300);
            history.Clear(8);
            Equal(TelemetrySampleStatus.NoTelemetry, history.TrySample(8, "Game", "Car", 1300, out sample), "pause clears history");
            history.Add(7, "Game", "Car", "3", 5000, 1300);
            Equal(TelemetrySampleStatus.NoTelemetry, history.TrySample(8, "Game", "Car", 1300, out sample), "old-session arrivals are ignored");
        }
    }
}
