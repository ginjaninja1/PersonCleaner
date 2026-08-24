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
        private const int SchemaVersion = 6;
        private readonly object sync = new object();
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
                if (!ColumnExists("provider_media", "slug") || !ColumnExists("provider_media_credit", "role_category") || !ColumnExists("resolution_decision", "local_anchor_confidence") || !ColumnExists("cache_manifest", "materializer_version") || !ColumnExists("provider_media_observation", "materializer_version") || !ColumnExists("work_queue", "graph_eligible") || !TableExists("resolution_pair") || !TableExists("resolution_cluster") || !TableExists("provider_correction") || !TableExists("correction_application") || !TableExists("provider_absence_cache") || !TableExists("acquisition_observation"))
                {
                    db.Dispose(); db = null;
                    throw new InvalidOperationException("PersonCleaner schema 6 is incomplete. Stop Emby and restore the most recent pre-migration backup before applying the numbered migrations again.");
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
                Statement("INSERT INTO resolution_run(status,mode,phase,started_utc,updated_utc,message) VALUES('running',@mode,'snapshot',@now,@now,'Selecting bounded media sample')", s => { s.Bind("@mode", mode); s.Bind("@now", Now()); });
                using (var s = db.PrepareStatement("SELECT last_insert_rowid()")) foreach (var row in s.Rows()) return row.GetInt64(0);
            }
            throw new InvalidOperationException("Unable to create a PersonCleaner run.");
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
            lock (sync) Statement("UPDATE resolution_run SET status=@status,phase=@phase,message=@message,decisions=@decisions,finished_utc=@now,updated_utc=@now WHERE run_id=@run", s => { s.Bind("@status", status); s.Bind("@phase", status == "completed" ? "complete" : status); s.Bind("@message", message); s.Bind("@decisions", decisions); s.Bind("@now", Now()); s.Bind("@run", runId); });
        }

        public void ReplaceSnapshot(long runId, IReadOnlyCollection<MediaSeed> media, IReadOnlyCollection<LocalPerson> people, IReadOnlyCollection<LocalCredit> credits)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                x.Execute("DELETE FROM current_media"); x.Execute("DELETE FROM current_local_person"); x.Execute("DELETE FROM current_local_credit"); x.Execute("DELETE FROM current_provider_media"); x.Execute("DELETE FROM work_queue");
                foreach (var item in media)
                {
                    Statement(x, "INSERT INTO current_media VALUES(@id,@type,@name,@year,@tmdb,@tvdb,@imdb)", s => { s.Bind("@id", item.EmbyId); s.Bind("@type", item.MediaType); s.Bind("@name", item.Name); s.Bind("@year", item.Year); s.Bind("@tmdb", item.TmdbId); s.Bind("@tvdb", item.TvdbId); s.Bind("@imdb", item.ImdbId); });
                    SeedMedia(x, runId, ProviderNames.Tmdb, item.MediaType, item.TmdbId);
                    SeedMedia(x, runId, ProviderNames.Tvdb, item.MediaType, item.TvdbId);
                }
                ApplyLocalMediaQueueCorrections(x, runId, media, LoadCorrections());
                foreach (var person in people)
                    Statement(x, "INSERT INTO current_local_person VALUES(@id,@name,@tmdb,@tvdb,@imdb)", s => { s.Bind("@id", person.EmbyId); s.Bind("@name", person.Name); s.Bind("@tmdb", person.TmdbId); s.Bind("@tvdb", person.TvdbId); s.Bind("@imdb", person.ImdbId); });
                foreach (var credit in credits.Distinct(new LocalCreditComparer()))
                    Statement(x, "INSERT INTO current_local_credit VALUES(@person,@media,@role)", s => { s.Bind("@person", credit.PersonEmbyId); s.Bind("@media", credit.MediaEmbyId); s.Bind("@role", credit.Role); });
                var movies = media.Count(x => x.MediaType == MediaTypes.Movie); var series = media.Count(x => x.MediaType == MediaTypes.Series);
                Statement(x, "UPDATE resolution_run SET selected_movies=@movies,selected_series=@series,updated_utc=@now WHERE run_id=@run", s => { s.Bind("@movies", movies); s.Bind("@series", series); s.Bind("@now", Now()); s.Bind("@run", runId); });
            }, TransactionMode.Immediate);
        }

        public List<QueueItem> PendingMedia()
        {
            var result = new List<QueueItem>();
            lock (sync) using (var s = db.PrepareStatement("SELECT provider,entity_type,provider_id,media_type,priority,graph_eligible FROM work_queue WHERE status='pending' AND entity_type='media' ORDER BY priority DESC,provider,media_type,provider_id"))
                foreach (var r in s.Rows()) result.Add(ReadQueue(r));
            return result;
        }

        public List<QueueItem> PendingPeople()
        {
            var result = new List<QueueItem>();
            lock (sync) using (var s = db.PrepareStatement("SELECT provider,entity_type,provider_id,media_type,priority,graph_eligible FROM work_queue WHERE status='pending' AND entity_type='person' ORDER BY graph_eligible DESC,priority DESC,provider,provider_id"))
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
                using (var s = db.PrepareStatement(@"SELECT DISTINCT c.provider,c.provider_person_id,c.person_name,m.emby_id
FROM provider_media_credit c
JOIN current_media m ON m.media_type=c.media_type AND ((c.provider='tmdb' AND m.tmdb_id=c.provider_media_id) OR (c.provider='tvdb' AND m.tvdb_id=c.provider_media_id))"))
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
                using (var s = db.PrepareStatement("SELECT emby_id,media_type,name,production_year,tmdb_id,tvdb_id,imdb_id FROM current_media")) foreach (var r in s.Rows()) input.Media.Add(new MediaSeed { EmbyId = r.GetInt64(0), MediaType = r.GetString(1), Name = r.GetString(2), Year = r.IsDBNull(3) ? (int?)null : r.GetInt(3), TmdbId = Null(r, 4), TvdbId = Null(r, 5), ImdbId = Null(r, 6) });
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
                input.CorrectionApplications.AddRange(tracker.Results);
                if (runId.HasValue) SaveCorrectionApplications(runId.Value, tracker.Results);
            }
            return input;
        }

        public void SaveDecisions(long runId, IReadOnlyCollection<ResolutionDecision> decisions, IReadOnlyCollection<ResolutionPairEvaluation> pairs = null, IReadOnlyCollection<ResolutionClusterSnapshot> clusters = null)
        {
            lock (sync) db.RunInTransaction(x =>
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
                }
                foreach (var pair in pairs ?? new ResolutionPairEvaluation[0])
                {
                    var score = pair.Score ?? new ScoreBreakdown();
                    Statement(x, "INSERT OR REPLACE INTO resolution_pair VALUES(@run,@pair,@leftProvider,@leftId,@rightProvider,@rightId,@model,@disposition,@confidence)", s => { s.Bind("@run", runId); s.Bind("@pair", pair.PairId); s.Bind("@leftProvider", pair.LeftProvider); s.Bind("@leftId", pair.LeftProviderId); s.Bind("@rightProvider", pair.RightProvider); s.Bind("@rightId", pair.RightProviderId); s.Bind("@model", Required(score.ModelVersion, "unknown")); s.Bind("@disposition", Required(pair.Disposition, "unknown")); s.Bind("@confidence", score.Score); });
                    PairFeature(x, runId, pair.PairId, "shared_media_count", score.SharedMediaCount, null);
                    PairFeature(x, runId, pair.PairId, "left_media_count", score.LeftMediaCount, null);
                    PairFeature(x, runId, pair.PairId, "right_media_count", score.RightMediaCount, null);
                    PairFeature(x, runId, pair.PairId, "filmography_containment", score.FilmographyContainment, null);
                    PairFeature(x, runId, pair.PairId, "filmography_jaccard", score.FilmographyJaccard, null);
                    PairFeature(x, runId, pair.PairId, "role_agreement", score.RoleAgreement, null);
                    PairFeature(x, runId, pair.PairId, "exact_role_matches", score.ExactRoleMatches, null);
                    PairFeature(x, runId, pair.PairId, "compatible_role_matches", score.CompatibleRoleMatches, null);
                    PairFeature(x, runId, pair.PairId, "name_frequency", score.NameFrequency, score.ExactNameMatch ? "exact" : score.AliasMatch ? "alias" : "none");
                    PairFeature(x, runId, pair.PairId, "birthday", null, score.BirthdayState);
                    PairFeature(x, runId, pair.PairId, "birthday_detail", null, score.BirthdayDetail);
                    PairFeature(x, runId, pair.PairId, "external_id", null, score.ExternalIdState);
                    PairFeature(x, runId, pair.PairId, "external_id_matches", null, score.IdentifierMatchDetail);
                    PairFeature(x, runId, pair.PairId, "external_id_conflicts", null, score.IdentifierConflictDetail);
                    PairFeature(x, runId, pair.PairId, "native_provider_crosswalk", score.NativeProviderCrosswalkMatch ? 1 : 0, score.NativeProviderCrosswalkMatch ? "exact" : "missing");
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
                Statement(x, "UPDATE resolution_run SET decisions=@count,updated_utc=@now WHERE run_id=@run", s => { s.Bind("@count", decisions.Count); s.Bind("@now", Now()); s.Bind("@run", runId); });
            }, TransactionMode.Immediate);
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
                    Statement("INSERT INTO provider_correction(kind,operation,provider,media_type,provider_media_id,provider_person_id,field_name,current_value,replacement_value,secondary_provider,secondary_id,emby_id,reason,note,enabled,created_utc,updated_utc) VALUES(@kind,@operation,@provider,coalesce(@mediaType,''),coalesce(@mediaId,''),coalesce(@personId,''),coalesce(@field,''),coalesce(@current,''),coalesce(@replacement,''),coalesce(@secondaryProvider,''),coalesce(@secondaryId,''),@emby,@reason,coalesce(@note,''),@enabled,@now,@now)", s => BindCorrection(s, correction, now));
                    using (var s = db.PrepareStatement("SELECT last_insert_rowid()")) foreach (var row in s.Rows()) return row.GetInt64(0);
                }
                else
                {
                    Statement("UPDATE provider_correction SET kind=@kind,operation=@operation,provider=@provider,media_type=coalesce(@mediaType,''),provider_media_id=coalesce(@mediaId,''),provider_person_id=coalesce(@personId,''),field_name=coalesce(@field,''),current_value=coalesce(@current,''),replacement_value=coalesce(@replacement,''),secondary_provider=coalesce(@secondaryProvider,''),secondary_id=coalesce(@secondaryId,''),emby_id=@emby,reason=@reason,note=coalesce(@note,''),enabled=@enabled,updated_utc=@now WHERE correction_id=@id", s => { BindCorrection(s, correction, now); s.Bind("@id", correction.CorrectionId); });
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
                using (var s = db.PrepareStatement("SELECT run_id,status,mode,phase,message,selected_movies,selected_series,media_fetched,people_fetched,cache_hits,failures,decisions FROM resolution_run ORDER BY run_id DESC LIMIT 1"))
                    foreach (var r in s.Rows()) run = new RunStatus { RunId = r.GetInt64(0), Status = r.GetString(1), Mode = r.GetString(2), Phase = r.GetString(3), Message = r.GetString(4), SelectedMovies = r.GetInt(5), SelectedSeries = r.GetInt(6), MediaFetched = r.GetInt(7), PeopleFetched = r.GetInt(8), CacheHits = r.GetInt(9), Failures = r.GetInt(10), Decisions = r.GetInt(11) };
                if (run == null) return null;

                var counts = new List<string>();
                using (var s = db.PrepareStatement("SELECT status,count(*) FROM resolution_decision WHERE run_id=@run GROUP BY status ORDER BY CASE status WHEN 'SPLIT' THEN 0 WHEN 'CONFLATION' THEN 1 WHEN 'DRIFT' THEN 2 WHEN 'ORPHAN' THEN 3 WHEN 'MATCH_WITH_CONFLICT' THEN 4 ELSE 5 END,status"))
                {
                    s.Bind("@run", run.RunId);
                    foreach (var r in s.Rows()) counts.Add(r.GetString(0) + "=" + r.GetInt(1).ToString(CultureInfo.InvariantCulture));
                }
                run.DecisionBreakdown = counts.Count == 0 ? "none" : string.Join(", ", counts);
                return run;
            }
        }

        public DashboardDecision[] Dashboard(int maximumRows, int mediaExamples)
        {
            var result = new List<DashboardDecision>();
            lock (sync)
            {
                var latest = 0L; using (var q = db.PrepareStatement("SELECT max(run_id) FROM resolution_run WHERE status='completed'")) foreach (var r in q.Rows()) if (!r.IsDBNull(0)) latest = r.GetInt64(0);
                const string visible = "SELECT decision_id FROM (SELECT decision_id,status,confidence,impact_media_count,ROW_NUMBER() OVER(PARTITION BY status ORDER BY confidence ASC,impact_media_count DESC,decision_id) AS status_row FROM resolution_decision WHERE run_id=@run) WHERE status_row<=@summaryLimit";
                using (var s = db.PrepareStatement("WITH visible AS (" + visible + ") SELECT d.decision_id,d.status,d.action,d.display_name,d.anchor_emby_id,d.provider_keys,d.confidence,d.impact_media_count,d.headline,d.explanation,d.local_anchor_confidence FROM resolution_decision d JOIN visible v ON v.decision_id=d.decision_id WHERE d.run_id=@run ORDER BY CASE d.status WHEN 'SPLIT' THEN 0 WHEN 'CONFLATION' THEN 1 WHEN 'DRIFT' THEN 2 WHEN 'ORPHAN' THEN 3 WHEN 'MATCH_WITH_CONFLICT' THEN 4 ELSE 5 END,d.confidence ASC,d.impact_media_count DESC,d.decision_id"))
                {
                    s.Bind("@run", latest); s.Bind("@summaryLimit", Math.Max(1, maximumRows));
                    foreach (var r in s.Rows()) result.Add(new DashboardDecision { DecisionId = r.GetString(0), Status = r.GetString(1), Action = r.GetString(2), Person = r.GetString(3), EmbyAnchor = r.IsDBNull(4) ? "—" : r.GetInt64(4).ToString(CultureInfo.InvariantCulture), ProviderIdentities = r.GetString(5), Confidence = r.GetDouble(6).ToString("P0", CultureInfo.InvariantCulture), ImpactedTitles = r.GetInt(7), Decision = r.GetString(8), Why = r.GetString(9), LocalAnchorConfidence = r.GetDouble(10).ToString("P0", CultureInfo.InvariantCulture) });
                }
                var localPeople = new List<LocalPerson>();
                using (var s = db.PrepareStatement("SELECT emby_id,name,tmdb_id,tvdb_id,imdb_id FROM current_local_person"))
                    foreach (var r in s.Rows()) localPeople.Add(new LocalPerson { EmbyId = r.GetInt64(0), Name = r.GetString(1), TmdbId = Null(r, 2), TvdbId = Null(r, 3), ImdbId = Null(r, 4) });
                foreach (var decision in result)
                {
                    LocalPerson local = null;
                    if (long.TryParse(decision.EmbyAnchor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var anchor))
                        local = localPeople.FirstOrDefault(x => x.EmbyId == anchor);
                    if (local == null)
                    {
                        var keys = new HashSet<string>((decision.ProviderIdentities ?? string.Empty).Split(',').Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
                        var matches = localPeople.Where(x => (!string.IsNullOrWhiteSpace(x.TmdbId) && keys.Contains(ProviderNames.Tmdb + ":" + x.TmdbId)) || (!string.IsNullOrWhiteSpace(x.TvdbId) && keys.Contains(ProviderNames.Tvdb + ":" + x.TvdbId))).ToList();
                        if (matches.Count == 1) { local = matches[0]; decision.EmbyAnchor = local.EmbyId.ToString(CultureInfo.InvariantCulture); }
                    }
                    if (local != null) decision.CurrentProviderIds = ProviderIdText(local);
                }
                var byDecision = result.ToDictionary(x => x.DecisionId, StringComparer.Ordinal);
                using (var s = db.PrepareStatement("WITH visible AS (" + visible + ") SELECT e.decision_id,e.sort_order,e.signal_type,e.verdict,e.narrative,e.metric_raw FROM resolution_evidence e JOIN visible v ON v.decision_id=e.decision_id WHERE e.run_id=@run ORDER BY e.decision_id,e.sort_order,e.signal_type"))
                {
                    s.Bind("@run", latest); s.Bind("@summaryLimit", Math.Max(1, maximumRows));
                    foreach (var r in s.Rows()) if (byDecision.TryGetValue(r.GetString(0), out var decision))
                        decision.Details = decision.Details.Concat(new[] { new DashboardDetail { DetailId = r.GetString(0) + ":e:" + r.GetInt(1).ToString(CultureInfo.InvariantCulture) + ":" + r.GetString(2), Section = "Evidence", Order = r.GetInt(1), Signal = r.GetString(2), Verdict = r.GetString(3), Explanation = r.GetString(4), RawMetric = r.GetString(5) } }).ToArray();
                }
                const string mediaSql = "WITH visible AS (" + visible + "), ranked AS (SELECT m.*,ROW_NUMBER() OVER(PARTITION BY m.decision_id ORDER BY m.media_type,m.display_name,m.emby_media_id) AS row_number FROM resolution_media m JOIN visible v ON v.decision_id=m.decision_id WHERE m.run_id=@run) SELECT r.decision_id,r.row_number,r.media_type,r.display_name,r.role,r.emby_media_id,c.tmdb_id,c.tvdb_id,c.imdb_id,t.slug FROM ranked r LEFT JOIN current_media c ON c.emby_id=r.emby_media_id LEFT JOIN provider_media t ON t.provider='tvdb' AND t.media_type=r.media_type AND t.provider_media_id=c.tvdb_id WHERE r.row_number<=@mediaLimit ORDER BY r.decision_id,r.row_number";
                using (var s = db.PrepareStatement(mediaSql))
                {
                    s.Bind("@run", latest); s.Bind("@summaryLimit", Math.Max(1, maximumRows)); s.Bind("@mediaLimit", Math.Max(0, mediaExamples));
                    foreach (var r in s.Rows()) if (byDecision.TryGetValue(r.GetString(0), out var decision))
                        decision.Details = decision.Details.Concat(new[] { new DashboardDetail { DetailId = r.GetString(0) + ":m:" + r.GetInt(1).ToString(CultureInfo.InvariantCulture), Section = "Impacted titles", Order = 10000 + r.GetInt(1), Signal = r.GetString(2), Verdict = r.GetString(4), Explanation = r.GetString(3), RawMetric = string.Empty, EmbyMediaId = r.GetInt64(5), MediaType = r.GetString(2), TmdbId = Null(r, 6), TvdbId = Null(r, 7), ImdbId = Null(r, 8), TvdbSlug = Null(r, 9) } }).ToArray();
                }
            }
            return result.ToArray();
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

        private static void SeedMedia(IDatabaseConnection x, long runId, string provider, string type, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            Statement(x, "INSERT OR IGNORE INTO current_provider_media VALUES(@provider,@type,@id)", s => { s.Bind("@provider", provider); s.Bind("@type", type); s.Bind("@id", id); });
            Statement(x, "INSERT OR REPLACE INTO work_queue(provider,entity_type,media_type,provider_id,priority,status,attempts,error,updated_utc,graph_eligible) VALUES(@provider,'media',@type,@id,2,'pending',0,NULL,@now,0)", s => { s.Bind("@provider", provider); s.Bind("@type", type); s.Bind("@id", id); s.Bind("@now", Now()); });
        }
        private static void ApplyLocalMediaQueueCorrections(IDatabaseConnection x, long runId, IEnumerable<MediaSeed> media, IEnumerable<ProviderCorrection> corrections)
        {
            var byId = media.ToDictionary(y => y.EmbyId);
            foreach (var rule in corrections.Where(y => y.Kind == CorrectionKinds.LocalMediaBinding).OrderBy(y => y.CorrectionId))
            {
                if (!rule.EmbyId.HasValue || !byId.TryGetValue(rule.EmbyId.Value, out var item)) continue;
                var current = rule.Provider == ProviderNames.Tmdb ? item.TmdbId : item.TvdbId;
                if (!string.IsNullOrWhiteSpace(rule.CurrentValue) && !string.Equals(rule.CurrentValue, current, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(current))
                {
                    Statement(x, "DELETE FROM current_provider_media WHERE provider=@provider AND media_type=@type AND provider_media_id=@id", s => { s.Bind("@provider", rule.Provider); s.Bind("@type", item.MediaType); s.Bind("@id", current); });
                    Statement(x, "DELETE FROM work_queue WHERE provider=@provider AND entity_type='media' AND media_type=@type AND provider_id=@id", s => { s.Bind("@provider", rule.Provider); s.Bind("@type", item.MediaType); s.Bind("@id", current); });
                }
                if (rule.Operation == CorrectionOperations.Replace) SeedMedia(x, runId, rule.Provider, item.MediaType, rule.ReplacementValue);
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

        private static QueueItem ReadQueue(IResultSet r) => new QueueItem { Provider = r.GetString(0), EntityType = r.GetString(1), ProviderId = r.GetString(2), MediaType = r.GetString(3), Priority = r.GetInt(4), GraphEligible = r.GetInt(5) != 0 };
        private static string Dimension(string entityType, string mediaType) => string.Equals(entityType, "person", StringComparison.Ordinal) ? "person" : string.IsNullOrWhiteSpace(mediaType) ? "unknown" : mediaType;
        private static string Required(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
        private static string Null(IResultSet r, int index) => r.IsDBNull(index) ? null : r.GetString(index);
        private static void DeleteForMedia(IDatabaseConnection x, string table, FlattenedMedia media) => Statement(x, "DELETE FROM " + table + " WHERE provider=@provider AND media_type=@type AND provider_media_id=@id", s => { s.Bind("@provider", media.Provider); s.Bind("@type", media.MediaType); s.Bind("@id", media.ProviderMediaId); });
        private static void PairFeature(IDatabaseConnection x, long runId, string pairId, string name, double? numeric, string text)
        {
            Statement(x, "INSERT OR REPLACE INTO resolution_pair_feature VALUES(@run,@pair,@name,@numeric,@text)", s => { s.Bind("@run", runId); s.Bind("@pair", pairId); s.Bind("@name", name); s.Bind("@numeric", numeric); s.Bind("@text", text); });
        }
        private void Statement(string sql, Action<IStatement> bind) => Statement(db, sql, bind);
        private static void Statement(IDatabaseConnection connection, string sql, Action<IStatement> bind) { using (var s = connection.PrepareStatement(sql)) { bind(s); s.MoveNext(); } }
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

        private static readonly string[] Schema =
        {
            "CREATE TABLE IF NOT EXISTS schema_info(singleton INTEGER PRIMARY KEY CHECK(singleton=1),version INTEGER NOT NULL)",
            "CREATE TABLE IF NOT EXISTS resolution_run(run_id INTEGER PRIMARY KEY AUTOINCREMENT,status TEXT NOT NULL,mode TEXT NOT NULL,phase TEXT NOT NULL,started_utc INTEGER NOT NULL,updated_utc INTEGER NOT NULL,finished_utc INTEGER,message TEXT NOT NULL,selected_movies INTEGER NOT NULL DEFAULT 0,selected_series INTEGER NOT NULL DEFAULT 0,media_fetched INTEGER NOT NULL DEFAULT 0,people_fetched INTEGER NOT NULL DEFAULT 0,cache_hits INTEGER NOT NULL DEFAULT 0,failures INTEGER NOT NULL DEFAULT 0,decisions INTEGER NOT NULL DEFAULT 0)",
            "CREATE TABLE IF NOT EXISTS current_media(emby_id INTEGER PRIMARY KEY,media_type TEXT NOT NULL,name TEXT NOT NULL,production_year INTEGER,tmdb_id TEXT,tvdb_id TEXT,imdb_id TEXT)",
            "CREATE TABLE IF NOT EXISTS current_local_person(emby_id INTEGER PRIMARY KEY,name TEXT NOT NULL,tmdb_id TEXT,tvdb_id TEXT,imdb_id TEXT)",
            "CREATE TABLE IF NOT EXISTS current_local_credit(person_emby_id INTEGER NOT NULL,media_emby_id INTEGER NOT NULL,role TEXT NOT NULL,PRIMARY KEY(person_emby_id,media_emby_id,role)) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS current_provider_media(provider TEXT NOT NULL,media_type TEXT NOT NULL,provider_media_id TEXT NOT NULL,PRIMARY KEY(provider,media_type,provider_media_id)) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS work_queue(provider TEXT NOT NULL,entity_type TEXT NOT NULL,media_type TEXT NOT NULL,provider_id TEXT NOT NULL,priority INTEGER NOT NULL,status TEXT NOT NULL,attempts INTEGER NOT NULL,error TEXT,updated_utc INTEGER NOT NULL,graph_eligible INTEGER NOT NULL CHECK(graph_eligible IN(0,1)),PRIMARY KEY(provider,entity_type,media_type,provider_id)) WITHOUT ROWID",
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
            "CREATE TABLE IF NOT EXISTS resolution_pair(run_id INTEGER NOT NULL,pair_id TEXT NOT NULL,left_provider TEXT NOT NULL,left_provider_person_id TEXT NOT NULL,right_provider TEXT NOT NULL,right_provider_person_id TEXT NOT NULL,model_version TEXT NOT NULL,disposition TEXT NOT NULL,confidence REAL NOT NULL,PRIMARY KEY(run_id,pair_id),FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_resolution_pair_disposition ON resolution_pair(run_id,disposition,confidence)",
            "CREATE TABLE IF NOT EXISTS resolution_pair_feature(run_id INTEGER NOT NULL,pair_id TEXT NOT NULL,feature_name TEXT NOT NULL,numeric_value REAL,text_value TEXT,PRIMARY KEY(run_id,pair_id,feature_name),FOREIGN KEY(run_id,pair_id) REFERENCES resolution_pair(run_id,pair_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_cluster(run_id INTEGER NOT NULL,cluster_id TEXT NOT NULL,anchor_emby_id INTEGER,identity_confidence REAL NOT NULL,local_anchor_confidence REAL NOT NULL,PRIMARY KEY(run_id,cluster_id),FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_cluster_member(run_id INTEGER NOT NULL,cluster_id TEXT NOT NULL,provider TEXT NOT NULL,provider_person_id TEXT NOT NULL,PRIMARY KEY(run_id,cluster_id,provider,provider_person_id),FOREIGN KEY(run_id,cluster_id) REFERENCES resolution_cluster(run_id,cluster_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS provider_correction(correction_id INTEGER PRIMARY KEY AUTOINCREMENT,kind TEXT NOT NULL,operation TEXT NOT NULL,provider TEXT NOT NULL DEFAULT '',media_type TEXT NOT NULL DEFAULT '',provider_media_id TEXT NOT NULL DEFAULT '',provider_person_id TEXT NOT NULL DEFAULT '',field_name TEXT NOT NULL DEFAULT '',current_value TEXT NOT NULL DEFAULT '',replacement_value TEXT NOT NULL DEFAULT '',secondary_provider TEXT NOT NULL DEFAULT '',secondary_id TEXT NOT NULL DEFAULT '',emby_id INTEGER,reason TEXT NOT NULL,note TEXT NOT NULL DEFAULT '',enabled INTEGER NOT NULL CHECK(enabled IN(0,1)),created_utc INTEGER NOT NULL,updated_utc INTEGER NOT NULL)",
            "CREATE INDEX IF NOT EXISTS idx_provider_correction_enabled ON provider_correction(enabled,kind,provider)",
            "CREATE TABLE IF NOT EXISTS correction_application(run_id INTEGER NOT NULL,correction_id INTEGER NOT NULL,matched_count INTEGER NOT NULL,changed_count INTEGER NOT NULL,summary TEXT NOT NULL,applied_utc INTEGER NOT NULL,PRIMARY KEY(run_id,correction_id),FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE,FOREIGN KEY(correction_id) REFERENCES provider_correction(correction_id) ON DELETE CASCADE) WITHOUT ROWID"
        };
    }
}
