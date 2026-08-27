-- PersonCleaner schema 9 -> 10
-- Apply only while Emby is stopped and after taking a copy of entity-resolution.db.
-- Run with sqlite3's -bail option so the version guard stops the script on error.
PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

CREATE TEMP TABLE personcleaner_schema_guard(version INTEGER NOT NULL CHECK(version=9));
INSERT INTO personcleaner_schema_guard(version) VALUES(COALESCE((SELECT version FROM schema_info WHERE singleton=1),-1));
DROP TABLE personcleaner_schema_guard;

-- Persist the provider-native owner assertions shown by the correction UI. The
-- primary key is also the case-local read index; opening one case never scans the
-- reusable provider credit index or the global Emby person table.
CREATE TABLE resolution_case_credit_attribution(
    run_id INTEGER NOT NULL, case_id TEXT NOT NULL, assignment_id TEXT NOT NULL,
    provider TEXT NOT NULL CHECK(provider IN('tmdb','tvdb')),
    provider_media_id TEXT NOT NULL, provider_person_id TEXT NOT NULL,
    person_name TEXT NOT NULL DEFAULT '', role TEXT NOT NULL, role_category TEXT NOT NULL,
    outcome_id TEXT NOT NULL,
    PRIMARY KEY(run_id,case_id,assignment_id,provider,provider_media_id,provider_person_id,role),
    FOREIGN KEY(run_id,case_id,assignment_id) REFERENCES resolution_case_credit(run_id,case_id,assignment_id) ON DELETE CASCADE,
    FOREIGN KEY(run_id,case_id,outcome_id) REFERENCES resolution_identity_outcome(run_id,case_id,outcome_id) ON DELETE CASCADE
) WITHOUT ROWID;

-- The global binding table can contain hundreds of thousands of Emby people.
-- These indexes make targeted collision and correction lookups proportional to
-- the requested IDs instead of the population size.
CREATE INDEX idx_current_local_tmdb ON current_local_person(tmdb_id) WHERE tmdb_id IS NOT NULL;
CREATE INDEX idx_current_local_tvdb ON current_local_person(tvdb_id) WHERE tvdb_id IS NOT NULL;
CREATE INDEX idx_current_local_imdb ON current_local_person(imdb_id) WHERE imdb_id IS NOT NULL;
CREATE INDEX idx_global_local_tmdb ON global_local_person(tmdb_id) WHERE tmdb_id IS NOT NULL;
CREATE INDEX idx_global_local_tvdb ON global_local_person(tvdb_id) WHERE tvdb_id IS NOT NULL;
CREATE INDEX idx_global_local_imdb ON global_local_person(imdb_id) WHERE imdb_id IS NOT NULL;

UPDATE schema_info SET version=10 WHERE singleton=1 AND version=9;
COMMIT;
