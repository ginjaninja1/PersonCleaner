-- Deterministic like-for-like sample: first 3 matched Emby IDs per entity type.
-- Raw size is the exact cached response used for the entity detail call.
-- Signal counts are normalized external IDs + aliases + credits. Credit scope differs:
-- TVDB currently retains its screen-role whitelist; TMDB retains the appended credit set.

WITH matched AS (
    SELECT e.emby_id,e.item_type,e.name,
           r.resolved_tvdb_id AS tvdb_id,
           tr.resolved_tmdb_id AS tmdb_id,
           ROW_NUMBER() OVER(PARTITION BY e.item_type ORDER BY e.emby_id) AS sample_rank
    FROM emby_item e
    JOIN item_resolution r ON r.emby_id=e.emby_id
      AND r.provenance IN ('direct','inferred')
    JOIN tmdb_item_resolution tr ON tr.emby_id=e.emby_id
      AND tr.provenance IN ('direct','coordinate','external-id')
),
sample AS (
    SELECT * FROM matched WHERE sample_rank<=3
),
measured AS (
    SELECT s.*,
      (SELECT length(CAST(response_json AS BLOB))
       FROM api_response_cache
       WHERE request_path=(CASE s.item_type
           WHEN 'series' THEN '/series/' WHEN 'movie' THEN '/movies/'
           WHEN 'person' THEN '/people/' ELSE '/episodes/' END)
           ||s.tvdb_id||'/extended') AS tvdb_chars,
      (SELECT length(CAST(response_json AS BLOB))
       FROM tmdb_api_response_cache
       WHERE (s.item_type='series' AND request_path LIKE '/tv/'||s.tmdb_id||'?%')
          OR (s.item_type='movie' AND request_path LIKE '/movie/'||s.tmdb_id||'?%')
          OR (s.item_type='person' AND request_path LIKE '/person/'||s.tmdb_id||'?%')
          OR (s.item_type='episode' AND request_path GLOB '/tv/*/season/*/episode/*'
              AND CAST(json_extract(response_json,'$.id') AS TEXT)=s.tmdb_id)
       LIMIT 1) AS tmdb_chars,
      (SELECT COUNT(*) FROM remote_id
       WHERE tvdb_id=s.tvdb_id AND entity_type=s.item_type) AS tvdb_external_ids,
      (SELECT COUNT(*) FROM tvdb_alias
       WHERE tvdb_id=s.tvdb_id AND entity_type=s.item_type) AS tvdb_aliases,
      (SELECT COUNT(*) FROM credit
       WHERE (s.item_type='person' AND person_tvdb_id=s.tvdb_id)
          OR (s.item_type<>'person' AND subject_tvdb_id=s.tvdb_id AND subject_type=s.item_type)) AS tvdb_credits,
      (SELECT COUNT(*) FROM tmdb_external_id
       WHERE tmdb_id=s.tmdb_id AND entity_type=s.item_type) AS tmdb_external_ids,
      (SELECT COUNT(*) FROM tmdb_alias
       WHERE tmdb_id=s.tmdb_id AND entity_type=s.item_type) AS tmdb_aliases,
      (SELECT COUNT(*) FROM tmdb_credit
       WHERE (s.item_type='person' AND person_tmdb_id=s.tmdb_id)
          OR (s.item_type<>'person' AND production_tmdb_id=s.tmdb_id AND production_type=s.item_type)) AS tmdb_credits
    FROM sample s
)
SELECT item_type,emby_id,name,tvdb_id,tmdb_id,
       ROUND(tvdb_chars/1024.0,1) AS tvdb_kib,
       ROUND(tmdb_chars/1024.0,1) AS tmdb_kib,
       ROUND(1.0*tmdb_chars/NULLIF(tvdb_chars,0),2) AS tmdb_size_ratio,
       tvdb_external_ids,tvdb_aliases,tvdb_credits,
       tmdb_external_ids,tmdb_aliases,tmdb_credits,
       ROUND((tvdb_external_ids+tvdb_aliases+tvdb_credits)/(tvdb_chars/1024.0),2) AS tvdb_signals_per_kib,
       ROUND((tmdb_external_ids+tmdb_aliases+tmdb_credits)/(tmdb_chars/1024.0),2) AS tmdb_signals_per_kib
FROM measured
ORDER BY item_type,emby_id;
