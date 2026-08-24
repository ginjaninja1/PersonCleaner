namespace PersonCleaner.V2.Storage
{
    using PersonCleaner.V2.Domain;

    internal sealed class QueueItem
    {
        public string Provider { get; set; }
        public string EntityType { get; set; }
        public string ProviderId { get; set; }
        public string MediaType { get; set; }
        public int Priority { get; set; }
        public bool GraphEligible { get; set; }
    }

    internal sealed class CacheEntry
    {
        public string Provider { get; set; }
        public string EntityType { get; set; }
        public string ProviderId { get; set; }
        public string MediaType { get; set; }
        public string PayloadHash { get; set; }
        public string RelativePath { get; set; }
        public long LastFetchedUnix { get; set; }
        public int MaterializerVersion { get; set; }
    }

    internal sealed class AbsenceCacheEntry
    {
        public string Provider { get; set; }
        public string EntityType { get; set; }
        public string ProviderId { get; set; }
        public string MediaType { get; set; }
        public long ConfirmedUnix { get; set; }
        public int StatusCode { get; set; }
    }

    internal sealed class PersonSeedSummary
    {
        public int DiscoveredTmdb { get; set; }
        public int DiscoveredTvdb { get; set; }
        public int SelectedTmdb { get; set; }
        public int SelectedTvdb { get; set; }
        public int ValidationTmdb { get; set; }
        public int ValidationTvdb { get; set; }
        public int DiscoveredTotal => DiscoveredTmdb + DiscoveredTvdb;
        public int SelectedTotal => SelectedTmdb + SelectedTvdb;
        public int ValidationTotal => ValidationTmdb + ValidationTvdb;
    }

    public sealed class CorrectionReviewRow
    {
        public ProviderCorrection Correction { get; set; }
        public long? LastRunId { get; set; }
        public int LastMatchedCount { get; set; }
        public int LastChangedCount { get; set; }
        public string LastSummary { get; set; }
        public long? LastAppliedUtc { get; set; }
    }

    public sealed class DashboardDecision
    {
        public string DecisionId { get; set; }
        [System.ComponentModel.DisplayName("Change")]
        public bool ReviewChanges { get; set; }
        public string Status { get; set; }
        public string Action { get; set; }
        public string Person { get; set; }
        public string EmbyAnchor { get; set; }
        public string ProviderIdentities { get; set; }
        public string CurrentProviderIds { get; set; }
        public string Confidence { get; set; }
        public string LocalAnchorConfidence { get; set; }
        public int ImpactedTitles { get; set; }
        public string Decision { get; set; }
        public string Why { get; set; }
        public DashboardDetail[] Details { get; set; } = new DashboardDetail[0];
    }

    public sealed class DashboardDetail
    {
        public string DetailId { get; set; }
        public string Section { get; set; }
        public int Order { get; set; }
        public string Signal { get; set; }
        public string Verdict { get; set; }
        public string Explanation { get; set; }
        public string RawMetric { get; set; }
        public long? EmbyMediaId { get; set; }
        public string MediaType { get; set; }
        public string TmdbId { get; set; }
        public string TvdbId { get; set; }
        public string TvdbSlug { get; set; }
        public string ImdbId { get; set; }
        public string ProviderObjects { get; set; }
    }

    public sealed class RunStatus
    {
        public long RunId { get; set; }
        public string Status { get; set; }
        public string Mode { get; set; }
        public string Phase { get; set; }
        public string Message { get; set; }
        public int SelectedMovies { get; set; }
        public int SelectedSeries { get; set; }
        public int MediaFetched { get; set; }
        public int PeopleFetched { get; set; }
        public int CacheHits { get; set; }
        public int Failures { get; set; }
        public int Decisions { get; set; }
        public string DecisionBreakdown { get; set; }
    }
}
