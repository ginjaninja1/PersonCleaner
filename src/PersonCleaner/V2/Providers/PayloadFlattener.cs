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
        public const int MaterializerVersion = 3;
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
            if (source.id > 0) AddMediaId(result.ExternalIds, ProviderNames.Tmdb, source.id.ToString());
            AddMediaId(result.ExternalIds, ProviderNames.Imdb, source.external_ids?.imdb_id);
            AddMediaId(result.ExternalIds, ProviderNames.Tvdb, source.external_ids?.tvdb_id);
            AddMediaId(result.ExternalIds, ProviderNames.Wikidata, source.external_ids?.wikidata_id);
            var credits = item.MediaType == MediaTypes.Series ? source.aggregate_credits : source.credits;
            var castCredits = (credits?.cast ?? new List<TmdbCredit>())
                .Concat(credits?.guest_stars ?? new List<TmdbCredit>())
                .Concat(item.MediaType == MediaTypes.Episode ? source.guest_stars ?? new List<TmdbCredit>() : new List<TmdbCredit>())
                .GroupBy(x => x.id + "|" + (x.character ?? string.Join("/", (x.roles ?? new List<TmdbRole>()).Select(y => y.character))), StringComparer.Ordinal)
                .Select(x => x.First());
            foreach (var cast in castCredits)
            {
                var role = string.Join(" / ", (cast.roles ?? new List<TmdbRole>()).Select(x => x.character).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(3));
                var roleName = string.IsNullOrWhiteSpace(role) ? cast.character : role;
                result.Credits.Add(new ProviderCredit { ProviderPersonId = cast.id.ToString(), PersonName = cast.name, Role = string.IsNullOrWhiteSpace(roleName) ? "Actor" : "Actor: " + roleName, RoleCategory = "Actor", RoleName = roleName });
            }
            var crewCredits = (credits?.crew ?? new List<TmdbCredit>())
                .Concat(item.MediaType == MediaTypes.Episode ? source.crew ?? new List<TmdbCredit>() : new List<TmdbCredit>());
            foreach (var crew in crewCredits)
            {
                var jobs = (crew.jobs ?? new List<TmdbJob>()).Select(x => x.job).Concat(new[] { crew.job }).Where(IsScreenCrewRole).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var job in jobs) result.Credits.Add(new ProviderCredit { ProviderPersonId = crew.id.ToString(), PersonName = crew.name, Role = job, RoleCategory = RoleCategory(job), RoleName = job });
            }
            return result;
        }

        private FlattenedPerson TmdbPerson(QueueItem item, string payload)
        {
            var source = json.DeserializeFromString<TmdbPerson>(payload) ?? new TmdbPerson();
            var result = new FlattenedPerson { Provider = ProviderNames.Tmdb, ProviderPersonId = item.ProviderId, Name = source.name, Birthday = source.birthday, Aliases = source.also_known_as ?? new List<string>() };
            AddPersonId(result.ExternalIds, ProviderNames.Imdb, source.external_ids?.imdb_id);
            AddPersonId(result.ExternalIds, ProviderNames.Tvdb, source.external_ids?.tvdb_id);
            AddPersonId(result.ExternalIds, ProviderNames.Wikidata, source.external_ids?.wikidata_id);
            return result;
        }

        private FlattenedMedia TvdbMedia(QueueItem item, string payload)
        {
            var source = json.DeserializeFromString<TvdbResponse<TvdbEntity>>(payload)?.data ?? new TvdbEntity();
            var result = new FlattenedMedia { Provider = ProviderNames.Tvdb, MediaType = item.MediaType, ProviderMediaId = item.ProviderId, Name = source.name, Slug = source.slug };
            if (source.id > 0) AddMediaId(result.ExternalIds, ProviderNames.Tvdb, source.id.ToString());
            AddRemoteIds(result.ExternalIds, source.remoteIds, false);
            foreach (var credit in (source.characters ?? new List<TvdbCharacter>()).Where(x => x.peopleId > 0 && TvdbRoles.Contains((x.peopleType ?? string.Empty).Trim())))
                result.Credits.Add(new ProviderCredit { ProviderPersonId = credit.peopleId.ToString(), PersonName = credit.personName, Role = credit.peopleType + (string.IsNullOrWhiteSpace(credit.name) ? string.Empty : ": " + credit.name), RoleCategory = RoleCategory(credit.peopleType), RoleName = credit.name });
            return result;
        }

        private FlattenedPerson TvdbPerson(QueueItem item, string payload)
        {
            var source = json.DeserializeFromString<TvdbResponse<TvdbEntity>>(payload)?.data ?? new TvdbEntity();
            var result = new FlattenedPerson { Provider = ProviderNames.Tvdb, ProviderPersonId = item.ProviderId, Name = source.name, Birthday = source.birth, Aliases = (source.aliases ?? new List<TvdbAlias>()).Select(x => x.name).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() };
            AddRemoteIds(result.ExternalIds, source.remoteIds, true);
            return result;
        }

        private static bool IsScreenCrewRole(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var role = value.Trim();
            return role.IndexOf("Director", StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("Writer", StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("Screenplay", StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("Producer", StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("Creator", StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("Showrunner", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RoleCategory(string value)
        {
            var role = (value ?? string.Empty).Trim();
            if (role.Equals("Actor", StringComparison.OrdinalIgnoreCase) || role.Equals("Guest Star", StringComparison.OrdinalIgnoreCase)) return "Actor";
            if (role.IndexOf("Director", StringComparison.OrdinalIgnoreCase) >= 0) return "Director";
            if (role.IndexOf("Writer", StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("Screenplay", StringComparison.OrdinalIgnoreCase) >= 0) return "Writer";
            if (role.IndexOf("Producer", StringComparison.OrdinalIgnoreCase) >= 0) return "Producer";
            if (role.IndexOf("Creator", StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("Showrunner", StringComparison.OrdinalIgnoreCase) >= 0) return "Creator";
            return "Other";
        }

        private static void AddRemoteIds(IDictionary<string, string> target, IEnumerable<TvdbRemoteId> remoteIds, bool person)
        {
            var values = new List<KeyValuePair<string, string>>();
            foreach (var remote in remoteIds ?? new List<TvdbRemoteId>())
            {
                string provider; string normalized;
                var valid = person
                    ? ExternalIdNormalizer.TryPersonId(remote.sourceName, remote.id, out provider, out normalized)
                    : ExternalIdNormalizer.TryMediaId(remote.sourceName, remote.id, out provider, out normalized);
                if (valid) values.Add(new KeyValuePair<string, string>(provider, normalized));
            }
            foreach (var group in values.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var distinct = group.Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (distinct.Count == 1) target[group.Key] = distinct[0];
            }
        }

        private static void AddPersonId(IDictionary<string, string> target, string provider, string id)
        {
            string normalizedProvider; string normalized;
            if (ExternalIdNormalizer.TryPersonId(provider, id, out normalizedProvider, out normalized)) target[normalizedProvider] = normalized;
        }

        private static void AddMediaId(IDictionary<string, string> target, string provider, string id)
        {
            string normalizedProvider; string normalized;
            if (ExternalIdNormalizer.TryMediaId(provider, id, out normalizedProvider, out normalized)) target[normalizedProvider] = normalized;
        }
    }
}
