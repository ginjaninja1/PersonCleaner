SELECT version,applied_utc,description
FROM archive_schema_migration
WHERE version=8;

PRAGMA table_info(housekeeping_recommendation_evidence);
PRAGMA index_list(housekeeping_recommendation_evidence);
PRAGMA foreign_key_check;

SELECT algorithm_version,status,count(*) runs
FROM housekeeping_run
GROUP BY algorithm_version,status
ORDER BY max(run_id) DESC;

SELECT acceptance_path,recommendation_type,provider,count(*) cases
FROM housekeeping_recommendation
WHERE run_id=(SELECT max(run_id) FROM housekeeping_run WHERE status='completed')
GROUP BY acceptance_path,recommendation_type,provider
ORDER BY acceptance_path,recommendation_type,provider;

SELECT category,polarity,count(*) evidence_rows
FROM housekeeping_recommendation_evidence
WHERE run_id=(SELECT max(run_id) FROM housekeeping_run WHERE status='completed')
GROUP BY category,polarity
ORDER BY category,polarity;

SELECT r.recommendation_id,r.person_emby_id,r.recommendation_type,r.provider,
       r.current_value,r.proposed_value,r.acceptance_path,
       r.identity_confidence,r.relationship_confidence,r.operation_confidence,
       count(e.evidence_id) materialized_evidence_rows
FROM housekeeping_recommendation r
LEFT JOIN housekeeping_recommendation_evidence e ON e.recommendation_id=r.recommendation_id
WHERE r.run_id=(SELECT max(run_id) FROM housekeeping_run WHERE status='completed')
GROUP BY r.recommendation_id
ORDER BY r.recommendation_id;
