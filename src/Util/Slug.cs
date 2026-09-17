using System.Globalization;
using System.Text;

namespace LovelyCarDataCapture.Util
{
    internal static class Slug
    {
        // Same rules as the Lovely Car Data README's nameCleaner: lowercase, strip accents,
        // non-alphanumerics -> single hyphens, trimmed.
        public static string Make(string value)
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
    }
}
