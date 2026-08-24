-- PersonCleaner schema 5 -> 6
-- Apply only while Emby is stopped and after taking a copy of entity-resolution.db.
-- Run with sqlite3's -bail option so the version guard stops the script on error.
PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

CREATE TEMP TABLE personcleaner_schema_guard(
    version INTEGER NOT NULL CHECK(version=5)
);
INSERT INTO personcleaner_schema_guard(version)
VALUES(COALESCE((SELECT version FROM schema_info WHERE singleton=1),-1));
DROP TABLE personcleaner_schema_guard;

ALTER TABLE work_queue
ADD COLUMN graph_eligible INTEGER NOT NULL DEFAULT 0 CHECK(graph_eligible IN(0,1));

-- Every person queue row created by schema 5 was media-discovered or explicitly
-- correction-seeded. Preserve that meaning for the current historical queue.
UPDATE work_queue SET graph_eligible=1 WHERE entity_type='person';

CREATE TABLE provider_absence_cache(
    provider TEXT NOT NULL,
    entity_type TEXT NOT NULL,
    media_type TEXT NOT NULL,
    provider_id TEXT NOT NULL,
    confirmed_utc INTEGER NOT NULL,
    status_code INTEGER NOT NULL,
    PRIMARY KEY(provider,entity_type,media_type,provider_id)
) WITHOUT ROWID;

CREATE TABLE acquisition_observation(
    run_id INTEGER NOT NULL,
    provider TEXT NOT NULL,
    entity_type TEXT NOT NULL,
    media_type TEXT NOT NULL,
    provider_id TEXT NOT NULL,
    outcome TEXT NOT NULL CHECK(outcome IN('PRESENT','ABSENT','UNAVAILABLE')),
    source TEXT NOT NULL,
    graph_eligible INTEGER NOT NULL CHECK(graph_eligible IN(0,1)),
    observed_utc INTEGER NOT NULL,
    detail TEXT,
    PRIMARY KEY(run_id,provider,entity_type,media_type,provider_id),
    FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE INDEX idx_acquisition_resolution
ON acquisition_observation(run_id,entity_type,outcome,graph_eligible,provider,provider_id);

UPDATE schema_info SET version=6 WHERE singleton=1 AND version=5;
COMMIT;
