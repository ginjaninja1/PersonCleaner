-- Open tvdb-archive.db read-only in DB Browser for SQLite and run each section.
-- A running Emby task writes in WAL mode, so read-only inspection does not block it.

-- 1. Current task status and genuine resume checkpoint.
SELECT task_key, status, total_items, processed_items, success_count,
       failure_count, last_emby_id, updated_utc, message
FROM run_state
WHERE task_key IN ('TvdbArchivePreview', 'TvdbArchiveFull')
ORDER BY task_key;

-- 1a. Progress against the persisted total in every required dump area.
SELECT entity_type, id_area, total_items, examined_items, accepted_dumps,
       review_items, failed_items, percent_examined
FROM export_area_progress
WHERE task_key='TvdbArchiveFull'
ORDER BY CASE entity_type WHEN 'series' THEN 1 WHEN 'movie' THEN 2 WHEN 'person' THEN 3 ELSE 4 END,
         id_area;

-- 2. Every preview/full item remains distinguishable by provenance.
SELECT provenance, entity_type, COUNT(*) AS item_count,
       ROUND(AVG(confidence), 4) AS average_confidence
FROM item_resolution
GROUP BY provenance, entity_type
ORDER BY entity_type, provenance;

-- 3. Confirm data was actually archived, not merely matched.
SELECT entity_type, COUNT(*) AS archived_entities,
       SUM(CASE WHEN raw_json IS NOT NULL THEN 1 ELSE 0 END) AS extended_records
FROM tvdb_entity
GROUP BY entity_type
ORDER BY entity_type;

-- Episode feeds discover every regular TVDB episode, while extended calls are
-- deliberately limited to accepted Emby episodes. Check that subset separately.
SELECT COUNT(*) AS accepted_emby_episodes,
       SUM(CASE WHEN t.raw_json IS NOT NULL THEN 1 ELSE 0 END) AS extended_episode_records,
       SUM(CASE WHEN EXISTS (
           SELECT 1 FROM remote_id x
           WHERE x.tvdb_id=t.tvdb_id AND x.entity_type='episode'
       ) THEN 1 ELSE 0 END) AS episodes_with_external_ids
FROM item_resolution r
JOIN tvdb_entity t ON t.tvdb_id=r.resolved_tvdb_id AND t.entity_type='episode'
WHERE r.entity_type='episode' AND r.provenance IN ('direct','inferred');

-- 4. External identifiers captured literally from TVDB.
SELECT entity_type, source_name, COUNT(*) AS identifiers
FROM remote_id
GROUP BY entity_type, source_name
ORDER BY entity_type, identifiers DESC;

-- 5. Filmography/cast rows captured by production type and TVDB people type.
SELECT subject_type, COALESCE(credit_type, '(not supplied)') AS credit_type,
       COUNT(*) AS credits
FROM credit
GROUP BY subject_type, COALESCE(credit_type, '(not supplied)')
ORDER BY subject_type, credits DESC;

-- 6. Cached API calls and their expiry. Successful calls should be about 30 days.
SELECT COUNT(*) AS cached_responses, MIN(fetched_utc) AS oldest_fetch,
       MAX(fetched_utc) AS newest_fetch, MIN(expires_utc) AS earliest_expiry,
       MAX(expires_utc) AS latest_expiry
FROM api_response_cache;

-- 7. Items needing human review; these must not be mixed with accepted exports.
SELECT *
FROM identity_review_queue
ORDER BY item_type, emby_name;

-- 8. Accepted items with both Emby and TVDB identity columns.
SELECT *
FROM resolved_searchable_media
WHERE provenance IN ('direct', 'inferred')
ORDER BY item_type, emby_name
LIMIT 100;
