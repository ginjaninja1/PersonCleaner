using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;
using PersonCleaner.Tmdb;
using SQLitePCL.pretty;
using SQLitePCLEx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PersonCleaner.Storage
{
    internal sealed class TmdbArchiveRepository : IDisposable
    {
        private readonly object sync = new object();
        private readonly ILogger logger;
        private IDatabaseConnection db;
        public string DatabasePath { get; }

        public TmdbArchiveRepository(IApplicationPaths paths, ILogger logger) { this.logger = logger; DatabasePath = ArchiveDatabase.ResolvePath(paths); }

        public void Initialize()
        {
            lock (sync)
            {
                if (db != null) return;
                db = SQLite3.Open(DatabasePath, ConnectionFlags.Create | ConnectionFlags.ReadWrite | ConnectionFlags.PrivateCache | ConnectionFlags.NoMutex, null,
                    new Dictionary<string, delegate_collation>(), new Dictionary<Tuple<string, int>, Action<IReadOnlyList<sqlite3_value>, sqlite3_context>>(), true, false);
                db.Execute("PRAGMA busy_timeout=30000");
                db.Execute("PRAGMA journal_mode=WAL"); db.Execute("PRAGMA synchronous=NORMAL");
                db.Execute("CREATE TABLE IF NOT EXISTS tmdb_schema_info(version INTEGER NOT NULL)");
                db.Execute("INSERT INTO tmdb_schema_info(version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM tmdb_schema_info)");
                db.Execute("CREATE TABLE IF NOT EXISTS tmdb_entity(tmdb_id TEXT NOT NULL,entity_type TEXT NOT NULL,name TEXT,original_name TEXT,birth_date TEXT,death_date TEXT,birth_place TEXT,first_date TEXT,season_number INTEGER,episode_number INTEGER,raw_json TEXT NOT NULL,fetched_utc TEXT NOT NULL,PRIMARY KEY(tmdb_id,entity_type))");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_tmdb_entity_name ON tmdb_entity(name,entity_type)");
                db.Execute("CREATE TABLE IF NOT EXISTS tmdb_external_id(tmdb_id TEXT NOT NULL,entity_type TEXT NOT NULL,source_name TEXT NOT NULL,external_id TEXT NOT NULL,PRIMARY KEY(tmdb_id,entity_type,source_name,external_id))");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_tmdb_external_value ON tmdb_external_id(external_id,source_name,entity_type)");
                db.Execute("CREATE TABLE IF NOT EXISTS tmdb_alias(tmdb_id TEXT NOT NULL,entity_type TEXT NOT NULL,alias TEXT NOT NULL,country TEXT NOT NULL DEFAULT '',alias_type TEXT NOT NULL DEFAULT '',PRIMARY KEY(tmdb_id,entity_type,alias,country,alias_type))");
                db.Execute("CREATE TABLE IF NOT EXISTS tmdb_credit(person_tmdb_id TEXT NOT NULL,production_tmdb_id TEXT NOT NULL,production_type TEXT NOT NULL,credit_id TEXT NOT NULL DEFAULT '',credit_kind TEXT NOT NULL,job_or_character TEXT NOT NULL DEFAULT '',department TEXT,episode_count INTEGER,production_name TEXT,first_date TEXT,PRIMARY KEY(person_tmdb_id,production_tmdb_id,production_type,credit_id,credit_kind,job_or_character))");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_tmdb_credit_production ON tmdb_credit(production_tmdb_id,production_type)");
                db.Execute("CREATE TABLE IF NOT EXISTS tmdb_credit_observation(source_entity_type TEXT NOT NULL,source_tmdb_id TEXT NOT NULL,person_tmdb_id TEXT NOT NULL,production_tmdb_id TEXT NOT NULL,production_type TEXT NOT NULL,credit_id TEXT NOT NULL DEFAULT '',credit_kind TEXT NOT NULL,job_or_character TEXT NOT NULL DEFAULT '',department TEXT,episode_count INTEGER,production_name TEXT,first_date TEXT,observed_utc TEXT NOT NULL,PRIMARY KEY(source_entity_type,source_tmdb_id,person_tmdb_id,production_tmdb_id,production_type,credit_id,credit_kind,job_or_character))");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_tmdb_credit_observation_person ON tmdb_credit_observation(person_tmdb_id,source_entity_type)");
                db.Execute("CREATE INDEX IF NOT EXISTS ix_tmdb_credit_observation_production ON tmdb_credit_observation(production_tmdb_id,production_type,source_entity_type)");
                db.Execute("CREATE TABLE IF NOT EXISTS tmdb_item_resolution(emby_id INTEGER PRIMARY KEY,entity_type TEXT NOT NULL,observed_tmdb_id TEXT,resolved_tmdb_id TEXT,provenance TEXT NOT NULL,method TEXT NOT NULL,candidate_count INTEGER NOT NULL,evidence_json TEXT,evaluated_utc TEXT NOT NULL)");
                db.Execute("CREATE TABLE IF NOT EXISTS tmdb_resolution_candidate(emby_id INTEGER NOT NULL,rank INTEGER NOT NULL,entity_type TEXT NOT NULL,tmdb_id TEXT,name TEXT,source_external_id TEXT,raw_json TEXT,evaluated_utc TEXT NOT NULL,PRIMARY KEY(emby_id,rank))");
                db.Execute("CREATE TABLE IF NOT EXISTS tmdb_api_response_cache(request_path TEXT PRIMARY KEY,response_json TEXT NOT NULL,fetched_utc TEXT NOT NULL,expires_utc TEXT NOT NULL)");
                db.Execute("CREATE TABLE IF NOT EXISTS tmdb_api_response_archive(request_path TEXT NOT NULL,fetched_utc TEXT NOT NULL,response_json TEXT NOT NULL,PRIMARY KEY(request_path,fetched_utc))");
                db.Execute("CREATE TABLE IF NOT EXISTS tmdb_fetch_cache(cache_key TEXT PRIMARY KEY,state TEXT NOT NULL,attempt_count INTEGER NOT NULL,fetched_utc TEXT,next_attempt_utc TEXT NOT NULL,error TEXT)");
                db.Execute("UPDATE tmdb_fetch_cache SET state='not-found',next_attempt_utc=datetime('now','+30 days') WHERE state='failed' AND lower(COALESCE(error,'')) LIKE '%notfound%'");
                db.Execute("CREATE TABLE IF NOT EXISTS tmdb_run_state(task_key TEXT PRIMARY KEY,status TEXT NOT NULL,started_utc TEXT,updated_utc TEXT NOT NULL,finished_utc TEXT,total_items INTEGER NOT NULL,processed_items INTEGER NOT NULL,success_count INTEGER NOT NULL,failure_count INTEGER NOT NULL,last_emby_id INTEGER,message TEXT)");
                db.Execute("CREATE VIEW IF NOT EXISTS provider_identity_signals AS SELECT e.emby_id,e.item_type,e.name AS emby_name,e.imdb_id,e.tvdb_id AS emby_tvdb_id,e.tmdb_id AS emby_tmdb_id,r.resolved_tvdb_id,r.provenance AS tvdb_provenance,tr.resolved_tmdb_id,tr.provenance AS tmdb_provenance,te.name AS tvdb_name,me.name AS tmdb_name FROM emby_item e LEFT JOIN item_resolution r ON r.emby_id=e.emby_id LEFT JOIN tvdb_entity te ON te.tvdb_id=r.resolved_tvdb_id AND te.entity_type=e.item_type LEFT JOIN tmdb_item_resolution tr ON tr.emby_id=e.emby_id LEFT JOIN tmdb_entity me ON me.tmdb_id=tr.resolved_tmdb_id AND me.entity_type=e.item_type");
                db.Execute("DROP VIEW IF EXISTS provider_entity");
                db.Execute("CREATE VIEW provider_entity AS SELECT 'tvdb' AS provider,tvdb_id AS provider_id,entity_type,name,name AS original_name,birth_date,death_date,birth_place,first_aired AS first_date,fetched_utc FROM tvdb_entity UNION ALL SELECT 'tmdb',tmdb_id,entity_type,name,original_name,birth_date,death_date,birth_place,first_date,fetched_utc FROM tmdb_entity");
                db.Execute("DROP VIEW IF EXISTS provider_external_id");
                db.Execute("CREATE VIEW provider_external_id AS SELECT 'tvdb' AS provider,tvdb_id AS provider_id,entity_type,source_name,remote_id AS external_id,source_type FROM remote_id UNION ALL SELECT 'tmdb',tmdb_id,entity_type,source_name,external_id,NULL FROM tmdb_external_id");
                db.Execute("DROP VIEW IF EXISTS provider_alias");
                db.Execute("CREATE VIEW provider_alias AS SELECT 'tvdb' AS provider,tvdb_id AS provider_id,entity_type,alias,language AS locale,alias_type FROM tvdb_alias UNION ALL SELECT 'tmdb',tmdb_id,entity_type,alias,country,alias_type FROM tmdb_alias");
                db.Execute("DROP VIEW IF EXISTS provider_credit_observation");
                db.Execute("CREATE VIEW provider_credit_observation AS SELECT 'tvdb' AS provider,source_entity_type,source_tvdb_id AS source_provider_id,person_tvdb_id AS person_provider_id,production_tvdb_id AS production_provider_id,production_type,CAST(character_id AS TEXT) AS credit_id,CASE WHEN credit_type IN ('Actor','Guest Star') THEN 'cast' ELSE 'crew' END AS credit_kind,role_name AS job_or_character,credit_type AS department,NULL AS episode_count,observed_utc FROM tvdb_credit_observation UNION ALL SELECT 'tmdb',source_entity_type,source_tmdb_id,person_tmdb_id,production_tmdb_id,production_type,credit_id,credit_kind,job_or_character,department,episode_count,observed_utc FROM tmdb_credit_observation");
                logger.Info("TMDB archive schema initialized at {0}", DatabasePath);
            }
        }

        public bool TryGetApiResponse(string path, out string raw)
        {
            lock (sync) using (var s = db.PrepareStatement("SELECT response_json FROM tmdb_api_response_cache WHERE request_path=@path AND expires_utc>@now"))
            { s.TryBind("@path", path); s.TryBind("@now", Now()); foreach (var row in s.ExecuteQuery()) { raw = row.GetString(0); return true; } }
            raw = null; return false;
        }

        public void SaveApiResponse(string path, string raw)
        {
            var now = Now(); var expires = DateTimeOffset.UtcNow.AddDays(Math.Max(1, Plugin.Instance.Configuration.SuccessCacheDays)).ToString("O");
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "INSERT OR IGNORE INTO tmdb_api_response_archive SELECT request_path,fetched_utc,response_json FROM tmdb_api_response_cache WHERE request_path=@path", s => s.TryBind("@path", path));
                Statement(x, "INSERT OR REPLACE INTO tmdb_api_response_cache VALUES(@path,@raw,@now,@expires)", s => { s.TryBind("@path", path); s.TryBind("@raw", raw); s.TryBind("@now", now); s.TryBind("@expires", expires); });
            }, TransactionMode.Immediate);
        }

        public bool IsDue(string key)
        {
            lock (sync) using (var s = db.PrepareStatement("SELECT next_attempt_utc FROM tmdb_fetch_cache WHERE cache_key=@key"))
            { s.TryBind("@key", key); foreach (var row in s.ExecuteQuery()) return DateTimeOffset.Parse(row.GetString(0), CultureInfo.InvariantCulture) <= DateTimeOffset.UtcNow; }
            return true;
        }

        public void MarkFetch(string key, bool success, string error)
        {
            var next = DateTimeOffset.UtcNow.Add(success ? TimeSpan.FromDays(Math.Max(1, Plugin.Instance.Configuration.SuccessCacheDays)) : TimeSpan.FromMinutes(Math.Max(1, Plugin.Instance.Configuration.FailureRetryMinutes)));
            Execute("INSERT OR REPLACE INTO tmdb_fetch_cache VALUES(@key,@state,COALESCE((SELECT attempt_count+1 FROM tmdb_fetch_cache WHERE cache_key=@key),1),@now,@next,@error)", s => { s.TryBind("@key", key); s.TryBind("@state", success ? "success" : "failed"); s.TryBind("@now", Now()); s.TryBind("@next", next.ToString("O")); s.TryBind("@error", error); });
        }

        public void MarkNotFound(string key, string error)
        {
            var next = DateTimeOffset.UtcNow.AddDays(Math.Max(1, Plugin.Instance.Configuration.SuccessCacheDays));
            Execute("INSERT OR REPLACE INTO tmdb_fetch_cache VALUES(@key,'not-found',COALESCE((SELECT attempt_count+1 FROM tmdb_fetch_cache WHERE cache_key=@key),1),@now,@next,@error)", s => { s.TryBind("@key", key); s.TryBind("@now", Now()); s.TryBind("@next", next.ToString("O")); s.TryBind("@error", error); });
        }

        public void SaveEntity(string id, string type, TmdbEntity entity, string raw)
        {
            var name = entity.name ?? entity.title; var original = entity.original_name ?? entity.original_title;
            var first = entity.first_air_date ?? entity.release_date ?? entity.air_date;
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "INSERT OR REPLACE INTO tmdb_entity VALUES(@id,@type,@name,@original,@birth,@death,@place,@first,@season,@episode,@raw,@now)", s => { s.TryBind("@id", id); s.TryBind("@type", type); s.TryBind("@name", name); s.TryBind("@original", original); s.TryBind("@birth", entity.birthday); s.TryBind("@death", entity.deathday); s.TryBind("@place", entity.place_of_birth); s.TryBind("@first", first); s.TryBind("@season", entity.season_number); s.TryBind("@episode", entity.episode_number); s.TryBind("@raw", raw); s.TryBind("@now", Now()); });
                Statement(x, "DELETE FROM tmdb_external_id WHERE tmdb_id=@id AND entity_type=@type", s => { s.TryBind("@id", id); s.TryBind("@type", type); });
                SaveExternal(x, id, type, "imdb", entity.external_ids?.imdb_id); SaveExternal(x, id, type, "tvdb", entity.external_ids?.tvdb_id); SaveExternal(x, id, type, "wikidata", entity.external_ids?.wikidata_id);
                SaveExternal(x, id, type, "facebook", entity.external_ids?.facebook_id); SaveExternal(x, id, type, "instagram", entity.external_ids?.instagram_id); SaveExternal(x, id, type, "twitter", entity.external_ids?.twitter_id); SaveExternal(x, id, type, "tiktok", entity.external_ids?.tiktok_id); SaveExternal(x, id, type, "youtube", entity.external_ids?.youtube_id);
                Statement(x, "DELETE FROM tmdb_alias WHERE tmdb_id=@id AND entity_type=@type", s => { s.TryBind("@id", id); s.TryBind("@type", type); });
                foreach (var alias in (entity.alternative_names?.results ?? new List<TmdbAlias>())
                    .Concat(entity.alternative_titles?.results ?? new List<TmdbAlias>())
                    .Concat(entity.alternative_titles?.titles ?? new List<TmdbAlias>()))
                    Statement(x, "INSERT OR IGNORE INTO tmdb_alias VALUES(@id,@type,@alias,@country,@atype)", s => { s.TryBind("@id", id); s.TryBind("@type", type); s.TryBind("@alias", alias.name ?? alias.title); s.TryBind("@country", alias.iso_3166_1 ?? ""); s.TryBind("@atype", alias.type ?? ""); });
                foreach (var alias in entity.also_known_as ?? new List<string>())
                    if (!string.IsNullOrWhiteSpace(alias)) Statement(x, "INSERT OR IGNORE INTO tmdb_alias VALUES(@id,@type,@alias,'','also_known_as')", s => { s.TryBind("@id", id); s.TryBind("@type", type); s.TryBind("@alias", alias); });
                SaveCredits(x, id, type, entity.combined_credits ?? entity.aggregate_credits ?? entity.credits);
            }, TransactionMode.Immediate);
        }

        private static void SaveExternal(IDatabaseConnection x, string id, string type, string source, string value)
        { if (!string.IsNullOrWhiteSpace(value)) Statement(x, "INSERT OR REPLACE INTO tmdb_external_id VALUES(@id,@type,@source,@value)", s => { s.TryBind("@id", id); s.TryBind("@type", type); s.TryBind("@source", source); s.TryBind("@value", value); }); }

        private static void SaveCredits(IDatabaseConnection x, string subjectId, string subjectType, TmdbCredits credits)
        {
            if (credits == null) return;
            Statement(x, "DELETE FROM tmdb_credit_observation WHERE source_entity_type=@type AND source_tmdb_id=@id", s => { s.TryBind("@type", subjectType); s.TryBind("@id", subjectId); });
            if (subjectType == "person") Statement(x, "DELETE FROM tmdb_credit WHERE person_tmdb_id=@id", s => s.TryBind("@id", subjectId));
            else Statement(x, "DELETE FROM tmdb_credit WHERE production_tmdb_id=@id AND production_type=@type", s => { s.TryBind("@id", subjectId); s.TryBind("@type", subjectType); });
            foreach (var c in credits.cast ?? new List<TmdbCredit>()) SaveCredit(x, subjectId, subjectType, c, "cast", c.character);
            foreach (var c in credits.crew ?? new List<TmdbCredit>()) SaveCredit(x, subjectId, subjectType, c, "crew", c.job);
        }

        private static void SaveCredit(IDatabaseConnection x, string subjectId, string subjectType, TmdbCredit c, string kind, string role)
        {
            var personId = subjectType == "person" ? subjectId : c.id.ToString(CultureInfo.InvariantCulture);
            var productionId = subjectType == "person" ? c.id.ToString(CultureInfo.InvariantCulture) : subjectId;
            var productionType = subjectType == "person" ? (c.media_type == "tv" ? "series" : c.media_type ?? "movie") : subjectType;
            var productionName = subjectType == "person" ? c.name ?? c.title : null;
            var first = c.first_air_date ?? c.release_date;
            var roles = kind != "cast" ? Enumerable.Empty<Tuple<string, string, int?>>() : c.roles != null && c.roles.Count > 0 ? c.roles.Select(r => Tuple.Create(r.credit_id, r.character, r.episode_count)) : new[] { Tuple.Create(c.credit_id, role, c.episode_count) };
            var jobs = kind != "crew" ? Enumerable.Empty<Tuple<string, string, int?>>() : c.jobs != null && c.jobs.Count > 0 ? c.jobs.Select(j => Tuple.Create(j.credit_id, j.job, j.episode_count)) : new[] { Tuple.Create(c.credit_id, role, c.episode_count) };
            foreach (var value in roles.Concat(jobs))
            {
                Statement(x, "INSERT OR REPLACE INTO tmdb_credit VALUES(@person,@production,@ptype,@credit,@kind,@role,@department,@episodes,@name,@first)", s => { s.TryBind("@person", personId); s.TryBind("@production", productionId); s.TryBind("@ptype", productionType); s.TryBind("@credit", value.Item1 ?? ""); s.TryBind("@kind", kind); s.TryBind("@role", value.Item2 ?? ""); s.TryBind("@department", c.department); s.TryBind("@episodes", value.Item3); s.TryBind("@name", productionName); s.TryBind("@first", first); });
                Statement(x, "INSERT OR REPLACE INTO tmdb_credit_observation VALUES(@sourceType,@sourceId,@person,@production,@ptype,@credit,@kind,@role,@department,@episodes,@name,@first,@now)", s => { s.TryBind("@sourceType", subjectType); s.TryBind("@sourceId", subjectId); s.TryBind("@person", personId); s.TryBind("@production", productionId); s.TryBind("@ptype", productionType); s.TryBind("@credit", value.Item1 ?? ""); s.TryBind("@kind", kind); s.TryBind("@role", value.Item2 ?? ""); s.TryBind("@department", c.department); s.TryBind("@episodes", value.Item3); s.TryBind("@name", productionName); s.TryBind("@first", first); s.TryBind("@now", Now()); });
            }
        }

        public void SaveResolution(long embyId, string type, string observed, string resolved, string provenance, string method, int candidateCount, string evidence)
        { Execute("INSERT OR REPLACE INTO tmdb_item_resolution VALUES(@emby,@type,@observed,@resolved,@provenance,@method,@count,@evidence,@now)", s => { s.TryBind("@emby", embyId); s.TryBind("@type", type); s.TryBind("@observed", observed); s.TryBind("@resolved", resolved); s.TryBind("@provenance", provenance); s.TryBind("@method", method); s.TryBind("@count", candidateCount); s.TryBind("@evidence", evidence); s.TryBind("@now", Now()); }); }

        public void SaveCandidates(long embyId, string type, string imdb, IEnumerable<TmdbEntity> candidates, Func<object, string> serialize)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "DELETE FROM tmdb_resolution_candidate WHERE emby_id=@emby", s => s.TryBind("@emby", embyId)); var rank = 0;
                foreach (var c in candidates ?? Enumerable.Empty<TmdbEntity>()) { rank++; Statement(x, "INSERT INTO tmdb_resolution_candidate VALUES(@emby,@rank,@type,@id,@name,@imdb,@raw,@now)", s => { s.TryBind("@emby", embyId); s.TryBind("@rank", rank); s.TryBind("@type", type); s.TryBind("@id", c.id.ToString(CultureInfo.InvariantCulture)); s.TryBind("@name", c.name ?? c.title); s.TryBind("@imdb", imdb); s.TryBind("@raw", serialize(c)); s.TryBind("@now", Now()); }); }
            }, TransactionMode.Immediate);
        }

        public Tuple<int, int, int> GetCheckpoint(string key, int total)
        {
            lock (sync) using (var s = db.PrepareStatement("SELECT status,total_items,processed_items,success_count,failure_count FROM tmdb_run_state WHERE task_key=@key"))
            { s.TryBind("@key", key); foreach (var r in s.ExecuteQuery()) if (r.GetInt(1) == total && new[] { "running", "cancelled", "failed" }.Contains(r.GetString(0))) return Tuple.Create(r.GetInt(2), r.GetInt(3), r.GetInt(4)); }
            return Tuple.Create(0, 0, 0);
        }

        public void SetRun(string key, string status, int total, int done, int success, int failure, long? last, string message)
        { Execute("INSERT OR REPLACE INTO tmdb_run_state VALUES(@key,@status,COALESCE((SELECT started_utc FROM tmdb_run_state WHERE task_key=@key),@now),@now,CASE WHEN @status IN ('completed','cancelled','failed') THEN @now ELSE NULL END,@total,@done,@success,@failure,@last,@message)", s => { s.TryBind("@key", key); s.TryBind("@status", status); s.TryBind("@now", Now()); s.TryBind("@total", total); s.TryBind("@done", done); s.TryBind("@success", success); s.TryBind("@failure", failure); s.TryBind("@last", last); s.TryBind("@message", message); }); }

        private void Execute(string sql, Action<IStatement> bind) { lock (sync) Statement(db, sql, bind); }
        private static void Statement(IDatabaseConnection connection, string sql, Action<IStatement> bind) { using (var s = connection.PrepareStatement(sql)) { bind(s); s.MoveNext(); } }
        private static string Now() => DateTimeOffset.UtcNow.ToString("O");
        public void Dispose() { lock (sync) { db?.Dispose(); db = null; } }
    }
}
