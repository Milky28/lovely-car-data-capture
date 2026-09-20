using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace LovelyCarDataCapture.Capture
{
    /// <summary>A monotonic process clock shared by telemetry and screen capture.</summary>
    internal static class CaptureClock
    {
        private static readonly long Started = Stopwatch.GetTimestamp();
        private static readonly double MillisecondsPerTick = 1000.0 / Stopwatch.Frequency;

        public static long NowMilliseconds
        {
            get { return (long)((Stopwatch.GetTimestamp() - Started) * MillisecondsPerTick); }
        }
    }

    internal struct TelemetrySample
    {
        public string Gear;
        public int Rpm;
        public long TimeMs;
        /// <summary>True when RPM was interpolated between two telemetry arrivals.</summary>
        public bool Interpolated;
        /// <summary>Age of the source telemetry when interpolation was unavailable.</summary>
        public long TelemetryAgeMs;
    }

    internal enum TelemetrySampleStatus
    {
        Ok,
        NoTelemetry,
        Stale,
        GearChanged,
        SessionChanged,
    }

    /// <summary>
    /// Keeps just enough recent telemetry to match a screen frame to the RPM at its acquisition time.
    /// It has no SimHub dependency so interpolation and its edge cases can be checked in isolation.
    /// </summary>
    internal sealed class TelemetryHistory
    {
        // A few seconds of updates covers a slow screen thread without retaining an entire capture.
        private const int MaxSamples = 128;
        // An old value is worse than dropping a frame: it can move a light threshold to another sweep.
        internal const long MaxAgeMs = 250;

        private struct Entry
        {
            public long SessionId;
            public string Game;
            public string Car;
            public string Gear;
            public double Rpm;
            public long TimeMs;
        }

        private readonly object _lock = new object();
        private readonly List<Entry> _entries = new List<Entry>(MaxSamples);
        private long _sessionId;

        public void Reset(long sessionId)
        {
            lock (_lock)
            {
                _sessionId = sessionId;
                _entries.Clear();
            }
        }

        /// <summary>Drop history without changing the capture generation, used for pause and pit limiter gaps.</summary>
        public void Clear(long sessionId)
        {
            lock (_lock)
            {
                if (_sessionId != sessionId) return;
                _entries.Clear();
            }
        }

        public void Add(long sessionId, string game, string car, string gear, double rpm, long timeMs)
        {
            if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(car) || string.IsNullOrEmpty(gear) || rpm <= 0) return;

            lock (_lock)
            {
                if (_sessionId != sessionId) return;
                if (_entries.Count > 0 && timeMs < _entries[_entries.Count - 1].TimeMs) return;
                if (_entries.Count == MaxSamples) _entries.RemoveAt(0);
                _entries.Add(new Entry
                {
                    SessionId = sessionId,
                    Game = game,
                    Car = car,
                    Gear = gear,
                    Rpm = rpm,
                    TimeMs = timeMs,
                });
            }
        }

        /// <summary>
        /// Samples without waiting for a future update. If both sides exist, they must be in the same
        /// gear and close enough to interpolate. A recent preceding sample is accepted when the next
        /// telemetry update has not arrived yet.
        /// </summary>
        public TelemetrySampleStatus TrySample(long sessionId, string game, string car, long timeMs, out TelemetrySample sample)
        {
            sample = default(TelemetrySample);
            lock (_lock)
            {
                if (_sessionId != sessionId) return TelemetrySampleStatus.SessionChanged;
                if (_entries.Count == 0) return TelemetrySampleStatus.NoTelemetry;

                Entry before = default(Entry), after = default(Entry);
                bool haveBefore = false, haveAfter = false;
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    var e = _entries[i];
                    if (e.SessionId != sessionId || !string.Equals(e.Game, game, StringComparison.Ordinal) ||
                        !string.Equals(e.Car, car, StringComparison.Ordinal)) continue;
                    if (e.TimeMs <= timeMs)
                    {
                        before = e;
                        haveBefore = true;
                        break;
                    }
                    after = e;
                    haveAfter = true;
                }

                if (!haveBefore)
                    return TelemetrySampleStatus.NoTelemetry;

                long beforeAge = timeMs - before.TimeMs;
                if (haveAfter)
                {
                    long afterAge = after.TimeMs - timeMs;
                    if (beforeAge > MaxAgeMs || afterAge > MaxAgeMs) return TelemetrySampleStatus.Stale;
                    if (!string.Equals(before.Gear, after.Gear, StringComparison.Ordinal))
                        return TelemetrySampleStatus.GearChanged;

                    double fraction = (double)(timeMs - before.TimeMs) / Math.Max(1L, after.TimeMs - before.TimeMs);
                    sample = new TelemetrySample
                    {
                        Gear = before.Gear,
                        Rpm = (int)Math.Round(before.Rpm + (after.Rpm - before.Rpm) * fraction),
                        TimeMs = timeMs,
                        Interpolated = true,
                        TelemetryAgeMs = Math.Min(beforeAge, afterAge),
                    };
                    return TelemetrySampleStatus.Ok;
                }

                if (beforeAge > MaxAgeMs) return TelemetrySampleStatus.Stale;
                sample = new TelemetrySample
                {
                    Gear = before.Gear,
                    Rpm = (int)Math.Round(before.Rpm),
                    TimeMs = timeMs,
                    Interpolated = false,
                    TelemetryAgeMs = beforeAge,
                };
                return TelemetrySampleStatus.Ok;
            }
        }
    }
}
