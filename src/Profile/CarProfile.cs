using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LovelyCarDataCapture.Profile
{
    /// <summary>A Lovely Car Data v2.0.0 car file.</summary>
    internal sealed class CarProfile
    {
        private static readonly string[] KnownKeys = { "carName", "carId", "carClass", "ledNumber", "redlineBlinkInterval", "ledColor", "ledRpm" };

        public string CarName = "";
        public string CarId = "";
        public string CarClass = "";
        public int LedNumber;
        public int RedlineBlinkInterval;
        /// <summary>Index 0 is the redline flash color, 1..N the strip.</summary>
        public List<string> LedColor = new List<string>();
        public List<string> GearOrder = new List<string>();
        /// <summary>Per gear: index 0 is the redline RPM, 1..N the LED thresholds (0 = unused).</summary>
        public Dictionary<string, int[]> LedRpm = new Dictionary<string, int[]>();
        /// <summary>Keys this tool doesn't edit (_schemaVersion, carSettings, …), written back unchanged.</summary>
        public JObject Extra = new JObject();

        public static CarProfile Parse(string json, ICollection<string> notes = null)
        {
            var obj = JObject.Parse(json);
            var p = new CarProfile
            {
                CarName = (string)obj["carName"] ?? "",
                CarId = (string)obj["carId"] ?? "",
                CarClass = (string)obj["carClass"] ?? "",
                LedNumber = obj["ledNumber"]?.Type == JTokenType.Integer ? (int)obj["ledNumber"] : 0,
            };

            var blink = obj["redlineBlinkInterval"];
            if (blink is JArray arr && arr.Count > 0)
            {
                notes?.Add("redlineBlinkInterval is a list in the repo file; only its first value is kept.");
                p.RedlineBlinkInterval = ToInt(arr[0]);
            }
            else if (blink != null) p.RedlineBlinkInterval = ToInt(blink);

            if (obj["ledColor"] is JArray colors) p.LedColor = colors.Select(c => (string)c ?? "").ToList();

            if (obj["ledRpm"] is JArray rpmArr && rpmArr.Count > 0 && rpmArr[0] is JObject gears)
            {
                foreach (var prop in gears.Properties())
                {
                    p.GearOrder.Add(prop.Name);
                    p.LedRpm[prop.Name] = prop.Value is JArray values ? values.Select(ToInt).ToArray() : new int[0];
                }
            }

            foreach (var prop in obj.Properties())
            {
                if (Array.IndexOf(KnownKeys, prop.Name) < 0) p.Extra[prop.Name] = prop.Value.DeepClone();
            }
            return p;
        }

        private static int ToInt(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return (int)Math.Round((double)t);
                case JTokenType.String:
                    return int.TryParse((string)t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
                default:
                    return 0;
            }
        }

        public CarProfile Clone() => Parse(ToJson());

        /// <summary>Makes every color/RPM list ledNumber + 1 long, padding with transparent/0.</summary>
        public void Normalize()
        {
            int len = LedNumber + 1;
            while (LedColor.Count < len) LedColor.Add("#00000000");
            if (LedColor.Count > len) LedColor.RemoveRange(len, LedColor.Count - len);
            foreach (var gear in GearOrder.ToList())
            {
                if (!LedRpm.TryGetValue(gear, out var row)) row = new int[0];
                if (row.Length != len)
                {
                    var resized = new int[len];
                    Array.Copy(row, resized, Math.Min(row.Length, len));
                    LedRpm[gear] = resized;
                }
            }
        }

        /// <summary>Adds a gear keeping the repo's R, N, 1, 2, … order.</summary>
        public void EnsureGear(string gear)
        {
            if (GearOrder.Contains(gear)) return;
            int rank = GearRank(gear);
            int at = GearOrder.FindIndex(g => GearRank(g) > rank);
            if (at < 0) GearOrder.Add(gear); else GearOrder.Insert(at, gear);
            LedRpm[gear] = new int[LedNumber + 1];
        }

        public static int GearRank(string gear)
        {
            if (gear == "R") return -1;
            if (gear == "N") return 0;
            return int.TryParse(gear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1000;
        }

        // Layout mirrors the repo's scripts/format_json.py: _schemaVersion first, compact arrays.
        public string ToJson()
        {
            var entries = new List<string>();
            if (Extra.TryGetValue("_schemaVersion", out var schema)) entries.Add("  \"_schemaVersion\": " + schema.ToString(Formatting.None));
            entries.Add("  \"carName\": " + Quote(CarName));
            entries.Add("  \"carId\": " + Quote(CarId));
            entries.Add("  \"carClass\": " + Quote(CarClass));
            entries.Add("  \"ledNumber\": " + LedNumber.ToString(CultureInfo.InvariantCulture));
            entries.Add("  \"redlineBlinkInterval\": " + RedlineBlinkInterval.ToString(CultureInfo.InvariantCulture));
            entries.Add("  \"ledColor\": [" + string.Join(",", LedColor.Select(Quote)) + "]");

            var gearLines = GearOrder.Select(g => "      " + Quote(g) + ": [" +
                string.Join(",", (LedRpm.TryGetValue(g, out var row) ? row : new int[0]).Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]");
            entries.Add("  \"ledRpm\": [\n    {\n" + (GearOrder.Count > 0 ? string.Join(",\n", gearLines) + "\n" : "") + "    }\n  ]");

            foreach (var prop in Extra.Properties())
            {
                if (prop.Name == "_schemaVersion") continue;
                entries.Add("  " + Quote(prop.Name) + ": " + prop.Value.ToString(Formatting.Indented).Replace("\r\n", "\n").Replace("\n", "\n  "));
            }
            return "{\n" + string.Join(",\n", entries) + "\n}\n";
        }

        private static string Quote(string value) => JsonConvert.ToString(value ?? "");
    }
}
