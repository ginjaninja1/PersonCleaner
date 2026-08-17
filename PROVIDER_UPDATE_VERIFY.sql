-- Unified PersonCleaner update audit. Open personcleaner-archive.db read-only.

-- Latest task run.
SELECT * FROM provider_update_run ORDER BY run_id DESC LIMIT 1;

-- Live dashboard: snapshot phase has total_work=0; provider phase has queued/running/done counts.
WITH latest AS (SELECT MAX(run_id) AS run_id FROM provider_update_run),
counts AS (
  SELECT last_run_id,
         SUM(state='pending') AS queued,
         SUM(state='running') AS running,
         SUM(state='completed') AS completed,
         SUM(state='failed') AS failed
  FROM provider_work
  WHERE last_run_id=(SELECT run_id FROM latest)
)
SELECT r.run_id,r.status,r.message,r.started_utc,r.updated_utc,
       r.total_items,r.total_work,r.completed_work,
       COALESCE(c.queued,0) AS queued,
       COALESCE(c.running,0) AS running,
       COALESCE(c.completed,0) AS completed,
       COALESCE(c.failed,0) AS failed,
       ROUND(100.0*r.completed_work/NULLIF(r.total_work,0),2) AS provider_percent
FROM provider_update_run r
LEFT JOIN counts c ON c.last_run_id=r.run_id
WHERE r.run_id=(SELECT run_id FROM latest);

-- Exact response-cache hits and misses observed during the latest run.
SELECT provider,cache_hits,cache_misses,
       ROUND(100.0*cache_hits/NULLIF(cache_hits+cache_misses,0),2) AS hit_percent,
       updated_utc
FROM provider_run_cache
WHERE run_id=(SELECT MAX(run_id) FROM provider_update_run)
ORDER BY provider;

-- Work executing now.
SELECT w.provider,w.emby_id,w.entity_type,e.name,w.route,w.updated_utc
FROM provider_work w
LEFT JOIN emby_item e ON e.emby_id=w.emby_id
WHERE w.last_run_id=(SELECT MAX(run_id) FROM provider_update_run)
  AND w.state='running'
ORDER BY w.provider,w.updated_utc;

-- Exact local snapshot progress, including the denominator, for new runs.
SELECT phase,expected_entities,processed_entities,
       expected_entities-processed_entities AS entities_remaining,
       expected_relationships,processed_relationships,
       expected_relationships-processed_relationships AS relationships_remaining,
       ROUND(100.0*(processed_entities+processed_relationships)/
             NULLIF(expected_entities+expected_relationships,0),2) AS snapshot_percent,
       updated_utc
FROM provider_snapshot_progress
WHERE run_id=(SELECT MAX(run_id) FROM provider_update_run);

-- Provider/entity outcomes for the latest run.
SELECT provider,entity_type,state,outcome,COUNT(*) AS item_count
FROM provider_work
WHERE last_run_id=(SELECT MAX(run_id) FROM provider_update_run)
GROUP BY provider,entity_type,state,outcome
ORDER BY provider,entity_type,state,outcome;

-- Errors retained by the manifest.
SELECT provider,emby_id,entity_type,route,outcome,error,updated_utc
FROM provider_work
WHERE last_run_id=(SELECT MAX(run_id) FROM provider_update_run)
  AND state='failed'
ORDER BY provider,entity_type,emby_id;

-- Emby observation and truth coverage.
SELECT
  (SELECT COUNT(*) FROM emby_item) AS current_entities,
  (SELECT COUNT(*) FROM emby_observation) AS entity_observations,
  (SELECT COUNT(*) FROM emby_relationship) AS current_relationships,
  (SELECT COUNT(*) FROM emby_relationship_observation) AS relationship_observations,
  (SELECT COUNT(*) FROM truth_entity WHERE truth_id=1) AS baseline_truth_entities,
  (SELECT COUNT(*) FROM truth_relationship WHERE truth_id=1) AS baseline_truth_relationships;

-- Side-by-side provider position.
SELECT * FROM provider_identity_signals
ORDER BY item_type,emby_id;

-- Historical provider-specific verification remains available in
-- TVDB_ARCHIVE_VERIFY.sql and TMDB_ARCHIVE_VERIFY.sql.
