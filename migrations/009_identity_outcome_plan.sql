-- PersonCleaner schema 8 -> 9
-- Apply only while Emby is stopped and after taking a copy of entity-resolution.db.
-- Run with sqlite3's -bail option so the version guard stops the script on error.
-- Schema-8 relationship evidence remains the audit input to this case-wide projection.
PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

CREATE TEMP TABLE personcleaner_schema_guard(version INTEGER NOT NULL CHECK(version=8));
INSERT INTO personcleaner_schema_guard(version) VALUES(COALESCE((SELECT version FROM schema_info WHERE singleton=1),-1));
DROP TABLE personcleaner_schema_guard;

CREATE TABLE resolution_case(
    run_id INTEGER NOT NULL, case_id TEXT NOT NULL, plan_hash TEXT NOT NULL,
    display_name TEXT NOT NULL, case_type TEXT NOT NULL, summary TEXT NOT NULL,
    warning TEXT NOT NULL DEFAULT '',
    state TEXT NOT NULL CHECK(state IN('COMPLETE','CORRECTION_REQUIRED','BLOCKED')),
    apply_caption TEXT NOT NULL,
    PRIMARY KEY(run_id,case_id),
    FOREIGN KEY(run_id) REFERENCES resolution_run(run_id) ON DELETE CASCADE
) WITHOUT ROWID;
CREATE INDEX idx_resolution_case_ui ON resolution_case(run_id,state,display_name,case_id);

