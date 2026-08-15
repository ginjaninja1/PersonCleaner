using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using PersonCleaner.Storage;
using PersonCleaner.Tmdb;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.Tasks
{
    public abstract class TmdbArchiveTaskBase : IScheduledTask
    {
        private readonly ILibraryManager library;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private readonly TmdbArchiveRepository repository;
        private readonly TvdbArchiveRepository sharedRepository;
        private readonly TmdbApiClient api;
        protected abstract bool IsPreview { get; }
        public abstract string Name { get; }
        public abstract string Key { get; }
        public string Description => IsPreview ? "Archives a small direct-TMDB/IMDb-fallback sample for inspection." : "Resumable direct TMDB export for in-scope Emby shows, episodes, movies and people.";
        public string Category => "GinjaNinja Tools";

        protected TmdbArchiveTaskBase(ILibraryManager library, IHttpClient http, IJsonSerializer json, IApplicationPaths paths, ILogManager logs)
        {
            this.library = library; this.json = json; logger = logs.GetLogger("TMDB Archive");
            sharedRepository = new TvdbArchiveRepository(paths, logger); sharedRepository.Initialize();
            repository = new TmdbArchiveRepository(paths, logger); repository.Initialize();
            api = new TmdbApiClient(http, json, logger, repository);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task Execute(CancellationToken ct, IProgress<double> progress)
        {
            if (!Plugin.Instance.Configuration.EnablePlugin) return;
            if (string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.TmdbApiKey)) throw new InvalidOperationException("TMDB API key is not configured.");
            if (!IsPreview && Plugin.Instance.Configuration.RequireSuccessfulPreview && !repository.HasPreview()) throw new InvalidOperationException("Run 'TMDB Archive - Preview first items' successfully before the full export.");

            var media = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(Series).Name, typeof(Episode).Name, typeof(Movie).Name }, Recursive = true }, ct)
                .Where(x => !(x is Episode episode) || episode.ParentIndexNumber.GetValueOrDefault() >= 1).ToList();
            var roles = new[] { PersonType.Actor, PersonType.GuestStar, PersonType.Director, PersonType.Writer, PersonType.Producer };
            var peopleIds = new HashSet<long>(); var mediaIds = media.Select(x => x.InternalId).ToArray();
            for (var offset = 0; offset < mediaIds.Length; offset += 500)
                foreach (var p in library.GetItemPeople(new InternalPeopleQuery { ItemIds = mediaIds.Skip(offset).Take(500).ToArray(), PersonTypes = roles, EnableIds = true, EnableProviderIds = true, EnableGroupByName = true }).Where(x => x.Id > 0)) peopleIds.Add(p.Id);
            var items = media.Concat<BaseItem>(peopleIds.Select(library.GetItemById).OfType<Person>()).OrderBy(TypeOrder).ThenBy(x => x.InternalId).ToList();
            if (IsPreview)
            {
                var limit = Math.Max(1, Plugin.Instance.Configuration.PreviewItemLimit);
                items = items.GroupBy(EntityType).SelectMany(g => g.Where(HasDirectRoute).Take(limit).Concat(g.Where(x => !HasDirectRoute(x) && x.HasProviderId(MetadataProviders.Imdb)).Take(limit))).ToList();
            }

            var checkpoint = IsPreview ? Tuple.Create(0, 0, 0) : repository.GetCheckpoint(Key, items.Count);
            var done = Math.Min(checkpoint.Item1, items.Count); var successes = checkpoint.Item2; var failures = checkpoint.Item3;
            repository.SetRun(Key, "running", items.Count, done, successes, failures, done > 0 ? (long?)items[done - 1].InternalId : null, "Starting or resuming");
            try
            {
                while (done < items.Count)
                {
                    var batchSize = items[done] is Person ? Math.Min(Math.Max(1, Plugin.Instance.Configuration.PersonWorkerCount), items.Count - done) : 1;
                    while (batchSize > 1 && !(items[done + batchSize - 1] is Person)) batchSize--;
                    var results = await Task.WhenAll(items.Skip(done).Take(batchSize).Select(x => Process(x, ct))).ConfigureAwait(false);
                    done += results.Length; successes += results.Count(x => x); failures += results.Count(x => !x);
                    progress.Report(items.Count == 0 ? 100 : done * 100.0 / items.Count);
                    if (!(items[done - 1] is Person) || done % 250 == 0) repository.SetRun(Key, "running", items.Count, done, successes, failures, items[done - 1].InternalId, items[done - 1].Name);
                }
                repository.SetRun(Key, "completed", items.Count, done, successes, failures, done > 0 ? (long?)items[done - 1].InternalId : null, "Finished");
                logger.Info("TMDB Archive {0}: {1} processed, {2} archived, {3} unresolved/failed. Database: {4}", IsPreview ? "preview" : "export", done, successes, failures, repository.DatabasePath);
            }
            catch (OperationCanceledException) { repository.SetRun(Key, "cancelled", items.Count, done, successes, failures, done > 0 ? (long?)items[done - 1].InternalId : null, "Stopped; next run resumes"); throw; }
        }

        private async Task<bool> Process(BaseItem item, CancellationToken ct)
        {
            var type = EntityType(item); var observed = item.GetProviderId(MetadataProviders.Tmdb); var imdb = item.GetProviderId(MetadataProviders.Imdb);
            sharedRepository.SaveEmby(item.InternalId, item.Id.ToString("N"), type, item.Name, item.ProductionYear, item.Parent?.InternalId, item.GetProviderId(MetadataProviders.Tvdb), imdb, observed, item.Path);
            try
            {
                TmdbEntity entity = null; string resolved = null; string provenance; string method; var candidates = new List<TmdbEntity>();
                if (item is Episode episode && episode.Series != null && episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
                {
                    var seriesId = episode.Series.GetProviderId(MetadataProviders.Tmdb);
                    if (!string.IsNullOrWhiteSpace(seriesId)) { entity = await api.GetEpisode(seriesId, episode.ParentIndexNumber.Value, episode.IndexNumber.Value, ct).ConfigureAwait(false); resolved = entity.id.ToString(CultureInfo.InvariantCulture); provenance = "coordinate"; method = "emby-parent-tmdb-season-episode"; }
                    else { provenance = "unresolved"; method = "missing-parent-tmdb-id"; }
                }
                else if (!string.IsNullOrWhiteSpace(observed))
                {
                    entity = await GetDirect(type, observed, ct).ConfigureAwait(false); resolved = observed; provenance = "direct"; method = "emby-tmdb-id";
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(imdb)) candidates = TypedResults(await api.FindImdb(imdb, ct).ConfigureAwait(false), type).ToList();
                    repository.SaveCandidates(item.InternalId, type, imdb, candidates, x => json.SerializeToString(x));
                    if (candidates.Count == 1)
                    {
                        resolved = candidates[0].id.ToString(CultureInfo.InvariantCulture); provenance = "external-id"; method = "tmdb-find-imdb";
                        if (type != "episode") entity = await GetDirect(type, resolved, ct).ConfigureAwait(false);
                        else if (candidates[0].show_id.HasValue && candidates[0].season_number.HasValue && candidates[0].episode_number.HasValue) entity = await api.GetEpisode(candidates[0].show_id.Value.ToString(CultureInfo.InvariantCulture), candidates[0].season_number.Value, candidates[0].episode_number.Value, ct).ConfigureAwait(false);
                    }
                    else { provenance = candidates.Count == 0 ? "unresolved" : "ambiguous"; method = string.IsNullOrWhiteSpace(imdb) ? "no-tmdb-or-imdb-id" : "tmdb-find-imdb"; }
                }
                repository.SaveResolution(item.InternalId, type, observed, resolved, provenance, method, candidates.Count, json.SerializeToString(new { emby_name = item.Name, imdb_id = imdb, candidate_count = candidates.Count }));
                if (entity == null || string.IsNullOrWhiteSpace(resolved)) return false;
                repository.SaveEntity(entity.id.ToString(CultureInfo.InvariantCulture), type, entity, json.SerializeToString(entity)); repository.MarkFetch(type + ":" + resolved, true, null); return true;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                repository.MarkFetch(type + ":" + (observed ?? imdb ?? item.InternalId.ToString(CultureInfo.InvariantCulture)), false, ex.Message);
                repository.SaveResolution(item.InternalId, type, observed, null, "failed", "tmdb-request-failed", 0, json.SerializeToString(new { error = ex.Message }));
                logger.ErrorException("TMDB extraction failed for {0} Emby={1} '{2}'", ex, type, item.InternalId, item.Name); return false;
            }
        }

        private Task<TmdbEntity> GetDirect(string type, string id, CancellationToken ct) => type == "person" ? api.GetPerson(id, ct) : type == "series" ? api.GetSeries(id, ct) : api.GetMovie(id, ct);
        private static IEnumerable<TmdbEntity> TypedResults(TmdbFindResponse r, string type) => r == null ? Enumerable.Empty<TmdbEntity>() : type == "person" ? r.person_results : type == "series" ? r.tv_results : type == "episode" ? r.tv_episode_results : r.movie_results;
        private static bool HasDirectRoute(BaseItem x) => x is Episode e ? e.Series != null && e.Series.HasProviderId(MetadataProviders.Tmdb) && e.ParentIndexNumber.HasValue && e.IndexNumber.HasValue : x.HasProviderId(MetadataProviders.Tmdb);
        private static int TypeOrder(BaseItem x) => x is Series ? 0 : x is Movie ? 1 : x is Person ? 2 : 3;
        private static string EntityType(BaseItem x) => x is Person ? "person" : x is Series ? "series" : x is Episode ? "episode" : "movie";
    }

    public sealed class TmdbArchivePreviewTask : TmdbArchiveTaskBase
    {
        protected override bool IsPreview => true; public override string Name => "TMDB Archive - Preview first items"; public override string Key => "TmdbArchivePreview";
        public TmdbArchivePreviewTask(ILibraryManager l, IHttpClient h, IJsonSerializer j, IApplicationPaths p, ILogManager m) : base(l, h, j, p, m) { }
    }

    public sealed class TmdbArchiveFullTask : TmdbArchiveTaskBase
    {
        protected override bool IsPreview => false; public override string Name => "TMDB Archive - Full resumable export"; public override string Key => "TmdbArchiveFull";
        public TmdbArchiveFullTask(ILibraryManager l, IHttpClient h, IJsonSerializer j, IApplicationPaths p, ILogManager m) : base(l, h, j, p, m) { }
    }
}
