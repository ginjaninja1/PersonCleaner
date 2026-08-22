# Entity-resolution truth model

`personcleaner-archive.db` deliberately separates observations, provider evidence, and desired truth.

## Emby observations

The unified update task snapshots Emby before provider requests.

- `emby_item` and `emby_relationship` are current-state projections.
- `emby_observation` receives a row only when an Emby entity representation changes.
- `emby_relationship_observation` receives a row when a scoped person/media/role relationship is
  first seen or changes.

The relationship key includes media ID, person ID, person type, and role/character. This retains local
production and role evidence for provider-credit comparison without querying live Emby.

## Write-once truths

`truth` names a complete desired Emby graph. Truth 1 is the baseline seeded from Emby.

An Emby entity is imported into a draft truth only when its source Emby ID has never contributed to
that truth. Its initial name, year, and Emby/TVDB/TMDB/IMDb identities are write-once. Later Emby
changes create observations but cannot overwrite truth metadata or identities.

Relationships follow the same rule. They are inserted after both endpoint entities are seeded, and
duplicate snapshots cannot rewrite them. Frozen truths receive neither new entities nor relationships.

The graph is stored in `truth_entity`, `truth_external_identity`, `truth_entity_lineage`, and
`truth_relationship`. Lineage is many-to-many: multiple sources for one truth entity represent a
merge, while one source contributing to multiple truth entities represents a split. The
`truth_merge_candidates` and `truth_split_candidates` views expose those cases.

## Provider evidence and algorithms

TVDB and TMDB caches, entities, aliases, external IDs, credits, candidates, and resolutions are
observations. Acquisition may refresh them but does not apply them to a truth or live Emby.

Algorithms operate against a truth and observation cutoff. `experiment_run`, `experiment_prediction`,
and `experiment_metric` retain immutable results. `resolution_proposal` stores proposed graph changes;
review and creation of a derived truth are separate operations.

Person-housekeeping algorithm version `normalized-v10` uses the migration-5 tables
`housekeeping_run`, `housekeeping_signal`, and `housekeeping_recommendation`. Operational fields are
normalized and indexed rather than hidden in JSON. Runs commit bounded phases and persist progress,
heartbeat, cancellation, and failure state. Derived housekeeping truths are parent-relative deltas in
`truth_entity_delta`, `truth_identity_delta`, and `truth_relationship_delta`; the 1.28-million-row
baseline graph is never copied for an experiment.

Migration 7 adds the materialized `provider_credit_observation` contract and
`provider_production_evidence` completeness ledger. Provider-native tables and raw response caches
remain authoritative acquisition records; the common tables are rebuildable analytical indexes.
An exact episode can supply negative evidence only when acquisition is complete and its raw and
normalized screen-credit counts agree. `provider_normalization_mismatch` exposes exceptions.

## Migrations and database filename

`archive_schema_migration` records offline migrations. Migration 6 adds
`provider_identity_issue`, a persistent reviewed ledger for provider-side person splits,
conflations, bad credits, and bad names. Run-specific candidate/media observations remain in
`housekeeping_signal`; recommendations do not replace or erase those observations.

1. Append-only Emby entity observations and versioned truth graph.
2. Unified provider work/run state plus relationship observations and write-once relationship seeding.
3. Live running-work state and exact per-run provider response-cache counters.
4. Source-first lineage index supporting set-based relationship seeding.
5. Normalized housekeeping evidence/recommendations, resumable run state, and delta truths.
6. Persistent reviewed provider identity issues, separate from library recommendations.
7. Materialized symmetric provider credits and completeness/mismatch views; rebuild of TMDB exact
   episode cast from preserved response JSON; reset of historical housekeeping runs and derived
   delta truths while preserving baseline truth, provider observations and API response archives.
8. Materialized normalized evidence children for each actionable housekeeping recommendation plus
   acceptance-path and separate identity, relationship and operation confidence. These compact rows
   are indexed for the expandable review UI and avoid page-time aggregation of the full signal ledger.
9. Normalized ordered recommendation-action children. Merge and split parents absorb dependent
   rename, identity and relationship operations so one operator decision cannot produce competing
   top-level rows or independently applicable dependent deltas.

The plugin prefers `personcleaner-archive.db`. If only historic `tvdb-archive.db` exists, it continues
using that file. It never moves a live database or WAL/SHM sidecars during construction.
