using PersonCleaner.Storage;
using SQLitePCL.pretty;
using SQLitePCLEx;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PersonCleaner.Housekeeping
{
    /// <summary>
    /// Converts detector findings into normalized-v17 operator cases using the
    /// normalized-v15/Migration-10 case graph. Detector
    /// rows are deliberately treated as nominations: no operation follows from
    /// media co-occurrence or from current Emby ownership alone.
    /// </summary>
    internal static class HousekeepingCasePlanner
    {
        internal static readonly List<string> LastTimings = new List<string>();
        internal static void Materialize(IDatabaseConnection x, long run)
        {
            LastTimings.Clear();
            Action<string,Action> step=(name,action)=>{var clock=Stopwatch.StartNew();action();LastTimings.Add(name+"="+clock.Elapsed.TotalSeconds.ToString("0.000")+"s");};
            step("clear",()=>Clear(x,run));
            step("duplicate-identity-equivalence",()=>BuildDuplicateIdentityEquivalence(x));
            step("duplicates",()=>DuplicatePeople(x,run));
            step("provider-repairs",()=>ProviderIdentityRepairs(x,run));
            step("provider-removals",()=>ProviderIdentityRemovals(x,run));
            step("cross-provider-splits",()=>CrossProviderSplits(x,run));
            step("unresolved",()=>UnresolvedPartitions(x,run));
            step("names",()=>Names(x,run));
            step("general-evidence",()=>GeneralEvidence(x,run));
            step("relationship-evidence",()=>RelationshipEvidence(x,run));
            step("projections",()=>Projections(x,run));
            step("validate",()=>Validate(x,run));
            step("drop-temporary-evidence",()=>{Exec(x,"DROP TABLE IF EXISTS temp.hk_duplicate_identity_equivalence",s=>{});Exec(x,"DROP TABLE IF EXISTS temp.hk_duplicate_linked_media",s=>{});});
        }

        private static void Clear(IDatabaseConnection x,long run)=>Exec(x,
            "DELETE FROM housekeeping_case WHERE run_id=@run",s=>s.TryBind("@run",run));

        // Candidate acquisition is deliberately broader than final detector
        // recommendations.  This case-local index promotes an alternate provider
        // person into the assigned real-person cluster only when an independent
        // external ID agrees, or when a compatible name has at least two exact
        // cross-provider media overlaps.  Contradictory external IDs or birth/death
        // data block the edge.  The edge is pairwise and never transitive.
        private static void BuildDuplicateIdentityEquivalence(IDatabaseConnection x)
        {
            Exec(x,"DROP TABLE IF EXISTS temp.hk_duplicate_identity_equivalence",s=>{});
            Exec(x,"DROP TABLE IF EXISTS temp.hk_duplicate_linked_media",s=>{});
            Exec(x,@"CREATE TEMP TABLE hk_duplicate_linked_media(
 tmdb_id TEXT NOT NULL,
 tvdb_id TEXT NOT NULL,
 resolved_imdb TEXT NOT NULL,
 media_emby_id INTEGER NOT NULL,
 media_type TEXT NOT NULL,
 tmdb_production_id TEXT,
 tvdb_production_id TEXT,
 PRIMARY KEY(tmdb_id,tvdb_id,resolved_imdb,media_emby_id)
)",s=>{});
            Exec(x,@"WITH people AS (
 SELECT p.emby_id,p.tmdb_id,p.tvdb_id,coalesce(p.imdb_id,(SELECT external_id FROM tmdb_external_id x WHERE x.entity_type='person' AND x.tmdb_id=p.tmdb_id AND x.source_name='imdb' LIMIT 1)) resolved_imdb
 FROM emby_item p WHERE p.item_type='person'
), duplicate_groups AS (
 SELECT tmdb_id,tvdb_id,resolved_imdb FROM people WHERE tmdb_id IS NOT NULL AND tvdb_id IS NOT NULL AND resolved_imdb IS NOT NULL GROUP BY tmdb_id,tvdb_id,resolved_imdb HAVING count(*)>1
)
INSERT INTO hk_duplicate_linked_media(tmdb_id,tvdb_id,resolved_imdb,media_emby_id,media_type,tmdb_production_id,tvdb_production_id)
SELECT DISTINCT g.tmdb_id,g.tvdb_id,g.resolved_imdb,er.media_emby_id,m.item_type,coalesce(m.tmdb_id,tr.resolved_tmdb_id),coalesce(m.tvdb_id,vr.resolved_tvdb_id)
FROM duplicate_groups g JOIN people p ON p.tmdb_id=g.tmdb_id AND p.tvdb_id=g.tvdb_id AND p.resolved_imdb=g.resolved_imdb
JOIN emby_relationship er ON er.person_emby_id=p.emby_id JOIN emby_item m ON m.emby_id=er.media_emby_id
LEFT JOIN tmdb_item_resolution tr ON tr.emby_id=m.emby_id LEFT JOIN item_resolution vr ON vr.emby_id=m.emby_id",s=>{});
            Exec(x,@"CREATE TEMP TABLE hk_duplicate_identity_equivalence(
 tmdb_id TEXT NOT NULL,
 tvdb_id TEXT NOT NULL,
 resolved_imdb TEXT NOT NULL,
 candidate_provider TEXT NOT NULL,
 candidate_provider_id TEXT NOT NULL,
 candidate_name TEXT,
 counterpart_provider TEXT NOT NULL,
 counterpart_provider_id TEXT NOT NULL,
 exact_cross_provider_overlap INTEGER NOT NULL,
 linked_media_support INTEGER NOT NULL,
 identity_confidence REAL NOT NULL,
 acceptance_path TEXT NOT NULL,
 summary TEXT NOT NULL,
 PRIMARY KEY(tmdb_id,tvdb_id,resolved_imdb,candidate_provider,candidate_provider_id)
)",s=>{});
            Exec(x,@"WITH people AS (
 SELECT p.emby_id,p.name,p.tmdb_id,p.tvdb_id,coalesce(p.imdb_id,(SELECT external_id FROM tmdb_external_id x WHERE x.entity_type='person' AND x.tmdb_id=p.tmdb_id AND x.source_name='imdb' LIMIT 1)) resolved_imdb
 FROM emby_item p WHERE p.item_type='person'
), duplicate_groups AS (
 SELECT tmdb_id,tvdb_id,resolved_imdb
 FROM people WHERE tmdb_id IS NOT NULL AND tvdb_id IS NOT NULL AND resolved_imdb IS NOT NULL
 GROUP BY tmdb_id,tvdb_id,resolved_imdb HAVING count(*)>1
), candidates AS (
 SELECT DISTINCT g.tmdb_id,g.tvdb_id,g.resolved_imdb,p.emby_id,p.name current_name,'tvdb' candidate_provider,rc.tvdb_id candidate_provider_id,e.name candidate_name,'tmdb' counterpart_provider,g.tmdb_id counterpart_provider_id
 FROM duplicate_groups g JOIN people p ON p.tmdb_id=g.tmdb_id AND p.tvdb_id=g.tvdb_id AND p.resolved_imdb=g.resolved_imdb
 JOIN resolution_candidate rc ON rc.emby_id=p.emby_id AND rc.entity_type='person' AND rc.tvdb_id IS NOT NULL AND rc.tvdb_id<>g.tvdb_id
 JOIN provider_entity e ON e.provider='tvdb' AND e.entity_type='person' AND e.provider_id=rc.tvdb_id
 UNION
 SELECT DISTINCT g.tmdb_id,g.tvdb_id,g.resolved_imdb,p.emby_id,p.name,'tmdb',rc.tmdb_id,e.name,'tvdb',g.tvdb_id
 FROM duplicate_groups g JOIN people p ON p.tmdb_id=g.tmdb_id AND p.tvdb_id=g.tvdb_id AND p.resolved_imdb=g.resolved_imdb
 JOIN tmdb_resolution_candidate rc ON rc.emby_id=p.emby_id AND rc.entity_type='person' AND rc.tmdb_id IS NOT NULL AND rc.tmdb_id<>g.tmdb_id
 JOIN provider_entity e ON e.provider='tmdb' AND e.entity_type='person' AND e.provider_id=rc.tmdb_id
), scored AS (
 SELECT c.*,
  CASE WHEN lower(trim(c.candidate_name))=lower(trim(c.current_name)) OR lower(trim(c.current_name)) LIKE lower(trim(c.candidate_name))||' %' OR lower(trim(c.candidate_name)) LIKE lower(trim(c.current_name))||' %' OR EXISTS(
   SELECT 1 FROM provider_alias a WHERE a.provider=c.candidate_provider AND a.entity_type='person' AND a.provider_id=c.candidate_provider_id AND
    (lower(trim(a.alias))=lower(trim(c.current_name)) OR lower(trim(c.current_name)) LIKE lower(trim(a.alias))||' %' OR lower(trim(a.alias)) LIKE lower(trim(c.current_name))||' %')) THEN 1 ELSE 0 END name_compatible,
  CASE WHEN EXISTS(SELECT 1 FROM provider_external_id x WHERE x.provider=c.candidate_provider AND x.entity_type='person' AND x.provider_id=c.candidate_provider_id AND
   ((lower(x.source_name)='imdb' AND x.external_id=c.resolved_imdb) OR (c.candidate_provider='tvdb' AND lower(x.source_name) IN('themoviedb.com','tmdb') AND x.external_id=c.tmdb_id) OR (c.candidate_provider='tmdb' AND lower(x.source_name)='tvdb' AND x.external_id=c.tvdb_id))) THEN 1 ELSE 0 END external_match,
  CASE WHEN EXISTS(SELECT 1 FROM provider_external_id x WHERE x.provider=c.candidate_provider AND x.entity_type='person' AND x.provider_id=c.candidate_provider_id AND
   ((lower(x.source_name)='imdb' AND x.external_id<>c.resolved_imdb) OR (c.candidate_provider='tvdb' AND lower(x.source_name) IN('themoviedb.com','tmdb') AND x.external_id<>c.tmdb_id) OR (c.candidate_provider='tmdb' AND lower(x.source_name)='tvdb' AND x.external_id<>c.tvdb_id))) THEN 1 ELSE 0 END external_conflict,
  CASE WHEN EXISTS(SELECT 1 FROM provider_entity candidate JOIN provider_entity assigned ON assigned.entity_type='person' AND ((assigned.provider='tmdb' AND assigned.provider_id=c.tmdb_id) OR (assigned.provider='tvdb' AND assigned.provider_id=c.tvdb_id))
   WHERE candidate.provider=c.candidate_provider AND candidate.entity_type='person' AND candidate.provider_id=c.candidate_provider_id AND
    ((candidate.birth_date IS NOT NULL AND assigned.birth_date IS NOT NULL AND candidate.birth_date<>assigned.birth_date) OR (candidate.death_date IS NOT NULL AND assigned.death_date IS NOT NULL AND candidate.death_date<>assigned.death_date))) THEN 1 ELSE 0 END biographical_conflict,
  (SELECT count(*) FROM hk_duplicate_linked_media lm
   WHERE lm.tmdb_id=c.tmdb_id AND lm.tvdb_id=c.tvdb_id AND lm.resolved_imdb=c.resolved_imdb AND
    EXISTS(SELECT 1 FROM provider_credit_observation pc WHERE pc.provider=c.candidate_provider AND pc.production_type=lm.media_type AND pc.production_provider_id=CASE c.candidate_provider WHEN 'tmdb' THEN lm.tmdb_production_id ELSE lm.tvdb_production_id END AND pc.person_provider_id=c.candidate_provider_id) AND
    EXISTS(SELECT 1 FROM provider_credit_observation pc WHERE pc.provider=c.counterpart_provider AND pc.production_type=lm.media_type AND pc.production_provider_id=CASE c.counterpart_provider WHEN 'tmdb' THEN lm.tmdb_production_id ELSE lm.tvdb_production_id END AND pc.person_provider_id=c.counterpart_provider_id)) exact_overlap,
  (SELECT count(*) FROM hk_duplicate_linked_media lm
   WHERE lm.tmdb_id=c.tmdb_id AND lm.tvdb_id=c.tvdb_id AND lm.resolved_imdb=c.resolved_imdb AND EXISTS(SELECT 1 FROM provider_credit_observation pc WHERE pc.provider=c.candidate_provider AND pc.production_type=lm.media_type AND pc.production_provider_id=CASE c.candidate_provider WHEN 'tmdb' THEN lm.tmdb_production_id ELSE lm.tvdb_production_id END AND pc.person_provider_id=c.candidate_provider_id)) candidate_support
 FROM candidates c
)
INSERT INTO hk_duplicate_identity_equivalence(tmdb_id,tvdb_id,resolved_imdb,candidate_provider,candidate_provider_id,candidate_name,counterpart_provider,counterpart_provider_id,exact_cross_provider_overlap,linked_media_support,identity_confidence,acceptance_path,summary)
SELECT tmdb_id,tvdb_id,resolved_imdb,candidate_provider,candidate_provider_id,candidate_name,counterpart_provider,counterpart_provider_id,exact_overlap,candidate_support,
 CASE WHEN external_match=1 THEN .99 WHEN exact_overlap>=3 THEN .97 ELSE .92 END,
 CASE WHEN external_match=1 THEN 'external-id-crosswalk' ELSE 'multiple-exact-cross-provider-media' END,
 upper(candidate_provider)||' '||candidate_provider_id||' '||coalesce(candidate_name,'(name unavailable)')||' is a case-local supporting identity: exact cross-provider linked-media overlap='||exact_overlap||'; linked relationships supported='||candidate_support||'; external-ID agreement='||external_match||'. It supplements the retained provider identity and is not itself an ID-change instruction.'
FROM scored WHERE external_conflict=0 AND biographical_conflict=0 AND candidate_support>0 AND (external_match=1 OR (name_compatible=1 AND exact_overlap>=2))",s=>{});
        }

        // Shared stored IDs are a nomination, not proof.  They become an actionable
        // merge only when the provider crosswalks are non-contradictory, every
        // relationship is positively supported by that identity landscape, and no
        // different linked-cast identity has positive support.  Otherwise one
        // consolidated reconciliation case retains the exact media partitions.
        private static void DuplicatePeople(IDatabaseConnection x,long run)
        {
            Exec(x,@"WITH people AS (
 SELECT p.*,coalesce(p.imdb_id,(SELECT external_id FROM tmdb_external_id x WHERE x.entity_type='person' AND x.tmdb_id=p.tmdb_id AND x.source_name='imdb' LIMIT 1)) resolved_imdb,
        (SELECT count(*) FROM emby_relationship er WHERE er.person_emby_id=p.emby_id) rels
 FROM emby_item p WHERE p.item_type='person'), groups AS (
 SELECT tmdb_id,tvdb_id,resolved_imdb,count(*) people,sum(rels) rels,
        (SELECT p2.emby_id FROM people p2 WHERE p2.tmdb_id=p.tmdb_id AND p2.tvdb_id=p.tvdb_id AND p2.resolved_imdb=p.resolved_imdb ORDER BY p2.rels DESC,p2.emby_id LIMIT 1) survivor,
        (SELECT group_concat(emby_id,',') FROM (SELECT p3.emby_id FROM people p3 WHERE p3.tmdb_id=p.tmdb_id AND p3.tvdb_id=p.tvdb_id AND p3.resolved_imdb=p.resolved_imdb ORDER BY p3.emby_id)) participants
 FROM people p WHERE tmdb_id IS NOT NULL AND tvdb_id IS NOT NULL AND resolved_imdb IS NOT NULL
 GROUP BY tmdb_id,tvdb_id,resolved_imdb HAVING count(*)>1), assessed AS (
 SELECT g.*,
  (CASE WHEN EXISTS(SELECT 1 FROM tmdb_external_id x WHERE x.entity_type='person' AND x.tmdb_id=g.tmdb_id AND x.source_name='imdb' AND x.external_id<>g.resolved_imdb) THEN 1 ELSE 0 END+
   CASE WHEN EXISTS(SELECT 1 FROM tmdb_external_id x WHERE x.entity_type='person' AND x.tmdb_id=g.tmdb_id AND x.source_name='tvdb' AND x.external_id<>g.tvdb_id) THEN 1 ELSE 0 END+
   CASE WHEN EXISTS(SELECT 1 FROM remote_id x WHERE x.entity_type='person' AND x.tvdb_id=g.tvdb_id AND x.source_name='IMDB' AND x.remote_id<>g.resolved_imdb) THEN 1 ELSE 0 END+
   CASE WHEN EXISTS(SELECT 1 FROM remote_id x WHERE x.entity_type='person' AND x.tvdb_id=g.tvdb_id AND x.source_name='TheMovieDB.com' AND x.remote_id<>g.tmdb_id) THEN 1 ELSE 0 END) contradictions,
  (SELECT count(*) FROM people p JOIN emby_relationship er ON er.person_emby_id=p.emby_id JOIN emby_item m ON m.emby_id=er.media_emby_id LEFT JOIN tmdb_item_resolution tr ON tr.emby_id=m.emby_id LEFT JOIN item_resolution vr ON vr.emby_id=m.emby_id
   WHERE p.tmdb_id=g.tmdb_id AND p.tvdb_id=g.tvdb_id AND p.resolved_imdb=g.resolved_imdb AND
    NOT EXISTS(SELECT 1 FROM tmdb_credit_observation c WHERE c.production_tmdb_id=coalesce(m.tmdb_id,tr.resolved_tmdb_id) AND c.production_type=m.item_type AND c.person_tmdb_id=g.tmdb_id) AND
    NOT EXISTS(SELECT 1 FROM tvdb_credit_observation c WHERE c.production_tvdb_id=coalesce(m.tvdb_id,vr.resolved_tvdb_id) AND c.production_type=m.item_type AND c.person_tvdb_id=g.tvdb_id) AND
    NOT EXISTS(SELECT 1 FROM hk_duplicate_identity_equivalence q JOIN provider_credit_observation c ON c.provider=q.candidate_provider AND c.person_provider_id=q.candidate_provider_id AND c.production_type=m.item_type AND c.production_provider_id=CASE q.candidate_provider WHEN 'tmdb' THEN coalesce(m.tmdb_id,tr.resolved_tmdb_id) ELSE coalesce(m.tvdb_id,vr.resolved_tvdb_id) END
     WHERE q.tmdb_id=g.tmdb_id AND q.tvdb_id=g.tvdb_id AND q.resolved_imdb=g.resolved_imdb)) unsupported,
  (SELECT count(DISTINCT s.provider||':'||s.candidate_external_id) FROM housekeeping_signal s JOIN people p ON p.emby_id=s.person_emby_id
   WHERE s.run_id=@run AND p.tmdb_id=g.tmdb_id AND p.tvdb_id=g.tvdb_id AND p.resolved_imdb=g.resolved_imdb AND s.signal_type LIKE 'confirmed-%' AND s.candidate_external_id IS NOT NULL AND
    ((s.provider='tmdb' AND s.candidate_external_id<>g.tmdb_id) OR (s.provider='tvdb' AND s.candidate_external_id<>g.tvdb_id)) AND
    NOT EXISTS(SELECT 1 FROM hk_duplicate_identity_equivalence q WHERE q.tmdb_id=g.tmdb_id AND q.tvdb_id=g.tvdb_id AND q.resolved_imdb=g.resolved_imdb AND q.candidate_provider=s.provider AND q.candidate_provider_id=s.candidate_external_id)) alternatives,
  (SELECT count(*) FROM hk_duplicate_identity_equivalence q WHERE q.tmdb_id=g.tmdb_id AND q.tvdb_id=g.tvdb_id AND q.resolved_imdb=g.resolved_imdb) equivalences
 FROM groups g)
INSERT INTO housekeeping_case(run_id,case_key,anchor_emby_id,case_type,decision_state,title,summary,identity_confidence,relationship_confidence,operation_confidence,linked_relationship_count,assessed_relationship_count,affected_relationship_count,created_utc)
SELECT @run,'duplicate:'||tmdb_id||':'||tvdb_id||':'||resolved_imdb,survivor,
 CASE WHEN contradictions=0 AND unsupported=0 AND alternatives=0 THEN 'merge-duplicate-people' ELSE 'reconcile-person' END,
 CASE WHEN contradictions=0 AND unsupported=0 AND alternatives=0 THEN 'actionable' ELSE 'operator-choice' END,
 CASE WHEN contradictions=0 AND unsupported=0 AND alternatives=0 THEN 'Merge duplicate Emby people ['||participants||']' ELSE 'Reconcile shared IDs across Emby people ['||participants||']' END,
 CASE WHEN contradictions=0 AND unsupported=0 AND alternatives=0 THEN 'TMDB '||tmdb_id||', TVDB '||tvdb_id||' and IMDb '||resolved_imdb||' crosswalk without contradiction; the case-local identity cluster supports every relationship. Supporting alternate provider identities admitted by independent crosswalk or multiple exact cross-provider media='||equivalences||'. Retain Emby '||survivor||' for continuity and move only those verified relationships.'
 ELSE 'Shared stored IDs are not merge proof. Crosswalk contradictions='||contradictions||'; relationships unsupported by the complete case-local identity cluster='||unsupported||'; distinct positively supported incompatible linked-cast identities='||alternatives||'; supporting alternate provider identities admitted to the assigned cluster='||equivalences||'. Review the identity clusters and exact media partitions; no blanket merge or relationship move is proposed.' END,
 CASE WHEN contradictions=0 THEN .95 ELSE .35 END,CASE WHEN unsupported=0 AND alternatives=0 THEN .95 ELSE .55 END,CASE WHEN contradictions=0 AND unsupported=0 AND alternatives=0 THEN .95 ELSE 0 END,rels,rels,CASE WHEN contradictions=0 AND unsupported=0 AND alternatives=0 THEN rels-(SELECT rels FROM people WHERE emby_id=survivor) ELSE 0 END,@now FROM assessed",s=>{s.TryBind("@run",run);s.TryBind("@now",Now());});

            Exec(x,@"INSERT INTO housekeeping_case_cluster(case_id,cluster_key,cluster_role,cluster_state,preferred_name,continuity_emby_id,identity_confidence,summary)
SELECT c.case_id,'assigned',CASE c.case_type WHEN 'merge-duplicate-people' THEN 'retained' ELSE 'current' END,CASE c.case_type WHEN 'merge-duplicate-people' THEN 'established' ELSE 'contradictory' END,p.name,c.anchor_emby_id,c.identity_confidence,CASE c.case_type WHEN 'merge-duplicate-people' THEN 'One identity cluster validated by crosswalks and every exact relationship.' ELSE 'The shared stored identity landscape is an assertion under reconciliation, not a proven person cluster.' END FROM housekeeping_case c JOIN emby_item p ON p.emby_id=c.anchor_emby_id WHERE c.run_id=@run AND c.case_type IN('merge-duplicate-people','reconcile-person')",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_participant(case_id,emby_id,participant_role,cluster_key,current_name,current_tmdb_id,current_tvdb_id,current_imdb_id,proposed_disposition,summary)
SELECT c.case_id,p.emby_id,CASE WHEN c.case_type='merge-duplicate-people' THEN CASE WHEN p.emby_id=c.anchor_emby_id THEN 'retained' ELSE 'duplicate' END ELSE CASE WHEN p.emby_id=c.anchor_emby_id THEN 'anchor' ELSE 'context' END END,'assigned',p.name,p.tmdb_id,p.tvdb_id,coalesce(p.imdb_id,x.external_id),CASE WHEN c.case_type='merge-duplicate-people' THEN CASE WHEN p.emby_id=c.anchor_emby_id THEN 'retain' ELSE 'merge-into' END ELSE 'review' END,
 CASE WHEN c.case_type='merge-duplicate-people' AND p.emby_id=c.anchor_emby_id THEN 'Continuity survivor selected by relationship count, then stable Emby ID.' WHEN c.case_type='merge-duplicate-people' THEN 'Validated duplicate container; retire only after every verified relationship has moved.' ELSE 'Participant in a shared-ID reconciliation; its relationships must be partitioned before any disposition is chosen.' END
FROM housekeeping_case c JOIN housekeeping_case_cluster k ON k.case_id=c.case_id AND k.cluster_key='assigned' JOIN emby_item a ON a.emby_id=c.anchor_emby_id JOIN emby_item p ON p.item_type='person' AND p.tmdb_id=a.tmdb_id AND p.tvdb_id=a.tvdb_id LEFT JOIN tmdb_external_id x ON x.entity_type='person' AND x.tmdb_id=p.tmdb_id AND x.source_name='imdb'
WHERE c.run_id=@run AND c.case_type IN('merge-duplicate-people','reconcile-person') AND coalesce(p.imdb_id,x.external_id)=(SELECT coalesce(a.imdb_id,ax.external_id) FROM emby_item a LEFT JOIN tmdb_external_id ax ON ax.entity_type='person' AND ax.tmdb_id=a.tmdb_id AND ax.source_name='imdb' WHERE a.emby_id=c.anchor_emby_id)",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT OR IGNORE INTO housekeeping_case_cluster(case_id,cluster_key,cluster_role,cluster_state,preferred_name,identity_confidence,summary)
SELECT c.case_id,s.provider||':'||s.candidate_external_id,'alternative','corroborated',max(s.candidate_name),max(s.confidence),'Different provider identity with positive support on linked media.' FROM housekeeping_case c JOIN housekeeping_case_participant p ON p.case_id=c.case_id JOIN housekeeping_signal s ON s.run_id=c.run_id AND s.person_emby_id=p.emby_id AND s.signal_type LIKE 'confirmed-%' AND s.candidate_external_id IS NOT NULL JOIN emby_item a ON a.emby_id=c.anchor_emby_id WHERE c.run_id=@run AND c.case_type='reconcile-person' AND ((s.provider='tmdb' AND s.candidate_external_id<>a.tmdb_id) OR (s.provider='tvdb' AND s.candidate_external_id<>a.tvdb_id)) AND NOT EXISTS(SELECT 1 FROM hk_duplicate_identity_equivalence q WHERE c.case_key='duplicate:'||q.tmdb_id||':'||q.tvdb_id||':'||q.resolved_imdb AND q.candidate_provider=s.provider AND q.candidate_provider_id=s.candidate_external_id) GROUP BY c.case_id,s.provider,s.candidate_external_id",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_identity(case_id,cluster_key,provider,provider_person_id,canonical_name,identity_state,provenance,confidence,summary)
SELECT c.case_id,'assigned',v.provider,v.id,v.name,CASE c.case_type WHEN 'merge-duplicate-people' THEN 'confirmed' ELSE 'current' END,CASE c.case_type WHEN 'merge-duplicate-people' THEN 'external-id' ELSE 'emby-current' END,c.identity_confidence,CASE c.case_type WHEN 'merge-duplicate-people' THEN 'Identity is identical across every participant and its provider crosswalks agree.' ELSE 'Shared current identity assertion retained for reconciliation.' END FROM housekeeping_case c JOIN emby_item p ON p.emby_id=c.anchor_emby_id
JOIN (SELECT emby_id,'tmdb' provider,tmdb_id id,name FROM emby_item WHERE tmdb_id IS NOT NULL UNION ALL SELECT emby_id,'tvdb',tvdb_id,name FROM emby_item WHERE tvdb_id IS NOT NULL UNION ALL SELECT emby_id,'imdb',imdb_id,name FROM emby_item WHERE imdb_id IS NOT NULL UNION ALL SELECT p.emby_id,'imdb',x.external_id,p.name FROM emby_item p JOIN tmdb_external_id x ON x.entity_type='person' AND x.tmdb_id=p.tmdb_id AND x.source_name='imdb' WHERE p.imdb_id IS NULL) v ON v.emby_id=p.emby_id
WHERE c.run_id=@run AND c.case_type IN('merge-duplicate-people','reconcile-person')",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT OR IGNORE INTO housekeeping_case_identity(case_id,cluster_key,provider,provider_person_id,canonical_name,identity_state,provenance,confidence,summary)
SELECT c.case_id,'assigned',q.candidate_provider,q.candidate_provider_id,q.candidate_name,'corroborating','provider-native-media',q.identity_confidence,q.summary
FROM housekeeping_case c JOIN hk_duplicate_identity_equivalence q ON c.case_key='duplicate:'||q.tmdb_id||':'||q.tvdb_id||':'||q.resolved_imdb
WHERE c.run_id=@run AND c.case_type IN('merge-duplicate-people','reconcile-person')",s=>s.TryBind("@run",run));
            Exec(x,@"UPDATE housekeeping_case_cluster SET summary=summary||coalesce((SELECT ' Supporting provider-side identities: '||group_concat(upper(i.provider)||' '||i.provider_person_id||' ('||coalesce(i.canonical_name,'name unavailable')||')',', ')||'.' FROM housekeeping_case_identity i WHERE i.case_id=housekeeping_case_cluster.case_id AND i.cluster_key=housekeeping_case_cluster.cluster_key AND i.identity_state='corroborating' AND i.provenance='provider-native-media'),'') WHERE case_id IN(SELECT case_id FROM housekeeping_case WHERE run_id=@run AND case_type IN('merge-duplicate-people','reconcile-person')) AND cluster_key='assigned'",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT OR IGNORE INTO housekeeping_case_identity(case_id,cluster_key,provider,provider_person_id,canonical_name,identity_state,provenance,confidence,summary)
SELECT c.case_id,s.provider||':'||s.candidate_external_id,s.provider,s.candidate_external_id,max(s.candidate_name),'proposed','provider-native-media',max(s.confidence),'Alternative identity is directly supported on linked media.' FROM housekeeping_case c JOIN housekeeping_case_participant p ON p.case_id=c.case_id JOIN housekeeping_signal s ON s.run_id=c.run_id AND s.person_emby_id=p.emby_id AND s.signal_type LIKE 'confirmed-%' AND s.candidate_external_id IS NOT NULL JOIN emby_item a ON a.emby_id=c.anchor_emby_id WHERE c.run_id=@run AND c.case_type='reconcile-person' AND ((s.provider='tmdb' AND s.candidate_external_id<>a.tmdb_id) OR (s.provider='tvdb' AND s.candidate_external_id<>a.tvdb_id)) AND NOT EXISTS(SELECT 1 FROM hk_duplicate_identity_equivalence q WHERE c.case_key='duplicate:'||q.tmdb_id||':'||q.tvdb_id||':'||q.resolved_imdb AND q.candidate_provider=s.provider AND q.candidate_provider_id=s.candidate_external_id) GROUP BY c.case_id,s.provider,s.candidate_external_id",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_relationship(case_id,cluster_key,media_emby_id,media_type,relationship_type,relationship_role,current_person_emby_id,proposed_person_emby_id,disposition,relationship_confidence,summary)
SELECT c.case_id,'assigned',er.media_emby_id,m.item_type,coalesce(er.person_type,'Person'),er.role,er.person_emby_id,CASE c.case_type WHEN 'merge-duplicate-people' THEN c.anchor_emby_id END,CASE WHEN c.case_type='reconcile-person' THEN 'review' WHEN er.person_emby_id=c.anchor_emby_id THEN 'retain' ELSE 'move' END,CASE c.case_type WHEN 'merge-duplicate-people' THEN .99 ELSE .5 END,
 CASE WHEN c.case_type='reconcile-person' THEN 'Review this exact relationship against every supported identity cluster; shared stored IDs do not decide its owner.' WHEN er.person_emby_id=c.anchor_emby_id THEN 'Retain verified relationship on continuity survivor.' ELSE 'Move verified relationship from duplicate Emby '||er.person_emby_id||' to survivor Emby '||c.anchor_emby_id||'.' END
FROM housekeeping_case c JOIN housekeeping_case_participant p ON p.case_id=c.case_id JOIN emby_relationship er ON er.person_emby_id=p.emby_id JOIN emby_item m ON m.emby_id=er.media_emby_id WHERE c.run_id=@run AND c.case_type IN('merge-duplicate-people','reconcile-person')",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT OR IGNORE INTO housekeeping_case_relationship(case_id,cluster_key,media_emby_id,media_type,relationship_type,relationship_role,current_person_emby_id,disposition,relationship_confidence,summary)
SELECT c.case_id,s.provider||':'||s.candidate_external_id,s.media_emby_id,m.item_type,coalesce(er.person_type,'Person'),er.role,s.person_emby_id,'review',s.confidence,upper(s.provider)||' identity '||s.candidate_external_id||' has '||s.signal_type||' support for this relationship; choose the justified identity owner.' FROM housekeeping_case c JOIN housekeeping_case_participant p ON p.case_id=c.case_id JOIN housekeeping_signal s ON s.run_id=c.run_id AND s.person_emby_id=p.emby_id AND s.signal_type LIKE 'confirmed-%' AND s.media_emby_id IS NOT NULL JOIN emby_item a ON a.emby_id=c.anchor_emby_id JOIN emby_relationship er ON er.person_emby_id=s.person_emby_id AND er.media_emby_id=s.media_emby_id JOIN emby_item m ON m.emby_id=s.media_emby_id WHERE c.run_id=@run AND c.case_type='reconcile-person' AND ((s.provider='tmdb' AND s.candidate_external_id<>a.tmdb_id) OR (s.provider='tvdb' AND s.candidate_external_id<>a.tvdb_id)) AND NOT EXISTS(SELECT 1 FROM hk_duplicate_identity_equivalence q WHERE c.case_key='duplicate:'||q.tmdb_id||':'||q.tvdb_id||':'||q.resolved_imdb AND q.candidate_provider=s.provider AND q.candidate_provider_id=s.candidate_external_id)",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_action(case_id,action_key,action_order,action_type,cluster_key,source_emby_id,target_emby_id,media_emby_id,relationship_type,relationship_role,precondition,operation_confidence,summary)
SELECT r.case_id,'move:'||r.case_relationship_id,100+r.case_relationship_id,'move-relationship','assigned',r.current_person_emby_id,r.proposed_person_emby_id,r.media_emby_id,r.relationship_type,r.relationship_role,'Identity cluster remains corroborated, this exact relationship remains positively supported, and destination relationship does not already exist',.95,r.summary FROM housekeeping_case_relationship r JOIN housekeeping_case c ON c.case_id=r.case_id WHERE c.run_id=@run AND c.case_type='merge-duplicate-people' AND r.disposition='move'",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_action(case_id,action_key,action_order,action_type,cluster_key,source_emby_id,target_emby_id,precondition,operation_confidence,summary)
SELECT c.case_id,'merge:'||p.emby_id,100000+p.emby_id,'merge-person','assigned',p.emby_id,c.anchor_emby_id,'All verified exact relationships have moved and source is empty',.95,'Merge validated duplicate Emby '||p.emby_id||' into continuity survivor Emby '||c.anchor_emby_id||'.' FROM housekeeping_case c JOIN housekeeping_case_participant p ON p.case_id=c.case_id AND p.participant_role='duplicate' WHERE c.run_id=@run AND c.case_type='merge-duplicate-people'",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_action(case_id,action_key,action_order,action_type,cluster_key,source_emby_id,operator_choice_required,identity_confidence,relationship_confidence,operation_confidence,summary)
SELECT c.case_id,'review-reconciliation',10,'review-identity','assigned',c.anchor_emby_id,1,c.identity_confidence,c.relationship_confidence,0,'Review all participating Emby IDs, provider crosswalk contradictions and exact relationship partitions. No mutation is proposed until every relationship has a justified identity owner.' FROM housekeeping_case c WHERE c.run_id=@run AND c.case_type='reconcile-person'",s=>s.TryBind("@run",run));
        }

        private static void ProviderIdentityRepairs(IDatabaseConnection x,long run)
        {
            Exec(x,@"WITH r0 AS (SELECT r.*,p.tmdb_id current_tmdb,p.tvdb_id current_tvdb,p.imdb_id current_imdb,CASE r.provider WHEN 'tmdb' THEN (SELECT emby_id FROM emby_item q WHERE q.item_type='person' AND q.tmdb_id=r.proposed_value AND q.emby_id<>r.person_emby_id LIMIT 1) ELSE (SELECT emby_id FROM emby_item q WHERE q.item_type='person' AND q.tvdb_id=r.proposed_value AND q.emby_id<>r.person_emby_id LIMIT 1) END owner FROM housekeeping_recommendation r JOIN emby_item p ON p.emby_id=r.person_emby_id WHERE r.run_id=@run AND r.recommendation_type='replace-provider-id'), disjoint AS (
SELECT DISTINCT r0.recommendation_id
FROM r0
JOIN emby_relationship er ON er.person_emby_id=r0.person_emby_id
JOIN emby_item m ON m.emby_id=er.media_emby_id
LEFT JOIN tmdb_item_resolution tr ON tr.emby_id=m.emby_id
LEFT JOIN item_resolution vr ON vr.emby_id=m.emby_id
WHERE
 (r0.provider='tmdb' AND
  EXISTS(SELECT 1 FROM tmdb_credit_observation current_credit WHERE current_credit.production_tmdb_id=coalesce(m.tmdb_id,tr.resolved_tmdb_id) AND current_credit.production_type=m.item_type AND current_credit.person_tmdb_id=r0.current_value) AND
  NOT EXISTS(SELECT 1 FROM tmdb_credit_observation proposed_credit WHERE proposed_credit.production_tmdb_id=coalesce(m.tmdb_id,tr.resolved_tmdb_id) AND proposed_credit.production_type=m.item_type AND proposed_credit.person_tmdb_id=r0.proposed_value))
 OR
 (r0.provider='tvdb' AND
  EXISTS(SELECT 1 FROM tvdb_credit_observation current_credit WHERE current_credit.production_tvdb_id=coalesce(m.tvdb_id,vr.resolved_tvdb_id) AND current_credit.production_type=m.item_type AND current_credit.person_tvdb_id=r0.current_value) AND
  NOT EXISTS(SELECT 1 FROM tvdb_credit_observation proposed_credit WHERE proposed_credit.production_tvdb_id=coalesce(m.tvdb_id,vr.resolved_tvdb_id) AND proposed_credit.production_type=m.item_type AND proposed_credit.person_tvdb_id=r0.proposed_value))
), r AS (
SELECT r0.*,
 CASE WHEN r0.provider='tmdb' THEN
  CASE WHEN EXISTS(SELECT 1 FROM tmdb_external_id x WHERE x.entity_type='person' AND x.tmdb_id=r0.proposed_value AND x.source_name='imdb' AND r0.current_imdb IS NOT NULL AND x.external_id<>r0.current_imdb) OR EXISTS(SELECT 1 FROM tmdb_external_id x WHERE x.entity_type='person' AND x.tmdb_id=r0.proposed_value AND x.source_name='tvdb' AND r0.current_tvdb IS NOT NULL AND x.external_id<>r0.current_tvdb) THEN 1 ELSE 0 END
 ELSE CASE WHEN EXISTS(SELECT 1 FROM remote_id x WHERE x.entity_type='person' AND x.tvdb_id=r0.proposed_value AND x.source_name='IMDB' AND r0.current_imdb IS NOT NULL AND x.remote_id<>r0.current_imdb) OR EXISTS(SELECT 1 FROM remote_id x WHERE x.entity_type='person' AND x.tvdb_id=r0.proposed_value AND x.source_name='TheMovieDB.com' AND r0.current_tmdb IS NOT NULL AND x.remote_id<>r0.current_tmdb) THEN 1 ELSE 0 END END crosswalk_conflict,
 CASE WHEN disjoint.recommendation_id IS NULL THEN 0 ELSE 1 END disjoint_current_support
FROM r0 LEFT JOIN disjoint ON disjoint.recommendation_id=r0.recommendation_id)
INSERT INTO housekeeping_case(run_id,case_key,anchor_emby_id,case_type,decision_state,title,summary,identity_confidence,relationship_confidence,operation_confidence,linked_relationship_count,assessed_relationship_count,affected_relationship_count,created_utc)
SELECT @run,'identity:'||provider||':'||person_emby_id||':'||proposed_value,person_emby_id,CASE WHEN crosswalk_conflict=1 OR disjoint_current_support=1 THEN 'reconcile-person' WHEN owner IS NULL THEN 'repair-provider-identity' ELSE 'reassign-relationships' END,CASE WHEN crosswalk_conflict=1 OR disjoint_current_support=1 OR owner IS NOT NULL THEN 'operator-choice' ELSE 'actionable' END,
 CASE WHEN crosswalk_conflict=1 OR disjoint_current_support=1 THEN 'Reconcile competing identities on Emby '||person_emby_id WHEN owner IS NULL THEN 'Repair '||upper(provider)||' identity for Emby '||person_emby_id ELSE 'Reassign supported relationships from Emby '||person_emby_id||' to existing owner '||owner END,
 evidence_summary||CASE WHEN crosswalk_conflict=1 THEN ' The proposed identity contradicts a current independent provider/external-ID assertion; changing the ID in place is withheld.' WHEN disjoint_current_support=1 THEN ' The current and proposed identities have positive support on different linked relationships; changing the ID in place is withheld pending an exact relationship partition.' WHEN owner IS NULL THEN ' The supported identity has no other Emby owner and no contradictory identity or disjoint current support, so repair this container in place.' ELSE ' The supported identity is already owned by Emby '||owner||'; do not merge people merely because both are credited on the same media.' END,
 CASE WHEN crosswalk_conflict=1 THEN min(.55,coalesce(identity_confidence,confidence)) ELSE coalesce(identity_confidence,confidence) END,coalesce(relationship_confidence,confidence),CASE WHEN crosswalk_conflict=1 OR disjoint_current_support=1 THEN 0 WHEN owner IS NULL THEN coalesce(operation_confidence,confidence) ELSE .65 END,linked_media_count,checked_media_count,CASE WHEN crosswalk_conflict=1 OR disjoint_current_support=1 THEN 0 ELSE supported_media_count END,@now FROM r
WHERE NOT EXISTS(SELECT 1 FROM housekeeping_case c JOIN housekeeping_case_participant cp ON cp.case_id=c.case_id WHERE c.run_id=@run AND c.case_type='reconcile-person' AND cp.emby_id=r.person_emby_id)",s=>{s.TryBind("@run",run);s.TryBind("@now",Now());});
            BaseSingleParticipant(x,run,"repair-provider-identity","anchor","retain");
            BaseSingleParticipant(x,run,"reassign-relationships","source","move-relationships");
            Exec(x,@"INSERT INTO housekeeping_case_participant(case_id,emby_id,participant_role,cluster_key,current_name,current_tmdb_id,current_tvdb_id,current_imdb_id,proposed_disposition,summary)
SELECT c.case_id,p.emby_id,'anchor','current',p.name,p.tmdb_id,p.tvdb_id,p.imdb_id,'review','Current Emby person contains competing identity/relationship evidence; no in-place identity change is proposed.' FROM housekeeping_case c JOIN emby_item p ON p.emby_id=c.anchor_emby_id WHERE c.run_id=@run AND c.case_type='reconcile-person' AND c.case_key LIKE 'identity:%'",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_cluster(case_id,cluster_key,cluster_role,cluster_state,preferred_name,continuity_emby_id,identity_confidence,summary)
SELECT c.case_id,'current','current','established',p.name,c.anchor_emby_id,c.identity_confidence,'Current provider identity retains positive relationship support or remains independently asserted.' FROM housekeeping_case c JOIN emby_item p ON p.emby_id=c.anchor_emby_id WHERE c.run_id=@run AND c.case_type='reconcile-person' AND c.case_key LIKE 'identity:%'",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_cluster(case_id,cluster_key,cluster_role,cluster_state,preferred_name,continuity_emby_id,identity_confidence,summary)
SELECT c.case_id,'proposed','destination','corroborated',coalesce(r.proposed_value,p.name),CASE c.case_type WHEN 'reassign-relationships' THEN (SELECT p2.emby_id FROM emby_item p2 WHERE p2.item_type='person' AND p2.emby_id<>c.anchor_emby_id AND ((r.provider='tmdb' AND p2.tmdb_id=r.proposed_value) OR (r.provider='tvdb' AND p2.tvdb_id=r.proposed_value)) LIMIT 1) ELSE c.anchor_emby_id END,c.identity_confidence,'Provider identity nominated through linked-media/cross-provider evidence.'
FROM housekeeping_case c JOIN housekeeping_recommendation r ON r.recommendation_type='replace-provider-id' AND c.case_key='identity:'||r.provider||':'||r.person_emby_id||':'||r.proposed_value JOIN emby_item p ON p.emby_id=c.anchor_emby_id WHERE c.run_id=@run AND r.run_id=@run AND c.case_type IN('repair-provider-identity','reassign-relationships','reconcile-person')",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_participant(case_id,emby_id,participant_role,cluster_key,current_name,current_tmdb_id,current_tvdb_id,current_imdb_id,proposed_disposition,summary)
SELECT c.case_id,k.continuity_emby_id,'existing-owner','proposed',p.name,p.tmdb_id,p.tvdb_id,p.imdb_id,'receive-relationships','Existing Emby owner of the supported destination identity.' FROM housekeeping_case c JOIN housekeeping_case_cluster k ON k.case_id=c.case_id AND k.cluster_key='proposed' JOIN emby_item p ON p.emby_id=k.continuity_emby_id WHERE c.run_id=@run AND c.case_type='reassign-relationships'",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_identity(case_id,cluster_key,provider,provider_person_id,canonical_name,identity_state,provenance,confidence,summary)
SELECT c.case_id,'proposed',r.provider,r.proposed_value,CASE r.provider WHEN 'tmdb' THEN (SELECT name FROM tmdb_entity e WHERE e.entity_type='person' AND e.tmdb_id=r.proposed_value) ELSE (SELECT name FROM tvdb_entity e WHERE e.entity_type='person' AND e.tvdb_id=r.proposed_value) END,'proposed','provider-native-media',c.identity_confidence,r.evidence_summary FROM housekeeping_case c JOIN housekeeping_recommendation r ON r.recommendation_type='replace-provider-id' AND c.case_key='identity:'||r.provider||':'||r.person_emby_id||':'||r.proposed_value WHERE c.run_id=@run AND r.run_id=@run",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT OR IGNORE INTO housekeeping_case_identity(case_id,cluster_key,provider,provider_person_id,identity_state,provenance,confidence,summary)
SELECT c.case_id,'proposed',CASE x.source_name WHEN 'imdb' THEN 'imdb' WHEN 'tvdb' THEN 'tvdb' WHEN 'wikidata' THEN 'wikidata' END,x.external_id,'corroborating','provider-crosswalk',.99,'TMDB external identity crosswalk.' FROM housekeeping_case c JOIN housekeeping_recommendation r ON r.recommendation_type='replace-provider-id' AND c.case_key='identity:'||r.provider||':'||r.person_emby_id||':'||r.proposed_value JOIN tmdb_external_id x ON r.provider='tmdb' AND x.entity_type='person' AND x.tmdb_id=r.proposed_value AND x.source_name IN('imdb','tvdb','wikidata') WHERE c.run_id=@run AND r.run_id=@run
UNION ALL SELECT c.case_id,'proposed',CASE x.source_name WHEN 'IMDB' THEN 'imdb' WHEN 'TheMovieDB.com' THEN 'tmdb' ELSE 'wikidata' END,x.remote_id,'corroborating','provider-crosswalk',.99,'TVDB remote identity crosswalk.' FROM housekeeping_case c JOIN housekeeping_recommendation r ON r.recommendation_type='replace-provider-id' AND c.case_key='identity:'||r.provider||':'||r.person_emby_id||':'||r.proposed_value JOIN remote_id x ON r.provider='tvdb' AND x.entity_type='person' AND x.tvdb_id=r.proposed_value AND (x.source_name IN('IMDB','TheMovieDB.com') OR lower(x.source_name)='wikidata') WHERE c.run_id=@run AND r.run_id=@run",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_action(case_id,action_key,action_order,action_type,cluster_key,source_emby_id,target_emby_id,provider,current_value,proposed_value,precondition,identity_confidence,operation_confidence,summary)
SELECT c.case_id,'set:'||r.provider,10,'set-provider-id','proposed',c.anchor_emby_id,c.anchor_emby_id,r.provider,r.current_value,r.proposed_value,'Candidate remains unowned and identity evidence remains non-contradictory',c.identity_confidence,c.operation_confidence,'Set '||upper(r.provider)||' identity from '||coalesce(r.current_value,'(none)')||' to '||r.proposed_value||' on Emby '||c.anchor_emby_id||'.' FROM housekeeping_case c JOIN housekeeping_recommendation r ON r.recommendation_type='replace-provider-id' AND c.case_key='identity:'||r.provider||':'||r.person_emby_id||':'||r.proposed_value WHERE c.run_id=@run AND r.run_id=@run AND c.case_type='repair-provider-identity'",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT OR IGNORE INTO housekeeping_case_relationship(case_id,cluster_key,media_emby_id,media_type,relationship_type,relationship_role,current_person_emby_id,proposed_person_emby_id,disposition,relationship_confidence,summary)
SELECT c.case_id,'proposed',s.media_emby_id,m.item_type,coalesce(er.person_type,'Person'),er.role,c.anchor_emby_id,k.continuity_emby_id,'move',s.confidence,upper(s.provider)||' '||s.candidate_external_id||' positively supports this exact relationship while the current identity does not; move it to existing owner Emby '||k.continuity_emby_id||'.' FROM housekeeping_case c JOIN housekeeping_case_cluster k ON k.case_id=c.case_id AND k.cluster_key='proposed' JOIN housekeeping_recommendation q ON q.run_id=c.run_id AND c.case_key='identity:'||q.provider||':'||q.person_emby_id||':'||q.proposed_value JOIN housekeeping_signal s ON s.run_id=c.run_id AND s.person_emby_id=c.anchor_emby_id AND s.provider=q.provider AND s.candidate_external_id=q.proposed_value AND s.signal_type LIKE 'confirmed-%' AND s.media_emby_id IS NOT NULL JOIN emby_relationship er ON er.person_emby_id=c.anchor_emby_id AND er.media_emby_id=s.media_emby_id JOIN emby_item m ON m.emby_id=s.media_emby_id WHERE c.run_id=@run AND c.case_type='reassign-relationships' AND NOT EXISTS(SELECT 1 FROM housekeeping_signal cur WHERE cur.run_id=s.run_id AND cur.person_emby_id=s.person_emby_id AND cur.provider=s.provider AND cur.media_emby_id=s.media_emby_id AND cur.candidate_external_id=q.current_value AND cur.signal_type LIKE 'confirmed-%')",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_action(case_id,action_key,action_order,action_type,cluster_key,source_emby_id,target_emby_id,media_emby_id,relationship_type,relationship_role,operator_choice_required,identity_confidence,relationship_confidence,operation_confidence,summary)
SELECT c.case_id,'move:'||r.case_relationship_id,100+r.case_relationship_id,'move-relationship','proposed',r.current_person_emby_id,r.proposed_person_emby_id,r.media_emby_id,r.relationship_type,r.relationship_role,1,c.identity_confidence,r.relationship_confidence,c.operation_confidence,r.summary FROM housekeeping_case c JOIN housekeeping_case_relationship r ON r.case_id=c.case_id AND r.cluster_key='proposed' WHERE c.run_id=@run AND c.case_type='reassign-relationships'",s=>s.TryBind("@run",run));
            Exec(x,@"UPDATE housekeeping_case SET affected_relationship_count=(SELECT count(*) FROM housekeeping_case_relationship r WHERE r.case_id=housekeeping_case.case_id),decision_state=CASE WHEN EXISTS(SELECT 1 FROM housekeeping_case_relationship r WHERE r.case_id=housekeeping_case.case_id) THEN 'operator-choice' ELSE 'unresolved' END,operation_confidence=CASE WHEN EXISTS(SELECT 1 FROM housekeeping_case_relationship r WHERE r.case_id=housekeeping_case.case_id) THEN operation_confidence ELSE 0 END,summary=summary||CASE WHEN EXISTS(SELECT 1 FROM housekeeping_case_relationship r WHERE r.case_id=housekeeping_case.case_id) THEN ' Exact target-positive/current-negative relationship moves are listed below.' ELSE ' No exact target-positive/current-negative relationship was materialized, so no operator action is proposed.' END WHERE run_id=@run AND case_type='reassign-relationships'",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_action(case_id,action_key,action_order,action_type,cluster_key,source_emby_id,operator_choice_required,identity_confidence,relationship_confidence,operation_confidence,summary)
SELECT c.case_id,'review-reconciliation',10,'review-identity','current',c.anchor_emby_id,1,c.identity_confidence,c.relationship_confidence,0,'Review the contradictory provider crosswalk and exact current/proposed media partitions. No provider ID or relationship mutation is proposed.' FROM housekeeping_case c WHERE c.run_id=@run AND c.case_type='reconcile-person' AND c.case_key LIKE 'identity:%'",s=>s.TryBind("@run",run));
        }

        private static void ProviderIdentityRemovals(IDatabaseConnection x,long run)
        {
            Exec(x,@"INSERT INTO housekeeping_case(run_id,case_key,anchor_emby_id,case_type,decision_state,title,summary,identity_confidence,relationship_confidence,operation_confidence,linked_relationship_count,assessed_relationship_count,affected_relationship_count,created_utc)
SELECT @run,'remove:'||provider||':'||person_emby_id||':'||current_value,person_emby_id,'remove-provider-identity','actionable','Remove unavailable '||upper(provider)||' identity from Emby '||person_emby_id,evidence_summary,coalesce(identity_confidence,confidence),coalesce(relationship_confidence,.5),coalesce(operation_confidence,confidence),linked_media_count,checked_media_count,0,@now FROM housekeeping_recommendation WHERE run_id=@run AND recommendation_type='remove-provider-id'",s=>{s.TryBind("@run",run);s.TryBind("@now",Now());});
            BaseSingleParticipant(x,run,"remove-provider-identity","anchor","retain");
            Exec(x,@"INSERT INTO housekeeping_case_cluster(case_id,cluster_key,cluster_role,cluster_state,continuity_emby_id,identity_confidence,summary) SELECT case_id,'current','current','unresolved',anchor_emby_id,identity_confidence,'The Emby container and relationships remain; only the unavailable provider assertion is proposed for removal.' FROM housekeeping_case WHERE run_id=@run AND case_type='remove-provider-identity'",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_action(case_id,action_key,action_order,action_type,cluster_key,source_emby_id,target_emby_id,provider,current_value,precondition,identity_confidence,operation_confidence,summary)
SELECT c.case_id,'remove:'||r.provider,10,'remove-provider-id','current',c.anchor_emby_id,c.anchor_emby_id,r.provider,r.current_value,'Provider response remains explicitly unavailable and candidate acquisition is complete',c.identity_confidence,c.operation_confidence,'Remove '||upper(r.provider)||' identity '||r.current_value||' from Emby '||c.anchor_emby_id||'; retain the person and all relationships.' FROM housekeeping_case c JOIN housekeeping_recommendation r ON r.recommendation_type='remove-provider-id' AND c.case_key='remove:'||r.provider||':'||r.person_emby_id||':'||r.current_value WHERE c.run_id=@run AND r.run_id=@run",s=>s.TryBind("@run",run));
        }

        private static void CrossProviderSplits(IDatabaseConnection x,long run)
        {
            Exec(x,@"INSERT INTO housekeeping_case(run_id,case_key,anchor_emby_id,case_type,decision_state,title,summary,identity_confidence,relationship_confidence,operation_confidence,linked_relationship_count,assessed_relationship_count,affected_relationship_count,created_utc)
SELECT @run,'split:cross:'||person_emby_id,person_emby_id,'split-conflated-person','operator-choice','Split conflated identities on Emby '||person_emby_id,evidence_summary,coalesce(identity_confidence,confidence),coalesce(relationship_confidence,confidence),coalesce(operation_confidence,confidence),linked_media_count,checked_media_count,supported_media_count,@now FROM housekeeping_recommendation WHERE run_id=@run AND recommendation_type='review-split' AND primary_signal_type='cross-provider-media-partition'",s=>{s.TryBind("@run",run);s.TryBind("@now",Now());});
            BaseSingleParticipant(x,run,"split-conflated-person","source","move-relationships");
            Exec(x,@"INSERT INTO housekeeping_case_cluster(case_id,cluster_key,cluster_role,cluster_state,preferred_name,continuity_emby_id,identity_confidence,summary)
SELECT c.case_id,'retain','retained','established',p.name,c.anchor_emby_id,c.identity_confidence,'TMDB-led cluster retained on the current Emby container.' FROM housekeeping_case c JOIN emby_item p ON p.emby_id=c.anchor_emby_id WHERE c.run_id=@run AND c.case_type='split-conflated-person'
UNION ALL SELECT c.case_id,'split','alternative','corroborated',p.name,(SELECT e.emby_id FROM emby_item e WHERE e.item_type='person' AND e.emby_id<>c.anchor_emby_id AND (e.tmdb_id=json_extract(r.proposed_value,'$.split_tmdb') OR e.tvdb_id=json_extract(r.proposed_value,'$.split_tvdb') OR e.imdb_id=json_extract(r.proposed_value,'$.split_imdb')) ORDER BY CASE WHEN e.imdb_id=json_extract(r.proposed_value,'$.split_imdb') THEN 0 ELSE 1 END LIMIT 1),c.identity_confidence,'TVDB-led independently cross-walked cluster. Reuse an existing Emby owner when present.' FROM housekeeping_case c JOIN housekeeping_recommendation r ON r.run_id=c.run_id AND r.person_emby_id=c.anchor_emby_id AND r.primary_signal_type='cross-provider-media-partition' JOIN emby_item p ON p.emby_id=c.anchor_emby_id WHERE c.run_id=@run AND c.case_type='split-conflated-person'",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_identity(case_id,cluster_key,provider,provider_person_id,identity_state,provenance,confidence,summary)
SELECT c.case_id,v.cluster_key,v.provider,v.id,'proposed','external-id',c.identity_confidence,'Identity explicitly materialized from the cross-provider partition.' FROM housekeeping_case c JOIN housekeeping_recommendation r ON r.run_id=c.run_id AND r.person_emby_id=c.anchor_emby_id AND r.primary_signal_type='cross-provider-media-partition' JOIN (
 SELECT recommendation_id,'retain' cluster_key,'tmdb' provider,json_extract(proposed_value,'$.retain_tmdb') id FROM housekeeping_recommendation UNION ALL SELECT recommendation_id,'retain','tvdb',json_extract(proposed_value,'$.retain_tvdb') FROM housekeeping_recommendation UNION ALL SELECT recommendation_id,'retain','imdb',json_extract(proposed_value,'$.retain_imdb') FROM housekeeping_recommendation UNION ALL SELECT recommendation_id,'split','tmdb',json_extract(proposed_value,'$.split_tmdb') FROM housekeeping_recommendation UNION ALL SELECT recommendation_id,'split','tvdb',json_extract(proposed_value,'$.split_tvdb') FROM housekeeping_recommendation UNION ALL SELECT recommendation_id,'split','imdb',json_extract(proposed_value,'$.split_imdb') FROM housekeeping_recommendation) v ON v.recommendation_id=r.recommendation_id AND v.id IS NOT NULL AND v.id<>'' WHERE c.run_id=@run AND c.case_type='split-conflated-person'",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_relationship(case_id,cluster_key,media_emby_id,media_type,relationship_type,relationship_role,current_person_emby_id,proposed_person_emby_id,disposition,relationship_confidence,summary)
SELECT c.case_id,'split',er.media_emby_id,m.item_type,coalesce(er.person_type,'Person'),er.role,c.anchor_emby_id,k.continuity_emby_id,CASE WHEN k.continuity_emby_id IS NULL THEN 'review' ELSE 'move' END,c.relationship_confidence,
 CASE WHEN k.continuity_emby_id IS NULL THEN 'TVDB supports the split cluster on this exact relationship; select or create its owner before moving.' ELSE 'Move this exact TVDB-supported relationship to existing split-cluster owner Emby '||k.continuity_emby_id||'.' END
FROM housekeeping_case c JOIN housekeeping_case_cluster k ON k.case_id=c.case_id AND k.cluster_key='split' JOIN emby_relationship er ON er.person_emby_id=c.anchor_emby_id JOIN emby_item m ON m.emby_id=er.media_emby_id JOIN emby_person_media_provider_support tv ON tv.provider='tvdb' AND tv.person_emby_id=c.anchor_emby_id AND tv.media_emby_id=er.media_emby_id AND tv.evidence_state IN('supported-exact','supported-broader') LEFT JOIN emby_person_media_provider_support tm ON tm.provider='tmdb' AND tm.person_emby_id=c.anchor_emby_id AND tm.media_emby_id=er.media_emby_id AND tm.evidence_state IN('supported-exact','supported-broader') WHERE c.run_id=@run AND c.case_type='split-conflated-person' AND tm.person_emby_id IS NULL",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_action(case_id,action_key,action_order,action_type,cluster_key,source_emby_id,target_emby_id,operator_choice_required,identity_confidence,operation_confidence,summary)
SELECT c.case_id,CASE WHEN k.continuity_emby_id IS NULL THEN 'create-split-person' ELSE 'reuse-split-owner' END,10,CASE WHEN k.continuity_emby_id IS NULL THEN 'create-person' ELSE 'retain-person' END,'split',c.anchor_emby_id,k.continuity_emby_id,1,c.identity_confidence,c.operation_confidence,CASE WHEN k.continuity_emby_id IS NULL THEN 'Create one person only after the operator confirms the split cluster has no existing owner.' ELSE 'Reuse existing Emby '||k.continuity_emby_id||' as the owner of the split cluster.' END FROM housekeeping_case c JOIN housekeeping_case_cluster k ON k.case_id=c.case_id AND k.cluster_key='split' WHERE c.run_id=@run AND c.case_type='split-conflated-person'",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_action(case_id,action_key,action_order,action_type,cluster_key,source_emby_id,target_emby_id,media_emby_id,relationship_type,relationship_role,operator_choice_required,identity_confidence,relationship_confidence,operation_confidence,summary)
SELECT c.case_id,'split-rel:'||cr.case_relationship_id,100+cr.case_relationship_id,CASE cr.disposition WHEN 'move' THEN 'move-relationship' ELSE 'review-relationship' END,'split',cr.current_person_emby_id,cr.proposed_person_emby_id,cr.media_emby_id,cr.relationship_type,cr.relationship_role,CASE cr.disposition WHEN 'move' THEN 0 ELSE 1 END,c.identity_confidence,cr.relationship_confidence,c.operation_confidence,cr.summary FROM housekeeping_case c JOIN housekeeping_case_relationship cr ON cr.case_id=c.case_id AND cr.cluster_key='split' WHERE c.run_id=@run AND c.case_type='split-conflated-person'",s=>s.TryBind("@run",run));
        }

        private static void UnresolvedPartitions(IDatabaseConnection x,long run){Exec(x,@"INSERT INTO housekeeping_case(run_id,case_key,anchor_emby_id,case_type,decision_state,title,summary,identity_confidence,relationship_confidence,operation_confidence,linked_relationship_count,assessed_relationship_count,affected_relationship_count,created_utc)
SELECT @run,'unresolved:'||recommendation_id,person_emby_id,'review-unresolved','unresolved','Unresolved identity/relationship partition on Emby '||person_emby_id,evidence_summary,coalesce(identity_confidence,confidence),coalesce(relationship_confidence,confidence),0,linked_media_count,checked_media_count,0,@now FROM housekeeping_recommendation WHERE run_id=@run AND ((recommendation_type='review-split' AND primary_signal_type<>'cross-provider-media-partition') OR recommendation_type='review-unresolved-provider-id')",s=>{s.TryBind("@run",run);s.TryBind("@now",Now());});
            BaseSingleParticipant(x,run,"review-unresolved","anchor","review");}

        private static void Names(IDatabaseConnection x,long run)
        {
            Exec(x,@"INSERT INTO housekeeping_case(run_id,case_key,anchor_emby_id,case_type,decision_state,title,summary,identity_confidence,relationship_confidence,operation_confidence,linked_relationship_count,assessed_relationship_count,affected_relationship_count,created_utc)
SELECT @run,'name:'||person_emby_id,person_emby_id,'repair-name',CASE recommendation_type WHEN 'rename-person' THEN 'actionable' ELSE 'operator-choice' END,'Review name for Emby '||person_emby_id,evidence_summary,coalesce(identity_confidence,confidence),coalesce(relationship_confidence,confidence),coalesce(operation_confidence,confidence),linked_media_count,checked_media_count,0,@now FROM housekeeping_recommendation r WHERE run_id=@run AND recommendation_type IN('rename-person','review-existing-emby-person') AND NOT EXISTS(SELECT 1 FROM housekeeping_case c LEFT JOIN housekeeping_case_participant cp ON cp.case_id=c.case_id WHERE c.run_id=@run AND c.case_type IN('repair-provider-identity','reassign-relationships','split-conflated-person','merge-duplicate-people','reconcile-person') AND (c.anchor_emby_id=r.person_emby_id OR cp.emby_id=r.person_emby_id)) GROUP BY person_emby_id HAVING count(DISTINCT proposed_value)=1",s=>{s.TryBind("@run",run);s.TryBind("@now",Now());});
            BaseSingleParticipant(x,run,"repair-name","anchor","retain");
            Exec(x,@"INSERT INTO housekeeping_case_cluster(case_id,cluster_key,cluster_role,cluster_state,preferred_name,continuity_emby_id,identity_confidence,summary) SELECT c.case_id,'person','current','corroborated',r.proposed_value,c.anchor_emby_id,c.identity_confidence,'Name arbitration follows established identity structure; it does not create a new identity.' FROM housekeeping_case c JOIN housekeeping_recommendation r ON r.run_id=c.run_id AND r.person_emby_id=c.anchor_emby_id AND r.recommendation_type IN('rename-person','review-existing-emby-person') WHERE c.run_id=@run AND c.case_type='repair-name' GROUP BY c.case_id",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_action(case_id,action_key,action_order,action_type,cluster_key,source_emby_id,target_emby_id,current_value,proposed_value,operator_choice_required,identity_confidence,operation_confidence,summary)
SELECT c.case_id,'rename',10,'rename-person','person',c.anchor_emby_id,c.anchor_emby_id,r.current_value,r.proposed_value,CASE c.decision_state WHEN 'actionable' THEN 0 ELSE 1 END,c.identity_confidence,c.operation_confidence,'Rename Emby '||c.anchor_emby_id||' from '||r.current_value||' to '||r.proposed_value||'.' FROM housekeeping_case c JOIN housekeeping_recommendation r ON r.run_id=c.run_id AND r.person_emby_id=c.anchor_emby_id AND r.recommendation_type IN('rename-person','review-existing-emby-person') WHERE c.run_id=@run AND c.case_type='repair-name' GROUP BY c.case_id",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_action(case_id,action_key,action_order,action_type,source_emby_id,target_emby_id,current_value,proposed_value,operator_choice_required,identity_confidence,operation_confidence,summary)
SELECT c.case_id,'dependent-rename',900000,'rename-person',c.anchor_emby_id,c.anchor_emby_id,r.current_value,r.proposed_value,CASE WHEN count(DISTINCT r.proposed_value)=1 THEN 0 ELSE 1 END,c.identity_confidence,c.operation_confidence,CASE WHEN count(DISTINCT r.proposed_value)=1 THEN 'After the identity/relationship operation, rename Emby '||c.anchor_emby_id||' from '||r.current_value||' to '||r.proposed_value||'.' ELSE 'Providers propose conflicting names; operator name choice remains within this parent case.' END FROM housekeeping_case c JOIN housekeeping_recommendation r ON r.run_id=c.run_id AND r.person_emby_id=c.anchor_emby_id AND r.recommendation_type IN('rename-person','review-existing-emby-person') WHERE c.run_id=@run AND c.case_type IN('repair-provider-identity','reassign-relationships','split-conflated-person','merge-duplicate-people') GROUP BY c.case_id",s=>s.TryBind("@run",run));
        }

        private static void BaseSingleParticipant(IDatabaseConnection x,long run,string type,string role,string disposition)=>Exec(x,@"INSERT INTO housekeeping_case_participant(case_id,emby_id,participant_role,current_name,current_tmdb_id,current_tvdb_id,current_imdb_id,proposed_disposition,summary)
SELECT c.case_id,p.emby_id,@role,p.name,p.tmdb_id,p.tvdb_id,p.imdb_id,@disposition,'Current Emby person is the continuity container, not proof of identity.' FROM housekeeping_case c JOIN emby_item p ON p.emby_id=c.anchor_emby_id WHERE c.run_id=@run AND c.case_type=@type",s=>{s.TryBind("@run",run);s.TryBind("@type",type);s.TryBind("@role",role);s.TryBind("@disposition",disposition);});

        private static void GeneralEvidence(IDatabaseConnection x,long run)
        {
            Exec(x,@"INSERT INTO housekeeping_case_evidence(case_id,category,polarity,provider,confidence,summary,source_signal_id,display_order)
SELECT c.case_id,'decision-summary','informational',r.provider,coalesce(r.operation_confidence,r.confidence),r.evidence_summary,NULL,0 FROM housekeeping_case c JOIN housekeeping_recommendation r ON r.run_id=c.run_id AND ((r.recommendation_type='replace-provider-id' AND c.case_key='identity:'||r.provider||':'||r.person_emby_id||':'||r.proposed_value) OR (r.recommendation_type='remove-provider-id' AND c.case_key='remove:'||r.provider||':'||r.person_emby_id||':'||r.current_value) OR (r.recommendation_type IN('rename-person','review-existing-emby-person') AND c.case_key='name:'||r.person_emby_id) OR c.case_key='unresolved:'||r.recommendation_id OR (c.case_key='split:cross:'||r.person_emby_id AND r.primary_signal_type='cross-provider-media-partition')) WHERE c.run_id=@run GROUP BY c.case_id,r.recommendation_id",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_evidence(case_id,category,polarity,provider,subject_provider_id,confidence,summary,source_signal_id,display_order)
SELECT c.case_id,CASE WHEN s.signal_type LIKE 'confirmed-%' THEN 'identity-link' WHEN s.signal_type='not-present' THEN 'identity-conflict' ELSE 'unresolved' END,CASE WHEN s.signal_type LIKE 'confirmed-%' THEN 'positive' WHEN s.signal_type='not-present' THEN 'negative' ELSE 'unresolved' END,s.provider,s.candidate_external_id,s.confidence,s.evidence_text,s.signal_id,20 FROM housekeeping_case c JOIN housekeeping_signal s ON s.run_id=c.run_id AND s.person_emby_id=c.anchor_emby_id WHERE c.run_id=@run AND ((s.candidate_external_id IS NULL AND s.signal_type LIKE 'identity-%') OR EXISTS(SELECT 1 FROM housekeeping_case_identity i WHERE i.case_id=c.case_id AND i.provider=s.provider AND i.provider_person_id=s.candidate_external_id))",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT INTO housekeeping_case_evidence(case_id,category,polarity,provider,subject_provider_id,confidence,summary,source_signal_id,display_order)
SELECT i.case_id,'provider-quality','positive',i.provider,i.provider_person_id,i.confidence,i.summary,NULL,10 FROM housekeeping_case_identity i JOIN housekeeping_case c ON c.case_id=i.case_id WHERE c.run_id=@run AND i.cluster_key='assigned' AND i.identity_state='corroborating' AND i.provenance='provider-native-media'",s=>s.TryBind("@run",run));
        }

        private static void RelationshipEvidence(IDatabaseConnection x,long run)
        {
            Exec(x,@"INSERT INTO housekeeping_case_relationship_evidence(case_id,case_relationship_id,provider,provider_person_id,production_provider_id,production_type,evidence_state,evidence_scope,polarity,acquisition_complete,raw_credit_count,normalized_credit_count,confidence,summary)
SELECT r.case_id,r.case_relationship_id,s.provider,CASE s.provider WHEN 'tmdb' THEN p.tmdb_id ELSE p.tvdb_id END,s.production_provider_id,s.production_type,
 CASE s.evidence_state WHEN 'supported-exact' THEN 'exact-support' WHEN 'supported-broader' THEN 'broader-series-support' WHEN 'not-present' THEN 'not-present' ELSE 'media-unresolved' END,
 CASE s.evidence_state WHEN 'supported-exact' THEN 'exact-media' WHEN 'supported-broader' THEN 'series' ELSE 'media' END,
 CASE s.evidence_state WHEN 'supported-exact' THEN 'positive' WHEN 'supported-broader' THEN 'positive' WHEN 'not-present' THEN 'negative' ELSE 'unresolved' END,
 CASE WHEN pe.acquisition_status='complete' AND (pe.raw_credit_count=pe.normalized_credit_count OR pe.raw_credit_count IS NULL) THEN 1 ELSE 0 END,pe.raw_credit_count,pe.normalized_credit_count,
 CASE s.evidence_state WHEN 'supported-exact' THEN .99 WHEN 'supported-broader' THEN .80 WHEN 'not-present' THEN .75 ELSE .20 END,
 upper(s.provider)||' '||s.evidence_state||' for person '||coalesce(CASE s.provider WHEN 'tmdb' THEN p.tmdb_id ELSE p.tvdb_id END,'unresolved')||' on provider production '||coalesce(s.production_provider_id,'unresolved')
FROM housekeeping_case_relationship r JOIN housekeeping_case c ON c.case_id=r.case_id JOIN emby_item p ON p.emby_id=r.current_person_emby_id JOIN emby_person_media_provider_support s ON s.person_emby_id=r.current_person_emby_id AND s.media_emby_id=r.media_emby_id AND s.provider IN('tmdb','tvdb') LEFT JOIN provider_production_evidence pe ON pe.provider=s.provider AND pe.production_type=s.production_type AND pe.production_provider_id=s.production_provider_id AND pe.component='screen-credits' WHERE c.run_id=@run",s=>s.TryBind("@run",run));
            Exec(x,@"INSERT OR IGNORE INTO housekeeping_case_relationship_evidence(case_id,case_relationship_id,provider,provider_person_id,production_provider_id,production_type,evidence_state,evidence_scope,polarity,acquisition_complete,raw_credit_count,normalized_credit_count,confidence,summary)
SELECT DISTINCT r.case_id,r.case_relationship_id,i.provider,i.provider_person_id,
 CASE i.provider WHEN 'tmdb' THEN coalesce(m.tmdb_id,tr.resolved_tmdb_id) ELSE coalesce(m.tvdb_id,vr.resolved_tvdb_id) END,r.media_type,
 'exact-support','exact-media','positive',CASE WHEN pe.acquisition_status='complete' THEN 1 ELSE 0 END,pe.raw_credit_count,pe.normalized_credit_count,i.confidence,
 upper(i.provider)||' supporting provider-side identity '||i.provider_person_id||' has exact credit support on provider production '||CASE i.provider WHEN 'tmdb' THEN coalesce(m.tmdb_id,tr.resolved_tmdb_id) ELSE coalesce(m.tvdb_id,vr.resolved_tvdb_id) END||'. '||i.summary
FROM housekeeping_case_relationship r
JOIN housekeeping_case c ON c.case_id=r.case_id
JOIN housekeeping_case_identity i ON i.case_id=r.case_id AND i.cluster_key=r.cluster_key AND i.identity_state='corroborating' AND i.provenance='provider-native-media'
JOIN emby_item m ON m.emby_id=r.media_emby_id
LEFT JOIN tmdb_item_resolution tr ON tr.emby_id=m.emby_id
LEFT JOIN item_resolution vr ON vr.emby_id=m.emby_id
JOIN provider_credit_observation pc ON pc.provider=i.provider AND pc.person_provider_id=i.provider_person_id AND pc.production_type=r.media_type AND pc.production_provider_id=CASE i.provider WHEN 'tmdb' THEN coalesce(m.tmdb_id,tr.resolved_tmdb_id) ELSE coalesce(m.tvdb_id,vr.resolved_tvdb_id) END
LEFT JOIN provider_production_evidence pe ON pe.provider=i.provider AND pe.production_type=r.media_type AND pe.production_provider_id=pc.production_provider_id AND pe.component='screen-credits'
WHERE c.run_id=@run",s=>s.TryBind("@run",run));
        }

        private static void Projections(IDatabaseConnection x,long run)
        {
            Exec(x,@"INSERT INTO housekeeping_case_projection(case_id,current_emby_ids,proposed_emby_ids,current_names,proposed_names,current_tmdb_ids,proposed_tmdb_ids,current_tvdb_ids,proposed_tvdb_ids,current_imdb_ids,proposed_imdb_ids,provider_summary,action_summary,evidence_summary,detail_row_count)
SELECT c.case_id,
 coalesce((SELECT group_concat(emby_id,',') FROM housekeeping_case_participant p WHERE p.case_id=c.case_id),cast(c.anchor_emby_id AS text)),
 coalesce((SELECT group_concat(DISTINCT coalesce(target_emby_id,source_emby_id)) FROM housekeeping_case_action a WHERE a.case_id=c.case_id AND coalesce(target_emby_id,source_emby_id) IS NOT NULL),cast(c.anchor_emby_id AS text)),
 coalesce((SELECT group_concat('Emby '||emby_id||': '||coalesce(current_name,'-'),' | ') FROM housekeeping_case_participant p WHERE p.case_id=c.case_id),''),
 coalesce((SELECT group_concat(DISTINCT coalesce(proposed_value,preferred_name)) FROM housekeeping_case_action a LEFT JOIN housekeeping_case_cluster k ON k.case_id=a.case_id AND k.cluster_key=a.cluster_key WHERE a.case_id=c.case_id AND (a.action_type='rename-person' OR k.preferred_name IS NOT NULL)),(SELECT name FROM emby_item WHERE emby_id=c.anchor_emby_id),''),
 coalesce((SELECT group_concat('Emby '||emby_id||': '||coalesce(current_tmdb_id,'-'),' | ') FROM housekeeping_case_participant p WHERE p.case_id=c.case_id),''),coalesce((SELECT group_concat(DISTINCT provider_person_id) FROM housekeeping_case_identity i WHERE i.case_id=c.case_id AND provider='tmdb' AND NOT(identity_state='corroborating' AND provenance='provider-native-media')),''),
 coalesce((SELECT group_concat('Emby '||emby_id||': '||coalesce(current_tvdb_id,'-'),' | ') FROM housekeeping_case_participant p WHERE p.case_id=c.case_id),''),coalesce((SELECT group_concat(DISTINCT provider_person_id) FROM housekeeping_case_identity i WHERE i.case_id=c.case_id AND provider='tvdb' AND NOT(identity_state='corroborating' AND provenance='provider-native-media')),''),
 coalesce((SELECT group_concat('Emby '||emby_id||': '||coalesce(current_imdb_id,'-'),' | ') FROM housekeeping_case_participant p WHERE p.case_id=c.case_id),''),coalesce((SELECT group_concat(DISTINCT provider_person_id) FROM housekeeping_case_identity i WHERE i.case_id=c.case_id AND provider='imdb' AND NOT(identity_state='corroborating' AND provenance='provider-native-media')),''),
 coalesce((SELECT group_concat(DISTINCT upper(provider)) FROM housekeeping_case_identity i WHERE i.case_id=c.case_id),''),
 coalesce((SELECT group_concat(summary,' | ') FROM housekeeping_case_action a WHERE a.case_id=c.case_id),CASE c.decision_state WHEN 'unresolved' THEN 'No mutation proposed; retain as unresolved review evidence.' ELSE '' END),
 c.summary,(SELECT count(*) FROM housekeeping_case_action a WHERE a.case_id=c.case_id)+(SELECT count(*) FROM housekeeping_case_evidence e WHERE e.case_id=c.case_id)+(SELECT count(*) FROM housekeeping_case_relationship_evidence e WHERE e.case_id=c.case_id)
FROM housekeeping_case c WHERE c.run_id=@run",s=>s.TryBind("@run",run));
        }

        private static void Validate(IDatabaseConnection x,long run)
        {
            AssertZero(x,run,"choose-survivor outside duplicate merge","SELECT count(*) FROM housekeeping_case_action a JOIN housekeeping_case c ON c.case_id=a.case_id WHERE c.run_id=@run AND a.action_type='choose-survivor' AND c.case_type<>'merge-duplicate-people'");
            AssertZero(x,run,"incomplete relationship move","SELECT count(*) FROM housekeeping_case_action a JOIN housekeeping_case c ON c.case_id=a.case_id WHERE c.run_id=@run AND a.action_type='move-relationship' AND (a.source_emby_id IS NULL OR a.target_emby_id IS NULL OR a.media_emby_id IS NULL OR a.relationship_type IS NULL)");
            AssertZero(x,run,"negative episode evidence without complete matching normalization","SELECT count(*) FROM housekeeping_case_relationship_evidence e JOIN housekeeping_case c ON c.case_id=e.case_id WHERE c.run_id=@run AND e.evidence_state='not-present' AND e.production_type='episode' AND (e.acquisition_complete<>1 OR e.raw_credit_count<>e.normalized_credit_count)");
            AssertZero(x,run,"provider repair does not have exactly one proposed identity cluster","SELECT count(*) FROM housekeeping_case c WHERE c.run_id=@run AND c.case_type IN('repair-provider-identity','reassign-relationships') AND 1<>(SELECT count(*) FROM housekeeping_case_cluster k WHERE k.case_id=c.case_id AND k.cluster_key='proposed')");
            AssertZero(x,run,"reconciliation case contains a mutation action","SELECT count(*) FROM housekeeping_case_action a JOIN housekeeping_case c ON c.case_id=a.case_id WHERE c.run_id=@run AND c.case_type='reconcile-person' AND a.action_type NOT IN('review-identity','review-relationship')");
            AssertZero(x,run,"admitted duplicate identity equivalence was not materialized","SELECT count(*) FROM hk_duplicate_identity_equivalence q JOIN housekeeping_case c ON c.run_id=@run AND c.case_key='duplicate:'||q.tmdb_id||':'||q.tvdb_id||':'||q.resolved_imdb WHERE NOT EXISTS(SELECT 1 FROM housekeeping_case_identity i WHERE i.case_id=c.case_id AND i.cluster_key='assigned' AND i.provider=q.candidate_provider AND i.provider_person_id=q.candidate_provider_id AND i.identity_state='corroborating' AND i.provenance='provider-native-media')");
            AssertZero(x,run,"supporting provider-side identity became an ID mutation","SELECT count(*) FROM housekeeping_case_action a JOIN housekeeping_case c ON c.case_id=a.case_id JOIN housekeeping_case_identity i ON i.case_id=c.case_id AND i.cluster_key='assigned' AND i.identity_state='corroborating' AND i.provenance='provider-native-media' AND i.provider=a.provider AND i.provider_person_id=a.proposed_value WHERE c.run_id=@run AND a.action_type='set-provider-id'");
            AssertZero(x,run,"actionable duplicate merge contains a relationship unsupported by both assigned identities","SELECT count(*) FROM housekeeping_case_relationship r JOIN housekeeping_case c ON c.case_id=r.case_id WHERE c.run_id=@run AND c.case_type='merge-duplicate-people' AND NOT EXISTS(SELECT 1 FROM housekeeping_case_relationship_evidence e WHERE e.case_relationship_id=r.case_relationship_id AND e.polarity='positive')");
            AssertZero(x,run,"reassignment case lacks an exact target-positive/current-negative relationship","SELECT count(*) FROM housekeeping_case c WHERE c.run_id=@run AND c.case_type='reassign-relationships' AND c.decision_state<>'unresolved' AND NOT EXISTS(SELECT 1 FROM housekeeping_case_relationship r WHERE r.case_id=c.case_id AND r.disposition='move')");
            AssertZero(x,run,"case without compact projection","SELECT count(*) FROM housekeeping_case c LEFT JOIN housekeeping_case_projection p ON p.case_id=c.case_id WHERE c.run_id=@run AND p.case_id IS NULL");
        }

        private static void AssertZero(IDatabaseConnection x,long run,string invariant,string sql)
        {
            using(var s=x.PrepareStatement(sql)){s.TryBind("@run",run);foreach(var r in s.ExecuteQuery())if(!r.IsDBNull(0)&&r.GetInt64(0)!=0)throw new InvalidOperationException("normalized-v17 invariant failed: "+invariant+" ("+r.GetInt64(0)+" rows)");}
        }
        private static void Exec(IDatabaseConnection x,string sql,Action<IStatement>b){using(var s=x.PrepareStatement(sql)){b(s);s.MoveNext();}}
        private static string Now()=>DateTime.UtcNow.ToString("o");
    }
}
