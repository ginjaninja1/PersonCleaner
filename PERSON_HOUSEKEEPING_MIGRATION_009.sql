PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

-- One recommendation is one operator decision.  Ordered child actions describe
-- every dependent mutation (relationship movement, identity change, rename,
-- retirement) without creating competing top-level review rows.
CREATE TABLE housekeeping_recommendation_action(
    action_id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL,
    recommendation_id INTEGER NOT NULL,
    action_order INTEGER NOT NULL,
    action_type TEXT NOT NULL CHECK(action_type IN(
        'select-survivor','retain-person','move-relationships','create-person',
        'set-provider-id','remove-provider-id','rename-person','retire-empty-person',
        'review-identity','review-relationship')),
    source_emby_id INTEGER,
    target_emby_id INTEGER,
    provider TEXT,
    current_value TEXT,
    proposed_value TEXT,
    dependency TEXT,
    operator_choice_required INTEGER NOT NULL DEFAULT 0 CHECK(operator_choice_required IN(0,1)),
    confidence REAL,
    summary TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    FOREIGN KEY(recommendation_id) REFERENCES housekeeping_recommendation(recommendation_id) ON DELETE CASCADE
);
CREATE INDEX ix_housekeeping_recommendation_action_case
ON housekeeping_recommendation_action(recommendation_id,action_order,action_id);
CREATE INDEX ix_housekeeping_recommendation_action_run
ON housekeeping_recommendation_action(run_id,recommendation_id);
CREATE INDEX ix_housekeeping_recommendation_action_emby
ON housekeeping_recommendation_action(source_emby_id,target_emby_id,action_type);

INSERT INTO archive_schema_migration(version,applied_utc,description)
VALUES(9,strftime('%Y-%m-%dT%H:%M:%fZ','now'),'Normalized ordered child actions so merge/split cases absorb dependent rename and identity operations');

COMMIT;
