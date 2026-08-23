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
                foreach (var sql in Schema) db.Execute(sql);
                db.Execute("INSERT OR IGNORE INTO schema_info(singleton,version) VALUES(1,1)");
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
            lock (sync) using (var s = db.PrepareStatement("SELECT provider,entity_type,provider_id,media_type,priority FROM work_queue WHERE status='pending' AND entity_type='media' ORDER BY priority DESC,provider,media_type,provider_id"))
                foreach (var r in s.Rows()) result.Add(ReadQueue(r));
            return result;
        }

        public List<QueueItem> PendingPeople()
        {
            var result = new List<QueueItem>();
            lock (sync) using (var s = db.PrepareStatement("SELECT provider,entity_type,provider_id,media_type,priority FROM work_queue WHERE status='pending' AND entity_type='person' ORDER BY priority DESC,provider,provider_id"))
                foreach (var r in s.Rows()) result.Add(ReadQueue(r));
            return result;
        }

        public PersonSeedSummary SeedDiscoveredPeople()
        {
            var localByMedia = new Dictionary<long, LocalPersonScope>(capacity: 128);
            lock (sync)
            {
                using (var s = db.PrepareStatement(@"SELECT DISTINCT c.media_emby_id,p.emby_id,p.name,p.tmdb_id,p.tvdb_id,p.imdb_id
FROM current_local_credit c JOIN current_local_person p ON p.emby_id=c.person_emby_id"))
                    foreach (var r in s.Rows())
                    {
                        var mediaId = r.GetInt64(0);
                        if (!localByMedia.TryGetValue(mediaId, out var scope)) localByMedia[mediaId] = scope = new LocalPersonScope();
                        scope.Add(r.GetString(2), Null(r, 3), Null(r, 4));
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

                db.RunInTransaction(x =>
                {
                    foreach (var key in selected)
                    {
                        var separator = key.IndexOf(':'); var provider = key.Substring(0, separator); var providerId = key.Substring(separator + 1);
                        Statement(x, "INSERT OR IGNORE INTO work_queue(provider,entity_type,media_type,provider_id,priority,status,attempts,error,updated_utc) VALUES(@provider,'person','person',@person,1,'pending',0,NULL,@now)", s => { s.Bind("@provider", provider); s.Bind("@person", providerId); s.Bind("@now", Now()); });
                    }
                }, TransactionMode.Immediate);

                return new PersonSeedSummary
                {
                    DiscoveredTmdb = discovered.Count(x => x.StartsWith(ProviderNames.Tmdb + ":", StringComparison.Ordinal)),
                    DiscoveredTvdb = discovered.Count(x => x.StartsWith(ProviderNames.Tvdb + ":", StringComparison.Ordinal)),
                    SelectedTmdb = selected.Count(x => x.StartsWith(ProviderNames.Tmdb + ":", StringComparison.Ordinal)),
                    SelectedTvdb = selected.Count(x => x.StartsWith(ProviderNames.Tvdb + ":", StringComparison.Ordinal))
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

        public CacheEntry GetCache(string provider, string entityType, string mediaType, string providerId)
        {
            lock (sync) using (var s = db.PrepareStatement("SELECT payload_hash,relative_path,last_fetched_utc FROM cache_manifest WHERE provider=@provider AND entity_type=@entity AND media_type=@media AND provider_id=@id"))
            {
                s.Bind("@provider", provider); s.Bind("@entity", entityType); s.Bind("@media", Dimension(entityType, mediaType)); s.Bind("@id", providerId);
                foreach (var r in s.Rows()) return new CacheEntry { Provider = provider, EntityType = entityType, MediaType = mediaType, ProviderId = providerId, PayloadHash = r.GetString(0), RelativePath = r.GetString(1), LastFetchedUnix = r.GetInt64(2) };
            }
            return null;
        }

        public void SaveCache(CacheEntry entry)
        {
            lock (sync) Statement("INSERT OR REPLACE INTO cache_manifest VALUES(@provider,@entity,@media,@id,@hash,@path,@fetched)", s => { s.Bind("@provider", entry.Provider); s.Bind("@entity", entry.EntityType); s.Bind("@media", Dimension(entry.EntityType, entry.MediaType)); s.Bind("@id", entry.ProviderId); s.Bind("@hash", entry.PayloadHash); s.Bind("@path", entry.RelativePath); s.Bind("@fetched", entry.LastFetchedUnix); });
        }

        public void MarkQueue(QueueItem item, string status, string error = null)
        {
            lock (sync) Statement("UPDATE work_queue SET status=@status,attempts=attempts+1,error=@error,updated_utc=@now WHERE provider=@provider AND entity_type=@entity AND media_type=@media AND provider_id=@id", s => { s.Bind("@status", status); s.Bind("@error", error); s.Bind("@now", Now()); s.Bind("@provider", item.Provider); s.Bind("@entity", item.EntityType); s.Bind("@media", Dimension(item.EntityType, item.MediaType)); s.Bind("@id", item.ProviderId); });
        }

        public void ReplaceMedia(FlattenedMedia media)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "INSERT OR REPLACE INTO provider_media VALUES(@provider,@type,@id,@name,@now)", s => { s.Bind("@provider", media.Provider); s.Bind("@type", media.MediaType); s.Bind("@id", media.ProviderMediaId); s.Bind("@name", media.Name); s.Bind("@now", Now()); });
                DeleteForMedia(x, "media_external_id", media); DeleteForMedia(x, "provider_media_credit", media);
                foreach (var id in media.ExternalIds.Where(x => !string.IsNullOrWhiteSpace(x.Value)))
                    Statement(x, "INSERT OR IGNORE INTO media_external_id VALUES(@provider,@type,@id,@source,@external)", s => { s.Bind("@provider", media.Provider); s.Bind("@type", media.MediaType); s.Bind("@id", media.ProviderMediaId); s.Bind("@source", id.Key.ToLowerInvariant()); s.Bind("@external", id.Value); });
                foreach (var credit in media.Credits.Where(x => !string.IsNullOrWhiteSpace(x.ProviderPersonId)).GroupBy(x => x.ProviderPersonId + "|" + x.Role, StringComparer.Ordinal).Select(x => x.First()))
                {
                    Statement(x, "INSERT OR IGNORE INTO provider_media_credit VALUES(@provider,@type,@media,@person,@name,@role)", s => { s.Bind("@provider", media.Provider); s.Bind("@type", media.MediaType); s.Bind("@media", media.ProviderMediaId); s.Bind("@person", credit.ProviderPersonId); s.Bind("@name", credit.PersonName); s.Bind("@role", credit.Role); });
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

        public ResolutionInput LoadResolutionInput()
        {
            var input = new ResolutionInput();
            lock (sync)
            {
                using (var s = db.PrepareStatement("SELECT emby_id,media_type,name,production_year,tmdb_id,tvdb_id,imdb_id FROM current_media")) foreach (var r in s.Rows()) input.Media.Add(new MediaSeed { EmbyId = r.GetInt64(0), MediaType = r.GetString(1), Name = r.GetString(2), Year = r.IsDBNull(3) ? (int?)null : r.GetInt(3), TmdbId = Null(r, 4), TvdbId = Null(r, 5), ImdbId = Null(r, 6) });
                using (var s = db.PrepareStatement("SELECT emby_id,name,tmdb_id,tvdb_id,imdb_id FROM current_local_person")) foreach (var r in s.Rows()) input.LocalPeople.Add(new LocalPerson { EmbyId = r.GetInt64(0), Name = r.GetString(1), TmdbId = Null(r, 2), TvdbId = Null(r, 3), ImdbId = Null(r, 4) });
                using (var s = db.PrepareStatement("SELECT person_emby_id,media_emby_id,role FROM current_local_credit")) foreach (var r in s.Rows()) input.LocalCredits.Add(new LocalCredit { PersonEmbyId = r.GetInt64(0), MediaEmbyId = r.GetInt64(1), Role = r.GetString(2) });
                const string personSql = "SELECT DISTINCT p.provider,p.provider_person_id,p.name,p.clean_name,p.birthday FROM provider_person p JOIN provider_media_credit c ON c.provider=p.provider AND c.provider_person_id=p.provider_person_id JOIN current_provider_media m ON m.provider=c.provider AND m.media_type=c.media_type AND m.provider_media_id=c.provider_media_id";
                using (var s = db.PrepareStatement(personSql)) foreach (var r in s.Rows()) input.ProviderPeople.Add(new ProviderPerson { Provider = r.GetString(0), ProviderId = r.GetString(1), Name = r.GetString(2), CleanName = r.GetString(3), Birthday = Null(r, 4) });
                var byKey = input.ProviderPeople.ToDictionary(x => x.Key, StringComparer.Ordinal);
                using (var s = db.PrepareStatement("SELECT provider,provider_person_id,external_provider,external_id FROM person_external_id")) foreach (var r in s.Rows()) if (byKey.TryGetValue(r.GetString(0) + ":" + r.GetString(1), out var p)) p.ExternalIds[r.GetString(2)] = r.GetString(3);
                using (var s = db.PrepareStatement("SELECT provider,provider_person_id,alias FROM person_alias")) foreach (var r in s.Rows()) if (byKey.TryGetValue(r.GetString(0) + ":" + r.GetString(1), out var p)) p.Aliases.Add(r.GetString(2));
                const string mediaSql = "SELECT c.provider,c.provider_person_id,c.media_type,c.provider_media_id,e.external_provider,e.external_id FROM provider_media_credit c JOIN current_provider_media m ON m.provider=c.provider AND m.media_type=c.media_type AND m.provider_media_id=c.provider_media_id LEFT JOIN media_external_id e ON e.provider=c.provider AND e.media_type=c.media_type AND e.provider_media_id=c.provider_media_id";
                var attributions = new Dictionary<string, CanonicalMediaBuilder>(StringComparer.Ordinal);
                using (var s = db.PrepareStatement(mediaSql)) foreach (var r in s.Rows())
                {
                    var personKey = r.GetString(0) + ":" + r.GetString(1);
                    if (!byKey.ContainsKey(personKey)) continue;
                    var attributionKey = personKey + "|" + r.GetString(2) + "|" + r.GetString(3);
                    if (!attributions.TryGetValue(attributionKey, out var value)) attributions[attributionKey] = value = new CanonicalMediaBuilder { PersonKey = personKey, Provider = r.GetString(0), MediaType = r.GetString(2), NativeId = r.GetString(3) };
                    if (!r.IsDBNull(4) && !r.IsDBNull(5)) value.ExternalIds[r.GetString(4)] = r.GetString(5);
                }
                foreach (var value in attributions.Values) byKey[value.PersonKey].CanonicalMediaKeys.Add(value.CanonicalKey());
                using (var s = db.PrepareStatement("SELECT provider_a,provider_id_a,provider_b,provider_id_b,disposition FROM manual_bridge")) foreach (var r in s.Rows()) input.Bridges.Add(new ManualBridge { ProviderA = r.GetString(0), ProviderIdA = r.GetString(1), ProviderB = r.GetString(2), ProviderIdB = r.GetString(3), IsRejected = r.GetString(4) == "reject" });
            }
            return input;
        }

        public void SaveDecisions(long runId, IReadOnlyCollection<ResolutionDecision> decisions)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "DELETE FROM resolution_decision WHERE run_id=@run", s => s.Bind("@run", runId));
                foreach (var decision in decisions)
                {
                    Statement(x, "INSERT OR REPLACE INTO resolution_decision VALUES(@run,@id,@status,@action,@name,@anchor,@keys,@confidence,@impact,@headline,@explanation)", s => { s.Bind("@run", runId); s.Bind("@id", Required(decision.DecisionId, "decision-id-missing")); s.Bind("@status", Required(decision.Status, "UNKNOWN")); s.Bind("@action", Required(decision.Action, "HUMAN_REVIEW")); s.Bind("@name", Required(decision.DisplayName, "Unnamed person")); s.Bind("@anchor", decision.AnchorEmbyPersonId); s.Bind("@keys", Required(decision.ProviderKeys, "No current provider person ID")); s.Bind("@confidence", decision.Confidence); s.Bind("@impact", decision.ImpactedMediaCount); s.Bind("@headline", Required(decision.Headline, "Decision summary unavailable")); s.Bind("@explanation", Required(decision.Explanation, "No additional explanation was generated.")); });
                    foreach (var evidence in decision.Evidence)
                        Statement(x, "INSERT OR REPLACE INTO resolution_evidence VALUES(@run,@decision,@sort,@signal,@verdict,@narrative,@metric)", s => { s.Bind("@run", runId); s.Bind("@decision", Required(decision.DecisionId, "decision-id-missing")); s.Bind("@sort", evidence.SortOrder); s.Bind("@signal", Required(evidence.SignalType, "UNSPECIFIED")); s.Bind("@verdict", Required(evidence.Verdict, "unknown")); s.Bind("@narrative", Required(evidence.Narrative, "No narrative was generated.")); s.Bind("@metric", Required(evidence.Metric, "-")); });
                    foreach (var media in decision.ImpactedMedia)
                        Statement(x, "INSERT OR REPLACE INTO resolution_media VALUES(@run,@decision,@media,@type,@name,@role)", s => { s.Bind("@run", runId); s.Bind("@decision", Required(decision.DecisionId, "decision-id-missing")); s.Bind("@media", media.EmbyMediaId); s.Bind("@type", Required(media.MediaType, "unknown")); s.Bind("@name", Required(media.DisplayName, "Unnamed media")); s.Bind("@role", Required(media.Role, "Unspecified role")); });
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

        public RunStatus LatestRun()
        {
            lock (sync)
            {
                RunStatus run = null;
                using (var s = db.PrepareStatement("SELECT run_id,status,mode,phase,message,selected_movies,selected_series,media_fetched,people_fetched,cache_hits,failures,decisions FROM resolution_run ORDER BY run_id DESC LIMIT 1"))
                    foreach (var r in s.Rows()) run = new RunStatus { RunId = r.GetInt64(0), Status = r.GetString(1), Mode = r.GetString(2), Phase = r.GetString(3), Message = r.GetString(4), SelectedMovies = r.GetInt(5), SelectedSeries = r.GetInt(6), MediaFetched = r.GetInt(7), PeopleFetched = r.GetInt(8), CacheHits = r.GetInt(9), Failures = r.GetInt(10), Decisions = r.GetInt(11) };
                if (run == null) return null;

                var counts = new List<string>();
                using (var s = db.PrepareStatement("SELECT status,count(*) FROM resolution_decision WHERE run_id=@run GROUP BY status ORDER BY CASE status WHEN 'SPLIT' THEN 0 WHEN 'CONFLATION' THEN 1 WHEN 'DRIFT' THEN 2 WHEN 'ORPHAN' THEN 3 ELSE 4 END,status"))
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
                using (var s = db.PrepareStatement("WITH visible AS (" + visible + ") SELECT d.decision_id,d.status,d.action,d.display_name,d.anchor_emby_id,d.provider_keys,d.confidence,d.impact_media_count,d.headline,d.explanation FROM resolution_decision d JOIN visible v ON v.decision_id=d.decision_id WHERE d.run_id=@run ORDER BY CASE d.status WHEN 'SPLIT' THEN 0 WHEN 'CONFLATION' THEN 1 WHEN 'DRIFT' THEN 2 WHEN 'ORPHAN' THEN 3 ELSE 4 END,d.confidence ASC,d.impact_media_count DESC,d.decision_id"))
                {
                    s.Bind("@run", latest); s.Bind("@summaryLimit", Math.Max(1, maximumRows));
                    foreach (var r in s.Rows()) result.Add(new DashboardDecision { DecisionId = r.GetString(0), Status = r.GetString(1), Action = r.GetString(2), Person = r.GetString(3), EmbyAnchor = r.IsDBNull(4) ? "—" : r.GetInt64(4).ToString(CultureInfo.InvariantCulture), ProviderIdentities = r.GetString(5), Confidence = r.GetDouble(6).ToString("P0", CultureInfo.InvariantCulture), ImpactedTitles = r.GetInt(7), Decision = r.GetString(8), Why = r.GetString(9) });
                }
                var byDecision = result.ToDictionary(x => x.DecisionId, StringComparer.Ordinal);
                using (var s = db.PrepareStatement("WITH visible AS (" + visible + ") SELECT e.decision_id,e.sort_order,e.signal_type,e.verdict,e.narrative,e.metric_raw FROM resolution_evidence e JOIN visible v ON v.decision_id=e.decision_id WHERE e.run_id=@run ORDER BY e.decision_id,e.sort_order,e.signal_type"))
                {
                    s.Bind("@run", latest); s.Bind("@summaryLimit", Math.Max(1, maximumRows));
                    foreach (var r in s.Rows()) if (byDecision.TryGetValue(r.GetString(0), out var decision))
                        decision.Details = decision.Details.Concat(new[] { new DashboardDetail { DetailId = r.GetString(0) + ":e:" + r.GetInt(1).ToString(CultureInfo.InvariantCulture) + ":" + r.GetString(2), Section = "Evidence", Order = r.GetInt(1), Signal = r.GetString(2), Verdict = r.GetString(3), Explanation = r.GetString(4), RawMetric = r.GetString(5) } }).ToArray();
                }
                const string mediaSql = "WITH visible AS (" + visible + "), ranked AS (SELECT m.*,ROW_NUMBER() OVER(PARTITION BY m.decision_id ORDER BY m.media_type,m.display_name,m.emby_media_id) AS row_number FROM resolution_media m JOIN visible v ON v.decision_id=m.decision_id WHERE m.run_id=@run) SELECT decision_id,row_number,media_type,display_name,role FROM ranked WHERE row_number<=@mediaLimit ORDER BY decision_id,row_number";
                using (var s = db.PrepareStatement(mediaSql))
                {
                    s.Bind("@run", latest); s.Bind("@summaryLimit", Math.Max(1, maximumRows)); s.Bind("@mediaLimit", Math.Max(0, mediaExamples));
                    foreach (var r in s.Rows()) if (byDecision.TryGetValue(r.GetString(0), out var decision))
                        decision.Details = decision.Details.Concat(new[] { new DashboardDetail { DetailId = r.GetString(0) + ":m:" + r.GetInt(1).ToString(CultureInfo.InvariantCulture), Section = "Impacted titles", Order = 10000 + r.GetInt(1), Signal = r.GetString(2), Verdict = r.GetString(4), Explanation = r.GetString(3), RawMetric = string.Empty } }).ToArray();
                }
            }
            return result.ToArray();
        }

        private static void SeedMedia(IDatabaseConnection x, long runId, string provider, string type, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            Statement(x, "INSERT OR IGNORE INTO current_provider_media VALUES(@provider,@type,@id)", s => { s.Bind("@provider", provider); s.Bind("@type", type); s.Bind("@id", id); });
            Statement(x, "INSERT OR REPLACE INTO work_queue VALUES(@provider,'media',@type,@id,2,'pending',0,NULL,@now)", s => { s.Bind("@provider", provider); s.Bind("@type", type); s.Bind("@id", id); s.Bind("@now", Now()); });
        }

        private static QueueItem ReadQueue(IResultSet r) => new QueueItem { Provider = r.GetString(0), EntityType = r.GetString(1), ProviderId = r.GetString(2), MediaType = r.GetString(3), Priority = r.GetInt(4) };
        private static string Dimension(string entityType, string mediaType) => string.Equals(entityType, "person", StringComparison.Ordinal) ? "person" : string.IsNullOrWhiteSpace(mediaType) ? "unknown" : mediaType;
        private static string Required(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
        private static string Null(IResultSet r, int index) => r.IsDBNull(index) ? null : r.GetString(index);
        private static void DeleteForMedia(IDatabaseConnection x, string table, FlattenedMedia media) => Statement(x, "DELETE FROM " + table + " WHERE provider=@provider AND media_type=@type AND provider_media_id=@id", s => { s.Bind("@provider", media.Provider); s.Bind("@type", media.MediaType); s.Bind("@id", media.ProviderMediaId); });
        private void Statement(string sql, Action<IStatement> bind) => Statement(db, sql, bind);
        private static void Statement(IDatabaseConnection connection, string sql, Action<IStatement> bind) { using (var s = connection.PrepareStatement(sql)) { bind(s); s.MoveNext(); } }
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
            public string PersonKey { get; set; }
            public string Provider { get; set; }
            public string MediaType { get; set; }
            public string NativeId { get; set; }
            public Dictionary<string, string> ExternalIds { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string CanonicalKey()
            {
                if (ExternalIds.TryGetValue(ProviderNames.Imdb, out var imdb) && !string.IsNullOrWhiteSpace(imdb)) return ProviderNames.Imdb + ":" + imdb;
                if (Provider == ProviderNames.Tmdb) return ProviderNames.Tmdb + ":" + MediaType + ":" + NativeId;
                if (ExternalIds.TryGetValue(ProviderNames.Tmdb, out var tmdb) && !string.IsNullOrWhiteSpace(tmdb)) return ProviderNames.Tmdb + ":" + MediaType + ":" + tmdb;
                if (Provider == ProviderNames.Tvdb) return ProviderNames.Tvdb + ":" + MediaType + ":" + NativeId;
                return Provider + ":" + MediaType + ":" + NativeId;
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
            "CREATE TABLE IF NOT EXISTS work_queue(provider TEXT NOT NULL,entity_type TEXT NOT NULL,media_type TEXT NOT NULL,provider_id TEXT NOT NULL,priority INTEGER NOT NULL,status TEXT NOT NULL,attempts INTEGER NOT NULL,error TEXT,updated_utc INTEGER NOT NULL,PRIMARY KEY(provider,entity_type,media_type,provider_id)) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_work_queue_status ON work_queue(status,entity_type,priority DESC)",
            "CREATE TABLE IF NOT EXISTS cache_manifest(provider TEXT NOT NULL,entity_type TEXT NOT NULL,media_type TEXT NOT NULL,provider_id TEXT NOT NULL,payload_hash TEXT NOT NULL,relative_path TEXT NOT NULL,last_fetched_utc INTEGER NOT NULL,PRIMARY KEY(provider,entity_type,media_type,provider_id)) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS fetch_failure(provider TEXT NOT NULL,entity_type TEXT NOT NULL,media_type TEXT NOT NULL,provider_id TEXT NOT NULL,last_failed_utc INTEGER NOT NULL,error TEXT NOT NULL,PRIMARY KEY(provider,entity_type,media_type,provider_id)) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS provider_media(provider TEXT NOT NULL,media_type TEXT NOT NULL,provider_media_id TEXT NOT NULL,name TEXT,updated_utc INTEGER NOT NULL,PRIMARY KEY(provider,media_type,provider_media_id)) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS media_external_id(provider TEXT NOT NULL,media_type TEXT NOT NULL,provider_media_id TEXT NOT NULL,external_provider TEXT NOT NULL,external_id TEXT NOT NULL,PRIMARY KEY(provider,media_type,provider_media_id,external_provider,external_id)) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_media_external_reverse ON media_external_id(media_type,external_provider,external_id)",
            "CREATE TABLE IF NOT EXISTS provider_media_credit(provider TEXT NOT NULL,media_type TEXT NOT NULL,provider_media_id TEXT NOT NULL,provider_person_id TEXT NOT NULL,person_name TEXT,role TEXT NOT NULL,PRIMARY KEY(provider,media_type,provider_media_id,provider_person_id,role)) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_provider_credit_person ON provider_media_credit(provider,provider_person_id)",
            "CREATE TABLE IF NOT EXISTS provider_person(provider TEXT NOT NULL,provider_person_id TEXT NOT NULL,name TEXT NOT NULL,clean_name TEXT NOT NULL,birthday TEXT,updated_utc INTEGER NOT NULL,PRIMARY KEY(provider,provider_person_id)) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_provider_person_name ON provider_person(clean_name)",
            "CREATE TABLE IF NOT EXISTS person_external_id(provider TEXT NOT NULL,provider_person_id TEXT NOT NULL,external_provider TEXT NOT NULL,external_id TEXT NOT NULL,PRIMARY KEY(provider,provider_person_id,external_provider,external_id)) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_person_external_reverse ON person_external_id(external_provider,external_id)",
            "CREATE TABLE IF NOT EXISTS person_alias(provider TEXT NOT NULL,provider_person_id TEXT NOT NULL,alias TEXT NOT NULL,clean_alias TEXT NOT NULL,PRIMARY KEY(provider,provider_person_id,alias)) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_person_alias_clean ON person_alias(clean_alias)",
            "CREATE TABLE IF NOT EXISTS manual_bridge(provider_a TEXT NOT NULL,provider_id_a TEXT NOT NULL,provider_b TEXT NOT NULL,provider_id_b TEXT NOT NULL,disposition TEXT NOT NULL CHECK(disposition IN('confirm','reject')),created_utc INTEGER NOT NULL,PRIMARY KEY(provider_a,provider_id_a,provider_b,provider_id_b)) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_decision(run_id INTEGER NOT NULL,decision_id TEXT NOT NULL,status TEXT NOT NULL,action TEXT NOT NULL,display_name TEXT NOT NULL,anchor_emby_id INTEGER,provider_keys TEXT NOT NULL,confidence REAL NOT NULL,impact_media_count INTEGER NOT NULL,headline TEXT NOT NULL,explanation TEXT NOT NULL,PRIMARY KEY(run_id,decision_id),FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE INDEX IF NOT EXISTS idx_decision_ui ON resolution_decision(run_id,status,confidence,impact_media_count DESC)",
            "CREATE TABLE IF NOT EXISTS resolution_evidence(run_id INTEGER NOT NULL,decision_id TEXT NOT NULL,sort_order INTEGER NOT NULL,signal_type TEXT NOT NULL,verdict TEXT NOT NULL,narrative TEXT NOT NULL,metric_raw TEXT NOT NULL,PRIMARY KEY(run_id,decision_id,sort_order,signal_type),FOREIGN KEY(run_id,decision_id) REFERENCES resolution_decision(run_id,decision_id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE TABLE IF NOT EXISTS resolution_media(run_id INTEGER NOT NULL,decision_id TEXT NOT NULL,emby_media_id INTEGER NOT NULL,media_type TEXT NOT NULL,display_name TEXT NOT NULL,role TEXT NOT NULL,PRIMARY KEY(run_id,decision_id,emby_media_id,role),FOREIGN KEY(run_id,decision_id) REFERENCES resolution_decision(run_id,decision_id) ON DELETE CASCADE) WITHOUT ROWID"
        };
    }
}
