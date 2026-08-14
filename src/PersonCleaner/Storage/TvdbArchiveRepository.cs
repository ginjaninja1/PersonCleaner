using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using SQLitePCLEx;
using System.Collections.Generic;
using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PersonCleaner.Storage
{
    internal sealed class TvdbArchiveRepository : IDisposable
    {
        private readonly ILogger logger;
        private readonly object sync = new object();
        private IDatabaseConnection db;
        public string DatabasePath { get; }

        public TvdbArchiveRepository(IApplicationPaths paths, ILogger logger)
        {
            this.logger = logger;
            DatabasePath = Path.Combine(paths.DataPath, "tvdb-archive.db");
        }

        public void Initialize()
        {
            lock (sync)
            {
                if (db != null) return;
                db = SQLite3.Open(
                    DatabasePath,
                    ConnectionFlags.Create | ConnectionFlags.ReadWrite | ConnectionFlags.PrivateCache | ConnectionFlags.NoMutex,
                    null,
                    new Dictionary<string, delegate_collation>(),
                    new Dictionary<Tuple<string, int>, Action<IReadOnlyList<sqlite3_value>, sqlite3_context>>(),
                    true,
                    false);
                db.Execute("PRAGMA journal_mode=WAL");
                db.Execute("PRAGMA synchronous=NORMAL");
                db.Execute("PRAGMA foreign_keys=ON");
                db.Execute("CREATE TABLE IF NOT EXISTS schema_info(version INTEGER NOT NULL)");
                db.Execute("INSERT INTO schema_info(version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM schema_info)");
                db.Execute("CREATE TABLE IF NOT EXISTS emby_item(emby_id INTEGER PRIMARY KEY, emby_guid TEXT, item_type TEXT NOT NULL, name TEXT, production_year INTEGER, parent_emby_id INTEGER, tvdb_id TEXT, imdb_id TEXT, tmdb_id TEXT, path TEXT, discovered_utc TEXT NOT NULL)");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_emby_item_tvdb ON emby_item(tvdb_id,item_type)");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_emby_item_imdb ON emby_item(imdb_id,item_type)");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_emby_item_name ON emby_item(name,item_type)");
                db.Execute("CREATE TABLE IF NOT EXISTS tvdb_entity(tvdb_id TEXT NOT NULL, entity_type TEXT NOT NULL, name TEXT, slug TEXT, birth_date TEXT, death_date TEXT, birth_place TEXT, first_aired TEXT, last_aired TEXT, country TEXT, language TEXT, raw_json TEXT, fetched_utc TEXT NOT NULL, PRIMARY KEY(tvdb_id,entity_type))");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_tvdb_entity_name ON tvdb_entity(name,entity_type)");
                db.Execute("CREATE TABLE IF NOT EXISTS remote_id(tvdb_id TEXT NOT NULL, entity_type TEXT NOT NULL, source_name TEXT NOT NULL, remote_id TEXT NOT NULL, source_type INTEGER, PRIMARY KEY(tvdb_id,entity_type,source_name,remote_id))");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_remote_id_value ON remote_id(remote_id,source_name,entity_type)");
                db.Execute("CREATE TABLE IF NOT EXISTS credit(subject_tvdb_id TEXT NOT NULL, subject_type TEXT NOT NULL, person_tvdb_id TEXT NOT NULL, character_id INTEGER NOT NULL, episode_tvdb_id TEXT, person_name TEXT, role_name TEXT, credit_type TEXT, sort_order INTEGER, is_featured INTEGER, PRIMARY KEY(subject_tvdb_id,subject_type,person_tvdb_id,character_id))");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_credit_person ON credit(person_tvdb_id,subject_type)");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_credit_episode ON credit(episode_tvdb_id)");
                db.Execute("DELETE FROM credit WHERE credit_type IS NULL OR credit_type NOT IN ('Actor','Guest Star','Director','Writer','Screenplay','Producer','Executive Producer','Creator','Showrunner')");
                db.Execute("CREATE TABLE IF NOT EXISTS fetch_cache(cache_key TEXT PRIMARY KEY, state TEXT NOT NULL, http_status INTEGER, attempt_count INTEGER NOT NULL, fetched_utc TEXT, next_attempt_utc TEXT NOT NULL, error TEXT)");
                db.Execute("CREATE TABLE IF NOT EXISTS api_response_cache(request_path TEXT PRIMARY KEY, response_json TEXT NOT NULL, fetched_utc TEXT NOT NULL, expires_utc TEXT NOT NULL)");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_api_response_expiry ON api_response_cache(expires_utc)");
                db.Execute("CREATE TABLE IF NOT EXISTS run_state(task_key TEXT PRIMARY KEY, status TEXT NOT NULL, started_utc TEXT, updated_utc TEXT NOT NULL, finished_utc TEXT, total_items INTEGER NOT NULL, processed_items INTEGER NOT NULL, success_count INTEGER NOT NULL, failure_count INTEGER NOT NULL, last_emby_id INTEGER, message TEXT)");
                db.Execute("CREATE TABLE IF NOT EXISTS export_scope(task_key TEXT NOT NULL, emby_id INTEGER NOT NULL, ordinal INTEGER NOT NULL, entity_type TEXT NOT NULL, id_area TEXT NOT NULL, result TEXT NOT NULL DEFAULT 'pending', updated_utc TEXT NOT NULL, PRIMARY KEY(task_key,emby_id))");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_export_scope_progress ON export_scope(task_key,entity_type,id_area,result)");
                db.Execute("CREATE TABLE IF NOT EXISTS id_probe(direction TEXT NOT NULL, entity_type TEXT NOT NULL, input_id TEXT NOT NULL, tvdb_id TEXT, imdb_id TEXT, name TEXT, success INTEGER NOT NULL, checked_utc TEXT NOT NULL, raw_json TEXT, PRIMARY KEY(direction,entity_type,input_id,tvdb_id))");
                db.Execute("CREATE TABLE IF NOT EXISTS resolution_evaluation(emby_id INTEGER NOT NULL, entity_type TEXT NOT NULL, emby_name TEXT, withheld_tvdb_id TEXT NOT NULL, predicted_tvdb_id TEXT, method TEXT NOT NULL, confidence REAL NOT NULL, candidate_count INTEGER NOT NULL, is_correct INTEGER NOT NULL, evidence_json TEXT, evaluated_utc TEXT NOT NULL, PRIMARY KEY(emby_id,method))");
                db.Execute("CREATE TABLE IF NOT EXISTS item_resolution(emby_id INTEGER PRIMARY KEY, entity_type TEXT NOT NULL, observed_tvdb_id TEXT, resolved_tvdb_id TEXT, provenance TEXT NOT NULL, method TEXT NOT NULL, confidence REAL NOT NULL, candidate_count INTEGER NOT NULL, evidence_json TEXT, evaluated_utc TEXT NOT NULL)");
                db.Execute("CREATE TABLE IF NOT EXISTS resolution_candidate(emby_id INTEGER NOT NULL, rank INTEGER NOT NULL, entity_type TEXT NOT NULL, tvdb_id TEXT, name TEXT, score REAL NOT NULL, external_ids_json TEXT, filmography_ids_json TEXT, evidence TEXT, evaluated_utc TEXT NOT NULL, PRIMARY KEY(emby_id,rank))");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_resolution_candidate_tvdb ON resolution_candidate(tvdb_id,entity_type)");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_item_resolution_tvdb ON item_resolution(resolved_tvdb_id,entity_type)");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_item_resolution_provenance ON item_resolution(provenance,entity_type,confidence)");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_resolution_eval_result ON resolution_evaluation(entity_type,is_correct,confidence)");
                db.Execute("CREATE VIEW IF NOT EXISTS resolution_evaluation_summary AS SELECT entity_type,method,COUNT(*) AS evaluated,SUM(is_correct) AS correct,ROUND(100.0*SUM(is_correct)/COUNT(*),2) AS precision_percent,ROUND(AVG(confidence),4) AS average_confidence FROM resolution_evaluation GROUP BY entity_type,method");
                db.Execute("CREATE VIEW IF NOT EXISTS resolution_inventory AS SELECT r.provenance,r.entity_type,COUNT(*) AS item_count,ROUND(AVG(r.confidence),4) AS average_confidence FROM item_resolution r GROUP BY r.provenance,r.entity_type");
                db.Execute("CREATE VIEW IF NOT EXISTS resolved_searchable_media AS SELECT e.emby_id,e.emby_guid,e.item_type,e.name AS emby_name,e.production_year,e.imdb_id,e.tmdb_id,r.observed_tvdb_id,r.resolved_tvdb_id,r.provenance,r.method,r.confidence,r.candidate_count,t.name AS tvdb_name FROM emby_item e JOIN item_resolution r ON r.emby_id=e.emby_id LEFT JOIN tvdb_entity t ON t.tvdb_id=r.resolved_tvdb_id AND t.entity_type=r.entity_type");
                db.Execute("CREATE VIEW IF NOT EXISTS identity_review_queue AS SELECT e.emby_id,e.item_type,e.name AS emby_name,e.production_year,e.imdb_id,e.tmdb_id,r.observed_tvdb_id,r.resolved_tvdb_id,r.provenance,r.method,r.confidence,r.candidate_count,r.evidence_json,r.evaluated_utc FROM emby_item e JOIN item_resolution r ON r.emby_id=e.emby_id WHERE r.provenance IN ('direct-unavailable','rejected','unresolved','conflict')");
                db.Execute("CREATE VIEW IF NOT EXISTS export_area_progress AS SELECT task_key,entity_type,id_area,COUNT(*) AS total_items,SUM(CASE WHEN result<>'pending' THEN 1 ELSE 0 END) AS examined_items,SUM(CASE WHEN result IN ('direct','inferred') THEN 1 ELSE 0 END) AS accepted_dumps,SUM(CASE WHEN result IN ('rejected','unresolved','direct-unavailable','conflict') THEN 1 ELSE 0 END) AS review_items,SUM(CASE WHEN result='failed' THEN 1 ELSE 0 END) AS failed_items,ROUND(100.0*SUM(CASE WHEN result<>'pending' THEN 1 ELSE 0 END)/COUNT(*),2) AS percent_examined FROM export_scope GROUP BY task_key,entity_type,id_area");
                // Versions before 0.0.0.2 could write a successful reverse probe with a NULL
                // TVDB id. NULLs are distinct in SQLite composite keys, so those rows were not
                // replaced by the corrected probe result. They contain no useful mapping.
                db.Execute("DELETE FROM id_probe WHERE direction='imdb-to-tvdb' AND (tvdb_id IS NULL OR trim(tvdb_id)='')");
                db.Execute("CREATE VIEW IF NOT EXISTS searchable_media AS SELECT e.emby_id,e.emby_guid,e.item_type,e.name AS emby_name,e.production_year,e.tvdb_id,e.imdb_id,e.tmdb_id,t.name AS tvdb_name,t.fetched_utc FROM emby_item e LEFT JOIN tvdb_entity t ON t.tvdb_id=e.tvdb_id AND t.entity_type=e.item_type");
                // Older builds wrapped entity 404s in AggregateException, leaving a failed
                // fetch-cache row rather than the intended direct-unavailable classification.
                db.Execute("UPDATE item_resolution SET provenance='direct-unavailable',method='tvdb-404',confidence=0.0,evidence_json='{\"evidence\":\"Emby has this TVDB id, but TVDB returned HTTP 404. Human review recommended; Emby was not modified.\"}',evaluated_utc=datetime('now') WHERE observed_tvdb_id IS NOT NULL AND EXISTS(SELECT 1 FROM fetch_cache f WHERE f.cache_key=item_resolution.entity_type||':'||item_resolution.observed_tvdb_id AND f.state='failed' AND lower(f.error) LIKE '%notfound%')");
                db.Execute("UPDATE export_scope SET result='direct-unavailable',updated_utc=datetime('now') WHERE EXISTS(SELECT 1 FROM item_resolution r WHERE r.emby_id=export_scope.emby_id AND r.provenance='direct-unavailable')");
                logger.Info("TVDB Archive database initialized at {0}", DatabasePath);
            }
        }

        public bool IsDue(string key)
        {
            lock (sync) using (var s = db.PrepareStatement("SELECT next_attempt_utc FROM fetch_cache WHERE cache_key=@key"))
            { s.TryBind("@key", key); foreach (var row in s.ExecuteQuery()) return DateTimeOffset.Parse(row.GetString(0), CultureInfo.InvariantCulture) <= DateTimeOffset.UtcNow; }
            return true;
        }

        public bool TryGetApiResponse(string requestPath, out string responseJson)
        {
            lock (sync) using (var s = db.PrepareStatement("SELECT response_json FROM api_response_cache WHERE request_path=@path AND expires_utc>@now"))
            {
                s.TryBind("@path", requestPath); s.TryBind("@now", Now());
                foreach (var row in s.ExecuteQuery()) { responseJson = row.GetString(0); return true; }
            }
            responseJson = null; return false;
        }

        public void SaveApiResponse(string requestPath, string responseJson)
        {
            var expires = DateTimeOffset.UtcNow.AddDays(Math.Max(1, Plugin.Instance.Configuration.SuccessCacheDays));
            Execute("INSERT OR REPLACE INTO api_response_cache VALUES(@path,@json,@now,@expires)", s => { s.TryBind("@path", requestPath); s.TryBind("@json", responseJson); s.TryBind("@now", Now()); s.TryBind("@expires", expires.ToString("O")); });
        }

        public void SaveEmby(long id, string guid, string type, string name, int? year, long? parent, string tvdb, string imdb, string tmdb, string path)
        {
            Execute("REPLACE INTO emby_item VALUES(@id,@guid,@type,@name,@year,@parent,@tvdb,@imdb,@tmdb,@path,@now)", s =>
            { s.TryBind("@id", id); s.TryBind("@guid", guid); s.TryBind("@type", type); s.TryBind("@name", name); s.TryBind("@year", year); s.TryBind("@parent", parent); s.TryBind("@tvdb", tvdb); s.TryBind("@imdb", imdb); s.TryBind("@tmdb", tmdb); s.TryBind("@path", path); s.TryBind("@now", Now()); });
        }

        public void SaveEntity(string id, string type, Tvdb.EntityData d, string raw)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "REPLACE INTO tvdb_entity VALUES(@id,@type,@name,@slug,@birth,@death,@place,@first,@last,@country,@lang,@raw,@now)", s =>
                { s.TryBind("@id", id); s.TryBind("@type", type); s.TryBind("@name", d.name); s.TryBind("@slug", d.slug); s.TryBind("@birth", d.birth); s.TryBind("@death", d.death); s.TryBind("@place", d.birthPlace); s.TryBind("@first", d.firstAired); s.TryBind("@last", d.lastAired); s.TryBind("@country", d.originalCountry); s.TryBind("@lang", d.originalLanguage); s.TryBind("@raw", raw); s.TryBind("@now", Now()); });
                x.Execute("DELETE FROM remote_id WHERE tvdb_id='" + id.Replace("'", "''") + "' AND entity_type='" + type.Replace("'", "''") + "'");
                foreach (var r in d.remoteIds ?? new System.Collections.Generic.List<Tvdb.RemoteIdData>())
                    Statement(x, "INSERT OR REPLACE INTO remote_id VALUES(@id,@type,@source,@remote,@stype)", s => { s.TryBind("@id", id); s.TryBind("@type", type); s.TryBind("@source", r.sourceName ?? "unknown"); s.TryBind("@remote", r.id); s.TryBind("@stype", r.type); });
                SaveCredits(x, id, type, d.characters);
            }, TransactionMode.Immediate);
        }

        public void SaveEpisodeBatch(string seriesId, Tvdb.EpisodesData data)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                var episodes = (data.episodes ?? new System.Collections.Generic.List<Tvdb.EpisodeData>()).Where(e => e.seasonNumber >= 1).ToList();
                var episodeIds = new HashSet<int>(episodes.Select(e => e.id));
                foreach (var e in episodes)
                {
                    var episodeId = e.id.ToString(CultureInfo.InvariantCulture);
                    Statement(x, "INSERT OR IGNORE INTO tvdb_entity(tvdb_id,entity_type,name,first_aired,raw_json,fetched_utc) VALUES(@id,'episode',@name,@aired,NULL,@now)", s => { s.TryBind("@id", episodeId); s.TryBind("@name", e.name); s.TryBind("@aired", e.aired); s.TryBind("@now", Now()); });
                    Statement(x, "UPDATE tvdb_entity SET name=@name,first_aired=@aired,fetched_utc=@now WHERE tvdb_id=@id AND entity_type='episode'", s => { s.TryBind("@id", episodeId); s.TryBind("@name", e.name); s.TryBind("@aired", e.aired); s.TryBind("@now", Now()); });
                    foreach (var r in e.remoteIds ?? new List<Tvdb.RemoteIdData>())
                        Statement(x, "INSERT OR REPLACE INTO remote_id VALUES(@id,'episode',@source,@remote,@stype)", s => { s.TryBind("@id", episodeId); s.TryBind("@source", r.sourceName ?? "unknown"); s.TryBind("@remote", r.id); s.TryBind("@stype", r.type); });
                }
                SaveCredits(x, seriesId, "series", (data.characters ?? new List<Tvdb.CharacterData>()).Where(c => c.episodeId.HasValue && episodeIds.Contains(c.episodeId.Value)).ToList());
            }, TransactionMode.Immediate);
        }

        private static void SaveCredits(IDatabaseConnection x, string subject, string type, System.Collections.Generic.List<Tvdb.CharacterData> credits)
        {
            foreach (var c in (credits ?? new System.Collections.Generic.List<Tvdb.CharacterData>()).Where(Tvdb.TvdbScope.IsScreenCredit))
            {
                var productionId = subject;
                var productionType = type;
                var personId = c.peopleId.ToString(CultureInfo.InvariantCulture);
                if (type == "person")
                {
                    personId = subject;
                    if (c.episodeId.HasValue) { productionId = c.episodeId.Value.ToString(CultureInfo.InvariantCulture); productionType = "episode"; }
                    else if (c.movieId.HasValue) { productionId = c.movieId.Value.ToString(CultureInfo.InvariantCulture); productionType = "movie"; }
                    else if (c.seriesId.HasValue) { productionId = c.seriesId.Value.ToString(CultureInfo.InvariantCulture); productionType = "series"; }
                }
                Statement(x, "INSERT OR REPLACE INTO credit VALUES(@subject,@type,@person,@character,@episode,@pname,@role,@ctype,@sort,@featured)", s => { s.TryBind("@subject", productionId); s.TryBind("@type", productionType); s.TryBind("@person", personId); s.TryBind("@character", c.id); s.TryBind("@episode", c.episodeId?.ToString(CultureInfo.InvariantCulture)); s.TryBind("@pname", c.personName); s.TryBind("@role", c.name); s.TryBind("@ctype", c.peopleType); s.TryBind("@sort", c.sort); s.TryBind("@featured", c.isFeatured ? 1 : 0); });
            }
        }

        public void MarkFetch(string key, bool success, string error)
        {
            var cfg = Plugin.Instance.Configuration;
            var next = DateTimeOffset.UtcNow.Add(success ? TimeSpan.FromDays(Math.Max(1, cfg.SuccessCacheDays)) : TimeSpan.FromMinutes(Math.Max(1, cfg.FailureRetryMinutes)));
            Execute("INSERT OR REPLACE INTO fetch_cache VALUES(@key,@state,NULL,COALESCE((SELECT attempt_count+1 FROM fetch_cache WHERE cache_key=@key),1),@now,@next,@error)", s => { s.TryBind("@key", key); s.TryBind("@state", success ? "success" : "failed"); s.TryBind("@now", Now()); s.TryBind("@next", next.ToString("O")); s.TryBind("@error", error); });
        }

        public void SetRun(string key, string status, int total, int processed, int successes, int failures, long? lastId, string message)
        {
            Execute("INSERT OR REPLACE INTO run_state VALUES(@key,@status,COALESCE((SELECT started_utc FROM run_state WHERE task_key=@key),@now),@now,CASE WHEN @status IN ('completed','cancelled','failed') THEN @now ELSE NULL END,@total,@processed,@success,@failure,@last,@message)", s => { s.TryBind("@key", key); s.TryBind("@status", status); s.TryBind("@now", Now()); s.TryBind("@total", total); s.TryBind("@processed", processed); s.TryBind("@success", successes); s.TryBind("@failure", failures); s.TryBind("@last", lastId); s.TryBind("@message", message); });
        }

        public Tuple<int, int, int, int> GetResumeCheckpoint(string key, int expectedTotal)
        {
            lock (sync) using (var s = db.PrepareStatement("SELECT status,total_items,processed_items,success_count,failure_count FROM run_state WHERE task_key=@key"))
            {
                s.TryBind("@key", key);
                foreach (var row in s.ExecuteQuery())
                {
                    var status = row.GetString(0);
                    var total = row.GetInt(1);
                    if (total == expectedTotal && (status == "cancelled" || status == "running" || status == "failed"))
                        return Tuple.Create(row.GetInt(2), row.GetInt(3), row.GetInt(4), total);
                }
            }
            return Tuple.Create(0, 0, 0, expectedTotal);
        }

        public void SeedExportScope(string taskKey, IEnumerable<Tuple<long, string, bool, int>> items)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                foreach (var item in items)
                    Statement(x, "INSERT OR IGNORE INTO export_scope(task_key,emby_id,ordinal,entity_type,id_area,result,updated_utc) VALUES(@task,@emby,@ordinal,@type,@area,'pending',@now)", s =>
                    {
                        s.TryBind("@task", taskKey); s.TryBind("@emby", item.Item1); s.TryBind("@ordinal", item.Item4);
                        s.TryBind("@type", item.Item2); s.TryBind("@area", item.Item3 ? "has-tvdb-id" : "missing-tvdb-id"); s.TryBind("@now", Now());
                    });
                Statement(x, "UPDATE export_scope SET result=COALESCE((SELECT provenance FROM item_resolution r WHERE r.emby_id=export_scope.emby_id),'pending'),updated_utc=@now WHERE task_key=@task AND result='pending'", s => { s.TryBind("@task", taskKey); s.TryBind("@now", Now()); });
            }, TransactionMode.Immediate);
        }

        public void SetExportScopeResult(string taskKey, long embyId, string result)
        {
            Execute("UPDATE export_scope SET result=@result,updated_utc=@now WHERE task_key=@task AND emby_id=@emby", s =>
            { s.TryBind("@result", result); s.TryBind("@now", Now()); s.TryBind("@task", taskKey); s.TryBind("@emby", embyId); });
        }

        public string GetAcceptedResolvedTvdbId(long embyId)
        {
            lock (sync) using (var s = db.PrepareStatement("SELECT resolved_tvdb_id FROM item_resolution WHERE emby_id=@emby AND provenance IN ('direct','inferred') LIMIT 1"))
            {
                s.TryBind("@emby", embyId);
                foreach (var row in s.ExecuteQuery()) return row.IsDBNull(0) ? null : row.GetString(0);
            }
            return null;
        }

        public bool HasSuccessfulPreview()
        {
            lock (sync) using (var s = db.PrepareStatement("SELECT 1 FROM run_state WHERE task_key='TvdbArchivePreview' AND status='completed' LIMIT 1")) foreach (var ignored in s.ExecuteQuery()) return true;
            return false;
        }

        public Tuple<int, int, int> GetCastIdCoverage()
        {
            lock (sync)
            {
                var total = Scalar("SELECT COUNT(DISTINCT person_tvdb_id) FROM credit");
                var imdb = Scalar("SELECT COUNT(DISTINCT c.person_tvdb_id) FROM credit c JOIN remote_id r ON r.tvdb_id=c.person_tvdb_id AND r.entity_type='person' WHERE lower(r.source_name) LIKE '%imdb%'");
                var tmdb = Scalar("SELECT COUNT(DISTINCT c.person_tvdb_id) FROM credit c JOIN remote_id r ON r.tvdb_id=c.person_tvdb_id AND r.entity_type='person' WHERE lower(r.source_name) LIKE '%movie%db%'");
                return Tuple.Create(total, tmdb, imdb);
            }
        }

        public void SaveProbe(string direction, string type, string input, string tvdb, string imdb, string name, bool success, string raw)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "DELETE FROM id_probe WHERE direction=@direction AND entity_type=@type AND input_id=@input", s => { s.TryBind("@direction", direction); s.TryBind("@type", type); s.TryBind("@input", input); });
                Statement(x, "INSERT INTO id_probe VALUES(@direction,@type,@input,@tvdb,@imdb,@name,@success,@now,@raw)", s => { s.TryBind("@direction", direction); s.TryBind("@type", type); s.TryBind("@input", input); s.TryBind("@tvdb", tvdb); s.TryBind("@imdb", imdb); s.TryBind("@name", name); s.TryBind("@success", success ? 1 : 0); s.TryBind("@now", Now()); s.TryBind("@raw", raw); });
            }, TransactionMode.Immediate);
        }

        public void SaveResolutionEvaluation(long embyId, string type, string name, string truth, string predicted, string method, double confidence, int candidates, bool correct, string evidence)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "DELETE FROM resolution_evaluation WHERE emby_id=@emby", s => s.TryBind("@emby", embyId));
                Statement(x, "INSERT INTO resolution_evaluation VALUES(@emby,@type,@name,@truth,@predicted,@method,@confidence,@candidates,@correct,@evidence,@now)", s => { s.TryBind("@emby", embyId); s.TryBind("@type", type); s.TryBind("@name", name); s.TryBind("@truth", truth); s.TryBind("@predicted", predicted); s.TryBind("@method", method); s.TryBind("@confidence", confidence); s.TryBind("@candidates", candidates); s.TryBind("@correct", correct ? 1 : 0); s.TryBind("@evidence", evidence); s.TryBind("@now", Now()); });
            }, TransactionMode.Immediate);
        }

        public void SaveItemResolution(long embyId, string type, string observed, string resolved, string provenance, string method, double confidence, int candidates, string evidence)
        {
            Execute("INSERT OR REPLACE INTO item_resolution VALUES(@emby,@type,@observed,@resolved,@provenance,@method,@confidence,@candidates,@evidence,@now)", s => { s.TryBind("@emby", embyId); s.TryBind("@type", type); s.TryBind("@observed", observed); s.TryBind("@resolved", resolved); s.TryBind("@provenance", provenance); s.TryBind("@method", method); s.TryBind("@confidence", confidence); s.TryBind("@candidates", candidates); s.TryBind("@evidence", evidence); s.TryBind("@now", Now()); });
        }

        public void SaveResolutionCandidates(long embyId, IEnumerable<Tvdb.ResolutionCandidate> candidates, Func<object, string> serialize)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "DELETE FROM resolution_candidate WHERE emby_id=@emby", s => s.TryBind("@emby", embyId));
                foreach (var c in candidates ?? Enumerable.Empty<Tvdb.ResolutionCandidate>())
                    Statement(x, "INSERT INTO resolution_candidate VALUES(@emby,@rank,@type,@tvdb,@name,@score,@external,@filmography,@evidence,@now)", s => { s.TryBind("@emby", embyId); s.TryBind("@rank", c.Rank); s.TryBind("@type", c.EntityType); s.TryBind("@tvdb", c.TvdbId); s.TryBind("@name", c.Name); s.TryBind("@score", c.Score); s.TryBind("@external", serialize(c.ExternalIds)); s.TryBind("@filmography", serialize(c.FilmographyIds)); s.TryBind("@evidence", c.Evidence); s.TryBind("@now", Now()); });
            }, TransactionMode.Immediate);
        }

        private int Scalar(string sql) { using (var s = db.PrepareStatement(sql)) foreach (var row in s.ExecuteQuery()) return row.GetInt(0); return 0; }

        private void Execute(string sql, Action<IStatement> bind) { lock (sync) Statement(db, sql, bind); }
        private static void Statement(IDatabaseConnection connection, string sql, Action<IStatement> bind) { using (var s = connection.PrepareStatement(sql)) { bind(s); s.MoveNext(); } }
        private static string Now() => DateTimeOffset.UtcNow.ToString("O");
        public void Dispose() { lock (sync) { db?.Dispose(); db = null; } }
    }
}
