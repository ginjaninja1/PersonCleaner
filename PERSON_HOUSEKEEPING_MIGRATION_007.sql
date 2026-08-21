PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;

-- Raw provider caches and response archives are deliberately untouched. The
-- materialized table below is a rebuildable, provider-symmetric evidence index.
DROP VIEW provider_credit_observation;
CREATE TABLE provider_credit_observation(
    provider TEXT NOT NULL CHECK(provider IN ('tmdb','tvdb')),
    source_entity_type TEXT NOT NULL,
    source_provider_id TEXT NOT NULL,
    person_provider_id TEXT NOT NULL,
    production_provider_id TEXT NOT NULL,
    production_type TEXT NOT NULL,
    credit_id TEXT NOT NULL DEFAULT '',
    credit_kind TEXT NOT NULL CHECK(credit_kind IN ('cast','crew')),
    job_or_character TEXT NOT NULL DEFAULT '',
    department TEXT,
    episode_count INTEGER,
    evidence_scope TEXT NOT NULL CHECK(evidence_scope IN ('exact-media','broader-series','aggregate-unmapped')),
    source_endpoint TEXT,
    normalization_source TEXT NOT NULL,
    observed_utc TEXT NOT NULL,
    PRIMARY KEY(provider,source_entity_type,source_provider_id,person_provider_id,production_provider_id,production_type,credit_id,credit_kind)
) WITHOUT ROWID;

INSERT OR IGNORE INTO provider_credit_observation
SELECT 'tvdb',source_entity_type,source_tvdb_id,person_tvdb_id,production_tvdb_id,production_type,
       cast(character_id AS text),CASE WHEN credit_type IN('Actor','Guest Star') THEN 'cast' ELSE 'crew' END,
       coalesce(role_name,''),credit_type,NULL,
       CASE WHEN production_type IN('episode','movie') THEN 'exact-media' WHEN production_type='series' THEN 'broader-series' ELSE 'aggregate-unmapped' END,
       NULL,'tvdb-normalized-observation',observed_utc
FROM tvdb_credit_observation;

INSERT OR IGNORE INTO provider_credit_observation
SELECT 'tmdb',source_entity_type,source_tmdb_id,person_tmdb_id,production_tmdb_id,production_type,
       credit_id,credit_kind,coalesce(job_or_character,''),department,episode_count,
       CASE WHEN production_type IN('episode','movie') THEN 'exact-media' WHEN production_type='series' THEN 'broader-series' ELSE 'aggregate-unmapped' END,
       NULL,'tmdb-normalized-observation',observed_utc
FROM tmdb_credit_observation WHERE source_entity_type<>'episode';

-- Rebuild exact TMDB episode cast directly from preserved response JSON. This
-- repairs the historic loss of root guest_stars without another API request.
INSERT OR IGNORE INTO provider_credit_observation
SELECT 'tmdb','episode',cast(json_extract(c.response_json,'$.id') AS text),cast(json_extract(j.value,'$.id') AS text),
       cast(json_extract(c.response_json,'$.id') AS text),'episode',coalesce(json_extract(j.value,'$.credit_id'),'cast:'||json_extract(j.value,'$.id')),
       'cast',coalesce(json_extract(j.value,'$.character'),''),NULL,NULL,'exact-media',c.request_path,'tmdb-cache-credits-cast',c.fetched_utc
FROM tmdb_api_response_cache c,json_each(c.response_json,'$.credits.cast') j
WHERE c.request_path GLOB '/tv/*/season/*/episode/*?append_to_response=external_ids,credits';
INSERT OR IGNORE INTO provider_credit_observation
SELECT 'tmdb','episode',cast(json_extract(c.response_json,'$.id') AS text),cast(json_extract(j.value,'$.id') AS text),
       cast(json_extract(c.response_json,'$.id') AS text),'episode',coalesce(json_extract(j.value,'$.credit_id'),'guest:'||json_extract(j.value,'$.id')),
       'cast',coalesce(json_extract(j.value,'$.character'),''),NULL,NULL,'exact-media',c.request_path,'tmdb-cache-root-guest-stars',c.fetched_utc
FROM tmdb_api_response_cache c,json_each(c.response_json,'$.guest_stars') j
WHERE c.request_path GLOB '/tv/*/season/*/episode/*?append_to_response=external_ids,credits';
INSERT OR IGNORE INTO provider_credit_observation
SELECT 'tmdb','episode',cast(json_extract(c.response_json,'$.id') AS text),cast(json_extract(j.value,'$.id') AS text),
       cast(json_extract(c.response_json,'$.id') AS text),'episode',coalesce(json_extract(j.value,'$.credit_id'),'crew:'||json_extract(j.value,'$.id')||':'||coalesce(json_extract(j.value,'$.job'),'')),
       'crew',coalesce(json_extract(j.value,'$.job'),''),json_extract(j.value,'$.department'),NULL,'exact-media',c.request_path,'tmdb-cache-credits-crew',c.fetched_utc
