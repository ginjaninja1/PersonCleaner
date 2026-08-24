-- PersonCleaner schema 6 -> 7
-- Apply only while Emby is stopped and after taking a copy of entity-resolution.db.
-- Run with sqlite3's -bail option so the version guard stops the script on error.
PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

CREATE TEMP TABLE personcleaner_schema_guard(
    version INTEGER NOT NULL CHECK(version=6)
);
INSERT INTO personcleaner_schema_guard(version)
VALUES(COALESCE((SELECT version FROM schema_info WHERE singleton=1),-1));
DROP TABLE personcleaner_schema_guard;

-- This is a safety index, not part of the bounded evidence cohort. The next
-- evidence run populates it from every live Emby Person row.
CREATE TABLE global_local_person(
    emby_id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    tmdb_id TEXT,
    tvdb_id TEXT,
    imdb_id TEXT
);

UPDATE schema_info SET version=7 WHERE singleton=1 AND version=6;
COMMIT;
