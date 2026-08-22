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
                if (EquivalentGivenNameWithSameRemainingTokens(currentExact, aliasExact, configuredPairs, out var aliasPair))
                    return Match("configured given-name equivalence " + aliasPair + " with identical remaining provider-alias tokens");
            }
            if (EquivalentGivenNameWithSameRemainingTokens(currentExact, proposedExact, configuredPairs, out var canonicalPair))
                return Match("configured given-name equivalence " + canonicalPair + " with identical remaining name tokens");
            return new PersonNameMatch { Compatible = false, Reason = "no configured or structural name compatibility" };
        }

        public static PersonNameMatch CompareIdentityEnvelope(string current, string canonical, IEnumerable<string> aliases, string configuredPairs)
        {
            var materializedAliases = (aliases ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var direct = Compare(current, canonical, materializedAliases, configuredPairs);
            if (direct.Compatible) return direct;

            var currentTokens = Tokens(current);
            var canonicalTokens = Tokens(canonical);
            if (currentTokens.Length < 2 || canonicalTokens.Length < 2) return direct;

            foreach (var alias in materializedAliases)
            {
                var aliasTokens = Tokens(alias);
                if (aliasTokens.Length < 2) continue;
                var canonicalGivenMatches = currentTokens[0] == canonicalTokens[0] || IsConfiguredPair(currentTokens[0], canonicalTokens[0], configuredPairs);
                var aliasFamilyMatches = currentTokens[currentTokens.Length - 1] == aliasTokens[aliasTokens.Length - 1];
                if (canonicalGivenMatches && aliasFamilyMatches)
                    return Match("provider identity envelope combines canonical given name '" + canonicalTokens[0] + "' with provider-alias family name '" + aliasTokens[aliasTokens.Length - 1] + "'");
            }
            return direct;
        }

        public static bool IsPlausibleLead(string current, string displayedName, string configuredPairs)
        {
            var left = Tokens(current); var right = Tokens(displayedName);
            if (left.Length < 2 || right.Length < 2) return ExactForm(current) == ExactForm(displayedName);
            if (left[0] == right[0] || left[left.Length - 1] == right[right.Length - 1]) return true;
            return IsConfiguredPair(left[0], right[0], configuredPairs) && left.Skip(1).SequenceEqual(right.Skip(1));
        }

        private static bool EquivalentGivenNameWithSameRemainingTokens(string leftValue, string rightValue, string configuredPairs, out string pair)
        {
            var left = Tokens(leftValue); var right = Tokens(rightValue); pair = null;
            if (left.Length < 2 || right.Length != left.Length || !left.Skip(1).SequenceEqual(right.Skip(1)) || !IsConfiguredPair(left[0], right[0], configuredPairs)) return false;
            pair = left[0] + "=" + right[0];
            return true;
        }

        private static string[] Tokens(string value) => ExactForm(value).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

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
