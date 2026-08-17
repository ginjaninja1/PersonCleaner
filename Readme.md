# PersonCleaner provider archive and entity-resolution truth

PersonCleaner is an Emby 4.10 scheduled-task plugin. It snapshots the in-scope Emby graph, archives
TVDB v4 and TMDB observations in SQLite, and maintains versioned write-once truths for later entity
resolution. It does not change live Emby names, provider IDs, people, or relationships.

The database is `personcleaner-archive.db` under Emby's data directory. The plugin uses the SQLite
assemblies shipped with Emby and does not deploy another SQLite native library.

## Scheduled task

Configure both API keys in the plugin settings and run:

**PersonCleaner - Update Emby and provider archive**

Each run:

1. Enumerates in-scope series, movies, regular episodes, and their scoped people once.
2. Records changed Emby entity and credit-relationship observations.
3. Imports unseen Emby entities and relationships into every draft truth exactly once.
4. Runs configured TVDB and TMDB pipelines concurrently under separate request limits.
5. Records a durable outcome for every Emby/provider work item.

One missing provider key does not prevent the snapshot or the other provider from completing. With
neither key, the task still updates Emby observations and truth coverage. Successful responses are
cached for 30 days by default; failures have a shorter retry time. Cancellation retains all committed
observations, work outcomes, raw responses, and normalized data.

## Scope and identity policy

- Media: series, movies, and episodes from season 1 onwards.
- People: Actor, Guest Star, Director, Writer, and Producer relationships on in-scope media.
- TVDB acquisition uses an Emby TVDB ID or a previously accepted archived resolution. It does not
  infer and accept a new TVDB identity during extraction.
- TMDB acquisition uses direct IDs, episode coordinates, and unique IMDb `/find` results. Ambiguous
  results remain evidence.
- Provider 404s, missing identities, candidates, and contradictions are retained; Emby is not changed.
- Resolution experiments and proposals remain separate from provider acquisition.

The former TVDB/TMDB preview and full exports, ID probe, resolver evaluation, and diagnostic probe
tasks are retired from scheduled-task registration. Their historical database rows and source remain
available for provenance.

## Main tables

- `emby_item`, `emby_relationship`: rebuildable current Emby projections.
- `emby_observation`, `emby_relationship_observation`: append-only changed observations.
- `provider_update_run`, `provider_work`: unified task and per-provider work position.
- `tvdb_entity`, `remote_id`, `tvdb_alias`, `tvdb_credit_observation`: TVDB indexes.
- `tmdb_entity`, `tmdb_external_id`, `tmdb_alias`, `tmdb_credit_observation`: TMDB indexes.
- `item_resolution`, `tmdb_item_resolution`: current provider identity answers.
- `truth`, `truth_entity`, `truth_external_identity`, `truth_entity_lineage`, and
  `truth_relationship`: versioned desired Emby graphs.

Provider equivalences are in `PROVIDER_SCHEMA.md`; truth semantics are in
`ENTITY_RESOLUTION_SCHEMA.md`; current operational notes are in `PROJECT_HANDOFF.md`.
Use `PROVIDER_UPDATE_VERIFY.sql` for the unified run audit; the provider-specific verification files
remain useful for historical and detailed archive inspection.

## Useful SQL

```sql
SELECT * FROM provider_update_run ORDER BY run_id DESC LIMIT 1;

SELECT provider,entity_type,state,outcome,COUNT(*) AS items
FROM provider_work
WHERE last_run_id=(SELECT MAX(run_id) FROM provider_update_run)
GROUP BY provider,entity_type,state,outcome;

SELECT * FROM provider_identity_signals WHERE emby_id=173844;
SELECT * FROM truth_relationship WHERE truth_id=1 LIMIT 100;
```

## Build

```powershell
dotnet build src/PersonCleaner/PersonCleaner.csproj -c Release --no-restore -p:SkipPluginDeploy=true
```