FROM tmdb_api_response_cache c,json_each(c.response_json,'$.credits.crew') j
WHERE c.request_path GLOB '/tv/*/season/*/episode/*?append_to_response=external_ids,credits';

-- Keep the legacy TMDB indexes correct while housekeeping SQL transitions to
-- the common contract. These rows are rebuilt, not fetched.
DELETE FROM tmdb_credit_observation WHERE source_entity_type='episode';
DELETE FROM tmdb_credit WHERE production_type='episode';
INSERT OR REPLACE INTO tmdb_credit_observation
SELECT source_entity_type,source_provider_id,person_provider_id,production_provider_id,production_type,
       credit_id,credit_kind,job_or_character,department,episode_count,NULL,NULL,observed_utc
FROM provider_credit_observation WHERE provider='tmdb' AND production_type='episode';
INSERT OR REPLACE INTO tmdb_credit
SELECT person_provider_id,production_provider_id,production_type,credit_id,credit_kind,
       job_or_character,department,episode_count,NULL,NULL
FROM provider_credit_observation WHERE provider='tmdb' AND production_type='episode';

CREATE TRIGGER tr_tmdb_credit_unified_insert AFTER INSERT ON tmdb_credit_observation BEGIN
  INSERT OR REPLACE INTO provider_credit_observation VALUES(
    'tmdb',new.source_entity_type,new.source_tmdb_id,new.person_tmdb_id,new.production_tmdb_id,new.production_type,
    new.credit_id,new.credit_kind,coalesce(new.job_or_character,''),new.department,new.episode_count,
    CASE WHEN new.production_type IN('episode','movie') THEN 'exact-media' WHEN new.production_type='series' THEN 'broader-series' ELSE 'aggregate-unmapped' END,
    NULL,'tmdb-live-normalization',new.observed_utc);
END;
CREATE TRIGGER tr_tmdb_credit_unified_delete AFTER DELETE ON tmdb_credit_observation BEGIN
  DELETE FROM provider_credit_observation WHERE provider='tmdb' AND source_entity_type=old.source_entity_type AND source_provider_id=old.source_tmdb_id AND person_provider_id=old.person_tmdb_id AND production_provider_id=old.production_tmdb_id AND production_type=old.production_type AND credit_id=old.credit_id AND credit_kind=old.credit_kind;
END;
CREATE TRIGGER tr_tvdb_credit_unified_insert AFTER INSERT ON tvdb_credit_observation BEGIN
  INSERT OR REPLACE INTO provider_credit_observation VALUES(
    'tvdb',new.source_entity_type,new.source_tvdb_id,new.person_tvdb_id,new.production_tvdb_id,new.production_type,
    cast(new.character_id AS text),CASE WHEN new.credit_type IN('Actor','Guest Star') THEN 'cast' ELSE 'crew' END,
    coalesce(new.role_name,''),new.credit_type,NULL,
    CASE WHEN new.production_type IN('episode','movie') THEN 'exact-media' WHEN new.production_type='series' THEN 'broader-series' ELSE 'aggregate-unmapped' END,
    NULL,'tvdb-live-normalization',new.observed_utc);
END;
CREATE TRIGGER tr_tvdb_credit_unified_delete AFTER DELETE ON tvdb_credit_observation BEGIN
  DELETE FROM provider_credit_observation WHERE provider='tvdb' AND source_entity_type=old.source_entity_type AND source_provider_id=old.source_tvdb_id AND person_provider_id=old.person_tvdb_id AND production_provider_id=old.production_tvdb_id AND production_type=old.production_type AND credit_id=cast(old.character_id AS text) AND credit_kind=CASE WHEN old.credit_type IN('Actor','Guest Star') THEN 'cast' ELSE 'crew' END;
END;

CREATE INDEX ix_provider_credit_production ON provider_credit_observation(provider,production_type,production_provider_id,person_provider_id);
CREATE INDEX ix_provider_credit_person ON provider_credit_observation(provider,person_provider_id,production_type,production_provider_id);
CREATE INDEX ix_provider_credit_source ON provider_credit_observation(provider,source_entity_type,source_provider_id);

