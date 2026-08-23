using System.Globalization;
using System.Text;

namespace PersonCleaner.V2.Domain
{
    public static class TextNormalizer
    {
        public static string PersonName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
            var result = new StringBuilder(decomposed.Length);
            var pendingSpace = false;
            foreach (var character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(character))
                {
                    if (pendingSpace && result.Length > 0) result.Append(' ');
                    result.Append(char.ToLowerInvariant(character));
                    pendingSpace = false;
                }
                else pendingSpace = true;
            }
            return result.ToString();
        }
    }
}
