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
    internal sealed class TmdbRecoveryTarget { public long EmbyId { get; set; } public string Name { get; set; } public string CurrentId { get; set; } public string ImdbId { get; set; } public int LinkedCount { get; set; } }
    internal sealed class TmdbCrossProviderLead { public long EmbyId { get; set; } public string TmdbId { get; set; } public string TvdbId { get; set; } public string ImdbId { get; set; } }
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
                ArchiveDatabase.RequireExisting(DatabasePath);
                try
                {
                    db = SQLite3.Open(DatabasePath, ConnectionFlags.ReadWrite | ConnectionFlags.PrivateCache | ConnectionFlags.NoMutex, null,
                        new Dictionary<string, delegate_collation>(), new Dictionary<Tuple<string, int>, Action<IReadOnlyList<sqlite3_value>, sqlite3_context>>(), true, false);
                    db.Execute("PRAGMA busy_timeout=30000"); db.Execute("PRAGMA synchronous=NORMAL");
                    ArchiveDatabase.ValidateObjects(db, "TMDB", "tmdb_schema_info", "tmdb_entity", "tmdb_external_id", "tmdb_alias", "tmdb_credit", "tmdb_credit_observation", "tmdb_item_resolution", "tmdb_resolution_candidate", "tmdb_api_response_cache", "tmdb_api_response_archive", "tmdb_fetch_cache", "tmdb_run_state", "provider_identity_signals", "provider_entity", "provider_external_id", "provider_alias", "provider_credit_observation", "provider_production_evidence");
                    ArchiveDatabase.ValidateVersion(db, "TMDB", "tmdb_schema_info", 1);
                    ArchiveDatabase.ValidateMigrations(db, 7);
                }
                catch { db?.Dispose(); db = null; throw; }
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

        public bool IsNotFoundCached(string key)
        {
            lock(sync) using(var s=db.PrepareStatement("SELECT 1 FROM tmdb_fetch_cache WHERE cache_key=@key AND state='not-found' AND next_attempt_utc>@now LIMIT 1")){s.TryBind("@key",key);s.TryBind("@now",Now());foreach(var row in s.ExecuteQuery())return true;}return false;
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

        public List<TmdbRecoveryTarget> GetPersonEvidenceGapTargets()
        {
            var frozen=new List<TmdbRecoveryTarget>();lock(sync)
            {
                using(var cohort=db.PrepareStatement("WITH original AS (SELECT cache_key FROM tmdb_fetch_cache WHERE cache_key LIKE 'evidence-cohort:tmdb:%' AND state IN('cohort-active','success') ORDER BY fetched_utc,cache_key LIMIT 1000) SELECT p.emby_id,p.name,p.tmdb_id,p.imdb_id,count(DISTINCT er.media_emby_id) FROM original f JOIN emby_item p ON p.emby_id=cast(substr(f.cache_key,length('evidence-cohort:tmdb:')+1) AS integer) JOIN emby_relationship er ON er.person_emby_id=p.emby_id GROUP BY p.emby_id,p.name,p.tmdb_id,p.imdb_id ORDER BY p.emby_id"))foreach(var r in cohort.ExecuteQuery())frozen.Add(new TmdbRecoveryTarget{EmbyId=r.GetInt64(0),Name=r.GetString(1),CurrentId=r.IsDBNull(2)?null:r.GetString(2),ImdbId=r.IsDBNull(3)?null:r.GetString(3),LinkedCount=r.GetInt(4)});
            }
            if(frozen.Count>0)return frozen;
            var result=new List<TmdbRecoveryTarget>(); lock(sync) using(var s=db.PrepareStatement(@"WITH linked AS (SELECT person_emby_id,count(DISTINCT media_emby_id) linked FROM emby_relationship GROUP BY person_emby_id), duplicates AS (SELECT tmdb_id FROM emby_item WHERE item_type='person' AND tmdb_id IS NOT NULL GROUP BY tmdb_id HAVING count(*)>1), supported AS (SELECT p.emby_id,count(DISTINCT er.media_emby_id) media FROM emby_item p JOIN emby_relationship er ON er.person_emby_id=p.emby_id JOIN emby_item m ON m.emby_id=er.media_emby_id LEFT JOIN tmdb_item_resolution mr ON mr.emby_id=m.emby_id JOIN tmdb_credit c ON c.production_tmdb_id=coalesce(m.tmdb_id,mr.resolved_tmdb_id) AND c.production_type=m.item_type AND c.person_tmdb_id=p.tmdb_id WHERE p.item_type='person' AND p.tmdb_id IS NOT NULL GROUP BY p.emby_id), conflicts AS (SELECT DISTINCT p.emby_id FROM emby_item p JOIN remote_id rt ON rt.entity_type='person' AND rt.tvdb_id=p.tvdb_id AND rt.source_name='TheMovieDB.com' AND rt.remote_id<>p.tmdb_id JOIN remote_id ri ON ri.entity_type='person' AND ri.tvdb_id=p.tvdb_id AND ri.source_name='IMDB' AND ri.remote_id<>p.imdb_id WHERE p.item_type='person' AND p.tmdb_id IS NOT NULL AND p.tvdb_id IS NOT NULL AND p.imdb_id IS NOT NULL)
SELECT p.emby_id,p.name,p.tmdb_id,p.imdb_id,l.linked FROM emby_item p JOIN linked l ON l.person_emby_id=p.emby_id LEFT JOIN tmdb_item_resolution r ON r.emby_id=p.emby_id LEFT JOIN tmdb_entity pe ON pe.entity_type='person' AND pe.tmdb_id=p.tmdb_id LEFT JOIN duplicates d ON d.tmdb_id=p.tmdb_id LEFT JOIN supported s ON s.emby_id=p.emby_id LEFT JOIN conflicts cf ON cf.emby_id=p.emby_id WHERE p.item_type='person' AND (p.tmdb_id IS NULL OR r.emby_id IS NULL OR r.provenance IN('direct-unavailable','unresolved') OR (p.tmdb_id IS NOT NULL AND pe.tmdb_id IS NULL) OR d.tmdb_id IS NOT NULL OR coalesce(s.media,0)<l.linked) ORDER BY CASE WHEN cf.emby_id IS NOT NULL THEN 0 WHEN r.provenance='direct-unavailable' THEN 1 WHEN d.tmdb_id IS NOT NULL THEN 2 WHEN p.tmdb_id IS NULL THEN 4 ELSE 3 END,p.emby_id LIMIT 1000")){s.TryBind("@now",Now());foreach(var r in s.ExecuteQuery()) result.Add(new TmdbRecoveryTarget{EmbyId=r.GetInt64(0),Name=r.GetString(1),CurrentId=r.IsDBNull(2)?null:r.GetString(2),ImdbId=r.IsDBNull(3)?null:r.GetString(3),LinkedCount=r.GetInt(4)});} foreach(var target in result)MarkFetch("evidence-cohort:tmdb:"+target.EmbyId,true,"Frozen evaluation cohort"); return result;
        }
        public int GetLinkedSupport(long embyId,string candidateId)
        {
            lock(sync) using(var s=db.PrepareStatement("SELECT count(DISTINCT er.media_emby_id) FROM emby_relationship er JOIN emby_item m ON m.emby_id=er.media_emby_id LEFT JOIN tmdb_item_resolution mr ON mr.emby_id=m.emby_id JOIN tmdb_credit_observation c ON c.production_tmdb_id=coalesce(m.tmdb_id,mr.resolved_tmdb_id) AND c.person_tmdb_id=@candidate WHERE er.person_emby_id=@emby")){s.TryBind("@candidate",candidateId);s.TryBind("@emby",embyId);foreach(var r in s.ExecuteQuery())return r.GetInt(0);}return 0;
        }
        public List<string> GetTopLinkedCandidateIds(long embyId,string currentId,int limit)
        {
            var result=new List<string>(); lock(sync) using(var s=db.PrepareStatement("SELECT c.person_tmdb_id FROM emby_relationship er JOIN emby_item m ON m.emby_id=er.media_emby_id LEFT JOIN tmdb_item_resolution mr ON mr.emby_id=m.emby_id JOIN tmdb_credit_observation c ON c.production_tmdb_id=coalesce(m.tmdb_id,mr.resolved_tmdb_id) WHERE er.person_emby_id=@emby AND c.person_tmdb_id<>@current GROUP BY c.person_tmdb_id ORDER BY count(DISTINCT er.media_emby_id) DESC LIMIT @limit")){s.TryBind("@emby",embyId);s.TryBind("@current",currentId??"");s.TryBind("@limit",limit);foreach(var r in s.ExecuteQuery())result.Add(r.GetString(0));}return result;
        }
        public List<long> GetLinkedMediaIds(long embyId)
        {
            var result=new List<long>(); lock(sync) using(var s=db.PrepareStatement("SELECT DISTINCT media_emby_id FROM emby_relationship WHERE person_emby_id=@emby ORDER BY media_emby_id")){s.TryBind("@emby",embyId);foreach(var r in s.ExecuteQuery())result.Add(r.GetInt64(0));}return result;
        }

        public void SaveRecoveryCandidates(long embyId,IEnumerable<TmdbEntity> candidates,Func<TmdbEntity,string> serialize)
        {
            lock(sync) db.RunInTransaction(x=>
            {
                Statement(x,"DELETE FROM tmdb_resolution_candidate WHERE emby_id=@emby",s=>s.TryBind("@emby",embyId));var rank=0;
                foreach(var candidate in (candidates??Enumerable.Empty<TmdbEntity>()).Where(c=>c!=null).GroupBy(c=>c.id).Select(g=>g.First()))
                {
                    rank++;Statement(x,"INSERT INTO tmdb_resolution_candidate(emby_id,rank,entity_type,tmdb_id,name,source_external_id,raw_json,evaluated_utc) VALUES(@emby,@rank,'person',@id,@name,NULL,@raw,@now)",s=>{s.TryBind("@emby",embyId);s.TryBind("@rank",rank);s.TryBind("@id",candidate.id.ToString(CultureInfo.InvariantCulture));s.TryBind("@name",candidate.name);s.TryBind("@raw",serialize(candidate));s.TryBind("@now",Now());});
                }
            },TransactionMode.Immediate);
        }

        public void AddRecoveryCandidate(long embyId,TmdbEntity candidate,string sourceExternalId,Func<TmdbEntity,string> serialize)
        {
            if(candidate==null)return;lock(sync)db.RunInTransaction(x=>
            {
                Statement(x,"DELETE FROM tmdb_resolution_candidate WHERE emby_id=@emby AND tmdb_id=@id",s=>{s.TryBind("@emby",embyId);s.TryBind("@id",candidate.id.ToString(CultureInfo.InvariantCulture));});
                Statement(x,"INSERT INTO tmdb_resolution_candidate(emby_id,rank,entity_type,tmdb_id,name,source_external_id,raw_json,evaluated_utc) SELECT @emby,coalesce(max(rank),0)+1,'person',@id,@name,@source,@raw,@now FROM tmdb_resolution_candidate WHERE emby_id=@emby",s=>{s.TryBind("@emby",embyId);s.TryBind("@id",candidate.id.ToString(CultureInfo.InvariantCulture));s.TryBind("@name",candidate.name);s.TryBind("@source",sourceExternalId);s.TryBind("@raw",serialize(candidate));s.TryBind("@now",Now());});
            },TransactionMode.Immediate);
        }

        public List<TmdbCrossProviderLead> GetMediaSupportedCrossProviderLeads()
        {
            const string sql=@"SELECT rc.emby_id,rc.tmdb_id,
(SELECT external_id FROM tmdb_external_id x WHERE x.entity_type='person' AND x.tmdb_id=rc.tmdb_id AND x.source_name='tvdb' LIMIT 1),
(SELECT external_id FROM tmdb_external_id x WHERE x.entity_type='person' AND x.tmdb_id=rc.tmdb_id AND x.source_name='imdb' LIMIT 1)
FROM tmdb_resolution_candidate rc WHERE rc.entity_type='person' AND EXISTS(SELECT 1 FROM emby_relationship er JOIN emby_item m ON m.emby_id=er.media_emby_id LEFT JOIN tmdb_item_resolution mr ON mr.emby_id=m.emby_id JOIN tmdb_credit_observation c ON c.production_tmdb_id=coalesce(m.tmdb_id,mr.resolved_tmdb_id) AND c.production_type=m.item_type AND c.person_tmdb_id=rc.tmdb_id WHERE er.person_emby_id=rc.emby_id)
AND EXISTS(SELECT 1 FROM tmdb_external_id x WHERE x.entity_type='person' AND x.tmdb_id=rc.tmdb_id AND x.source_name IN('tvdb','imdb')) ORDER BY rc.emby_id,rc.rank";
            var result=new List<TmdbCrossProviderLead>();lock(sync)using(var s=db.PrepareStatement(sql))foreach(var r in s.ExecuteQuery())result.Add(new TmdbCrossProviderLead{EmbyId=r.GetInt64(0),TmdbId=r.GetString(1),TvdbId=r.IsDBNull(2)?null:r.GetString(2),ImdbId=r.IsDBNull(3)?null:r.GetString(3)});return result;
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
                var mergedCredits = new TmdbCredits
                {
                    cast = TmdbCreditMerger.Cast(entity),
                    crew = (entity.combined_credits ?? entity.aggregate_credits ?? entity.credits)?.crew ?? new List<TmdbCredit>()
                };
                SaveCredits(x, id, type, mergedCredits);
                if(type=="episode")Statement(x,"INSERT OR REPLACE INTO provider_production_evidence VALUES('tmdb','episode',@id,'screen-credits','complete','tmdb-repository-save',@raw,@normalized,@now)",s=>{s.TryBind("@id",id);s.TryBind("@raw",mergedCredits.cast.Count);s.TryBind("@normalized",mergedCredits.cast.Count);s.TryBind("@now",Now());});
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
