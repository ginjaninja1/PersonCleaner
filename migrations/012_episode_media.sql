-- PersonCleaner schema 11 -> 12
-- Apply only while Emby is stopped and after taking a copy of entity-resolution.db.
-- Run with sqlite3's -bail option so the version guard stops the script on error.
PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

CREATE TEMP TABLE personcleaner_schema_guard(version INTEGER NOT NULL CHECK(version=11));
INSERT INTO personcleaner_schema_guard(version) VALUES(COALESCE((SELECT version FROM schema_info WHERE singleton=1),-1));
DROP TABLE personcleaner_schema_guard;

ALTER TABLE resolution_run ADD COLUMN selected_episodes INTEGER NOT NULL DEFAULT 0;

ALTER TABLE current_media ADD COLUMN tmdb_acquisition_id TEXT;
ALTER TABLE current_media ADD COLUMN tvdb_acquisition_id TEXT;
ALTER TABLE current_media ADD COLUMN parent_emby_id INTEGER;
ALTER TABLE current_media ADD COLUMN parent_tmdb_id TEXT;
ALTER TABLE current_media ADD COLUMN parent_tvdb_id TEXT;
ALTER TABLE current_media ADD COLUMN season_number INTEGER;
ALTER TABLE current_media ADD COLUMN episode_number INTEGER;

UPDATE current_media
SET tmdb_acquisition_id=tmdb_id,
    tvdb_acquisition_id=tvdb_id;

ALTER TABLE work_queue ADD COLUMN route_series_id TEXT;
ALTER TABLE work_queue ADD COLUMN route_season_number INTEGER;
ALTER TABLE work_queue ADD COLUMN route_episode_number INTEGER;

UPDATE schema_info SET version=12 WHERE singleton=1 AND version=11;
COMMIT;
