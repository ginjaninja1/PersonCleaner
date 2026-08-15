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

        public TmdbApiClient(IHttpClient http, IJsonSerializer json, ILogger logger, TmdbArchiveRepository repository)
        {
            this.http = http; this.json = json; this.logger = logger; this.repository = repository;
            concurrencyGate = new SemaphoreSlim(Math.Max(1, Plugin.Instance.Configuration.TmdbMaximumConcurrentRequests));
        }

        public Task<TmdbEntity> GetPerson(string id, CancellationToken ct) => Get<TmdbEntity>("/person/" + id + "?append_to_response=external_ids,combined_credits,alternative_names", ct);
        public Task<TmdbEntity> GetMovie(string id, CancellationToken ct) => Get<TmdbEntity>("/movie/" + id + "?append_to_response=external_ids,credits,alternative_titles", ct);
        public Task<TmdbEntity> GetSeries(string id, CancellationToken ct) => Get<TmdbEntity>("/tv/" + id + "?append_to_response=external_ids,aggregate_credits,alternative_titles", ct);
        public Task<TmdbEntity> GetEpisode(string seriesId, int season, int episode, CancellationToken ct) => Get<TmdbEntity>("/tv/" + seriesId + "/season/" + season + "/episode/" + episode + "?append_to_response=external_ids,credits", ct);
        public Task<TmdbFindResponse> FindImdb(string imdbId, CancellationToken ct) => Get<TmdbFindResponse>("/find/" + Uri.EscapeDataString(imdbId) + "?external_source=imdb_id", ct);

        private async Task<T> Get<T>(string path, CancellationToken ct)
        {
            if (repository.TryGetApiResponse(path, out var cached)) return json.DeserializeFromString<T>(cached);
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
                    logger.Warn("TMDB transient response on {0}; retry {1}/5 in {2}s", path, attempt + 1, delay.TotalSeconds);
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
