using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LovelyCarDataCapture
{
    internal static class CarProfileBuilder
    {
        private const string Green = "#FF00FF00";
        private const string Yellow = "#FFFFFF00";
        private const string Red = "#FFFF0000";
        private const string Blue = "#FF0000FF";

        // Output mirrors the repo's format_json.py layout (compact arrays, R/N first) so it imports cleanly into the builder.
        public static string Build(CaptureSession s, CaptureSettings cfg, ICollection<string> notes)
        {
            int leds = Math.Max(1, cfg.LedNumber);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"carName\": ").Append(Quote(string.IsNullOrEmpty(s.CarModel) ? s.CarId : s.CarModel)).Append(",\n");
            sb.Append("  \"carId\": ").Append(Quote(s.CarId)).Append(",\n");
            sb.Append("  \"carClass\": ").Append(Quote(s.CarClass ?? "")).Append(",\n");
            sb.Append("  \"ledNumber\": ").Append(leds).Append(",\n");
            sb.Append("  \"redlineBlinkInterval\": 0,\n");
            sb.Append("  \"ledColor\": [").Append(string.Join(",", Colors(leds).Select(Quote))).Append("],\n");
            sb.Append("  \"ledRpm\": [\n    {\n");

            var gears = GearOrder(s);
            for (int i = 0; i < gears.Count; i++)
            {
                var redline = ResolveRedline(s, gears[i], out var source);
                notes.Add($"gear {gears[i]}: redline {redline:0} rpm ({source})");
                sb.Append("      ").Append(Quote(gears[i])).Append(": [")
                  .Append(string.Join(",", Breakpoints(redline, leds, cfg).Select(v => v.ToString(CultureInfo.InvariantCulture))))
                  .Append(']').Append(i < gears.Count - 1 ? ",\n" : "\n");
            }

            sb.Append("    }\n  ]\n}\n");
            return sb.ToString();
        }

        private static List<string> GearOrder(CaptureSession s)
        {
            int topGear = s.MaxGears;
            foreach (var key in s.Gears.Keys)
            {
                if (int.TryParse(key, out var n) && n > topGear) topGear = n;
            }

            var order = new List<string> { "R", "N" };
            for (int i = 1; i <= topGear; i++) order.Add(i.ToString(CultureInfo.InvariantCulture));
            order.AddRange(s.Gears.Keys.Where(k => !order.Contains(k)));
            return order;
        }

        private static double ResolveRedline(CaptureSession s, string gear, out string source)
        {
            if (s.Gears.TryGetValue(gear, out var g) && g.RedlineRpm > 0) { source = "SimHub redline, sampled in gear"; return g.RedlineRpm; }
            if (s.CarRedlineRpm > 0) { source = "SimHub car redline, gear not driven"; return s.CarRedlineRpm; }
            if (s.CarMaxRpm > 0) { source = "max RPM fallback"; return s.CarMaxRpm; }

            var peak = s.Gears.Values.Select(x => x.PeakRpm).DefaultIfEmpty(0).Max();
            source = peak > 0 ? "highest observed RPM fallback" : "NO RPM DATA - fill in manually";
            return peak;
        }

        // Index 0 is the redline (RL) value; LEDs 1..N spread evenly between FirstLedPercent and LastLedPercent of redline.
        private static IEnumerable<int> Breakpoints(double redline, int leds, CaptureSettings cfg)
        {
            yield return RoundTo(redline, cfg.RoundRpmTo);
            for (int i = 1; i <= leds; i++)
            {
                double t = leds == 1 ? 1.0 : (i - 1) / (double)(leds - 1);
                double pct = cfg.FirstLedPercent + (cfg.LastLedPercent - cfg.FirstLedPercent) * t;
                yield return RoundTo(redline * pct / 100.0, cfg.RoundRpmTo);
            }
        }

        private static IEnumerable<string> Colors(int leds)
        {
            yield return Blue;
            for (int i = 1; i <= leds; i++)
            {
                int band = (i - 1) * 3 / leds;
                yield return band == 0 ? Green : band == 1 ? Yellow : Red;
            }
        }

        private static int RoundTo(double value, int step)
        {
            if (step <= 1) return (int)Math.Round(value);
            return (int)(Math.Round(value / step) * step);
        }

        // Same rules as the README's nameCleaner: lowercase, strip accents, non-alphanumerics -> single hyphens, trimmed.
        public static string Slugify(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var decomposed = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                var c = char.ToLowerInvariant(ch);
                bool alnum = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (alnum) sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
            }
            return sb.ToString().Trim('-');
        }

        private static string Quote(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in value ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
