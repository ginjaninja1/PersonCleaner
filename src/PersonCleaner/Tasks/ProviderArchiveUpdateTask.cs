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
using PersonCleaner.Tmdb;
using PersonCleaner.Tvdb;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace PersonCleaner.Tasks
{
    public sealed class ProviderArchiveUpdateTask : IScheduledTask
    {
        private static readonly PersonType[] ScreenRoles = { PersonType.Actor, PersonType.GuestStar, PersonType.Director, PersonType.Writer, PersonType.Producer };
        private readonly ILibraryManager library;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private readonly TvdbArchiveRepository tvdbRepository;
        private readonly TmdbArchiveRepository tmdbRepository;
        private readonly UnifiedArchiveRepository unifiedRepository;
        private readonly TvdbApiClient tvdbApi;
        private readonly TmdbApiClient tmdbApi;

        public string Name => "PersonCleaner - Update Emby and provider archive";
        public string Key => "PersonCleanerProviderUpdate";
        public string Description => "Snapshots Emby once, seeds unseen write-once truth objects, and concurrently refreshes due TVDB and TMDB observations without changing live Emby metadata.";
        public string Category => "GinjaNinja Tools";

        public ProviderArchiveUpdateTask(ILibraryManager library, IHttpClient http, IJsonSerializer json, IApplicationPaths paths, ILogManager logs)
        {
            this.library = library; this.json = json; logger = logs.GetLogger("PersonCleaner Provider Update");
            tvdbRepository = new TvdbArchiveRepository(paths, logger); tvdbRepository.Initialize();
            tmdbRepository = new TmdbArchiveRepository(paths, logger); tmdbRepository.Initialize();
            unifiedRepository = new UnifiedArchiveRepository(paths); unifiedRepository.Initialize();
            tvdbApi = new TvdbApiClient(http, json, logger, tvdbRepository);
            tmdbApi = new TmdbApiClient(http, json, logger, tmdbRepository);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task Execute(CancellationToken ct, IProgress<double> progress)
        {
            if (!Plugin.Instance.Configuration.EnablePlugin) { logger.Info("PersonCleaner is disabled."); return; }
            var runId = unifiedRepository.StartRun(0);
            try
            {
                progress.Report(1.0);
                logger.Info("Emby snapshot starting: reading in-scope media and local people/credit relationships. No provider requests or Emby writes occur in this phase.");
                var snapshot = Snapshot(runId, ct, progress);
                unifiedRepository.SetSnapshotComplete(runId, snapshot.Items.Count);
                var tvdbEnabled = !string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.TvdbApiKey);
                var tmdbEnabled = !string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.TmdbApiKey);
                var enabledProviders = Convert.ToInt32(tvdbEnabled) + Convert.ToInt32(tmdbEnabled);
                unifiedRepository.SetRunMessage(runId, "Classifying due provider work");
                var tvdbDue = tvdbEnabled ? snapshot.Items.Where(IsTvdbWorkDue).ToList() : new List<BaseItem>();
                var tmdbDue = tmdbEnabled ? snapshot.Items.Where(IsTmdbWorkDue).ToList() : new List<BaseItem>();
                var manifestTotal = tvdbDue.Count + tmdbDue.Count;
                var manifest = new List<ProviderWorkRecord>(manifestTotal);
                manifest.AddRange(tvdbDue.Select(item => new ProviderWorkRecord { Provider = "tvdb", EmbyId = item.InternalId, EntityType = EntityType(item), Route = TvdbRoute(item) }));
                manifest.AddRange(tmdbDue.Select(item => new ProviderWorkRecord { Provider = "tmdb", EmbyId = item.InternalId, EntityType = EntityType(item), Route = TmdbRoute(item) }));
                unifiedRepository.SetRunMessage(runId, "Building provider work manifest");
                logger.Info("Provider work classification complete: {0} TVDB and {1} TMDB items are due; {2} current or inapplicable provider/item combinations require no work and will not be written to provider_work.", tvdbDue.Count, tmdbDue.Count, snapshot.Items.Count * enabledProviders - manifestTotal);
                for (var offset = 0; offset < manifest.Count; offset += 1000)
                {
                    ct.ThrowIfCancellationRequested();
                    unifiedRepository.SeedWorkBatch(runId, manifest.Skip(offset).Take(1000));
                    var seeded = Math.Min(manifest.Count, offset + 1000);
                    progress.Report(10.0 + 2.0 * seeded / Math.Max(1, manifest.Count));
                    if (seeded % 10000 == 0 || seeded == manifest.Count) logger.Info("Provider work manifest progress: {0}/{1} rows prepared.", seeded, manifest.Count);
                }
                unifiedRepository.FinishSeeding(runId);
                unifiedRepository.SetRunMessage(runId, "TVDB and TMDB provider workers running");
                var totalWork = manifestTotal;
                var finished = 0;
                var pipelines = new List<Task>();
                var tvdbProgress = new ProviderProgress("TVDB", tvdbDue.Count, logger);
                var tmdbProgress = new ProviderProgress("TMDB", tmdbDue.Count, logger);
                logger.Info("Provider acquisition starting: TVDB={0} due, TMDB={1} due; only these items will create provider_work rows.", tvdbDue.Count, tmdbDue.Count);
                if (tvdbDue.Count > 0) pipelines.Add(RunWorkers(tvdbDue, Math.Max(1, Plugin.Instance.Configuration.TvdbMaximumConcurrentRequests), async item =>
                {
                    unifiedRepository.StartWork(runId, "tvdb", item.InternalId);
                    var result = await ProcessTvdb(item, ct).ConfigureAwait(false);
                    unifiedRepository.CompleteWork(runId, "tvdb", item.InternalId, result.Success, result.Outcome, result.Error, tvdbApi.CacheHits, tvdbApi.CacheMisses);
                    tvdbProgress.Report(item, result, tvdbApi.CacheHits, tvdbApi.CacheMisses);
                    progress.Report(12.0 + Interlocked.Increment(ref finished) * 88.0 / Math.Max(1, totalWork));
                }, ct));
                else if (!tvdbEnabled) logger.Warn("TVDB key is not configured; the Emby snapshot and TMDB pipeline will still run.");
                else logger.Info("TVDB archive is current; no TVDB work is due.");
                if (tmdbDue.Count > 0) pipelines.Add(RunWorkers(tmdbDue, Math.Max(1, Plugin.Instance.Configuration.TmdbMaximumConcurrentRequests), async item =>
                {
                    unifiedRepository.StartWork(runId, "tmdb", item.InternalId);
                    var result = await ProcessTmdb(item, ct).ConfigureAwait(false);
                    unifiedRepository.CompleteWork(runId, "tmdb", item.InternalId, result.Success, result.Outcome, result.Error, tmdbApi.CacheHits, tmdbApi.CacheMisses);
                    tmdbProgress.Report(item, result, tmdbApi.CacheHits, tmdbApi.CacheMisses);
                    progress.Report(12.0 + Interlocked.Increment(ref finished) * 88.0 / Math.Max(1, totalWork));
                }, ct));
                else if (!tmdbEnabled) logger.Warn("TMDB key is not configured; the Emby snapshot and TVDB pipeline will still run.");
                else logger.Info("TMDB archive is current; no TMDB work is due.");
                await Task.WhenAll(pipelines).ConfigureAwait(false);
                unifiedRepository.FinishRun(runId, "completed", totalWork == 0 ? "Emby snapshot completed; provider archives already current" : "Emby snapshot and due provider work completed");
                progress.Report(100);
                logger.Info("Unified provider update completed: {0} Emby items, {1} provider work rows. Database: {2}", snapshot.Items.Count, totalWork, unifiedRepository.DatabasePath);
            }
            catch (OperationCanceledException) { unifiedRepository.FinishRun(runId, "cancelled", "Stopped; completed work and provider caches are retained"); throw; }
            catch (Exception ex) { unifiedRepository.FinishRun(runId, "failed", ex.Message); throw; }
        }

        private SnapshotResult Snapshot(long runId, CancellationToken ct, IProgress<double> progress)
        {
            var media = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(Series).Name, typeof(Episode).Name, typeof(Movie).Name }, Recursive = true }, ct)
                .Where(x => !(x is Episode episode) || episode.ParentIndexNumber.GetValueOrDefault() >= 1).ToList();
            progress.Report(2.0);
            logger.Info("Emby media read complete: {0} in-scope series, movies and regular episodes. Reading scoped people/credit relationships in local batches.", media.Count);
            var people = new Dictionary<long, Person>();
            var relationships = new List<Tuple<PersonInfo, BaseItem>>();
            var byMediaId = media.ToDictionary(x => x.InternalId);
            var mediaIds = media.Select(x => x.InternalId).ToArray();
            for (var offset = 0; offset < mediaIds.Length; offset += 500)
            {
                ct.ThrowIfCancellationRequested();
                var rows = library.GetItemPeople(new InternalPeopleQuery { ItemIds = mediaIds.Skip(offset).Take(500).ToArray(), PersonTypes = ScreenRoles, EnableIds = true, EnableProviderIds = true, EnableGroupByName = false });
                foreach (var row in rows.Where(x => x.Id > 0))
                {
                    if (!people.ContainsKey(row.Id) && library.GetItemById(row.Id) is Person person) people.Add(row.Id, person);
                    if (byMediaId.TryGetValue(row.ItemId, out var mediaItem)) relationships.Add(Tuple.Create(row, mediaItem));
                }
                var mediaExamined = Math.Min(mediaIds.Length, offset + 500);
                progress.Report(2.0 + 2.0 * mediaExamined / Math.Max(1, mediaIds.Length));
                if (mediaExamined % 10000 == 0 || mediaExamined == mediaIds.Length) logger.Info("Emby relationship read progress: {0}/{1} media examined; {2} raw scoped relationships observed.", mediaExamined, mediaIds.Length, relationships.Count);
            }
            var items = media.Concat<BaseItem>(people.Values).OrderBy(TypeOrder).ThenBy(x => x.InternalId).ToList();
            var archiveItems = items.Select(ToArchiveItem).ToList();
            var relationshipRows = relationships.Select(x => new EmbyRelationshipRecord { PersonId = x.Item1.Id, MediaId = x.Item2.InternalId, MediaType = EntityType(x.Item2), PersonType = x.Item1.Type.ToString(), Role = x.Item1.Role })
                .GroupBy(RelationshipKey, StringComparer.Ordinal).Select(x => x.First()).ToList();
            unifiedRepository.BeginSnapshotWrites(runId, archiveItems.Count, relationshipRows.Count);
            logger.Info("Emby snapshot read complete: {0} entities and {1} distinct scoped relationships. Writing changed/new archive rows only.", archiveItems.Count, relationshipRows.Count);
            for (var offset = 0; offset < archiveItems.Count; offset += 1000)
            {
                ct.ThrowIfCancellationRequested();
                tvdbRepository.SaveEmbyBatch(archiveItems.Skip(offset).Take(1000));
                var processed = Math.Min(archiveItems.Count, offset + 1000);
                unifiedRepository.UpdateSnapshotWrites(runId, "entities", processed, 0);
                progress.Report(4.0 + 3.0 * processed / Math.Max(1, archiveItems.Count));
                if (processed % 10000 == 0 || processed == archiveItems.Count) logger.Info("Emby entity snapshot progress: {0}/{1} examined; unchanged rows are not rewritten.", processed, archiveItems.Count);
            }
            unifiedRepository.UpdateSnapshotWrites(runId, "relationships", archiveItems.Count, 0);
            logger.Info("Emby relationship archive starting: {0} distinct relationships will be examined in batches of 500; existing rows will be skipped and only missing rows will be inserted.", relationshipRows.Count);
            var relationshipTimer = Stopwatch.StartNew();
            var insertedRelationships = 0;
            for (var offset = 0; offset < relationshipRows.Count; offset += 500)
            {
                ct.ThrowIfCancellationRequested();
                var batchCount = Math.Min(500, relationshipRows.Count - offset);
                insertedRelationships += unifiedRepository.SaveRelationshipBatch(relationshipRows.Skip(offset).Take(batchCount));
                var processed = Math.Min(relationshipRows.Count, offset + 500);
                unifiedRepository.UpdateSnapshotWrites(runId, "relationships", archiveItems.Count, processed);
                progress.Report(7.0 + 3.0 * processed / Math.Max(1, relationshipRows.Count));
                if (processed % 5000 == 0 || processed == relationshipRows.Count) logger.Info("Emby relationship archive progress: {0}/{1} examined; {2} missing relationships inserted, {3} existing relationships skipped; {4:F0} examined/second.", processed, relationshipRows.Count, insertedRelationships, processed - insertedRelationships, processed / Math.Max(0.001, relationshipTimer.Elapsed.TotalSeconds));
            }
            logger.Info("Emby snapshot captured {0} entities and {1} scoped credit relationships before provider work.", items.Count, relationships.Count);
            return new SnapshotResult { Items = items };
        }

        private static EmbyArchiveItem ToArchiveItem(BaseItem item) => new EmbyArchiveItem
        {
            Id = item.InternalId, Guid = item.Id.ToString("N"), Type = EntityType(item), Name = item.Name,
            Year = item.ProductionYear, Parent = item.Parent?.InternalId,
            Tvdb = item.GetProviderId(MetadataProviders.Tvdb), Imdb = item.GetProviderId(MetadataProviders.Imdb),
            Tmdb = item.GetProviderId(MetadataProviders.Tmdb), Path = item.Path
        };

        private static string RelationshipKey(EmbyRelationshipRecord value) => value.MediaId.ToString(CultureInfo.InvariantCulture) + ":" + value.PersonId.ToString(CultureInfo.InvariantCulture) + ":" + (value.PersonType ?? "") + ":" + (value.Role ?? "");

        private async Task<WorkResult> ProcessTvdb(BaseItem item, CancellationToken ct)
        {
            var type = EntityType(item); var observed = item.GetProviderId(MetadataProviders.Tvdb);
            var accepted = observed ?? tvdbRepository.GetAcceptedResolvedTvdbId(item.InternalId);
            if (string.IsNullOrWhiteSpace(accepted))
            {
                return WorkResult.Ok("skipped-no-provider-identity");
            }
            try
            {
                var fetchDue = tvdbRepository.IsDue(type + ":" + accepted);
                if (!fetchDue) return WorkResult.Ok("archive-current");
                if (fetchDue) logger.Info("TVDB checking {0} using the archived TVDB identity {1}.", DescribeItem(item), accepted);
                await FetchTvdbEntity(type, accepted, ct).ConfigureAwait(false);
                var provenance = string.IsNullOrWhiteSpace(observed) ? "inferred" : "direct";
                tvdbRepository.SaveItemResolution(item.InternalId, type, observed, accepted, provenance, string.IsNullOrWhiteSpace(observed) ? "existing-resolution" : "emby-tvdb-id", string.IsNullOrWhiteSpace(observed) ? 0.95 : 1.0, 1, json.SerializeToString(new { evidence = "Provider update used an existing accepted identity and did not run inference." }));
                return WorkResult.Ok(provenance);
            }
            catch (HttpException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                tvdbRepository.MarkNotFound(type + ":" + accepted, "TVDB confirmed HTTP 404: " + ex.Message);
                logger.Info("TVDB confirms that {0} is not available at TVDB identity {1}; this absence is cached.", DescribeItem(item), accepted);
                tvdbRepository.SaveItemResolution(item.InternalId, type, observed, accepted, "direct-unavailable", "tvdb-404", 0, 0, json.SerializeToString(new { evidence = "TVDB returned 404; Emby was not modified." }));
                return WorkResult.Ok("direct-unavailable");
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { SafeMarkTvdbFailure(type + ":" + accepted, ex); return WorkResult.Fail("failed", ex.Message); }
        }

        private async Task FetchTvdbEntity(string type, string id, CancellationToken ct)
        {
            var key = type + ":" + id;
            if (!tvdbRepository.IsDue(key)) return;
            var endpoint = type == "person" ? "people" : type == "series" ? "series" : type == "movie" ? "movies" : "episodes";
            var data = await tvdbApi.GetEntity(endpoint, id, ct).ConfigureAwait(false);
            tvdbRepository.SaveEntity(id, type, data, json.SerializeToString(data));
            tvdbRepository.MarkFetch(key, true, null);
            if (type == "series") await FetchTvdbSeriesEpisodes(id, ct).ConfigureAwait(false);
            if (type != "person") await FetchTvdbPeople((data.characters ?? new List<CharacterData>()).Where(TvdbScope.IsScreenCredit).Select(x => x.peopleId), ct).ConfigureAwait(false);
        }

        private async Task FetchTvdbSeriesEpisodes(string seriesId, CancellationToken ct)
        {
            var key = "series-episodes:" + seriesId;
            if (!tvdbRepository.IsDue(key)) return;
            var page = 0; var received = false;
            while (page < 100)
            {
                var response = await tvdbApi.GetSeriesEpisodes(seriesId, page, ct).ConfigureAwait(false);
                if (response?.data == null) break;
                received = true; tvdbRepository.SaveEpisodeBatch(seriesId, response.data);
                var regular = new HashSet<int>((response.data.episodes ?? new List<EpisodeData>()).Where(x => x.seasonNumber >= 1).Select(x => x.id));
                await FetchTvdbPeople((response.data.characters ?? new List<CharacterData>()).Where(TvdbScope.IsScreenCredit).Where(x => x.episodeId.HasValue && regular.Contains(x.episodeId.Value)).Select(x => x.peopleId), ct).ConfigureAwait(false);
                var count = response.data.episodes?.Count ?? 0;
                if (count == 0 || response.links == null || count < response.links.page_size || string.IsNullOrEmpty(response.links.next)) break;
                page++;
            }
            tvdbRepository.MarkFetch(key, received, received ? null : "No official season-1+ episode feed returned");
        }

        private Task FetchTvdbPeople(IEnumerable<int> ids, CancellationToken ct) => Task.WhenAll(ids.Where(x => x > 0).Distinct().Select(x => FetchTvdbEntity("person", x.ToString(CultureInfo.InvariantCulture), ct)));

        private async Task<WorkResult> ProcessTmdb(BaseItem item, CancellationToken ct)
        {
            var type = EntityType(item); var observed = item.GetProviderId(MetadataProviders.Tmdb); var imdb = item.GetProviderId(MetadataProviders.Imdb);
            var episode = item as Episode;
            var coordinateSeriesId = episode?.Series?.GetProviderId(MetadataProviders.Tmdb);
            var hasCoordinate = episode != null && !string.IsNullOrWhiteSpace(coordinateSeriesId) && episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue;
            var retryKey = hasCoordinate
                ? "episode-coordinate:" + coordinateSeriesId + ":" + episode.ParentIndexNumber.Value.ToString(CultureInfo.InvariantCulture) + ":" + episode.IndexNumber.Value.ToString(CultureInfo.InvariantCulture)
                : type + ":" + (observed ?? imdb ?? item.InternalId.ToString(CultureInfo.InvariantCulture));
            if (!tmdbRepository.IsDue(retryKey)) return WorkResult.Ok("archive-current");
            try
            {
                TmdbEntity entity = null; string resolved = null; string provenance; string method; var candidates = new List<TmdbEntity>();
                if (hasCoordinate)
                {
                    logger.Info("TMDB checking {0} using show identity {1}, season {2}, episode {3}.", DescribeItem(item), coordinateSeriesId, episode.ParentIndexNumber.Value, episode.IndexNumber.Value);
                    entity = await tmdbApi.GetEpisode(coordinateSeriesId, episode.ParentIndexNumber.Value, episode.IndexNumber.Value, ct).ConfigureAwait(false); resolved = entity.id.ToString(CultureInfo.InvariantCulture); provenance = "coordinate"; method = "emby-parent-tmdb-season-episode";
                }
                else if (!string.IsNullOrWhiteSpace(observed))
                {
                    logger.Info("TMDB checking {0} using its direct TMDB identity {1}.", DescribeItem(item), observed);
                    entity = await GetTmdbDirect(type, observed, ct).ConfigureAwait(false); resolved = observed; provenance = "direct"; method = "emby-tmdb-id";
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(imdb))
                    {
                        logger.Info("TMDB looking up {0} from its IMDb identity {1}.", DescribeItem(item), imdb);
                        candidates = TypedTmdbResults(await tmdbApi.FindImdb(imdb, ct).ConfigureAwait(false), type).ToList();
                    }
                    tmdbRepository.SaveCandidates(item.InternalId, type, imdb, candidates, x => json.SerializeToString(x));
                    if (candidates.Count == 1)
                    {
                        resolved = candidates[0].id.ToString(CultureInfo.InvariantCulture); provenance = "external-id"; method = "tmdb-find-imdb";
                        if (type != "episode") entity = await GetTmdbDirect(type, resolved, ct).ConfigureAwait(false);
                        else if (candidates[0].show_id.HasValue && candidates[0].season_number.HasValue && candidates[0].episode_number.HasValue) entity = await tmdbApi.GetEpisode(candidates[0].show_id.Value.ToString(CultureInfo.InvariantCulture), candidates[0].season_number.Value, candidates[0].episode_number.Value, ct).ConfigureAwait(false);
                    }
                    else { provenance = candidates.Count == 0 ? "unresolved" : "ambiguous"; method = string.IsNullOrWhiteSpace(imdb) ? "no-tmdb-or-imdb-id" : "tmdb-find-imdb"; }
                }
                tmdbRepository.SaveResolution(item.InternalId, type, observed, resolved, provenance, method, candidates.Count, json.SerializeToString(new { emby_name = item.Name, imdb_id = imdb, candidate_count = candidates.Count }));
                if (entity == null || string.IsNullOrWhiteSpace(resolved)) return WorkResult.Ok(provenance);
                tmdbRepository.SaveEntity(entity.id.ToString(CultureInfo.InvariantCulture), type, entity, json.SerializeToString(entity)); tmdbRepository.MarkFetch(retryKey, true, null);
                return WorkResult.Ok(provenance);
            }
            catch (HttpException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                tmdbRepository.MarkNotFound(retryKey, "TMDB confirmed HTTP 404: " + ex.Message);
                var provenance = string.IsNullOrWhiteSpace(observed) ? "coordinate-unavailable" : "direct-unavailable";
                tmdbRepository.SaveResolution(item.InternalId, type, observed, observed, provenance, "tmdb-404", 0, json.SerializeToString(new { evidence = "TMDB successfully responded that the requested entity or episode coordinate does not exist." }));
                logger.Info("TMDB confirms that {0} is not available by the requested {1}; this absence is cached.", DescribeItem(item), hasCoordinate ? "show/season/episode coordinates" : "direct identity");
                return WorkResult.Ok("provider-confirmed-absent");
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { SafeMarkTmdbFailure(retryKey, ex); return WorkResult.Fail("failed", ex.Message); }
        }

        private void SafeMarkTvdbFailure(string key, Exception original)
        {
            try { tvdbRepository.MarkFetch(key, false, original.Message); }
            catch (Exception cacheError) { logger.ErrorException("TVDB failure-cache write also failed for {0}; work will continue", cacheError, key); }
        }

        private void SafeMarkTmdbFailure(string key, Exception original)
        {
            try { tmdbRepository.MarkFetch(key, false, original.Message); }
            catch (Exception cacheError) { logger.ErrorException("TMDB failure-cache write also failed for {0}; work will continue", cacheError, key); }
        }

        private Task<TmdbEntity> GetTmdbDirect(string type, string id, CancellationToken ct) => type == "person" ? tmdbApi.GetPerson(id, ct) : type == "series" ? tmdbApi.GetSeries(id, ct) : tmdbApi.GetMovie(id, ct);
        private static IEnumerable<TmdbEntity> TypedTmdbResults(TmdbFindResponse r, string type) => r == null ? Enumerable.Empty<TmdbEntity>() : type == "person" ? r.person_results : type == "series" ? r.tv_results : type == "episode" ? r.tv_episode_results : r.movie_results;
        private static async Task RunWorkers(IReadOnlyList<BaseItem> items, int workerCount, Func<BaseItem, Task> action, CancellationToken ct)
        {
            var next = -1;
            var workers = Enumerable.Range(0, Math.Min(Math.Max(1, workerCount), Math.Max(1, items.Count))).Select(async ignored =>
            {
                while (true) { var index = Interlocked.Increment(ref next); if (index >= items.Count) return; ct.ThrowIfCancellationRequested(); await action(items[index]).ConfigureAwait(false); }
            });
            await Task.WhenAll(workers).ConfigureAwait(false);
        }

        private static string TvdbRoute(BaseItem item) => string.IsNullOrWhiteSpace(item.GetProviderId(MetadataProviders.Tvdb)) ? "existing-accepted-or-unresolved" : "direct";
        private static string TmdbRoute(BaseItem item) => item is Episode ? "coordinate-or-imdb" : !string.IsNullOrWhiteSpace(item.GetProviderId(MetadataProviders.Tmdb)) ? "direct" : "imdb-find";
        private bool IsTvdbWorkDue(BaseItem item)
        {
            var id = item.GetProviderId(MetadataProviders.Tvdb) ?? tvdbRepository.GetAcceptedResolvedTvdbId(item.InternalId);
            return !string.IsNullOrWhiteSpace(id) && tvdbRepository.IsDue(EntityType(item) + ":" + id);
        }
        private bool IsTmdbWorkDue(BaseItem item)
        {
            var episode = item as Episode;
            var seriesId = episode?.Series?.GetProviderId(MetadataProviders.Tmdb);
            string key;
            if (episode != null && !string.IsNullOrWhiteSpace(seriesId) && episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
                key = "episode-coordinate:" + seriesId + ":" + episode.ParentIndexNumber.Value.ToString(CultureInfo.InvariantCulture) + ":" + episode.IndexNumber.Value.ToString(CultureInfo.InvariantCulture);
            else
            {
                var identity = item.GetProviderId(MetadataProviders.Tmdb) ?? item.GetProviderId(MetadataProviders.Imdb);
                if (string.IsNullOrWhiteSpace(identity)) return false;
                key = EntityType(item) + ":" + identity;
            }
            return tmdbRepository.IsDue(key);
        }
        private static int TypeOrder(BaseItem item) => item is Series ? 0 : item is Movie ? 1 : item is Person ? 2 : 3;
        private static string EntityType(BaseItem item) => item is Person ? "person" : item is Series ? "series" : item is Episode ? "episode" : "movie";
        private static string DescribeItem(BaseItem item)
        {
            if (item is Episode episode)
            {
                var show = episode.Series?.Name ?? "unknown show";
                var season = episode.ParentIndexNumber?.ToString(CultureInfo.InvariantCulture) ?? "?";
                var number = episode.IndexNumber?.ToString(CultureInfo.InvariantCulture) ?? "?";
                return "episode '" + (episode.Name ?? "(unnamed)") + "' from '" + show + "' S" + season + "E" + number + " (Emby " + item.InternalId.ToString(CultureInfo.InvariantCulture) + ")";
            }
            var kind = item is Person ? "person" : item is Series ? "show" : "movie";
            var year = item.ProductionYear.HasValue ? " (" + item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture) + ")" : "";
            return kind + " '" + (item.Name ?? "(unnamed)") + "'" + year + " (Emby " + item.InternalId.ToString(CultureInfo.InvariantCulture) + ")";
        }
        private sealed class SnapshotResult { public List<BaseItem> Items { get; set; } }
        private sealed class WorkResult
        {
            public bool Success { get; private set; } public string Outcome { get; private set; } public string Error { get; private set; }
            public static WorkResult Ok(string outcome) => new WorkResult { Success = true, Outcome = outcome };
            public static WorkResult Fail(string outcome, string error) => new WorkResult { Success = false, Outcome = outcome, Error = error };
        }

        private sealed class ProviderProgress
        {
            private readonly string provider; private readonly int total; private readonly ILogger logger; private readonly Stopwatch timer = Stopwatch.StartNew();
            private int processed; private int failures; private int skipped; private int current; private int absent; private int evaluated;
            public ProviderProgress(string provider, int total, ILogger logger) { this.provider = provider; this.total = total; this.logger = logger; }
            public void Report(BaseItem item, WorkResult result, long hits, long misses)
            {
                if (!result.Success) Interlocked.Increment(ref failures);
                else if (result.Outcome == "skipped-no-provider-identity") Interlocked.Increment(ref skipped);
                else if (result.Outcome == "archive-current") Interlocked.Increment(ref current);
                else if (result.Outcome == "provider-confirmed-absent" || result.Outcome == "direct-unavailable" || result.Outcome == "coordinate-unavailable") Interlocked.Increment(ref absent);
                else Interlocked.Increment(ref evaluated);
                var done = Interlocked.Increment(ref processed);
                if (done % 1000 == 0 || done == total)
                    logger.Info("{0} provider progress: {1}/{2} Emby items classified; {3} skipped (no {0} identity), {4} archive-current, {5} evaluated/refreshed, {6} provider-confirmed absent, {7} failed; HTTP response cache: {8} hits, {9} network requests; {10:F0} items/second.", provider, done, total, Volatile.Read(ref skipped), Volatile.Read(ref current), Volatile.Read(ref evaluated), Volatile.Read(ref absent), Volatile.Read(ref failures), hits, misses, done / Math.Max(0.001, timer.Elapsed.TotalSeconds));
            }
        }
    }
}
