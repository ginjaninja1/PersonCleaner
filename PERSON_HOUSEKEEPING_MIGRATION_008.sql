PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

-- A recommendation is the operator decision.  Its evidence is materialized
-- once during the run so the review page never has to aggregate the much
-- larger housekeeping_signal ledger.
ALTER TABLE housekeeping_recommendation ADD COLUMN acceptance_path TEXT;
ALTER TABLE housekeeping_recommendation ADD COLUMN identity_confidence REAL;
ALTER TABLE housekeeping_recommendation ADD COLUMN relationship_confidence REAL;
ALTER TABLE housekeeping_recommendation ADD COLUMN operation_confidence REAL;

CREATE TABLE housekeeping_recommendation_evidence(
    evidence_id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL,
    recommendation_id INTEGER NOT NULL,
    category TEXT NOT NULL CHECK(category IN('person-identity','library-media','cross-provider-filmography','name-alias','ownership','contradiction','unresolved','summary')),
    polarity TEXT NOT NULL CHECK(polarity IN('positive','negative','contradictory','unresolved','informational')),
    evidence_scope TEXT NOT NULL,
    provider TEXT,
    counterpart_provider TEXT,
    subject_provider_id TEXT,
    counterpart_provider_id TEXT,
    external_id_type TEXT,
    external_id TEXT,
    media_emby_id INTEGER,
    production_type TEXT,
    production_title TEXT,
    relationship_role TEXT,
    provider_role TEXT,
    confidence REAL,
    summary TEXT NOT NULL,
    source_signal_id INTEGER,
    display_order INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY(recommendation_id) REFERENCES housekeeping_recommendation(recommendation_id) ON DELETE CASCADE
);
CREATE INDEX ix_housekeeping_recommendation_evidence_case
ON housekeeping_recommendation_evidence(recommendation_id,category,display_order,evidence_id);
CREATE INDEX ix_housekeeping_recommendation_evidence_run
ON housekeeping_recommendation_evidence(run_id,recommendation_id);
CREATE INDEX ix_housekeeping_recommendation_evidence_identity
ON housekeeping_recommendation_evidence(provider,subject_provider_id,counterpart_provider,counterpart_provider_id);

DROP VIEW housekeeping_latest_results;
CREATE VIEW housekeeping_latest_results AS
SELECT r.recommendation_id,r.run_id,r.person_emby_id,e.name AS person,
       r.operation,r.recommendation_type,r.primary_signal_type,r.provider,
       r.current_value,r.proposed_value,r.linked_media_count,
       r.checked_media_count,r.supported_media_count,r.score,r.confidence,
       r.review_status,r.evidence_summary,r.acceptance_path,
       r.identity_confidence,r.relationship_confidence,r.operation_confidence
FROM housekeeping_recommendation r
LEFT JOIN emby_item e ON e.emby_id=r.person_emby_id
WHERE r.run_id=(SELECT MAX(run_id) FROM housekeeping_run WHERE status='completed');

INSERT INTO archive_schema_migration(version,applied_utc,description)
VALUES(8,strftime('%Y-%m-%dT%H:%M:%fZ','now'),'Materialized operator-facing recommendation evidence and separate identity, relationship and operation confidence');

COMMIT;
