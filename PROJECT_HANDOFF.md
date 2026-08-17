# PersonCleaner project handoff

Last updated: 2026-08-17

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
