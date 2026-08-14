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
using PersonCleaner.Tvdb;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.Tasks
{
    public sealed class TvdbResolutionEvaluationTask : IScheduledTask
    {
        private static readonly PersonType[] ScreenRoles = { PersonType.Actor, PersonType.GuestStar, PersonType.Director, PersonType.Writer, PersonType.Producer };
        private readonly ILibraryManager library;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private readonly TvdbArchiveRepository repository;
        private readonly TvdbIdentityResolver resolver;
        public string Name => "TVDB Archive - Evaluate identity resolver";
        public string Key => "TvdbResolutionEvaluation";
        public string Description => "Withholds known-good TVDB IDs and measures resolver precision for in-scope movies, series, season-1+ episodes and screen-credit people.";
        public string Category => "GinjaNinja Tools";
        public TvdbResolutionEvaluationTask(ILibraryManager l, IHttpClient h, IJsonSerializer j, IApplicationPaths p, ILogManager m)
        { library = l; json = j; logger = m.GetLogger("TVDB Archive"); repository = new TvdbArchiveRepository(p, logger); repository.Initialize(); resolver = new TvdbIdentityResolver(new TvdbApiClient(h, j, logger, repository), l); }
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task Execute(CancellationToken ct, IProgress<double> progress)
        {
            var limit = Math.Max(1, Plugin.Instance.Configuration.ResolutionEvaluationItemsPerType);
            var media = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(Movie).Name, typeof(Series).Name, typeof(Episode).Name }, Recursive = true }, ct)
                .Where(InScopeMedia).ToList();
            var inScopePersonIds = new HashSet<long>();
            var mediaIds = media.Select(x => x.InternalId).ToArray();
            for (var offset = 0; offset < mediaIds.Length; offset += 500)
                foreach (var person in library.GetItemPeople(new InternalPeopleQuery
                {
                    ItemIds = mediaIds.Skip(offset).Take(500).ToArray(), PersonTypes = ScreenRoles,
                    EnableIds = true, EnableProviderIds = true, EnableGroupByName = true
                }).Where(x => x.Id > 0)) inScopePersonIds.Add(person.Id);
            var people = inScopePersonIds.Select(id => library.GetItemById(id)).OfType<Person>().Where(x => x.HasProviderId(MetadataProviders.Tvdb));
            var sample = media.Where(x => x.HasProviderId(MetadataProviders.Tvdb)).Concat<BaseItem>(people)
                .GroupBy(TvdbIdentityResolver.TypeOf).SelectMany(g => g.OrderBy(x => x.InternalId).Take(limit)).ToList();

            var done = 0; var correct = 0;
            foreach (var item in sample)
            {
                ct.ThrowIfCancellationRequested();
                var truth = item.GetProviderId(MetadataProviders.Tvdb);
                ResolutionResult result;
                try { result = await resolver.Resolve(item, ct).ConfigureAwait(false); }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { result = new ResolutionResult { Method = "error", Confidence = 0, Evidence = ex.Message }; }
                var isCorrect = string.Equals(truth, result.TvdbId, StringComparison.Ordinal);
                if (isCorrect) correct++;
                repository.SaveResolutionEvaluation(item.InternalId, TvdbIdentityResolver.TypeOf(item), item.Name, truth, result.TvdbId, result.Method, result.Confidence, result.CandidateCount, isCorrect, json.SerializeToString(result));
                logger.Info("Resolver evaluation {0} '{1}' Emby={2}: withheld={3}, predicted={4}, method={5}, confidence={6:F3}, correct={7}", TvdbIdentityResolver.TypeOf(item), item.Name, item.InternalId, truth, result.TvdbId ?? "(none)", result.Method, result.Confidence, isCorrect);
                done++; progress.Report(done * 100.0 / Math.Max(1, sample.Count));
            }
            logger.Info("Resolver evaluation finished: {0}/{1} correct ({2:F2}%). Query resolution_evaluation_summary and resolution_evaluation for threshold analysis.", correct, done, done == 0 ? 0 : correct * 100.0 / done);
        }

        private static bool InScopeMedia(BaseItem item) => !(item is Episode e) || e.ParentIndexNumber.GetValueOrDefault() >= 1;
    }
}
