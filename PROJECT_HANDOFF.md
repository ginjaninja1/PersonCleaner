# PersonCleaner project handoff

Last updated: 2026-08-21

## Current workflow

The only production archive task is `PersonCleanerProviderUpdate`, displayed as
**PersonCleaner - Update Emby and provider archive**. It replaces the former TVDB/TMDB preview and
full exports, TVDB ID probe, resolver evaluation, and diagnostics. Historical task tables and evidence
remain intact.

The task enumerates Emby once, stores changed entity and credit relationships, seeds unseen objects
into draft truths once, creates `provider_work`, and runs TVDB and TMDB worker pools concurrently with
independent throttles. One missing key does not block the other pipeline; no keys still permits the
Emby snapshot.

## Scope and safety

- Series, movies, and season-1+ episodes; specials remain excluded.
- Actor, Guest Star, Director, Writer, and Producer relationships.
- Raw successful responses are retained and replaced responses archived.
- TVDB series episode feeds remain the bulk episode/cast route.
- A direct or previously accepted TVDB identity may be fetched, but acquisition accepts no new TVDB
  inference.
- TMDB uses direct IDs, episode coordinates, and unique typed IMDb `/find` evidence.
- Failures, 404s, ambiguity, and contradictions remain evidence.
- Live Emby and accepted truths are never updated by provider acquisition.

Merge, split, identity correction, name cleansing, and relationship movement belong in
`resolution_proposal` and derived-truth workflows.

## Storage

- Database: `<Emby DataPath>\personcleaner-archive.db`.
- Historic `tvdb-archive.db` is used if it is the only existing file.
- SQLite uses Emby's bundled assemblies and WAL mode.
- Migration 2 adds `provider_update_run`, `provider_work`, `emby_relationship`, and
  `emby_relationship_observation`, and seeds `truth_relationship` from complete local credits.
- Migration 3 adds exact per-run provider response-cache counters and live `running` work state.
- Migration 6 adds persistent provider identity issues. Housekeeping normalized-v6 audits every
  linked Emby person against both providers, queues only evidence gaps for bounded cached HTTP
  acquisition, retains every plausible candidate, writes per-candidate/per-media signals, withholds removals when
  coverage is incomplete or positive evidence exists, and separates Emby merge reviews from
  provider-side person split findings.
- Migration 7 materializes symmetric TMDB/TVDB credit evidence and completeness/mismatch views,
  rebuilds TMDB episode guest credits from preserved cached responses, and removes all historical
  housekeeping runs, recommendations and derived delta truths for a fresh human sniff-test cycle.
  Raw API caches/archives, provider acquisition history, baseline truth and persistent provider
  issues are preserved.
- Migration 7 was applied offline on 2026-08-21. The first clean post-reset housekeeping execution
  is run 1, algorithm `normalized-v10`, completed at 2026-08-21T13:43:55Z with derived truth 15,
  737,384 signals and 2,118 human-review recommendations. This is the current sniff-test baseline;
  pre-migration run numbers are no longer present in the database.
- The materialized evidence baseline contains 6,568,278 unified credit observations and 96,319
  exact-episode completeness records. TVDB episode 11763808 is the sole recorded normalization
  mismatch (raw 9, normalized 11) and therefore cannot provide negative absence evidence.
- Cross-provider split detection treats conflicting exact external-ID clusters as corroboration of
  a direct media partition. The stored David Cameron case is Emby 160974: TMDB 1220273/IMDb
  nm2090098 owns the Brexit movie; TVDB 9148336 -> TMDB 1235383/IMDb nm0131538 owns three episodes.
- TMDB episode acquisition must merge root `guest_stars` with appended `credits.cast`. Do not treat
  a failed TVDB episode ID lookup through TMDB `/find` as absence when the exact TMDB
  series/season/episode route remains available.
- Evidence acquisition uses frozen 1,000-person development cohorts reconstructed from the earliest
  provider person-audit checkpoints. Cached completion does not advance the cohort. Candidate-person
  404s are negatively cached and isolated so one stale provider ID cannot abort the task.

Legacy `run_state`, `tmdb_run_state`, `export_scope`, `id_probe`, and `resolution_evaluation` tables
are retained for provenance.

## Build and runtime verification

```powershell
dotnet build src/PersonCleaner/PersonCleaner.csproj -c Release --no-restore -p:SkipPluginDeploy=true
```

After installing and restarting Emby, confirm only the unified archive task is registered. Run it,
cancel once during provider work, rerun, and inspect:

```sql
SELECT * FROM provider_update_run ORDER BY run_id DESC;

SELECT provider,entity_type,state,outcome,COUNT(*)
FROM provider_work
WHERE last_run_id=(SELECT MAX(run_id) FROM provider_update_run)
GROUP BY provider,entity_type,state,outcome;

SELECT COUNT(*) FROM emby_observation;
SELECT COUNT(*) FROM emby_relationship_observation;
SELECT COUNT(*) FROM truth_relationship WHERE truth_id=1;
```

## Retained design cases

- Das Boot (1985): stale TVDB identity with useful TMDB/IMDb evidence.
- Cross-type matches such as Tales from the Far Side: classification evidence, not automatic fixes.
- Stephen Hawking and the Theory of Everything: direct/reverse lookup can work when name search fails.
- Coco/Eliot Sumner: converging IDs and role evidence explain a name transition but still require a
  reviewed proposal.
