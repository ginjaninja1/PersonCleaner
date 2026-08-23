using SQLitePCL.pretty;
using SQLitePCLEx;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PersonCleaner.Storage;

namespace PersonCleaner.Housekeeping
{
    /// <summary>
    /// Reconstructs people from provider-native media credits. Current Emby
    /// people are continuity containers and the comparison target; they never
    /// create identity edges. All media_truth_* rows are disposable.
    /// </summary>
    internal static class MediaFirstTruthBuilder
    {
        internal static void Materialize(IDatabaseConnection db,long run,Action<string,TimeSpan> timing=null)
        {
            var now=DateTime.UtcNow.ToString("o");
            db.Execute("PRAGMA temp_store=MEMORY");
            Exec(db,"INSERT INTO media_truth_run(run_id,algorithm_version,status,phase,progress,started_utc) VALUES(@run,'media-first-v1','running','normalizing-media',1,@now)",s=>{s.TryBind("@run",run);s.TryBind("@now",now);});
            Timed("media scope",timing,()=>TempMedia(db));
            Timed("provider candidates",timing,()=>TempCandidates(db));
            Timed("cross-provider links",timing,()=>TempProviderLinks(db));
            Timed("person clusters",timing,()=>People(db,run));
            CreditsAndIssues(db,run,timing);
            Timed("truth changes",timing,()=>Changes(db,run));
            Timed("review projections",timing,()=>Projections(db,run));
            Timed("invariants",timing,()=>Validate(db,run));
            Exec(db,"UPDATE media_truth_run SET status='completed',phase='completed',progress=100,completed_utc=@now,summary=(SELECT 'Media-first truth contains '||count(*)||' reconstructed people; auto='||coalesce(sum(CASE decision_class WHEN ''auto-commit'' THEN 1 ELSE 0 END),0)||'; review='||coalesce(sum(CASE decision_class WHEN ''human-review'' THEN 1 ELSE 0 END),0) FROM media_truth_projection WHERE run_id=@run) WHERE run_id=@run",s=>{s.TryBind("@run",run);s.TryBind("@now",DateTime.UtcNow.ToString("o"));});
        }

        private static void Timed(string name,Action<string,TimeSpan> timing,Action action){var c=Stopwatch.StartNew();action();timing?.Invoke(name,c.Elapsed);}

