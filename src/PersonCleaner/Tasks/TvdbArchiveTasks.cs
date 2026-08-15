using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using PersonCleaner.Storage;
using PersonCleaner.Tvdb;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.Tasks
{
    public abstract class TvdbArchiveTaskBase : IScheduledTask
    {
        private readonly ILibraryManager library;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private readonly TvdbArchiveRepository repository;
        private readonly TvdbApiClient api;
        private readonly TvdbIdentityResolver resolver;
        protected abstract bool IsPreview { get; }
        public abstract string Name { get; }
        public abstract string Key { get; }
        public string Description => IsPreview ? "Fetches direct-ID and unidentified samples, recording inferred, rejected and unresolved identities for inspection." : "Resumable export of direct and confidently inferred TVDB identities for in-scope Emby media and people.";
        public string Category => "GinjaNinja Tools";

        protected TvdbArchiveTaskBase(ILibraryManager library, IHttpClient http, IJsonSerializer json, IApplicationPaths paths, ILogManager logs)
        {
            this.library = library; this.json = json; logger = logs.GetLogger("TVDB Archive");
            repository = new TvdbArchiveRepository(paths, logger); repository.Initialize();
            api = new TvdbApiClient(http, json, logger, repository);
            resolver = new TvdbIdentityResolver(api, library, repository);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (!Plugin.Instance.Configuration.EnablePlugin) { logger.Info("TVDB Archive is disabled."); return; }
            if (!IsPreview && Plugin.Instance.Configuration.RequireSuccessfulPreview && !repository.HasSuccessfulPreview())
                throw new InvalidOperationException("Run 'TVDB Archive - Preview first items' successfully before the full export.");

            var media = library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { typeof(Series).Name, typeof(Episode).Name, typeof(Movie).Name },
                Recursive = true
            }, cancellationToken).Where(x => !(x is Episode episode) || episode.ParentIndexNumber.GetValueOrDefault() >= 1).ToList();
            var screenRoles = new[] { PersonType.Actor, PersonType.GuestStar, PersonType.Director, PersonType.Writer, PersonType.Producer };
            var personIds = new HashSet<long>();
            var mediaIds = media.Select(x => x.InternalId).ToArray();
            for (var offset = 0; offset < mediaIds.Length; offset += 500)
                foreach (var person in library.GetItemPeople(new InternalPeopleQuery
                {
                    ItemIds = mediaIds.Skip(offset).Take(500).ToArray(), PersonTypes = screenRoles,
                    EnableIds = true, EnableProviderIds = true, EnableGroupByName = true
                }).Where(x => x.Id > 0)) personIds.Add(person.Id);
            var people = personIds.Select(x => library.GetItemById(x)).OfType<Person>();
            var items = media.Concat<BaseItem>(people)
            .OrderBy(x => TypeOrder(x)).ThenBy(x => x.InternalId).ToList();

            if (IsPreview)
            {
                var limit = Math.Max(1, Plugin.Instance.Configuration.PreviewItemLimit);
                items = items.GroupBy(EntityType).SelectMany(g =>
                    g.Where(x => x.HasProviderId(MetadataProviders.Tvdb)).Take(limit)
                    .Concat(g.Where(x => !x.HasProviderId(MetadataProviders.Tvdb)).Take(limit))).ToList();
            }

            var checkpoint = IsPreview ? Tuple.Create(0, 0, 0, items.Count) : repository.GetResumeCheckpoint(Key, items.Count);
            repository.SeedExportScope(Key, items.Select((item, ordinal) =>
                Tuple.Create(item.InternalId, EntityType(item), item.HasProviderId(MetadataProviders.Tvdb), ordinal)));
            var done = Math.Min(checkpoint.Item1, items.Count);
            var successes = checkpoint.Item2;
            var failures = checkpoint.Item3;
            long? last = done > 0 ? (long?)items[done - 1].InternalId : null;
            repository.SetRun(Key, "running", items.Count, done, successes, failures, last,
                done > 0 ? "Resuming at item " + (done + 1).ToString(CultureInfo.InvariantCulture) : "Starting");
            if (done > 0)
            {
                progress.Report(items.Count == 0 ? 100 : done * 100.0 / items.Count);
                logger.Info("TVDB Archive export resuming after {0} of {1} items; {2} successful and {3} failed are already recorded.", done, items.Count, successes, failures);
            }
            var resolvedSeries = new Dictionary<long, string>();
            foreach (var series in items.Take(done).OfType<Series>())
            {
                var seriesTvdb = repository.GetAcceptedResolvedTvdbId(series.InternalId) ?? series.GetProviderId(MetadataProviders.Tvdb);
                if (!string.IsNullOrWhiteSpace(seriesTvdb)) resolvedSeries[series.InternalId] = seriesTvdb;
            }
            try
            {
                foreach (var item in items.Skip(done))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    last = item.InternalId;
                    SaveEmbyItem(item);
                    var type = EntityType(item);
                    var observedId = item.GetProviderId(MetadataProviders.Tvdb);
                    string id; string provenance; string method; double confidence; int candidateCount; string evidence;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(observedId))
                        {
                            id = observedId; provenance = "direct"; method = "emby-tvdb-id"; confidence = 1.0; candidateCount = 1; evidence = "TVDB id already present on Emby item";
                            repository.SaveResolutionCandidates(item.InternalId, Enumerable.Empty<ResolutionCandidate>(), value => json.SerializeToString(value));
                        }
                        else
                        {
                            string parentSeriesId = null;
                            if (item is Episode unresolvedEpisode && unresolvedEpisode.Series != null) resolvedSeries.TryGetValue(unresolvedEpisode.Series.InternalId, out parentSeriesId);
                            var result = await resolver.Resolve(item, cancellationToken, parentSeriesId).ConfigureAwait(false);
                            id = result.TvdbId; method = result.Method; confidence = result.Confidence; candidateCount = result.CandidateCount; evidence = result.Evidence;
                            repository.SaveResolutionCandidates(item.InternalId, result.Candidates, value => json.SerializeToString(value));
                            provenance = string.IsNullOrWhiteSpace(id) ? "unresolved" : confidence >= Plugin.Instance.Configuration.AutoResolutionMinimumConfidence ? "inferred" : "rejected";
                        }
                        repository.SaveItemResolution(item.InternalId, type, observedId, id, provenance, method, confidence, candidateCount, json.SerializeToString(new { evidence }));
                        if ((provenance == "direct" || provenance == "inferred") && !string.IsNullOrWhiteSpace(id))
                        {
                            if (item is Series) resolvedSeries[item.InternalId] = id;
                            if (type == "episode" && item is Episode episode)
                            {
                                if (episode.Series is Series parent && resolvedSeries.TryGetValue(parent.InternalId, out var resolvedParentId))
                                    await FetchSeriesEpisodes(resolvedParentId, cancellationToken).ConfigureAwait(false);
                                await FetchEntity(type, id, cancellationToken).ConfigureAwait(false);
                            }
                            else await FetchEntity(type, id, cancellationToken).ConfigureAwait(false);
                            successes++;
                        }
                        repository.SetExportScopeResult(Key, item.InternalId, provenance);
                        logger.Info("TVDB identity {0} Emby={1} '{2}': observed={3}, resolved={4}, provenance={5}, method={6}, confidence={7:F3}", type, item.InternalId, item.Name, observedId ?? "(none)", id ?? "(none)", provenance, method, confidence);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        failures++;
                        if (!string.IsNullOrWhiteSpace(observedId)) repository.MarkFetch(type + ":" + observedId, false, ex.Message);
                        if (!string.IsNullOrWhiteSpace(observedId) && ex is HttpException httpException && httpException.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            repository.SaveItemResolution(item.InternalId, type, observedId, observedId, "direct-unavailable", "tvdb-404", 0.0, 0,
                                json.SerializeToString(new { evidence = "Emby has this TVDB id, but TVDB returned HTTP 404 for the entity endpoint. Human review recommended; Emby was not modified." }));
                            repository.SetExportScopeResult(Key, item.InternalId, "direct-unavailable");
                        }
                        else repository.SetExportScopeResult(Key, item.InternalId, "failed");
                        logger.ErrorException("TVDB resolution/fetch failed for {0} (Emby {1})", ex, type, item.InternalId);
                    }
                    done++; progress.Report(items.Count == 0 ? 100 : done * 100.0 / items.Count);
                    repository.SetRun(Key, "running", items.Count, done, successes, failures, last, item.Name);
                }
                if (successes == 0 && items.Count > 0)
                {
                    repository.SetRun(Key, "failed", items.Count, done, successes, failures, last, "No TVDB fetch succeeded");
                    throw new InvalidOperationException("No TVDB fetch succeeded; inspect the task log and API credentials.");
                }
                repository.SetRun(Key, "completed", items.Count, done, successes, failures, last, "Finished");
                var coverage = repository.GetCastIdCoverage();
                logger.Info("TVDB cast identity coverage: {0} distinct cast people; TMDB {1} ({2:F2}%); IMDb {3} ({4:F2}%).",
                    coverage.Item1, coverage.Item2, Percent(coverage.Item2, coverage.Item1), coverage.Item3, Percent(coverage.Item3, coverage.Item1));
                logger.Info("TVDB Archive {0}: {1} processed, {2} successful, {3} failed. Database: {4}", IsPreview ? "preview" : "export", done, successes, failures, repository.DatabasePath);
            }
            catch (OperationCanceledException)
            {
                repository.SetRun(Key, "cancelled", items.Count, done, successes, failures, last, "Stopped; next run resumes using the cache/checkpoint");
                throw;
            }
        }

        private async Task FetchEntity(string type, string id, CancellationToken ct)
        {
            var key = type + ":" + id;
            if (!repository.IsDue(key)) return;
            var endpoint = type == "person" ? "people" : type == "series" ? "series" : type == "movie" ? "movies" : "episodes";
            var data = await api.GetEntity(endpoint, id, ct).ConfigureAwait(false);
            repository.SaveEntity(id, type, data, json.SerializeToString(data));
            repository.MarkFetch(key, true, null);
            if (type != "person")
                foreach (var personId in (data.characters ?? new List<CharacterData>()).Where(TvdbScope.IsScreenCredit).Select(x => x.peopleId).Where(x => x > 0).Distinct())
                    await FetchEntity("person", personId.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            if (type == "series") await FetchSeriesEpisodes(id, ct).ConfigureAwait(false);
        }

        private async Task FetchSeriesEpisodes(string seriesId, CancellationToken ct)
        {
            var key = "series-episodes:" + seriesId;
            if (!repository.IsDue(key)) return;
            var page = 0; var receivedAnyPage = false;
            while (page < 100)
            {
                var response = await api.GetSeriesEpisodes(seriesId, page, ct).ConfigureAwait(false);
                if (response?.data == null) break;
                receivedAnyPage = true;
                repository.SaveEpisodeBatch(seriesId, response.data);
                var regularEpisodeIds = new HashSet<int>((response.data.episodes ?? new List<EpisodeData>()).Where(x => x.seasonNumber >= 1).Select(x => x.id));
                foreach (var personId in (response.data.characters ?? new List<CharacterData>()).Where(TvdbScope.IsScreenCredit).Where(x => x.episodeId.HasValue && regularEpisodeIds.Contains(x.episodeId.Value)).Select(x => x.peopleId).Where(x => x > 0).Distinct())
                    await FetchEntity("person", personId.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                var count = response.data.episodes?.Count ?? 0;
                if (count == 0 || response.links == null || count < response.links.page_size || string.IsNullOrEmpty(response.links.next)) break;
                page++;
            }
            repository.MarkFetch(key, receivedAnyPage, receivedAnyPage ? null : "No official season-1+ episode feed returned");
        }

        private void SaveEmbyItem(BaseItem item)
        {
            repository.SaveEmby(item.InternalId, item.Id.ToString("N"), EntityType(item), item.Name, item.ProductionYear,
                item.Parent?.InternalId, item.GetProviderId(MetadataProviders.Tvdb), item.GetProviderId(MetadataProviders.Imdb), item.GetProviderId(MetadataProviders.Tmdb), item.Path);
        }

        private static int TypeOrder(BaseItem x) => x is Series ? 0 : x is Movie ? 1 : x is Person ? 2 : 3;
        private static double Percent(int value, int total) => total == 0 ? 0 : value * 100.0 / total;
        private static string EntityType(BaseItem x) => x is Person ? "person" : x is Series ? "series" : x is Episode ? "episode" : "movie";
    }

    public sealed class TvdbArchivePreviewTask : TvdbArchiveTaskBase
    {
        protected override bool IsPreview => true;
        public override string Name => "TVDB Archive - Preview first items";
        public override string Key => "TvdbArchivePreview";
        public TvdbArchivePreviewTask(ILibraryManager l, IHttpClient h, IJsonSerializer j, IApplicationPaths p, ILogManager m) : base(l, h, j, p, m) { }
    }

    public sealed class TvdbArchiveFullTask : TvdbArchiveTaskBase
    {
        protected override bool IsPreview => false;
        public override string Name => "TVDB Archive - Full resumable export";
        public override string Key => "TvdbArchiveFull";
        public TvdbArchiveFullTask(ILibraryManager l, IHttpClient h, IJsonSerializer j, IApplicationPaths p, ILogManager m) : base(l, h, j, p, m) { }
    }

    public sealed class TvdbArchiveIdProbeTask : IScheduledTask
    {
        private readonly ILibraryManager library;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private readonly TvdbArchiveRepository repository;
        private readonly TvdbApiClient api;
        public string Name => "TVDB Archive - Probe IMDb/TVDB mappings";
        public string Key => "TvdbArchiveIdProbe";
        public string Description => "Proves TVDB-to-IMDb and IMDb-to-TVDB lookup for one Emby show, episode, movie and person.";
        public string Category => "GinjaNinja Tools";
        public TvdbArchiveIdProbeTask(ILibraryManager l, IHttpClient h, IJsonSerializer j, IApplicationPaths p, ILogManager m)
        { library = l; json = j; logger = m.GetLogger("TVDB Archive"); repository = new TvdbArchiveRepository(p, logger); repository.Initialize(); api = new TvdbApiClient(h, j, logger, repository); }
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
        public async Task Execute(CancellationToken ct, IProgress<double> progress)
        {
            var items = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(Series).Name, typeof(Episode).Name, typeof(Movie).Name, typeof(Person).Name }, Recursive = true }, ct)
                .Where(x => x.HasProviderId(MetadataProviders.Tvdb)).GroupBy(x => x is Series ? "series" : x is Episode ? "episode" : x is Movie ? "movie" : "person").Select(g => g.First()).ToList();
            var done = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var type = item is Series ? "series" : item is Episode ? "episode" : item is Movie ? "movie" : "person";
                var endpoint = type == "series" ? "series" : type == "episode" ? "episodes" : type == "movie" ? "movies" : "people";
                var tvdb = item.GetProviderId(MetadataProviders.Tvdb);
                var entity = await api.GetEntity(endpoint, tvdb, ct).ConfigureAwait(false);
                var imdb = (entity.remoteIds ?? new List<RemoteIdData>()).FirstOrDefault(x => (x.sourceName ?? "").IndexOf("imdb", StringComparison.OrdinalIgnoreCase) >= 0)?.id;
                repository.SaveProbe("tvdb-to-imdb", type, tvdb, tvdb, imdb, entity.name, !string.IsNullOrWhiteSpace(imdb), json.SerializeToString(entity));
                if (!string.IsNullOrWhiteSpace(imdb))
                {
                    var reverse = await api.SearchRemoteId(imdb, ct).ConfigureAwait(false);
                    var wrapper = (reverse.data ?? new List<SearchByRemoteIdData>()).FirstOrDefault(x => GetReverseEntity(x, type) != null);
                    var match = GetReverseEntity(wrapper, type);
                    var reverseTvdb = match?.id > 0 ? match.id.ToString(CultureInfo.InvariantCulture) : null;
                    var roundTripMatches = string.Equals(reverseTvdb, tvdb, StringComparison.Ordinal);
                    repository.SaveProbe("imdb-to-tvdb", type, imdb, reverseTvdb, imdb, match?.name, roundTripMatches, json.SerializeToString(reverse));
                    logger.Info("ID probe {0}: TVDB {1} -> IMDb {2} -> TVDB {3}; exact match={4}", type, tvdb, imdb, reverseTvdb ?? "(none)", roundTripMatches);
                }
                else logger.Info("ID probe {0}: TVDB {1} has no IMDb id; reverse lookup not attempted", type, tvdb);
                done++; progress.Report(done * 100.0 / Math.Max(1, items.Count));
            }
        }

        private static SearchEntityData GetReverseEntity(SearchByRemoteIdData result, string type)
        {
            if (result == null) return null;
            if (type == "series") return result.series;
            if (type == "episode") return result.episode;
            if (type == "movie") return result.movie;
            return result.people;
        }
    }
}
