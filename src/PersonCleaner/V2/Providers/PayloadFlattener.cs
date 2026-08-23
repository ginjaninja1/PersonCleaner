using MediaBrowser.Model.Serialization;
using PersonCleaner.V2.Domain;
using PersonCleaner.V2.Storage;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PersonCleaner.V2.Providers
{
    internal sealed class PayloadFlattener
    {
        private static readonly HashSet<string> TvdbRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Actor", "Guest Star", "Director", "Writer", "Screenplay", "Producer", "Executive Producer", "Creator", "Showrunner" };
        private readonly IJsonSerializer json;
        public PayloadFlattener(IJsonSerializer json) { this.json = json; }

        public FlattenedMedia Media(QueueItem item, string payload)
        {
            return item.Provider == ProviderNames.Tmdb ? TmdbMedia(item, payload) : TvdbMedia(item, payload);
        }

        public FlattenedPerson Person(QueueItem item, string payload)
        {
            return item.Provider == ProviderNames.Tmdb ? TmdbPerson(item, payload) : TvdbPerson(item, payload);
        }

        private FlattenedMedia TmdbMedia(QueueItem item, string payload)
        {
            var source = json.DeserializeFromString<TmdbMedia>(payload) ?? new TmdbMedia();
            var result = new FlattenedMedia { Provider = ProviderNames.Tmdb, MediaType = item.MediaType, ProviderMediaId = item.ProviderId, Name = source.title ?? source.name };
            Add(result.ExternalIds, ProviderNames.Imdb, source.external_ids?.imdb_id);
            Add(result.ExternalIds, ProviderNames.Tvdb, source.external_ids?.tvdb_id);
            Add(result.ExternalIds, ProviderNames.Wikidata, source.external_ids?.wikidata_id);
            var credits = item.MediaType == MediaTypes.Series ? source.aggregate_credits : source.credits;
            foreach (var cast in credits?.cast ?? new List<TmdbCredit>())
            {
                var role = string.Join(" / ", (cast.roles ?? new List<TmdbRole>()).Select(x => x.character).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(3));
                result.Credits.Add(new ProviderCredit { ProviderPersonId = cast.id.ToString(), PersonName = cast.name, Role = string.IsNullOrWhiteSpace(role) ? cast.character ?? "Actor" : "Actor: " + role });
            }
            foreach (var crew in credits?.crew ?? new List<TmdbCredit>())
            {
                var jobs = (crew.jobs ?? new List<TmdbJob>()).Select(x => x.job).Concat(new[] { crew.job }).Where(IsScreenCrewRole).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var job in jobs) result.Credits.Add(new ProviderCredit { ProviderPersonId = crew.id.ToString(), PersonName = crew.name, Role = job });
            }
            return result;
        }

        private FlattenedPerson TmdbPerson(QueueItem item, string payload)
        {
            var source = json.DeserializeFromString<TmdbPerson>(payload) ?? new TmdbPerson();
            var result = new FlattenedPerson { Provider = ProviderNames.Tmdb, ProviderPersonId = item.ProviderId, Name = source.name, Birthday = source.birthday, Aliases = source.also_known_as ?? new List<string>() };
            Add(result.ExternalIds, ProviderNames.Imdb, source.external_ids?.imdb_id);
            Add(result.ExternalIds, ProviderNames.Tvdb, source.external_ids?.tvdb_id);
            Add(result.ExternalIds, ProviderNames.Wikidata, source.external_ids?.wikidata_id);
            return result;
        }

        private FlattenedMedia TvdbMedia(QueueItem item, string payload)
        {
            var source = json.DeserializeFromString<TvdbResponse<TvdbEntity>>(payload)?.data ?? new TvdbEntity();
            var result = new FlattenedMedia { Provider = ProviderNames.Tvdb, MediaType = item.MediaType, ProviderMediaId = item.ProviderId, Name = source.name };
            foreach (var remote in source.remoteIds ?? new List<TvdbRemoteId>()) Add(result.ExternalIds, RemoteProvider(remote.sourceName), remote.id);
            foreach (var credit in (source.characters ?? new List<TvdbCharacter>()).Where(x => x.peopleId > 0 && TvdbRoles.Contains((x.peopleType ?? string.Empty).Trim())))
                result.Credits.Add(new ProviderCredit { ProviderPersonId = credit.peopleId.ToString(), PersonName = credit.personName, Role = credit.peopleType + (string.IsNullOrWhiteSpace(credit.name) ? string.Empty : ": " + credit.name) });
            return result;
        }

        private FlattenedPerson TvdbPerson(QueueItem item, string payload)
        {
            var source = json.DeserializeFromString<TvdbResponse<TvdbEntity>>(payload)?.data ?? new TvdbEntity();
            var result = new FlattenedPerson { Provider = ProviderNames.Tvdb, ProviderPersonId = item.ProviderId, Name = source.name, Birthday = source.birth, Aliases = (source.aliases ?? new List<TvdbAlias>()).Select(x => x.name).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() };
            foreach (var remote in source.remoteIds ?? new List<TvdbRemoteId>()) Add(result.ExternalIds, RemoteProvider(remote.sourceName), remote.id);
            return result;
        }

        private static bool IsScreenCrewRole(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var role = value.Trim();
            return role.IndexOf("Director", StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("Writer", StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("Screenplay", StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("Producer", StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("Creator", StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("Showrunner", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RemoteProvider(string source)
        {
            var value = (source ?? string.Empty).ToLowerInvariant();
            if (value.Contains("imdb")) return ProviderNames.Imdb;
            if (value.Contains("movie") || value.Contains("tmdb")) return ProviderNames.Tmdb;
            if (value.Contains("wiki")) return ProviderNames.Wikidata;
            if (value.Contains("tvdb")) return ProviderNames.Tvdb;
            return string.Empty;
        }

        private static void Add(IDictionary<string, string> target, string provider, string id)
        { if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(id)) target[provider] = id.Trim(); }
    }
}
