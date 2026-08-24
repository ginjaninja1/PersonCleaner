-- PersonCleaner schema 7 -> 8
-- Apply only while Emby is stopped and after taking a copy of entity-resolution.db.
-- Run with sqlite3's -bail option so the version guard stops the script on error.
PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

CREATE TEMP TABLE personcleaner_schema_guard(
    version INTEGER NOT NULL CHECK(version=7)
);
INSERT INTO personcleaner_schema_guard(version)
VALUES(COALESCE((SELECT version FROM schema_info WHERE singleton=1),-1));
DROP TABLE personcleaner_schema_guard;

CREATE TABLE resolution_credit_assignment(
    run_id INTEGER NOT NULL,
    decision_id TEXT NOT NULL,
    source_person_emby_id INTEGER NOT NULL,
    target_person_emby_id INTEGER NOT NULL,
    media_emby_id INTEGER NOT NULL,
    role TEXT NOT NULL,
    disposition TEXT NOT NULL CHECK(disposition IN('KEEP','MOVE')),
    component_key TEXT NOT NULL,
    rationale TEXT NOT NULL,
    PRIMARY KEY(run_id,decision_id,source_person_emby_id,target_person_emby_id,media_emby_id,role),
    FOREIGN KEY(run_id,decision_id) REFERENCES resolution_decision(run_id,decision_id) ON DELETE CASCADE
) WITHOUT ROWID;

UPDATE schema_info SET version=8 WHERE singleton=1 AND version=7;
COMMIT;
