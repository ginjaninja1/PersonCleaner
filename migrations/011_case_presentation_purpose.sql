-- PersonCleaner schema 10 -> 11
-- Apply only while Emby is stopped and after taking a copy of entity-resolution.db.
-- Run with sqlite3's -bail option so the version guard stops the script on error.
PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

CREATE TEMP TABLE personcleaner_schema_guard(version INTEGER NOT NULL CHECK(version=10));
INSERT INTO personcleaner_schema_guard(version) VALUES(COALESCE((SELECT version FROM schema_info WHERE singleton=1),-1));
DROP TABLE personcleaner_schema_guard;

ALTER TABLE resolution_case ADD COLUMN presentation_purpose TEXT NOT NULL DEFAULT 'PROBLEM'
    CHECK(presentation_purpose IN('PROBLEM','SATISFIED_CHANGE','SATISFIED_NO_CHANGE'));

UPDATE resolution_case
SET presentation_purpose=CASE
    WHEN state<>'COMPLETE' THEN 'PROBLEM'
    WHEN apply_caption='No Emby changes required' THEN 'SATISFIED_NO_CHANGE'
    ELSE 'SATISFIED_CHANGE'
END;

UPDATE resolution_case
SET case_type=CASE
    WHEN state='BLOCKED' THEN 'Blocked by out-of-scope records'
    WHEN state<>'COMPLETE' THEN case_type
    WHEN apply_caption='No Emby changes required' THEN 'No Emby changes required'
    WHEN apply_caption LIKE '%create %' AND apply_caption LIKE '%move %' AND apply_caption LIKE '%change %' THEN 'Person creation, credit realignment and provider ID alignment'
    WHEN apply_caption LIKE '%create %' AND apply_caption LIKE '%move %' THEN 'Person creation and credit realignment'
    WHEN apply_caption LIKE '%move %' AND apply_caption LIKE '%change %' THEN 'Credit realignment and provider ID alignment'
    WHEN apply_caption LIKE '%move %' THEN 'Credit realignment'
    WHEN apply_caption LIKE '%create %' AND apply_caption LIKE '%change %' THEN 'Person creation and provider ID alignment'
    WHEN apply_caption LIKE '%create %' THEN 'Person creation'
    ELSE 'Provider ID alignment'
END;

DROP INDEX IF EXISTS idx_resolution_case_ui;
CREATE INDEX idx_resolution_case_ui
ON resolution_case(run_id,presentation_purpose,case_type,display_name,case_id);

CREATE UNIQUE INDEX idx_identity_case_apply_committed
ON identity_case_apply(source_run_id,case_id,reviewed_plan_hash)
WHERE status='COMMITTED';

UPDATE schema_info SET version=11 WHERE singleton=1 AND version=10;
COMMIT;
