using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using PersonCleaner.Housekeeping;
using PersonCleaner.Storage;
using PersonCleaner.Tmdb;
using PersonCleaner.Tvdb;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics;

namespace PersonCleaner.Tasks
{
    public sealed class PersonHousekeepingEvaluationTask : IScheduledTask
    {
        private readonly IApplicationPaths paths;
        private readonly ILogger logger;
        private readonly IHttpClient http;
        private readonly IJsonSerializer json;
        private readonly ILibraryManager library;
        public string Name => "PersonCleaner - Evaluate baseline person truth";
        public string Key => "PersonCleanerHousekeepingEvaluation";
        public string Description => "Acquires targeted provider evidence for unresolved people, then evaluates housekeeping proposals without changing Emby or the baseline truth.";
        public string Category => "GinjaNinja Tools";

        public PersonHousekeepingEvaluationTask(IApplicationPaths paths, ILogManager logs, IHttpClient http, IJsonSerializer json, ILibraryManager library)
        {
            this.paths=paths;
            this.http=http; this.json=json; this.library=library;
            logger=logs.GetLogger("PersonCleaner Housekeeping");
        }
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            cancellationToken.ThrowIfCancellationRequested(); progress.Report(1);
            await Task.WhenAll(AcquireTmdbEvidenceGaps(cancellationToken),AcquireTvdbEvidenceGaps(cancellationToken)).ConfigureAwait(false);
            using (var repository = new HousekeepingRepository(paths))
            {
                logger.Info("Housekeeping evaluation starting from captured provider evidence.");
                var activePhase = "Starting";
                var activePercent = 1d;
                var phaseStarted = Stopwatch.StartNew();
                var totalStarted = Stopwatch.StartNew();
                using (var heartbeat = new Timer(_ => logger.Info(
                    "Housekeeping heartbeat: phase={0}; phase elapsed={1:n1}s; total elapsed={2:n1}s; progress={3:n0}%; cancellation requested={4}",
                    activePhase, phaseStarted.Elapsed.TotalSeconds, totalStarted.Elapsed.TotalSeconds, activePercent, cancellationToken.IsCancellationRequested), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)))
                {
                    var run = repository.Run(DateTime.UtcNow, cancellationToken, (phase, percent, elapsed) =>
                    {
                        activePhase = phase;
                        activePercent = percent;
                        phaseStarted.Restart();
                        progress.Report(percent);
                        logger.Info("Housekeeping phase: {0}; total elapsed {1:n1}s; progress {2:n0}%", phase, elapsed.TotalSeconds, percent);
                    });
                    activePhase = "Loading review rows";
                    activePercent = 99;
                    phaseStarted.Restart();
                    logger.Info("Housekeeping phase: Loading review rows; algorithm run {0} is complete; progress 99%", run);
                    HousekeepingResultsCache.Replace(repository.LatestResults().ToArray());
                    progress.Report(100); logger.Info("Person housekeeping evaluation run {0} completed with {1} UI result rows. A frozen derived truth was created; Emby and baseline truth were not changed.", run, HousekeepingResultsCache.Rows.Length);
                }
            }
        }

        private async Task AcquireTmdbEvidenceGaps(CancellationToken ct)
        {
            if(string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.TmdbApiKey)){logger.Warn("TMDB evidence-gap acquisition skipped because no TMDB key is configured.");return;}
            using(var archive=new TmdbArchiveRepository(paths,logger))
            {
                archive.Initialize(); var api=new TmdbApiClient(http,json,logger,archive); var targets=archive.GetPersonEvidenceGapTargets();
                var mediaItemCache=new Dictionary<long,BaseItem>();
                logger.Info("TMDB evidence-gap acquisition: evaluating {0} Emby people whose archived identity/media evidence is missing, contradictory, duplicated or unsupported.",targets.Count);
                foreach(var target in targets)
                {
                    ct.ThrowIfCancellationRequested(); api.SetEvidenceContext(target.Name,target.EmbyId,target.CurrentId); var mediaContext=await BuildTmdbMediaContext(api,archive,archive.GetLinkedMediaIds(target.EmbyId),mediaItemCache,ct).ConfigureAwait(false);
                    var qualified=new List<Tuple<TmdbEntity,int,int,int,int>>();var seenCandidates=new HashSet<string>(StringComparer.Ordinal);
                    foreach(var id in new[]{target.CurrentId}.Concat(mediaContext.ExactCastCountByPerson.OrderByDescending(x=>x.Value).Select(x=>x.Key)).Where(x=>!string.IsNullOrWhiteSpace(x)).Take(8))await EvaluateTmdbCandidate(api,archive,target,mediaContext,id,qualified,seenCandidates,ct).ConfigureAwait(false);
                    if(!qualified.Any(x=>x.Item2+x.Item5>0))
                    {
                        var search=await api.SearchPerson(target.Name,ct).ConfigureAwait(false);var leads=new List<TmdbEntity>();leads.AddRange((search?.results??new List<TmdbEntity>()).Take(5));
                        if(!string.IsNullOrWhiteSpace(target.ImdbId))leads.AddRange((await api.FindImdb(target.ImdbId,ct).ConfigureAwait(false))?.person_results??new List<TmdbEntity>());
                        foreach(var id in leads.Select(x=>x.id.ToString()).Where(x=>!string.IsNullOrWhiteSpace(x)).Take(8))await EvaluateTmdbCandidate(api,archive,target,mediaContext,id,qualified,seenCandidates,ct).ConfigureAwait(false);
                    }
                    archive.SaveRecoveryCandidates(target.EmbyId,qualified.Select(x=>x.Item1),x=>json.SerializeToString(x));
                    qualified=qualified.Where(x=>x.Item2+x.Item5>=Math.Max(1,(int)Math.Ceiling(target.LinkedCount*.8))).OrderByDescending(x=>x.Item2).ThenByDescending(x=>x.Item5).ThenByDescending(x=>x.Item4).ThenByDescending(x=>x.Item3).ToList();
                    if(qualified.Count>1&&qualified[0].Item2==qualified[1].Item2&&qualified[0].Item5==qualified[1].Item5&&qualified[0].Item4==qualified[1].Item4&&qualified[0].Item3==qualified[1].Item3){logger.Warn("[{0} - {1} - TMDB {2}] Evidence-gap acquisition found ambiguous equally-supported candidates {3} and {4}; no replacement proposed.",target.Name,target.EmbyId,target.CurrentId??"-",qualified[0].Item1.id,qualified[1].Item1.id);archive.MarkFetch("person-evidence-audit:"+target.EmbyId,true,"ambiguous candidates retained");continue;}
                    if(qualified.Count>0)
                    {
                        var detail=qualified[0].Item1;var chosenOverlap=qualified[0].Item2;var seriesSupport=qualified[0].Item4;var broaderSupport=qualified[0].Item5;var id=detail.id.ToString();
                        var imdbMatch=!string.IsNullOrWhiteSpace(target.ImdbId)&&string.Equals(target.ImdbId,detail.external_ids?.imdb_id,StringComparison.OrdinalIgnoreCase);
                        var canonical=string.Equals(target.Name,detail.name,StringComparison.OrdinalIgnoreCase);var alias=(detail.also_known_as??new List<string>()).Any(x=>string.Equals(target.Name,x,StringComparison.OrdinalIgnoreCase));
                        var weightedCoverage=(chosenOverlap+.9*broaderSupport)/Math.Max(1d,target.LinkedCount);var confidence=Math.Min(.99,.68+.20*weightedCoverage+(canonical||alias ? .04 : 0)+(imdbMatch ? .04 : 0));
                        if(!string.Equals(target.CurrentId,id,StringComparison.Ordinal))archive.SaveResolution(target.EmbyId,"person",target.CurrentId,id,"inferred","targeted-scope-aware-media-cast",1,json.SerializeToString(new{current_name=target.Name,candidate_name=detail.name,current_imdb=target.ImdbId,candidate_imdb=detail.external_ids?.imdb_id,wikidata=detail.external_ids?.wikidata_id,birth_date=detail.birthday,aliases=detail.also_known_as,linked_media=target.LinkedCount,checked_exact_scope_media=mediaContext.CheckedMedia,unresolved_exact_scope_media=target.LinkedCount-mediaContext.CheckedMedia,exact_scope_cast_matches=chosenOverlap,broader_series_scope_episode_matches=broaderSupport,weighted_media_coverage=weightedCoverage,matching_series_credits=seriesSupport,matched_media=mediaContext.MatchedMediaByPerson.ContainsKey(id)?mediaContext.MatchedMediaByPerson[id]:new List<string>(),name_canonical=canonical,name_alias=alias,imdb_corroborates=imdbMatch,confidence=confidence,evidence="Emby movie, series and episode credits are compared at the same provider scope where available. An Emby episode supported only by a provider series credit is retained separately as broader-scope evidence at 90% of an exact-scope match. Provider episode_count is never used as overlap. Names qualify candidate compatibility; external IDs and biography corroborate identity."}));
                        logger.Info("[{0} - {1} - TMDB {2}] Evidence-gap candidate '{3}' is explicitly present in {4}/{5} exact TMDB media cast lists ({6} linked media unresolved).",target.Name,target.EmbyId,id,detail.name,chosenOverlap,mediaContext.CheckedMedia,target.LinkedCount-mediaContext.CheckedMedia);
                    }
                    archive.MarkFetch("person-evidence-audit:"+target.EmbyId,true,qualified.Count>0?null:"no qualifying candidate; evidence retained");
                }
                logger.Info("TMDB evidence-gap acquisition cache summary: {0} hits, {1} misses; successful responses cached for {2} day(s).",api.CacheHits,api.CacheMisses,Math.Max(1,Plugin.Instance.Configuration.SuccessCacheDays));
            }
        }
        private async Task EvaluateTmdbCandidate(TmdbApiClient api,TmdbArchiveRepository archive,TmdbRecoveryTarget target,TmdbMediaContext mediaContext,string id,List<Tuple<TmdbEntity,int,int,int,int>> candidates,HashSet<string> seen,CancellationToken ct)
        {
            if(!seen.Add(id)||archive.IsNotFoundCached("person:"+id))return;TmdbEntity detail;try{detail=await api.GetPerson(id,ct).ConfigureAwait(false);}catch(HttpException ex) when(ex.StatusCode==System.Net.HttpStatusCode.NotFound){archive.MarkNotFound("person:"+id,"TMDB person returned 404");logger.Warn("[{0} - {1} - TMDB {2}] Candidate person returned 404; retained as unavailable evidence and skipped without aborting the cohort.",target.Name,target.EmbyId,id);return;}archive.SaveEntity(id,"person",detail,json.SerializeToString(detail));var overlap=MediaOverlap(mediaContext,detail);var seriesSupport=SeriesCreditOverlap(mediaContext,detail);var broaderSupport=BroaderEpisodeSupport(mediaContext,detail);var exact=string.Equals(target.Name,detail.name,StringComparison.OrdinalIgnoreCase)||(detail.also_known_as??new List<string>()).Any(x=>string.Equals(target.Name,x,StringComparison.OrdinalIgnoreCase));var compatible=exact||NameCompatible(target.Name,detail.name)||(detail.also_known_as??new List<string>()).Any(x=>NameCompatible(target.Name,x));if(overlap>0||broaderSupport>0||compatible)candidates.Add(Tuple.Create(detail,overlap,exact?2:compatible?1:0,seriesSupport,broaderSupport));
        }
        private async Task AcquireTvdbEvidenceGaps(CancellationToken ct)
        {
            if(string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.TvdbApiKey)){logger.Warn("TVDB evidence-gap acquisition skipped because no TVDB key is configured.");return;}
            using(var archive=new TvdbArchiveRepository(paths,logger))
            {
                archive.Initialize();var api=new TvdbApiClient(http,json,logger,archive);var resolver=new TvdbIdentityResolver(api,library,archive);var targets=archive.GetPersonEvidenceGapIds();
                logger.Info("TVDB evidence-gap acquisition: evaluating {0} Emby people whose archived identity/media evidence is missing, contradictory, duplicated or unsupported.",targets.Count);
                foreach(var embyId in targets)
                {
                    ct.ThrowIfCancellationRequested();var person=library.GetItemById(embyId) as Person;if(person==null)continue;api.SetEvidenceContext(person.Name,embyId,person.GetProviderId(MetadataProviders.Tvdb));var supportedTmdbImdb=archive.GetMediaSupportedTmdbImdbId(embyId,person.GetProviderId(MetadataProviders.Tmdb));var result=await resolver.Resolve(person,ct,null,supportedTmdbImdb).ConfigureAwait(false);archive.SaveResolutionCandidates(embyId,result.Candidates,x=>json.SerializeToString(x));var mediaContext=BuildTvdbMediaContext(person,ct);
                    var linked=archive.GetLinkedMediaCount(embyId);var qualified=(result.Candidates??new List<ResolutionCandidate>()).Select(c=>{var support=TvdbScopeSupport(mediaContext,c.FilmographyIds);return new{Candidate=c,Exact=support.Item1,Broader=support.Item2,Weighted=support.Item1+.9*support.Item2};}).Where(x=>x.Weighted>=Math.Max(1,linked*.8)).OrderByDescending(x=>x.Exact).ThenByDescending(x=>x.Broader).ThenByDescending(x=>x.Candidate.Score).ToList();
                    if(qualified.Count==0||qualified.Count>1&&qualified[0].Exact==qualified[1].Exact&&qualified[0].Broader==qualified[1].Broader&&Math.Abs(qualified[0].Candidate.Score-qualified[1].Candidate.Score)<.05){archive.MarkFetch("person-evidence-audit:"+embyId,true,qualified.Count==0?"no qualifying candidate; evidence retained":"ambiguous candidates retained");continue;}
                    var winner=qualified[0];if(archive.IsNotFoundCached("person:"+winner.Candidate.TvdbId)){archive.MarkFetch("person-evidence-audit:"+embyId,true,"candidate remains negatively cached after 404; evidence retained");continue;}EntityData detail;try{detail=await api.GetEntity("people",winner.Candidate.TvdbId,ct).ConfigureAwait(false);}catch(HttpException ex) when(ex.StatusCode==System.Net.HttpStatusCode.NotFound){archive.MarkNotFound("person:"+winner.Candidate.TvdbId,"TVDB person returned 404");logger.Warn("[{0} - {1} - TVDB {2}] Candidate person returned 404; retained as unavailable evidence and skipped without aborting the cohort.",person.Name,embyId,winner.Candidate.TvdbId);archive.MarkFetch("person-evidence-audit:"+embyId,true,"candidate returned 404; evidence retained");continue;}archive.SaveEntity(winner.Candidate.TvdbId,"person",detail,json.SerializeToString(detail));
                    var confidence=Math.Min(.99,.70+.20*winner.Weighted/Math.Max(1d,linked)+Math.Min(.09,Math.Max(0,winner.Candidate.Score-.70)));
                    if(!string.Equals(person.GetProviderId(MetadataProviders.Tvdb),winner.Candidate.TvdbId,StringComparison.Ordinal))archive.SaveItemResolution(embyId,"person",person.GetProviderId(MetadataProviders.Tvdb),winner.Candidate.TvdbId,"inferred","targeted-scope-aware-media-cast",confidence,result.CandidateCount,json.SerializeToString(new{current_name=person.Name,candidate_name=detail.name,linked_media=linked,exact_scope_cast_matches=winner.Exact,broader_series_scope_episode_matches=winner.Broader,weighted_media_coverage=winner.Weighted/Math.Max(1,linked),confidence=confidence,external_ids=detail.remoteIds,birth_date=detail.birth,aliases=detail.aliases,evidence="TVDB evidence preserves movie, series and episode scope. Episode relationships supported only by a series credit are reported separately at 90% weight; episode counts are not used as overlap. External IDs and biography corroborate identity."}));
                    logger.Info("[{0} - {1} - TVDB {2}] Evidence-gap candidate '{3}' has {4} exact-scope and {5} broader-series matches across {6} linked media.",person.Name,embyId,winner.Candidate.TvdbId,detail.name,winner.Exact,winner.Broader,linked);
                    archive.MarkFetch("person-evidence-audit:"+embyId,true,null);
                }
                logger.Info("TVDB evidence-gap acquisition cache summary: {0} hits, {1} misses; successful responses cached for {2} day(s).",api.CacheHits,api.CacheMisses,Math.Max(1,Plugin.Instance.Configuration.SuccessCacheDays));
            }
        }
        private sealed class TmdbMediaContext
        {
            public int CheckedMedia { get; set; }
            public HashSet<string> LinkedSeriesIds { get; }=new HashSet<string>(StringComparer.Ordinal);
            public Dictionary<string,int> LinkedEpisodeCountBySeries { get; }=new Dictionary<string,int>(StringComparer.Ordinal);
            public Dictionary<string,int> ExactEpisodeCountByPersonSeries { get; }=new Dictionary<string,int>(StringComparer.Ordinal);
            public Dictionary<string,int> ExactCastCountByPerson { get; }=new Dictionary<string,int>(StringComparer.Ordinal);
            public Dictionary<string,List<string>> MatchedMediaByPerson { get; }=new Dictionary<string,List<string>>(StringComparer.Ordinal);
        }
        private async Task<TmdbMediaContext> BuildTmdbMediaContext(TmdbApiClient api,TmdbArchiveRepository archive,List<long> mediaIds,Dictionary<long,BaseItem> mediaItemCache,CancellationToken ct)
        {
            var context=new TmdbMediaContext();
            foreach(var mediaId in mediaIds)
            {
                ct.ThrowIfCancellationRequested();
                if(!mediaItemCache.TryGetValue(mediaId,out var media)){media=library.GetItemById(mediaId);mediaItemCache[mediaId]=media;}
                TmdbEntity entity=null;string label=null;string type=null;string episodeSeriesId=null;
                if(media is Episode episode&&episode.Series!=null&&episode.ParentIndexNumber.HasValue&&episode.IndexNumber.HasValue)
                {
                    var seriesId=episode.Series.GetProviderId(MetadataProviders.Tmdb);
                    if(!string.IsNullOrWhiteSpace(seriesId)){episodeSeriesId=seriesId;context.LinkedSeriesIds.Add(seriesId);context.LinkedEpisodeCountBySeries.TryGetValue(seriesId,out var episodeCount);context.LinkedEpisodeCountBySeries[seriesId]=episodeCount+1;try{entity=await api.GetEpisode(seriesId,episode.ParentIndexNumber.Value,episode.IndexNumber.Value,ct).ConfigureAwait(false);}catch(Exception ex) when(!(ex is OperationCanceledException)){logger.Warn("{0} TMDB exact episode cast unavailable for Emby media {1}: {2}",api.EvidencePrefix,mediaId,ex.Message);}type="episode";label="Emby "+mediaId+" "+episode.Series.Name+" S"+episode.ParentIndexNumber.Value+"E"+episode.IndexNumber.Value;}
                }
                else if(media is Series series)
                {
                    var seriesId=series.GetProviderId(MetadataProviders.Tmdb);if(!string.IsNullOrWhiteSpace(seriesId)){context.LinkedSeriesIds.Add(seriesId);entity=await api.GetSeries(seriesId,ct).ConfigureAwait(false);type="series";label="Emby "+mediaId+" series "+series.Name;}
                }
                else if(media is Movie movie)
                {
                    var movieId=movie.GetProviderId(MetadataProviders.Tmdb);if(!string.IsNullOrWhiteSpace(movieId)){entity=await api.GetMovie(movieId,ct).ConfigureAwait(false);type="movie";label="Emby "+mediaId+" "+movie.Name;}
                }
                if(entity==null)continue;context.CheckedMedia++;archive.SaveEntity(entity.id.ToString(),type,entity,json.SerializeToString(entity));
                var castRows=TmdbCreditMerger.Cast(entity);foreach(var cast in castRows){var personId=cast.id.ToString();context.ExactCastCountByPerson.TryGetValue(personId,out var count);context.ExactCastCountByPerson[personId]=count+1;if(episodeSeriesId!=null){var key=personId+"|"+episodeSeriesId;context.ExactEpisodeCountByPersonSeries.TryGetValue(key,out var exactEpisodes);context.ExactEpisodeCountByPersonSeries[key]=exactEpisodes+1;}if(!context.MatchedMediaByPerson.TryGetValue(personId,out var matches)){matches=new List<string>();context.MatchedMediaByPerson[personId]=matches;}matches.Add(label+" as "+(cast.character??"(role unavailable)"));}
            }
            return context;
        }
        private static int MediaOverlap(TmdbMediaContext context,TmdbEntity person)
        {
            if(person==null)return 0;return context.ExactCastCountByPerson.TryGetValue(person.id.ToString(),out var overlap)?overlap:0;
        }
        private static int SeriesCreditOverlap(TmdbMediaContext context,TmdbEntity person)
        {
            return (person?.combined_credits?.cast??new List<TmdbCredit>()).Where(x=>x.media_type=="tv"&&context.LinkedSeriesIds.Contains(x.id.ToString())).Select(x=>x.id).Distinct().Count();
        }
        private static int BroaderEpisodeSupport(TmdbMediaContext context,TmdbEntity person)
        {
            if(person==null)return 0;var personId=person.id.ToString();var creditedSeries=new HashSet<string>((person.combined_credits?.cast??new List<TmdbCredit>()).Where(x=>x.media_type=="tv").Select(x=>x.id.ToString()),StringComparer.Ordinal);var support=0;foreach(var pair in context.LinkedEpisodeCountBySeries)if(creditedSeries.Contains(pair.Key)){context.ExactEpisodeCountByPersonSeries.TryGetValue(personId+"|"+pair.Key,out var exact);support+=Math.Max(0,pair.Value-exact);}return support;
        }
        private sealed class TvdbMediaContext
        {
            public HashSet<string> ExactProductions { get; }=new HashSet<string>(StringComparer.Ordinal);
            public List<Tuple<string,string>> Episodes { get; }=new List<Tuple<string,string>>();
        }
        private TvdbMediaContext BuildTvdbMediaContext(Person person,CancellationToken ct)
        {
            var context=new TvdbMediaContext();var roles=new[]{PersonType.Actor,PersonType.GuestStar,PersonType.Director,PersonType.Writer,PersonType.Producer};var items=library.GetItemList(new InternalItemsQuery{IncludeItemTypes=new[]{"Movie","Series","Episode"},PersonIds=new[]{person.InternalId},PersonTypes=roles,Recursive=true},ct);
            foreach(var media in items){if(media is Episode episode&&episode.ParentIndexNumber.GetValueOrDefault()<1)continue;var id=media.GetProviderId(MetadataProviders.Tvdb);if(media is Episode e){var seriesId=e.Series?.GetProviderId(MetadataProviders.Tvdb);if(!string.IsNullOrWhiteSpace(id))context.ExactProductions.Add("episode:"+id);if(!string.IsNullOrWhiteSpace(seriesId))context.Episodes.Add(Tuple.Create(string.IsNullOrWhiteSpace(id)?null:"episode:"+id,"series:"+seriesId));}else if(!string.IsNullOrWhiteSpace(id))context.ExactProductions.Add((media is Movie?"movie:":"series:")+id);}return context;
        }
        private static Tuple<int,int> TvdbScopeSupport(TvdbMediaContext context,List<string> filmography)
        {
            var remote=new HashSet<string>(filmography??new List<string>(),StringComparer.Ordinal);var exact=context.ExactProductions.Count(remote.Contains);var broader=context.Episodes.Count(x=>(x.Item1==null||!remote.Contains(x.Item1))&&remote.Contains(x.Item2));return Tuple.Create(exact,broader);
        }
        private static bool NameCompatible(string a,string b)
        {
            if(string.IsNullOrWhiteSpace(a)||string.IsNullOrWhiteSpace(b))return false; if(string.Equals(a,b,StringComparison.OrdinalIgnoreCase))return true;
            var x=a.Trim().Split(' ');var y=b.Trim().Split(' ');return x.Length>1&&y.Length>1&&char.ToUpperInvariant(x[0][0])==char.ToUpperInvariant(y[0][0])&&string.Equals(x[x.Length-1],y[y.Length-1],StringComparison.OrdinalIgnoreCase);
        }
    }
}
