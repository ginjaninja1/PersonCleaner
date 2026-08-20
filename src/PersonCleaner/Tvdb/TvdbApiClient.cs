using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Serialization;
using PersonCleaner.Configuration;
using PersonCleaner.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.Tvdb
{
    internal sealed class TvdbApiClient
    {
        private const string BaseUrl = "https://api4.thetvdb.com/v4";
        private readonly IHttpClient httpClient;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private readonly TvdbArchiveRepository repository;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim concurrencyGate;
        private string token;
        private DateTimeOffset tokenExpiresUtc;
        private DateTimeOffset nextRequestUtc;
        private long cacheHits;
        private long cacheMisses;
        public long CacheHits => Interlocked.Read(ref cacheHits);
        public long CacheMisses => Interlocked.Read(ref cacheMisses);
        private string evidenceName;
        private long? evidenceEmbyId;
        private string evidenceProviderId;

        public TvdbApiClient(IHttpClient httpClient, IJsonSerializer json, ILogger logger, TvdbArchiveRepository repository)
        {
            this.httpClient = httpClient;
            this.json = json;
            this.logger = logger;
            this.repository = repository;
            concurrencyGate = new SemaphoreSlim(Math.Max(1, Plugin.Instance.Configuration.TvdbMaximumConcurrentRequests));
        }

        public void SetEvidenceContext(string name, long embyId, string providerId)
        {
            evidenceName = name; evidenceEmbyId = embyId; evidenceProviderId = providerId;
        }

        public string EvidencePrefix => LogPrefix(string.Empty);

        private string LogPrefix(string path)
        {
            if (!evidenceEmbyId.HasValue) return "[housekeeping - - - TVDB -]";
            var id = evidenceProviderId;
            if (path.StartsWith("/people/", StringComparison.OrdinalIgnoreCase))
            {
                var value = path.Substring(8); var end = value.IndexOfAny(new[] { '/', '?' });
                id = end < 0 ? value : value.Substring(0, end);
            }
            return "[" + (evidenceName ?? "-") + " - " + evidenceEmbyId.Value + " - TVDB " + (string.IsNullOrWhiteSpace(id) ? "-" : id) + "]";
        }

        public async Task<EntityData> GetEntity(string kind, string id, CancellationToken ct)
        {
            var response = await Get<TvdbResponse<EntityData>>("/" + kind + "/" + id + "/extended", ct).ConfigureAwait(false);
            return response.data;
        }

        public Task<TvdbResponse<List<SearchByRemoteIdData>>> SearchRemoteId(string remoteId, CancellationToken ct) =>
            Get<TvdbResponse<List<SearchByRemoteIdData>>>("/search/remoteid/" + Uri.EscapeDataString(remoteId), ct);

        public Task<TvdbResponse<List<SearchData>>> Search(string type, string query, int? year, CancellationToken ct)
        {
            var path = "/search?type=" + Uri.EscapeDataString(type) + "&query=" + Uri.EscapeDataString(query);
            if (year.HasValue) path += "&year=" + year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Get<TvdbResponse<List<SearchData>>>(path, ct);
        }

        public async Task<TvdbResponse<EpisodesData>> GetSeriesEpisodes(string seriesId, int page, CancellationToken ct)
        {
            var path = "/series/" + seriesId + "/episodes/official?page=" + page;
            var failureKey = "api-failure:" + path;
            if (!repository.IsDue(failureKey)) return null;
            try { return await Get<TvdbResponse<EpisodesData>>(path, ct).ConfigureAwait(false); }
            catch (HttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                repository.MarkFetch(failureKey, false, "TVDB returned 404");
                logger.Warn("{0} TVDB has no official series episode feed for series {1}; negatively cached for the configured failure retry period", LogPrefix(path), seriesId);
                return null;
            }
        }

        private async Task<T> Get<T>(string path, CancellationToken ct)
        {
            if (repository.TryGetApiResponse(path, out var cachedJson))
            {
                Interlocked.Increment(ref cacheHits);
                logger.Debug("{0} TVDB Archive API cache hit: {1}", LogPrefix(path), path);
                return json.DeserializeFromString<T>(cachedJson);
            }
            Interlocked.Increment(ref cacheMisses);
            logger.Debug("{0} TVDB Archive API cache miss: {1}; successful response will be cached", LogPrefix(path), path);
            await EnsureToken(ct).ConfigureAwait(false);
            Exception last = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await concurrencyGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await Throttle(ct).ConfigureAwait(false);
                    var options = new HttpRequestOptions { Url = BaseUrl + path, CancellationToken = ct, BufferContent = false };
                    options.RequestHeaders["Authorization"] = "Bearer " + token;
                    using (var response = await httpClient.SendAsync(options, "GET").ConfigureAwait(false))
                    using (var stream = response.Content)
                    using (var reader = new StreamReader(stream))
                    {
                        var responseJson = await reader.ReadToEndAsync().ConfigureAwait(false);
                        var parsed = json.DeserializeFromString<T>(responseJson);
                        repository.SaveApiResponse(path, responseJson);
                        return parsed;
                    }
                }
                catch (HttpException ex) when (IsTransient(ex))
                {
                    last = ex;
                    var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt + 1)));
                    if (ex.StatusCode == (HttpStatusCode)429) await ExtendProviderCooldown(delay, ct).ConfigureAwait(false);
                    logger.Warn("{0} TVDB transient response on {1}; retry {2}/5 in {3}s", LogPrefix(path), path, attempt + 1, delay.TotalSeconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (IOException ex) { last = ex; await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct).ConfigureAwait(false); }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { last = ex; await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct).ConfigureAwait(false); }
                finally { concurrencyGate.Release(); }
            }
            throw last ?? new InvalidOperationException("TVDB request failed");
        }

        private static bool IsTransient(HttpException ex) => !ex.StatusCode.HasValue || ex.StatusCode == (HttpStatusCode)429 || (int)ex.StatusCode.Value >= 500;

        private async Task EnsureToken(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(token) && tokenExpiresUtc > DateTimeOffset.UtcNow.AddDays(1)) return;
            var config = Plugin.Instance.Configuration;
            if (string.IsNullOrWhiteSpace(config.TvdbApiKey)) throw new InvalidOperationException("TVDB API key is not configured.");
            var body = new LoginRequest { apikey = config.TvdbApiKey, pin = string.IsNullOrWhiteSpace(config.TvdbSubscriberPin) ? null : config.TvdbSubscriberPin };
            var options = new HttpRequestOptions
            {
                Url = BaseUrl + "/login", CancellationToken = ct, RequestContentType = "application/json",
                RequestContent = json.SerializeToString(body).AsMemory()
            };
            using (var response = await httpClient.SendAsync(options, "POST").ConfigureAwait(false))
            using (var stream = response.Content)
            {
                var login = await json.DeserializeFromStreamAsync<TvdbResponse<LoginData>>(stream).ConfigureAwait(false);
                token = login.data.token;
                tokenExpiresUtc = DateTimeOffset.UtcNow.AddDays(28);
            }
        }

        private async Task Throttle(CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var delay = nextRequestUtc - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
                nextRequestUtc = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(0, Plugin.Instance.Configuration.MinimumRequestIntervalMilliseconds));
            }
            finally { gate.Release(); }
        }

        private async Task ExtendProviderCooldown(TimeSpan delay, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var cooldownUntil = DateTimeOffset.UtcNow.Add(delay);
                if (cooldownUntil > nextRequestUtc) nextRequestUtc = cooldownUntil;
            }
            finally { gate.Release(); }
        }
    }
}
