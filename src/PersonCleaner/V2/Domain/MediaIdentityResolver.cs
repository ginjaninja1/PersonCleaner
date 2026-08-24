using System;
using System.Collections.Generic;
using System.Linq;

namespace PersonCleaner.V2.Domain
{
    /// <summary>
    /// Resolves provider media records through the transitive closure of every
    /// native and external identifier observed for those records.
    /// </summary>
    public static class MediaIdentityResolver
    {
        public static IReadOnlyDictionary<string, string> Resolve(IEnumerable<ProviderMediaIdentity> source)
        {
            var records = (source ?? Enumerable.Empty<ProviderMediaIdentity>()).ToList();
            var graph = new DisjointSet();
            var tokensByRecord = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var record in records)
            {
                var recordKey = RecordKey(record.Provider, record.MediaType, record.ProviderMediaId);
                var tokens = Tokens(record).Distinct(StringComparer.Ordinal).ToList();
                if (tokens.Count == 0) continue;
                tokensByRecord[recordKey] = tokens;
                for (var i = 1; i < tokens.Count; i++) graph.Union(tokens[0], tokens[i]);
            }

            var canonicalByRoot = new Dictionary<string, string>(StringComparer.Ordinal);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in tokensByRecord)
            {
                var root = graph.Find(entry.Value[0]);
                if (!canonicalByRoot.TryGetValue(root, out var canonical))
                {
                    canonical = graph.Component(root)
                        .OrderBy(TokenRank)
                        .ThenBy(x => x, StringComparer.Ordinal)
                        .First();
                    canonicalByRoot[root] = canonical;
                }
                result[entry.Key] = canonical;
            }
            return result;
        }

        public static string RecordKey(string provider, string mediaType, string providerMediaId)
        {
            return Required(provider) + "|" + Required(mediaType) + "|" + Required(providerMediaId);
        }

        private static IEnumerable<string> Tokens(ProviderMediaIdentity record)
        {
            var provider = Required(record.Provider).ToLowerInvariant();
            var mediaType = Required(record.MediaType).ToLowerInvariant();
            var nativeId = Required(record.ProviderMediaId);
            yield return Token(provider, mediaType, nativeId);
            foreach (var external in record.ExternalIds ?? new List<MediaExternalIdentity>())
            {
                if (string.IsNullOrWhiteSpace(external.Provider) || string.IsNullOrWhiteSpace(external.Id)) continue;
                yield return Token(external.Provider.ToLowerInvariant(), mediaType, external.Id);
            }
        }

        private static string Token(string provider, string mediaType, string id)
        {
            if (provider == ProviderNames.Imdb || provider == ProviderNames.Wikidata)
                return provider + ":" + id.Trim();
            return provider + ":" + mediaType + ":" + id.Trim();
        }

        private static int TokenRank(string token)
        {
            if (token.StartsWith(ProviderNames.Imdb + ":", StringComparison.Ordinal)) return 0;
            if (token.StartsWith(ProviderNames.Tmdb + ":", StringComparison.Ordinal)) return 1;
            if (token.StartsWith(ProviderNames.Tvdb + ":", StringComparison.Ordinal)) return 2;
            if (token.StartsWith(ProviderNames.Wikidata + ":", StringComparison.Ordinal)) return 3;
            return 4;
        }

        private static string Required(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Media identity fields cannot be empty.");
            return value.Trim();
        }
    }
}
