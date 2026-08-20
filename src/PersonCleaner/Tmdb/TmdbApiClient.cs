using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Serialization;
using PersonCleaner.Storage;
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.Tmdb
{
    internal sealed class TmdbApiClient
    {
        private const string BaseUrl = "https://api.themoviedb.org/3";
        private readonly IHttpClient http;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private readonly TmdbArchiveRepository repository;
        private readonly SemaphoreSlim throttleGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim concurrencyGate;
        private DateTimeOffset nextRequestUtc;
        private long cacheHits;
        private long cacheMisses;
        public long CacheHits => Interlocked.Read(ref cacheHits);
        public long CacheMisses => Interlocked.Read(ref cacheMisses);
        private string evidenceName;
        private long? evidenceEmbyId;
        private string evidenceProviderId;

        public TmdbApiClient(IHttpClient http, IJsonSerializer json, ILogger logger, TmdbArchiveRepository repository)
        {
            this.http = http; this.json = json; this.logger = logger; this.repository = repository;
            concurrencyGate = new SemaphoreSlim(Math.Max(1, Plugin.Instance.Configuration.TmdbMaximumConcurrentRequests));
        }

        public void SetEvidenceContext(string name, long embyId, string providerId)
        {
            evidenceName = name; evidenceEmbyId = embyId; evidenceProviderId = providerId;
        }

        public string EvidencePrefix => LogPrefix(string.Empty);

        private string LogPrefix(string path)
        {
            if (!evidenceEmbyId.HasValue) return "[housekeeping - - - TMDB -]";
            var id = evidenceProviderId;
            if (path.StartsWith("/person/", StringComparison.OrdinalIgnoreCase))
            {
                var value = path.Substring(8); var end = value.IndexOfAny(new[] { '/', '?' });
                id = end < 0 ? value : value.Substring(0, end);
            }
            return "[" + (evidenceName ?? "-") + " - " + evidenceEmbyId.Value + " - TMDB " + (string.IsNullOrWhiteSpace(id) ? "-" : id) + "]";
        }

        public Task<TmdbEntity> GetPerson(string id, CancellationToken ct) => Get<TmdbEntity>(
            "/person/" + id + "?append_to_response=external_ids,combined_credits,alternative_names", ct);
        public Task<TmdbEntity> GetMovie(string id, CancellationToken ct) => Get<TmdbEntity>("/movie/" + id + "?append_to_response=external_ids,credits,alternative_titles", ct);
        public Task<TmdbEntity> GetSeries(string id, CancellationToken ct) => Get<TmdbEntity>("/tv/" + id + "?append_to_response=external_ids,aggregate_credits,alternative_titles", ct);
        public Task<TmdbEntity> GetEpisode(string seriesId, int season, int episode, CancellationToken ct) => Get<TmdbEntity>("/tv/" + seriesId + "/season/" + season + "/episode/" + episode + "?append_to_response=external_ids,credits", ct);
        public Task<TmdbFindResponse> FindImdb(string imdbId, CancellationToken ct) => Get<TmdbFindResponse>("/find/" + Uri.EscapeDataString(imdbId) + "?external_source=imdb_id", ct);
        public Task<TmdbPersonSearchResponse> SearchPerson(string name, CancellationToken ct) => Get<TmdbPersonSearchResponse>("/search/person?query=" + Uri.EscapeDataString(name), ct);

        private async Task<T> Get<T>(string path, CancellationToken ct, string legacyCachePath = null)
        {
            if (repository.TryGetApiResponse(path, out var cached)) { Interlocked.Increment(ref cacheHits); logger.Debug("{0} TMDB Archive API cache hit: {1}", LogPrefix(path), path); return json.DeserializeFromString<T>(cached); }
            if (!string.IsNullOrWhiteSpace(legacyCachePath) && repository.TryGetApiResponse(legacyCachePath, out cached))
            {
                Interlocked.Increment(ref cacheHits);
                logger.Debug("{0} TMDB Archive using compatible legacy cache entry: {1}", LogPrefix(path), legacyCachePath);
                return json.DeserializeFromString<T>(cached);
            }
            Interlocked.Increment(ref cacheMisses);
            logger.Debug("{0} TMDB Archive API cache miss: {1}; successful response will be cached", LogPrefix(path), path);
            var apiKey = Plugin.Instance.Configuration.TmdbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("TMDB API key is not configured.");
            Exception last = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await concurrencyGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await Throttle(ct).ConfigureAwait(false);
                    var separator = path.IndexOf('?') >= 0 ? "&" : "?";
                    var options = new HttpRequestOptions { Url = BaseUrl + path + separator + "api_key=" + Uri.EscapeDataString(apiKey), CancellationToken = ct, BufferContent = false };
                    using (var response = await http.SendAsync(options, "GET").ConfigureAwait(false))
                    using (var stream = response.Content)
                    using (var reader = new StreamReader(stream))
                    {
                        var raw = await reader.ReadToEndAsync().ConfigureAwait(false);
                        var result = json.DeserializeFromString<T>(raw);
                        repository.SaveApiResponse(path, raw);
                        return result;
                    }
                }
                catch (HttpException ex) when (!ex.StatusCode.HasValue || ex.StatusCode == (HttpStatusCode)429 || (int)ex.StatusCode.Value >= 500)
                {
                    last = ex;
                    var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt + 1)));
                    logger.Warn("{0} TMDB transient response on {1}; retry {2}/5 in {3}s", LogPrefix(path), path, attempt + 1, delay.TotalSeconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (IOException ex) { last = ex; await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct).ConfigureAwait(false); }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { last = ex; await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct).ConfigureAwait(false); }
                finally { concurrencyGate.Release(); }
            }
            throw last ?? new InvalidOperationException("TMDB request failed");
        }

        private async Task Throttle(CancellationToken ct)
        {
            await throttleGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var delay = nextRequestUtc - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
                nextRequestUtc = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(0, Plugin.Instance.Configuration.TmdbMinimumRequestIntervalMilliseconds));
            }
            finally { throttleGate.Release(); }
        }
    }
}
