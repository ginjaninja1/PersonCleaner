using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using PersonCleaner.V2.Domain;
using PersonCleaner.V2.Storage;
using System;
using System.Net;
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
        Absent,
        AuthenticationFailed,
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

        public async Task<HydrationOutcome> Process(QueueItem item, long runId, int ttlDays, int failureRetryMinutes, bool providerConfigured, CancellationToken cancellationToken)
        {
            var cache = repository.GetCache(item.Provider, item.EntityType, item.MediaType, item.ProviderId);
            var absence = repository.GetAbsence(item.Provider, item.EntityType, item.MediaType, item.ProviderId);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, ttlDays)).ToUnixTimeSeconds();
            var positiveIsFresh = cache != null && cache.LastFetchedUnix >= cutoff && payloads.Exists(cache.RelativePath);
            var absenceIsFresh = absence != null && absence.ConfirmedUnix >= cutoff;
            if (absenceIsFresh && (!positiveIsFresh || absence.ConfirmedUnix >= cache.LastFetchedUnix))
            {
                repository.RecordAcquisition(runId, item, AcquisitionStates.Absent, "absence-cache", "HTTP " + absence.StatusCode);
                repository.MarkQueue(item, "absent", "Provider-confirmed HTTP " + absence.StatusCode); repository.IncrementRun(runId, "cache_hits");
                logger.Debug("PersonCleaner run {0}: {1} authoritative absence cache hit for {2}; HTTP {3}.", runId, item.Provider.ToUpperInvariant(), Label(item), absence.StatusCode);
                return HydrationOutcome.Absent;
            }
            if (positiveIsFresh)
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
                        repository.RecordAcquisition(runId, item, AcquisitionStates.Unavailable, "materializer", safeError);
                        logger.Error("PersonCleaner run {0}: cached materialization upgrade failed for {1} {2}; the cache row remains at v{3} and will be retried. {4}: {5}", runId, item.Provider.ToUpperInvariant(), Label(item), cache.MaterializerVersion, ex.GetType().Name, safeError);
                        return HydrationOutcome.Failed;
                    }
                }
                var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(cache.LastFetchedUnix);
                logger.Debug("PersonCleaner run {0}: {1} cache hit for {2}; age={3:0.0}h; payload={4}", runId, item.Provider.ToUpperInvariant(), Label(item), age.TotalHours, cache.RelativePath);
                repository.RecordAcquisition(runId, item, AcquisitionStates.Present, "payload-cache");
                repository.MarkQueue(item, "completed"); repository.IncrementRun(runId, "cache_hits"); return HydrationOutcome.CacheHit;
            }
            if (!providerConfigured)
            {
                const string detail = "API key is not configured and no fresh positive or authoritative-absence cache is available.";
                repository.MarkQueue(item, "skipped", detail); repository.RecordAcquisition(runId, item, AcquisitionStates.Unavailable, "configuration", detail);
                return HydrationOutcome.Skipped;
            }
            if (!repository.IsFailureRetryDue(item, failureRetryMinutes))
            {
                logger.Debug("PersonCleaner run {0}: {1} {2} deferred because its recent failure is still inside the {3}-minute retry window.", runId, item.Provider.ToUpperInvariant(), Label(item), failureRetryMinutes);
                repository.MarkQueue(item, "deferred", "A recent failure is still inside the configured retry window.");
                repository.RecordAcquisition(runId, item, AcquisitionStates.Unavailable, "failure-cache", "A recent failure is still inside the configured retry window.");
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
                repository.ClearAbsence(item); repository.ClearFailure(item); repository.RecordAcquisition(runId, item, AcquisitionStates.Present, "provider"); repository.MarkQueue(item, "completed"); repository.IncrementRun(runId, item.EntityType == "media" ? "media_fetched" : "people_fetched");
                return changed ? HydrationOutcome.FetchedChanged : HydrationOutcome.FetchedUnchanged;
            }
            catch (HttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.StatusCode == HttpStatusCode.Gone)
            {
                var statusCode = (int)ex.StatusCode.Value;
                repository.RecordAbsent(runId, item, statusCode, "provider"); repository.ClearFailure(item); repository.MarkQueue(item, "absent", "Provider-confirmed HTTP " + statusCode);
                logger.Info("PersonCleaner run {0}: {1} confirmed {2} is absent with HTTP {3}; the authoritative absence is cached.", runId, item.Provider.ToUpperInvariant(), Label(item), statusCode);
                return HydrationOutcome.Absent;
            }
            catch (HttpException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
            {
                var safeError = SafeError(ex);
                repository.MarkQueue(item, "failed", safeError); repository.IncrementRun(runId, "failures");
                repository.RecordAcquisition(runId, item, AcquisitionStates.Unavailable, "authentication", safeError);
                return HydrationOutcome.AuthenticationFailed;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                var safeError = SafeError(ex);
                repository.RecordFailure(item, safeError); repository.MarkQueue(item, "failed", safeError); repository.IncrementRun(runId, "failures");
                repository.RecordAcquisition(runId, item, AcquisitionStates.Unavailable, "provider-failure", safeError);
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
