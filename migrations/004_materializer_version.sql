-- PersonCleaner schema 3 -> 4
-- Apply only while Emby is stopped and after taking a copy of entity-resolution.db.
-- Run with sqlite3's -bail option so the version guard stops the script on error.
PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

CREATE TEMP TABLE personcleaner_schema_guard(
    version INTEGER NOT NULL CHECK(version=3)
);
INSERT INTO personcleaner_schema_guard(version)
VALUES(COALESCE((SELECT version FROM schema_info WHERE singleton=1),-1));
DROP TABLE personcleaner_schema_guard;

ALTER TABLE cache_manifest
ADD COLUMN materializer_version INTEGER NOT NULL DEFAULT 1;

ALTER TABLE provider_media_observation
ADD COLUMN materializer_version INTEGER NOT NULL DEFAULT 1;

UPDATE schema_info SET version=4 WHERE singleton=1 AND version=3;
COMMIT;
