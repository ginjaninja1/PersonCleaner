using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PersonCleaner.Housekeeping
{
    public sealed class PersonNameMatch
    {
        public bool Compatible { get; set; }
        public string Reason { get; set; }
    }

    public static class PersonNameCompatibility
    {
        private static readonly Regex OptionalNickname = new Regex("\\s*(?:'[^']+'|\"[^\"]+\"|\\([^\\)]+\\))\\s*", RegexOptions.Compiled);
        private static readonly Regex NonName = new Regex(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

        public static string ExactForm(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var decomposed = value.Normalize(NormalizationForm.FormD);
            var b = new StringBuilder();
            foreach (var c in decomposed)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) b.Append(char.ToLowerInvariant(c));
            return string.Join(" ", NonName.Replace(b.ToString(), " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public static PersonNameMatch Compare(string current, string proposed, IEnumerable<string> aliases, string configuredPairs)
        {
            var currentExact = ExactForm(current); var proposedExact = ExactForm(proposed);
            if (currentExact == proposedExact) return Match("exact normalized name");
            var withoutNickname = ExactForm(OptionalNickname.Replace(proposed ?? string.Empty, " "));
            if (currentExact == withoutNickname) return Match("proposed canonical name becomes the current name after removing an optional quoted/parenthesized nickname");
            foreach (var alias in aliases ?? Enumerable.Empty<string>())
            {
                var aliasExact = ExactForm(alias);
                if (aliasExact == currentExact) return Match("current name is an exact provider alias");
                if (ExactForm(OptionalNickname.Replace(alias ?? string.Empty, " ")) == currentExact) return Match("current name matches a provider alias after optional nickname removal");
            }
            var left = currentExact.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var right = proposedExact.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (left.Length >= 2 && right.Length == left.Length && left.Skip(1).SequenceEqual(right.Skip(1)) && IsConfiguredPair(left[0], right[0], configuredPairs))
                return Match("configured given-name equivalence " + left[0] + "=" + right[0] + " with identical remaining name tokens");
            return new PersonNameMatch { Compatible = false, Reason = "no configured or structural name compatibility" };
        }

        private static bool IsConfiguredPair(string a, string b, string configuredPairs)
        {
            foreach (var entry in (configuredPairs ?? string.Empty).Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = entry.Split(new[] { '=' }, 2);
                if (pair.Length == 2 && ((ExactForm(pair[0]) == a && ExactForm(pair[1]) == b) || (ExactForm(pair[0]) == b && ExactForm(pair[1]) == a))) return true;
            }
            return false;
        }

        private static PersonNameMatch Match(string reason) => new PersonNameMatch { Compatible = true, Reason = reason };
    }
}
