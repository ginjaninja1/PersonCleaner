-- PersonCleaner schema 4 -> 5
-- Apply only while Emby is stopped and after taking a copy of entity-resolution.db.
-- Run with sqlite3's -bail option so the version guard stops the script on error.
-- Raw provider payloads and flattened facts remain unchanged. Corrections are a
-- persistent operator-owned overlay applied only to effective resolution input.
PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

CREATE TEMP TABLE personcleaner_schema_guard(
    version INTEGER NOT NULL CHECK(version=4)
);
INSERT INTO personcleaner_schema_guard(version)
VALUES(COALESCE((SELECT version FROM schema_info WHERE singleton=1),-1));
DROP TABLE personcleaner_schema_guard;

CREATE TABLE provider_correction(
    correction_id INTEGER PRIMARY KEY AUTOINCREMENT,
    kind TEXT NOT NULL,
    operation TEXT NOT NULL,
    provider TEXT NOT NULL DEFAULT '',
    media_type TEXT NOT NULL DEFAULT '',
    provider_media_id TEXT NOT NULL DEFAULT '',
    provider_person_id TEXT NOT NULL DEFAULT '',
    field_name TEXT NOT NULL DEFAULT '',
    current_value TEXT NOT NULL DEFAULT '',
    replacement_value TEXT NOT NULL DEFAULT '',
    secondary_provider TEXT NOT NULL DEFAULT '',
    secondary_id TEXT NOT NULL DEFAULT '',
    emby_id INTEGER,
    reason TEXT NOT NULL,
    note TEXT NOT NULL DEFAULT '',
    enabled INTEGER NOT NULL CHECK(enabled IN(0,1)),
    created_utc INTEGER NOT NULL,
    updated_utc INTEGER NOT NULL
);
CREATE INDEX idx_provider_correction_enabled
ON provider_correction(enabled,kind,provider);

CREATE TABLE correction_application(
    run_id INTEGER NOT NULL,
    correction_id INTEGER NOT NULL,
    matched_count INTEGER NOT NULL,
    changed_count INTEGER NOT NULL,
    summary TEXT NOT NULL,
    applied_utc INTEGER NOT NULL,
    PRIMARY KEY(run_id,correction_id),
    FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE,
    FOREIGN KEY(correction_id) REFERENCES provider_correction(correction_id) ON DELETE CASCADE
) WITHOUT ROWID;

UPDATE schema_info SET version=5 WHERE singleton=1 AND version=4;
COMMIT;
