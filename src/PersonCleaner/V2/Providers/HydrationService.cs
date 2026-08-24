using MediaBrowser.Model.Logging;
using PersonCleaner.V2.Storage;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.V2.Providers
{
    internal enum HydrationOutcome
    {
        CacheHit,
        FetchedChanged,
        FetchedUnchanged,
        Deferred,
        Failed,
        Skipped
    }

    internal sealed class HydrationService
    {
        private readonly ResolutionRepository repository;
        private readonly RawPayloadCache payloads;
        private readonly ProviderApiClient api;
        private readonly PayloadFlattener flattener;
        private readonly ILogger logger;

        public HydrationService(ResolutionRepository repository, ProviderApiClient api, PayloadFlattener flattener, ILogger logger)
        { this.repository = repository; payloads = new RawPayloadCache(repository.PayloadPath); this.api = api; this.flattener = flattener; this.logger = logger; }

        public async Task<HydrationOutcome> Process(QueueItem item, long runId, int ttlDays, int failureRetryMinutes, CancellationToken cancellationToken)
        {
            var cache = repository.GetCache(item.Provider, item.EntityType, item.MediaType, item.ProviderId);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, ttlDays)).ToUnixTimeSeconds();
            if (cache != null && cache.LastFetchedUnix >= cutoff && payloads.Exists(cache.RelativePath))
            {
                if (cache.MaterializerVersion != PayloadFlattener.MaterializerVersion)
                {
                    try
                    {
                        Materialize(item, payloads.Read(cache.RelativePath));
                        var previous = cache.MaterializerVersion;
                        cache.MaterializerVersion = PayloadFlattener.MaterializerVersion;
                        repository.SaveCache(cache);
                        logger.Info("PersonCleaner run {0}: {1} cache-only materialization upgraded {2} from v{3} to v{4} without a provider request.", runId, item.Provider.ToUpperInvariant(), Label(item), previous, cache.MaterializerVersion);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        var safeError = SafeError(ex);
                        repository.RecordFailure(item, safeError); repository.MarkQueue(item, "failed", safeError); repository.IncrementRun(runId, "failures");
                        logger.Error("PersonCleaner run {0}: cached materialization upgrade failed for {1} {2}; the cache row remains at v{3} and will be retried. {4}: {5}", runId, item.Provider.ToUpperInvariant(), Label(item), cache.MaterializerVersion, ex.GetType().Name, safeError);
                        return HydrationOutcome.Failed;
                    }
                }
                var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(cache.LastFetchedUnix);
                logger.Debug("PersonCleaner run {0}: {1} cache hit for {2}; age={3:0.0}h; payload={4}", runId, item.Provider.ToUpperInvariant(), Label(item), age.TotalHours, cache.RelativePath);
                repository.MarkQueue(item, "completed"); repository.IncrementRun(runId, "cache_hits"); return HydrationOutcome.CacheHit;
            }
            if (!repository.IsFailureRetryDue(item, failureRetryMinutes))
            {
                logger.Debug("PersonCleaner run {0}: {1} {2} deferred because its recent failure is still inside the {3}-minute retry window.", runId, item.Provider.ToUpperInvariant(), Label(item), failureRetryMinutes);
                repository.MarkQueue(item, "deferred", "A recent failure is still inside the configured retry window.");
                return HydrationOutcome.Deferred;
            }
            try
            {
                logger.Debug("PersonCleaner run {0}: {1} network fetch starting for {2}; cache={3}.", runId, item.Provider.ToUpperInvariant(), Label(item), cache == null ? "miss" : "expired");
                var raw = await api.Fetch(item, cancellationToken).ConfigureAwait(false);
                var hash = RawPayloadCache.Hash(raw);
                var relative = cache?.RelativePath ?? payloads.RelativePath(item);
                var changed = cache == null || !string.Equals(cache.PayloadHash, hash, StringComparison.Ordinal) || !payloads.Exists(relative);
                var materializerChanged = cache == null || cache.MaterializerVersion != PayloadFlattener.MaterializerVersion;
                if (changed || materializerChanged)
                {
                    // Parse before replacing the last known-good raw payload.
                    Materialize(item, raw, changed ? (Action)(() => payloads.Write(relative, raw)) : null);
                    logger.Debug("PersonCleaner run {0}: {1} fetched and materialized {2}; payloadChanged={3}; materializerChanged={4}; bytes={5}; hash={6}.", runId, item.Provider.ToUpperInvariant(), Label(item), changed, materializerChanged, System.Text.Encoding.UTF8.GetByteCount(raw), ShortHash(hash));
                }
                else logger.Debug("PersonCleaner run {0}: {1} fetched {2}; payload hash is unchanged, so TTL was refreshed without JSON parsing or re-flattening; bytes={3}; hash={4}.", runId, item.Provider.ToUpperInvariant(), Label(item), System.Text.Encoding.UTF8.GetByteCount(raw), ShortHash(hash));
                repository.SaveCache(new CacheEntry { Provider = item.Provider, EntityType = item.EntityType, MediaType = item.MediaType, ProviderId = item.ProviderId, PayloadHash = hash, RelativePath = relative, LastFetchedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), MaterializerVersion = PayloadFlattener.MaterializerVersion });
                repository.ClearFailure(item); repository.MarkQueue(item, "completed"); repository.IncrementRun(runId, item.EntityType == "media" ? "media_fetched" : "people_fetched");
                return changed ? HydrationOutcome.FetchedChanged : HydrationOutcome.FetchedUnchanged;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                var safeError = SafeError(ex);
                repository.RecordFailure(item, safeError); repository.MarkQueue(item, "failed", safeError); repository.IncrementRun(runId, "failures");
                logger.Error("PersonCleaner run {0}: {1} failed for {2}; {3}: {4}. The remaining queue will continue.", runId, item.Provider.ToUpperInvariant(), Label(item), ex.GetType().Name, safeError);
                logger.Debug("PersonCleaner run {0}: failure stack for {1} {2}: {3}", runId, item.Provider.ToUpperInvariant(), Label(item), ex.StackTrace ?? "stack unavailable");
                return HydrationOutcome.Failed;
            }
        }

        private void Materialize(QueueItem item, string raw, Action beforeReplace = null)
        {
            if (item.EntityType == "media")
            {
                var flattened = flattener.Media(item, raw);
                beforeReplace?.Invoke();
                repository.ReplaceMedia(flattened);
            }
            else
            {
                var flattened = flattener.Person(item, raw);
                beforeReplace?.Invoke();
                repository.ReplacePerson(flattened);
            }
        }

        private static string Label(QueueItem item) => item.EntityType == "media" ? item.MediaType + ":" + item.ProviderId : "person:" + item.ProviderId;
        private static string ShortHash(string hash) => string.IsNullOrWhiteSpace(hash) ? "-" : hash.Substring(0, Math.Min(12, hash.Length));
        private static string SafeError(Exception exception)
        {
            var message = exception?.Message ?? "Unknown provider error";
            var configuration = Plugin.Instance.Configuration;
            foreach (var secret in new[] { configuration.TmdbApiKey, configuration.TvdbApiKey, configuration.TvdbSubscriberPin })
                if (!string.IsNullOrWhiteSpace(secret)) message = message.Replace(secret, "[redacted]");
            return message;
        }
    }
}
