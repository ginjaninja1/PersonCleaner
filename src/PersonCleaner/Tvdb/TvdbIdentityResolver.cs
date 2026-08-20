using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using PersonCleaner.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.Tvdb
{
    internal sealed class ResolutionResult
    {
        public string TvdbId { get; set; }
        public string Name { get; set; }
        public string Method { get; set; }
        public double Confidence { get; set; }
        public int CandidateCount { get; set; }
        public string Evidence { get; set; }
        public List<ResolutionCandidate> Candidates { get; set; } = new List<ResolutionCandidate>();
    }

    internal sealed class ResolutionCandidate
    {
        public int Rank { get; set; }
        public string TvdbId { get; set; }
        public string EntityType { get; set; }
        public string Name { get; set; }
        public double Score { get; set; }
        public List<RemoteIdData> ExternalIds { get; set; } = new List<RemoteIdData>();
        public List<string> FilmographyIds { get; set; } = new List<string>();
        public List<string> LocalFilmographyIds { get; set; } = new List<string>();
        public List<string> OverlapIds { get; set; } = new List<string>();
        public string Evidence { get; set; }
        public int SearchRank { get; set; }
        public string NameClass { get; set; }
        public string DiscoveryMethods { get; set; }
        public bool ExtendedFetched { get; set; }
        public string ExtendedFetchReason { get; set; }
    }

    internal sealed class TvdbIdentityResolver
    {
        private readonly TvdbApiClient api;
        private readonly ILibraryManager library;
        private readonly TvdbArchiveRepository repository;
        public TvdbIdentityResolver(TvdbApiClient api, ILibraryManager library, TvdbArchiveRepository repository) { this.api = api; this.library = library; this.repository = repository; }

        public async Task<ResolutionResult> Resolve(BaseItem item, CancellationToken ct, string resolvedSeriesTvdbId = null, string corroboratedPersonImdbId = null)
        {
            var type = TypeOf(item);
            if (item is Person)
            {
                // Strong remote IDs nominate candidates before the looser name search.
                // TVDB-native filmography is still required by the caller before a
                // candidate can be accepted.
                var remoteEvidence = new List<ResolutionResult>();
                var personImdb = item.GetProviderId(MetadataProviders.Imdb) ?? corroboratedPersonImdbId;
                if (!string.IsNullOrWhiteSpace(personImdb))
                {
                    var method = item.GetProviderId(MetadataProviders.Imdb) == null ? "supported-tmdb-imdb-remote" : "imdb-remote";
                    var result = await ResolveRemote(item, personImdb, type, method, 0.995, ct).ConfigureAwait(false);
                    if (result != null) remoteEvidence.Add(result);
                }
                var personTmdb = item.GetProviderId(MetadataProviders.Tmdb);
                if (!string.IsNullOrWhiteSpace(personTmdb))
                {
                    var result = await ResolveRemote(item, personTmdb, type, "tmdb-remote", 0.990, ct).ConfigureAwait(false);
                    if (result != null) remoteEvidence.Add(result);
                }
                var native = await ResolveName(item, type, ct).ConfigureAwait(false);
                MergeRemoteEvidence(native, remoteEvidence);
                await EnrichRemotePersonCandidates(item as Person, native, ct).ConfigureAwait(false);
                native.Evidence = "evidence_first=true; supported remote IDs nominated before name fallback; remote lookups are non-authoritative candidate evidence; TVDB-native media support remains mandatory; " + native.Evidence;
                return native;
            }
            var rejected = new List<string>();
            var rejectedResults = new List<ResolutionResult>();
            var imdb = item.GetProviderId(MetadataProviders.Imdb);
            if (!string.IsNullOrWhiteSpace(imdb))
            {
                var result = await ResolveRemote(item, imdb, type, "imdb-remote", 0.995, ct).ConfigureAwait(false);
                if (result != null && result.Confidence >= 0.90) return result;
                if (result != null) { rejectedResults.Add(result); rejected.Add(result.Method + ": " + result.Evidence + "; confidence=" + result.Confidence.ToString("F3", CultureInfo.InvariantCulture)); }
            }
            var tmdb = item.GetProviderId(MetadataProviders.Tmdb);
            if (!string.IsNullOrWhiteSpace(tmdb))
            {
                var result = await ResolveRemote(item, tmdb, type, "tmdb-remote", 0.990, ct).ConfigureAwait(false);
                if (result != null && result.Confidence >= 0.90) return result;
                if (result != null) { rejectedResults.Add(result); rejected.Add(result.Method + ": " + result.Evidence + "; confidence=" + result.Confidence.ToString("F3", CultureInfo.InvariantCulture)); }
            }
            if (item is Episode episode && episode.ParentIndexNumber.GetValueOrDefault() >= 1 && episode.IndexNumber.HasValue && episode.Series is Series series)
            {
                var seriesTvdb = resolvedSeriesTvdbId ?? series.GetProviderId(MetadataProviders.Tvdb);
                if (!string.IsNullOrWhiteSpace(seriesTvdb))
                {
                    var found = await ResolveEpisodeCoordinate(seriesTvdb, episode, ct).ConfigureAwait(false);
                    if (found != null) return found;
                }
            }
            var fallback = await ResolveName(item, type, ct).ConfigureAwait(false);
            if (rejected.Count > 0) fallback.Evidence = "rejected_remote_candidates=[" + string.Join(" | ", rejected) + "]; " + fallback.Evidence;
            var consensus = rejectedResults.Where(x => !string.IsNullOrWhiteSpace(x.TvdbId)).GroupBy(x => x.TvdbId).FirstOrDefault(g => g.Select(x => x.Method).Distinct().Count() >= 2);
            if (consensus != null && (string.IsNullOrWhiteSpace(fallback.TvdbId) || string.Equals(fallback.TvdbId, consensus.Key, StringComparison.Ordinal)))
            {
                var representative = consensus.First();
                return new ResolutionResult
                {
                    TvdbId = consensus.Key, Name = fallback.Name ?? representative.Name, Method = "remote-consensus",
                    Confidence = 0.96, CandidateCount = Math.Max(1, fallback.CandidateCount),
                    Evidence = "IMDb and TMDB converge on the same TVDB id; no structurally superior alternative was found. " + fallback.Evidence,
                    Candidates = fallback.Candidates
                };
            }
            return fallback;
        }

        private async Task<ResolutionResult> ResolveRemote(BaseItem item, string remoteId, string type, string method, double confidence, CancellationToken ct)
        {
            var response = await api.SearchRemoteId(remoteId, ct).ConfigureAwait(false);
            var matches = (response.data ?? new List<SearchByRemoteIdData>()).Select(x => EntityFor(x, type)).Where(x => x != null).ToList();
            if (matches.Count != 1) return null;
            var match = matches[0];
            var localName = Normalize(item.Name); var remoteName = Normalize(match.name);
            var nameAgreement = localName == remoteName ? "exact" : (localName.Contains(remoteName) || remoteName.Contains(localName) ? "compatible" : "conflict");
            if (nameAgreement == "compatible") confidence = Math.Min(confidence, 0.970);
            else if (nameAgreement == "conflict") confidence = Math.Min(confidence, 0.700);
            var yearAgreement = "unknown";
            if (item.ProductionYear.HasValue && int.TryParse(match.year, out var remoteYear))
            {
                yearAgreement = Math.Abs(item.ProductionYear.Value - remoteYear) <= 1 ? "compatible" : "conflict";
                if (yearAgreement == "conflict") confidence = Math.Min(confidence, 0.650);
            }
            var episodeEvidence = string.Empty;
            if (item is Series series)
            {
                var localCount = GetLocalRegularEpisodeCount(series, ct);
                var remoteCount = await GetTvdbRegularEpisodeCount(match.id.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                confidence = ApplyEpisodeCountConfidence(confidence, localCount, remoteCount);
                episodeEvidence = "; local_regular_episodes=" + localCount + "; tvdb_regular_episodes=" + remoteCount + "; episode_difference=" + Math.Abs(localCount - remoteCount);
            }
            return new ResolutionResult { TvdbId = match.id.ToString(CultureInfo.InvariantCulture), Name = match.name, Method = method, Confidence = confidence, CandidateCount = matches.Count, Evidence = "remote_id=" + remoteId + "; unique typed result; name=" + nameAgreement + "; year=" + yearAgreement + episodeEvidence };
        }

        private async Task<ResolutionResult> ResolveEpisodeCoordinate(string seriesTvdb, Episode episode, CancellationToken ct)
        {
            var page = 0; var candidates = new List<EpisodeData>();
            while (page < 100)
            {
                var response = await api.GetSeriesEpisodes(seriesTvdb, page, ct).ConfigureAwait(false);
                if (response?.data?.episodes == null) break;
                candidates.AddRange(response.data.episodes.Where(x => x.seasonNumber == episode.ParentIndexNumber && x.number == episode.IndexNumber));
                if (response.data.episodes.Count == 0 || response.links == null || response.data.episodes.Count < response.links.page_size || string.IsNullOrEmpty(response.links.next)) break;
                page++;
            }
            if (candidates.Count != 1) return null;
            var c = candidates[0];
            var nameAgrees = Normalize(c.name) == Normalize(episode.Name);
            return new ResolutionResult { TvdbId = c.id.ToString(CultureInfo.InvariantCulture), Name = c.name, Method = "series-season-episode", Confidence = nameAgrees ? 0.995 : 0.970, CandidateCount = 1, Evidence = "series_tvdb=" + seriesTvdb + "; season=" + episode.ParentIndexNumber + "; episode=" + episode.IndexNumber + "; name_agrees=" + nameAgrees };
        }

        private async Task<ResolutionResult> ResolveName(BaseItem item, string type, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) return new ResolutionResult { Method = "no-evidence", Confidence = 0, Evidence = "missing name" };
            // A Person's Emby ProductionYear is commonly their birth year. TVDB's
            // generic search year filter does not represent person birth year and
            // suppresses valid name matches (proven by Mark Burdis: 4 without the
            // filter, 0 with year=1968).
            var searchYear = item is Person ? null : item.ProductionYear;
            var response = await api.Search(type, item.Name, searchYear, ct).ConfigureAwait(false);
            var all = (response.data ?? new List<SearchData>())
                .Where(x => IsCompatibleSearchType(x.type, type))
                .ToList();
            var scored = new List<Tuple<SearchData, double, string>>();
            var candidateEvidence = new List<ResolutionCandidate>();
            var localEpisodeCount = item is Series localSeries ? GetLocalRegularEpisodeCount(localSeries, ct) : -1;
            var localFilmography = item is Person localPerson ? GetLocalPersonProductionIds(localPerson, ct) : null;
            var searchRank = 0;
            foreach (var candidate in all.Take(10))
            {
                searchRank++;
                var score = Score(item, candidate); var structural = string.Empty;
                var nameClass = ClassifyName(item.Name, candidate.name);
                if (localEpisodeCount >= 0 && !string.IsNullOrWhiteSpace(candidate.tvdb_id))
                {
                    var remoteCount = await GetTvdbRegularEpisodeCount(candidate.tvdb_id, ct).ConfigureAwait(false);
                    var difference = Math.Abs(localEpisodeCount - remoteCount);
                    var ratio = localEpisodeCount == 0 ? (remoteCount == 0 ? 0 : 1) : difference / (double)localEpisodeCount;
                    if (difference == 0) score += 0.20;
                    else if (difference <= 2 || ratio <= 0.02) score += 0.17;
                    else if (ratio <= 0.05) score += 0.12;
                    else if (ratio <= 0.15) score += 0.05;
                    structural = "; local_regular_episodes=" + localEpisodeCount + "; tvdb_regular_episodes=" + remoteCount + "; episode_difference=" + difference;
                }
                var fetchReason = CandidateFetchReason(item, candidate, nameClass, searchRank);
                if (item is Person && fetchReason != null && int.TryParse(candidate.tvdb_id, out var personTvdbId))
                {
                    var comparableFilmography = localFilmography ?? new HashSet<string>();
                    var candidatePerson = await api.GetEntity("people", personTvdbId.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                    var remoteFilmography = new HashSet<string>();
                    foreach (var credit in candidatePerson.characters ?? new List<CharacterData>())
                    {
                        if (credit.episodeId.HasValue) remoteFilmography.Add("episode:" + credit.episodeId.Value);
                        if (credit.movieId.HasValue) remoteFilmography.Add("movie:" + credit.movieId.Value);
                        if (credit.seriesId.HasValue) remoteFilmography.Add("series:" + credit.seriesId.Value);
                    }
                    var overlap = comparableFilmography.Count(x => remoteFilmography.Contains(x));
                    var overlapIds = comparableFilmography.Where(x => remoteFilmography.Contains(x)).OrderBy(x => x).ToArray();
                    var overlapDescriptions = overlapIds.Select(repository.DescribeProduction).ToArray();
                    if (overlap >= 3) score += 0.35;
                    else if (overlap == 2) score += 0.30;
                    else if (overlap == 1) score += 0.20;
                    structural += "; local_known_productions=" + comparableFilmography.Count + "; local_production_ids=[" + string.Join(",", comparableFilmography.OrderBy(x => x)) + "]" +
                                  "; tvdb_productions=" + remoteFilmography.Count + "; filmography_overlap=" + overlap + "; overlap_ids=[" + string.Join(",", overlapIds) + "]" +
                                  "; shared_productions=[" + string.Join(" | ", overlapDescriptions) + "]";
                    structural += "; extended_fetched=true; extended_fetch_reason=" + fetchReason;
                    candidateEvidence.Add(new ResolutionCandidate { TvdbId = candidate.tvdb_id, EntityType = type, Name = candidate.name, Score = score, ExternalIds = candidatePerson.remoteIds ?? new List<RemoteIdData>(), FilmographyIds = remoteFilmography.OrderBy(x => x).ToList(), LocalFilmographyIds = comparableFilmography.OrderBy(x => x).ToList(), OverlapIds = overlapIds.ToList(), Evidence = structural.TrimStart(';', ' '), SearchRank = searchRank, NameClass = nameClass, DiscoveryMethods = "name-search", ExtendedFetched = true, ExtendedFetchReason = fetchReason });
                }
                else
                {
                    var reason = localFilmography == null || localFilmography.Count == 0 ? "no-local-tvdb-filmography" : "preflight-signal-too-weak";
                    structural += "; extended_fetched=false; extended_fetch_reason=" + reason;
                    candidateEvidence.Add(new ResolutionCandidate { TvdbId = candidate.tvdb_id, EntityType = type, Name = candidate.name, Score = score, ExternalIds = candidate.remote_ids ?? new List<RemoteIdData>(), LocalFilmographyIds = (localFilmography ?? new HashSet<string>()).OrderBy(x => x).ToList(), Evidence = structural.TrimStart(';', ' '), SearchRank = searchRank, NameClass = nameClass, DiscoveryMethods = "name-search", ExtendedFetched = false, ExtendedFetchReason = reason });
                }
                scored.Add(Tuple.Create(candidate, score, structural));
            }
            scored = scored.OrderByDescending(x => x.Item2).ToList();
            if (scored.Count == 0) return new ResolutionResult { Method = "name-metadata", Confidence = 0, CandidateCount = 0, Evidence = "no candidates" };
            var top = scored[0];
            var margin = scored.Count == 1 ? 0.10 : Math.Max(0, Math.Min(0.10, top.Item2 - scored[1].Item2));
            var confidence = Math.Min(0.98, top.Item2 + margin);
            var orderedEvidence = candidateEvidence.OrderByDescending(x => x.Score).ToList();
            for (var rank = 0; rank < orderedEvidence.Count; rank++) orderedEvidence[rank].Rank = rank + 1;
            return new ResolutionResult { TvdbId = top.Item1.tvdb_id, Name = top.Item1.name, Method = "name-metadata", Confidence = confidence, CandidateCount = all.Count, Evidence = "normalized_name=" + Normalize(item.Name) + "; top_score=" + top.Item2.ToString("F3", CultureInfo.InvariantCulture) + "; margin=" + margin.ToString("F3", CultureInfo.InvariantCulture) + top.Item3, Candidates = orderedEvidence };
        }

        private int GetLocalRegularEpisodeCount(Series series, CancellationToken ct) => library.GetItemList(new InternalItemsQuery
        { IncludeItemTypes = new[] { typeof(Episode).Name }, SeriesIds = new[] { series.InternalId }, Recursive = true }, ct).OfType<Episode>().Count(x => x.ParentIndexNumber.GetValueOrDefault() >= 1);

        private HashSet<string> GetLocalPersonProductionIds(Person person, CancellationToken ct)
        {
            var roles = new[] { PersonType.Actor, PersonType.GuestStar, PersonType.Director, PersonType.Writer, PersonType.Producer };
            var items = library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie", "Series", "Episode" }, PersonIds = new[] { person.InternalId },
                PersonTypes = roles, Recursive = true
            }, ct);
            var result = new HashSet<string>();
            foreach (var media in items)
            {
                if (media is Episode episode && episode.ParentIndexNumber.GetValueOrDefault() < 1) continue;
                var tvdb = media.GetProviderId(MetadataProviders.Tvdb) ?? repository.GetAcceptedResolvedTvdbId(media.InternalId);
                if (!string.IsNullOrWhiteSpace(tvdb)) result.Add(TypeOf(media) + ":" + tvdb);
                if (media is Episode regularEpisode && regularEpisode.Series != null)
                {
                    var seriesTvdb = regularEpisode.Series.GetProviderId(MetadataProviders.Tvdb) ?? repository.GetAcceptedResolvedTvdbId(regularEpisode.Series.InternalId);
                    if (!string.IsNullOrWhiteSpace(seriesTvdb)) result.Add("series:" + seriesTvdb);
                }
            }
            return result;
        }

        private static void MergeRemoteEvidence(ResolutionResult fallback, IEnumerable<ResolutionResult> remoteResults)
        {
            foreach (var remote in remoteResults.Where(x => !string.IsNullOrWhiteSpace(x.TvdbId)))
            {
                var existing = fallback.Candidates.FirstOrDefault(x => string.Equals(x.TvdbId, remote.TvdbId, StringComparison.Ordinal));
                if (existing == null)
                {
                    fallback.Candidates.Add(new ResolutionCandidate
                    {
                        Rank = fallback.Candidates.Count + 1,
                        TvdbId = remote.TvdbId,
                        EntityType = "person",
                        Name = remote.Name,
                        Score = 0,
                        NameClass = "remote-only",
                        DiscoveryMethods = remote.Method,
                        ExtendedFetched = false,
                        ExtendedFetchReason = "remote-evidence-only",
                        Evidence = remote.Evidence
                    });
                }
                else
                {
                    var methods = new HashSet<string>((existing.DiscoveryMethods ?? "name-search").Split(','), StringComparer.OrdinalIgnoreCase) { remote.Method };
                    existing.DiscoveryMethods = string.Join(",", methods.OrderBy(x => x));
                    existing.Evidence = (existing.Evidence ?? string.Empty) + "; remote_evidence=" + remote.Method + "[" + remote.Evidence + "]";
                }
            }
        }

        private async Task EnrichRemotePersonCandidates(Person person, ResolutionResult result, CancellationToken ct)
        {
            var localFilmography = GetLocalPersonProductionIds(person, ct);
            foreach (var candidate in result.Candidates.Where(x => !x.ExtendedFetched && (x.DiscoveryMethods ?? string.Empty).IndexOf("remote", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var detail = await api.GetEntity("people", candidate.TvdbId, ct).ConfigureAwait(false);
                var remoteFilmography = new HashSet<string>();
                foreach (var credit in detail.characters ?? new List<CharacterData>())
                {
                    if (credit.episodeId.HasValue) remoteFilmography.Add("episode:" + credit.episodeId.Value);
                    if (credit.movieId.HasValue) remoteFilmography.Add("movie:" + credit.movieId.Value);
                    if (credit.seriesId.HasValue) remoteFilmography.Add("series:" + credit.seriesId.Value);
                }
                candidate.ExternalIds = detail.remoteIds ?? new List<RemoteIdData>();
                candidate.FilmographyIds = remoteFilmography.OrderBy(x => x).ToList();
                candidate.LocalFilmographyIds = localFilmography.OrderBy(x => x).ToList();
                candidate.OverlapIds = localFilmography.Where(remoteFilmography.Contains).OrderBy(x => x).ToList();
                candidate.ExtendedFetched = true;
                candidate.ExtendedFetchReason = "strong-remote-id-candidate";
                candidate.Evidence = (candidate.Evidence ?? string.Empty) + "; TVDB person filmography fetched for provider-native acceptance; overlap_ids=[" + string.Join(",", candidate.OverlapIds) + "]";
            }
        }

        private static string CandidateFetchReason(BaseItem item, SearchData candidate, string nameClass, int searchRank)
        {
            if (!(item is Person)) return "non-person-structural-evaluation";
            if (nameClass == "exact") return "exact-normalized-name";
            if (nameClass == "close" && searchRank <= 3) return "close-name-top-three";
            // Stable one-percent research sample across resumes and processes.
            var key = item.InternalId.ToString(CultureInfo.InvariantCulture) + ":" + (candidate.tvdb_id ?? string.Empty);
            var hash = 17;
            foreach (var c in key) hash = unchecked(hash * 31 + c);
            if ((hash & 0x7fffffff) % 100 == 0) return "weak-candidate-research-sample-1pct";
            return null;
        }

        private static string ClassifyName(string local, string remote)
        {
            var a = Normalize(local); var b = Normalize(remote);
            if (a == b) return "exact";
            if (a.Length == 0 || b.Length == 0) return "weak";
            if (a.Contains(b) || b.Contains(a)) return "close";
            var distance = LevenshteinDistance(a, b);
            return distance <= Math.Max(1, Math.Max(a.Length, b.Length) / 10) ? "close" : "weak";
        }

        private static int LevenshteinDistance(string a, string b)
        {
            var previous = new int[b.Length + 1]; var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) previous[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var swap = previous; previous = current; current = swap;
            }
            return previous[b.Length];
        }

        private async Task<int> GetTvdbRegularEpisodeCount(string seriesId, CancellationToken ct)
        {
            var count = 0; var page = 0;
            while (page < 100)
            {
                var response = await api.GetSeriesEpisodes(seriesId, page, ct).ConfigureAwait(false);
                var episodes = response?.data?.episodes;
                if (episodes == null) break;
                count += episodes.Count(x => x.seasonNumber >= 1);
                if (episodes.Count == 0 || response.links == null || episodes.Count < response.links.page_size || string.IsNullOrEmpty(response.links.next)) break;
                page++;
            }
            return count;
        }

        private static double ApplyEpisodeCountConfidence(double confidence, int local, int remote)
        {
            var difference = Math.Abs(local - remote);
            var ratio = local == 0 ? (remote == 0 ? 0 : 1) : difference / (double)local;
            if (difference == 0) return confidence;
            if (difference <= 2 || ratio <= 0.02) return Math.Min(confidence, 0.985);
            if (ratio <= 0.05) return Math.Min(confidence, 0.950);
            if (ratio <= 0.15) return Math.Min(confidence, 0.850);
            return Math.Min(confidence, 0.650);
        }

        private static double Score(BaseItem item, SearchData candidate)
        {
            var score = 0.0;
            var a = Normalize(item.Name); var b = Normalize(candidate.name);
            if (a == b) score += 0.60;
            else if (a.Contains(b) || b.Contains(a)) score += 0.38;
            if (item.ProductionYear.HasValue && int.TryParse(candidate.year, out var year))
            { var delta = Math.Abs(item.ProductionYear.Value - year); if (delta == 0) score += 0.25; else if (delta == 1) score += 0.12; }
            if (!string.IsNullOrWhiteSpace(candidate.tvdb_id)) score += 0.05;
            return score;
        }

        internal static string TypeOf(BaseItem x) => x is Person ? "person" : x is Series ? "series" : x is Episode ? "episode" : "movie";
        private static bool IsCompatibleSearchType(string returnedType, string requestedType)
        {
            if (string.IsNullOrWhiteSpace(returnedType)) return true;
            return string.Equals(CanonicalEntityType(returnedType), CanonicalEntityType(requestedType), StringComparison.Ordinal);
        }

        private static string CanonicalEntityType(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
            switch (normalized)
            {
                case "person": case "people": case "persons": return "person";
                case "series": case "tvseries": case "show": case "shows": return "series";
                case "movie": case "movies": case "film": case "films": return "movie";
                case "episode": case "episodes": case "tvepisode": case "tvepisodes": return "episode";
                case "company": case "companies": return "company";
                default: return normalized;
            }
        }
        private static SearchEntityData EntityFor(SearchByRemoteIdData x, string type) => type == "series" ? x.series : type == "episode" ? x.episode : type == "movie" ? x.movie : x.people;
        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var decomposed = value.Normalize(NormalizationForm.FormD); var b = new StringBuilder();
            foreach (var c in decomposed) if (char.IsLetterOrDigit(c)) b.Append(char.ToLowerInvariant(c));
            return b.ToString();
        }
    }
}
