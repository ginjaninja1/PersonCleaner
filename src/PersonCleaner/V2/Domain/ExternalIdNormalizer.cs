using System;

namespace PersonCleaner.V2.Domain
{
    public static class ExternalIdNormalizer
    {
        public static bool TryPersonId(string sourceName, string value, out string provider, out string normalized)
        {
            return TryId(sourceName, value, true, out provider, out normalized);
        }

        public static bool TryMediaId(string sourceName, string value, out string provider, out string normalized)
        {
            return TryId(sourceName, value, false, out provider, out normalized);
        }

        private static bool TryId(string sourceName, string value, bool person, out string provider, out string normalized)
        {
            provider = Provider(sourceName);
            normalized = (value ?? string.Empty).Trim();
            if (provider.Length == 0 || normalized.Length == 0) return false;

            switch (provider)
            {
                case ProviderNames.Imdb:
                    var prefix = person ? "nm" : "tt";
                    if (!PrefixedDigits(normalized, prefix)) return false;
                    normalized = prefix + normalized.Substring(2);
                    return true;
                case ProviderNames.Wikidata:
                    if (normalized.Length < 2 || char.ToUpperInvariant(normalized[0]) != 'Q' || !Digits(normalized, 1)) return false;
                    normalized = "Q" + normalized.Substring(1);
                    return true;
                case ProviderNames.Tmdb:
                case ProviderNames.Tvdb:
                    return Digits(normalized, 0);
                default:
                    return false;
            }
        }

        private static string Provider(string sourceName)
        {
            var source = (sourceName ?? string.Empty).Trim().ToLowerInvariant();
            switch (source)
            {
                case "imdb":
                case "imdb.com": return ProviderNames.Imdb;
                case "wikidata":
                case "wikidata.org": return ProviderNames.Wikidata;
                case "tmdb":
                case "themoviedb":
                case "themoviedb.com": return ProviderNames.Tmdb;
                case "tvdb":
                case "thetvdb":
                case "thetvdb.com": return ProviderNames.Tvdb;
                default: return string.Empty;
            }
        }

        private static bool PrefixedDigits(string value, string prefix)
        {
            return value.Length > 2 && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Digits(value, 2);
        }

        private static bool Digits(string value, int start)
        {
            if (value.Length <= start) return false;
            for (var index = start; index < value.Length; index++)
                if (!char.IsDigit(value[index])) return false;
            return true;
        }
    }
}