CREATE TABLE resolution_case_decision(
    run_id INTEGER NOT NULL, case_id TEXT NOT NULL, decision_id TEXT NOT NULL, sort_order INTEGER NOT NULL,
    PRIMARY KEY(run_id,case_id,decision_id),
    FOREIGN KEY(run_id,case_id) REFERENCES resolution_case(run_id,case_id) ON DELETE CASCADE,
    FOREIGN KEY(run_id,decision_id) REFERENCES resolution_decision(run_id,decision_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE resolution_identity_outcome(
    run_id INTEGER NOT NULL, case_id TEXT NOT NULL, outcome_id TEXT NOT NULL, sort_order INTEGER NOT NULL,
    cluster_key TEXT NOT NULL,
    target_kind TEXT NOT NULL CHECK(target_kind IN('EXISTING','NEW','UNRESOLVED')),
    target_emby_id INTEGER, display_name TEXT NOT NULL, outcome TEXT NOT NULL,
    PRIMARY KEY(run_id,case_id,outcome_id),
    CHECK((target_kind='EXISTING' AND target_emby_id IS NOT NULL) OR (target_kind IN('NEW','UNRESOLVED') AND target_emby_id IS NULL)),
    FOREIGN KEY(run_id,case_id) REFERENCES resolution_case(run_id,case_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE resolution_case_person_snapshot(
    run_id INTEGER NOT NULL, case_id TEXT NOT NULL, emby_id INTEGER NOT NULL, name TEXT NOT NULL,
    tmdb_id TEXT, tvdb_id TEXT, imdb_id TEXT,
    PRIMARY KEY(run_id,case_id,emby_id),
    FOREIGN KEY(run_id,case_id) REFERENCES resolution_case(run_id,case_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE resolution_identity_outcome_source(
    run_id INTEGER NOT NULL, case_id TEXT NOT NULL, outcome_id TEXT NOT NULL, source_emby_id INTEGER NOT NULL,
    PRIMARY KEY(run_id,case_id,outcome_id,source_emby_id),
    FOREIGN KEY(run_id,case_id,outcome_id) REFERENCES resolution_identity_outcome(run_id,case_id,outcome_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE resolution_identity_outcome_provider_id(
    run_id INTEGER NOT NULL, case_id TEXT NOT NULL, outcome_id TEXT NOT NULL,
    provider TEXT NOT NULL CHECK(provider IN('tmdb','tvdb','imdb')), provider_id TEXT NOT NULL,
    source TEXT NOT NULL CHECK(source IN('native','external')),
    PRIMARY KEY(run_id,case_id,outcome_id,provider,provider_id),
    FOREIGN KEY(run_id,case_id,outcome_id) REFERENCES resolution_identity_outcome(run_id,case_id,outcome_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE resolution_case_credit(
    run_id INTEGER NOT NULL, case_id TEXT NOT NULL, assignment_id TEXT NOT NULL,
    source_person_emby_id INTEGER NOT NULL, target_outcome_id TEXT NOT NULL,
    media_emby_id INTEGER NOT NULL, media_type TEXT NOT NULL, media_name TEXT NOT NULL, role TEXT NOT NULL,
    tmdb_id TEXT, tvdb_id TEXT, tvdb_slug TEXT, imdb_id TEXT,
    disposition TEXT NOT NULL CHECK(disposition IN('KEEP','MOVE')), rationale TEXT NOT NULL,
    correction_required INTEGER NOT NULL CHECK(correction_required IN(0,1)),
    PRIMARY KEY(run_id,case_id,assignment_id),
    FOREIGN KEY(run_id,case_id,target_outcome_id) REFERENCES resolution_identity_outcome(run_id,case_id,outcome_id) ON DELETE CASCADE
) WITHOUT ROWID;
CREATE INDEX idx_resolution_case_credit_media ON resolution_case_credit(run_id,case_id,media_emby_id);

CREATE TABLE resolution_question(
    run_id INTEGER NOT NULL, case_id TEXT NOT NULL, question_id TEXT NOT NULL, kind TEXT NOT NULL,
    outcome_id TEXT, assignment_id TEXT, narrative TEXT NOT NULL,
    PRIMARY KEY(run_id,case_id,question_id),
    FOREIGN KEY(run_id,case_id) REFERENCES resolution_case(run_id,case_id) ON DELETE CASCADE,
    FOREIGN KEY(run_id,case_id,outcome_id) REFERENCES resolution_identity_outcome(run_id,case_id,outcome_id) ON DELETE CASCADE,
    FOREIGN KEY(run_id,case_id,assignment_id) REFERENCES resolution_case_credit(run_id,case_id,assignment_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE resolution_question_choice(
    run_id INTEGER NOT NULL, case_id TEXT NOT NULL, question_id TEXT NOT NULL, choice_id TEXT NOT NULL,
    caption TEXT NOT NULL, effect TEXT NOT NULL,
    correction_kind TEXT NOT NULL, correction_operation TEXT NOT NULL,
    provider TEXT NOT NULL DEFAULT '', media_type TEXT NOT NULL DEFAULT '', provider_media_id TEXT NOT NULL DEFAULT '',
    provider_person_id TEXT NOT NULL DEFAULT '', field_name TEXT NOT NULL DEFAULT '', current_value TEXT NOT NULL DEFAULT '',
    replacement_value TEXT NOT NULL DEFAULT '', secondary_provider TEXT NOT NULL DEFAULT '', secondary_id TEXT NOT NULL DEFAULT '',
    emby_id INTEGER, reason TEXT NOT NULL, note TEXT NOT NULL DEFAULT '',
    PRIMARY KEY(run_id,case_id,question_id,choice_id),
    FOREIGN KEY(run_id,case_id,question_id) REFERENCES resolution_question(run_id,case_id,question_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE identity_case_apply(
    apply_id INTEGER PRIMARY KEY AUTOINCREMENT, source_run_id INTEGER NOT NULL, case_id TEXT NOT NULL,
    reviewed_plan_hash TEXT NOT NULL, started_utc INTEGER NOT NULL, finished_utc INTEGER,
    status TEXT NOT NULL CHECK(status IN('STARTED','COMMITTED','ROLLED_BACK','FAILED')), summary TEXT NOT NULL
);

CREATE TABLE identity_case_apply_change(
    apply_id INTEGER NOT NULL, change_order INTEGER NOT NULL, change_kind TEXT NOT NULL,
    source_emby_id INTEGER, target_emby_id INTEGER, outcome_id TEXT, media_emby_id INTEGER, role TEXT,
    provider TEXT, old_value TEXT, new_value TEXT, summary TEXT NOT NULL,
    PRIMARY KEY(apply_id,change_order),
    FOREIGN KEY(apply_id) REFERENCES identity_case_apply(apply_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE provider_correction_selection(
    correction_id INTEGER PRIMARY KEY,
    source_run_id INTEGER NOT NULL,
    case_id TEXT NOT NULL,
    question_id TEXT NOT NULL,
    choice_id TEXT NOT NULL,
    selected_utc INTEGER NOT NULL,
    FOREIGN KEY(correction_id) REFERENCES provider_correction(correction_id) ON DELETE CASCADE
);

UPDATE schema_info SET version=9 WHERE singleton=1 AND version=8;
COMMIT;
