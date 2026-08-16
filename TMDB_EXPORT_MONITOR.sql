-- Live TMDB export monitor. One statement -> one DB Browser result grid.
-- Re-run to refresh. Times and rates come from committed SQLite observations.

WITH
run AS (
    SELECT * FROM tmdb_run_state WHERE task_key='TmdbArchiveFull'
),
request_stats AS (
    SELECT
        COUNT(*) AS total_cached,
        SUM(CASE WHEN julianday(fetched_utc)>=julianday('now')-1.0/1440 THEN 1 ELSE 0 END) AS requests_1m,
        SUM(CASE WHEN julianday(fetched_utc)>=julianday('now')-5.0/1440 THEN 1 ELSE 0 END) AS requests_5m,
        MAX(fetched_utc) AS latest_fetch
    FROM tmdb_api_response_cache
),
recent_failures AS (
    SELECT cache_key,error,fetched_utc
    FROM tmdb_fetch_cache
    WHERE state='failed'
    ORDER BY fetched_utc DESC
    LIMIT 20
),
audit(section,subject,detail,value) AS (
    SELECT '01 progress','status',status,message FROM run
    UNION ALL SELECT '01 progress','items','processed / total',printf('%d / %d (%.2f%%)',processed_items,total_items,100.0*processed_items/NULLIF(total_items,0)) FROM run
    UNION ALL SELECT '01 progress','results','archived / unresolved-or-failed',printf('%d / %d',success_count,failure_count) FROM run
    UNION ALL SELECT '01 progress','checkpoint age','seconds',printf('%.1f',(julianday('now')-julianday(updated_utc))*86400.0) FROM run
    UNION ALL SELECT '01 progress','elapsed','minutes',printf('%.1f',(julianday('now')-julianday(started_utc))*1440.0) FROM run
    UNION ALL SELECT '01 progress','average item rate','items/minute',printf('%.1f',processed_items/NULLIF((julianday('now')-julianday(started_utc))*1440.0,0)) FROM run

    UNION ALL SELECT '02 request throughput','uncached requests','last 1 minute',CAST(requests_1m AS TEXT) FROM request_stats
    UNION ALL SELECT '02 request throughput','uncached requests','last 5 minutes',CAST(requests_5m AS TEXT) FROM request_stats
    UNION ALL SELECT '02 request throughput','uncached rate','requests/minute over 5m',printf('%.1f',requests_5m/5.0) FROM request_stats
    UNION ALL SELECT '02 request throughput','latest committed response','seconds ago',printf('%.1f',(julianday('now')-julianday(latest_fetch))*86400.0) FROM request_stats
    UNION ALL SELECT '02 request throughput','current response cache','rows',CAST(total_cached AS TEXT) FROM request_stats
    UNION ALL SELECT '02 request throughput','response history','rows',CAST(COUNT(*) AS TEXT) FROM tmdb_api_response_archive

    UNION ALL
    SELECT '03 request minute buckets',strftime('%Y-%m-%d %H:%M',fetched_utc),'uncached responses',CAST(COUNT(*) AS TEXT)
    FROM tmdb_api_response_cache
    WHERE julianday(fetched_utc)>=julianday('now')-10.0/1440
    GROUP BY strftime('%Y-%m-%d %H:%M',fetched_utc)

    UNION ALL
    SELECT '04 fetch outcomes',state,'rows',CAST(COUNT(*) AS TEXT)
    FROM tmdb_fetch_cache GROUP BY state

    UNION ALL
    SELECT '05 entities',entity_type,'archived',CAST(COUNT(*) AS TEXT)
    FROM tmdb_entity GROUP BY entity_type

    UNION ALL
    SELECT '06 resolutions',entity_type,provenance||' / '||method,CAST(COUNT(*) AS TEXT)
    FROM tmdb_item_resolution GROUP BY entity_type,provenance,method

    UNION ALL
    SELECT '07 external IDs',entity_type,source_name,CAST(COUNT(DISTINCT tmdb_id) AS TEXT)
    FROM tmdb_external_id GROUP BY entity_type,source_name

    UNION ALL
    SELECT '08 aliases',entity_type,'aliases / entities',printf('%d / %d',
        (SELECT COUNT(*) FROM tmdb_alias a WHERE a.entity_type=e.entity_type),COUNT(*))
    FROM tmdb_entity e GROUP BY entity_type

    UNION ALL
    SELECT '09 credit capture',source_entity_type||' endpoint',production_type||' '||credit_kind,
           printf('%d observations; %d people; %d productions',COUNT(*),COUNT(DISTINCT person_tmdb_id),COUNT(DISTINCT production_tmdb_id))
    FROM tmdb_credit_observation
    GROUP BY source_entity_type,production_type,credit_kind

    UNION ALL
    SELECT '10 recent failures',cache_key,COALESCE(error,'no error text'),fetched_utc
    FROM recent_failures
)
SELECT section,subject,detail,value FROM audit
ORDER BY section,subject,detail;
