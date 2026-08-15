-- Open tvdb-archive.db read-only and run these after the TMDB preview/full task.

SELECT task_key,status,total_items,processed_items,success_count,failure_count,
       last_emby_id,updated_utc,message
FROM tmdb_run_state ORDER BY updated_utc DESC;

SELECT entity_type,COUNT(*) AS entities,
       SUM(CASE WHEN raw_json IS NOT NULL THEN 1 ELSE 0 END) AS raw_payloads
FROM tmdb_entity GROUP BY entity_type;

SELECT entity_type,source_name,COUNT(DISTINCT tmdb_id) AS entities
FROM tmdb_external_id GROUP BY entity_type,source_name
ORDER BY entity_type,source_name;

-- Side-by-side independent provider signals for an Emby item.
SELECT * FROM provider_identity_signals WHERE emby_id = 173844;

SELECT 'external-id' AS evidence_type,source_name AS label,external_id AS value
FROM tmdb_external_id
WHERE tmdb_id=(SELECT resolved_tmdb_id FROM tmdb_item_resolution WHERE emby_id=173844)
  AND entity_type='person'
UNION ALL
SELECT 'alias',COALESCE(country,''),alias FROM tmdb_alias
WHERE tmdb_id=(SELECT resolved_tmdb_id FROM tmdb_item_resolution WHERE emby_id=173844)
  AND entity_type='person';

-- IMDb-find candidates remain visible even when ambiguous.
SELECT r.emby_id,e.name AS emby_name,r.provenance,r.method,r.candidate_count,
       c.rank,c.tmdb_id,c.name AS candidate_name,c.source_external_id
FROM tmdb_item_resolution r JOIN emby_item e ON e.emby_id=r.emby_id
LEFT JOIN tmdb_resolution_candidate c ON c.emby_id=r.emby_id
WHERE r.method='tmdb-find-imdb' ORDER BY r.emby_id,c.rank;

SELECT c.person_tmdb_id,p.name AS person_name,c.production_type,
       c.production_tmdb_id,c.production_name,c.first_date,
       c.credit_kind,c.job_or_character,c.department,c.episode_count
FROM tmdb_credit c
LEFT JOIN tmdb_entity p ON p.tmdb_id=c.person_tmdb_id AND p.entity_type='person'
WHERE c.person_tmdb_id=(SELECT resolved_tmdb_id FROM tmdb_item_resolution WHERE emby_id=173844)
ORDER BY c.first_date,c.production_name;

SELECT cache_key,state,attempt_count,fetched_utc,next_attempt_utc,error
FROM tmdb_fetch_cache WHERE state='failed' ORDER BY fetched_utc DESC;
