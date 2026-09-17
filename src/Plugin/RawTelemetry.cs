using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using LovelyCarDataCapture.Capture;

namespace LovelyCarDataCapture.Plugin
{
    internal struct F1Sample
    {
        public F1Sample(int gear, int rpm, int bits)
        {
            Gear = gear;
            Rpm = rpm;
            Bits = bits;
        }

        public readonly int Gear;
        public readonly int Rpm;
        public readonly int Bits;
    }

    /// <summary>Reads game-specific LED data from SimHub's raw game data object (GameRawData).</summary>
    internal static class RawTelemetry
    {
        // F1: each game year has its own CodemastersReader.F12xxx namespace with an identical
        // PlayerCarTelemetryData struct, so it's read by name and compiled once per type instead
        // of referencing every year's types. Null = that raw type has no rev-light bits.
        private static readonly Dictionary<Type, Func<object, F1Sample>> F1Readers = new Dictionary<Type, Func<object, F1Sample>>();

        public static bool TryReadF1(object raw, out F1Sample sample)
        {
            sample = default(F1Sample);
            if (raw == null) return false;
            var type = raw.GetType();
            if (!F1Readers.TryGetValue(type, out var reader))
            {
                reader = BuildF1Reader(type);
                F1Readers[type] = reader;
            }
            if (reader == null) return false;
            sample = reader(raw);
            return true;
        }

        private static Func<object, F1Sample> BuildF1Reader(Type rawType)
        {
            var player = rawType.GetProperty("PlayerCarTelemetryData", BindingFlags.Public | BindingFlags.Instance);
            if (player == null) return null;
            var t = player.PropertyType;
            var gear = t.GetField("m_gear");
            var rpm = t.GetField("m_engineRPM");
            var bits = t.GetField("m_revLightsBitValue");
            if (gear == null || rpm == null || bits == null) return null;

            var obj = Expression.Parameter(typeof(object), "raw");
            var telemetry = Expression.Variable(t, "telemetry");
            var ctor = typeof(F1Sample).GetConstructor(new[] { typeof(int), typeof(int), typeof(int) });
            var body = Expression.Block(new[] { telemetry },
                Expression.Assign(telemetry, Expression.Property(Expression.Convert(obj, rawType), player)),
                Expression.New(ctor,
                    Expression.Convert(Expression.Field(telemetry, gear), typeof(int)),
                    Expression.Convert(Expression.Field(telemetry, rpm), typeof(int)),
                    Expression.Convert(Expression.Field(telemetry, bits), typeof(int))));
            return Expression.Lambda<Func<object, F1Sample>>(body, obj).Compile();
        }

        public static string F1GearName(int gear) =>
            gear < 0 ? "R" : gear == 0 ? "N" : gear.ToString(CultureInfo.InvariantCulture);

        // iRacing: SimHub's raw object is iRacingSDK.DataSample.
        public static bool TryReadIRacing(object raw, out ShiftLightValues perGear, out ShiftLightValues carWide)
        {
            perGear = null;
            carWide = null;
            if (!(raw is iRacingSDK.DataSample sample)) return false;

            var driver = sample.SessionData?.DriverInfo;
            if (driver != null)
            {
                carWide = new ShiftLightValues
                {
                    First = driver.DriverCarSLFirstRPM,
                    Shift = driver.DriverCarSLShiftRPM,
                    Last = driver.DriverCarSLLastRPM,
                    Blink = driver.DriverCarSLBlinkRPM,
                };
            }

            var telemetry = sample.Telemetry;
            if (telemetry != null &&
                TryGetDouble(telemetry, "PlayerCarSLFirstRPM", out var first) &&
                TryGetDouble(telemetry, "PlayerCarSLLastRPM", out var last))
            {
                TryGetDouble(telemetry, "PlayerCarSLShiftRPM", out var shift);
                TryGetDouble(telemetry, "PlayerCarSLBlinkRPM", out var blink);
                perGear = new ShiftLightValues { First = first, Shift = shift, Last = last, Blink = blink };
            }
            return true;
        }

        private static bool TryGetDouble(Dictionary<string, object> values, string key, out double value)
        {
            value = 0;
            if (!values.TryGetValue(key, out var o) || o == null) return false;
            try
            {
                value = Convert.ToDouble(o, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                return false;
            }
        }
    }
}
