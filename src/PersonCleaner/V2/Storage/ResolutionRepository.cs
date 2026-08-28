using MediaBrowser.Common.Configuration;
using PersonCleaner.V2.Domain;
using SQLitePCL.pretty;
using SQLitePCLEx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PersonCleaner.V2.Storage
{
    internal sealed class ResolutionRepository : IDisposable
    {
        private const int SchemaVersion = 12;
        private readonly object sync = new object();
        [ThreadStatic] private static StatementBatch activeStatementBatch;
        private IDatabaseConnection db;
        public string WorkspacePath { get; }
        public string DatabasePath { get; }
        public string PayloadPath { get; }

        public ResolutionRepository(IApplicationPaths paths)
        {
            WorkspacePath = Path.Combine(paths.DataPath, "personcleaner-v2");
            DatabasePath = Path.Combine(WorkspacePath, "entity-resolution.db");
            PayloadPath = Path.Combine(WorkspacePath, "payload-cache");
        }

        public void Initialize()
        {
            lock (sync)
            {
                if (db != null) return;
                Directory.CreateDirectory(WorkspacePath);
                Directory.CreateDirectory(PayloadPath);
                db = SQLite3.Open(DatabasePath, ConnectionFlags.ReadWrite | ConnectionFlags.Create | ConnectionFlags.FullMutex, null,
                    new Dictionary<string, delegate_collation>(),
                    new Dictionary<Tuple<string, int>, Action<IReadOnlyList<sqlite3_value>, sqlite3_context>>(), true, false);
                db.Execute("PRAGMA journal_mode=WAL");
                db.Execute("PRAGMA synchronous=NORMAL");
                db.Execute("PRAGMA foreign_keys=ON");
                db.Execute("PRAGMA busy_timeout=30000");
                db.Execute(Schema[0]);
                int? version = null;
                using (var s = db.PrepareStatement("SELECT version FROM schema_info WHERE singleton=1")) foreach (var r in s.Rows()) version = r.GetInt(0);
                if (version.HasValue && version.Value != SchemaVersion)
                {
                    db.Dispose(); db = null;
                    throw new InvalidOperationException("PersonCleaner database schema " + version.Value + " is not schema " + SchemaVersion + ". Stop Emby and apply the remaining numbered SQL files in migrations\\ offline before restarting Emby.");
                }
                foreach (var sql in Schema) db.Execute(sql);
                if (!version.HasValue) db.Execute("INSERT INTO schema_info(singleton,version) VALUES(1," + SchemaVersion.ToString(CultureInfo.InvariantCulture) + ")");
                if (!ColumnExists("provider_media", "slug") || !ColumnExists("provider_media_credit", "role_category") || !ColumnExists("resolution_decision", "local_anchor_confidence") || !ColumnExists("cache_manifest", "materializer_version") || !ColumnExists("provider_media_observation", "materializer_version") || !ColumnExists("work_queue", "graph_eligible") || !ColumnExists("work_queue", "route_series_id") || !ColumnExists("current_media", "tmdb_acquisition_id") || !ColumnExists("resolution_run", "selected_episodes") || !ColumnExists("resolution_case", "presentation_purpose") || !TableExists("resolution_pair") || !TableExists("resolution_cluster") || !TableExists("provider_correction") || !TableExists("correction_application") || !TableExists("provider_correction_selection") || !TableExists("provider_absence_cache") || !TableExists("acquisition_observation") || !TableExists("global_local_person") || !TableExists("resolution_credit_assignment") || !TableExists("resolution_case") || !TableExists("resolution_case_person_snapshot") || !TableExists("resolution_identity_outcome") || !TableExists("resolution_case_credit") || !TableExists("resolution_case_credit_attribution") || !TableExists("resolution_question") || !TableExists("identity_case_apply"))
                {
                    db.Dispose(); db = null;
                    throw new InvalidOperationException("PersonCleaner schema 12 is incomplete. Stop Emby and restore the most recent pre-migration backup before applying the numbered migrations again.");
                }
                // v2 originally represented the non-media dimension of person
                // work as an empty string. Emby's SQLite binder can coerce an
                // empty bound string to NULL, which violates these key columns.
                // Normalize existing workspaces in place to a stable sentinel.
                db.Execute("UPDATE work_queue SET media_type='person' WHERE entity_type='person' AND media_type=''");
                db.Execute("UPDATE cache_manifest SET media_type='person' WHERE entity_type='person' AND media_type=''");
                db.Execute("UPDATE fetch_failure SET media_type='person' WHERE entity_type='person' AND media_type=''");
            }
        }

        public long BeginRun(string mode)
        {
            lock (sync)
            {
                var runId = 0L;
                db.RunInTransaction(x =>
                {
                    Statement(x, "INSERT INTO resolution_run(status,mode,phase,started_utc,updated_utc,message) VALUES('running',@mode,'snapshot',@now,@now,'Selecting bounded media sample')", s => { s.Bind("@mode", mode); s.Bind("@now", Now()); });
                    using (var s = x.PrepareStatement("SELECT last_insert_rowid()")) foreach (var row in s.Rows()) runId = row.GetInt64(0);
                    if (runId <= 0) throw new InvalidOperationException("Unable to create a PersonCleaner run.");
                    // Keep the most recent completed evidence visible while its
                    // replacement is running. Abandoned, failed and older runs
                    // have no dashboard value and their run-scoped rows cascade.
                    Statement(x, "DELETE FROM resolution_run WHERE run_id<>@run AND run_id<>coalesce((SELECT max(run_id) FROM resolution_run WHERE status='completed' AND run_id<>@run),-1)", s => s.Bind("@run", runId));
                }, TransactionMode.Immediate);
                return runId;
            }
        }

        public void UpdateRun(long runId, string phase, string message)
        {
            lock (sync) Statement("UPDATE resolution_run SET phase=@phase,message=@message,updated_utc=@now WHERE run_id=@run", s => { s.Bind("@phase", phase); s.Bind("@message", message); s.Bind("@now", Now()); s.Bind("@run", runId); });
        }

        public void IncrementRun(long runId, string column)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "media_fetched", "people_fetched", "cache_hits", "failures" };
            if (!allowed.Contains(column)) throw new ArgumentOutOfRangeException(nameof(column));
            lock (sync) db.Execute("UPDATE resolution_run SET " + column + "=" + column + "+1,updated_utc=" + Now().ToString(CultureInfo.InvariantCulture) + " WHERE run_id=" + runId.ToString(CultureInfo.InvariantCulture));
        }

        public void FinishRun(long runId, string status, string message, int decisions)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "UPDATE resolution_run SET status=@status,phase=@phase,message=@message,decisions=@decisions,finished_utc=@now,updated_utc=@now WHERE run_id=@run", s => { s.Bind("@status", status); s.Bind("@phase", status == "completed" ? "complete" : status); s.Bind("@message", message); s.Bind("@decisions", decisions); s.Bind("@now", Now()); s.Bind("@run", runId); });
                if (status == "completed")
                    Statement(x, "DELETE FROM resolution_run WHERE run_id<>@run", s => s.Bind("@run", runId));
                else
                    Statement(x, "DELETE FROM resolution_run WHERE run_id=@run", s => s.Bind("@run", runId));
            }, TransactionMode.Immediate);
        }

        public void ReplaceSnapshot(long runId, IReadOnlyCollection<MediaSeed> media, IReadOnlyCollection<LocalPerson> people, IReadOnlyCollection<LocalCredit> credits, IReadOnlyCollection<LocalPerson> globalPeople)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                x.Execute("DELETE FROM current_media"); x.Execute("DELETE FROM current_local_person"); x.Execute("DELETE FROM current_local_credit"); x.Execute("DELETE FROM global_local_person"); x.Execute("DELETE FROM current_provider_media"); x.Execute("DELETE FROM work_queue");
                foreach (var item in media)
                {
                    Statement(x, @"INSERT INTO current_media(emby_id,media_type,name,production_year,tmdb_id,tvdb_id,imdb_id,tmdb_acquisition_id,tvdb_acquisition_id,parent_emby_id,parent_tmdb_id,parent_tvdb_id,season_number,episode_number)
VALUES(@id,@type,@name,@year,@tmdb,@tvdb,@imdb,@tmdbAcquisition,@tvdbAcquisition,@parent,@parentTmdb,@parentTvdb,@season,@episode)", s => { s.Bind("@id", item.EmbyId); s.Bind("@type", item.MediaType); s.Bind("@name", item.Name); s.Bind("@year", item.Year); s.Bind("@tmdb", item.TmdbId); s.Bind("@tvdb", item.TvdbId); s.Bind("@imdb", item.ImdbId); s.Bind("@tmdbAcquisition", item.ProviderAcquisitionId(ProviderNames.Tmdb)); s.Bind("@tvdbAcquisition", item.ProviderAcquisitionId(ProviderNames.Tvdb)); s.Bind("@parent", item.ParentEmbyId); s.Bind("@parentTmdb", item.ParentTmdbId); s.Bind("@parentTvdb", item.ParentTvdbId); s.Bind("@season", item.SeasonNumber); s.Bind("@episode", item.EpisodeNumber); });
                    SeedMedia(x, runId, ProviderNames.Tmdb, item.MediaType, item.ProviderAcquisitionId(ProviderNames.Tmdb), item.ParentTmdbId, item.SeasonNumber, item.EpisodeNumber);
                    SeedMedia(x, runId, ProviderNames.Tvdb, item.MediaType, item.ProviderAcquisitionId(ProviderNames.Tvdb), item.ParentTvdbId, item.SeasonNumber, item.EpisodeNumber);
                }
                ApplyLocalMediaQueueCorrections(x, runId, media, LoadCorrections());
                foreach (var person in people)
                    Statement(x, "INSERT INTO current_local_person VALUES(@id,@name,@tmdb,@tvdb,@imdb)", s => { s.Bind("@id", person.EmbyId); s.Bind("@name", person.Name); s.Bind("@tmdb", person.TmdbId); s.Bind("@tvdb", person.TvdbId); s.Bind("@imdb", person.ImdbId); });
                foreach (var person in globalPeople ?? new LocalPerson[0])
                    Statement(x, "INSERT INTO global_local_person VALUES(@id,@name,@tmdb,@tvdb,@imdb)", s => { s.Bind("@id", person.EmbyId); s.Bind("@name", person.Name); s.Bind("@tmdb", person.TmdbId); s.Bind("@tvdb", person.TvdbId); s.Bind("@imdb", person.ImdbId); });
                foreach (var credit in credits.Distinct(new LocalCreditComparer()))
                    Statement(x, "INSERT INTO current_local_credit VALUES(@person,@media,@role)", s => { s.Bind("@person", credit.PersonEmbyId); s.Bind("@media", credit.MediaEmbyId); s.Bind("@role", credit.Role); });
                var movies = media.Count(x => x.MediaType == MediaTypes.Movie); var series = media.Count(x => x.MediaType == MediaTypes.Series); var episodes = media.Count(x => x.MediaType == MediaTypes.Episode);
                Statement(x, "UPDATE resolution_run SET selected_movies=@movies,selected_series=@series,selected_episodes=@episodes,updated_utc=@now WHERE run_id=@run", s => { s.Bind("@movies", movies); s.Bind("@series", series); s.Bind("@episodes", episodes); s.Bind("@now", Now()); s.Bind("@run", runId); });
            }, TransactionMode.Immediate);
        }

        public List<QueueItem> PendingMedia()
        {
            var result = new List<QueueItem>();
            lock (sync) using (var s = db.PrepareStatement("SELECT provider,entity_type,provider_id,media_type,priority,graph_eligible,route_series_id,route_season_number,route_episode_number FROM work_queue WHERE status='pending' AND entity_type='media' ORDER BY priority DESC,provider,media_type,provider_id"))
                foreach (var r in s.Rows()) result.Add(ReadQueue(r));
            return result;
        }

        public List<QueueItem> PendingPeople()
        {
            var result = new List<QueueItem>();
            lock (sync) using (var s = db.PrepareStatement("SELECT provider,entity_type,provider_id,media_type,priority,graph_eligible,route_series_id,route_season_number,route_episode_number FROM work_queue WHERE status='pending' AND entity_type='person' ORDER BY graph_eligible DESC,priority DESC,provider,provider_id"))
                foreach (var r in s.Rows()) result.Add(ReadQueue(r));
            return result;
        }

        public PersonSeedSummary SeedDiscoveredPeople()
        {
            var localByMedia = new Dictionary<long, LocalPersonScope>(capacity: 128);
            var validation = new HashSet<string>(StringComparer.Ordinal);
            lock (sync)
            {
                using (var s = db.PrepareStatement(@"SELECT DISTINCT c.media_emby_id,p.emby_id,p.name,p.tmdb_id,p.tvdb_id,p.imdb_id
FROM current_local_credit c JOIN current_local_person p ON p.emby_id=c.person_emby_id"))
                    foreach (var r in s.Rows())
                    {
                        var mediaId = r.GetInt64(0);
                        if (!localByMedia.TryGetValue(mediaId, out var scope)) localByMedia[mediaId] = scope = new LocalPersonScope();
                        var tmdbId = Null(r, 3); var tvdbId = Null(r, 4);
                        scope.Add(r.GetString(2), tmdbId, tvdbId);
                        AddProviderKey(validation, ProviderNames.Tmdb, tmdbId);
                        AddProviderKey(validation, ProviderNames.Tvdb, tvdbId);
                    }
                var discovered = new HashSet<string>(StringComparer.Ordinal);
                var selected = new HashSet<string>(StringComparer.Ordinal);
                // Keep current_media as the outer loop and probe the leading
                // provider/media columns of provider_media_credit's primary key.
                // A single OR join makes SQLite scan the entire historical credit
                // archive and compare it with every current title of the same type.
                using (var s = db.PrepareStatement(@"SELECT c.provider,c.provider_person_id,c.person_name,m.emby_id
FROM current_media m
CROSS JOIN provider_media_credit c
  ON c.provider='tmdb' AND c.media_type=m.media_type AND c.provider_media_id=coalesce(m.tmdb_acquisition_id,m.tmdb_id)
WHERE coalesce(m.tmdb_acquisition_id,m.tmdb_id) IS NOT NULL
UNION
SELECT c.provider,c.provider_person_id,c.person_name,m.emby_id
FROM current_media m
CROSS JOIN provider_media_credit c
  ON c.provider='tvdb' AND c.media_type=m.media_type AND c.provider_media_id=coalesce(m.tvdb_acquisition_id,m.tvdb_id)
WHERE coalesce(m.tvdb_acquisition_id,m.tvdb_id) IS NOT NULL"))
                    foreach (var r in s.Rows())
                    {
                        var provider = r.GetString(0); var providerId = r.GetString(1); var key = provider + ":" + providerId;
                        discovered.Add(key);
                        if (localByMedia.TryGetValue(r.GetInt64(3), out var scope) && scope.Matches(provider, providerId, Null(r, 2))) selected.Add(key);
                    }

                foreach (var correction in LoadCorrections())
                {
                    if (correction.Kind == CorrectionKinds.MediaCredit && correction.Operation == CorrectionOperations.Replace)
                        AddCorrectionPerson(selected, correction.Provider, correction.ReplacementValue);
                    else if (correction.Kind == CorrectionKinds.PersonField || correction.Kind == CorrectionKinds.PersonExternalId)
                        AddCorrectionPerson(selected, correction.Provider, correction.ProviderPersonId);
                    else if (correction.Kind == CorrectionKinds.LocalPersonBinding && correction.Operation == CorrectionOperations.Replace)
                        AddCorrectionPerson(selected, correction.Provider, correction.ReplacementValue);
                    else if (correction.Kind == CorrectionKinds.IdentityRelation)
                    {
                        AddCorrectionPerson(selected, correction.Provider, correction.ProviderPersonId);
                        AddCorrectionPerson(selected, correction.SecondaryProvider, correction.SecondaryId);
                    }
                }

                db.RunInTransaction(x =>
                {
                    foreach (var key in validation.Union(selected, StringComparer.Ordinal))
                    {
                        var separator = key.IndexOf(':'); var provider = key.Substring(0, separator); var providerId = key.Substring(separator + 1);
                        var graphEligible = selected.Contains(key) ? 1 : 0;
                        Statement(x, "INSERT OR IGNORE INTO work_queue(provider,entity_type,media_type,provider_id,priority,status,attempts,error,updated_utc,graph_eligible) VALUES(@provider,'person','person',@person,@priority,'pending',0,NULL,@now,@graph)", s => { s.Bind("@provider", provider); s.Bind("@person", providerId); s.Bind("@priority", graphEligible == 1 ? 2 : 1); s.Bind("@now", Now()); s.Bind("@graph", graphEligible); });
                        if (graphEligible == 1)
                            Statement(x, "UPDATE work_queue SET graph_eligible=1,priority=2 WHERE provider=@provider AND entity_type='person' AND media_type='person' AND provider_id=@person", s => { s.Bind("@provider", provider); s.Bind("@person", providerId); });
                    }
                }, TransactionMode.Immediate);

                return new PersonSeedSummary
                {
                    DiscoveredTmdb = discovered.Count(x => x.StartsWith(ProviderNames.Tmdb + ":", StringComparison.Ordinal)),
                    DiscoveredTvdb = discovered.Count(x => x.StartsWith(ProviderNames.Tvdb + ":", StringComparison.Ordinal)),
                    SelectedTmdb = selected.Count(x => x.StartsWith(ProviderNames.Tmdb + ":", StringComparison.Ordinal)),
                    SelectedTvdb = selected.Count(x => x.StartsWith(ProviderNames.Tvdb + ":", StringComparison.Ordinal)),
                    ValidationTmdb = validation.Count(x => x.StartsWith(ProviderNames.Tmdb + ":", StringComparison.Ordinal)),
                    ValidationTvdb = validation.Count(x => x.StartsWith(ProviderNames.Tvdb + ":", StringComparison.Ordinal))
                };
            }
        }

        public bool IsFailureRetryDue(QueueItem item, int retryMinutes)
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(1, retryMinutes)).ToUnixTimeSeconds();
            lock (sync) using (var s = db.PrepareStatement("SELECT last_failed_utc FROM fetch_failure WHERE provider=@provider AND entity_type=@entity AND media_type=@media AND provider_id=@id"))
            {
                s.Bind("@provider", item.Provider); s.Bind("@entity", item.EntityType); s.Bind("@media", Dimension(item.EntityType, item.MediaType)); s.Bind("@id", item.ProviderId);
                foreach (var r in s.Rows()) return r.GetInt64(0) <= cutoff;
            }
            return true;
        }

        public void RecordFailure(QueueItem item, string error)
        {
            lock (sync) Statement("INSERT OR REPLACE INTO fetch_failure VALUES(@provider,@entity,@media,@id,@now,@error)", s => { s.Bind("@provider", item.Provider); s.Bind("@entity", item.EntityType); s.Bind("@media", Dimension(item.EntityType, item.MediaType)); s.Bind("@id", item.ProviderId); s.Bind("@now", Now()); s.Bind("@error", error); });
        }

        public void ClearFailure(QueueItem item)
        {
            lock (sync) Statement("DELETE FROM fetch_failure WHERE provider=@provider AND entity_type=@entity AND media_type=@media AND provider_id=@id", s => { s.Bind("@provider", item.Provider); s.Bind("@entity", item.EntityType); s.Bind("@media", Dimension(item.EntityType, item.MediaType)); s.Bind("@id", item.ProviderId); });
        }

        public AbsenceCacheEntry GetAbsence(string provider, string entityType, string mediaType, string providerId)
        {
            lock (sync) using (var s = db.PrepareStatement("SELECT confirmed_utc,status_code FROM provider_absence_cache WHERE provider=@provider AND entity_type=@entity AND media_type=@media AND provider_id=@id"))
            {
                s.Bind("@provider", provider); s.Bind("@entity", entityType); s.Bind("@media", Dimension(entityType, mediaType)); s.Bind("@id", providerId);
                foreach (var r in s.Rows()) return new AbsenceCacheEntry { Provider = provider, EntityType = entityType, MediaType = mediaType, ProviderId = providerId, ConfirmedUnix = r.GetInt64(0), StatusCode = r.GetInt(1) };
            }
            return null;
        }

        public void RecordAbsent(long runId, QueueItem item, int statusCode, string source)
        {
            var now = Now();
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "INSERT OR REPLACE INTO provider_absence_cache(provider,entity_type,media_type,provider_id,confirmed_utc,status_code) VALUES(@provider,@entity,@media,@id,@now,@status)", s => { s.Bind("@provider", item.Provider); s.Bind("@entity", item.EntityType); s.Bind("@media", Dimension(item.EntityType, item.MediaType)); s.Bind("@id", item.ProviderId); s.Bind("@now", now); s.Bind("@status", statusCode); });
                RecordAcquisition(x, runId, item, AcquisitionStates.Absent, source, "HTTP " + statusCode.ToString(CultureInfo.InvariantCulture), now);
            }, TransactionMode.Immediate);
        }

        public void ClearAbsence(QueueItem item)
        {
            lock (sync) Statement("DELETE FROM provider_absence_cache WHERE provider=@provider AND entity_type=@entity AND media_type=@media AND provider_id=@id", s => { s.Bind("@provider", item.Provider); s.Bind("@entity", item.EntityType); s.Bind("@media", Dimension(item.EntityType, item.MediaType)); s.Bind("@id", item.ProviderId); });
        }

        public void RecordAcquisition(long runId, QueueItem item, string outcome, string source, string detail = null)
        {
            lock (sync) RecordAcquisition(db, runId, item, outcome, source, detail, Now());
        }

        private static void RecordAcquisition(IDatabaseConnection connection, long runId, QueueItem item, string outcome, string source, string detail, long observedUtc)
        {
            Statement(connection, "INSERT OR REPLACE INTO acquisition_observation(run_id,provider,entity_type,media_type,provider_id,outcome,source,graph_eligible,observed_utc,detail) VALUES(@run,@provider,@entity,@media,@id,@outcome,@source,@graph,@now,@detail)", s => { s.Bind("@run", runId); s.Bind("@provider", item.Provider); s.Bind("@entity", item.EntityType); s.Bind("@media", Dimension(item.EntityType, item.MediaType)); s.Bind("@id", item.ProviderId); s.Bind("@outcome", outcome); s.Bind("@source", source); s.Bind("@graph", item.GraphEligible ? 1 : 0); s.Bind("@now", observedUtc); s.Bind("@detail", detail); });
        }

        public CacheEntry GetCache(string provider, string entityType, string mediaType, string providerId)
        {
            lock (sync) using (var s = db.PrepareStatement("SELECT payload_hash,relative_path,last_fetched_utc,materializer_version FROM cache_manifest WHERE provider=@provider AND entity_type=@entity AND media_type=@media AND provider_id=@id"))
            {
                s.Bind("@provider", provider); s.Bind("@entity", entityType); s.Bind("@media", Dimension(entityType, mediaType)); s.Bind("@id", providerId);
                foreach (var r in s.Rows()) return new CacheEntry { Provider = provider, EntityType = entityType, MediaType = mediaType, ProviderId = providerId, PayloadHash = r.GetString(0), RelativePath = r.GetString(1), LastFetchedUnix = r.GetInt64(2), MaterializerVersion = r.GetInt(3) };
            }
            return null;
        }

        public void SaveCache(CacheEntry entry)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "INSERT OR REPLACE INTO cache_manifest(provider,entity_type,media_type,provider_id,payload_hash,relative_path,last_fetched_utc,materializer_version) VALUES(@provider,@entity,@media,@id,@hash,@path,@fetched,@materializer)", s => { s.Bind("@provider", entry.Provider); s.Bind("@entity", entry.EntityType); s.Bind("@media", Dimension(entry.EntityType, entry.MediaType)); s.Bind("@id", entry.ProviderId); s.Bind("@hash", entry.PayloadHash); s.Bind("@path", entry.RelativePath); s.Bind("@fetched", entry.LastFetchedUnix); s.Bind("@materializer", entry.MaterializerVersion); });
                if (entry.EntityType == "media")
                    Statement(x, "INSERT OR REPLACE INTO provider_media_observation(provider,media_type,provider_media_id,payload_hash,observed_utc,endpoint_shape,credit_scope,is_complete,materializer_version) VALUES(@provider,@type,@id,@hash,@fetched,@endpoint,'screen-roles',1,@materializer)", s => { s.Bind("@provider", entry.Provider); s.Bind("@type", entry.MediaType); s.Bind("@id", entry.ProviderId); s.Bind("@hash", entry.PayloadHash); s.Bind("@fetched", entry.LastFetchedUnix); s.Bind("@endpoint", entry.Provider == ProviderNames.Tvdb ? "extended-full" : "details-with-credits"); s.Bind("@materializer", entry.MaterializerVersion); });
            }, TransactionMode.Immediate);
        }

        public void MarkQueue(QueueItem item, string status, string error = null)
        {
            lock (sync) Statement("UPDATE work_queue SET status=@status,attempts=attempts+1,error=@error,updated_utc=@now WHERE provider=@provider AND entity_type=@entity AND media_type=@media AND provider_id=@id", s => { s.Bind("@status", status); s.Bind("@error", error); s.Bind("@now", Now()); s.Bind("@provider", item.Provider); s.Bind("@entity", item.EntityType); s.Bind("@media", Dimension(item.EntityType, item.MediaType)); s.Bind("@id", item.ProviderId); });
        }

        public void ReplaceMedia(FlattenedMedia media)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "INSERT OR REPLACE INTO provider_media(provider,media_type,provider_media_id,name,updated_utc,slug) VALUES(@provider,@type,@id,@name,@now,@slug)", s => { s.Bind("@provider", media.Provider); s.Bind("@type", media.MediaType); s.Bind("@id", media.ProviderMediaId); s.Bind("@name", media.Name); s.Bind("@now", Now()); s.Bind("@slug", media.Slug); });
                DeleteForMedia(x, "media_external_id", media); DeleteForMedia(x, "provider_media_credit", media);
                foreach (var id in media.ExternalIds.Where(x => !string.IsNullOrWhiteSpace(x.Value)))
                    Statement(x, "INSERT OR IGNORE INTO media_external_id VALUES(@provider,@type,@id,@source,@external)", s => { s.Bind("@provider", media.Provider); s.Bind("@type", media.MediaType); s.Bind("@id", media.ProviderMediaId); s.Bind("@source", id.Key.ToLowerInvariant()); s.Bind("@external", id.Value); });
                foreach (var credit in media.Credits.Where(x => !string.IsNullOrWhiteSpace(x.ProviderPersonId)).GroupBy(x => x.ProviderPersonId + "|" + x.Role, StringComparer.Ordinal).Select(x => x.First()))
                {
                    Statement(x, "INSERT OR IGNORE INTO provider_media_credit(provider,media_type,provider_media_id,provider_person_id,person_name,role,role_category,role_name) VALUES(@provider,@type,@media,@person,@name,@role,@category,@roleName)", s => { s.Bind("@provider", media.Provider); s.Bind("@type", media.MediaType); s.Bind("@media", media.ProviderMediaId); s.Bind("@person", credit.ProviderPersonId); s.Bind("@name", credit.PersonName); s.Bind("@role", credit.Role); s.Bind("@category", Required(credit.RoleCategory, "Unknown")); s.Bind("@roleName", credit.RoleName); });
                }
            }, TransactionMode.Immediate);
        }

        public void ReplacePerson(FlattenedPerson person)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "INSERT OR REPLACE INTO provider_person VALUES(@provider,@id,@name,@clean,@birth,@now)", s => { s.Bind("@provider", person.Provider); s.Bind("@id", person.ProviderPersonId); s.Bind("@name", person.Name); s.Bind("@clean", TextNormalizer.PersonName(person.Name)); s.Bind("@birth", person.Birthday); s.Bind("@now", Now()); });
                Statement(x, "DELETE FROM person_external_id WHERE provider=@provider AND provider_person_id=@id", s => { s.Bind("@provider", person.Provider); s.Bind("@id", person.ProviderPersonId); });
                Statement(x, "DELETE FROM person_alias WHERE provider=@provider AND provider_person_id=@id", s => { s.Bind("@provider", person.Provider); s.Bind("@id", person.ProviderPersonId); });
                foreach (var id in person.ExternalIds.Where(x => !string.IsNullOrWhiteSpace(x.Value)))
                    Statement(x, "INSERT OR IGNORE INTO person_external_id VALUES(@provider,@id,@source,@external)", s => { s.Bind("@provider", person.Provider); s.Bind("@id", person.ProviderPersonId); s.Bind("@source", id.Key.ToLowerInvariant()); s.Bind("@external", id.Value); });
                foreach (var alias in person.Aliases.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                    Statement(x, "INSERT OR IGNORE INTO person_alias VALUES(@provider,@id,@alias,@clean)", s => { s.Bind("@provider", person.Provider); s.Bind("@id", person.ProviderPersonId); s.Bind("@alias", alias); s.Bind("@clean", TextNormalizer.PersonName(alias)); });
            }, TransactionMode.Immediate);
        }

        public ResolutionInput LoadResolutionInput(long? runId = null)
        {
            var input = new ResolutionInput { AcquisitionTrackingEnabled = true };
            lock (sync)
            {
                var acquisitionRunId = runId ?? LatestCompletedRunId();
                var tracker = new CorrectionApplicationTracker(LoadCorrections());
                using (var s = db.PrepareStatement(@"SELECT m.emby_id,m.media_type,m.name,m.production_year,m.tmdb_id,m.tvdb_id,m.imdb_id,
(SELECT p.slug FROM provider_media p WHERE p.provider='tvdb' AND p.media_type=m.media_type AND p.provider_media_id=coalesce(m.tvdb_acquisition_id,m.tvdb_id)),
m.tmdb_acquisition_id,m.tvdb_acquisition_id,m.parent_emby_id,m.parent_tmdb_id,m.parent_tvdb_id,m.season_number,m.episode_number FROM current_media m"))
                    foreach (var r in s.Rows()) input.Media.Add(new MediaSeed { EmbyId = r.GetInt64(0), MediaType = r.GetString(1), Name = r.GetString(2), Year = r.IsDBNull(3) ? (int?)null : r.GetInt(3), TmdbId = Null(r, 4), TvdbId = Null(r, 5), ImdbId = Null(r, 6), TvdbSlug = Null(r, 7), TmdbAcquisitionId = Null(r, 8), TvdbAcquisitionId = Null(r, 9), ParentEmbyId = r.IsDBNull(10) ? (long?)null : r.GetInt64(10), ParentTmdbId = Null(r, 11), ParentTvdbId = Null(r, 12), SeasonNumber = r.IsDBNull(13) ? (int?)null : r.GetInt(13), EpisodeNumber = r.IsDBNull(14) ? (int?)null : r.GetInt(14) });
                using (var s = db.PrepareStatement("SELECT emby_id,name,tmdb_id,tvdb_id,imdb_id FROM current_local_person")) foreach (var r in s.Rows()) input.LocalPeople.Add(new LocalPerson { EmbyId = r.GetInt64(0), Name = r.GetString(1), TmdbId = Null(r, 2), TvdbId = Null(r, 3), ImdbId = Null(r, 4) });
                using (var s = db.PrepareStatement("SELECT person_emby_id,media_emby_id,role FROM current_local_credit")) foreach (var r in s.Rows()) input.LocalCredits.Add(new LocalCredit { PersonEmbyId = r.GetInt64(0), MediaEmbyId = r.GetInt64(1), Role = r.GetString(2) });
                const string personSql = @"SELECT p.provider,p.provider_person_id,p.name,p.clean_name,p.birthday
FROM provider_person p
JOIN acquisition_observation a ON a.provider=p.provider AND a.entity_type='person' AND a.media_type='person' AND a.provider_id=p.provider_person_id
WHERE a.run_id=@run AND a.outcome='PRESENT' AND a.graph_eligible=1";
                using (var s = db.PrepareStatement(personSql))
                {
                    s.Bind("@run", acquisitionRunId);
                    foreach (var r in s.Rows()) input.ProviderPeople.Add(new ProviderPerson { Provider = r.GetString(0), ProviderId = r.GetString(1), Name = r.GetString(2), CleanName = r.GetString(3), Birthday = Null(r, 4) });
                }
                var byKey = input.ProviderPeople.ToDictionary(x => x.Key, StringComparer.Ordinal);
                using (var s = db.PrepareStatement("SELECT provider,provider_person_id,external_provider,external_id FROM person_external_id")) foreach (var r in s.Rows()) if (byKey.TryGetValue(r.GetString(0) + ":" + r.GetString(1), out var p)) p.ExternalIds[r.GetString(2)] = r.GetString(3);
                using (var s = db.PrepareStatement("SELECT provider,provider_person_id,alias FROM person_alias")) foreach (var r in s.Rows()) if (byKey.TryGetValue(r.GetString(0) + ":" + r.GetString(1), out var p)) p.Aliases.Add(r.GetString(2));
                const string mediaSql = @"SELECT m.provider,m.media_type,m.provider_media_id,e.external_provider,e.external_id
FROM current_provider_media m
JOIN acquisition_observation a ON a.provider=m.provider AND a.entity_type='media' AND a.media_type=m.media_type AND a.provider_id=m.provider_media_id
LEFT JOIN media_external_id e ON e.provider=m.provider AND e.media_type=m.media_type AND e.provider_media_id=m.provider_media_id
WHERE a.run_id=@run AND a.outcome='PRESENT'";
                var providerMedia = new Dictionary<string, CanonicalMediaBuilder>(StringComparer.Ordinal);
                using (var s = db.PrepareStatement(mediaSql))
                {
                    s.Bind("@run", acquisitionRunId);
                    foreach (var r in s.Rows())
                    {
                        var mediaKey = MediaIdentityResolver.RecordKey(r.GetString(0), r.GetString(1), r.GetString(2));
                        if (!providerMedia.TryGetValue(mediaKey, out var value)) providerMedia[mediaKey] = value = new CanonicalMediaBuilder { Provider = r.GetString(0), MediaType = r.GetString(1), NativeId = r.GetString(2) };
                        if (!r.IsDBNull(3) && !r.IsDBNull(4)) value.ExternalIds.Add(new MediaExternalIdentity { Provider = r.GetString(3), Id = r.GetString(4) });
                    }
                }
                var mediaIdentities = providerMedia.Values.Select(x => x.Identity()).ToList();
                ProviderCorrectionOverlay.ApplyMediaIdentities(mediaIdentities, tracker);
                var canonicalMedia = MediaIdentityResolver.Resolve(mediaIdentities);
                const string creditSql = "SELECT c.provider,c.provider_person_id,c.person_name,c.media_type,c.provider_media_id,c.role,c.role_category,c.role_name FROM provider_media_credit c JOIN current_provider_media m ON m.provider=c.provider AND m.media_type=c.media_type AND m.provider_media_id=c.provider_media_id";
                using (var s = db.PrepareStatement(creditSql)) foreach (var r in s.Rows())
                {
                    var mediaKey = MediaIdentityResolver.RecordKey(r.GetString(0), r.GetString(3), r.GetString(4));
                    if (!providerMedia.TryGetValue(mediaKey, out var media)) continue;
                    var credit = new ObservedProviderCredit
                    {
                        Provider = r.GetString(0), ProviderPersonId = r.GetString(1), PersonName = Null(r, 2), CleanPersonName = TextNormalizer.PersonName(Null(r, 2)),
                        MediaType = r.GetString(3), ProviderMediaId = r.GetString(4), CanonicalMediaKey = canonicalMedia[mediaKey], Role = r.GetString(5),
                        RoleCategory = r.GetString(6), RoleName = Null(r, 7)
                    };
                    input.ProviderCredits.Add(credit);
                }
                using (var s = db.PrepareStatement("SELECT provider_a,provider_id_a,provider_b,provider_id_b,disposition FROM manual_bridge")) foreach (var r in s.Rows()) input.Bridges.Add(new ManualBridge { ProviderA = r.GetString(0), ProviderIdA = r.GetString(1), ProviderB = r.GetString(2), ProviderIdB = r.GetString(3), IsRejected = r.GetString(4) == "reject" });
                using (var s = db.PrepareStatement("SELECT provider,provider_id,outcome,graph_eligible,source,detail FROM acquisition_observation WHERE run_id=@run AND entity_type='person' AND media_type='person'"))
                {
                    s.Bind("@run", acquisitionRunId);
                    foreach (var r in s.Rows()) input.PersonAcquisitions.Add(new PersonAcquisition { Provider = r.GetString(0), ProviderId = r.GetString(1), State = r.GetString(2), GraphEligible = r.GetInt(3) != 0, Source = r.GetString(4), Detail = Null(r, 5) });
                }
                using (var s = db.PrepareStatement("SELECT provider,media_type,provider_id,outcome FROM acquisition_observation WHERE run_id=@run AND entity_type='media'"))
                {
                    s.Bind("@run", acquisitionRunId);
                    foreach (var r in s.Rows()) input.MediaAcquisitions.Add(new MediaAcquisition { Provider = r.GetString(0), MediaType = r.GetString(1), ProviderId = r.GetString(2), State = r.GetString(3) });
                }
                ProviderCorrectionOverlay.Apply(input, tracker);
                foreach (var media in input.Media)
                {
                    media.CanonicalMediaKeys.Clear();
                    AddCanonicalMediaKey(media, canonicalMedia, ProviderNames.Tmdb, media.ProviderAcquisitionId(ProviderNames.Tmdb));
                    AddCanonicalMediaKey(media, canonicalMedia, ProviderNames.Tvdb, media.ProviderAcquisitionId(ProviderNames.Tvdb));
                }
                LoadRelevantGlobalPeople(input, tracker.Rules);
                input.ActiveCorrections.AddRange(tracker.Rules);
                input.CorrectionApplications.AddRange(tracker.Results);
                if (runId.HasValue) SaveCorrectionApplications(runId.Value, tracker.Results);
            }
            return input;
        }

        private void LoadRelevantGlobalPeople(ResolutionInput input, IEnumerable<ProviderCorrection> corrections)
        {
            // Global people are used only as ownership guards for IDs this run could assign.
            // Loading all 300k people made every provider correction proportional to library size.
            var requested = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [ProviderNames.Tmdb] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                [ProviderNames.Tvdb] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                [ProviderNames.Imdb] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };
            Action<string, string> add = (provider, id) =>
            {
                if (!string.IsNullOrWhiteSpace(id) && requested.TryGetValue(provider ?? string.Empty, out var ids)) ids.Add(id);
            };
            foreach (var person in input.ProviderPeople)
            {
                add(person.Provider, person.ProviderId);
                foreach (var external in person.ExternalIds) add(external.Key, external.Value);
            }
            foreach (var person in input.LocalPeople)
            {
                add(ProviderNames.Tmdb, person.TmdbId); add(ProviderNames.Tvdb, person.TvdbId); add(ProviderNames.Imdb, person.ImdbId);
            }

            var rows = new Dictionary<long, LocalPerson>();
            LoadGlobalPeopleByBinding(rows, "tmdb_id", requested[ProviderNames.Tmdb]);
            LoadGlobalPeopleByBinding(rows, "tvdb_id", requested[ProviderNames.Tvdb]);
            LoadGlobalPeopleByBinding(rows, "imdb_id", requested[ProviderNames.Imdb]);
            foreach (var correction in (corrections ?? Enumerable.Empty<ProviderCorrection>()).Where(x => x.Enabled && (x.Kind == CorrectionKinds.IdentityTarget || x.Kind == CorrectionKinds.LocalCreditTarget) && (x.ReplacementValue ?? string.Empty).StartsWith("existing:", StringComparison.OrdinalIgnoreCase)))
            {
                if (!long.TryParse(correction.ReplacementValue.Substring("existing:".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var embyId) || rows.ContainsKey(embyId)) continue;
                using (var s = db.PrepareStatement("SELECT emby_id,name,tmdb_id,tvdb_id,imdb_id FROM global_local_person WHERE emby_id=@id"))
                {
                    s.Bind("@id", embyId);
                    foreach (var r in s.Rows()) rows[r.GetInt64(0)] = GlobalPerson(r);
                }
            }
            input.GlobalLocalPeople.AddRange(rows.Values);
        }

        private void LoadGlobalPeopleByBinding(Dictionary<long, LocalPerson> rows, string column, IEnumerable<string> ids)
        {
            const int chunkSize = 250;
            var values = (ids ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (var offset = 0; offset < values.Count; offset += chunkSize)
            {
                var chunk = values.Skip(offset).Take(chunkSize).ToList();
                var parameters = string.Join(",", chunk.Select((x, i) => "@id" + i.ToString(CultureInfo.InvariantCulture)));
                using (var s = db.PrepareStatement("SELECT emby_id,name,tmdb_id,tvdb_id,imdb_id FROM global_local_person WHERE " + column + " IN (" + parameters + ")"))
                {
                    for (var i = 0; i < chunk.Count; i++) s.Bind("@id" + i.ToString(CultureInfo.InvariantCulture), chunk[i]);
                    foreach (var r in s.Rows()) rows[r.GetInt64(0)] = GlobalPerson(r);
                }
            }
        }

        private static LocalPerson GlobalPerson(IResultSet r) => new LocalPerson { EmbyId = r.GetInt64(0), Name = r.GetString(1), TmdbId = Null(r, 2), TvdbId = Null(r, 3), ImdbId = Null(r, 4) };

        public void SaveDecisions(long runId, IReadOnlyCollection<ResolutionDecision> decisions, IReadOnlyCollection<ResolutionPairEvaluation> pairs = null, IReadOnlyCollection<ResolutionClusterSnapshot> clusters = null, ResolutionInput input = null)
        {
            var plans = input == null ? new List<IdentityCasePlan>() : IdentityCasePlanner.Build(runId, input, decisions, clusters);
            lock (sync) db.RunInTransaction(x => RunBatchedStatements(x, () =>
            {
                Statement(x, "DELETE FROM resolution_decision WHERE run_id=@run", s => s.Bind("@run", runId));
                Statement(x, "DELETE FROM resolution_pair WHERE run_id=@run", s => s.Bind("@run", runId));
                Statement(x, "DELETE FROM resolution_cluster WHERE run_id=@run", s => s.Bind("@run", runId));
                foreach (var decision in decisions)
                {
                    Statement(x, "INSERT OR REPLACE INTO resolution_decision(run_id,decision_id,status,action,display_name,anchor_emby_id,provider_keys,confidence,impact_media_count,headline,explanation,local_anchor_confidence) VALUES(@run,@id,@status,@action,@name,@anchor,@keys,@confidence,@impact,@headline,@explanation,@localConfidence)", s => { s.Bind("@run", runId); s.Bind("@id", Required(decision.DecisionId, "decision-id-missing")); s.Bind("@status", Required(decision.Status, "UNKNOWN")); s.Bind("@action", Required(decision.Action, "HUMAN_REVIEW")); s.Bind("@name", Required(decision.DisplayName, "Unnamed person")); s.Bind("@anchor", decision.AnchorEmbyPersonId); s.Bind("@keys", Required(decision.ProviderKeys, "No current provider person ID")); s.Bind("@confidence", decision.Confidence); s.Bind("@impact", decision.ImpactedMediaCount); s.Bind("@headline", Required(decision.Headline, "Decision summary unavailable")); s.Bind("@explanation", Required(decision.Explanation, "No additional explanation was generated.")); s.Bind("@localConfidence", decision.LocalAnchorConfidence); });
                    foreach (var evidence in decision.Evidence)
                        Statement(x, "INSERT OR REPLACE INTO resolution_evidence VALUES(@run,@decision,@sort,@signal,@verdict,@narrative,@metric)", s => { s.Bind("@run", runId); s.Bind("@decision", Required(decision.DecisionId, "decision-id-missing")); s.Bind("@sort", evidence.SortOrder); s.Bind("@signal", Required(evidence.SignalType, "UNSPECIFIED")); s.Bind("@verdict", Required(evidence.Verdict, "unknown")); s.Bind("@narrative", Required(evidence.Narrative, "No narrative was generated.")); s.Bind("@metric", Required(evidence.Metric, "-")); });
                    foreach (var media in decision.ImpactedMedia)
                        Statement(x, "INSERT OR REPLACE INTO resolution_media VALUES(@run,@decision,@media,@type,@name,@role)", s => { s.Bind("@run", runId); s.Bind("@decision", Required(decision.DecisionId, "decision-id-missing")); s.Bind("@media", media.EmbyMediaId); s.Bind("@type", Required(media.MediaType, "unknown")); s.Bind("@name", Required(media.DisplayName, "Unnamed media")); s.Bind("@role", Required(media.Role, "Unspecified role")); });
                    foreach (var assignment in decision.CreditAssignments ?? new List<ResolutionCreditAssignment>())
                        Statement(x, "INSERT OR REPLACE INTO resolution_credit_assignment VALUES(@run,@decision,@source,@target,@media,@role,@disposition,@component,@rationale)", s => { s.Bind("@run", runId); s.Bind("@decision", Required(decision.DecisionId, "decision-id-missing")); s.Bind("@source", assignment.SourcePersonEmbyId); s.Bind("@target", assignment.TargetPersonEmbyId); s.Bind("@media", assignment.MediaEmbyId); s.Bind("@role", Required(assignment.Role, "Unspecified role")); s.Bind("@disposition", Required(assignment.Disposition, "KEEP")); s.Bind("@component", Required(assignment.ComponentKey, "unknown")); s.Bind("@rationale", Required(assignment.Rationale, "No assignment rationale was generated.")); });
                }
                foreach (var pair in pairs ?? new ResolutionPairEvaluation[0])
                {
                    var score = pair.Score ?? new ScoreBreakdown();
                    Statement(x, "INSERT OR REPLACE INTO resolution_pair VALUES(@run,@pair,@leftProvider,@leftId,@rightProvider,@rightId,@model,@disposition,@confidence)", s => { s.Bind("@run", runId); s.Bind("@pair", pair.PairId); s.Bind("@leftProvider", pair.LeftProvider); s.Bind("@leftId", pair.LeftProviderId); s.Bind("@rightProvider", pair.RightProvider); s.Bind("@rightId", pair.RightProviderId); s.Bind("@model", Required(score.ModelVersion, "unknown")); s.Bind("@disposition", Required(pair.Disposition, "unknown")); s.Bind("@confidence", score.Score); });
                    PairFeature(x, runId, pair.PairId, "shared_media_count", score.SharedMediaCount, null);
                    PairFeature(x, runId, pair.PairId, "positive_evidence_score", score.PositiveEvidenceScore, null);
                    PairFeature(x, runId, pair.PairId, "metadata_conflict_penalty", score.MetadataConflictPenalty, null);
                    PairFeature(x, runId, pair.PairId, "left_media_count", score.LeftMediaCount, null);
                    PairFeature(x, runId, pair.PairId, "right_media_count", score.RightMediaCount, null);
                    PairFeature(x, runId, pair.PairId, "filmography_containment", score.FilmographyContainment, null);
                    PairFeature(x, runId, pair.PairId, "filmography_jaccard", score.FilmographyJaccard, null);
                    PairFeature(x, runId, pair.PairId, "role_agreement", score.RoleAgreement, null);
                    PairFeature(x, runId, pair.PairId, "exact_role_matches", score.ExactRoleMatches, null);
                    PairFeature(x, runId, pair.PairId, "compatible_role_matches", score.CompatibleRoleMatches, null);
                    PairFeature(x, runId, pair.PairId, "episode_credit_matches", score.EpisodeCreditMatches, null);
                    PairFeature(x, runId, pair.PairId, "name_frequency", score.NameFrequency, score.ExactNameMatch ? "exact" : score.AliasMatch ? "alias" : "none");
                    PairFeature(x, runId, pair.PairId, "birthday", null, score.BirthdayState);
                    PairFeature(x, runId, pair.PairId, "birthday_detail", null, score.BirthdayDetail);
                    PairFeature(x, runId, pair.PairId, "external_id", null, score.ExternalIdState);
                    PairFeature(x, runId, pair.PairId, "external_id_matches", null, score.IdentifierMatchDetail);
                    PairFeature(x, runId, pair.PairId, "external_id_conflicts", null, score.IdentifierConflictDetail);
                    PairFeature(x, runId, pair.PairId, "native_provider_crosswalk", score.NativeProviderCrosswalkMatch ? 1 : 0, score.NativeProviderCrosswalkMatch ? "exact" : "missing");
                    PairFeature(x, runId, pair.PairId, "stable_identifier_match", score.StableIdentifierMatch ? 1 : 0, score.StableIdentifierMatch ? "exact" : "missing");
                    PairFeature(x, runId, pair.PairId, "media_attribution_dominant", score.MediaAttributionDominant ? 1 : 0, score.MediaAttributionDominant ? "yes" : "no");
                    PairFeature(x, runId, pair.PairId, "competing_attributions", score.CompetingAttributionCount, null);
                }
                foreach (var cluster in clusters ?? new ResolutionClusterSnapshot[0])
                {
                    Statement(x, "INSERT OR REPLACE INTO resolution_cluster VALUES(@run,@cluster,@anchor,@identity,@local)", s => { s.Bind("@run", runId); s.Bind("@cluster", cluster.ClusterId); s.Bind("@anchor", cluster.AnchorEmbyPersonId); s.Bind("@identity", cluster.IdentityConfidence); s.Bind("@local", cluster.LocalAnchorConfidence); });
                    foreach (var key in cluster.ProviderKeys)
                    {
                        var separator = key.IndexOf(':'); if (separator <= 0) continue;
                        Statement(x, "INSERT OR IGNORE INTO resolution_cluster_member VALUES(@run,@cluster,@provider,@id)", s => { s.Bind("@run", runId); s.Bind("@cluster", cluster.ClusterId); s.Bind("@provider", key.Substring(0, separator)); s.Bind("@id", key.Substring(separator + 1)); });
                    }
                }
                SaveIdentityCasePlans(x, runId, plans);
                Statement(x, "UPDATE resolution_run SET decisions=@count,updated_utc=@now WHERE run_id=@run", s => { s.Bind("@count", decisions.Count); s.Bind("@now", Now()); s.Bind("@run", runId); });
            }), TransactionMode.Immediate);
        }

        private static void SaveIdentityCasePlans(IDatabaseConnection x, long runId, IEnumerable<IdentityCasePlan> plans, bool replaceWholeRun = true)
        {
            var rows = (plans ?? Enumerable.Empty<IdentityCasePlan>()).ToList();
            if (replaceWholeRun) Statement(x, "DELETE FROM resolution_case WHERE run_id=@run", s => s.Bind("@run", runId));
            else foreach (var plan in rows) Statement(x, "DELETE FROM resolution_case WHERE run_id=@run AND case_id=@case", s => { s.Bind("@run", runId); s.Bind("@case", plan.CaseId); });
            foreach (var plan in rows)
            {
                // Emby's SQLite binder represents an empty string as SQL NULL.
                // Keep the schema's intentional empty-text representation for
                // optional fields by coalescing at the statement boundary.
                Statement(x, "INSERT INTO resolution_case(run_id,case_id,plan_hash,display_name,case_type,summary,warning,state,apply_caption,presentation_purpose) VALUES(@run,@case,@hash,@name,@type,@summary,COALESCE(@warning,''),@state,@apply,@purpose)", s =>
                {
                    s.Bind("@run", runId); s.Bind("@case", plan.CaseId); s.Bind("@hash", plan.PlanHash); s.Bind("@name", plan.DisplayName); s.Bind("@type", plan.CaseType);
                    s.Bind("@summary", plan.Summary); s.Bind("@warning", plan.Warning ?? string.Empty); s.Bind("@state", plan.State); s.Bind("@apply", plan.ApplyCaption); s.Bind("@purpose", IdentityCasePlanner.PresentationPurpose(plan));
                });
                for (var i = 0; i < plan.DecisionIds.Count; i++)
                    Statement(x, "INSERT INTO resolution_case_decision VALUES(@run,@case,@decision,@sort)", s => { s.Bind("@run", runId); s.Bind("@case", plan.CaseId); s.Bind("@decision", plan.DecisionIds[i]); s.Bind("@sort", i); });
                foreach (var person in plan.CurrentPeople)
                    Statement(x, "INSERT INTO resolution_case_person_snapshot VALUES(@run,@case,@emby,@name,@tmdb,@tvdb,@imdb)", s => { s.Bind("@run", runId); s.Bind("@case", plan.CaseId); s.Bind("@emby", person.EmbyId); s.Bind("@name", person.Name); s.Bind("@tmdb", person.TmdbId); s.Bind("@tvdb", person.TvdbId); s.Bind("@imdb", person.ImdbId); });
                foreach (var outcome in plan.Outcomes)
                {
                    Statement(x, "INSERT INTO resolution_identity_outcome VALUES(@run,@case,@outcome,@sort,COALESCE(@cluster,''),@kind,@emby,@name,@text)", s =>
                    {
                        s.Bind("@run", runId); s.Bind("@case", plan.CaseId); s.Bind("@outcome", outcome.OutcomeId); s.Bind("@sort", outcome.SortOrder); s.Bind("@cluster", outcome.ClusterKey ?? string.Empty);
                        s.Bind("@kind", outcome.TargetKind); s.Bind("@emby", outcome.TargetEmbyId); s.Bind("@name", outcome.DisplayName); s.Bind("@text", outcome.Outcome);
                    });
                    foreach (var source in outcome.SourceEmbyIds)
                        Statement(x, "INSERT INTO resolution_identity_outcome_source VALUES(@run,@case,@outcome,@source)", s => { s.Bind("@run", runId); s.Bind("@case", plan.CaseId); s.Bind("@outcome", outcome.OutcomeId); s.Bind("@source", source); });
                    foreach (var id in outcome.ProviderIds)
                        Statement(x, "INSERT INTO resolution_identity_outcome_provider_id VALUES(@run,@case,@outcome,@provider,@id,@source)", s => { s.Bind("@run", runId); s.Bind("@case", plan.CaseId); s.Bind("@outcome", outcome.OutcomeId); s.Bind("@provider", id.Provider); s.Bind("@id", id.ProviderId); s.Bind("@source", id.Source); });
                }
                foreach (var credit in plan.Credits)
                {
                    Statement(x, "INSERT INTO resolution_case_credit VALUES(@run,@case,@assignment,@source,@target,@media,@type,@name,@role,@tmdb,@tvdb,@slug,@imdb,@disposition,@rationale,@required)", s =>
                    {
                        s.Bind("@run", runId); s.Bind("@case", plan.CaseId); s.Bind("@assignment", credit.AssignmentId); s.Bind("@source", credit.SourcePersonEmbyId); s.Bind("@target", credit.TargetOutcomeId);
                        s.Bind("@media", credit.MediaEmbyId); s.Bind("@type", credit.MediaType); s.Bind("@name", credit.MediaName); s.Bind("@role", credit.Role); s.Bind("@tmdb", credit.TmdbId); s.Bind("@tvdb", credit.TvdbId); s.Bind("@slug", credit.TvdbSlug); s.Bind("@imdb", credit.ImdbId);
                        s.Bind("@disposition", credit.Disposition); s.Bind("@rationale", credit.Rationale); s.Bind("@required", credit.CorrectionRequired ? 1 : 0);
                    });
                    foreach (var attribution in credit.Attributions)
                        Statement(x, "INSERT INTO resolution_case_credit_attribution VALUES(@run,@case,@assignment,@provider,@media,@person,COALESCE(@name,''),@role,@category,@outcome)", s =>
                        {
                            s.Bind("@run", runId); s.Bind("@case", plan.CaseId); s.Bind("@assignment", credit.AssignmentId); s.Bind("@provider", attribution.Provider);
                            s.Bind("@media", attribution.ProviderMediaId); s.Bind("@person", attribution.ProviderPersonId); s.Bind("@name", attribution.PersonName ?? string.Empty);
                            s.Bind("@role", attribution.Role); s.Bind("@category", attribution.RoleCategory); s.Bind("@outcome", attribution.OutcomeId);
                        });
                }
                foreach (var question in plan.Questions)
                {
                    Statement(x, "INSERT INTO resolution_question VALUES(@run,@case,@question,@kind,@outcome,@assignment,@narrative)", s => { s.Bind("@run", runId); s.Bind("@case", plan.CaseId); s.Bind("@question", question.QuestionId); s.Bind("@kind", question.Kind); s.Bind("@outcome", question.OutcomeId); s.Bind("@assignment", question.AssignmentId); s.Bind("@narrative", question.Narrative); });
                    foreach (var choice in question.Choices)
                    {
                        var c = choice.Correction;
                        Statement(x, "INSERT INTO resolution_question_choice VALUES(@run,@case,@question,@choice,@caption,@effect,@kind,@operation,COALESCE(@provider,''),COALESCE(@mediaType,''),COALESCE(@mediaId,''),COALESCE(@personId,''),COALESCE(@field,''),COALESCE(@current,''),COALESCE(@replacement,''),COALESCE(@secondaryProvider,''),COALESCE(@secondaryId,''),@emby,@reason,COALESCE(@note,''))", s =>
                        {
                            s.Bind("@run", runId); s.Bind("@case", plan.CaseId); s.Bind("@question", question.QuestionId); s.Bind("@choice", choice.ChoiceId); s.Bind("@caption", choice.Caption); s.Bind("@effect", choice.Effect);
                            s.Bind("@kind", c.Kind); s.Bind("@operation", c.Operation); s.Bind("@provider", c.Provider ?? string.Empty); s.Bind("@mediaType", c.MediaType ?? string.Empty); s.Bind("@mediaId", c.ProviderMediaId ?? string.Empty);
                            s.Bind("@personId", c.ProviderPersonId ?? string.Empty); s.Bind("@field", c.FieldName ?? string.Empty); s.Bind("@current", c.CurrentValue ?? string.Empty); s.Bind("@replacement", c.ReplacementValue ?? string.Empty);
                            s.Bind("@secondaryProvider", c.SecondaryProvider ?? string.Empty); s.Bind("@secondaryId", c.SecondaryId ?? string.Empty); s.Bind("@emby", c.EmbyId); s.Bind("@reason", c.Reason); s.Bind("@note", c.Note ?? string.Empty);
                        });
                    }
                }
            }
        }

        public IdentityCasePlan IdentityCase(string caseId)
        {
            if (string.IsNullOrWhiteSpace(caseId)) throw new ArgumentException("The case ID is missing.", nameof(caseId));
            lock (sync)
            {
                var runId = LatestCompletedRunId();
                IdentityCasePlan plan = null;
                using (var s = db.PrepareStatement("SELECT case_id,plan_hash,display_name,case_type,summary,warning,state,apply_caption,presentation_purpose FROM resolution_case WHERE run_id=@run AND case_id=@case"))
                {
                    s.Bind("@run", runId); s.Bind("@case", caseId);
                    foreach (var r in s.Rows()) plan = new IdentityCasePlan { RunId = runId, CaseId = r.GetString(0), PlanHash = r.GetString(1), DisplayName = r.GetString(2), CaseType = r.GetString(3), Summary = r.GetString(4), Warning = r.GetString(5), State = r.GetString(6), ApplyCaption = r.GetString(7), PresentationPurpose = r.GetString(8) };
                }
                if (plan == null) throw new InvalidOperationException("The selected identity case is no longer present in the latest completed run.");
                using (var s = db.PrepareStatement("SELECT 1 FROM identity_case_apply WHERE source_run_id=@run AND case_id=@case AND reviewed_plan_hash=@hash AND status='COMMITTED' LIMIT 1"))
                {
                    s.Bind("@run", runId); s.Bind("@case", caseId); s.Bind("@hash", plan.PlanHash);
                    foreach (var ignored in s.Rows()) { plan.State = IdentityPlanStates.Applied; plan.ApplyCaption = "Already applied"; plan.Summary += " This exact reviewed plan has already been applied."; break; }
                }
                using (var s = db.PrepareStatement("SELECT decision_id FROM resolution_case_decision WHERE run_id=@run AND case_id=@case ORDER BY sort_order,decision_id")) { s.Bind("@run", runId); s.Bind("@case", caseId); foreach (var r in s.Rows()) plan.DecisionIds.Add(r.GetString(0)); }
                using (var s = db.PrepareStatement("SELECT emby_id,name,tmdb_id,tvdb_id,imdb_id FROM resolution_case_person_snapshot WHERE run_id=@run AND case_id=@case ORDER BY emby_id")) { s.Bind("@run", runId); s.Bind("@case", caseId); foreach (var r in s.Rows()) plan.CurrentPeople.Add(new LocalPerson { EmbyId = r.GetInt64(0), Name = r.GetString(1), TmdbId = Null(r, 2), TvdbId = Null(r, 3), ImdbId = Null(r, 4) }); }
                using (var s = db.PrepareStatement("SELECT outcome_id,sort_order,cluster_key,target_kind,target_emby_id,display_name,outcome FROM resolution_identity_outcome WHERE run_id=@run AND case_id=@case ORDER BY sort_order,outcome_id"))
                {
                    s.Bind("@run", runId); s.Bind("@case", caseId);
                    foreach (var r in s.Rows()) plan.Outcomes.Add(new IdentityOutcome { OutcomeId = r.GetString(0), SortOrder = r.GetInt(1), ClusterKey = r.GetString(2), TargetKind = r.GetString(3), TargetEmbyId = r.IsDBNull(4) ? (long?)null : r.GetInt64(4), DisplayName = r.GetString(5), Outcome = r.GetString(6) });
                }
                var outcomes = plan.Outcomes.ToDictionary(x => x.OutcomeId, StringComparer.Ordinal);
                using (var s = db.PrepareStatement("SELECT outcome_id,source_emby_id FROM resolution_identity_outcome_source WHERE run_id=@run AND case_id=@case ORDER BY outcome_id,source_emby_id")) { s.Bind("@run", runId); s.Bind("@case", caseId); foreach (var r in s.Rows()) if (outcomes.TryGetValue(r.GetString(0), out var o)) o.SourceEmbyIds.Add(r.GetInt64(1)); }
                using (var s = db.PrepareStatement("SELECT outcome_id,provider,provider_id,source FROM resolution_identity_outcome_provider_id WHERE run_id=@run AND case_id=@case ORDER BY outcome_id,provider,provider_id")) { s.Bind("@run", runId); s.Bind("@case", caseId); foreach (var r in s.Rows()) if (outcomes.TryGetValue(r.GetString(0), out var o)) o.ProviderIds.Add(new IdentityProviderId { Provider = r.GetString(1), ProviderId = r.GetString(2), Source = r.GetString(3) }); }
                using (var s = db.PrepareStatement("SELECT assignment_id,source_person_emby_id,target_outcome_id,media_emby_id,media_type,media_name,role,tmdb_id,tvdb_id,tvdb_slug,imdb_id,disposition,rationale,correction_required FROM resolution_case_credit WHERE run_id=@run AND case_id=@case ORDER BY media_name,media_emby_id,role,source_person_emby_id"))
                {
                    s.Bind("@run", runId); s.Bind("@case", caseId);
                    foreach (var r in s.Rows()) plan.Credits.Add(new IdentityCreditOutcome { AssignmentId = r.GetString(0), SourcePersonEmbyId = r.GetInt64(1), TargetOutcomeId = r.GetString(2), MediaEmbyId = r.GetInt64(3), MediaType = r.GetString(4), MediaName = r.GetString(5), Role = r.GetString(6), TmdbId = Null(r, 7), TvdbId = Null(r, 8), TvdbSlug = Null(r, 9), ImdbId = Null(r, 10), Disposition = r.GetString(11), Rationale = r.GetString(12), CorrectionRequired = r.GetInt(13) != 0 });
                }
                var credits = plan.Credits.ToDictionary(x => x.AssignmentId, StringComparer.Ordinal);
                using (var s = db.PrepareStatement("SELECT assignment_id,provider,provider_media_id,provider_person_id,person_name,role,role_category,outcome_id FROM resolution_case_credit_attribution WHERE run_id=@run AND case_id=@case ORDER BY assignment_id,provider,provider_person_id,role"))
                {
                    s.Bind("@run", runId); s.Bind("@case", caseId);
                    foreach (var r in s.Rows()) if (credits.TryGetValue(r.GetString(0), out var credit)) credit.Attributions.Add(new IdentityCreditAttribution { Provider = r.GetString(1), ProviderMediaId = r.GetString(2), ProviderPersonId = r.GetString(3), PersonName = r.GetString(4), Role = r.GetString(5), RoleCategory = r.GetString(6), OutcomeId = r.GetString(7) });
                }
                using (var s = db.PrepareStatement("SELECT question_id,kind,outcome_id,assignment_id,narrative FROM resolution_question WHERE run_id=@run AND case_id=@case ORDER BY question_id"))
                {
                    s.Bind("@run", runId); s.Bind("@case", caseId);
                    foreach (var r in s.Rows()) plan.Questions.Add(new IdentityQuestion { QuestionId = r.GetString(0), Kind = r.GetString(1), OutcomeId = Null(r, 2), AssignmentId = Null(r, 3), Narrative = r.GetString(4) });
                }
                var questions = plan.Questions.ToDictionary(x => x.QuestionId, StringComparer.Ordinal);
                using (var s = db.PrepareStatement("SELECT question_id,choice_id,caption,effect,correction_kind,correction_operation,provider,media_type,provider_media_id,provider_person_id,field_name,current_value,replacement_value,secondary_provider,secondary_id,emby_id,reason,note FROM resolution_question_choice WHERE run_id=@run AND case_id=@case ORDER BY question_id,choice_id"))
                {
                    s.Bind("@run", runId); s.Bind("@case", caseId);
                    foreach (var r in s.Rows()) if (questions.TryGetValue(r.GetString(0), out var q)) q.Choices.Add(new IdentityQuestionChoice { ChoiceId = r.GetString(1), Caption = r.GetString(2), Effect = r.GetString(3), Correction = new ProviderCorrection { Kind = r.GetString(4), Operation = r.GetString(5), Provider = r.GetString(6), MediaType = r.GetString(7), ProviderMediaId = r.GetString(8), ProviderPersonId = r.GetString(9), FieldName = r.GetString(10), CurrentValue = r.GetString(11), ReplacementValue = r.GetString(12), SecondaryProvider = r.GetString(13), SecondaryId = r.GetString(14), EmbyId = r.IsDBNull(15) ? (long?)null : r.GetInt64(15), Reason = r.GetString(16), Note = r.GetString(17), Enabled = true } });
                }
                return plan;
            }
        }

        public List<string> AutoApplicableCaseIds()
        {
            var result = new List<string>();
            lock (sync)
            {
                using (var active = db.PrepareStatement("SELECT 1 FROM resolution_run WHERE status='running' LIMIT 1"))
                    foreach (var ignored in active.Rows()) throw new InvalidOperationException("Person evidence is currently being rebuilt. Run Mass Corrections after the evidence task completes.");
                var runId = LatestCompletedRunId();
                if (runId <= 0) return result;
                using (var s = db.PrepareStatement(@"SELECT c.case_id
FROM resolution_case c
WHERE c.run_id=@run AND c.presentation_purpose='SATISFIED_CHANGE'
AND NOT EXISTS(SELECT 1 FROM identity_case_apply a WHERE a.source_run_id=c.run_id AND a.case_id=c.case_id AND a.reviewed_plan_hash=c.plan_hash AND a.status='COMMITTED')
ORDER BY c.case_id"))
                {
                    s.Bind("@run", runId);
                    foreach (var r in s.Rows()) result.Add(r.GetString(0));
                }
            }
            return result;
        }

        public IdentityCasePlan IdentityCaseByReference(string caseId, IEnumerable<long> sourceEmbyIds)
        {
            try { return IdentityCase(caseId); }
            catch (InvalidOperationException)
            {
                var ids = new HashSet<long>(sourceEmbyIds ?? Enumerable.Empty<long>());
                if (ids.Count == 0) throw;
                string replacement = null; var best = -1;
                IdentityCasePlan terminal = null;
                lock (sync)
                {
                    var runId = LatestCompletedRunId();
                    using (var s = db.PrepareStatement("SELECT case_id,emby_id FROM resolution_case_person_snapshot WHERE run_id=@run ORDER BY case_id,emby_id"))
                    {
                        s.Bind("@run", runId);
                        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var r in s.Rows()) if (ids.Contains(r.GetInt64(1))) counts[r.GetString(0)] = counts.TryGetValue(r.GetString(0), out var count) ? count + 1 : 1;
                        foreach (var row in counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal)) if (row.Value > best) { replacement = row.Key; best = row.Value; }
                    }
                    if (replacement == null)
                    {
                        var people = new List<LocalPerson>();
                        foreach (var id in ids.OrderBy(x => x))
                        using (var s = db.PrepareStatement("SELECT emby_id,name,tmdb_id,tvdb_id,imdb_id FROM global_local_person WHERE emby_id=@emby LIMIT 1"))
                        {
                            s.Bind("@emby", id);
                            foreach (var r in s.Rows()) people.Add(new LocalPerson { EmbyId = r.GetInt64(0), Name = r.GetString(1), TmdbId = Null(r, 2), TvdbId = Null(r, 3), ImdbId = Null(r, 4) });
                        }
                        if (people.Count > 0) terminal = ResolvedReferencePlan(runId, caseId, people);
                    }
                }
                if (replacement == null)
                {
                    if (terminal != null) return terminal;
                    throw;
                }
                return IdentityCase(replacement);
            }
        }

        private static IdentityCasePlan ResolvedReferencePlan(long runId, string caseId, IReadOnlyList<LocalPerson> people)
        {
            var displayName = string.Join(" / ", people.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).DefaultIfEmpty("Resolved identity case"));
            var plan = new IdentityCasePlan
            {
                RunId = runId, CaseId = caseId, PlanHash = "resolved-reference:" + runId + ":" + caseId,
                DisplayName = displayName, CaseType = "No longer requires identity review",
                Summary = "The previous case is no longer emitted by the latest evidence run. The current Emby people are shown below; no further change is available from this obsolete case.",
                Warning = "Return to the case list. If a provider correction should produce an Emby ID cleanup, the latest run will expose that as a separate case.",
                State = IdentityPlanStates.Complete, ApplyCaption = "No Emby changes available from this obsolete case"
            };
            foreach (var person in people)
            {
                plan.CurrentPeople.Add(new LocalPerson { EmbyId = person.EmbyId, Name = person.Name, TmdbId = person.TmdbId, TvdbId = person.TvdbId, ImdbId = person.ImdbId });
                var outcome = new IdentityOutcome { OutcomeId = "resolved-existing:" + person.EmbyId, SortOrder = plan.Outcomes.Count, TargetKind = IdentityTargetKinds.Existing, TargetEmbyId = person.EmbyId, DisplayName = person.Name, Outcome = "Current Emby state; the previous identity case has resolved", SourceEmbyIds = new List<long> { person.EmbyId } };
                if (!string.IsNullOrWhiteSpace(person.TmdbId)) outcome.ProviderIds.Add(new IdentityProviderId { Provider = ProviderNames.Tmdb, ProviderId = person.TmdbId, Source = "native" });
                if (!string.IsNullOrWhiteSpace(person.TvdbId)) outcome.ProviderIds.Add(new IdentityProviderId { Provider = ProviderNames.Tvdb, ProviderId = person.TvdbId, Source = "native" });
                if (!string.IsNullOrWhiteSpace(person.ImdbId)) outcome.ProviderIds.Add(new IdentityProviderId { Provider = ProviderNames.Imdb, ProviderId = person.ImdbId, Source = "external" });
                plan.Outcomes.Add(outcome);
            }
            return plan;
        }

        public RoleCorrectionChoice[] RoleCorrectionChoices(string caseId, IdentityCreditOutcome credit)
        {
            if (credit == null) throw new ArgumentNullException(nameof(credit));
            var rows = new List<ObservedProviderCredit>();
            lock (sync)
            {
                const string sql = @"SELECT c.provider,c.media_type,c.provider_media_id,c.provider_person_id,c.role,c.role_category,c.role_name
FROM provider_media_credit c JOIN current_media m ON m.emby_id=@media
WHERE c.media_type=m.media_type AND
 ((c.provider='tmdb' AND c.provider_media_id=coalesce(m.tmdb_acquisition_id,m.tmdb_id)) OR (c.provider='tvdb' AND c.provider_media_id=coalesce(m.tvdb_acquisition_id,m.tvdb_id)))
ORDER BY c.provider,c.provider_person_id,c.role";
                using (var s = db.PrepareStatement(sql))
                {
                    s.Bind("@media", credit.MediaEmbyId);
                    foreach (var r in s.Rows()) rows.Add(new ObservedProviderCredit { Provider = r.GetString(0), MediaType = r.GetString(1), ProviderMediaId = r.GetString(2), ProviderPersonId = r.GetString(3), Role = r.GetString(4), RoleCategory = r.GetString(5), RoleName = Null(r, 6) });
                }
            }
            var involved = new HashSet<string>(IdentityCase(caseId).Outcomes.SelectMany(x => x.ProviderIds.Where(y => y.Source == "native").Select(y => y.Provider + ":" + y.ProviderId)), StringComparer.Ordinal);
            rows = rows.Where(x => involved.Contains(x.Provider + ":" + x.ProviderPersonId)).ToList();
            var roles = rows.Select(x => x.Role).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var result = new List<RoleCorrectionChoice>();
            foreach (var row in rows)
            {
                foreach (var role in roles.Where(x => !string.Equals(x, row.Role, StringComparison.Ordinal)))
                    result.Add(new RoleCorrectionChoice
                    {
                        Caption = "Provider role: " + row.Provider.ToUpperInvariant() + " " + row.ProviderPersonId + " — " + role,
                        Effect = "Replace the stored " + row.Provider.ToUpperInvariant() + " role '" + row.Role + "' with '" + role + "' for this title credit, then recalculate the case.",
                        Correction = new ProviderCorrection { Kind = CorrectionKinds.MediaCreditRole, Operation = CorrectionOperations.Replace, Provider = row.Provider, MediaType = row.MediaType, ProviderMediaId = row.ProviderMediaId, ProviderPersonId = row.ProviderPersonId, CurrentValue = row.Role, ReplacementValue = role, Reason = "OPERATOR_ROLE_ATTRIBUTION", Note = "Operator correction from case " + caseId, Enabled = true }
                    });
                result.Add(new RoleCorrectionChoice
                {
                    Caption = "Provider role: mark " + row.Provider.ToUpperInvariant() + " " + row.ProviderPersonId + " role unusable",
                    Effect = "Ignore this provider role value for resolution and recalculate the case.",
                    Correction = new ProviderCorrection { Kind = CorrectionKinds.MediaCreditRole, Operation = CorrectionOperations.Unusable, Provider = row.Provider, MediaType = row.MediaType, ProviderMediaId = row.ProviderMediaId, ProviderPersonId = row.ProviderPersonId, CurrentValue = row.Role, Reason = "OPERATOR_ROLE_ATTRIBUTION", Note = "Operator correction from case " + caseId, Enabled = true }
                });
            }
            return result.GroupBy(x => x.Caption, StringComparer.Ordinal).Select(x => x.First()).ToArray();
        }

        public void SaveBridge(string providerA, string providerIdA, string providerB, string providerIdB, bool reject)
        {
            providerA = (providerA ?? string.Empty).Trim().ToLowerInvariant(); providerIdA = (providerIdA ?? string.Empty).Trim();
            providerB = (providerB ?? string.Empty).Trim().ToLowerInvariant(); providerIdB = (providerIdB ?? string.Empty).Trim();
            if ((providerA != ProviderNames.Tmdb && providerA != ProviderNames.Tvdb) || (providerB != ProviderNames.Tmdb && providerB != ProviderNames.Tvdb) || providerA == providerB || providerIdA.Length == 0 || providerIdB.Length == 0)
                throw new ArgumentException("Enter one TMDB person ID and one TVDB person ID.");
            if (string.CompareOrdinal(providerA + ":" + providerIdA, providerB + ":" + providerIdB) > 0)
            { var p = providerA; providerA = providerB; providerB = p; var id = providerIdA; providerIdA = providerIdB; providerIdB = id; }
            lock (sync) Statement("INSERT OR REPLACE INTO manual_bridge VALUES(@a,@aid,@b,@bid,@disposition,@now)", s => { s.Bind("@a", providerA); s.Bind("@aid", providerIdA); s.Bind("@b", providerB); s.Bind("@bid", providerIdB); s.Bind("@disposition", reject ? "reject" : "confirm"); s.Bind("@now", Now()); });
        }

        public long SaveCorrection(ProviderCorrection correction)
        {
            if (correction == null) throw new ArgumentNullException(nameof(correction));
            correction.NormalizeAndValidate();
            lock (sync)
            {
                var now = Now();
                if (correction.CorrectionId <= 0)
                {
                    Statement("INSERT INTO provider_correction(kind,operation,provider,media_type,provider_media_id,provider_person_id,field_name,current_value,replacement_value,secondary_provider,secondary_id,emby_id,reason,note,enabled,created_utc,updated_utc) VALUES(@kind,@operation,coalesce(@provider,''),coalesce(@mediaType,''),coalesce(@mediaId,''),coalesce(@personId,''),coalesce(@field,''),coalesce(@current,''),coalesce(@replacement,''),coalesce(@secondaryProvider,''),coalesce(@secondaryId,''),@emby,@reason,coalesce(@note,''),@enabled,@now,@now)", s => BindCorrection(s, correction, now));
                    using (var s = db.PrepareStatement("SELECT last_insert_rowid()")) foreach (var row in s.Rows()) return row.GetInt64(0);
                }
                else
                {
                    Statement("UPDATE provider_correction SET kind=@kind,operation=@operation,provider=coalesce(@provider,''),media_type=coalesce(@mediaType,''),provider_media_id=coalesce(@mediaId,''),provider_person_id=coalesce(@personId,''),field_name=coalesce(@field,''),current_value=coalesce(@current,''),replacement_value=coalesce(@replacement,''),secondary_provider=coalesce(@secondaryProvider,''),secondary_id=coalesce(@secondaryId,''),emby_id=@emby,reason=@reason,note=coalesce(@note,''),enabled=@enabled,updated_utc=@now WHERE correction_id=@id", s => { BindCorrection(s, correction, now); s.Bind("@id", correction.CorrectionId); });
                    return correction.CorrectionId;
                }
            }
            throw new InvalidOperationException("Unable to save the provider correction.");
        }

        public ProviderCorrection GetCorrection(long correctionId)
        {
            lock (sync) using (var s = db.PrepareStatement(CorrectionSelect + " WHERE correction_id=@id"))
            {
                s.Bind("@id", correctionId); foreach (var r in s.Rows()) return ReadCorrection(r);
            }
            return null;
        }

        public CorrectionReviewRow[] Corrections()
        {
            var result = new List<CorrectionReviewRow>();
            lock (sync) using (var s = db.PrepareStatement(@"SELECT c.correction_id,c.kind,c.operation,c.provider,c.media_type,c.provider_media_id,c.provider_person_id,c.field_name,c.current_value,c.replacement_value,c.secondary_provider,c.secondary_id,c.emby_id,c.reason,c.note,c.enabled,c.created_utc,c.updated_utc,a.run_id,a.matched_count,a.changed_count,a.summary,a.applied_utc
FROM provider_correction c
LEFT JOIN correction_application a ON a.correction_id=c.correction_id AND a.run_id=(SELECT max(x.run_id) FROM correction_application x WHERE x.correction_id=c.correction_id)
ORDER BY c.enabled DESC,c.updated_utc DESC,c.correction_id DESC"))
                foreach (var r in s.Rows()) result.Add(new CorrectionReviewRow
                {
                    Correction = ReadCorrection(r), LastRunId = r.IsDBNull(18) ? (long?)null : r.GetInt64(18), LastMatchedCount = r.IsDBNull(19) ? 0 : r.GetInt(19), LastChangedCount = r.IsDBNull(20) ? 0 : r.GetInt(20), LastSummary = Null(r, 21), LastAppliedUtc = r.IsDBNull(22) ? (long?)null : r.GetInt64(22)
                });
            return result.ToArray();
        }

        public void SetCorrectionEnabled(long correctionId, bool enabled)
        {
            lock (sync) Statement("UPDATE provider_correction SET enabled=@enabled,updated_utc=@now WHERE correction_id=@id", s => { s.Bind("@enabled", enabled ? 1 : 0); s.Bind("@now", Now()); s.Bind("@id", correctionId); });
        }

        public void DeleteCorrection(long correctionId)
        {
            lock (sync) Statement("DELETE FROM provider_correction WHERE correction_id=@id", s => s.Bind("@id", correctionId));
        }

        public RunStatus LatestRun()
        {
            lock (sync)
            {
                RunStatus run = null;
                using (var s = db.PrepareStatement("SELECT run_id,status,mode,phase,message,selected_movies,selected_series,selected_episodes,media_fetched,people_fetched,cache_hits,failures,decisions FROM resolution_run ORDER BY run_id DESC LIMIT 1"))
                    foreach (var r in s.Rows()) run = new RunStatus { RunId = r.GetInt64(0), Status = r.GetString(1), Mode = r.GetString(2), Phase = r.GetString(3), Message = r.GetString(4), SelectedMovies = r.GetInt(5), SelectedSeries = r.GetInt(6), SelectedEpisodes = r.GetInt(7), MediaFetched = r.GetInt(8), PeopleFetched = r.GetInt(9), CacheHits = r.GetInt(10), Failures = r.GetInt(11), Decisions = r.GetInt(12) };
                if (run == null) return null;

                using (var s = db.PrepareStatement(@"SELECT count(*),
COALESCE(SUM(CASE WHEN c.presentation_purpose='SATISFIED_CHANGE' AND NOT EXISTS(SELECT 1 FROM identity_case_apply a WHERE a.source_run_id=c.run_id AND a.case_id=c.case_id AND a.reviewed_plan_hash=c.plan_hash AND a.status='COMMITTED') THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN c.presentation_purpose='SATISFIED_CHANGE' AND EXISTS(SELECT 1 FROM identity_case_apply a WHERE a.source_run_id=c.run_id AND a.case_id=c.case_id AND a.reviewed_plan_hash=c.plan_hash AND a.status='COMMITTED') THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN c.presentation_purpose='SATISFIED_NO_CHANGE' THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN c.presentation_purpose='PROBLEM' THEN 1 ELSE 0 END),0)
FROM resolution_case c WHERE c.run_id=@run"))
                {
                    s.Bind("@run", run.RunId);
                    foreach (var r in s.Rows())
                    {
                        run.Cases = r.GetInt(0); run.AutoApplicableCases = r.GetInt(1); run.AppliedCases = r.GetInt(2);
                        run.SatisfiedNoChangeCases = r.GetInt(3); run.ProblemCases = r.GetInt(4);
                    }
                }
                var counts = new List<string>();
                using (var s = db.PrepareStatement("SELECT case_type,count(*) FROM resolution_case WHERE run_id=@run AND presentation_purpose='PROBLEM' GROUP BY case_type ORDER BY case_type"))
                {
                    s.Bind("@run", run.RunId);
                    foreach (var r in s.Rows()) counts.Add(CaseTypeCode(r.GetString(0)) + "=" + r.GetInt(1).ToString(CultureInfo.InvariantCulture));
                }
                run.DecisionBreakdown = counts.Count == 0 ? "none" : string.Join(", ", counts);
                return run;
            }
        }

        private static string CaseTypeCode(string value)
        {
            switch (value)
            {
                case "Blocked by out-of-scope records": return "BLOCKED_BY_OUT_OF_SCOPE";
                case "Provider attribution disagreement": return "CONFLATION";
                case "Credits assigned to the wrong Emby person": return "REALIGNMENT";
                case "Possible combined identities": return "SPLIT";
                case "Emby provider-ID drift": return "DRIFT";
                case "Provider identity missing": return "ORPHAN";
                case "Identity aligned; provider metadata warning": return "MATCH_WITH_CONFLICT";
                case "Provider records agree": return "MATCH";
                default: return (value ?? "UNKNOWN").ToUpperInvariant().Replace(' ', '_').Replace('-', '_');
            }
        }

        public DashboardDecision[] Dashboard(int mediaExamples, EvidenceCaseFilter filter = EvidenceCaseFilter.All)
        {
            var result = new List<DashboardDecision>();
            lock (sync)
            {
                var runId = LatestCompletedRunId();
                if (runId <= 0) return result.ToArray();
                var selected = SelectedCasesSql(filter);
                var cases = new Dictionary<string, DashboardCaseRow>(StringComparer.Ordinal);
                using (var s = db.PrepareStatement(selected + @"
SELECT c.case_id,c.plan_hash,c.display_name,c.case_type,c.summary,c.warning,c.state,c.apply_caption,c.presentation_purpose,
CASE WHEN EXISTS(SELECT 1 FROM identity_case_apply a WHERE a.source_run_id=c.run_id AND a.case_id=c.case_id AND a.reviewed_plan_hash=c.plan_hash AND a.status='COMMITTED') THEN 1 ELSE 0 END,
(SELECT count(DISTINCT m.emby_media_id) FROM resolution_case_decision cd JOIN resolution_media m ON m.run_id=cd.run_id AND m.decision_id=cd.decision_id WHERE cd.run_id=c.run_id AND cd.case_id=c.case_id)
FROM selected_cases c
ORDER BY CASE c.presentation_purpose WHEN 'PROBLEM' THEN 0 WHEN 'SATISFIED_CHANGE' THEN 1 ELSE 2 END,c.case_type,c.display_name,c.case_id"))
                {
                    s.Bind("@run", runId);
                    foreach (var r in s.Rows())
                    {
                        var applied = r.GetInt(9) != 0; var purpose = r.GetString(8); var state = r.GetString(6); var apply = r.GetString(7); var summary = r.GetString(4); var warning = r.GetString(5);
                        var row = new DashboardDecision
                        {
                            CaseId = r.GetString(0), Person = r.GetString(2), Status = r.GetString(3), Decision = summary,
                            Action = applied ? "Applied" : purpose == CasePresentationPurposes.SatisfiedNoChange ? "No Emby changes required" : state == IdentityPlanStates.Complete ? apply : state == IdentityPlanStates.Blocked ? "Blocked — incomplete scope" : "Correction required",
                            Automation = applied ? "Applied" : purpose == CasePresentationPurposes.SatisfiedNoChange ? "No work required" : purpose == CasePresentationPurposes.SatisfiedChange ? "Ready for Mass Corrections" : state == IdentityPlanStates.Blocked ? "Blocked" : "Manual oversight required",
                            AutomationReason = string.IsNullOrWhiteSpace(warning) ? summary : summary + " " + warning,
                            Why = string.IsNullOrWhiteSpace(warning) ? summary : summary + " " + warning,
                            ImpactedTitles = r.GetInt(10), EmbyAnchor = "—", CurrentProviderIds = "No current provider IDs", ProviderIdentities = string.Empty
                        };
                        if (applied) { row.AutomationReason += " This exact persisted plan has already been applied."; row.Why = row.AutomationReason; }
                        var holder = new DashboardCaseRow { Row = row, PlanHash = r.GetString(1), State = state, Purpose = purpose };
                        holder.Details.Add(new DashboardDetail { DetailId = row.CaseId + ":assessment", Section = "Case assessment", Order = 0, Signal = "PRESENTATION_PURPOSE", Verdict = purpose, Explanation = row.AutomationReason, RawMetric = "state=" + state + ";purpose=" + purpose });
                        cases[row.CaseId] = holder; result.Add(row);
                    }
                }
                if (cases.Count == 0) return result.ToArray();

                var decisionCases = new Dictionary<string, DashboardDecisionCase>(StringComparer.Ordinal);
                using (var s = db.PrepareStatement(selected + @"
SELECT cd.case_id,cd.sort_order,d.decision_id,d.status,d.action,d.provider_keys,d.confidence,d.local_anchor_confidence,d.headline,d.explanation
FROM selected_cases c JOIN resolution_case_decision cd ON cd.run_id=c.run_id AND cd.case_id=c.case_id
JOIN resolution_decision d ON d.run_id=cd.run_id AND d.decision_id=cd.decision_id
ORDER BY cd.case_id,cd.sort_order,d.decision_id"))
                {
                    s.Bind("@run", runId);
                    foreach (var r in s.Rows())
                    {
                        var holder = cases[r.GetString(0)]; var decisionId = r.GetString(2); var providerKeys = r.GetString(5); var status = r.GetString(3); var ordinal = holder.DecisionIds.Count + 1;
                        holder.DecisionIds.Add(decisionId); holder.DecisionLabels.Add(RelationshipLabel(providerKeys, status)); holder.Confidence.Add(r.GetDouble(6)); holder.LocalConfidence.Add(r.GetDouble(7));
                        foreach (var key in ProviderKeys(providerKeys)) holder.ProviderKeys.Add(key);
                        var section = "Relationship " + ordinal.ToString(CultureInfo.InvariantCulture) + " — " + providerKeys;
                        holder.Details.Add(new DashboardDetail { DetailId = holder.Row.CaseId + ":relationship:" + ordinal, Section = section, Order = ordinal * 1000, Signal = r.GetString(4), Verdict = status, Explanation = r.GetString(8) + " " + r.GetString(9), RawMetric = decisionId });
                        decisionCases[decisionId] = new DashboardDecisionCase { Case = holder, Section = section, Order = ordinal * 1000 };
                    }
                }

                using (var s = db.PrepareStatement(selected + @"
SELECT e.decision_id,e.sort_order,e.signal_type,e.verdict,e.narrative,e.metric_raw
FROM selected_cases c JOIN resolution_case_decision cd ON cd.run_id=c.run_id AND cd.case_id=c.case_id
JOIN resolution_evidence e ON e.run_id=cd.run_id AND e.decision_id=cd.decision_id
ORDER BY cd.case_id,cd.sort_order,e.sort_order,e.signal_type"))
                {
                    s.Bind("@run", runId);
                    foreach (var r in s.Rows()) if (decisionCases.TryGetValue(r.GetString(0), out var relationship))
                        relationship.Case.Details.Add(new DashboardDetail { DetailId = relationship.Case.Row.CaseId + ":e:" + r.GetString(0) + ":" + r.GetInt(1).ToString(CultureInfo.InvariantCulture) + ":" + r.GetString(2), Section = relationship.Section, Order = relationship.Order + Math.Max(1, r.GetInt(1)), Signal = r.GetString(2), Verdict = r.GetString(3), Explanation = r.GetString(4), RawMetric = r.GetString(5) });
                }

                using (var s = db.PrepareStatement(selected + @",
media_rows AS (
 SELECT DISTINCT cd.case_id,m.emby_media_id,m.media_type,m.display_name,m.role,cm.tmdb_id,cm.tvdb_id,cm.imdb_id,pm.slug
 FROM selected_cases c JOIN resolution_case_decision cd ON cd.run_id=c.run_id AND cd.case_id=c.case_id
 JOIN resolution_media m ON m.run_id=cd.run_id AND m.decision_id=cd.decision_id
 LEFT JOIN current_media cm ON cm.emby_id=m.emby_media_id
 LEFT JOIN provider_media pm ON pm.provider='tvdb' AND pm.media_type=m.media_type AND pm.provider_media_id=coalesce(cm.tvdb_acquisition_id,cm.tvdb_id)
), ranked AS (
 SELECT media_rows.*,ROW_NUMBER() OVER(PARTITION BY case_id ORDER BY media_type,display_name,emby_media_id,role) row_number FROM media_rows
)
SELECT case_id,row_number,media_type,display_name,role,emby_media_id,tmdb_id,tvdb_id,imdb_id,slug
FROM ranked WHERE row_number<=@mediaLimit ORDER BY case_id,row_number"))
                {
                    s.Bind("@run", runId); s.Bind("@mediaLimit", Math.Max(0, mediaExamples));
                    foreach (var r in s.Rows()) if (cases.TryGetValue(r.GetString(0), out var holder))
                        holder.Details.Add(new DashboardDetail { DetailId = holder.Row.CaseId + ":m:" + r.GetInt(1).ToString(CultureInfo.InvariantCulture), Section = "Affected titles", Order = 100000 + r.GetInt(1), Signal = r.GetString(2), Verdict = r.GetString(4), Explanation = r.GetString(3), RawMetric = string.Empty, EmbyMediaId = r.GetInt64(5), MediaType = r.GetString(2), TmdbId = Null(r, 6), TvdbId = Null(r, 7), ImdbId = Null(r, 8), TvdbSlug = Null(r, 9) });
                }

                using (var s = db.PrepareStatement(selected + @"
SELECT p.case_id,p.emby_id,p.tmdb_id,p.tvdb_id,p.imdb_id
FROM selected_cases c JOIN resolution_case_person_snapshot p ON p.run_id=c.run_id AND p.case_id=c.case_id
ORDER BY p.case_id,p.emby_id"))
                {
                    s.Bind("@run", runId);
                    foreach (var r in s.Rows()) if (cases.TryGetValue(r.GetString(0), out var holder))
                    {
                        holder.EmbyIds.Add(r.GetInt64(1).ToString(CultureInfo.InvariantCulture));
                        AddProviderId(holder.CurrentProviderIds, ProviderNames.Tmdb, Null(r, 2)); AddProviderId(holder.CurrentProviderIds, ProviderNames.Tvdb, Null(r, 3)); AddProviderId(holder.CurrentProviderIds, ProviderNames.Imdb, Null(r, 4));
                    }
                }

                foreach (var holder in cases.Values)
                {
                    var row = holder.Row; row.DecisionId = holder.DecisionIds.FirstOrDefault(); row.UnderlyingDecisionIds = holder.DecisionIds.ToArray(); row.UnderlyingDecisionLabels = holder.DecisionLabels.ToArray();
                    row.Relationships = holder.DecisionIds.Count; row.ProviderRecords = holder.ProviderKeys.Count; row.ProviderIdentities = string.Join(", ", holder.ProviderKeys.OrderBy(x => x, StringComparer.Ordinal));
                    row.EmbyAnchor = holder.EmbyIds.Count == 0 ? "—" : string.Join(", ", holder.EmbyIds); row.CurrentProviderIds = holder.CurrentProviderIds.Count == 0 ? "No current provider IDs" : string.Join(", ", holder.CurrentProviderIds.OrderBy(x => x, StringComparer.Ordinal));
                    row.Confidence = PercentRange(holder.Confidence); row.LocalAnchorConfidence = PercentRange(holder.LocalConfidence); row.Details = holder.Details.OrderBy(x => x.Order).ThenBy(x => x.DetailId, StringComparer.Ordinal).ToArray();
                }
            }
            return result.ToArray();
        }

        private static string SelectedCasesSql(EvidenceCaseFilter filter)
        {
            var where = filter == EvidenceCaseFilter.Problem
                ? " AND c.presentation_purpose='PROBLEM'"
                : filter == EvidenceCaseFilter.SatisfiedChange
                    ? " AND c.presentation_purpose='SATISFIED_CHANGE' AND NOT EXISTS(SELECT 1 FROM identity_case_apply a WHERE a.source_run_id=c.run_id AND a.case_id=c.case_id AND a.reviewed_plan_hash=c.plan_hash AND a.status='COMMITTED')"
                    : string.Empty;
            return "WITH selected_cases AS (SELECT c.* FROM resolution_case c WHERE c.run_id=@run" + where + ")";
        }

        private static IEnumerable<string> ProviderKeys(string value) => (value ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Contains(":"));
        private static string RelationshipLabel(string providerKeys, string status)
        {
            var value = providerKeys + " — " + status;
            return value.Length <= 110 ? value : value.Substring(0, 107) + "...";
        }
        private static string PercentRange(IEnumerable<double> source)
        {
            var values = source.Select(x => Math.Round(x * 100, MidpointRounding.AwayFromZero)).Distinct().OrderBy(x => x).ToList();
            return values.Count == 0 ? "—" : values.Count == 1 ? values[0].ToString("0", CultureInfo.InvariantCulture) + "%" : values.First().ToString("0", CultureInfo.InvariantCulture) + "–" + values.Last().ToString("0", CultureInfo.InvariantCulture) + "%";
        }
        private static void AddProviderId(ISet<string> target, string provider, string id) { if (!string.IsNullOrWhiteSpace(id)) target.Add(provider + ":" + id); }

        private DashboardDecision[] LegacyDashboard(int mediaExamples)
        {
            var result = new List<DashboardDecision>();
            lock (sync)
            {
                var latest = 0L; using (var q = db.PrepareStatement("SELECT max(run_id) FROM resolution_run WHERE status='completed'")) foreach (var r in q.Rows()) if (!r.IsDBNull(0)) latest = r.GetInt64(0);
                const string visible = "SELECT decision_id FROM resolution_decision WHERE run_id=@run";
                using (var s = db.PrepareStatement("WITH visible AS (" + visible + ") SELECT d.decision_id,d.status,d.action,d.display_name,d.anchor_emby_id,d.provider_keys,d.confidence,d.impact_media_count,d.headline,d.explanation,d.local_anchor_confidence FROM resolution_decision d JOIN visible v ON v.decision_id=d.decision_id WHERE d.run_id=@run ORDER BY CASE d.status WHEN 'SPLIT' THEN 0 WHEN 'REALIGNMENT' THEN 1 WHEN 'MERGE' THEN 2 WHEN 'CONFLATION' THEN 3 WHEN 'DRIFT' THEN 4 WHEN 'ORPHAN' THEN 5 WHEN 'MATCH_WITH_CONFLICT' THEN 6 ELSE 7 END,d.confidence ASC,d.impact_media_count DESC,d.decision_id"))
                {
                    s.Bind("@run", latest);
                    foreach (var r in s.Rows()) result.Add(new DashboardDecision { DecisionId = r.GetString(0), Status = r.GetString(1), Action = r.GetString(2), Person = r.GetString(3), EmbyAnchor = r.IsDBNull(4) ? "—" : r.GetInt64(4).ToString(CultureInfo.InvariantCulture), ProviderIdentities = r.GetString(5), Confidence = r.GetDouble(6).ToString("P0", CultureInfo.InvariantCulture), ImpactedTitles = r.GetInt(7), Decision = r.GetString(8), Why = r.GetString(9), LocalAnchorConfidence = r.GetDouble(10).ToString("P0", CultureInfo.InvariantCulture) });
                }
                var localPeople = new List<LocalPerson>();
                using (var s = db.PrepareStatement("SELECT emby_id,name,tmdb_id,tvdb_id,imdb_id FROM current_local_person"))
                    foreach (var r in s.Rows()) localPeople.Add(new LocalPerson { EmbyId = r.GetInt64(0), Name = r.GetString(1), TmdbId = Null(r, 2), TvdbId = Null(r, 3), ImdbId = Null(r, 4) });
                var localById = localPeople.GroupBy(x => x.EmbyId).ToDictionary(x => x.Key, x => x.First());
                var localByProviderKey = new Dictionary<string, List<LocalPerson>>(StringComparer.OrdinalIgnoreCase);
                foreach (var person in localPeople)
                foreach (var key in new[] { string.IsNullOrWhiteSpace(person.TmdbId) ? null : ProviderNames.Tmdb + ":" + person.TmdbId, string.IsNullOrWhiteSpace(person.TvdbId) ? null : ProviderNames.Tvdb + ":" + person.TvdbId })
                {
                    if (key == null) continue;
                    if (!localByProviderKey.TryGetValue(key, out var owners)) localByProviderKey[key] = owners = new List<LocalPerson>();
                    owners.Add(person);
                }
                foreach (var decision in result)
                {
                    LocalPerson local = null;
                    if (long.TryParse(decision.EmbyAnchor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var anchor))
                        localById.TryGetValue(anchor, out local);
                    if (local == null)
                    {
                        var keys = new HashSet<string>((decision.ProviderIdentities ?? string.Empty).Split(',').Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
                        var matches = keys.SelectMany(x => localByProviderKey.TryGetValue(x, out var owners) ? owners : Enumerable.Empty<LocalPerson>()).GroupBy(x => x.EmbyId).Select(x => x.First()).ToList();
                        if (matches.Count == 1) { local = matches[0]; decision.EmbyAnchor = local.EmbyId.ToString(CultureInfo.InvariantCulture); }
                    }
                    if (local != null) decision.CurrentProviderIds = ProviderIdText(local);
                }
                var byDecision = result.ToDictionary(x => x.DecisionId, StringComparer.Ordinal);
                using (var s = db.PrepareStatement("WITH visible AS (" + visible + ") SELECT e.decision_id,e.sort_order,e.signal_type,e.verdict,e.narrative,e.metric_raw FROM resolution_evidence e JOIN visible v ON v.decision_id=e.decision_id WHERE e.run_id=@run ORDER BY e.decision_id,e.sort_order,e.signal_type"))
                {
                    s.Bind("@run", latest);
                    foreach (var r in s.Rows()) if (byDecision.TryGetValue(r.GetString(0), out var decision))
                        decision.Details = decision.Details.Concat(new[] { new DashboardDetail { DetailId = r.GetString(0) + ":e:" + r.GetInt(1).ToString(CultureInfo.InvariantCulture) + ":" + r.GetString(2), Section = "Evidence", Order = r.GetInt(1), Signal = r.GetString(2), Verdict = r.GetString(3), Explanation = r.GetString(4), RawMetric = r.GetString(5) } }).ToArray();
                }
                const string mediaSql = "WITH visible AS (" + visible + "), ranked AS (SELECT m.*,ROW_NUMBER() OVER(PARTITION BY m.decision_id ORDER BY m.media_type,m.display_name,m.emby_media_id) AS row_number FROM resolution_media m JOIN visible v ON v.decision_id=m.decision_id WHERE m.run_id=@run) SELECT r.decision_id,r.row_number,r.media_type,r.display_name,r.role,r.emby_media_id,c.tmdb_id,c.tvdb_id,c.imdb_id,t.slug FROM ranked r LEFT JOIN current_media c ON c.emby_id=r.emby_media_id LEFT JOIN provider_media t ON t.provider='tvdb' AND t.media_type=r.media_type AND t.provider_media_id=coalesce(c.tvdb_acquisition_id,c.tvdb_id) WHERE r.row_number<=@mediaLimit ORDER BY r.decision_id,r.row_number";
                using (var s = db.PrepareStatement(mediaSql))
                {
                    s.Bind("@run", latest); s.Bind("@mediaLimit", Math.Max(0, mediaExamples));
                    foreach (var r in s.Rows()) if (byDecision.TryGetValue(r.GetString(0), out var decision))
                        decision.Details = decision.Details.Concat(new[] { new DashboardDetail { DetailId = r.GetString(0) + ":m:" + r.GetInt(1).ToString(CultureInfo.InvariantCulture), Section = "Impacted titles", Order = 10000 + r.GetInt(1), Signal = r.GetString(2), Verdict = r.GetString(4), Explanation = r.GetString(3), RawMetric = string.Empty, EmbyMediaId = r.GetInt64(5), MediaType = r.GetString(2), TmdbId = Null(r, 6), TvdbId = Null(r, 7), ImdbId = Null(r, 8), TvdbSlug = Null(r, 9) } }).ToArray();
                }
            }
            var dashboard = DashboardCaseBuilder.Build(result);
            lock (sync)
            {
                var runId = LatestCompletedRunId();
                var plans = new Dictionary<string, CaseHeader>(StringComparer.Ordinal);
                using (var s = db.PrepareStatement("SELECT case_id,plan_hash,display_name,case_type,summary,warning,state,apply_caption FROM resolution_case WHERE run_id=@run"))
                {
                    s.Bind("@run", runId);
                    foreach (var r in s.Rows()) plans[r.GetString(0)] = new CaseHeader { CaseId = r.GetString(0), PlanHash = r.GetString(1), DisplayName = r.GetString(2), CaseType = r.GetString(3), Summary = r.GetString(4), Warning = r.GetString(5), State = r.GetString(6), ApplyCaption = r.GetString(7) };
                }
                var applied = new HashSet<string>(StringComparer.Ordinal);
                using (var s = db.PrepareStatement("SELECT case_id,reviewed_plan_hash FROM identity_case_apply WHERE source_run_id=@run AND status='COMMITTED'"))
                {
                    s.Bind("@run", runId);
                    foreach (var r in s.Rows()) applied.Add(r.GetString(0) + "\n" + r.GetString(1));
                }
                var caseByDecision = new Dictionary<string, string>(StringComparer.Ordinal);
                using (var s = db.PrepareStatement("SELECT case_id,decision_id FROM resolution_case_decision WHERE run_id=@run")) { s.Bind("@run", runId); foreach (var r in s.Rows()) caseByDecision[r.GetString(1)] = r.GetString(0); }
                foreach (var row in dashboard)
                {
                    var planId = (row.UnderlyingDecisionIds ?? new string[0]).Select(x => caseByDecision.TryGetValue(x, out var id) ? id : null).FirstOrDefault(x => x != null);
                    if (planId == null || !plans.TryGetValue(planId, out var plan)) continue;
                    row.CaseId = plan.CaseId; row.Person = plan.DisplayName; row.Status = plan.CaseType; row.Decision = plan.Summary;
                    var wasApplied = applied.Contains(plan.CaseId + "\n" + plan.PlanHash);
                    var hasMutations = HasMutationCaption(plan.ApplyCaption);
                    var noWork = plan.State == IdentityPlanStates.Complete && !hasMutations;
                    row.Action = wasApplied ? "Applied" : noWork ? "No Emby changes required" : plan.State == IdentityPlanStates.Complete && !hasMutations ? "No Emby changes proposed" : plan.State == IdentityPlanStates.Complete ? plan.ApplyCaption : plan.State == IdentityPlanStates.Blocked ? "Blocked — incomplete scope" : "Correction required";
                    row.Automation = wasApplied ? "Applied" : noWork ? "No work required" : plan.State == IdentityPlanStates.Complete && !hasMutations ? "Review evidence" : plan.State == IdentityPlanStates.Complete ? "Ready to apply" : plan.State == IdentityPlanStates.Blocked ? "Blocked" : "Correction required";
                    row.AutomationReason = string.IsNullOrWhiteSpace(plan.Warning) ? plan.Summary : plan.Summary + " " + plan.Warning;
                    if (wasApplied) row.AutomationReason += " This exact reviewed plan has already been applied.";
                }
            }
            return dashboard;
        }

        public DecisionChangeContext DecisionChangeContext(string decisionId)
        {
            if (string.IsNullOrWhiteSpace(decisionId)) throw new ArgumentException("The decision ID is missing.", nameof(decisionId));
            lock (sync)
            {
                var runId = LatestCompletedRunId();
                ResolutionDecision decision = null;
                using (var s = db.PrepareStatement("SELECT decision_id,status,action,display_name,anchor_emby_id,provider_keys,confidence,impact_media_count,headline,explanation,local_anchor_confidence FROM resolution_decision WHERE run_id=@run AND decision_id=@id"))
                {
                    s.Bind("@run", runId); s.Bind("@id", decisionId);
                    foreach (var r in s.Rows()) decision = new ResolutionDecision
                    {
                        DecisionId = r.GetString(0), Status = r.GetString(1), Action = r.GetString(2), DisplayName = r.GetString(3), AnchorEmbyPersonId = r.IsDBNull(4) ? (long?)null : r.GetInt64(4), ProviderKeys = r.GetString(5), Confidence = r.GetDouble(6), ImpactedMediaCount = r.GetInt(7), Headline = r.GetString(8), Explanation = r.GetString(9), LocalAnchorConfidence = r.GetDouble(10)
                    };
                }
                if (decision == null) throw new InvalidOperationException("The selected decision is no longer present in the latest completed run.");
                var context = new DecisionChangeContext { Decision = decision };
                using (var s = db.PrepareStatement("SELECT sort_order,signal_type,verdict,narrative,metric_raw FROM resolution_evidence WHERE run_id=@run AND decision_id=@decision ORDER BY sort_order,signal_type"))
                {
                    s.Bind("@run", runId); s.Bind("@decision", decisionId);
                    foreach (var r in s.Rows()) decision.Evidence.Add(new EvidenceLine { SortOrder = r.GetInt(0), SignalType = r.GetString(1), Verdict = r.GetString(2), Narrative = r.GetString(3), Metric = r.GetString(4) });
                }
                const string casePeopleSql = @"SELECT DISTINCT p.emby_id,p.name,p.tmdb_id,p.tvdb_id,p.imdb_id
FROM resolution_case_person_snapshot p
JOIN resolution_case_decision d ON d.run_id=p.run_id AND d.case_id=p.case_id
WHERE p.run_id=@run AND d.decision_id=@decision
ORDER BY p.emby_id";
                using (var s = db.PrepareStatement(casePeopleSql))
                {
                    s.Bind("@run", runId); s.Bind("@decision", decisionId);
                    foreach (var r in s.Rows()) context.LocalPeople.Add(new LocalPerson { EmbyId = r.GetInt64(0), Name = r.GetString(1), TmdbId = Null(r, 2), TvdbId = Null(r, 3), ImdbId = Null(r, 4) });
                }
                using (var s = db.PrepareStatement("SELECT source_person_emby_id,target_person_emby_id,media_emby_id,role,disposition,component_key,rationale FROM resolution_credit_assignment WHERE run_id=@run AND decision_id=@decision ORDER BY media_emby_id,role,source_person_emby_id,target_person_emby_id"))
                {
                    s.Bind("@run", runId); s.Bind("@decision", decisionId);
                    foreach (var r in s.Rows()) context.CreditAssignments.Add(new ResolutionCreditAssignment { SourcePersonEmbyId = r.GetInt64(0), TargetPersonEmbyId = r.GetInt64(1), MediaEmbyId = r.GetInt64(2), Role = r.GetString(3), Disposition = r.GetString(4), ComponentKey = r.GetString(5), Rationale = r.GetString(6) });
                }
                using (var s = db.PrepareStatement("SELECT provider,provider_id,outcome,graph_eligible,source,detail FROM acquisition_observation WHERE run_id=@run AND entity_type='person' AND media_type='person'"))
                {
                    s.Bind("@run", runId);
                    foreach (var r in s.Rows()) context.Acquisitions.Add(new PersonAcquisition { Provider = r.GetString(0), ProviderId = r.GetString(1), State = r.GetString(2), GraphEligible = r.GetInt(3) != 0, Source = r.GetString(4), Detail = Null(r, 5) });
                }
                var bindingCandidates = new List<KeyValuePair<string, string>>();
                foreach (var token in (decision.ProviderKeys ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = token.IndexOf(':'); if (separator <= 0 || separator == token.Length - 1) continue;
                    var provider = token.Substring(0, separator).Trim().ToLowerInvariant(); var providerId = token.Substring(separator + 1).Trim();
                    if (provider == ProviderNames.Tmdb || provider == ProviderNames.Tvdb || provider == ProviderNames.Imdb) bindingCandidates.Add(new KeyValuePair<string, string>(provider, providerId));
                    ProviderPerson person = null;
                    using (var s = db.PrepareStatement("SELECT name,clean_name,birthday FROM provider_person WHERE provider=@provider AND provider_person_id=@id"))
                    {
                        s.Bind("@provider", provider); s.Bind("@id", providerId);
                        foreach (var r in s.Rows()) person = new ProviderPerson { Provider = provider, ProviderId = providerId, Name = r.GetString(0), CleanName = r.GetString(1), Birthday = Null(r, 2) };
                    }
                    if (person == null) continue;
                    using (var s = db.PrepareStatement("SELECT external_provider,external_id FROM person_external_id WHERE provider=@provider AND provider_person_id=@id"))
                    {
                        s.Bind("@provider", provider); s.Bind("@id", providerId);
                        foreach (var r in s.Rows())
                        {
                            person.ExternalIds[r.GetString(0)] = r.GetString(1);
                            if (r.GetString(0) == ProviderNames.Tmdb || r.GetString(0) == ProviderNames.Tvdb || r.GetString(0) == ProviderNames.Imdb) bindingCandidates.Add(new KeyValuePair<string, string>(r.GetString(0), r.GetString(1)));
                        }
                    }
                    context.ProposedProviderPeople.Add(person);
                }
                var globalById = new Dictionary<long, LocalPerson>();
                foreach (var binding in bindingCandidates.Where(x => !string.IsNullOrWhiteSpace(x.Value)).GroupBy(x => x.Key + "\n" + x.Value, StringComparer.OrdinalIgnoreCase).Select(x => x.First()))
                {
                    var column = binding.Key == ProviderNames.Tmdb ? "tmdb_id" : binding.Key == ProviderNames.Tvdb ? "tvdb_id" : binding.Key == ProviderNames.Imdb ? "imdb_id" : null;
                    if (column == null) continue;
                    using (var s = db.PrepareStatement("SELECT emby_id,name,tmdb_id,tvdb_id,imdb_id FROM global_local_person WHERE " + column + "=@id"))
                    {
                        s.Bind("@id", binding.Value);
                        foreach (var r in s.Rows()) globalById[r.GetInt64(0)] = new LocalPerson { EmbyId = r.GetInt64(0), Name = r.GetString(1), TmdbId = Null(r, 2), TvdbId = Null(r, 3), ImdbId = Null(r, 4) };
                    }
                }
                context.GlobalLocalPeople.AddRange(globalById.Values);
                return context;
            }
        }

        public void RecordCommittedEmbyChanges(DecisionChangePlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            lock (sync) db.RunInTransaction(x =>
            {
                foreach (var change in plan.Changes)
                {
                    if (change.Kind == EmbyChangeKinds.SetPersonProviderId || change.Kind == EmbyChangeKinds.RemovePersonProviderId)
                    {
                        var column = change.Provider == ProviderNames.Tmdb ? "tmdb_id" : change.Provider == ProviderNames.Tvdb ? "tvdb_id" : change.Provider == ProviderNames.Imdb ? "imdb_id" : null;
                        if (column == null) throw new InvalidOperationException("Unsupported person provider change: " + change.Provider);
                        Statement(x, "UPDATE current_local_person SET " + column + "=@value WHERE emby_id=@id", s => { s.Bind("@value", change.Kind == EmbyChangeKinds.RemovePersonProviderId ? null : change.ProposedValue); s.Bind("@id", change.SourcePersonId); });
                        Statement(x, "UPDATE global_local_person SET " + column + "=@value WHERE emby_id=@id", s => { s.Bind("@value", change.Kind == EmbyChangeKinds.RemovePersonProviderId ? null : change.ProposedValue); s.Bind("@id", change.SourcePersonId); });
                    }
                    else if (change.Kind == EmbyChangeKinds.MoveCredit && change.TargetPersonId.HasValue && change.MediaId.HasValue)
                    {
                        Statement(x, "DELETE FROM current_local_credit WHERE person_emby_id=@source AND media_emby_id=@media AND role=@role", s => { s.Bind("@source", change.SourcePersonId); s.Bind("@media", change.MediaId.Value); s.Bind("@role", change.Role); });
                        Statement(x, "INSERT OR IGNORE INTO current_local_credit(person_emby_id,media_emby_id,role) VALUES(@target,@media,@role)", s => { s.Bind("@target", change.TargetPersonId.Value); s.Bind("@media", change.MediaId.Value); s.Bind("@role", change.Role); });
                    }
                }
            }, TransactionMode.Immediate);
        }

        private static bool HasMutationCaption(string caption)
        {
            return !string.IsNullOrWhiteSpace(caption) && (caption.IndexOf("create ", StringComparison.OrdinalIgnoreCase) >= 0 || caption.IndexOf("move ", StringComparison.OrdinalIgnoreCase) >= 0 || caption.IndexOf("change ", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public long SaveCorrectionChoice(ProviderCorrection correction, long sourceRunId, string caseId, string questionId, string choiceId)
        {
            if (correction == null) throw new ArgumentNullException(nameof(correction));
            if (string.IsNullOrWhiteSpace(caseId) || string.IsNullOrWhiteSpace(questionId) || string.IsNullOrWhiteSpace(choiceId)) throw new ArgumentException("The contextual correction selection is incomplete.");
            var now = Now(); var id = 0L;
            lock (sync)
            {
                correction.Note = CreatedRuleNote(CaseDisplayName(sourceRunId, caseId), now);
                correction.NormalizeAndValidate();
                if (correction.CorrectionId > 0) throw new InvalidOperationException("A contextual correction choice must create a new durable correction.");
                db.RunInTransaction(x =>
                {
                    Statement(x, "INSERT INTO provider_correction(kind,operation,provider,media_type,provider_media_id,provider_person_id,field_name,current_value,replacement_value,secondary_provider,secondary_id,emby_id,reason,note,enabled,created_utc,updated_utc) VALUES(@kind,@operation,coalesce(@provider,''),coalesce(@mediaType,''),coalesce(@mediaId,''),coalesce(@personId,''),coalesce(@field,''),coalesce(@current,''),coalesce(@replacement,''),coalesce(@secondaryProvider,''),coalesce(@secondaryId,''),@emby,@reason,coalesce(@note,''),@enabled,@now,@now)", s => BindCorrection(s, correction, now));
                    using (var s = x.PrepareStatement("SELECT last_insert_rowid()")) foreach (var row in s.Rows()) id = row.GetInt64(0);
                    Statement(x, "INSERT INTO provider_correction_selection VALUES(@id,@run,@case,@question,@choice,@now)", s => { s.Bind("@id", id); s.Bind("@run", sourceRunId); s.Bind("@case", caseId); s.Bind("@question", questionId); s.Bind("@choice", choiceId); s.Bind("@now", now); });
                }, TransactionMode.Immediate);
            }
            correction.CorrectionId = id; return id;
        }

        public int PendingCorrectionSelections(string caseId)
        {
            if (string.IsNullOrWhiteSpace(caseId)) return 0;
            var count = 0;
            lock (sync) using (var s = db.PrepareStatement(@"SELECT count(*)
FROM provider_correction_selection s
JOIN provider_correction c ON c.correction_id=s.correction_id AND c.enabled=1
LEFT JOIN correction_application a ON a.run_id=(SELECT run_id FROM resolution_run WHERE status='completed' ORDER BY run_id DESC LIMIT 1) AND a.correction_id=s.correction_id
WHERE s.case_id=@case AND a.correction_id IS NULL"))
            {
                s.Bind("@case", caseId);
                foreach (var row in s.Rows()) count = row.GetInt(0);
            }
            return count;
        }

        public long CommitIdentityCase(PersonBuilderCompilation compilation, IdentityCaseApplyReceipt receipt)
        {
            if (compilation?.Plan == null) throw new ArgumentNullException(nameof(compilation));
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            if (string.IsNullOrWhiteSpace(compilation.ReviewedPlanHash)) throw new InvalidOperationException("The reviewed evidence hash is missing.");
            var plan = compilation.Plan;
            var selections = (compilation.CorrectionSelections ?? new List<PersonBuilderCorrectionSelection>()).ToList();
            var now = Now();
            var note = CreatedRuleNote(PersonBuilderDisplayName(plan), now);
            foreach (var selection in selections)
            {
                if (string.IsNullOrWhiteSpace(selection?.QuestionId) || string.IsNullOrWhiteSpace(selection.ChoiceId) || selection.Correction == null)
                    throw new InvalidOperationException("An applied correction rule is missing its question or choice provenance.");
                selection.Correction.CorrectionId = 0;
                selection.Correction.Note = note;
                selection.Correction.NormalizeAndValidate();
            }
            var applyId = 0L;
            lock (sync) db.RunInTransaction(x =>
            {
                // Older builds saved the complete person-builder projection as durable corrections.
                // Applying a case replaces those broad layout rules with only the exact contextual
                // choices required to reproduce the operator-confirmed result on future runs.
                Statement(x, @"DELETE FROM provider_correction
WHERE correction_id IN (SELECT correction_id FROM provider_correction_selection WHERE case_id=@case AND question_id='person-builder')
   OR (reason='OPERATOR_PERSON_BUILDER' AND note=@legacy)", s => { s.Bind("@case", plan.CaseId); s.Bind("@legacy", "Person builder case " + plan.CaseId); });
                foreach (var selection in selections)
                {
                    var correction = selection.Correction;
                    Statement(x, "INSERT INTO provider_correction(kind,operation,provider,media_type,provider_media_id,provider_person_id,field_name,current_value,replacement_value,secondary_provider,secondary_id,emby_id,reason,note,enabled,created_utc,updated_utc) VALUES(@kind,@operation,coalesce(@provider,''),coalesce(@mediaType,''),coalesce(@mediaId,''),coalesce(@personId,''),coalesce(@field,''),coalesce(@current,''),coalesce(@replacement,''),coalesce(@secondaryProvider,''),coalesce(@secondaryId,''),@emby,@reason,coalesce(@note,''),@enabled,@now,@now)", s => BindCorrection(s, correction, now));
                    Statement(x, "INSERT INTO provider_correction_selection VALUES(last_insert_rowid(),@run,@case,@question,@choice,@now)", s => { s.Bind("@run", plan.RunId); s.Bind("@case", plan.CaseId); s.Bind("@question", selection.QuestionId); s.Bind("@choice", selection.ChoiceId); s.Bind("@now", now); });
                }
                Statement(x, "INSERT INTO identity_case_apply(source_run_id,case_id,reviewed_plan_hash,started_utc,finished_utc,status,summary) VALUES(@run,@case,@hash,@now,@now,'COMMITTED',@summary)", s => { s.Bind("@run", plan.RunId); s.Bind("@case", plan.CaseId); s.Bind("@hash", compilation.ReviewedPlanHash); s.Bind("@now", now); s.Bind("@summary", receipt.Summary); });
                using (var s = x.PrepareStatement("SELECT last_insert_rowid()")) foreach (var r in s.Rows()) applyId = r.GetInt64(0);
                for (var i = 0; i < receipt.Changes.Count; i++)
                {
                    var change = receipt.Changes[i];
                    Statement(x, "INSERT INTO identity_case_apply_change VALUES(@apply,@order,@kind,@source,@target,@outcome,@media,@role,@provider,@old,@new,@summary)", s => { s.Bind("@apply", applyId); s.Bind("@order", i); s.Bind("@kind", change.Kind); s.Bind("@source", change.SourceEmbyId); s.Bind("@target", change.TargetEmbyId); s.Bind("@outcome", change.OutcomeId); s.Bind("@media", change.MediaEmbyId); s.Bind("@role", change.Role); s.Bind("@provider", change.Provider); s.Bind("@old", change.OldValue); s.Bind("@new", change.NewValue); s.Bind("@summary", change.Summary); });
                }
                foreach (var outcome in plan.Outcomes.Where(o => receipt.OutcomeEmbyIds.ContainsKey(o.OutcomeId)))
                {
                    var id = receipt.OutcomeEmbyIds[outcome.OutcomeId];
                    var tmdb = outcome.ProviderIds.FirstOrDefault(y => y.Provider == ProviderNames.Tmdb)?.ProviderId;
                    var tvdb = outcome.ProviderIds.FirstOrDefault(y => y.Provider == ProviderNames.Tvdb)?.ProviderId;
                    var imdb = outcome.ProviderIds.FirstOrDefault(y => y.Provider == ProviderNames.Imdb)?.ProviderId;
                    Statement(x, "INSERT OR REPLACE INTO current_local_person(emby_id,name,tmdb_id,tvdb_id,imdb_id) VALUES(@id,@name,@tmdb,@tvdb,@imdb)", s => { s.Bind("@id", id); s.Bind("@name", outcome.DisplayName); s.Bind("@tmdb", tmdb); s.Bind("@tvdb", tvdb); s.Bind("@imdb", imdb); });
                    Statement(x, "INSERT OR REPLACE INTO global_local_person(emby_id,name,tmdb_id,tvdb_id,imdb_id) VALUES(@id,@name,@tmdb,@tvdb,@imdb)", s => { s.Bind("@id", id); s.Bind("@name", outcome.DisplayName); s.Bind("@tmdb", tmdb); s.Bind("@tvdb", tvdb); s.Bind("@imdb", imdb); });
                }
                foreach (var credit in plan.Credits.Where(c => receipt.OutcomeEmbyIds.ContainsKey(c.TargetOutcomeId)))
                {
                    var target = receipt.OutcomeEmbyIds[credit.TargetOutcomeId];
                    if (target != credit.SourcePersonEmbyId)
                        Statement(x, "DELETE FROM current_local_credit WHERE person_emby_id=@source AND media_emby_id=@media AND role=@role", s => { s.Bind("@source", credit.SourcePersonEmbyId); s.Bind("@media", credit.MediaEmbyId); s.Bind("@role", credit.Role); });
                    Statement(x, "INSERT OR IGNORE INTO current_local_credit(person_emby_id,media_emby_id,role) VALUES(@target,@media,@role)", s => { s.Bind("@target", target); s.Bind("@media", credit.MediaEmbyId); s.Bind("@role", credit.Role); });
                }
            }, TransactionMode.Immediate);
            return applyId;
        }

        private string CaseDisplayName(long runId, string caseId)
        {
            using (var s = db.PrepareStatement("SELECT display_name FROM resolution_case WHERE run_id=@run AND case_id=@case"))
            {
                s.Bind("@run", runId); s.Bind("@case", caseId);
                foreach (var row in s.Rows()) return DistinctDisplayName(row.GetString(0));
            }
            return "Unknown person";
        }

        private static string PersonBuilderDisplayName(IdentityCasePlan plan)
        {
            var names = plan.Outcomes.Select(x => x.DisplayName).Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return names.Count == 1 ? names[0] : DistinctDisplayName(plan.DisplayName);
        }

        private static string DistinctDisplayName(string displayName)
        {
            var names = (displayName ?? string.Empty).Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return names.Count == 0 ? "Unknown person" : string.Join(" / ", names);
        }

        private static string CreatedRuleNote(string displayName, long createdUtc)
        {
            var created = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(createdUtc);
            return DistinctDisplayName(displayName) + " — " + created.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        }

        private static string ProviderIdText(LocalPerson person)
        {
            var ids = new List<string>();
            if (!string.IsNullOrWhiteSpace(person.TmdbId)) ids.Add(ProviderNames.Tmdb + ":" + person.TmdbId);
            if (!string.IsNullOrWhiteSpace(person.TvdbId)) ids.Add(ProviderNames.Tvdb + ":" + person.TvdbId);
            if (!string.IsNullOrWhiteSpace(person.ImdbId)) ids.Add(ProviderNames.Imdb + ":" + person.ImdbId);
            return ids.Count == 0 ? "No current provider IDs" : string.Join(", ", ids);
        }

        private bool ColumnExists(string table, string column)
        {
            using (var s = db.PrepareStatement("PRAGMA table_info(" + table + ")"))
                foreach (var r in s.Rows()) if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private bool TableExists(string table)
        {
            using (var s = db.PrepareStatement("SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name"))
            {
                s.Bind("@name", table);
                foreach (var ignored in s.Rows()) return true;
            }
            return false;
        }

        private static void SeedMedia(IDatabaseConnection x, long runId, string provider, string type, string id, string routeSeriesId = null, int? routeSeasonNumber = null, int? routeEpisodeNumber = null)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            Statement(x, "INSERT OR IGNORE INTO current_provider_media VALUES(@provider,@type,@id)", s => { s.Bind("@provider", provider); s.Bind("@type", type); s.Bind("@id", id); });
            Statement(x, "INSERT OR REPLACE INTO work_queue(provider,entity_type,media_type,provider_id,priority,status,attempts,error,updated_utc,graph_eligible,route_series_id,route_season_number,route_episode_number) VALUES(@provider,'media',@type,@id,2,'pending',0,NULL,@now,0,@series,@season,@episode)", s => { s.Bind("@provider", provider); s.Bind("@type", type); s.Bind("@id", id); s.Bind("@now", Now()); s.Bind("@series", routeSeriesId); s.Bind("@season", routeSeasonNumber); s.Bind("@episode", routeEpisodeNumber); });
        }
        private static void ApplyLocalMediaQueueCorrections(IDatabaseConnection x, long runId, IEnumerable<MediaSeed> media, IEnumerable<ProviderCorrection> corrections)
        {
            var byId = media.ToDictionary(y => y.EmbyId);
            foreach (var rule in corrections.Where(y => y.Kind == CorrectionKinds.LocalMediaBinding).OrderBy(y => y.CorrectionId))
            {
                if (!rule.EmbyId.HasValue || !byId.TryGetValue(rule.EmbyId.Value, out var item)) continue;
                var currentStable = rule.Provider == ProviderNames.Tmdb ? item.TmdbId : item.TvdbId;
                var currentAcquisition = item.ProviderAcquisitionId(rule.Provider);
                if (!string.IsNullOrWhiteSpace(rule.CurrentValue) && !string.Equals(rule.CurrentValue, currentStable, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(currentAcquisition))
                {
                    Statement(x, "DELETE FROM current_provider_media WHERE provider=@provider AND media_type=@type AND provider_media_id=@id", s => { s.Bind("@provider", rule.Provider); s.Bind("@type", item.MediaType); s.Bind("@id", currentAcquisition); });
                    Statement(x, "DELETE FROM work_queue WHERE provider=@provider AND entity_type='media' AND media_type=@type AND provider_id=@id", s => { s.Bind("@provider", rule.Provider); s.Bind("@type", item.MediaType); s.Bind("@id", currentAcquisition); });
                }
                if (rule.Operation == CorrectionOperations.Replace) SeedMedia(x, runId, rule.Provider, item.MediaType, rule.ReplacementValue, rule.Provider == ProviderNames.Tmdb ? item.ParentTmdbId : item.ParentTvdbId, item.SeasonNumber, item.EpisodeNumber);
            }
        }
        private static void AddCorrectionPerson(ISet<string> target, string provider, string id)
        {
            if ((provider == ProviderNames.Tmdb || provider == ProviderNames.Tvdb) && !string.IsNullOrWhiteSpace(id)) target.Add(provider + ":" + id.Trim());
        }

        private static void AddProviderKey(ISet<string> target, string provider, string id)
        {
            if (!string.IsNullOrWhiteSpace(id)) target.Add(provider + ":" + id.Trim());
        }

        private static void AddCanonicalMediaKey(MediaSeed media, IReadOnlyDictionary<string, string> canonical, string provider, string providerMediaId)
        {
            if (string.IsNullOrWhiteSpace(providerMediaId)) return;
            var recordKey = MediaIdentityResolver.RecordKey(provider, media.MediaType, providerMediaId);
            if (canonical.TryGetValue(recordKey, out var canonicalKey)) media.CanonicalMediaKeys.Add(canonicalKey);
        }

        private static QueueItem ReadQueue(IResultSet r) => new QueueItem { Provider = r.GetString(0), EntityType = r.GetString(1), ProviderId = r.GetString(2), MediaType = r.GetString(3), Priority = r.GetInt(4), GraphEligible = r.GetInt(5) != 0, RouteSeriesId = Null(r, 6), RouteSeasonNumber = r.IsDBNull(7) ? (int?)null : r.GetInt(7), RouteEpisodeNumber = r.IsDBNull(8) ? (int?)null : r.GetInt(8) };
        private static string Dimension(string entityType, string mediaType) => string.Equals(entityType, "person", StringComparison.Ordinal) ? "person" : string.IsNullOrWhiteSpace(mediaType) ? "unknown" : mediaType;
        private static string Required(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
        private static string Null(IResultSet r, int index) => r.IsDBNull(index) ? null : r.GetString(index);
        private static void DeleteForMedia(IDatabaseConnection x, string table, FlattenedMedia media) => Statement(x, "DELETE FROM " + table + " WHERE provider=@provider AND media_type=@type AND provider_media_id=@id", s => { s.Bind("@provider", media.Provider); s.Bind("@type", media.MediaType); s.Bind("@id", media.ProviderMediaId); });
        private static void PairFeature(IDatabaseConnection x, long runId, string pairId, string name, double? numeric, string text)
        {
            Statement(x, "INSERT OR REPLACE INTO resolution_pair_feature VALUES(@run,@pair,@name,@numeric,@text)", s => { s.Bind("@run", runId); s.Bind("@pair", pairId); s.Bind("@name", name); s.Bind("@numeric", numeric); s.Bind("@text", text); });
        }
        private void Statement(string sql, Action<IStatement> bind) => Statement(db, sql, bind);
        private static void Statement(IDatabaseConnection connection, string sql, Action<IStatement> bind)
        {
            if (activeStatementBatch != null && ReferenceEquals(activeStatementBatch.Connection, connection)) { activeStatementBatch.Execute(sql, bind); return; }
            using (var s = connection.PrepareStatement(sql)) { bind(s); s.MoveNext(); }
        }
        private static void RunBatchedStatements(IDatabaseConnection connection, Action action)
        {
            if (activeStatementBatch != null) throw new InvalidOperationException("A SQLite statement batch is already active on this thread.");
            using (var batch = new StatementBatch(connection))
            {
                activeStatementBatch = batch;
                try { action(); }
                finally { activeStatementBatch = null; }
            }
        }
        private sealed class StatementBatch : IDisposable
        {
            private readonly Dictionary<string, IStatement> statements = new Dictionary<string, IStatement>(StringComparer.Ordinal);
            public IDatabaseConnection Connection { get; }
            public StatementBatch(IDatabaseConnection connection) { Connection = connection; }
            public void Execute(string sql, Action<IStatement> bind)
            {
                if (!statements.TryGetValue(sql, out var statement)) statements[sql] = statement = Connection.PrepareStatement(sql);
                try { bind(statement); statement.MoveNext(); }
                finally { statement.Reset(); statement.ClearBindings(); }
            }
            public void Dispose() { foreach (var statement in statements.Values) statement.Dispose(); statements.Clear(); }
        }
        private static void BindCorrection(IStatement s, ProviderCorrection c, long now)
        {
            s.Bind("@kind", c.Kind); s.Bind("@operation", c.Operation); s.Bind("@provider", c.Provider); s.Bind("@mediaType", c.MediaType);
            s.Bind("@mediaId", c.ProviderMediaId); s.Bind("@personId", c.ProviderPersonId); s.Bind("@field", c.FieldName); s.Bind("@current", c.CurrentValue);
            s.Bind("@replacement", c.ReplacementValue); s.Bind("@secondaryProvider", c.SecondaryProvider); s.Bind("@secondaryId", c.SecondaryId); s.Bind("@emby", c.EmbyId);
            s.Bind("@reason", c.Reason); s.Bind("@note", c.Note); s.Bind("@enabled", c.Enabled ? 1 : 0); s.Bind("@now", now);
        }
        private static ProviderCorrection ReadCorrection(IResultSet r) => new ProviderCorrection
        {
            CorrectionId = r.GetInt64(0), Kind = r.GetString(1), Operation = r.GetString(2), Provider = r.GetString(3), MediaType = r.GetString(4), ProviderMediaId = r.GetString(5),
            ProviderPersonId = r.GetString(6), FieldName = r.GetString(7), CurrentValue = r.GetString(8), ReplacementValue = r.GetString(9), SecondaryProvider = r.GetString(10), SecondaryId = r.GetString(11),
            EmbyId = r.IsDBNull(12) ? (long?)null : r.GetInt64(12), Reason = r.GetString(13), Note = r.GetString(14), Enabled = r.GetInt(15) != 0, CreatedUtc = r.GetInt64(16), UpdatedUtc = r.GetInt64(17)
        };
        private List<ProviderCorrection> LoadCorrections()
        {
            var result = new List<ProviderCorrection>();
            using (var s = db.PrepareStatement(CorrectionSelect + " WHERE enabled=1 ORDER BY correction_id")) foreach (var r in s.Rows()) result.Add(ReadCorrection(r));
            return result;
        }
        private void SaveCorrectionApplications(long runId, IEnumerable<CorrectionApplication> applications)
        {
            var rows = applications.ToList(); var now = Now();
            db.RunInTransaction(x =>
            {
                Statement(x, "DELETE FROM correction_application WHERE run_id=@run", s => s.Bind("@run", runId));
                foreach (var app in rows)
                    Statement(x, "INSERT INTO correction_application(run_id,correction_id,matched_count,changed_count,summary,applied_utc) VALUES(@run,@id,@matched,@changed,@summary,@now)", s => { s.Bind("@run", runId); s.Bind("@id", app.CorrectionId); s.Bind("@matched", app.MatchedCount); s.Bind("@changed", app.ChangedCount); s.Bind("@summary", Required(app.Summary, "Correction application result unavailable.")); s.Bind("@now", now); });
            }, TransactionMode.Immediate);
        }
        private const string CorrectionSelect = "SELECT correction_id,kind,operation,provider,media_type,provider_media_id,provider_person_id,field_name,current_value,replacement_value,secondary_provider,secondary_id,emby_id,reason,note,enabled,created_utc,updated_utc FROM provider_correction";
        private long LatestCompletedRunId()
        {
            using (var s = db.PrepareStatement("SELECT max(run_id) FROM resolution_run WHERE status='completed'"))
                foreach (var r in s.Rows()) if (!r.IsDBNull(0)) return r.GetInt64(0);
            return 0;
        }
        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public void Dispose() { lock (sync) { db?.Dispose(); db = null; } }

        private sealed class LocalCreditComparer : IEqualityComparer<LocalCredit>
        {
            public bool Equals(LocalCredit x, LocalCredit y) => x != null && y != null && x.PersonEmbyId == y.PersonEmbyId && x.MediaEmbyId == y.MediaEmbyId && string.Equals(x.Role, y.Role, StringComparison.Ordinal);
            public int GetHashCode(LocalCredit value) => value.PersonEmbyId.GetHashCode() ^ value.MediaEmbyId.GetHashCode() ^ (value.Role ?? string.Empty).GetHashCode();
        }

        private sealed class LocalPersonScope
        {
            private readonly HashSet<string> tmdbIds = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> tvdbIds = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> cleanNames = new HashSet<string>(StringComparer.Ordinal);

            public void Add(string name, string tmdbId, string tvdbId)
            {
                if (!string.IsNullOrWhiteSpace(tmdbId)) tmdbIds.Add(tmdbId);
                if (!string.IsNullOrWhiteSpace(tvdbId)) tvdbIds.Add(tvdbId);
                var cleanName = TextNormalizer.PersonName(name); if (cleanName.Length > 0) cleanNames.Add(cleanName);
            }

            public bool Matches(string provider, string providerId, string name)
            {
                if (provider == ProviderNames.Tmdb && tmdbIds.Contains(providerId)) return true;
                if (provider == ProviderNames.Tvdb && tvdbIds.Contains(providerId)) return true;
                var cleanName = TextNormalizer.PersonName(name); return cleanName.Length > 0 && cleanNames.Contains(cleanName);
            }
        }

        private sealed class CanonicalMediaBuilder
        {
            public string Provider { get; set; }
            public string MediaType { get; set; }
            public string NativeId { get; set; }
            public List<MediaExternalIdentity> ExternalIds { get; } = new List<MediaExternalIdentity>();
            public ProviderMediaIdentity Identity()
            {
                return new ProviderMediaIdentity { Provider = Provider, MediaType = MediaType, ProviderMediaId = NativeId, ExternalIds = ExternalIds };
            }
        }

        private sealed class CaseHeader
        {
            public string CaseId { get; set; }
            public string PlanHash { get; set; }
            public string DisplayName { get; set; }
            public string CaseType { get; set; }
            public string Summary { get; set; }
            public string Warning { get; set; }
            public string State { get; set; }
            public string ApplyCaption { get; set; }
        }

        private sealed class DashboardCaseRow
        {
            public DashboardDecision Row { get; set; }
            public string PlanHash { get; set; }
            public string State { get; set; }
            public string Purpose { get; set; }
            public List<string> DecisionIds { get; } = new List<string>();
            public List<string> DecisionLabels { get; } = new List<string>();
            public HashSet<string> ProviderKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> CurrentProviderIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<string> EmbyIds { get; } = new List<string>();
            public List<double> Confidence { get; } = new List<double>();
            public List<double> LocalConfidence { get; } = new List<double>();
            public List<DashboardDetail> Details { get; } = new List<DashboardDetail>();
        }

        private sealed class DashboardDecisionCase
        {
            public DashboardCaseRow Case { get; set; }
            public string Section { get; set; }
            public int Order { get; set; }
        }

        private static readonly string[] Schema =
        {
            "CREATE TABLE IF NOT EXISTS schema_info(singleton INTEGER PRIMARY KEY CHECK(singleton=1),version INTEGER NOT NULL)",
            "CREATE TABLE IF NOT EXISTS resolution_run(run_id INTEGER PRIMARY KEY AUTOINCREMENT,status TEXT NOT NULL,mode TEXT NOT NULL,phase TEXT NOT NULL,started_utc INTEGER NOT NULL,updated_utc INTEGER NOT NULL,finished_utc INTEGER,message TEXT NOT NULL,selected_movies INTEGER NOT NULL DEFAULT 0,selected_series INTEGER NOT NULL DEFAULT 0,selected_episodes INTEGER NOT NULL DEFAULT 0,media_fetched INTEGER NOT NULL DEFAULT 0,people_fetched INTEGER NOT NULL DEFAULT 0,cache_hits INTEGER NOT NULL DEFAULT 0,failures INTEGER NOT NULL DEFAULT 0,decisions INTEGER NOT NULL DEFAULT 0)",
            "CREATE TABLE IF NOT EXISTS current_media(emby_id INTEGER PRIMARY KEY,media_type TEXT NOT NULL,name TEXT NOT NULL,production_year INTEGER,tmdb_id TEXT,tvdb_id TEXT,imdb_id TEXT,tmdb_acquisition_id TEXT,tvdb_acquisition_id TEXT,parent_emby_id INTEGER,parent_tmdb_id TEXT,parent_tvdb_id TEXT,season_number INTEGER,episode_number INTEGER)",
            "CREATE TABLE IF NOT EXISTS current_local_person(emby_id INTEGER PRIMARY KEY,name TEXT NOT NULL,tmdb_id TEXT,tvdb_id TEXT,imdb_id TEXT)",
            "CREATE TABLE IF NOT EXISTS global_local_person(emby_id INTEGER PRIMARY KEY,name TEXT NOT NULL,tmdb_id TEXT,tvdb_id TEXT,imdb_id TEXT)",
            "CREATE INDEX IF NOT EXISTS idx_current_local_tmdb ON current_local_person(tmdb_id) WHERE tmdb_id IS NOT NULL",
            "CREATE INDEX IF NOT EXISTS idx_current_local_tvdb ON current_local_person(tvdb_id) WHERE tvdb_id IS NOT NULL",
            "CREATE INDEX IF NOT EXISTS idx_current_local_imdb ON current_local_person(imdb_id) WHERE imdb_id IS NOT NULL",
            "CREATE INDEX IF NOT EXISTS idx_global_local_tmdb ON global_local_person(tmdb_id) WHERE tmdb_id IS NOT NULL",
            "CREATE INDEX IF NOT EXISTS idx_global_local_tvdb ON global_local_person(tvdb_id) WHERE tvdb_id IS NOT NULL",
            "CREATE INDEX IF NOT EXISTS idx_global_local_imdb ON global_local_person(imdb_id) WHERE imdb_id IS NOT NULL",
            "CREATE TABLE IF NOT EXISTS current_local_credit(person_emby_id INTEGER NOT NULL,media_emby_id INTEGER NOT NULL,role TEXT NOT NULL,PRIMARY KEY(person_emby_id,media_emby_id,role)) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS current_provider_media(provider TEXT NOT NULL,media_type TEXT NOT NULL,provider_media_id TEXT NOT NULL,PRIMARY KEY(provider,media_type,provider_media_id)) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS work_queue(provider TEXT NOT NULL,entity_type TEXT NOT NULL,media_type TEXT NOT NULL,provider_id TEXT NOT NULL,priority INTEGER NOT NULL,status TEXT NOT NULL,attempts INTEGER NOT NULL,error TEXT,updated_utc INTEGER NOT NULL,graph_eligible INTEGER NOT NULL CHECK(graph_eligible IN(0,1)),route_series_id TEXT,route_season_number INTEGER,route_episode_number INTEGER,PRIMARY KEY(provider,entity_type,media_type,provider_id)) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_work_queue_status ON work_queue(status,entity_type,priority DESC)",
            "CREATE TABLE IF NOT EXISTS cache_manifest(provider TEXT NOT NULL,entity_type TEXT NOT NULL,media_type TEXT NOT NULL,provider_id TEXT NOT NULL,payload_hash TEXT NOT NULL,relative_path TEXT NOT NULL,last_fetched_utc INTEGER NOT NULL,materializer_version INTEGER NOT NULL,PRIMARY KEY(provider,entity_type,media_type,provider_id)) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS fetch_failure(provider TEXT NOT NULL,entity_type TEXT NOT NULL,media_type TEXT NOT NULL,provider_id TEXT NOT NULL,last_failed_utc INTEGER NOT NULL,error TEXT NOT NULL,PRIMARY KEY(provider,entity_type,media_type,provider_id)) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS provider_absence_cache(provider TEXT NOT NULL,entity_type TEXT NOT NULL,media_type TEXT NOT NULL,provider_id TEXT NOT NULL,confirmed_utc INTEGER NOT NULL,status_code INTEGER NOT NULL,PRIMARY KEY(provider,entity_type,media_type,provider_id)) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS acquisition_observation(run_id INTEGER NOT NULL,provider TEXT NOT NULL,entity_type TEXT NOT NULL,media_type TEXT NOT NULL,provider_id TEXT NOT NULL,outcome TEXT NOT NULL CHECK(outcome IN('PRESENT','ABSENT','UNAVAILABLE')),source TEXT NOT NULL,graph_eligible INTEGER NOT NULL CHECK(graph_eligible IN(0,1)),observed_utc INTEGER NOT NULL,detail TEXT,PRIMARY KEY(run_id,provider,entity_type,media_type,provider_id),FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_acquisition_resolution ON acquisition_observation(run_id,entity_type,outcome,graph_eligible,provider,provider_id)",
            "CREATE TABLE IF NOT EXISTS provider_media(provider TEXT NOT NULL,media_type TEXT NOT NULL,provider_media_id TEXT NOT NULL,name TEXT,slug TEXT,updated_utc INTEGER NOT NULL,PRIMARY KEY(provider,media_type,provider_media_id)) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS provider_media_observation(provider TEXT NOT NULL,media_type TEXT NOT NULL,provider_media_id TEXT NOT NULL,payload_hash TEXT NOT NULL,observed_utc INTEGER NOT NULL,endpoint_shape TEXT NOT NULL,credit_scope TEXT NOT NULL,is_complete INTEGER NOT NULL CHECK(is_complete IN(0,1)),materializer_version INTEGER NOT NULL,PRIMARY KEY(provider,media_type,provider_media_id)) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS media_external_id(provider TEXT NOT NULL,media_type TEXT NOT NULL,provider_media_id TEXT NOT NULL,external_provider TEXT NOT NULL,external_id TEXT NOT NULL,PRIMARY KEY(provider,media_type,provider_media_id,external_provider,external_id)) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_media_external_reverse ON media_external_id(media_type,external_provider,external_id)",
            "CREATE TABLE IF NOT EXISTS provider_media_credit(provider TEXT NOT NULL,media_type TEXT NOT NULL,provider_media_id TEXT NOT NULL,provider_person_id TEXT NOT NULL,person_name TEXT,role TEXT NOT NULL,role_category TEXT NOT NULL,role_name TEXT,PRIMARY KEY(provider,media_type,provider_media_id,provider_person_id,role)) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_provider_credit_person ON provider_media_credit(provider,provider_person_id)",
            "CREATE TABLE IF NOT EXISTS provider_person(provider TEXT NOT NULL,provider_person_id TEXT NOT NULL,name TEXT NOT NULL,clean_name TEXT NOT NULL,birthday TEXT,updated_utc INTEGER NOT NULL,PRIMARY KEY(provider,provider_person_id)) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_provider_person_name ON provider_person(clean_name)",
            "CREATE TABLE IF NOT EXISTS person_external_id(provider TEXT NOT NULL,provider_person_id TEXT NOT NULL,external_provider TEXT NOT NULL,external_id TEXT NOT NULL,PRIMARY KEY(provider,provider_person_id,external_provider,external_id)) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_person_external_reverse ON person_external_id(external_provider,external_id)",
            "CREATE TABLE IF NOT EXISTS person_alias(provider TEXT NOT NULL,provider_person_id TEXT NOT NULL,alias TEXT NOT NULL,clean_alias TEXT NOT NULL,PRIMARY KEY(provider,provider_person_id,alias)) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_person_alias_clean ON person_alias(clean_alias)",
            "CREATE TABLE IF NOT EXISTS manual_bridge(provider_a TEXT NOT NULL,provider_id_a TEXT NOT NULL,provider_b TEXT NOT NULL,provider_id_b TEXT NOT NULL,disposition TEXT NOT NULL CHECK(disposition IN('confirm','reject')),created_utc INTEGER NOT NULL,PRIMARY KEY(provider_a,provider_id_a,provider_b,provider_id_b)) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_decision(run_id INTEGER NOT NULL,decision_id TEXT NOT NULL,status TEXT NOT NULL,action TEXT NOT NULL,display_name TEXT NOT NULL,anchor_emby_id INTEGER,provider_keys TEXT NOT NULL,confidence REAL NOT NULL,impact_media_count INTEGER NOT NULL,headline TEXT NOT NULL,explanation TEXT NOT NULL,local_anchor_confidence REAL NOT NULL,PRIMARY KEY(run_id,decision_id),FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_decision_ui ON resolution_decision(run_id,status,confidence,impact_media_count DESC)",
            "CREATE TABLE IF NOT EXISTS resolution_evidence(run_id INTEGER NOT NULL,decision_id TEXT NOT NULL,sort_order INTEGER NOT NULL,signal_type TEXT NOT NULL,verdict TEXT NOT NULL,narrative TEXT NOT NULL,metric_raw TEXT NOT NULL,PRIMARY KEY(run_id,decision_id,sort_order,signal_type),FOREIGN KEY(run_id,decision_id) REFERENCES resolution_decision(run_id,decision_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_media(run_id INTEGER NOT NULL,decision_id TEXT NOT NULL,emby_media_id INTEGER NOT NULL,media_type TEXT NOT NULL,display_name TEXT NOT NULL,role TEXT NOT NULL,PRIMARY KEY(run_id,decision_id,emby_media_id,role),FOREIGN KEY(run_id,decision_id) REFERENCES resolution_decision(run_id,decision_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_credit_assignment(run_id INTEGER NOT NULL,decision_id TEXT NOT NULL,source_person_emby_id INTEGER NOT NULL,target_person_emby_id INTEGER NOT NULL,media_emby_id INTEGER NOT NULL,role TEXT NOT NULL,disposition TEXT NOT NULL CHECK(disposition IN('KEEP','MOVE')),component_key TEXT NOT NULL,rationale TEXT NOT NULL,PRIMARY KEY(run_id,decision_id,source_person_emby_id,target_person_emby_id,media_emby_id,role),FOREIGN KEY(run_id,decision_id) REFERENCES resolution_decision(run_id,decision_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_pair(run_id INTEGER NOT NULL,pair_id TEXT NOT NULL,left_provider TEXT NOT NULL,left_provider_person_id TEXT NOT NULL,right_provider TEXT NOT NULL,right_provider_person_id TEXT NOT NULL,model_version TEXT NOT NULL,disposition TEXT NOT NULL,confidence REAL NOT NULL,PRIMARY KEY(run_id,pair_id),FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_resolution_pair_disposition ON resolution_pair(run_id,disposition,confidence)",
            "CREATE TABLE IF NOT EXISTS resolution_pair_feature(run_id INTEGER NOT NULL,pair_id TEXT NOT NULL,feature_name TEXT NOT NULL,numeric_value REAL,text_value TEXT,PRIMARY KEY(run_id,pair_id,feature_name),FOREIGN KEY(run_id,pair_id) REFERENCES resolution_pair(run_id,pair_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_cluster(run_id INTEGER NOT NULL,cluster_id TEXT NOT NULL,anchor_emby_id INTEGER,identity_confidence REAL NOT NULL,local_anchor_confidence REAL NOT NULL,PRIMARY KEY(run_id,cluster_id),FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_cluster_member(run_id INTEGER NOT NULL,cluster_id TEXT NOT NULL,provider TEXT NOT NULL,provider_person_id TEXT NOT NULL,PRIMARY KEY(run_id,cluster_id,provider,provider_person_id),FOREIGN KEY(run_id,cluster_id) REFERENCES resolution_cluster(run_id,cluster_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS provider_correction(correction_id INTEGER PRIMARY KEY AUTOINCREMENT,kind TEXT NOT NULL,operation TEXT NOT NULL,provider TEXT NOT NULL DEFAULT '',media_type TEXT NOT NULL DEFAULT '',provider_media_id TEXT NOT NULL DEFAULT '',provider_person_id TEXT NOT NULL DEFAULT '',field_name TEXT NOT NULL DEFAULT '',current_value TEXT NOT NULL DEFAULT '',replacement_value TEXT NOT NULL DEFAULT '',secondary_provider TEXT NOT NULL DEFAULT '',secondary_id TEXT NOT NULL DEFAULT '',emby_id INTEGER,reason TEXT NOT NULL,note TEXT NOT NULL DEFAULT '',enabled INTEGER NOT NULL CHECK(enabled IN(0,1)),created_utc INTEGER NOT NULL,updated_utc INTEGER NOT NULL)",
            "CREATE INDEX IF NOT EXISTS idx_provider_correction_enabled ON provider_correction(enabled,kind,provider)",
            "CREATE TABLE IF NOT EXISTS correction_application(run_id INTEGER NOT NULL,correction_id INTEGER NOT NULL,matched_count INTEGER NOT NULL,changed_count INTEGER NOT NULL,summary TEXT NOT NULL,applied_utc INTEGER NOT NULL,PRIMARY KEY(run_id,correction_id),FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE,FOREIGN KEY(correction_id) REFERENCES provider_correction(correction_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_case(run_id INTEGER NOT NULL,case_id TEXT NOT NULL,plan_hash TEXT NOT NULL,display_name TEXT NOT NULL,case_type TEXT NOT NULL,summary TEXT NOT NULL,warning TEXT NOT NULL DEFAULT '',state TEXT NOT NULL CHECK(state IN('COMPLETE','CORRECTION_REQUIRED','BLOCKED')),apply_caption TEXT NOT NULL,presentation_purpose TEXT NOT NULL CHECK(presentation_purpose IN('PROBLEM','SATISFIED_CHANGE','SATISFIED_NO_CHANGE')),PRIMARY KEY(run_id,case_id),FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_resolution_case_ui ON resolution_case(run_id,presentation_purpose,case_type,display_name,case_id)",
            "CREATE TABLE IF NOT EXISTS resolution_case_decision(run_id INTEGER NOT NULL,case_id TEXT NOT NULL,decision_id TEXT NOT NULL,sort_order INTEGER NOT NULL,PRIMARY KEY(run_id,case_id,decision_id),FOREIGN KEY(run_id,case_id) REFERENCES resolution_case(run_id,case_id) ON DELETE CASCADE,FOREIGN KEY(run_id,decision_id) REFERENCES resolution_decision(run_id,decision_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_identity_outcome(run_id INTEGER NOT NULL,case_id TEXT NOT NULL,outcome_id TEXT NOT NULL,sort_order INTEGER NOT NULL,cluster_key TEXT NOT NULL,target_kind TEXT NOT NULL CHECK(target_kind IN('EXISTING','NEW','UNRESOLVED')),target_emby_id INTEGER,display_name TEXT NOT NULL,outcome TEXT NOT NULL,PRIMARY KEY(run_id,case_id,outcome_id),CHECK((target_kind='EXISTING' AND target_emby_id IS NOT NULL) OR (target_kind IN('NEW','UNRESOLVED') AND target_emby_id IS NULL)),FOREIGN KEY(run_id,case_id) REFERENCES resolution_case(run_id,case_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_case_person_snapshot(run_id INTEGER NOT NULL,case_id TEXT NOT NULL,emby_id INTEGER NOT NULL,name TEXT NOT NULL,tmdb_id TEXT,tvdb_id TEXT,imdb_id TEXT,PRIMARY KEY(run_id,case_id,emby_id),FOREIGN KEY(run_id,case_id) REFERENCES resolution_case(run_id,case_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_identity_outcome_source(run_id INTEGER NOT NULL,case_id TEXT NOT NULL,outcome_id TEXT NOT NULL,source_emby_id INTEGER NOT NULL,PRIMARY KEY(run_id,case_id,outcome_id,source_emby_id),FOREIGN KEY(run_id,case_id,outcome_id) REFERENCES resolution_identity_outcome(run_id,case_id,outcome_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_identity_outcome_provider_id(run_id INTEGER NOT NULL,case_id TEXT NOT NULL,outcome_id TEXT NOT NULL,provider TEXT NOT NULL CHECK(provider IN('tmdb','tvdb','imdb')),provider_id TEXT NOT NULL,source TEXT NOT NULL CHECK(source IN('native','external')),PRIMARY KEY(run_id,case_id,outcome_id,provider,provider_id),FOREIGN KEY(run_id,case_id,outcome_id) REFERENCES resolution_identity_outcome(run_id,case_id,outcome_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_case_credit(run_id INTEGER NOT NULL,case_id TEXT NOT NULL,assignment_id TEXT NOT NULL,source_person_emby_id INTEGER NOT NULL,target_outcome_id TEXT NOT NULL,media_emby_id INTEGER NOT NULL,media_type TEXT NOT NULL,media_name TEXT NOT NULL,role TEXT NOT NULL,tmdb_id TEXT,tvdb_id TEXT,tvdb_slug TEXT,imdb_id TEXT,disposition TEXT NOT NULL CHECK(disposition IN('KEEP','MOVE')),rationale TEXT NOT NULL,correction_required INTEGER NOT NULL CHECK(correction_required IN(0,1)),PRIMARY KEY(run_id,case_id,assignment_id),FOREIGN KEY(run_id,case_id,target_outcome_id) REFERENCES resolution_identity_outcome(run_id,case_id,outcome_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_resolution_case_credit_media ON resolution_case_credit(run_id,case_id,media_emby_id)",
            "CREATE TABLE IF NOT EXISTS resolution_case_credit_attribution(run_id INTEGER NOT NULL,case_id TEXT NOT NULL,assignment_id TEXT NOT NULL,provider TEXT NOT NULL CHECK(provider IN('tmdb','tvdb')),provider_media_id TEXT NOT NULL,provider_person_id TEXT NOT NULL,person_name TEXT NOT NULL DEFAULT '',role TEXT NOT NULL,role_category TEXT NOT NULL,outcome_id TEXT NOT NULL,PRIMARY KEY(run_id,case_id,assignment_id,provider,provider_media_id,provider_person_id,role),FOREIGN KEY(run_id,case_id,assignment_id) REFERENCES resolution_case_credit(run_id,case_id,assignment_id) ON DELETE CASCADE,FOREIGN KEY(run_id,case_id,outcome_id) REFERENCES resolution_identity_outcome(run_id,case_id,outcome_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_question(run_id INTEGER NOT NULL,case_id TEXT NOT NULL,question_id TEXT NOT NULL,kind TEXT NOT NULL,outcome_id TEXT,assignment_id TEXT,narrative TEXT NOT NULL,PRIMARY KEY(run_id,case_id,question_id),FOREIGN KEY(run_id,case_id) REFERENCES resolution_case(run_id,case_id) ON DELETE CASCADE,FOREIGN KEY(run_id,case_id,outcome_id) REFERENCES resolution_identity_outcome(run_id,case_id,outcome_id) ON DELETE CASCADE,FOREIGN KEY(run_id,case_id,assignment_id) REFERENCES resolution_case_credit(run_id,case_id,assignment_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_question_choice(run_id INTEGER NOT NULL,case_id TEXT NOT NULL,question_id TEXT NOT NULL,choice_id TEXT NOT NULL,caption TEXT NOT NULL,effect TEXT NOT NULL,correction_kind TEXT NOT NULL,correction_operation TEXT NOT NULL,provider TEXT NOT NULL DEFAULT '',media_type TEXT NOT NULL DEFAULT '',provider_media_id TEXT NOT NULL DEFAULT '',provider_person_id TEXT NOT NULL DEFAULT '',field_name TEXT NOT NULL DEFAULT '',current_value TEXT NOT NULL DEFAULT '',replacement_value TEXT NOT NULL DEFAULT '',secondary_provider TEXT NOT NULL DEFAULT '',secondary_id TEXT NOT NULL DEFAULT '',emby_id INTEGER,reason TEXT NOT NULL,note TEXT NOT NULL DEFAULT '',PRIMARY KEY(run_id,case_id,question_id,choice_id),FOREIGN KEY(run_id,case_id,question_id) REFERENCES resolution_question(run_id,case_id,question_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS identity_case_apply(apply_id INTEGER PRIMARY KEY AUTOINCREMENT,source_run_id INTEGER NOT NULL,case_id TEXT NOT NULL,reviewed_plan_hash TEXT NOT NULL,started_utc INTEGER NOT NULL,finished_utc INTEGER,status TEXT NOT NULL CHECK(status IN('STARTED','COMMITTED','ROLLED_BACK','FAILED')),summary TEXT NOT NULL)",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_identity_case_apply_committed ON identity_case_apply(source_run_id,case_id,reviewed_plan_hash) WHERE status='COMMITTED'",
            "CREATE TABLE IF NOT EXISTS identity_case_apply_change(apply_id INTEGER NOT NULL,change_order INTEGER NOT NULL,change_kind TEXT NOT NULL,source_emby_id INTEGER,target_emby_id INTEGER,outcome_id TEXT,media_emby_id INTEGER,role TEXT,provider TEXT,old_value TEXT,new_value TEXT,summary TEXT NOT NULL,PRIMARY KEY(apply_id,change_order),FOREIGN KEY(apply_id) REFERENCES identity_case_apply(apply_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS provider_correction_selection(correction_id INTEGER PRIMARY KEY,source_run_id INTEGER NOT NULL,case_id TEXT NOT NULL,question_id TEXT NOT NULL,choice_id TEXT NOT NULL,selected_utc INTEGER NOT NULL,FOREIGN KEY(correction_id) REFERENCES provider_correction(correction_id) ON DELETE CASCADE)"
        };
    }
}
