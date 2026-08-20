using MediaBrowser.Common.Configuration;
using PersonCleaner.Configuration;
using SQLitePCL.pretty;
using SQLitePCLEx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PersonCleaner.Storage
{
    internal sealed class EmbyRelationshipRecord
    {
        public long PersonId { get; set; }
        public long MediaId { get; set; }
        public string MediaType { get; set; }
        public string PersonType { get; set; }
        public string Role { get; set; }
    }

    internal sealed class ProviderWorkRecord
    {
        public string Provider { get; set; }
        public long EmbyId { get; set; }
        public string EntityType { get; set; }
        public string Route { get; set; }
    }

    internal sealed class UnifiedArchiveRepository : IDisposable
    {
        private readonly object sync = new object();
        private IDatabaseConnection db;
        public string DatabasePath { get; }

        public UnifiedArchiveRepository(IApplicationPaths paths)
        {
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
                    db.Execute("PRAGMA busy_timeout=30000"); db.Execute("PRAGMA synchronous=NORMAL");
                    ArchiveDatabase.ValidateObjects(db, "unified archive", "archive_schema_migration", "emby_relationship", "emby_relationship_observation", "provider_update_run", "provider_work", "provider_run_cache", "provider_snapshot_progress", "truth_entity_lineage", "truth_relationship");
                    ArchiveDatabase.ValidateMigrations(db, 4);
                }
                catch { db?.Dispose(); db = null; throw; }
            }
        }

        public long StartRun(int itemCount)
        {
            lock (sync)
            {
                Statement(db, "INSERT INTO provider_update_run(status,started_utc,updated_utc,total_items,message) VALUES('running',@now,@now,@items,'Reading Emby snapshot')", s => { s.TryBind("@now", Now()); s.TryBind("@items", itemCount); });
                using (var s = db.PrepareStatement("SELECT last_insert_rowid()")) foreach (var row in s.ExecuteQuery()) return row.GetInt64(0);
            }
            throw new InvalidOperationException("Unable to create provider update run");
        }

        public void SetSnapshotComplete(long runId, int itemCount)
        {
            lock (sync) Statement(db, "UPDATE provider_update_run SET total_items=@items,updated_utc=@now,message='Emby snapshot complete; provider work manifest being prepared' WHERE run_id=@run", s => { s.TryBind("@items", itemCount); s.TryBind("@now", Now()); s.TryBind("@run", runId); });
        }

        public void SetRunMessage(long runId, string message)
        {
            lock (sync) Statement(db, "UPDATE provider_update_run SET message=@message,updated_utc=@now WHERE run_id=@run", s => { s.TryBind("@message", message); s.TryBind("@now", Now()); s.TryBind("@run", runId); });
        }

        public void BeginSnapshotWrites(long runId, int entityCount, int relationshipCount)
        {
            lock (sync) Statement(db, "INSERT OR REPLACE INTO provider_snapshot_progress VALUES(@run,'entities',@entities,0,@relationships,0,@now)", s => { s.TryBind("@run", runId); s.TryBind("@entities", entityCount); s.TryBind("@relationships", relationshipCount); s.TryBind("@now", Now()); });
        }

        public void UpdateSnapshotWrites(long runId, string phase, int entities, int relationships)
        {
            lock (sync) Statement(db, "UPDATE provider_snapshot_progress SET phase=@phase,processed_entities=@entities,processed_relationships=@relationships,updated_utc=@now WHERE run_id=@run", s => { s.TryBind("@phase", phase); s.TryBind("@entities", entities); s.TryBind("@relationships", relationships); s.TryBind("@now", Now()); s.TryBind("@run", runId); });
        }

        public int SaveRelationshipBatch(IEnumerable<EmbyRelationshipRecord> relationships)
        {
            var batch = (relationships ?? new EmbyRelationshipRecord[0])
                .GroupBy(RelationshipKey, StringComparer.Ordinal)
                .Select(x => x.First()).ToList();
            if (batch.Count == 0) return 0;
            var now = Now();
            var inserted = 0;
            lock (sync) db.RunInTransaction(x =>
            {
                x.Execute("CREATE TEMP TABLE IF NOT EXISTS snapshot_relationship_stage(relationship_key TEXT PRIMARY KEY,person_emby_id INTEGER NOT NULL,media_emby_id INTEGER NOT NULL,media_type TEXT NOT NULL,person_type TEXT NOT NULL,role TEXT,observed_utc TEXT NOT NULL)");
                x.Execute("DELETE FROM snapshot_relationship_stage");
                for (var offset = 0; offset < batch.Count; offset += 100)
                {
                    var slice = batch.Skip(offset).Take(100).ToList();
                    var sql = new StringBuilder("INSERT OR IGNORE INTO snapshot_relationship_stage VALUES");
                    for (var i = 0; i < slice.Count; i++)
                    {
                        if (i > 0) sql.Append(',');
                        sql.Append("(@key").Append(i).Append(",@person").Append(i).Append(",@media").Append(i).Append(",@mediaType").Append(i).Append(",@personType").Append(i).Append(",@role").Append(i).Append(",@now").Append(i).Append(')');
                    }
                    Statement(x, sql.ToString(), s =>
                    {
                        for (var i = 0; i < slice.Count; i++)
                        {
                            var value = slice[i];
                            s.TryBind("@key" + i, RelationshipKey(value)); s.TryBind("@person" + i, value.PersonId); s.TryBind("@media" + i, value.MediaId);
                            s.TryBind("@mediaType" + i, value.MediaType); s.TryBind("@personType" + i, value.PersonType); s.TryBind("@role" + i, value.Role); s.TryBind("@now" + i, now);
                        }
                    });
                }
                using (var count = x.PrepareStatement("SELECT COUNT(*) FROM snapshot_relationship_stage s WHERE NOT EXISTS(SELECT 1 FROM emby_relationship e WHERE e.relationship_key=s.relationship_key)"))
                    foreach (var row in count.ExecuteQuery()) inserted = row.GetInt(0);
                x.Execute("INSERT INTO emby_relationship_observation(relationship_key,person_emby_id,media_emby_id,media_type,person_type,role,observed_utc) SELECT s.relationship_key,s.person_emby_id,s.media_emby_id,s.media_type,s.person_type,s.role,s.observed_utc FROM snapshot_relationship_stage s WHERE NOT EXISTS(SELECT 1 FROM emby_relationship e WHERE e.relationship_key=s.relationship_key)");
                x.Execute("INSERT OR IGNORE INTO emby_relationship SELECT relationship_key,person_emby_id,media_emby_id,media_type,person_type,role,observed_utc FROM snapshot_relationship_stage");
                x.Execute("INSERT OR IGNORE INTO truth_relationship(truth_id,relationship_id,subject_truth_entity_id,object_truth_entity_id,relationship_type,role,character_name,provenance_type,provenance_reference) SELECT t.truth_id,s.relationship_key,p.truth_entity_id,m.truth_entity_id,'credit',s.person_type,s.role,'initial-emby-import','emby-relationship:'||s.relationship_key FROM truth t CROSS JOIN snapshot_relationship_stage s JOIN truth_entity p ON p.truth_id=t.truth_id AND p.truth_entity_id='emby:'||s.person_emby_id JOIN truth_entity m ON m.truth_id=t.truth_id AND m.truth_entity_id='emby:'||s.media_emby_id WHERE t.status='draft'");
            }, TransactionMode.Immediate);
            return inserted;
        }

        private static string RelationshipKey(EmbyRelationshipRecord value) => value.MediaId.ToString(CultureInfo.InvariantCulture) + ":" + value.PersonId.ToString(CultureInfo.InvariantCulture) + ":" + (value.PersonType ?? "") + ":" + (value.Role ?? "");

        public void SeedWork(long runId, long embyId, string type, string provider, string route)
        {
            lock (sync) Statement(db, "INSERT OR REPLACE INTO provider_work(provider,emby_id,entity_type,route,state,attempt_count,last_run_id,outcome,error,updated_utc) VALUES(@provider,@emby,@type,@route,'pending',COALESCE((SELECT attempt_count FROM provider_work WHERE provider=@provider AND emby_id=@emby),0),@run,NULL,NULL,@now)", s =>
            { s.TryBind("@provider", provider); s.TryBind("@emby", embyId); s.TryBind("@type", type); s.TryBind("@route", route); s.TryBind("@run", runId); s.TryBind("@now", Now()); });
        }

        public void SeedWorkBatch(long runId, IEnumerable<ProviderWorkRecord> work)
        {
            var batch = new List<ProviderWorkRecord>(work ?? new ProviderWorkRecord[0]);
            if (batch.Count == 0) return;
            var now = Now();
            lock (sync) db.RunInTransaction(x =>
            {
                foreach (var item in batch)
                    Statement(x, "INSERT OR REPLACE INTO provider_work(provider,emby_id,entity_type,route,state,attempt_count,last_run_id,outcome,error,updated_utc) VALUES(@provider,@emby,@type,@route,'pending',COALESCE((SELECT attempt_count FROM provider_work WHERE provider=@provider AND emby_id=@emby),0),@run,NULL,NULL,@now)", s =>
                    { s.TryBind("@provider", item.Provider); s.TryBind("@emby", item.EmbyId); s.TryBind("@type", item.EntityType); s.TryBind("@route", item.Route); s.TryBind("@run", runId); s.TryBind("@now", now); });
            }, TransactionMode.Immediate);
        }

        public void FinishSeeding(long runId)
        {
            lock (sync) Statement(db, "UPDATE provider_update_run SET total_work=(SELECT COUNT(*) FROM provider_work WHERE last_run_id=@run),updated_utc=@now WHERE run_id=@run", s => { s.TryBind("@run", runId); s.TryBind("@now", Now()); });
        }

        public void StartWork(long runId, string provider, long embyId)
        {
            lock (sync) Statement(db, "UPDATE provider_work SET state='running',updated_utc=@now WHERE provider=@provider AND emby_id=@emby AND last_run_id=@run", s => { s.TryBind("@now", Now()); s.TryBind("@provider", provider); s.TryBind("@emby", embyId); s.TryBind("@run", runId); });
        }

        public void CompleteWork(long runId, string provider, long embyId, bool success, string outcome, string error, long cacheHits, long cacheMisses)
        {
            lock (sync) db.RunInTransaction(x =>
            {
                Statement(x, "UPDATE provider_work SET state=@state,attempt_count=attempt_count+1,outcome=@outcome,error=@error,updated_utc=@now WHERE provider=@provider AND emby_id=@emby AND last_run_id=@run", s => { s.TryBind("@state", success ? "completed" : "failed"); s.TryBind("@outcome", outcome); s.TryBind("@error", error); s.TryBind("@now", Now()); s.TryBind("@provider", provider); s.TryBind("@emby", embyId); s.TryBind("@run", runId); });
                Statement(x, "UPDATE provider_update_run SET completed_work=completed_work+1,success_count=success_count+@success,failure_count=failure_count+@failure,updated_utc=@now WHERE run_id=@run", s => { s.TryBind("@success", success ? 1 : 0); s.TryBind("@failure", success ? 0 : 1); s.TryBind("@now", Now()); s.TryBind("@run", runId); });
                Statement(x, "INSERT OR REPLACE INTO provider_run_cache(run_id,provider,cache_hits,cache_misses,updated_utc) VALUES(@run,@provider,@hits,@misses,@now)", s => { s.TryBind("@run", runId); s.TryBind("@provider", provider); s.TryBind("@hits", cacheHits); s.TryBind("@misses", cacheMisses); s.TryBind("@now", Now()); });
            }, TransactionMode.Immediate);
        }

        public void FinishRun(long runId, string status, string message)
        {
            lock (sync) Statement(db, "UPDATE provider_update_run SET status=@status,updated_utc=@now,finished_utc=@now,message=@message WHERE run_id=@run", s => { s.TryBind("@status", status); s.TryBind("@now", Now()); s.TryBind("@message", message); s.TryBind("@run", runId); });
        }

        private static void Statement(IDatabaseConnection connection, string sql, Action<IStatement> bind) { using (var s = connection.PrepareStatement(sql)) { bind(s); s.MoveNext(); } }
        private static string Now() => DateTimeOffset.UtcNow.ToString("O");
        public void Dispose() { lock (sync) { db?.Dispose(); db = null; } }
    }
}
