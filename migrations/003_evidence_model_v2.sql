-- PersonCleaner schema 2 -> 3
-- Apply only while Emby is stopped and after taking a copy of entity-resolution.db.
-- Run with sqlite3's -bail option so the version guard stops the script on error.
-- This migration preserves all raw payloads, flattened credits, manual bridges and
-- historical decisions. A subsequent offline recalculation replaces run decisions.
PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

CREATE TEMP TABLE personcleaner_schema_guard(
    version INTEGER NOT NULL CHECK(version=2)
);
INSERT INTO personcleaner_schema_guard(version)
VALUES(COALESCE((SELECT version FROM schema_info WHERE singleton=1),-1));
DROP TABLE personcleaner_schema_guard;

ALTER TABLE provider_media_credit ADD COLUMN role_category TEXT NOT NULL DEFAULT 'Unknown';
ALTER TABLE provider_media_credit ADD COLUMN role_name TEXT;

UPDATE provider_media_credit
SET role_category = CASE
    WHEN role LIKE '%Director%' THEN 'Director'
    WHEN role LIKE '%Writer%' OR role LIKE '%Screenplay%' THEN 'Writer'
    WHEN role LIKE '%Producer%' THEN 'Producer'
    WHEN role LIKE '%Creator%' OR role LIKE '%Showrunner%' THEN 'Creator'
    WHEN role LIKE 'Actor%' OR role LIKE 'Guest Star%' OR provider='tmdb' THEN 'Actor'
    ELSE 'Other'
END,
role_name = CASE
    WHEN instr(role, ':') > 0 THEN trim(substr(role, instr(role, ':') + 1))
    ELSE role
END;

ALTER TABLE resolution_decision ADD COLUMN local_anchor_confidence REAL NOT NULL DEFAULT 0;
UPDATE resolution_decision SET local_anchor_confidence=1 WHERE anchor_emby_id IS NOT NULL;

CREATE TABLE provider_media_observation(
    provider TEXT NOT NULL,
    media_type TEXT NOT NULL,
    provider_media_id TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    observed_utc INTEGER NOT NULL,
    endpoint_shape TEXT NOT NULL,
    credit_scope TEXT NOT NULL,
    is_complete INTEGER NOT NULL CHECK(is_complete IN(0,1)),
    PRIMARY KEY(provider,media_type,provider_media_id)
) WITHOUT ROWID;

INSERT OR IGNORE INTO provider_media_observation
SELECT m.provider,m.media_type,m.provider_media_id,c.payload_hash,c.last_fetched_utc,
       CASE WHEN m.provider='tvdb' THEN 'extended-full' ELSE 'details-with-credits' END,
       'screen-roles',1
FROM provider_media m
JOIN cache_manifest c ON c.provider=m.provider AND c.entity_type='media'
 AND c.media_type=m.media_type AND c.provider_id=m.provider_media_id;

CREATE TABLE resolution_pair(
    run_id INTEGER NOT NULL,
    pair_id TEXT NOT NULL,
    left_provider TEXT NOT NULL,
    left_provider_person_id TEXT NOT NULL,
    right_provider TEXT NOT NULL,
    right_provider_person_id TEXT NOT NULL,
    model_version TEXT NOT NULL,
    disposition TEXT NOT NULL,
    confidence REAL NOT NULL,
    PRIMARY KEY(run_id,pair_id),
    FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE
) WITHOUT ROWID;
CREATE INDEX idx_resolution_pair_disposition ON resolution_pair(run_id,disposition,confidence);

CREATE TABLE resolution_pair_feature(
    run_id INTEGER NOT NULL,
    pair_id TEXT NOT NULL,
    feature_name TEXT NOT NULL,
    numeric_value REAL,
    text_value TEXT,
    PRIMARY KEY(run_id,pair_id,feature_name),
    FOREIGN KEY(run_id,pair_id) REFERENCES resolution_pair(run_id,pair_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE resolution_cluster(
    run_id INTEGER NOT NULL,
    cluster_id TEXT NOT NULL,
    anchor_emby_id INTEGER,
    identity_confidence REAL NOT NULL,
    local_anchor_confidence REAL NOT NULL,
    PRIMARY KEY(run_id,cluster_id),
    FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE resolution_cluster_member(
    run_id INTEGER NOT NULL,
    cluster_id TEXT NOT NULL,
    provider TEXT NOT NULL,
    provider_person_id TEXT NOT NULL,
    PRIMARY KEY(run_id,cluster_id,provider,provider_person_id),
    FOREIGN KEY(run_id,cluster_id) REFERENCES resolution_cluster(run_id,cluster_id) ON DELETE CASCADE
) WITHOUT ROWID;

UPDATE schema_info SET version=3 WHERE singleton=1 AND version=2;
COMMIT;
