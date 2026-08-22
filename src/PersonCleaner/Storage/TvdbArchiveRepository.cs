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
    internal sealed class TvdbLinkedCreditCandidate
    {
        public string TvdbId { get; set; }
        public string DisplayedName { get; set; }
        public int SupportedMedia { get; set; }
        public int NameAffinity { get; set; }
        public int RoleAffinity { get; set; }
    }

    internal sealed class TvdbCrossProviderLead
    {
        public long EmbyId { get; set; }
        public string TvdbId { get; set; }
        public string TmdbId { get; set; }
        public string ImdbId { get; set; }
    }

    internal sealed class TvdbFilmographyCorroborationTarget
    {
        public long EmbyId { get; set; }
        public string Name { get; set; }
        public string CurrentTvdbId { get; set; }
        public string CandidateTvdbId { get; set; }
        public string TmdbId { get; set; }
        public string ImdbId { get; set; }
        public List<string> SeriesIds { get; set; } = new List<string>();
        public HashSet<string> TmdbSeriesIds { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public List<string> MovieIds { get; set; } = new List<string>();
        public HashSet<string> TmdbMovieIds { get; set; } = new HashSet<string>(StringComparer.Ordinal);
    }

    internal sealed class EmbyArchiveItem
    {
        public long Id { get; set; }
        public string Guid { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public int? Year { get; set; }
        public long? Parent { get; set; }
        public string Tvdb { get; set; }
        public string Imdb { get; set; }
        public string Tmdb { get; set; }
        public string Path { get; set; }
    }

    internal sealed class TvdbArchiveRepository : IDisposable
    {
        private readonly ILogger logger;
        private readonly object sync = new object();
        private IDatabaseConnection db;
        public string DatabasePath { get; }

        public TvdbArchiveRepository(IApplicationPaths paths, ILogger logger)
        {
            this.logger = logger;
            DatabasePath = ArchiveDatabase.ResolvePath(paths);
        }

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
                    db.Execute("PRAGMA busy_timeout=30000"); db.Execute("PRAGMA synchronous=NORMAL"); db.Execute("PRAGMA foreign_keys=ON");
                    ArchiveDatabase.ValidateObjects(db, "TVDB", "schema_info", "emby_item", "tvdb_entity", "remote_id", "tvdb_alias", "credit", "tvdb_credit_observation", "fetch_cache", "api_response_cache", "api_response_archive", "item_resolution", "resolution_decision_history", "resolution_candidate", "candidate_evidence", "person_local_production", "candidate_tvdb_production", "emby_observation", "truth", "truth_entity", "truth_external_identity", "truth_entity_lineage", "truth_relationship", "algorithm", "experiment_run", "resolution_proposal", "experiment_prediction", "experiment_metric", "archive_schema_migration", "provider_credit_observation", "provider_production_evidence");
                    ArchiveDatabase.ValidateVersion(db, "TVDB", "schema_info", 1);
                    ArchiveDatabase.ValidateMigrations(db, 9);
                }
                catch { db?.Dispose(); db = null; throw; }
            }
        }

        public bool IsDue(string key)
        {
            lock (sync) using (var s = db.PrepareStatement("SELECT next_attempt_utc FROM fetch_cache WHERE cache_key=@key"))
            { s.TryBind("@key", key); foreach (var row in s.ExecuteQuery()) return DateTimeOffset.Parse(row.GetString(0), CultureInfo.InvariantCulture) <= DateTimeOffset.UtcNow; }
            return true;
        }

        public bool IsNotFoundCached(string key)
        {
            lock(sync) using(var s=db.PrepareStatement("SELECT 1 FROM fetch_cache WHERE cache_key=@key AND state='not-found' AND next_attempt_utc>@now LIMIT 1")){s.TryBind("@key",key);s.TryBind("@now",Now());foreach(var row in s.ExecuteQuery())return true;}return false;
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
            var now = Now();
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "INSERT OR IGNORE INTO api_response_archive(request_path,fetched_utc,response_json) SELECT request_path,fetched_utc,response_json FROM api_response_cache WHERE request_path=@path", s => s.TryBind("@path", requestPath));
                Statement(x, "INSERT OR REPLACE INTO api_response_cache VALUES(@path,@json,@now,@expires)", s => { s.TryBind("@path", requestPath); s.TryBind("@json", responseJson); s.TryBind("@now", now); s.TryBind("@expires", expires.ToString("O")); });
            }, TransactionMode.Immediate);
        }

        public void SaveEmby(long id, string guid, string type, string name, int? year, long? parent, string tvdb, string imdb, string tmdb, string path)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                SaveEmbyRow(x, new EmbyArchiveItem { Id = id, Guid = guid, Type = type, Name = name, Year = year, Parent = parent, Tvdb = tvdb, Imdb = imdb, Tmdb = tmdb, Path = path }, Now());
            }, TransactionMode.Immediate);
        }

        public void SaveEmbyBatch(IEnumerable<EmbyArchiveItem> items)
        {
            var batch = (items ?? Enumerable.Empty<EmbyArchiveItem>()).ToList();
            if (batch.Count == 0) return;
            var now = Now();
            lock (sync) db.RunInTransaction(x =>
            {
                foreach (var item in batch) SaveEmbyRow(x, item, now);
            }, TransactionMode.Immediate);
        }

        private static void SaveEmbyRow(IDatabaseConnection x, EmbyArchiveItem item, string now)
        {
            var truthEntityId = "emby:" + item.Id.ToString(CultureInfo.InvariantCulture);
            if (!IsSameEmbyRow(x, item))
            {
                Statement(x, "INSERT INTO emby_observation(emby_id,emby_guid,item_type,name,production_year,parent_emby_id,tvdb_id,imdb_id,tmdb_id,path,observed_utc) VALUES(@id,@guid,@type,@name,@year,@parent,@tvdb,@imdb,@tmdb,@path,@now)", s => BindEmby(s, item.Id, item.Guid, item.Type, item.Name, item.Year, item.Parent, item.Tvdb, item.Imdb, item.Tmdb, item.Path, now));
                Statement(x, "REPLACE INTO emby_item VALUES(@id,@guid,@type,@name,@year,@parent,@tvdb,@imdb,@tmdb,@path,@now)", s => BindEmby(s, item.Id, item.Guid, item.Type, item.Name, item.Year, item.Parent, item.Tvdb, item.Imdb, item.Tmdb, item.Path, now));
            }
            foreach (var truthId in DraftTruthsWithoutSource(x, item.Id))
            {
                Statement(x, "INSERT INTO truth_entity(truth_id,truth_entity_id,entity_type,preferred_name,production_year,desired_emby_id,disposition) VALUES(@truth,@entity,@type,@name,@year,@id,'retain')", s => { s.TryBind("@truth", truthId); s.TryBind("@entity", truthEntityId); s.TryBind("@type", item.Type); s.TryBind("@name", item.Name); s.TryBind("@year", item.Year); s.TryBind("@id", item.Id); });
                Statement(x, "INSERT INTO truth_entity_lineage VALUES(@truth,@entity,@id,'retained')", s => { s.TryBind("@truth", truthId); s.TryBind("@entity", truthEntityId); s.TryBind("@id", item.Id); });
                SeedIdentity(x, truthId, truthEntityId, "emby", item.Id.ToString(CultureInfo.InvariantCulture));
                SeedIdentity(x, truthId, truthEntityId, "tvdb", item.Tvdb);
                SeedIdentity(x, truthId, truthEntityId, "imdb", item.Imdb);
                SeedIdentity(x, truthId, truthEntityId, "tmdb", item.Tmdb);
            }
        }

        private static bool IsSameEmbyRow(IDatabaseConnection x, EmbyArchiveItem item)
        {
            using (var s = x.PrepareStatement("SELECT 1 FROM emby_item WHERE emby_id=@id AND emby_guid IS @guid AND item_type=@type AND name IS @name AND production_year IS @year AND parent_emby_id IS @parent AND tvdb_id IS @tvdb AND imdb_id IS @imdb AND tmdb_id IS @tmdb AND path IS @path LIMIT 1"))
            {
                BindEmby(s, item.Id, item.Guid, item.Type, item.Name, item.Year, item.Parent, item.Tvdb, item.Imdb, item.Tmdb, item.Path, Now());
                foreach (var ignored in s.ExecuteQuery()) return true;
            }
            return false;
        }

        private static List<int> DraftTruthsWithoutSource(IDatabaseConnection x, long embyId)
        {
            var result = new List<int>();
            using (var s = x.PrepareStatement("SELECT t.truth_id FROM truth t WHERE t.status='draft' AND NOT EXISTS(SELECT 1 FROM truth_entity_lineage l WHERE l.truth_id=t.truth_id AND l.source_emby_id=@id) ORDER BY t.truth_id"))
            {
                s.TryBind("@id", embyId);
                foreach (var row in s.ExecuteQuery()) result.Add(row.GetInt(0));
            }
            return result;
        }

        private static void BindEmby(IStatement s, long id, string guid, string type, string name, int? year, long? parent, string tvdb, string imdb, string tmdb, string path, string now)
        {
            s.TryBind("@id", id); s.TryBind("@guid", guid); s.TryBind("@type", type); s.TryBind("@name", name); s.TryBind("@year", year); s.TryBind("@parent", parent); s.TryBind("@tvdb", tvdb); s.TryBind("@imdb", imdb); s.TryBind("@tmdb", tmdb); s.TryBind("@path", path); s.TryBind("@now", now);
        }

        private static void SeedIdentity(IDatabaseConnection x, int truthId, string truthEntityId, string provider, string externalId)
        {
            if (string.IsNullOrWhiteSpace(externalId)) return;
            Statement(x, "INSERT INTO truth_external_identity(truth_id,truth_entity_id,provider,external_id,provenance_type,provenance_reference) VALUES(@truth,@entity,@provider,@external,'initial-emby-import',@entity)", s => { s.TryBind("@truth", truthId); s.TryBind("@entity", truthEntityId); s.TryBind("@provider", provider); s.TryBind("@external", externalId); });
        }

        public bool IsUnchangedCachedDirectPerson(long id, string guid, string name, int? year, long? parent, string tvdb, string imdb, string tmdb, string path)
        {
            if (string.IsNullOrWhiteSpace(tvdb)) return false;
            lock (sync) using (var s = db.PrepareStatement(
                "SELECT 1 FROM emby_item e JOIN item_resolution r ON r.emby_id=e.emby_id " +
                "JOIN fetch_cache f ON f.cache_key='person:'||@tvdb " +
                "WHERE e.emby_id=@id AND e.item_type='person' AND e.emby_guid IS @guid AND e.name IS @name " +
                "AND e.production_year IS @year AND e.parent_emby_id IS @parent AND e.tvdb_id IS @tvdb " +
                "AND e.imdb_id IS @imdb AND e.tmdb_id IS @tmdb AND e.path IS @path " +
                "AND r.observed_tvdb_id=@tvdb AND r.resolved_tvdb_id=@tvdb AND r.provenance='direct' " +
                "AND f.state='success' AND f.next_attempt_utc>@now LIMIT 1"))
            {
                s.TryBind("@id", id); s.TryBind("@guid", guid); s.TryBind("@name", name); s.TryBind("@year", year);
                s.TryBind("@parent", parent); s.TryBind("@tvdb", tvdb); s.TryBind("@imdb", imdb); s.TryBind("@tmdb", tmdb);
                s.TryBind("@path", path); s.TryBind("@now", Now());
                foreach (var ignored in s.ExecuteQuery()) return true;
            }
            return false;
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
                Statement(x, "DELETE FROM tvdb_alias WHERE tvdb_id=@id AND entity_type=@type", s => { s.TryBind("@id", id); s.TryBind("@type", type); });
                foreach (var alias in d.aliases ?? new List<Tvdb.AliasData>())
                    if (!string.IsNullOrWhiteSpace(alias.name)) Statement(x, "INSERT OR REPLACE INTO tvdb_alias VALUES(@id,@type,@alias,@language,'alias')", s => { s.TryBind("@id", id); s.TryBind("@type", type); s.TryBind("@alias", alias.name); s.TryBind("@language", alias.language ?? ""); });
                SaveCredits(x, id, type, d.characters);
                if(type=="episode")
                {
                    var count=(d.characters??new List<Tvdb.CharacterData>()).Count(Tvdb.TvdbScope.IsScreenCredit);
                    Statement(x,"INSERT OR REPLACE INTO provider_production_evidence VALUES('tvdb','episode',@id,'screen-credits','complete','tvdb-repository-save',@raw,@normalized,@now)",s=>{s.TryBind("@id",id);s.TryBind("@raw",count);s.TryBind("@normalized",count);s.TryBind("@now",Now());});
                }
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
            Statement(x, "DELETE FROM tvdb_credit_observation WHERE source_entity_type=@type AND source_tvdb_id=@subject", s => { s.TryBind("@type", type); s.TryBind("@subject", subject); });
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
                Statement(x, "INSERT OR REPLACE INTO tvdb_credit_observation VALUES(@sourceType,@sourceId,@person,@production,@ptype,@character,@episode,@pname,@role,@ctype,@sort,@featured,@now)", s => { s.TryBind("@sourceType", type); s.TryBind("@sourceId", subject); s.TryBind("@person", personId); s.TryBind("@production", productionId); s.TryBind("@ptype", productionType); s.TryBind("@character", c.id); s.TryBind("@episode", c.episodeId?.ToString(CultureInfo.InvariantCulture) ?? ""); s.TryBind("@pname", c.personName); s.TryBind("@role", c.name); s.TryBind("@ctype", c.peopleType); s.TryBind("@sort", c.sort); s.TryBind("@featured", c.isFeatured ? 1 : 0); s.TryBind("@now", Now()); });
            }
        }

        public void MarkFetch(string key, bool success, string error)
        {
            var cfg = Plugin.Instance.Configuration;
            var next = DateTimeOffset.UtcNow.Add(success ? TimeSpan.FromDays(Math.Max(1, cfg.SuccessCacheDays)) : TimeSpan.FromMinutes(Math.Max(1, cfg.FailureRetryMinutes)));
            Execute("INSERT OR REPLACE INTO fetch_cache VALUES(@key,@state,NULL,COALESCE((SELECT attempt_count+1 FROM fetch_cache WHERE cache_key=@key),1),@now,@next,@error)", s => { s.TryBind("@key", key); s.TryBind("@state", success ? "success" : "failed"); s.TryBind("@now", Now()); s.TryBind("@next", next.ToString("O")); s.TryBind("@error", error); });
        }

        public void MarkNotFound(string key, string error)
        {
            var next = DateTimeOffset.UtcNow.AddDays(Math.Max(1, Plugin.Instance.Configuration.SuccessCacheDays));
            Execute("INSERT OR REPLACE INTO fetch_cache VALUES(@key,'not-found',404,COALESCE((SELECT attempt_count+1 FROM fetch_cache WHERE cache_key=@key),1),@now,@next,@error)", s => { s.TryBind("@key", key); s.TryBind("@now", Now()); s.TryBind("@next", next.ToString("O")); s.TryBind("@error", error); });
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
            var now = Now();
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "INSERT OR REPLACE INTO item_resolution VALUES(@emby,@type,@observed,@resolved,@provenance,@method,@confidence,@candidates,@evidence,@now)", s => { s.TryBind("@emby", embyId); s.TryBind("@type", type); s.TryBind("@observed", observed); s.TryBind("@resolved", resolved); s.TryBind("@provenance", provenance); s.TryBind("@method", method); s.TryBind("@confidence", confidence); s.TryBind("@candidates", candidates); s.TryBind("@evidence", evidence); s.TryBind("@now", now); });
                Statement(x, "INSERT OR IGNORE INTO resolution_decision_history VALUES(@emby,@now,'tvdb-evidence-first-v2',@type,@observed,@resolved,@provenance,@method,@confidence,@candidates,@evidence)", s => { s.TryBind("@emby", embyId); s.TryBind("@now", now); s.TryBind("@type", type); s.TryBind("@observed", observed); s.TryBind("@resolved", resolved); s.TryBind("@provenance", provenance); s.TryBind("@method", method); s.TryBind("@confidence", confidence); s.TryBind("@candidates", candidates); s.TryBind("@evidence", evidence); });
            }, TransactionMode.Immediate);
        }

        public void SaveResolutionCandidates(long embyId, IEnumerable<Tvdb.ResolutionCandidate> candidates, Func<object, string> serialize)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "DELETE FROM resolution_candidate WHERE emby_id=@emby", s => s.TryBind("@emby", embyId));
                Statement(x, "DELETE FROM candidate_evidence WHERE emby_id=@emby", s => s.TryBind("@emby", embyId));
                Statement(x, "DELETE FROM person_local_production WHERE emby_id=@emby", s => s.TryBind("@emby", embyId));
                Statement(x, "DELETE FROM candidate_tvdb_production WHERE emby_id=@emby", s => s.TryBind("@emby", embyId));
                var materialized = (candidates ?? Enumerable.Empty<Tvdb.ResolutionCandidate>()).ToList();
                foreach (var key in materialized.SelectMany(c => c.LocalFilmographyIds ?? new List<string>()).Distinct())
                    SaveProductionKey(x, "person_local_production", embyId, null, key, false);
                foreach (var c in materialized)
                {
                    Statement(x, "INSERT INTO resolution_candidate VALUES(@emby,@rank,@type,@tvdb,@name,@score,@external,@filmography,@evidence,@now)", s => { s.TryBind("@emby", embyId); s.TryBind("@rank", c.Rank); s.TryBind("@type", c.EntityType); s.TryBind("@tvdb", c.TvdbId); s.TryBind("@name", c.Name); s.TryBind("@score", c.Score); s.TryBind("@external", serialize(c.ExternalIds)); s.TryBind("@filmography", serialize(c.FilmographyIds)); s.TryBind("@evidence", c.Evidence); s.TryBind("@now", Now()); });
                    Statement(x, "INSERT OR REPLACE INTO candidate_evidence VALUES(@emby,@tvdb,@type,@searchRank,@finalRank,@name,@nameClass,@methods,@extended,@reason,@score,@external,@filmography,@evidence,@now)", s => { s.TryBind("@emby", embyId); s.TryBind("@tvdb", c.TvdbId); s.TryBind("@type", c.EntityType); s.TryBind("@searchRank", c.SearchRank); s.TryBind("@finalRank", c.Rank); s.TryBind("@name", c.Name); s.TryBind("@nameClass", c.NameClass ?? "unknown"); s.TryBind("@methods", c.DiscoveryMethods ?? "name-search"); s.TryBind("@extended", c.ExtendedFetched ? 1 : 0); s.TryBind("@reason", c.ExtendedFetchReason); s.TryBind("@score", c.Score); s.TryBind("@external", serialize(c.ExternalIds)); s.TryBind("@filmography", serialize(c.FilmographyIds)); s.TryBind("@evidence", c.Evidence); s.TryBind("@now", Now()); });
                    var overlaps = new HashSet<string>(c.OverlapIds ?? new List<string>());
                    foreach (var key in c.FilmographyIds ?? new List<string>()) SaveProductionKey(x, "candidate_tvdb_production", embyId, c.TvdbId, key, overlaps.Contains(key));
                }
            }, TransactionMode.Immediate);
        }

        public void AddResolutionCandidate(long embyId,Tvdb.ResolutionCandidate c,Func<object,string> serialize)
        {
            if(c==null)return;lock(sync)db.RunInTransaction(x=>
            {
                Statement(x,"DELETE FROM resolution_candidate WHERE emby_id=@emby AND tvdb_id=@tvdb",s=>{s.TryBind("@emby",embyId);s.TryBind("@tvdb",c.TvdbId);});Statement(x,"DELETE FROM candidate_evidence WHERE emby_id=@emby AND tvdb_id=@tvdb",s=>{s.TryBind("@emby",embyId);s.TryBind("@tvdb",c.TvdbId);});Statement(x,"DELETE FROM candidate_tvdb_production WHERE emby_id=@emby AND candidate_tvdb_id=@tvdb",s=>{s.TryBind("@emby",embyId);s.TryBind("@tvdb",c.TvdbId);});
                var rank=1;using(var q=x.PrepareStatement("SELECT coalesce(max(rank),0)+1 FROM resolution_candidate WHERE emby_id=@emby")){q.TryBind("@emby",embyId);foreach(var row in q.ExecuteQuery())rank=row.GetInt(0);}c.Rank=rank;
                Statement(x,"INSERT INTO resolution_candidate VALUES(@emby,@rank,@type,@tvdb,@name,@score,@external,@filmography,@evidence,@now)",s=>{s.TryBind("@emby",embyId);s.TryBind("@rank",rank);s.TryBind("@type",c.EntityType);s.TryBind("@tvdb",c.TvdbId);s.TryBind("@name",c.Name);s.TryBind("@score",c.Score);s.TryBind("@external",serialize(c.ExternalIds));s.TryBind("@filmography",serialize(c.FilmographyIds));s.TryBind("@evidence",c.Evidence);s.TryBind("@now",Now());});
                Statement(x,"INSERT OR REPLACE INTO candidate_evidence VALUES(@emby,@tvdb,@type,@searchRank,@finalRank,@name,@nameClass,@methods,@extended,@reason,@score,@external,@filmography,@evidence,@now)",s=>{s.TryBind("@emby",embyId);s.TryBind("@tvdb",c.TvdbId);s.TryBind("@type",c.EntityType);s.TryBind("@searchRank",c.SearchRank);s.TryBind("@finalRank",rank);s.TryBind("@name",c.Name);s.TryBind("@nameClass",c.NameClass??"unknown");s.TryBind("@methods",c.DiscoveryMethods??"cross-provider-direct-id");s.TryBind("@extended",c.ExtendedFetched?1:0);s.TryBind("@reason",c.ExtendedFetchReason);s.TryBind("@score",c.Score);s.TryBind("@external",serialize(c.ExternalIds));s.TryBind("@filmography",serialize(c.FilmographyIds));s.TryBind("@evidence",c.Evidence);s.TryBind("@now",Now());});
                var overlaps=new HashSet<string>(c.OverlapIds??new List<string>());foreach(var key in c.FilmographyIds??new List<string>())SaveProductionKey(x,"candidate_tvdb_production",embyId,c.TvdbId,key,overlaps.Contains(key));
            },TransactionMode.Immediate);
        }
        public List<long> GetPersonEvidenceGapIds()
        {
            var frozen=new List<long>();lock(sync)
            {
                using(var cohort=db.PrepareStatement("SELECT cast(substr(cache_key,length('evidence-cohort:tvdb:')+1) AS integer) FROM fetch_cache WHERE cache_key LIKE 'evidence-cohort:tvdb:%' AND state IN('cohort-active','success') ORDER BY fetched_utc,cache_key LIMIT 1000"))foreach(var r in cohort.ExecuteQuery())frozen.Add(r.GetInt64(0));
            }
            if(frozen.Count>0)return frozen;
            var result=new List<long>(); lock(sync) using(var s=db.PrepareStatement(@"WITH linked AS (SELECT person_emby_id,count(DISTINCT media_emby_id) linked FROM emby_relationship GROUP BY person_emby_id), duplicates AS (SELECT tvdb_id FROM emby_item WHERE item_type='person' AND tvdb_id IS NOT NULL GROUP BY tvdb_id HAVING count(*)>1), supported AS (SELECT p.emby_id,count(DISTINCT er.media_emby_id) media FROM emby_item p JOIN emby_relationship er ON er.person_emby_id=p.emby_id JOIN emby_item m ON m.emby_id=er.media_emby_id LEFT JOIN item_resolution mr ON mr.emby_id=m.emby_id JOIN credit c ON c.subject_tvdb_id=coalesce(m.tvdb_id,mr.resolved_tvdb_id) AND c.subject_type=m.item_type AND c.person_tvdb_id=p.tvdb_id WHERE p.item_type='person' AND p.tvdb_id IS NOT NULL GROUP BY p.emby_id)
SELECT p.emby_id FROM emby_item p JOIN linked l ON l.person_emby_id=p.emby_id LEFT JOIN item_resolution r ON r.emby_id=p.emby_id LEFT JOIN tvdb_entity pe ON pe.entity_type='person' AND pe.tvdb_id=p.tvdb_id LEFT JOIN duplicates d ON d.tvdb_id=p.tvdb_id LEFT JOIN supported s ON s.emby_id=p.emby_id LEFT JOIN (SELECT DISTINCT p.emby_id FROM emby_item p JOIN remote_id rt ON rt.entity_type='person' AND rt.tvdb_id=p.tvdb_id AND rt.source_name='TheMovieDB.com' AND rt.remote_id<>p.tmdb_id JOIN remote_id ri ON ri.entity_type='person' AND ri.tvdb_id=p.tvdb_id AND ri.source_name='IMDB' AND ri.remote_id<>p.imdb_id WHERE p.item_type='person' AND p.tmdb_id IS NOT NULL AND p.tvdb_id IS NOT NULL AND p.imdb_id IS NOT NULL) cf ON cf.emby_id=p.emby_id WHERE p.item_type='person' AND (p.tvdb_id IS NULL OR r.emby_id IS NULL OR r.provenance IN('direct-unavailable','unresolved') OR (p.tvdb_id IS NOT NULL AND pe.tvdb_id IS NULL) OR d.tvdb_id IS NOT NULL OR coalesce(s.media,0)<l.linked) ORDER BY CASE WHEN cf.emby_id IS NOT NULL THEN 0 WHEN r.provenance='direct-unavailable' THEN 1 WHEN d.tvdb_id IS NOT NULL THEN 2 WHEN p.tvdb_id IS NULL THEN 4 ELSE 3 END,p.emby_id LIMIT 1000")){s.TryBind("@now",Now());foreach(var r in s.ExecuteQuery()) result.Add(r.GetInt64(0));} foreach(var id in result)MarkFetch("evidence-cohort:tvdb:"+id,true,"Frozen evaluation cohort"); return result;
        }
        public int GetLinkedMediaCount(long personEmbyId)
        {
            lock(sync) using(var s=db.PrepareStatement("SELECT count(DISTINCT media_emby_id) FROM emby_relationship WHERE person_emby_id=@id")){s.TryBind("@id",personEmbyId);foreach(var r in s.ExecuteQuery())return r.GetInt(0);}return 0;
        }

        public List<TvdbLinkedCreditCandidate> GetLinkedCreditCandidates(long personEmbyId, string currentTvdbId)
        {
            const string sql = @"WITH linked AS (
SELECT p.name current_name,er.person_type,er.role,m.item_type media_type,coalesce(m.tvdb_id,mr.resolved_tvdb_id) production_id
FROM emby_item p JOIN emby_relationship er ON er.person_emby_id=p.emby_id JOIN emby_item m ON m.emby_id=er.media_emby_id
LEFT JOIN item_resolution mr ON mr.emby_id=m.emby_id WHERE p.emby_id=@emby), candidates AS (
SELECT c.person_tvdb_id candidate_id,max(c.person_name) displayed_name,count(DISTINCT l.media_type||':'||l.production_id) supported,
max(CASE WHEN lower(trim(c.person_name))=lower(trim(l.current_name)) THEN 6
 WHEN EXISTS(SELECT 1 FROM tvdb_alias a WHERE a.entity_type='person' AND a.tvdb_id=c.person_tvdb_id AND lower(trim(a.alias))=lower(trim(l.current_name))) THEN 6
 WHEN lower(substr(c.person_name,1,instr(c.person_name||' ',' ')-1))=lower(substr(l.current_name,1,instr(l.current_name||' ',' ')-1)) THEN 3
 ELSE 0 END) name_affinity,
max(CASE WHEN lower(replace(coalesce(c.credit_type,''),' ',''))=lower(replace(coalesce(nullif(l.role,''),l.person_type),' ','')) THEN 1 ELSE 0 END) role_affinity
FROM linked l JOIN credit c ON c.subject_tvdb_id=l.production_id AND c.subject_type=l.media_type
WHERE l.production_id IS NOT NULL AND c.person_tvdb_id<>coalesce(@current,'') GROUP BY c.person_tvdb_id)
SELECT candidate_id,displayed_name,supported,name_affinity,role_affinity FROM candidates ORDER BY supported DESC,name_affinity DESC,role_affinity DESC,candidate_id";
            var result = new List<TvdbLinkedCreditCandidate>();
            lock (sync) using (var s = db.PrepareStatement(sql))
            {
                s.TryBind("@emby", personEmbyId); s.TryBind("@current", currentTvdbId ?? string.Empty);
                foreach (var row in s.ExecuteQuery()) result.Add(new TvdbLinkedCreditCandidate{TvdbId=row.GetString(0),DisplayedName=row.IsDBNull(1)?null:row.GetString(1),SupportedMedia=row.GetInt(2),NameAffinity=row.GetInt(3),RoleAffinity=row.GetInt(4)});
            }
            return result;
        }

        public List<TvdbCrossProviderLead> GetMediaSupportedCrossProviderLeads()
        {
            const string sql=@"SELECT ce.emby_id,ce.tvdb_id,
(SELECT remote_id FROM remote_id r WHERE r.entity_type='person' AND r.tvdb_id=ce.tvdb_id AND r.source_name='TheMovieDB.com' LIMIT 1) tmdb_id,
(SELECT remote_id FROM remote_id r WHERE r.entity_type='person' AND r.tvdb_id=ce.tvdb_id AND r.source_name='IMDB' LIMIT 1) imdb_id
FROM candidate_evidence ce WHERE ce.entity_type='person' AND EXISTS(SELECT 1 FROM candidate_tvdb_production p WHERE p.emby_id=ce.emby_id AND p.candidate_tvdb_id=ce.tvdb_id AND p.is_shared=1)
AND EXISTS(SELECT 1 FROM remote_id r WHERE r.entity_type='person' AND r.tvdb_id=ce.tvdb_id AND r.source_name IN('TheMovieDB.com','IMDB')) ORDER BY ce.emby_id,ce.final_rank";
            var result=new List<TvdbCrossProviderLead>();lock(sync)using(var s=db.PrepareStatement(sql))foreach(var r in s.ExecuteQuery())result.Add(new TvdbCrossProviderLead{EmbyId=r.GetInt64(0),TvdbId=r.GetString(1),TmdbId=r.IsDBNull(2)?null:r.GetString(2),ImdbId=r.IsDBNull(3)?null:r.GetString(3)});return result;
        }

        public List<TvdbFilmographyCorroborationTarget> GetFilmographyCorroborationTargets()
        {
            const string sql=@"SELECT DISTINCT p.emby_id,p.name,p.tvdb_id,ce.tvdb_id,p.tmdb_id,coalesce(p.imdb_id,ti.external_id)
FROM candidate_evidence ce JOIN emby_item p ON p.emby_id=ce.emby_id
JOIN item_resolution ir ON ir.emby_id=p.emby_id AND ir.provenance='direct-unavailable'
LEFT JOIN tmdb_external_id ti ON ti.entity_type='person' AND ti.tmdb_id=p.tmdb_id AND ti.source_name='imdb'
JOIN json_each(ce.external_ids_json) jt ON json_extract(jt.value,'$.sourceName')='TheMovieDB.com' AND json_extract(jt.value,'$.id')=p.tmdb_id
JOIN json_each(ce.external_ids_json) ji ON json_extract(ji.value,'$.sourceName')='IMDB' AND json_extract(ji.value,'$.id')=coalesce(p.imdb_id,ti.external_id)
WHERE p.item_type='person' AND p.tmdb_id IS NOT NULL AND coalesce(p.imdb_id,ti.external_id) IS NOT NULL AND ce.tvdb_id<>coalesce(p.tvdb_id,'')
ORDER BY p.emby_id,ce.final_rank";
            var result=new List<TvdbFilmographyCorroborationTarget>();
            lock(sync)using(var s=db.PrepareStatement(sql))foreach(var r in s.ExecuteQuery())
            {
                var target=new TvdbFilmographyCorroborationTarget{EmbyId=r.GetInt64(0),Name=r.GetString(1),CurrentTvdbId=r.IsDBNull(2)?null:r.GetString(2),CandidateTvdbId=r.GetString(3),TmdbId=r.GetString(4),ImdbId=r.GetString(5)};
                using(var p=db.PrepareStatement("SELECT production_tvdb_id FROM candidate_tvdb_production WHERE emby_id=@emby AND candidate_tvdb_id=@candidate AND production_type='series' ORDER BY production_tvdb_id"))
                { p.TryBind("@emby",target.EmbyId);p.TryBind("@candidate",target.CandidateTvdbId);foreach(var row in p.ExecuteQuery())target.SeriesIds.Add(row.GetString(0)); }
                using(var p=db.PrepareStatement("SELECT DISTINCT production_tmdb_id FROM tmdb_credit WHERE person_tmdb_id=@person AND production_type='series'")){p.TryBind("@person",target.TmdbId);foreach(var row in p.ExecuteQuery())target.TmdbSeriesIds.Add(row.GetString(0));}
                using(var p=db.PrepareStatement("SELECT production_tvdb_id FROM candidate_tvdb_production WHERE emby_id=@emby AND candidate_tvdb_id=@candidate AND production_type='movie' ORDER BY production_tvdb_id")){p.TryBind("@emby",target.EmbyId);p.TryBind("@candidate",target.CandidateTvdbId);foreach(var row in p.ExecuteQuery())target.MovieIds.Add(row.GetString(0));}
                using(var p=db.PrepareStatement("SELECT DISTINCT production_tmdb_id FROM tmdb_credit WHERE person_tmdb_id=@person AND production_type='movie'")){p.TryBind("@person",target.TmdbId);foreach(var row in p.ExecuteQuery())target.TmdbMovieIds.Add(row.GetString(0));}
                result.Add(target);
            }
            return result;
        }

        public string GetMediaSupportedTmdbImdbId(long personEmbyId, string tmdbId)
        {
            if (string.IsNullOrWhiteSpace(tmdbId)) return null;
            const string sql = @"SELECT x.external_id
FROM tmdb_external_id x
WHERE x.tmdb_id=@tmdb AND x.entity_type='person' AND x.source_name='imdb'
AND EXISTS(
 SELECT 1 FROM emby_relationship er
 JOIN emby_item m ON m.emby_id=er.media_emby_id
 LEFT JOIN tmdb_item_resolution mr ON mr.emby_id=m.emby_id
 JOIN tmdb_credit_observation c ON c.production_tmdb_id=coalesce(m.tmdb_id,mr.resolved_tmdb_id)
  AND c.production_type=m.item_type AND c.person_tmdb_id=@tmdb
 WHERE er.person_emby_id=@emby)
LIMIT 1";
            lock (sync) using (var s = db.PrepareStatement(sql))
            {
                s.TryBind("@tmdb", tmdbId); s.TryBind("@emby", personEmbyId);
                foreach (var row in s.ExecuteQuery()) return row.IsDBNull(0) ? null : row.GetString(0);
            }
            return null;
        }

        public string DescribeProduction(string productionKey)
        {
            if (!TrySplitProductionKey(productionKey, out var type, out var id)) return productionKey;
            lock (sync) using (var s = db.PrepareStatement("SELECT name FROM tvdb_entity WHERE entity_type=@type AND tvdb_id=@id LIMIT 1"))
            {
                s.TryBind("@type", type); s.TryBind("@id", id);
                foreach (var row in s.ExecuteQuery())
                {
                    var name = row.IsDBNull(0) ? null : row.GetString(0);
                    if (!string.IsNullOrWhiteSpace(name)) return name + " (" + type + ", TVDB " + id + ")";
                }
            }
            return productionKey;
        }

        private static void SaveProductionKey(IDatabaseConnection x, string table, long embyId, string candidateTvdbId, string key, bool shared)
        {
            if (!TrySplitProductionKey(key, out var type, out var id)) return;
            if (table == "person_local_production")
                Statement(x, "INSERT OR IGNORE INTO person_local_production VALUES(@emby,@key,@type,@id)", s => { s.TryBind("@emby", embyId); s.TryBind("@key", key); s.TryBind("@type", type); s.TryBind("@id", id); });
            else
                Statement(x, "INSERT OR REPLACE INTO candidate_tvdb_production VALUES(@emby,@candidate,@key,@type,@id,@shared)", s => { s.TryBind("@emby", embyId); s.TryBind("@candidate", candidateTvdbId); s.TryBind("@key", key); s.TryBind("@type", type); s.TryBind("@id", id); s.TryBind("@shared", shared ? 1 : 0); });
        }

        private static bool TrySplitProductionKey(string key, out string type, out string id)
        {
            var separator = (key ?? string.Empty).IndexOf(':');
            if (separator <= 0 || separator == key.Length - 1) { type = null; id = null; return false; }
            type = key.Substring(0, separator); id = key.Substring(separator + 1); return true;
        }

        private int Scalar(string sql) { using (var s = db.PrepareStatement(sql)) foreach (var row in s.ExecuteQuery()) return row.GetInt(0); return 0; }

        private void Execute(string sql, Action<IStatement> bind) { lock (sync) Statement(db, sql, bind); }
        private static void Statement(IDatabaseConnection connection, string sql, Action<IStatement> bind) { using (var s = connection.PrepareStatement(sql)) { bind(s); s.MoveNext(); } }
        private static string Now() => DateTimeOffset.UtcNow.ToString("O");
        public void Dispose() { lock (sync) { db?.Dispose(); db = null; } }
    }
}
