using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LovelyCarDataCapture.Util;
using Newtonsoft.Json.Linq;

namespace LovelyCarDataCapture.Profile
{
    internal sealed class LocalProfileOverrideResult
    {
        public readonly List<int> ColorSlots = new List<int>();
        public int? BlinkInterval;
    }

    // Keep confirmed wheel adjustments separate from generated exports. Reusing the whole last
    // export would also preserve guesses and stale RPM measurements from an incomplete drive.
    internal static class LocalProfileOverrides
    {
        public static LocalProfileOverrideResult Apply(CarProfile profile, string game, string carId, string json, List<string> notes)
        {
            var applied = new LocalProfileOverrideResult();
            if (json == null) return applied;
            var data = JObject.Parse(json);
            var allowed = new[] { "game", "carId", "ledNumber", "ledColor", "redlineBlinkInterval" };
            if (data.Properties().Any(p => !allowed.Contains(p.Name)))
                throw new InvalidDataException("Local overrides contain an unknown field. Only colors and blink interval can be changed.");
            if (data["game"]?.Type != JTokenType.String ||
                !string.Equals((string)data["game"], Slug.Make(game), StringComparison.OrdinalIgnoreCase) ||
                data["carId"]?.Type != JTokenType.String ||
                !string.Equals((string)data["carId"], carId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Local overrides belong to a different car or game.");
            if (data["ledNumber"]?.Type != JTokenType.Integer || (int)data["ledNumber"] != profile.LedNumber)
                throw new InvalidDataException("Local overrides have a different LED count. Check the strip layout before reusing them.");

            var colors = new Dictionary<int, string>();
            if (data["ledColor"] != null)
            {
                if (!(data["ledColor"] is JObject entries))
                    throw new InvalidDataException("Local override ledColor must contain LED numbers and #AARRGGBB colors.");
                foreach (var entry in entries.Properties())
                {
                    if (!int.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int slot) ||
                        slot < 0 || slot > profile.LedNumber || colors.ContainsKey(slot))
                        throw new InvalidDataException("Local overrides contain an invalid or repeated LED number: " + entry.Name);
                    var color = entry.Value.Type == JTokenType.String ? (string)entry.Value : null;
                    if (color == null || color.Length != 9 || color[0] != '#' || !color.Skip(1).All(Uri.IsHexDigit))
                        throw new InvalidDataException("Local override LED " + slot + " needs a #AARRGGBB color.");
                    colors.Add(slot, color.ToUpperInvariant());
                }
            }
            int? blink = null;
            if (data["redlineBlinkInterval"] != null)
            {
                var value = data["redlineBlinkInterval"];
                if (value.Type != JTokenType.Integer || (long)value < 0 || (long)value > int.MaxValue)
                    throw new InvalidDataException("Local override blink interval must be a nonnegative integer in milliseconds.");
                blink = (int)value;
            }
            if (colors.Count == 0 && !blink.HasValue)
                throw new InvalidDataException("Local overrides contain no colors or blink interval.");

            // Validate the complete file before changing anything, including the report.
            foreach (var color in colors) profile.LedColor[color.Key] = color.Value;
            if (blink.HasValue) profile.RedlineBlinkInterval = blink.Value;
            applied.ColorSlots.AddRange(colors.Keys.OrderBy(k => k));
            applied.BlinkInterval = blink;
            notes.Add("Applied confirmed local overrides: " +
                string.Join(", ", colors.Select(c => "LED " + c.Key + " " + c.Value)
                    .Concat(blink.HasValue ? new[] { "blink interval " + blink.Value + " ms" } : new string[0])) +
                ". These take precedence over the repo and measured colors or blink timing; RPM thresholds follow the capture and retained-gear rules.");
            return applied;
        }
    }
}