CREATE TABLE provider_production_evidence(
    provider TEXT NOT NULL CHECK(provider IN ('tmdb','tvdb')),
    production_type TEXT NOT NULL,
    production_provider_id TEXT NOT NULL,
    component TEXT NOT NULL,
    acquisition_status TEXT NOT NULL CHECK(acquisition_status IN ('complete','unresolved','unavailable','failed')),
    source_endpoint TEXT,
    raw_credit_count INTEGER NOT NULL DEFAULT 0,
    normalized_credit_count INTEGER NOT NULL DEFAULT 0,
    observed_utc TEXT NOT NULL,
    PRIMARY KEY(provider,production_type,production_provider_id,component)
) WITHOUT ROWID;
CREATE INDEX ix_provider_evidence_status ON provider_production_evidence(provider,production_type,acquisition_status,production_provider_id);

INSERT OR REPLACE INTO provider_production_evidence
SELECT 'tmdb','episode',cast(json_extract(c.response_json,'$.id') AS text),'screen-credits','complete',c.request_path,
       coalesce(json_array_length(json_extract(c.response_json,'$.credits.cast')),0)+coalesce(json_array_length(json_extract(c.response_json,'$.guest_stars')),0),
       (SELECT count(*) FROM provider_credit_observation p WHERE p.provider='tmdb' AND p.production_type='episode' AND p.production_provider_id=cast(json_extract(c.response_json,'$.id') AS text) AND p.credit_kind='cast'),c.fetched_utc
FROM tmdb_api_response_cache c WHERE c.request_path GLOB '/tv/*/season/*/episode/*?append_to_response=external_ids,credits';

INSERT OR REPLACE INTO provider_production_evidence
SELECT 'tvdb','episode',e.tvdb_id,'screen-credits','complete','tvdb-entity:episode/'||e.tvdb_id,
       (SELECT count(*) FROM json_each(e.raw_json,'$.characters') j WHERE lower(trim(json_extract(j.value,'$.peopleType'))) IN('actor','guest star','director','writer','screenplay','producer','executive producer','creator','showrunner')),
       (SELECT count(DISTINCT p.person_provider_id||'|'||p.credit_kind||'|'||p.credit_id) FROM provider_credit_observation p WHERE p.provider='tvdb' AND p.production_type='episode' AND p.production_provider_id=e.tvdb_id),e.fetched_utc
FROM tvdb_entity e WHERE e.entity_type='episode' AND e.raw_json IS NOT NULL;

CREATE VIEW provider_normalization_mismatch AS
SELECT provider,production_type,production_provider_id,component,acquisition_status,source_endpoint,
       raw_credit_count,normalized_credit_count,raw_credit_count-normalized_credit_count AS missing_credit_count,observed_utc
FROM provider_production_evidence WHERE raw_credit_count<>normalized_credit_count;

CREATE VIEW tmdb_housekeeping_credit AS
SELECT source_entity_type,source_provider_id source_tmdb_id,person_provider_id person_tmdb_id,
       production_provider_id production_tmdb_id,production_type,credit_id,credit_kind,
       job_or_character,department,episode_count,NULL production_name,NULL first_date,observed_utc
FROM provider_credit_observation WHERE provider='tmdb';
CREATE VIEW tvdb_housekeeping_credit AS
SELECT source_entity_type,source_provider_id source_tvdb_id,person_provider_id person_tvdb_id,
       production_provider_id production_tvdb_id,production_type,cast(credit_id AS integer) character_id,
       '' episode_tvdb_id,'' person_name,job_or_character role_name,department credit_type,0 sort_order,0 is_featured,observed_utc
FROM provider_credit_observation WHERE provider='tvdb';

CREATE VIEW emby_person_media_provider_support AS
SELECT 'tmdb' provider,p.emby_id person_emby_id,er.media_emby_id,p.tmdb_id person_provider_id,
       coalesce(m.tmdb_id,mr.resolved_tmdb_id) production_provider_id,m.item_type production_type,
       CASE WHEN p.tmdb_id IS NULL OR coalesce(m.tmdb_id,mr.resolved_tmdb_id) IS NULL THEN 'unresolved'
            WHEN EXISTS(SELECT 1 FROM provider_credit_observation c WHERE c.provider='tmdb' AND c.person_provider_id=p.tmdb_id AND c.production_provider_id=coalesce(m.tmdb_id,mr.resolved_tmdb_id) AND c.production_type=m.item_type) THEN 'supported-exact'
            WHEN EXISTS(SELECT 1 FROM provider_production_evidence e WHERE e.provider='tmdb' AND e.production_provider_id=coalesce(m.tmdb_id,mr.resolved_tmdb_id) AND e.production_type=m.item_type AND e.acquisition_status='complete' AND e.raw_credit_count=e.normalized_credit_count) THEN 'not-present'
            ELSE 'unresolved' END evidence_state
