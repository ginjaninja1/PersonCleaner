-- Run against personcleaner-archive.db after applying migration 007.
-- Every query is read-only. Empty output from foreign_key_check is success.

SELECT version,applied_utc,description
FROM archive_schema_migration WHERE version=7;

SELECT 'housekeeping_runs_remaining' check_name,count(*) value FROM housekeeping_run
UNION ALL SELECT 'housekeeping_signals_remaining',count(*) FROM housekeeping_signal
UNION ALL SELECT 'housekeeping_recommendations_remaining',count(*) FROM housekeeping_recommendation;

SELECT provider,count(*) production_count,
       sum(raw_credit_count) raw_screen_credits,
       sum(normalized_credit_count) normalized_screen_credits,
       sum(CASE WHEN raw_credit_count<>normalized_credit_count THEN 1 ELSE 0 END) mismatched_productions
FROM provider_production_evidence
GROUP BY provider ORDER BY provider;

SELECT provider,production_type,production_provider_id,source_endpoint,
       raw_credit_count,normalized_credit_count,missing_credit_count
FROM provider_normalization_mismatch
ORDER BY abs(missing_credit_count) DESC,provider,production_provider_id
LIMIT 100;

-- Annie Karstens in A Fresh Start: this must return TMDB person 1137005.
SELECT provider,production_provider_id,person_provider_id,job_or_character,normalization_source
FROM provider_credit_observation
WHERE provider='tmdb' AND person_provider_id='1137005' AND production_type='episode';

-- These archives are intentionally retained; record the post-migration totals.
SELECT 'tmdb_response_cache' archive_name,count(*) row_count FROM tmdb_api_response_cache
UNION ALL SELECT 'tmdb_response_archive',count(*) FROM tmdb_api_response_archive
UNION ALL SELECT 'tvdb_response_cache',count(*) FROM api_response_cache
UNION ALL SELECT 'tvdb_response_archive',count(*) FROM api_response_archive;

PRAGMA foreign_key_check;
