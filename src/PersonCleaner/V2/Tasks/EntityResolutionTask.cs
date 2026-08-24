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
using PersonCleaner.V2.Domain;
using PersonCleaner.V2.Providers;
using PersonCleaner.V2.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.V2.Tasks
{
    public sealed class EntityResolutionTask : IScheduledTask
    {
        private static readonly PersonType[] Roles = { PersonType.Actor, PersonType.GuestStar, PersonType.Director, PersonType.Writer, PersonType.Producer };
        private readonly ILibraryManager library;
        private readonly IHttpClient http;
        private readonly IJsonSerializer json;
        private readonly IApplicationPaths paths;
        private readonly ILogger logger;

        public string Name => "PersonCleaner - Build person evidence";
        public string Key => "PersonCleanerEntityResolutionV2";
        public string Description => "Reads a bounded Emby media sample, hydrates cached TMDB/TVDB evidence, and rebuilds the read-only person resolution dashboard.";
        public string Category => "GinjaNinja Tools";

        public EntityResolutionTask(ILibraryManager library, IHttpClient http, IJsonSerializer json, IApplicationPaths paths, ILogManager logs)
        { this.library = library; this.http = http; this.json = json; this.paths = paths; logger = logs.GetLogger("PersonCleaner v2"); }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var configuration = Plugin.Instance.Configuration;
            if (!configuration.EnablePlugin) { logger.Info("PersonCleaner is disabled; the task made no changes."); return; }
            using (var repository = new ResolutionRepository(paths))
            {
                repository.Initialize();
                var mode = string.Equals(configuration.ExecutionMode, "Full", StringComparison.OrdinalIgnoreCase) ? "Full" : "Sandbox";
                var runId = repository.BeginRun(mode);
                try
                {
                    logger.Info("PersonCleaner run {0} starting: mode={1}; sample target={2} movie(s) + {2} series; explicit media IDs={3}; explicit person IDs={4}; cache TTL={5} day(s); failure retry={6} minute(s); TMDB configured={7}, workers={8}; TVDB configured={9}, workers={10}.", runId, mode, configuration.SandboxSampleSizePerMediaType, ParseEmbyIds(configuration.SandboxIncludedMediaIds).Count, ParseEmbyIds(configuration.SandboxIncludedPersonIds).Count, configuration.CacheTtlDays, configuration.FailureRetryMinutes, !string.IsNullOrWhiteSpace(configuration.TmdbApiKey), configuration.TmdbMaximumConcurrentRequests, !string.IsNullOrWhiteSpace(configuration.TvdbApiKey), configuration.TvdbMaximumConcurrentRequests);
                    logger.Info("PersonCleaner run {0} workspace: database={1}; raw payload cache={2}.", runId, repository.DatabasePath, repository.PayloadPath);
                    progress.Report(1);
                    var snapshot = CaptureSnapshot(mode, configuration.SandboxSampleSizePerMediaType, configuration.SandboxSeed, configuration.SandboxIncludedMediaIds, configuration.SandboxIncludedPersonIds, cancellationToken);
                    repository.ReplaceSnapshot(runId, snapshot.Media, snapshot.People, snapshot.Credits, snapshot.GlobalPeople);
                    logger.Info("PersonCleaner run {0} snapshot: {1} provider-addressable movie(s) and {2} series were eligible; selected {3} movie(s), {4} series, including {5} explicit title(s) ({6} requested directly, {7} found from requested people); captured {8} in-scope local people, {9} local credit relationships and {10} global Emby person binding rows. Live Emby remains read-only.", runId, snapshot.EligibleMovies, snapshot.EligibleSeries, snapshot.Media.Count(x => x.MediaType == MediaTypes.Movie), snapshot.Media.Count(x => x.MediaType == MediaTypes.Series), snapshot.ExplicitMediaCount, snapshot.DirectExplicitMediaCount, snapshot.PersonExplicitMediaCount, snapshot.People.Count, snapshot.Credits.Count, snapshot.GlobalPeople.Count);
                    progress.Report(10);

                    var api = new ProviderApiClient(http, json, logger);
                    var hydration = new HydrationService(repository, api, new PayloadFlattener(json), logger);
                    repository.UpdateRun(runId, "media", "Hydrating provider media credits");
                    var mediaWork = repository.PendingMedia();
                    logger.Info("PersonCleaner run {0} media phase starting: {1} queue item(s) ({2}); bounded parallelism TMDB={3}, TVDB={4}.", runId, mediaWork.Count, WorkBreakdown(mediaWork), configuration.TmdbMaximumConcurrentRequests, configuration.TvdbMaximumConcurrentRequests);
                    var mediaMetrics = await RunHydrationPhase("media", runId, mediaWork, hydration, repository, configuration, progress, 10, 35, 10, cancellationToken).ConfigureAwait(false);
                    logger.Info("PersonCleaner run {0} media phase complete: {1}.", runId, mediaMetrics.Summary());

                    var personSeeds = repository.SeedDiscoveredPeople();
                    logger.Info("PersonCleaner run {0} person scoping: media discovery found {1} unique provider people (TMDB={2}, TVDB={3}); {4} are graph-eligible by current provider ID or normalized same-title name (TMDB={5}, TVDB={6}); {7} current in-scope bindings are queued for validation (TMDB={8}, TVDB={9}). Validation-only people are fully cached but cannot seed the identity graph.", runId, personSeeds.DiscoveredTotal, personSeeds.DiscoveredTmdb, personSeeds.DiscoveredTvdb, personSeeds.SelectedTotal, personSeeds.SelectedTmdb, personSeeds.SelectedTvdb, personSeeds.ValidationTotal, personSeeds.ValidationTmdb, personSeeds.ValidationTvdb);
                    repository.UpdateRun(runId, "people", "Enriching people discovered from media credits");
                    var peopleWork = repository.PendingPeople();
                    logger.Info("PersonCleaner run {0} person phase starting: {1} unique provider person(s) ({2}); bounded parallelism TMDB={3}, TVDB={4}. Fresh cache entries bypass both network and JSON parsing.", runId, peopleWork.Count, WorkBreakdown(peopleWork), configuration.TmdbMaximumConcurrentRequests, configuration.TvdbMaximumConcurrentRequests);
                    var peopleMetrics = await RunHydrationPhase("person", runId, peopleWork, hydration, repository, configuration, progress, 45, 40, 100, cancellationToken).ConfigureAwait(false);
                    logger.Info("PersonCleaner run {0} person phase complete: {1}.", runId, peopleMetrics.Summary());

                    repository.UpdateRun(runId, "resolution", "Calculating provider graph, uncertainty and local-media anchors");
                    var settings = new ResolutionSettings
                    {
                        AutomaticMatchThreshold = configuration.AutomaticMatchThreshold,
                        HumanReviewThreshold = configuration.HumanReviewThreshold,
                        MaximumMediaExamples = configuration.MaximumMediaExamplesPerDecision
                    };
                    var resolutionInput = repository.LoadResolutionInput(runId);
                    foreach (var correction in resolutionInput.CorrectionApplications.Where(x => x.Triggered))
                        logger.Info("PersonCleaner run {0} provider correction {1} triggered: matched={2}, changed={3}. {4}", runId, correction.CorrectionId, correction.MatchedCount, correction.ChangedCount, correction.Summary);
                    var inactiveCorrections = resolutionInput.CorrectionApplications.Count(x => !x.Triggered);
                    if (resolutionInput.CorrectionApplications.Count > 0)
                        logger.Info("PersonCleaner run {0} correction overlay: active={1}, triggered={2}, not-triggered={3}.", runId, resolutionInput.CorrectionApplications.Count, resolutionInput.CorrectionApplications.Count(x => x.Triggered), inactiveCorrections);
                    logger.Info("PersonCleaner run {0} offline resolution starting: {1} flattened provider people (TMDB={2}, TVDB={3}), {4} local people, {5} local credits, {6} media and {7} operator bridge(s). No provider requests occur in this phase.", runId, resolutionInput.ProviderPeople.Count, resolutionInput.ProviderPeople.Count(x => x.Provider == ProviderNames.Tmdb), resolutionInput.ProviderPeople.Count(x => x.Provider == ProviderNames.Tvdb), resolutionInput.LocalPeople.Count, resolutionInput.LocalCredits.Count, resolutionInput.Media.Count, resolutionInput.Bridges.Count);
                    var engine = new ResolutionEngine();
                    var decisions = engine.Resolve(resolutionInput, settings).ToList();
                    var diagnostic = engine.Diagnostics;
                    logger.Info("PersonCleaner run {0} candidate gate: examined {1} cross-provider blocked pair(s), admitted {2} ({3} hard external-ID, {4} shared-title plus compatible-name/alias), operator-rejected={5}; evidence model v2 produced automatic={6}, human-review={7}, below-review={8}, constraint-blocked={9}, graph-components={10}.", runId, diagnostic.BlockedCrossProviderPairs, diagnostic.AdmittedCandidates, diagnostic.HardIdentityCandidates, diagnostic.NameCompatibleCandidates, diagnostic.RejectedByOperator, diagnostic.AutomaticCandidates, diagnostic.ReviewCandidates, diagnostic.BelowReviewCandidates, diagnostic.ConstraintBlockedCandidates, diagnostic.GraphComponents);
                    logger.Info("PersonCleaner run {0} offline resolution calculated {1} decision summaries ({2}); persisting pre-rendered decisions, evidence and impacted media.", runId, decisions.Count, DecisionBreakdown(decisions));
                    repository.SaveDecisions(runId, decisions, engine.PairEvaluations, engine.Clusters);
                    repository.FinishRun(runId, "completed", "Evidence is ready. The dashboard reads only pre-calculated rows; live Emby was not changed.", decisions.Count);
                    progress.Report(100);
                    logger.Info("PersonCleaner run {0} completed with {1} decision summaries ({2}). Workspace: {3}", runId, decisions.Count, DecisionBreakdown(decisions), repository.DatabasePath);
                }
                catch (OperationCanceledException)
                {
                    repository.FinishRun(runId, "cancelled", "Cancelled; completed payload cache and flattened evidence were retained.", 0);
                    logger.Warn("PersonCleaner run {0} was cancelled. Completed cache and flattened rows remain reusable at {1}.", runId, repository.WorkspacePath);
                    throw;
                }
                catch (Exception ex)
                {
                    repository.FinishRun(runId, "failed", ex.Message, 0);
                    logger.ErrorException("PersonCleaner run " + runId + " failed. Completed cache and flattened rows remain reusable at " + repository.WorkspacePath, ex);
                    throw;
                }
            }
        }

        private async Task<PhaseMetrics> RunHydrationPhase(
            string phase,
            long runId,
            IReadOnlyCollection<QueueItem> work,
            HydrationService hydration,
            ResolutionRepository repository,
            Configuration.PluginConfiguration configuration,
            IProgress<double> progress,
            double progressStart,
            double progressRange,
            int logInterval,
            CancellationToken cancellationToken)
        {
            var items = work.ToList();
            var metrics = new PhaseMetrics(items);
            var completed = 0;
            var progressSync = new object();
            var fatalProviders = new Dictionary<string, string>(StringComparer.Ordinal);

            async Task ProcessOne(QueueItem item)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HydrationOutcome outcome;
                string fatal;
                lock (progressSync) fatalProviders.TryGetValue(item.Provider, out fatal);
                if (fatal != null)
                {
                    repository.MarkQueue(item, "skipped", fatal);
                    repository.RecordAcquisition(runId, item, AcquisitionStates.Unavailable, "authentication", fatal);
                    outcome = HydrationOutcome.Skipped;
                }
                else
                {
                    outcome = await hydration.Process(item, runId, configuration.CacheTtlDays, configuration.FailureRetryMinutes, ProviderConfigured(item.Provider, configuration), cancellationToken).ConfigureAwait(false);
                    if (outcome == HydrationOutcome.AuthenticationFailed)
                        lock (progressSync) fatalProviders[item.Provider] = item.Provider.ToUpperInvariant() + " authentication failed; remaining provider work was not requested.";
                }

                metrics.Record(item.Provider, outcome);
                var processed = Interlocked.Increment(ref completed);
                lock (progressSync) progress.Report(progressStart + progressRange * processed / Math.Max(1, items.Count));
                if (ShouldLogProgress(processed, items.Count, logInterval))
                    logger.Info("PersonCleaner run {0} {1} progress: {2}/{3}; {4}.", runId, phase, processed, items.Count, metrics.Summary());
            }

            var tmdb = items.Where(x => x.Provider == ProviderNames.Tmdb).ToList();
            var tvdb = items.Where(x => x.Provider == ProviderNames.Tvdb).ToList();
            var other = items.Where(x => x.Provider != ProviderNames.Tmdb && x.Provider != ProviderNames.Tvdb).ToList();
            await Task.WhenAll(
                RunProviderWorkers(tmdb, configuration.TmdbMaximumConcurrentRequests, ProcessOne, cancellationToken),
                RunProviderWorkers(tvdb, configuration.TvdbMaximumConcurrentRequests, ProcessOne, cancellationToken),
                RunProviderWorkers(other, 1, ProcessOne, cancellationToken)).ConfigureAwait(false);
            lock (progressSync)
                if (fatalProviders.Count > 0) throw new InvalidOperationException(string.Join(" ", fatalProviders.OrderBy(x => x.Key).Select(x => x.Value)));
            return metrics;
        }

        private static Task RunProviderWorkers(
            IReadOnlyList<QueueItem> work,
            int maximumConcurrency,
            Func<QueueItem, Task> process,
            CancellationToken cancellationToken)
        {
            if (work.Count == 0) return Task.CompletedTask;
            var cursor = -1;
            var workerCount = Math.Min(Math.Max(1, maximumConcurrency), work.Count);
            var workers = Enumerable.Range(0, workerCount).Select(_ => Worker()).ToArray();
            return Task.WhenAll(workers);

            async Task Worker()
            {
                // Prevent a cache-only worker from completing synchronously
                // before the other provider's pipeline has been started.
                await Task.Yield();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = Interlocked.Increment(ref cursor);
                    if (index >= work.Count) return;
                    await process(work[index]).ConfigureAwait(false);
                }
            }
        }

        private SnapshotData CaptureSnapshot(string mode, int sampleSize, int seed, string includedMediaIds, string includedPersonIds, CancellationToken cancellationToken)
        {
            var allItems = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(Movie).Name, typeof(Series).Name }, Recursive = true }, cancellationToken)
                .Where(x => x is Movie || x is Series).Select(ToMedia).Where(x => !string.IsNullOrWhiteSpace(x.TmdbId) || !string.IsNullOrWhiteSpace(x.TvdbId)).ToList();
            var eligibleMovies = allItems.Count(x => x.MediaType == MediaTypes.Movie);
            var eligibleSeries = allItems.Count(x => x.MediaType == MediaTypes.Series);
            var globalPeople = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(Person).Name }, Recursive = true }, cancellationToken)
                .OfType<Person>().Select(ToLocalPerson).ToList();
            var sandbox = string.Equals(mode, "Sandbox", StringComparison.OrdinalIgnoreCase);
            var requestedMediaIds = sandbox ? ParseEmbyIds(includedMediaIds) : new HashSet<long>();
            var requestedPersonIds = sandbox ? ParseEmbyIds(includedPersonIds) : new HashSet<long>();
            var eligibleById = allItems.ToDictionary(x => x.EmbyId);
            var directExplicit = requestedMediaIds.Where(eligibleById.ContainsKey).Select(x => eligibleById[x]).ToList();
            var personExplicit = new List<MediaSeed>();
            if (requestedPersonIds.Count > 0)
            {
                personExplicit = library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { typeof(Movie).Name, typeof(Series).Name },
                    Recursive = true,
                    PersonIds = requestedPersonIds.ToArray()
                }, cancellationToken).Where(x => x is Movie || x is Series).Select(ToMedia)
                    .Where(x => (!string.IsNullOrWhiteSpace(x.TmdbId) || !string.IsNullOrWhiteSpace(x.TvdbId)) && eligibleById.ContainsKey(x.EmbyId))
                    .GroupBy(x => x.EmbyId).Select(x => x.First()).ToList();
            }
            var explicitMedia = directExplicit.Concat(personExplicit).GroupBy(x => x.EmbyId).Select(x => x.First()).ToList();
            var items = allItems;
            if (sandbox)
                items = allItems.GroupBy(x => x.MediaType).SelectMany(x => x.OrderBy(y => SampleKey(y.EmbyId, seed)).ThenBy(y => y.EmbyId).Take(Math.Max(1, sampleSize)))
                    .Concat(explicitMedia).GroupBy(x => x.EmbyId).Select(x => x.First()).ToList();

            var missingMedia = requestedMediaIds.Count - directExplicit.Count;
            var missingPeople = requestedPersonIds.Count(x => globalPeople.All(y => y.EmbyId != x));
            if (missingMedia > 0) logger.Warn("PersonCleaner explicit sandbox scope ignored {0} Emby media ID(s) that were missing, unsupported or lacked a TMDB/TVDB media ID.", missingMedia);
            if (missingPeople > 0) logger.Warn("PersonCleaner explicit sandbox scope ignored {0} Emby person ID(s) that no longer exist.", missingPeople);

            var credits = new List<LocalCredit>();
            var people = new Dictionary<long, LocalPerson>();
            var ids = items.Select(x => x.EmbyId).ToArray();
            for (var offset = 0; offset < ids.Length; offset += 250)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = library.GetItemPeople(new InternalPeopleQuery { ItemIds = ids.Skip(offset).Take(250).ToArray(), PersonTypes = Roles, EnableIds = true, EnableProviderIds = true, EnableGroupByName = false });
                foreach (var row in rows.Where(x => x.Id > 0))
                {
                    credits.Add(new LocalCredit { PersonEmbyId = row.Id, MediaEmbyId = row.ItemId, Role = row.Type + (string.IsNullOrWhiteSpace(row.Role) ? string.Empty : ": " + row.Role) });
                    if (!people.ContainsKey(row.Id) && library.GetItemById(row.Id) is Person person)
                        people[row.Id] = new LocalPerson { EmbyId = person.InternalId, Name = person.Name, TmdbId = person.GetProviderId(MetadataProviders.Tmdb), TvdbId = person.GetProviderId(MetadataProviders.Tvdb), ImdbId = person.GetProviderId(MetadataProviders.Imdb) };
                }
            }
            return new SnapshotData
            {
                Media = items, People = people.Values.ToList(), Credits = credits, GlobalPeople = globalPeople,
                EligibleMovies = eligibleMovies, EligibleSeries = eligibleSeries,
                DirectExplicitMediaCount = directExplicit.Count, PersonExplicitMediaCount = personExplicit.Count,
                ExplicitMediaCount = explicitMedia.Count
            };
        }

        private static MediaSeed ToMedia(BaseItem item) => new MediaSeed
        {
            EmbyId = item.InternalId,
            MediaType = item is Movie ? MediaTypes.Movie : MediaTypes.Series,
            Name = item.Name,
            Year = item.ProductionYear,
            TmdbId = item.GetProviderId(MetadataProviders.Tmdb),
            TvdbId = item.GetProviderId(MetadataProviders.Tvdb),
            ImdbId = item.GetProviderId(MetadataProviders.Imdb)
        };

        private static LocalPerson ToLocalPerson(Person person) => new LocalPerson
        {
            EmbyId = person.InternalId,
            Name = person.Name,
            TmdbId = person.GetProviderId(MetadataProviders.Tmdb),
            TvdbId = person.GetProviderId(MetadataProviders.Tvdb),
            ImdbId = person.GetProviderId(MetadataProviders.Imdb)
        };

        private static HashSet<long> ParseEmbyIds(string value) => new HashSet<long>((value ?? string.Empty)
            .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : 0)
            .Where(x => x > 0));

        private static ulong SampleKey(long value, int seed)
        {
            unchecked
            {
                var hash = 1469598103934665603UL;
                foreach (var character in value.ToString(CultureInfo.InvariantCulture) + ":" + seed.ToString(CultureInfo.InvariantCulture)) { hash ^= character; hash *= 1099511628211UL; }
                return hash;
            }
        }

        private static bool ProviderConfigured(string provider, Configuration.PluginConfiguration configuration) => provider == ProviderNames.Tmdb ? !string.IsNullOrWhiteSpace(configuration.TmdbApiKey) : !string.IsNullOrWhiteSpace(configuration.TvdbApiKey);

        private static bool ShouldLogProgress(int processed, int total, int interval) => processed == 1 || processed == total || processed % interval == 0;
        private static string WorkBreakdown(IEnumerable<QueueItem> work) => string.Join(", ", work.GroupBy(x => x.Provider.ToUpperInvariant() + (x.EntityType == "media" ? " " + x.MediaType : string.Empty)).OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Count()));
        private static string DecisionBreakdown(IEnumerable<ResolutionDecision> decisions) => string.Join(", ", decisions.GroupBy(x => x.Status).OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Count()));

        private sealed class SnapshotData
        {
            public List<MediaSeed> Media { get; set; }
            public List<LocalPerson> People { get; set; }
            public List<LocalPerson> GlobalPeople { get; set; }
            public List<LocalCredit> Credits { get; set; }
            public int EligibleMovies { get; set; }
            public int EligibleSeries { get; set; }
            public int DirectExplicitMediaCount { get; set; }
            public int PersonExplicitMediaCount { get; set; }
            public int ExplicitMediaCount { get; set; }
        }

        private sealed class PhaseMetrics
        {
            private readonly int tmdbTotal;
            private readonly int tvdbTotal;
            private int tmdbProcessed;
            private int tvdbProcessed;
            private int cacheHits;
            private int changed;
            private int unchanged;
            private int deferred;
            private int absent;
            private int authenticationFailed;
            private int failed;
            private int skipped;

            public PhaseMetrics(IEnumerable<QueueItem> work)
            {
                var items = work.ToList(); tmdbTotal = items.Count(x => x.Provider == ProviderNames.Tmdb); tvdbTotal = items.Count(x => x.Provider == ProviderNames.Tvdb);
            }

            public void Record(string provider, HydrationOutcome outcome)
            {
                if (provider == ProviderNames.Tmdb) Interlocked.Increment(ref tmdbProcessed); else if (provider == ProviderNames.Tvdb) Interlocked.Increment(ref tvdbProcessed);
                switch (outcome)
                {
                    case HydrationOutcome.CacheHit: Interlocked.Increment(ref cacheHits); break;
                    case HydrationOutcome.FetchedChanged: Interlocked.Increment(ref changed); break;
                    case HydrationOutcome.FetchedUnchanged: Interlocked.Increment(ref unchanged); break;
                    case HydrationOutcome.Deferred: Interlocked.Increment(ref deferred); break;
                    case HydrationOutcome.Absent: Interlocked.Increment(ref absent); break;
                    case HydrationOutcome.AuthenticationFailed: Interlocked.Increment(ref authenticationFailed); break;
                    case HydrationOutcome.Failed: Interlocked.Increment(ref failed); break;
                    case HydrationOutcome.Skipped: Interlocked.Increment(ref skipped); break;
                }
            }

            public string Summary() => "TMDB=" + Volatile.Read(ref tmdbProcessed) + "/" + tmdbTotal + ", TVDB=" + Volatile.Read(ref tvdbProcessed) + "/" + tvdbTotal + ", cache hits=" + Volatile.Read(ref cacheHits) + ", fetched+flattened=" + Volatile.Read(ref changed) + ", fetched unchanged=" + Volatile.Read(ref unchanged) + ", provider-confirmed absent=" + Volatile.Read(ref absent) + ", authentication failures=" + Volatile.Read(ref authenticationFailed) + ", deferred=" + Volatile.Read(ref deferred) + ", failed=" + Volatile.Read(ref failed) + ", skipped=" + Volatile.Read(ref skipped);
        }
    }
}