FROM emby_item p JOIN emby_relationship er ON er.person_emby_id=p.emby_id JOIN emby_item m ON m.emby_id=er.media_emby_id LEFT JOIN tmdb_item_resolution mr ON mr.emby_id=m.emby_id WHERE p.item_type='person'
UNION ALL
SELECT 'tvdb',p.emby_id,er.media_emby_id,p.tvdb_id,coalesce(m.tvdb_id,mr.resolved_tvdb_id),m.item_type,
       CASE WHEN p.tvdb_id IS NULL OR coalesce(m.tvdb_id,mr.resolved_tvdb_id) IS NULL THEN 'unresolved'
            WHEN EXISTS(SELECT 1 FROM provider_credit_observation c WHERE c.provider='tvdb' AND c.person_provider_id=p.tvdb_id AND c.production_provider_id=coalesce(m.tvdb_id,mr.resolved_tvdb_id) AND c.production_type=m.item_type) THEN 'supported-exact'
            WHEN EXISTS(SELECT 1 FROM provider_production_evidence e WHERE e.provider='tvdb' AND e.production_provider_id=coalesce(m.tvdb_id,mr.resolved_tvdb_id) AND e.production_type=m.item_type AND e.acquisition_status='complete' AND e.raw_credit_count=e.normalized_credit_count) THEN 'not-present'
            ELSE 'unresolved' END
FROM emby_item p JOIN emby_relationship er ON er.person_emby_id=p.emby_id JOIN emby_item m ON m.emby_id=er.media_emby_id LEFT JOIN item_resolution mr ON mr.emby_id=m.emby_id WHERE p.item_type='person';

CREATE VIEW emby_person_media_provider_mismatch AS
SELECT person_emby_id,media_emby_id,
       max(CASE WHEN provider='tmdb' THEN evidence_state END) tmdb_state,
       max(CASE WHEN provider='tvdb' THEN evidence_state END) tvdb_state
FROM emby_person_media_provider_support GROUP BY person_emby_id,media_emby_id
HAVING coalesce(max(CASE WHEN provider='tmdb' THEN evidence_state END),'unresolved')<>
       coalesce(max(CASE WHEN provider='tvdb' THEN evidence_state END),'unresolved');

-- Remove every historical housekeeping experiment while retaining baseline
-- truth, provider observations, caches, response archives and acquisition logs.
CREATE TEMP TABLE purge_housekeeping_runs(run_id INTEGER PRIMARY KEY,derived_truth_id INTEGER);
INSERT INTO purge_housekeeping_runs SELECT run_id,derived_truth_id FROM housekeeping_run;
DELETE FROM truth_relationship_delta WHERE truth_id IN(SELECT derived_truth_id FROM purge_housekeeping_runs WHERE derived_truth_id IS NOT NULL);
DELETE FROM truth_identity_delta WHERE truth_id IN(SELECT derived_truth_id FROM purge_housekeeping_runs WHERE derived_truth_id IS NOT NULL);
DELETE FROM truth_entity_delta WHERE truth_id IN(SELECT derived_truth_id FROM purge_housekeeping_runs WHERE derived_truth_id IS NOT NULL);
DELETE FROM housekeeping_recommendation;
DELETE FROM housekeeping_signal;
DELETE FROM housekeeping_run;
DELETE FROM experiment_metric WHERE run_id IN(SELECT run_id FROM purge_housekeeping_runs);
DELETE FROM experiment_prediction WHERE run_id IN(SELECT run_id FROM purge_housekeeping_runs);
DELETE FROM resolution_proposal WHERE run_id IN(SELECT run_id FROM purge_housekeeping_runs);
DELETE FROM experiment_run WHERE run_id IN(SELECT run_id FROM purge_housekeeping_runs);
DELETE FROM truth WHERE truth_id IN(SELECT derived_truth_id FROM purge_housekeeping_runs WHERE derived_truth_id IS NOT NULL);
DELETE FROM sqlite_sequence WHERE name IN('housekeeping_recommendation','housekeeping_signal','experiment_run');

INSERT INTO archive_schema_migration(version,applied_utc,description)
VALUES(7,strftime('%Y-%m-%dT%H:%M:%fZ','now'),'Materialized symmetric provider evidence, normalization completeness/mismatch views, TMDB guest-star rebuild, and housekeeping run reset');

COMMIT;
