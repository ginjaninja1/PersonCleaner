-- TMDB preview/full audit.
-- This file is deliberately ONE SQL statement so DB Browser produces one result grid.

WITH audit(section, subject, detail, value) AS (
    SELECT
        '01 task',
        task_key,
        status,
        printf('%d/%d processed; %d archived; %d unresolved-or-failed',
               processed_items,total_items,success_count,failure_count)
    FROM tmdb_run_state

    UNION ALL
    SELECT
        '02 entities',
        entity_type,
        'archived',
        CAST(COUNT(*) AS TEXT)
    FROM tmdb_entity
    GROUP BY entity_type

    UNION ALL
    SELECT
        '03 resolutions',
        entity_type,
        provenance || ' / ' || method,
        CAST(COUNT(*) AS TEXT)
    FROM tmdb_item_resolution
    GROUP BY entity_type,provenance,method

    UNION ALL
    SELECT
        '04 external IDs',
        entity_type,
        source_name,
        CAST(COUNT(DISTINCT tmdb_id) AS TEXT)
    FROM tmdb_external_id
    GROUP BY entity_type,source_name

    UNION ALL
    SELECT
        '05 aliases',
        e.entity_type,
        'entities with aliases / total entities',
        CAST(SUM(CASE WHEN EXISTS(
            SELECT 1 FROM tmdb_alias a
            WHERE a.tmdb_id=e.tmdb_id AND a.entity_type=e.entity_type
        ) THEN 1 ELSE 0 END) AS TEXT) || ' / ' || CAST(COUNT(*) AS TEXT)
    FROM tmdb_entity e
    GROUP BY e.entity_type

    UNION ALL
    SELECT
        '06 credits',
        source_entity_type || ' endpoint',
        production_type || ' ' || credit_kind,
        printf('%d observations; %d people; %d productions',
               COUNT(*),COUNT(DISTINCT person_tmdb_id),COUNT(DISTINCT production_tmdb_id))
    FROM tmdb_credit_observation
    GROUP BY source_entity_type,production_type,credit_kind

    UNION ALL
    SELECT
        '07 corroborated credits',
        production_type,
        'relationships seen from person and production endpoints',
        CAST(COUNT(*) AS TEXT)
    FROM (
        SELECT person_tmdb_id,production_type,production_tmdb_id
        FROM tmdb_credit_observation
        GROUP BY person_tmdb_id,production_type,production_tmdb_id
        HAVING MAX(CASE WHEN source_entity_type='person' THEN 1 ELSE 0 END)=1
           AND MAX(CASE WHEN source_entity_type=production_type THEN 1 ELSE 0 END)=1
    ) corroborated
    GROUP BY production_type

    UNION ALL
    SELECT
        '08 review',
        e.item_type || ' Emby ' || CAST(e.emby_id AS TEXT),
        e.name,
        r.provenance || ' / ' || r.method ||
        CASE WHEN e.imdb_id IS NULL THEN '' ELSE '; IMDb ' || e.imdb_id END
    FROM tmdb_item_resolution r
    JOIN emby_item e ON e.emby_id=r.emby_id
    WHERE r.provenance IN ('unresolved','ambiguous','failed')

    UNION ALL
    SELECT
        '09 cross-type find',
        e.item_type || ' Emby ' || CAST(e.emby_id AS TEXT),
        e.name,
        CASE
            WHEN json_extract(c.response_json,'$.movie_results[0].id') IS NOT NULL
                THEN 'TMDB movie ' || json_extract(c.response_json,'$.movie_results[0].id')
            WHEN json_extract(c.response_json,'$.tv_results[0].id') IS NOT NULL
                THEN 'TMDB series ' || json_extract(c.response_json,'$.tv_results[0].id')
            WHEN json_extract(c.response_json,'$.person_results[0].id') IS NOT NULL
                THEN 'TMDB person ' || json_extract(c.response_json,'$.person_results[0].id')
            WHEN json_extract(c.response_json,'$.tv_episode_results[0].id') IS NOT NULL
                THEN 'TMDB episode ' || json_extract(c.response_json,'$.tv_episode_results[0].id')
            ELSE 'no TMDB find result'
        END
    FROM emby_item e
    JOIN tmdb_item_resolution r ON r.emby_id=e.emby_id
    JOIN tmdb_api_response_cache c
      ON c.request_path='/find/'||e.imdb_id||'?external_source=imdb_id'
    WHERE r.method='tmdb-find-imdb'
)
SELECT section,subject,detail,value
FROM audit
ORDER BY section,subject,detail;
