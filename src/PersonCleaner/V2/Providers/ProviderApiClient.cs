using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Serialization;
using PersonCleaner.Configuration;
using PersonCleaner.V2.Domain;
using PersonCleaner.V2.Storage;
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.V2.Providers
{
    internal sealed class ProviderApiClient
    {
        private const string TmdbBase = "https://api.themoviedb.org/3";
        private const string TvdbBase = "https://api4.thetvdb.com/v4";
        private readonly IHttpClient http;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private readonly SemaphoreSlim tmdbGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim tvdbGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim tvdbAuthenticationGate = new SemaphoreSlim(1, 1);
        private DateTimeOffset nextTmdb;
        private DateTimeOffset nextTvdb;
        private string tvdbToken;
        private DateTimeOffset tvdbTokenExpires;

        public ProviderApiClient(IHttpClient http, IJsonSerializer json, ILogger logger) { this.http = http; this.json = json; this.logger = logger; }

        public async Task<string> Fetch(QueueItem item, CancellationToken cancellationToken)
        {
            if (item.Provider == ProviderNames.Tvdb) await EnsureTvdbToken(cancellationToken).ConfigureAwait(false);
            var path = PathFor(item);
            Exception last = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await Throttle(item.Provider, cancellationToken).ConfigureAwait(false);
                    var options = new HttpRequestOptions { Url = UrlFor(item.Provider, path), CancellationToken = cancellationToken, BufferContent = false };
                    if (item.Provider == ProviderNames.Tvdb) options.RequestHeaders["Authorization"] = "Bearer " + tvdbToken;
                    using (var response = await http.SendAsync(options, "GET").ConfigureAwait(false))
                    using (var reader = new StreamReader(response.Content)) return await reader.ReadToEndAsync().ConfigureAwait(false);
                }
                catch (HttpException ex) when (!ex.StatusCode.HasValue || ex.StatusCode == (HttpStatusCode)429 || (int)ex.StatusCode.Value >= 500) { last = ex; }
                catch (IOException ex) { last = ex; }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested) { last = ex; }
                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt + 1)));
                var reason = last is HttpException httpError && httpError.StatusCode.HasValue ? "HTTP " + (int)httpError.StatusCode.Value : last?.GetType().Name ?? "unknown error";
                logger.Warn("PersonCleaner {0} transient fetch failure for {1}:{2} ({3}); retry {4}/5 in {5}s", item.Provider.ToUpperInvariant(), item.EntityType, item.ProviderId, reason, attempt + 1, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            throw last ?? new InvalidOperationException("Provider request failed without an exception.");
        }

        private static string PathFor(QueueItem item)
        {
            if (item.Provider == ProviderNames.Tmdb)
            {
                if (item.EntityType == "person") return "/person/" + Uri.EscapeDataString(item.ProviderId) + "?append_to_response=external_ids";
                var type = item.MediaType == MediaTypes.Movie ? "movie" : "tv";
                var credits = item.MediaType == MediaTypes.Movie ? "credits" : "aggregate_credits";
                return "/" + type + "/" + Uri.EscapeDataString(item.ProviderId) + "?append_to_response=external_ids," + credits;
            }
            if (item.EntityType == "person") return "/people/" + Uri.EscapeDataString(item.ProviderId) + "/extended";
            var collection = item.MediaType == MediaTypes.Movie ? "movies" : "series";
            return "/" + collection + "/" + Uri.EscapeDataString(item.ProviderId) + "/extended";
        }

        private static string UrlFor(string provider, string path)
        {
            if (provider == ProviderNames.Tvdb) return TvdbBase + path;
            var separator = path.IndexOf('?') >= 0 ? "&" : "?";
            return TmdbBase + path + separator + "api_key=" + Uri.EscapeDataString(Plugin.Instance.Configuration.TmdbApiKey ?? string.Empty);
        }

        private async Task EnsureTvdbToken(CancellationToken cancellationToken)
        {
            if (HasValidTvdbToken()) return;
            await tvdbAuthenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (HasValidTvdbToken()) return;
                var configuration = Plugin.Instance.Configuration;
                if (string.IsNullOrWhiteSpace(configuration.TvdbApiKey)) throw new InvalidOperationException("TVDB API key is not configured.");
                logger.Debug("PersonCleaner TVDB authentication starting; subscriber PIN configured={0}. Other TVDB workers will wait for this single token refresh.", !string.IsNullOrWhiteSpace(configuration.TvdbSubscriberPin));
                var request = new TvdbLoginRequest { apikey = configuration.TvdbApiKey, pin = string.IsNullOrWhiteSpace(configuration.TvdbSubscriberPin) ? null : configuration.TvdbSubscriberPin };
                var options = new HttpRequestOptions { Url = TvdbBase + "/login", CancellationToken = cancellationToken, RequestContentType = "application/json", RequestContent = json.SerializeToString(request).AsMemory() };
                using (var response = await http.SendAsync(options, "POST").ConfigureAwait(false))
                using (var stream = response.Content)
                {
                    var login = await json.DeserializeFromStreamAsync<TvdbResponse<TvdbLogin>>(stream).ConfigureAwait(false);
                    tvdbToken = login?.data?.token;
                    if (string.IsNullOrWhiteSpace(tvdbToken)) throw new InvalidOperationException("TVDB login returned no bearer token.");
                    tvdbTokenExpires = DateTimeOffset.UtcNow.AddDays(28);
                    logger.Debug("PersonCleaner TVDB authentication succeeded; the in-memory bearer token will be reused until {0:O}. The token is not stored or logged.", tvdbTokenExpires);
                }
            }
            finally { tvdbAuthenticationGate.Release(); }
        }

        private bool HasValidTvdbToken() => !string.IsNullOrWhiteSpace(tvdbToken) && tvdbTokenExpires > DateTimeOffset.UtcNow.AddDays(1);

        private async Task Throttle(string provider, CancellationToken cancellationToken)
        {
            var gate = provider == ProviderNames.Tmdb ? tmdbGate : tvdbGate;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var next = provider == ProviderNames.Tmdb ? nextTmdb : nextTvdb;
                var delay = next - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                var configured = provider == ProviderNames.Tmdb ? Plugin.Instance.Configuration.TmdbMinimumRequestIntervalMilliseconds : Plugin.Instance.Configuration.TvdbMinimumRequestIntervalMilliseconds;
                if (provider == ProviderNames.Tmdb) nextTmdb = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(0, configured)); else nextTvdb = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(0, configured));
            }
            finally { gate.Release(); }
        }
    }
}
