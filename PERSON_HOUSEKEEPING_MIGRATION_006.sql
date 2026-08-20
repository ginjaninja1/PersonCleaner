PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

CREATE TABLE provider_identity_issue(
    issue_id INTEGER PRIMARY KEY AUTOINCREMENT,
    provider TEXT NOT NULL CHECK(provider IN ('tmdb','tvdb')),
    issue_type TEXT NOT NULL CHECK(issue_type IN ('person-split','person-conflation','bad-credit','bad-name')),
    identity_ids TEXT NOT NULL,
    related_emby_ids TEXT NOT NULL,
    preferred_identity_id TEXT,
    status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','confirmed','dismissed','provider-fixed')),
    confidence REAL,
    evidence_json TEXT NOT NULL,
    first_observed_utc TEXT NOT NULL,
    last_observed_utc TEXT NOT NULL,
    reviewed_utc TEXT,
    reviewed_by TEXT,
    review_note TEXT,
    UNIQUE(provider,issue_type,identity_ids)
);
CREATE INDEX ix_provider_identity_issue_status ON provider_identity_issue(provider,status,issue_type);
CREATE INDEX ix_provider_identity_issue_emby ON provider_identity_issue(related_emby_ids);

INSERT INTO archive_schema_migration(version,applied_utc,description)
VALUES(6,strftime('%Y-%m-%dT%H:%M:%fZ','now'),'Persistent reviewed provider identity issues for provider-side person splits, conflations, names and credits');

COMMIT;