        private static void TempMedia(IDatabaseConnection db)
        {
            db.Execute("DROP TABLE IF EXISTS temp.mf_media");
            db.Execute(@"CREATE TEMP TABLE mf_media AS
SELECT DISTINCT er.person_emby_id,er.media_emby_id,m.item_type media_type,m.name media_name,
       coalesce(er.person_type,'Person') relationship_type,coalesce(er.role,'') relationship_role,
       p.name current_name,lower(trim(p.name)) current_normalized_name,
       p.tmdb_id current_tmdb,p.tvdb_id current_tvdb,p.imdb_id current_imdb,
       coalesce(m.tmdb_id,tr.resolved_tmdb_id) tmdb_media,
       coalesce(m.tvdb_id,vr.resolved_tvdb_id) tvdb_media
FROM emby_relationship er JOIN emby_item p ON p.emby_id=er.person_emby_id
JOIN emby_item m ON m.emby_id=er.media_emby_id
LEFT JOIN tmdb_item_resolution tr ON tr.emby_id=m.emby_id
LEFT JOIN item_resolution vr ON vr.emby_id=m.emby_id
WHERE p.item_type='person'");
            db.Execute("CREATE INDEX temp.ix_mf_media_person ON mf_media(person_emby_id,media_emby_id)");
            db.Execute("CREATE INDEX temp.ix_mf_media_tmdb ON mf_media(media_type,tmdb_media)");
            db.Execute("CREATE INDEX temp.ix_mf_media_tvdb ON mf_media(media_type,tvdb_media)");
            db.Execute("CREATE INDEX temp.ix_mf_media_person_tmdb ON mf_media(person_emby_id,tmdb_media,media_type)");
            db.Execute("CREATE INDEX temp.ix_mf_media_person_tvdb ON mf_media(person_emby_id,tvdb_media,media_type)");
        }

        private static void TempCandidates(IDatabaseConnection db)
        {
            db.Execute("DROP TABLE IF EXISTS temp.mf_tmdb_person_name");
            db.Execute("CREATE TEMP TABLE mf_tmdb_person_name AS SELECT tmdb_id,lower(trim(name)) normalized_name,name FROM tmdb_entity WHERE entity_type='person'");
            db.Execute("CREATE UNIQUE INDEX temp.ix_mf_tmdb_person_name ON mf_tmdb_person_name(tmdb_id)");
            db.Execute("CREATE INDEX temp.ix_mf_tmdb_person_normalized_name ON mf_tmdb_person_name(normalized_name,tmdb_id)");
            db.Execute("DROP TABLE IF EXISTS temp.mf_tvdb_person_name");
            db.Execute("CREATE TEMP TABLE mf_tvdb_person_name AS SELECT tvdb_id,lower(trim(name)) normalized_name,name FROM tvdb_entity WHERE entity_type='person'");
            db.Execute("CREATE UNIQUE INDEX temp.ix_mf_tvdb_person_name ON mf_tvdb_person_name(tvdb_id)");
            db.Execute("CREATE INDEX temp.ix_mf_tvdb_person_normalized_name ON mf_tvdb_person_name(normalized_name,tvdb_id)");
            db.Execute("DROP TABLE IF EXISTS temp.mf_tmdb_name_match");
            db.Execute(@"CREATE TEMP TABLE mf_tmdb_name_match AS
SELECT DISTINCT m.person_emby_id,e.tmdb_id provider_person_id,e.name provider_name
FROM mf_media m JOIN mf_tmdb_person_name e ON e.normalized_name=m.current_normalized_name");
            db.Execute("CREATE UNIQUE INDEX temp.ix_mf_tmdb_name_match ON mf_tmdb_name_match(person_emby_id,provider_person_id)");
            db.Execute("DROP TABLE IF EXISTS temp.mf_tvdb_name_match");
            db.Execute(@"CREATE TEMP TABLE mf_tvdb_name_match AS
SELECT DISTINCT m.person_emby_id,e.tvdb_id provider_person_id,e.name provider_name
FROM mf_media m JOIN mf_tvdb_person_name e ON e.normalized_name=m.current_normalized_name");
            db.Execute("CREATE UNIQUE INDEX temp.ix_mf_tvdb_name_match ON mf_tvdb_name_match(person_emby_id,provider_person_id)");
            db.Execute("DROP TABLE IF EXISTS temp.mf_tmdb_candidate");
            db.Execute(@"CREATE TEMP TABLE mf_tmdb_candidate AS
SELECT DISTINCT m.person_emby_id,m.media_emby_id,c.person_tmdb_id provider_person_id,
       max(n.provider_name) provider_name,group_concat(DISTINCT coalesce(c.job_or_character,'')) provider_role
FROM mf_media m JOIN mf_tmdb_name_match n ON n.person_emby_id=m.person_emby_id
JOIN tmdb_credit_observation c ON c.person_tmdb_id=n.provider_person_id
 AND c.production_type=m.media_type AND c.production_tmdb_id=m.tmdb_media
GROUP BY m.person_emby_id,m.media_emby_id,c.person_tmdb_id");
            db.Execute("CREATE INDEX temp.ix_mf_tmdb_candidate_rel ON mf_tmdb_candidate(person_emby_id,media_emby_id,provider_person_id)");
            db.Execute("CREATE INDEX temp.ix_mf_tmdb_candidate_provider ON mf_tmdb_candidate(provider_person_id,person_emby_id,media_emby_id)");
            db.Execute("DROP TABLE IF EXISTS temp.mf_tvdb_candidate");
            db.Execute(@"CREATE TEMP TABLE mf_tvdb_candidate AS
SELECT DISTINCT m.person_emby_id,m.media_emby_id,c.person_tvdb_id provider_person_id,
       max(n.provider_name) provider_name,group_concat(DISTINCT coalesce(c.role_name,'')) provider_role
FROM mf_media m JOIN mf_tvdb_name_match n ON n.person_emby_id=m.person_emby_id
JOIN tvdb_credit_observation c ON c.person_tvdb_id=n.provider_person_id
 AND c.production_type=m.media_type AND c.production_tvdb_id=m.tvdb_media
GROUP BY m.person_emby_id,m.media_emby_id,c.person_tvdb_id");
            db.Execute("CREATE INDEX temp.ix_mf_tvdb_candidate_rel ON mf_tvdb_candidate(person_emby_id,media_emby_id,provider_person_id)");
            db.Execute("CREATE INDEX temp.ix_mf_tvdb_candidate_provider ON mf_tvdb_candidate(provider_person_id,person_emby_id,media_emby_id)");
            db.Execute("DROP TABLE IF EXISTS temp.mf_tmdb_cardinality");
            db.Execute(@"CREATE TEMP TABLE mf_tmdb_cardinality AS
SELECT person_emby_id,media_emby_id,count(*) candidate_count,min(provider_person_id) unique_provider_person_id
FROM mf_tmdb_candidate GROUP BY person_emby_id,media_emby_id");
            db.Execute("CREATE UNIQUE INDEX temp.ix_mf_tmdb_cardinality ON mf_tmdb_cardinality(person_emby_id,media_emby_id)");
            db.Execute("DROP TABLE IF EXISTS temp.mf_tvdb_cardinality");
            db.Execute(@"CREATE TEMP TABLE mf_tvdb_cardinality AS
SELECT person_emby_id,media_emby_id,count(*) candidate_count,min(provider_person_id) unique_provider_person_id
FROM mf_tvdb_candidate GROUP BY person_emby_id,media_emby_id");
            db.Execute("CREATE UNIQUE INDEX temp.ix_mf_tvdb_cardinality ON mf_tvdb_cardinality(person_emby_id,media_emby_id)");
        }

        private static void TempProviderLinks(IDatabaseConnection db)
        {
            db.Execute("DROP TABLE IF EXISTS temp.mf_pair");
            db.Execute(@"CREATE TEMP TABLE mf_pair AS
SELECT t.provider_person_id tmdb_person,v.provider_person_id tvdb_person,count(DISTINCT t.media_emby_id) overlap
FROM mf_tmdb_candidate t JOIN mf_tvdb_candidate v
 ON v.person_emby_id=t.person_emby_id AND v.media_emby_id=t.media_emby_id
GROUP BY t.provider_person_id,v.provider_person_id");
            db.Execute("CREATE INDEX temp.ix_mf_pair_tmdb ON mf_pair(tmdb_person,tvdb_person)");
            db.Execute("CREATE INDEX temp.ix_mf_pair_tvdb ON mf_pair(tvdb_person,tmdb_person)");
            db.Execute("DROP TABLE IF EXISTS temp.mf_conflated_tvdb");
            db.Execute("CREATE TEMP TABLE mf_conflated_tvdb AS SELECT tvdb_person,count(DISTINCT tmdb_person) clusters FROM mf_pair GROUP BY tvdb_person HAVING count(DISTINCT tmdb_person)>1");
            db.Execute("DROP TABLE IF EXISTS temp.mf_conflated_tmdb");
            db.Execute("CREATE TEMP TABLE mf_conflated_tmdb AS SELECT tmdb_person,count(DISTINCT tvdb_person) clusters FROM mf_pair GROUP BY tmdb_person HAVING count(DISTINCT tvdb_person)>1");
            db.Execute("DROP TABLE IF EXISTS temp.mf_unique_tvdb_link");
            db.Execute(@"CREATE TEMP TABLE mf_unique_tvdb_link AS
SELECT p.tvdb_person,min(p.tmdb_person) tmdb_person,max(p.overlap) overlap
FROM mf_pair p LEFT JOIN mf_conflated_tvdb c ON c.tvdb_person=p.tvdb_person
WHERE c.tvdb_person IS NULL GROUP BY p.tvdb_person HAVING count(DISTINCT p.tmdb_person)=1");
        }

        private static void People(IDatabaseConnection db,long run)
        {
            Exec(db,@"INSERT INTO media_truth_person(run_id,person_key,preferred_name,state,identity_confidence,supporting_media_count,summary)
SELECT @run,'tmdb:'||c.provider_person_id,max(c.provider_name),'established',
       CASE WHEN count(DISTINCT x.source_name)>=2 THEN .99 WHEN count(DISTINCT x.source_name)=1 THEN .97 ELSE .93 END,
       count(DISTINCT c.media_emby_id),
       'TMDB person '||c.provider_person_id||' is instantiated by '||count(DISTINCT c.media_emby_id)||' exact library media credit(s); external identity links='||count(DISTINCT x.source_name)||'.'
FROM mf_tmdb_candidate c LEFT JOIN tmdb_external_id x ON x.entity_type='person' AND x.tmdb_id=c.provider_person_id AND x.source_name IN('imdb','wikidata')
GROUP BY c.provider_person_id",s=>s.TryBind("@run",run));
            Exec(db,@"INSERT INTO media_truth_identity(truth_person_id,provider,external_id,identity_state,confidence,provenance,summary)
SELECT p.truth_person_id,'tmdb',substr(p.person_key,6),'established',p.identity_confidence,'exact-media-credit','TMDB identity directly supplies exact media credits.'
FROM media_truth_person p WHERE p.run_id=@run AND p.person_key LIKE 'tmdb:%'",s=>s.TryBind("@run",run));
            Exec(db,@"INSERT OR IGNORE INTO media_truth_identity(truth_person_id,provider,external_id,identity_state,confidence,provenance,summary)
SELECT p.truth_person_id,x.source_name,x.external_id,'corroborating',.99,'tmdb-external-id','Direct TMDB external-ID crosswalk.'
FROM media_truth_person p JOIN tmdb_external_id x ON x.entity_type='person' AND x.tmdb_id=substr(p.person_key,6) AND x.source_name IN('imdb','wikidata')
WHERE p.run_id=@run",s=>s.TryBind("@run",run));
            Exec(db,@"INSERT OR IGNORE INTO media_truth_identity(truth_person_id,provider,external_id,identity_state,confidence,provenance,summary)
SELECT p.truth_person_id,'tvdb',l.tvdb_person,'corroborating',CASE WHEN l.overlap>=2 THEN .95 ELSE .80 END,'unique-media-overlap',
       'TVDB identity has a unique TMDB counterpart across '||l.overlap||' exact crosswalked media credit(s).'
FROM media_truth_person p JOIN mf_unique_tvdb_link l ON l.tmdb_person=substr(p.person_key,6)
WHERE p.run_id=@run AND l.overlap>=2",s=>s.TryBind("@run",run));
            Exec(db,@"UPDATE media_truth_identity SET identity_state='withheld-conflict',confidence=.20,
summary='Multiple external IDs for this provider are archived on the same reconstructed person; withhold all until reviewed.'
WHERE truth_person_id IN(SELECT truth_person_id FROM media_truth_person WHERE run_id=@run)
AND (truth_person_id,provider) IN(
 SELECT truth_person_id,provider FROM media_truth_identity GROUP BY truth_person_id,provider HAVING count(DISTINCT external_id)>1)",s=>s.TryBind("@run",run));
            db.Execute("DROP TABLE IF EXISTS temp.mf_owner");
            db.Execute("CREATE TEMP TABLE mf_owner(tmdb_person TEXT PRIMARY KEY,person_emby_id INTEGER NOT NULL UNIQUE)");
            MaterializeOneToOneOwners(db);
            Exec(db,@"UPDATE media_truth_person
SET continuity_emby_id=(SELECT person_emby_id FROM mf_owner o WHERE o.tmdb_person=substr(media_truth_person.person_key,6))
WHERE run_id=@run",s=>s.TryBind("@run",run));
            Exec(db,@"INSERT INTO media_truth_lineage(truth_person_id,emby_person_id,disposition,relationship_count,summary)
SELECT p.truth_person_id,c.person_emby_id,CASE WHEN c.person_emby_id=p.continuity_emby_id THEN 'continuity-owner' ELSE 'source-container' END,
       count(DISTINCT c.media_emby_id),'Current Emby container contributes exact media relationships to reconstructed cluster.'
FROM media_truth_person p JOIN mf_tmdb_candidate c ON p.person_key='tmdb:'||c.provider_person_id
WHERE p.run_id=@run GROUP BY p.truth_person_id,c.person_emby_id",s=>s.TryBind("@run",run));
        }

        private static void CreditsAndIssues(IDatabaseConnection db,long run,Action<string,TimeSpan> timing)
        {
            Timed("auto credit decisions",timing,()=>Exec(db,@"INSERT INTO media_truth_credit(run_id,truth_person_id,media_emby_id,relationship_type,relationship_role,current_emby_person_id,disposition,decision_class,identity_confidence,relationship_confidence,operation_confidence,summary)
SELECT @run,p.truth_person_id,m.media_emby_id,m.relationship_type,m.relationship_role,m.person_emby_id,
       CASE WHEN m.person_emby_id=p.continuity_emby_id THEN 'retain' ELSE 'move' END,'auto-commit',p.identity_confidence,.99,
       CASE WHEN m.person_emby_id=p.continuity_emby_id THEN .99 ELSE .97 END,
       CASE WHEN m.person_emby_id=p.continuity_emby_id THEN 'Retain exact relationship on reconstructed person.' ELSE 'Move exact relationship to reconstructed continuity owner.' END
FROM mf_media m JOIN mf_tmdb_cardinality k ON k.person_emby_id=m.person_emby_id AND k.media_emby_id=m.media_emby_id AND k.candidate_count=1
JOIN mf_tmdb_candidate c ON c.person_emby_id=m.person_emby_id AND c.media_emby_id=m.media_emby_id AND c.provider_person_id=k.unique_provider_person_id
JOIN media_truth_person p ON p.run_id=@run AND p.person_key='tmdb:'||c.provider_person_id
",s=>s.TryBind("@run",run)));
            Timed("TMDB credit evidence",timing,()=>Exec(db,@"INSERT INTO media_truth_credit_evidence(truth_credit_id,provider,provider_person_id,provider_media_id,evidence_state,role,confidence,summary)
SELECT cr.truth_credit_id,'tmdb',substr(p.person_key,6),m.tmdb_media,'exact-support',c.provider_role,.99,'TMDB exact media cast supports this reconstructed relationship.'
FROM media_truth_credit cr JOIN media_truth_person p ON p.truth_person_id=cr.truth_person_id
JOIN mf_media m ON m.media_emby_id=cr.media_emby_id AND m.person_emby_id=cr.current_emby_person_id
 AND m.relationship_type=cr.relationship_type AND m.relationship_role=cr.relationship_role
JOIN mf_tmdb_candidate c ON c.media_emby_id=m.media_emby_id AND c.person_emby_id=m.person_emby_id AND c.provider_person_id=substr(p.person_key,6)
WHERE cr.run_id=@run",s=>s.TryBind("@run",run)));
            Timed("TVDB credit evidence",timing,()=>Exec(db,@"INSERT OR IGNORE INTO media_truth_credit_evidence(truth_credit_id,provider,provider_person_id,provider_media_id,evidence_state,role,confidence,summary)
SELECT cr.truth_credit_id,'tvdb',v.provider_person_id,m.tvdb_media,
       CASE WHEN cf.tvdb_person IS NULL THEN 'exact-support' ELSE 'withheld-conflated-provider' END,v.provider_role,
       CASE WHEN cf.tvdb_person IS NULL THEN .95 ELSE .35 END,
       CASE WHEN cf.tvdb_person IS NULL THEN 'TVDB exact media cast independently supports this relationship.' ELSE 'TVDB person spans multiple incompatible TMDB clusters and is withheld from identity truth.' END
FROM media_truth_credit cr JOIN mf_media m ON m.media_emby_id=cr.media_emby_id AND m.person_emby_id=cr.current_emby_person_id
 AND m.relationship_type=cr.relationship_type AND m.relationship_role=cr.relationship_role
JOIN mf_tvdb_candidate v ON v.media_emby_id=m.media_emby_id AND v.person_emby_id=m.person_emby_id
LEFT JOIN mf_conflated_tvdb cf ON cf.tvdb_person=v.provider_person_id WHERE cr.run_id=@run",s=>s.TryBind("@run",run)));
            Timed("relationship review issues",timing,()=>Exec(db,@"INSERT INTO media_truth_issue(run_id,issue_key,issue_type,person_name,media_emby_id,current_emby_person_id,relationship_type,relationship_role,identity_confidence,relationship_confidence,operation_confidence,summary)
SELECT @run,'credit:'||m.person_emby_id||':'||m.media_emby_id||':'||m.relationship_type||':'||m.relationship_role,
       CASE WHEN coalesce(t.candidate_count,0)>1 THEN 'ambiguous-person-cluster'
            WHEN coalesce(v.candidate_count,0)>0 AND coalesce(t.candidate_count,0)=0 THEN 'credit-provider-conflict'
            ELSE 'media-unresolved' END,
       m.current_name,m.media_emby_id,m.person_emby_id,m.relationship_type,m.relationship_role,
       CASE WHEN coalesce(t.candidate_count,0)>1 THEN .50 ELSE .35 END,
       CASE WHEN coalesce(v.candidate_count,0)>0 THEN .35 ELSE .20 END,0,
       CASE WHEN coalesce(t.candidate_count,0)>1 THEN 'Multiple name-compatible TMDB people are credited on the exact media; choose the real relationship owner.'
            WHEN coalesce(v.candidate_count,0)>0 THEN 'TVDB supports a name-compatible person but TMDB exact media cast does not; preserve the current relationship pending review.'
            ELSE 'No unique exact provider person cluster supports the current relationship; preserve it pending evidence or review.' END
FROM mf_media m LEFT JOIN mf_tmdb_cardinality t ON t.person_emby_id=m.person_emby_id AND t.media_emby_id=m.media_emby_id
LEFT JOIN mf_tvdb_cardinality v ON v.person_emby_id=m.person_emby_id AND v.media_emby_id=m.media_emby_id
WHERE coalesce(t.candidate_count,0)<>1
",s=>s.TryBind("@run",run)));
            Timed("provider conflation issues",timing,()=>Exec(db,@"INSERT INTO media_truth_issue(run_id,issue_key,issue_type,person_name,identity_confidence,relationship_confidence,operation_confidence,summary)
SELECT @run,'provider:tvdb:'||c.tvdb_person,'provider-person-conflation',max(v.provider_name),.20,.20,0,
       'TVDB person '||c.tvdb_person||' partitions across '||c.clusters||' distinct TMDB person clusters; its identity is withheld and affected exact credits are evaluated individually.'
FROM mf_conflated_tvdb c JOIN mf_tvdb_candidate v ON v.provider_person_id=c.tvdb_person GROUP BY c.tvdb_person",s=>s.TryBind("@run",run)));
            Timed("external identity conflicts",timing,()=>Exec(db,@"INSERT INTO media_truth_issue(run_id,issue_key,issue_type,person_name,current_emby_person_id,identity_confidence,relationship_confidence,operation_confidence,summary)
SELECT @run,'identity:'||p.truth_person_id||':'||i.provider,'identity-conflict',p.preferred_name,p.continuity_emby_id,.20,1,0,
       upper(i.provider)||' has multiple archived IDs for this reconstructed person: '||group_concat(i.external_id,', ')||'. All are withheld from derived truth pending review.'
FROM media_truth_person p JOIN media_truth_identity i ON i.truth_person_id=p.truth_person_id
WHERE p.run_id=@run AND i.identity_state='withheld-conflict'
GROUP BY p.truth_person_id,i.provider",s=>s.TryBind("@run",run)));
        }

        private static void Changes(IDatabaseConnection db,long run)
        {
            Exec(db,@"INSERT INTO media_truth_change(run_id,truth_person_id,change_order,change_type,decision_class,source_emby_id,target_emby_id,identity_confidence,relationship_confidence,operation_confidence,precondition,summary)
SELECT @run,p.truth_person_id,10,CASE WHEN p.continuity_emby_id IS NULL THEN 'create-person' ELSE 'reuse-person' END,'auto-commit',p.continuity_emby_id,p.continuity_emby_id,p.identity_confidence,1,.98,
       'Identity cluster remains non-contradictory and exact supporting media remain present',
       CASE WHEN p.continuity_emby_id IS NULL THEN 'Create a derived-truth person for supported cluster.' ELSE 'Reuse Emby '||p.continuity_emby_id||' as the continuity owner.' END
FROM media_truth_person p WHERE p.run_id=@run AND p.state='established'",s=>s.TryBind("@run",run));
            Exec(db,@"INSERT INTO media_truth_change(run_id,truth_person_id,change_order,change_type,decision_class,source_emby_id,target_emby_id,media_emby_id,identity_confidence,relationship_confidence,operation_confidence,precondition,summary)
SELECT @run,cr.truth_person_id,100+cr.truth_credit_id,CASE cr.disposition WHEN 'retain' THEN 'retain-relationship' ELSE 'move-relationship' END,'auto-commit',cr.current_emby_person_id,p.continuity_emby_id,cr.media_emby_id,cr.identity_confidence,cr.relationship_confidence,cr.operation_confidence,
       'Exact provider credit and identity cluster remain established',cr.summary
FROM media_truth_credit cr JOIN media_truth_person p ON p.truth_person_id=cr.truth_person_id WHERE cr.run_id=@run",s=>s.TryBind("@run",run));
            Exec(db,@"INSERT INTO media_truth_change(run_id,issue_id,change_order,change_type,decision_class,source_emby_id,target_emby_id,media_emby_id,identity_confidence,relationship_confidence,operation_confidence,precondition,summary)
SELECT @run,i.issue_id,10,'preserve-relationship','human-review',i.current_emby_person_id,i.current_emby_person_id,i.media_emby_id,i.identity_confidence,i.relationship_confidence,0,
       'No derived-truth mutation until operator or new evidence resolves the exact credit','Preserve the current relationship while presenting the provider evidence for human review.'
FROM media_truth_issue i WHERE i.run_id=@run AND i.media_emby_id IS NOT NULL",s=>s.TryBind("@run",run));
        }

        private static void Projections(IDatabaseConnection db,long run)
        {
            db.Execute("DROP TABLE IF EXISTS temp.mf_projection_lineage");
            Exec(db,@"CREATE TEMP TABLE mf_projection_lineage AS
SELECT l.truth_person_id,group_concat(l.emby_person_id) emby_ids,group_concat(DISTINCT e.name) names,
       group_concat(DISTINCT e.tmdb_id) tmdb_ids,group_concat(DISTINCT e.tvdb_id) tvdb_ids,
       group_concat(DISTINCT e.imdb_id) imdb_ids,count(*) lineage_count
FROM media_truth_lineage l JOIN media_truth_person p ON p.truth_person_id=l.truth_person_id
JOIN emby_item e ON e.emby_id=l.emby_person_id WHERE p.run_id=@run GROUP BY l.truth_person_id",s=>s.TryBind("@run",run));
            db.Execute("CREATE UNIQUE INDEX temp.ix_mf_projection_lineage ON mf_projection_lineage(truth_person_id)");
            db.Execute("DROP TABLE IF EXISTS temp.mf_projection_identity");
            Exec(db,@"CREATE TEMP TABLE mf_projection_identity AS
SELECT i.truth_person_id,
 max(CASE WHEN provider='tmdb' AND identity_state IN('established','corroborating') THEN external_id END) tmdb_id,
 max(CASE WHEN provider='tvdb' AND identity_state IN('established','corroborating') THEN external_id END) tvdb_id,
 max(CASE WHEN provider='imdb' AND identity_state IN('established','corroborating') THEN external_id END) imdb_id
FROM media_truth_identity i JOIN media_truth_person p ON p.truth_person_id=i.truth_person_id
WHERE p.run_id=@run GROUP BY i.truth_person_id",s=>s.TryBind("@run",run));
            db.Execute("CREATE UNIQUE INDEX temp.ix_mf_projection_identity ON mf_projection_identity(truth_person_id)");
            db.Execute("DROP TABLE IF EXISTS temp.mf_projection_credit");
            Exec(db,@"CREATE TEMP TABLE mf_projection_credit AS
SELECT truth_person_id,sum(CASE disposition WHEN 'move' THEN 1 ELSE 0 END) move_count
FROM media_truth_credit WHERE run_id=@run GROUP BY truth_person_id",s=>s.TryBind("@run",run));
            db.Execute("CREATE UNIQUE INDEX temp.ix_mf_projection_credit ON mf_projection_credit(truth_person_id)");
            db.Execute("DROP TABLE IF EXISTS temp.mf_projection_change");
            Exec(db,@"CREATE TEMP TABLE mf_projection_change AS
SELECT truth_person_id,min(operation_confidence) operation_confidence
FROM media_truth_change WHERE run_id=@run AND truth_person_id IS NOT NULL GROUP BY truth_person_id",s=>s.TryBind("@run",run));
            db.Execute("CREATE UNIQUE INDEX temp.ix_mf_projection_change ON mf_projection_change(truth_person_id)");
            Exec(db,@"INSERT INTO media_truth_projection(run_id,projection_key,decision_class,summary,truth_result,evidence_summary,current_emby_ids,proposed_emby_ids,current_names,proposed_names,current_tmdb_ids,proposed_tmdb_ids,current_tvdb_ids,proposed_tvdb_ids,current_imdb_ids,proposed_imdb_ids,identity_confidence,relationship_confidence,operation_confidence,supporting_media_count,affected_relationship_count)
SELECT @run,'person:'||p.truth_person_id,'auto-commit',p.preferred_name||' — '||p.supporting_media_count||' media credit(s)',
       CASE WHEN p.continuity_emby_id IS NULL THEN 'Create one derived-truth person' ELSE 'Reuse Emby '||p.continuity_emby_id END,
       p.summary,
       coalesce(l.emby_ids,''),coalesce(cast(p.continuity_emby_id AS text),'new'),coalesce(l.names,''),p.preferred_name,
       coalesce(l.tmdb_ids,''),coalesce(i.tmdb_id,''),coalesce(l.tvdb_ids,''),coalesce(i.tvdb_id,''),
       coalesce(l.imdb_ids,''),coalesce(i.imdb_id,''),p.identity_confidence,.99,coalesce(ch.operation_confidence,.95),
       p.supporting_media_count,coalesce(cr.move_count,0)
FROM media_truth_person p
LEFT JOIN mf_projection_lineage l ON l.truth_person_id=p.truth_person_id
LEFT JOIN mf_projection_identity i ON i.truth_person_id=p.truth_person_id
LEFT JOIN mf_projection_credit cr ON cr.truth_person_id=p.truth_person_id
LEFT JOIN mf_projection_change ch ON ch.truth_person_id=p.truth_person_id
LEFT JOIN emby_item owner ON owner.emby_id=p.continuity_emby_id
WHERE p.run_id=@run AND (p.continuity_emby_id IS NULL OR coalesce(cr.move_count,0)>0 OR coalesce(l.lineage_count,0)>1
 OR coalesce(i.tmdb_id,'')<>coalesce(owner.tmdb_id,'') OR coalesce(i.tvdb_id,'')<>coalesce(owner.tvdb_id,'')
 OR coalesce(i.imdb_id,'')<>coalesce(owner.imdb_id,''))",s=>s.TryBind("@run",run));
            Exec(db,@"INSERT INTO media_truth_projection(run_id,projection_key,decision_class,summary,truth_result,evidence_summary,current_emby_ids,proposed_emby_ids,current_names,proposed_names,current_tmdb_ids,proposed_tmdb_ids,current_tvdb_ids,proposed_tvdb_ids,current_imdb_ids,proposed_imdb_ids,identity_confidence,relationship_confidence,operation_confidence,supporting_media_count,affected_relationship_count)
SELECT @run,'issue:'||i.issue_id,'human-review',coalesce(m.name,i.person_name)||' — '||replace(i.issue_type,'-',' '),
       CASE WHEN i.media_emby_id IS NULL THEN 'Withhold provider identity from truth' ELSE 'Preserve current relationship pending decision' END,
       i.summary,coalesce(cast(i.current_emby_person_id AS text),''),coalesce(cast(i.current_emby_person_id AS text),''),coalesce(i.person_name,''),coalesce(i.person_name,''),
       coalesce(p.tmdb_id,''),'',coalesce(p.tvdb_id,''),'',coalesce(p.imdb_id,''),'',i.identity_confidence,i.relationship_confidence,i.operation_confidence,0,CASE WHEN i.media_emby_id IS NULL THEN 0 ELSE 1 END
FROM media_truth_issue i LEFT JOIN emby_item m ON m.emby_id=i.media_emby_id LEFT JOIN emby_item p ON p.emby_id=i.current_emby_person_id WHERE i.run_id=@run",s=>s.TryBind("@run",run));
        }

        private static void Validate(IDatabaseConnection db,long run)
        {
            AssertZero(db,run,"auto relationship without exact provider support","SELECT count(*) FROM media_truth_credit c WHERE c.run_id=@run AND c.decision_class='auto-commit' AND NOT EXISTS(SELECT 1 FROM media_truth_credit_evidence e WHERE e.truth_credit_id=c.truth_credit_id AND e.evidence_state='exact-support')");
            AssertZero(db,run,"human-review change mutates derived truth","SELECT count(*) FROM media_truth_change WHERE run_id=@run AND decision_class='human-review' AND change_type<>'preserve-relationship'");
            AssertZero(db,run,"conflated TVDB identity committed","SELECT count(*) FROM media_truth_identity i JOIN media_truth_person p ON p.truth_person_id=i.truth_person_id JOIN mf_conflated_tvdb c ON c.tvdb_person=i.external_id WHERE p.run_id=@run AND i.provider='tvdb' AND i.identity_state IN('established','corroborating')");
            AssertZero(db,run,"current relationship disappeared from derived truth","SELECT count(*) FROM mf_media m WHERE NOT EXISTS(SELECT 1 FROM media_truth_credit c WHERE c.run_id=@run AND c.current_emby_person_id=m.person_emby_id AND c.media_emby_id=m.media_emby_id AND c.relationship_type=m.relationship_type AND c.relationship_role=m.relationship_role) AND NOT EXISTS(SELECT 1 FROM media_truth_issue i WHERE i.run_id=@run AND i.current_emby_person_id=m.person_emby_id AND i.media_emby_id=m.media_emby_id AND i.relationship_type=m.relationship_type AND i.relationship_role=m.relationship_role)");
        }

        private static void MaterializeOneToOneOwners(IDatabaseConnection db)
        {
            var choices=new Dictionary<string,List<Tuple<long,int>>>(StringComparer.Ordinal);
            using(var s=db.PrepareStatement(@"SELECT provider_person_id,person_emby_id,count(DISTINCT media_emby_id) support
FROM mf_tmdb_candidate GROUP BY provider_person_id,person_emby_id
ORDER BY provider_person_id,support DESC,person_emby_id"))
                foreach(var r in s.ExecuteQuery())
                {
                    var provider=r.GetString(0);
                    if(!choices.TryGetValue(provider,out var candidates))choices.Add(provider,candidates=new List<Tuple<long,int>>());
                    candidates.Add(Tuple.Create(r.GetInt64(1),r.GetInt(2)));
                }
            var providers=choices.Keys.OrderByDescending(x=>choices[x][0].Item2).ThenBy(x=>x,StringComparer.Ordinal).ToList();
            var ownerByEmby=new Dictionary<long,string>();
            foreach(var provider in providers)TryAssignOwner(provider,choices,ownerByEmby,new HashSet<string>(StringComparer.Ordinal));
            foreach(var assignment in ownerByEmby)
                Exec(db,"INSERT INTO mf_owner(tmdb_person,person_emby_id) VALUES(@provider,@emby)",s=>{s.TryBind("@provider",assignment.Value);s.TryBind("@emby",assignment.Key);});
        }

        private static bool TryAssignOwner(string provider,Dictionary<string,List<Tuple<long,int>>> choices,Dictionary<long,string> ownerByEmby,HashSet<string> visitedProviders)
        {
            if(!visitedProviders.Add(provider))return false;
            foreach(var candidate in choices[provider])
            {
                var emby=candidate.Item1;
                if(!ownerByEmby.TryGetValue(emby,out var displaced) || TryAssignOwner(displaced,choices,ownerByEmby,visitedProviders))
                {
                    ownerByEmby[emby]=provider;
                    return true;
                }
            }
            return false;
        }

        private static void AssertZero(IDatabaseConnection db,long run,string invariant,string sql)
        {
            using(var s=db.PrepareStatement(sql)){s.TryBind("@run",run);foreach(var r in s.ExecuteQuery())if(r.GetInt64(0)!=0)throw new InvalidOperationException("Media-first invariant failed: "+invariant+" ("+r.GetInt64(0)+")");}
        }
        private static void Exec(IDatabaseConnection db,string sql,Action<IStatement> bind){using(var s=db.PrepareStatement(sql)){bind(s);while(s.MoveNext()){};}}
        private static long LastId(IDatabaseConnection db){using(var s=db.PrepareStatement("SELECT last_insert_rowid()"))foreach(var r in s.ExecuteQuery())return r.GetInt64(0);throw new InvalidOperationException("SQLite did not return last_insert_rowid()");}
    }
}
