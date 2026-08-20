PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

CREATE TABLE housekeeping_run(
    run_id INTEGER PRIMARY KEY,
    algorithm_version TEXT NOT NULL,
    base_truth_id INTEGER NOT NULL,
    derived_truth_id INTEGER,
    phase TEXT NOT NULL,
    progress REAL NOT NULL DEFAULT 0,
    status TEXT NOT NULL CHECK(status IN ('running','completed','cancelled','failed')),
    observation_cutoff_utc TEXT NOT NULL,
    started_utc TEXT NOT NULL,
    heartbeat_utc TEXT NOT NULL,
    completed_utc TEXT,
    error TEXT,
    FOREIGN KEY(run_id) REFERENCES experiment_run(run_id),
    FOREIGN KEY(base_truth_id) REFERENCES truth(truth_id),
    FOREIGN KEY(derived_truth_id) REFERENCES truth(truth_id)
);
CREATE INDEX ix_housekeeping_run_status ON housekeeping_run(status,run_id DESC);

CREATE TABLE housekeeping_signal(
    signal_id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL,
    person_emby_id INTEGER NOT NULL,
    subject_truth_entity_id TEXT NOT NULL,
    provider TEXT NOT NULL CHECK(provider IN ('emby','tmdb','tvdb')),
    signal_type TEXT NOT NULL,
    current_external_id TEXT,
    candidate_external_id TEXT,
    current_name TEXT,
    candidate_name TEXT,
    media_emby_id INTEGER,
    media_type TEXT,
    relationship_role TEXT,
    provider_role TEXT,
    linked_media_count INTEGER,
    checked_media_count INTEGER,
    supported_media_count INTEGER,
    score REAL,
    confidence REAL,
    evidence_text TEXT,
    evidence_json TEXT,
    created_utc TEXT NOT NULL,
    FOREIGN KEY(run_id) REFERENCES housekeeping_run(run_id)
);
CREATE INDEX ix_housekeeping_signal_run_type ON housekeeping_signal(run_id,signal_type,provider);
CREATE INDEX ix_housekeeping_signal_run_person ON housekeeping_signal(run_id,person_emby_id,provider);
CREATE INDEX ix_housekeeping_signal_candidate ON housekeeping_signal(run_id,provider,candidate_external_id);
CREATE INDEX ix_housekeeping_signal_media ON housekeeping_signal(run_id,media_emby_id,provider);

CREATE TABLE housekeeping_recommendation(
    recommendation_id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL,
    person_emby_id INTEGER NOT NULL,
    subject_truth_entity_id TEXT NOT NULL,
    operation TEXT NOT NULL CHECK(operation IN ('merge','split','change_identity','move_relationship','create','remove','update')),
    recommendation_type TEXT NOT NULL,
    primary_signal_type TEXT NOT NULL,
    provider TEXT,
    current_value TEXT,
    proposed_value TEXT,
    linked_media_count INTEGER NOT NULL DEFAULT 0,
    checked_media_count INTEGER NOT NULL DEFAULT 0,
    supported_media_count INTEGER NOT NULL DEFAULT 0,
    score REAL,
    confidence REAL,
    review_status TEXT NOT NULL DEFAULT 'pending' CHECK(review_status IN ('pending','accepted','rejected')),
    evidence_summary TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    reviewed_utc TEXT,
    reviewed_by TEXT,
    review_note TEXT,
    FOREIGN KEY(run_id) REFERENCES housekeeping_run(run_id)
);
CREATE INDEX ix_housekeeping_rec_run_type ON housekeeping_recommendation(run_id,recommendation_type,primary_signal_type);
CREATE INDEX ix_housekeeping_rec_run_person ON housekeeping_recommendation(run_id,person_emby_id,provider);
CREATE INDEX ix_housekeeping_rec_review ON housekeeping_recommendation(run_id,review_status,confidence DESC);

CREATE TABLE truth_entity_delta(
    truth_id INTEGER NOT NULL,
    truth_entity_id TEXT NOT NULL,
    operation TEXT NOT NULL CHECK(operation IN ('create','update','remove')),
    preferred_name TEXT,
    disposition TEXT,
    provenance_reference TEXT NOT NULL,
    PRIMARY KEY(truth_id,truth_entity_id),
    FOREIGN KEY(truth_id) REFERENCES truth(truth_id)
);
CREATE TABLE truth_identity_delta(
    truth_id INTEGER NOT NULL,
    truth_entity_id TEXT NOT NULL,
    provider TEXT NOT NULL,
    operation TEXT NOT NULL CHECK(operation IN ('set','remove')),
    external_id TEXT,
    provenance_reference TEXT NOT NULL,
    PRIMARY KEY(truth_id,truth_entity_id,provider),
    FOREIGN KEY(truth_id) REFERENCES truth(truth_id)
);
CREATE TABLE truth_relationship_delta(
    truth_id INTEGER NOT NULL,
    relationship_id TEXT NOT NULL,
    operation TEXT NOT NULL CHECK(operation IN ('create','update','remove','move')),
    subject_truth_entity_id TEXT,
    object_truth_entity_id TEXT,
    relationship_type TEXT,
    role TEXT,
    character_name TEXT,
    provenance_reference TEXT NOT NULL,
    PRIMARY KEY(truth_id,relationship_id),
    FOREIGN KEY(truth_id) REFERENCES truth(truth_id)
);

CREATE VIEW housekeeping_latest_results AS
SELECT r.recommendation_id,r.run_id,r.person_emby_id,e.name AS person,
       r.operation,r.recommendation_type,r.primary_signal_type,r.provider,
       r.current_value,r.proposed_value,r.linked_media_count,
       r.checked_media_count,r.supported_media_count,r.score,r.confidence,
       r.review_status,r.evidence_summary
FROM housekeeping_recommendation r
LEFT JOIN emby_item e ON e.emby_id=r.person_emby_id
WHERE r.run_id=(SELECT MAX(run_id) FROM housekeeping_run WHERE status='completed');

INSERT INTO archive_schema_migration(version,applied_utc,description)
VALUES(5,strftime('%Y-%m-%dT%H:%M:%fZ','now'),'Normalized person-housekeeping signals, recommendations, resumable phase state, and delta truths');

COMMIT;
