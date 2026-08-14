# TVDB Archive / Entity Resolution Project Handoff

Last updated: 2026-08-14

## Goal

Build an Emby 4.10 plugin that permanently archives TVDB identity data in SQLite while TVDB API access is available. The archive must support later entity-resolution research and operation without an active key. It must distinguish data obtained from an Emby-supplied TVDB ID from IDs inferred by the plugin.

The next development area is TMDB evidence capture and cross-source entity resolution, especially for uncertain people.

## Workspace and build

- Workspace: `C:\PluginDev\PersonCleaner`
- Project: `src\PersonCleaner\PersonCleaner.csproj`
- Release DLL: `src\PersonCleaner\bin\Release\netstandard2.0\PersonCleaner.dll`
- Build:
  `dotnet build 'src\PersonCleaner\PersonCleaner.csproj' -c Release --no-restore -p:SkipPluginDeploy=true`
- Current build succeeds. There is one pre-existing CS4014 warning in `PersonCleanerDiagnosticsTask.cs`.
- Preserve the user's existing diagnostics task and other unrelated local changes.
- Decompiled Emby reference source originally supplied as `A:\OneDrive\Desktop\Decompile 4.10.0.22.zip`.

## Runtime and database

- Plugin display name: TVDB Archive.
- SQLite database: `<Emby DataPath>\tvdb-archive.db`.
- User's current path: `C:\Users\Nicholas Bird\AppData\Roaming\Emby-Server\programdata\data\tvdb-archive.db`.
- SQLite uses Emby's bundled `SQLitePCL.pretty` / `SQLitePCLRawEx.core`; do not deploy a competing SQLite native library.
- WAL mode permits DB Browser for SQLite to inspect the database read-only while the Emby task runs. Refresh/rerun queries to see new commits.
- Schema initialization is additive and idempotent. For more complex future changes, convert `schema_info` into explicit numbered transactional migrations.

## Scheduled tasks

- `TvdbArchiveIdProbe`: TVDB/IMDb round-trip probes for series, episode, movie and person.
- `TvdbResolutionEvaluation`: withholds known-good Emby TVDB IDs to evaluate resolver precision.
- `TvdbArchivePreview`: small direct and unidentified sample.
- `TvdbArchiveFull`: full stoppable/resumable export.

The full task has a genuine persisted checkpoint. On restart it skips completed work and restores accepted inferred parent-series IDs needed for episode resolution.

## Required scope

- Media types: series, movies and episodes.
- Episodes: season 1 onwards only. Season zero/specials are excluded from the item workload and episode cast capture.
- People are gathered through in-scope Emby media relationships.
- Screen-credit whitelist for TVDB capture:
  Actor, Guest Star, Director, Writer, Screenplay, Producer, Executive Producer, Creator, Showrunner.
- Excluded examples: Host, Musical Guest, Composer and generic Crew.
- Raw successful API responses remain in `api_response_cache`; TTL expiry permits refetching but does not delete the stored response.

## Provenance model

- `direct`: Emby supplied a usable TVDB ID.
- `direct-unavailable`: Emby supplied a TVDB ID but TVDB returned 404. Keep the ID and queue human review; never modify Emby automatically.
- `inferred`: no Emby TVDB ID and an accepted resolver match met the configured confidence threshold.
- `rejected`: candidates existed but did not meet acceptance requirements.
- `unresolved`: no candidate was found.
- `failed`: technical processing failure in the export manifest.

Important known `direct-unavailable` examples:

- Emby 34, Das Boot (1985), TVDB 1893.
- Emby 47264, Tales from the Far Side, TVDB 122781.

TVDB no longer exposes Das Boot 1985 as the six-episode miniseries. TMDB still associates it with IMDb `tt0081834` and TVDB `1893`. This is a useful stale-ID test case.

## Main tables and views

- `emby_item`: observed Emby identity and provider IDs.
- `tvdb_entity`: normalized TVDB entity fields plus raw extended JSON.
- `remote_id`: literal TVDB `remoteIds`, preserving ID, numeric type and source name.
- `credit`: scoped screen credits/filmography.
- `api_response_cache`: exact successful TVDB GET response JSON and cache dates.
- `fetch_cache`: fetch outcome, retry date and error history.
- `item_resolution`: accepted/rejected identity result and provenance.
- `resolution_candidate`: ranked candidates with external IDs and TVDB filmography IDs.
- `resolution_evaluation`: known-good evaluation results.
- `export_scope`: persisted full workload split by entity type and whether Emby had a TVDB ID.
- `run_state`: task status/checkpoint.
- Useful views: `export_area_progress`, `resolution_inventory`, `resolved_searchable_media`, `identity_review_queue`, `resolution_evaluation_summary`.
- Verification SQL: `TVDB_ARCHIVE_VERIFY.sql`.

## Export call strategy

- Series extended endpoint archives the series and its literal external IDs.
- Paged series official-episode calls efficiently obtain season-1+ episode records and episode-level cast assignments.
- Those bulk responses do not contain episode `remoteIds`.
- Each accepted Emby episode therefore also receives an episode extended call for its TVDB-held external IDs and raw identity data.
- TVDB people reached through in-scope credits receive extended calls so their remote IDs and filmography can be archived.
- Episode rows discovered by a series feed use insert/update logic that does not overwrite a richer extended episode record.

## Cache, throttle and failures

- Successful raw API response TTL defaults to 30 days and is configurable.
- Failed work is eligible sooner; default retry is 30 minutes.
- Requests are serialized with a configurable minimum interval, currently 250 ms (maximum about four uncached requests/second).
- HTTP 429 and 5xx responses, timeouts and connection failures use bounded exponential retries.
- Ordinary pacing is silent; retry logs contain `TVDB transient response`.
- Entity calls previously used `ContinueWith(t => t.Result...)`, which wrapped 404 errors in `AggregateException`. This was replaced with `async/await`, and existing wrapped NotFound rows are backfilled as `direct-unavailable` during initialization.
- Series 1893's absent official episode feed is separately negatively cached.

## Resolver learnings

- Resolution evidence order currently includes IMDb remote lookup, TMDB remote lookup against TVDB remote search, parent-series/season/episode coordinates, then name/metadata candidates.
- Current `tmdb-remote` means asking TVDB to resolve an existing TMDB ID. It is not yet a direct call to the TMDB API.
- Search entity types must be canonicalized singular/plural to prevent cross-type matches.
- Do not pass an Emby person's `ProductionYear` into TVDB person search. It behaves like birth year and caused valid results such as Mark Burdis to disappear.
- Series matching uses title, year and regular episode count. Episode count is strong disambiguation evidence.
- Battle Line: IMDb points to wrong TVDB 280397 (39 episodes), while the structurally correct TVDB 267139 has the local count of 10; the latter is selected at high confidence.
- Money Heist is a deliberately difficult title/version/episode-count case. Remote consensus is accepted only when IMDb and TMDB converge and no structurally superior fallback exists.
- Exact TVDB production-ID overlap is too weak for people when Emby media IDs do not already crosswalk to the same TVDB productions.

Known people cases for TMDB work:

- Mark Burdis: Emby 10284, IMDb `nm0120986`, TMDB 218375; exact-name TVDB candidate 339216 has no useful remote IDs and zero raw TVDB production-ID overlap.
- Berwick Kaler: Emby 10303, IMDb `nm0435693`, TMDB 118614; TVDB candidate 444826.
- Harmage Singh Kalirai: Emby 10330, IMDb `nm0435877`, TMDB 107725; TVDB candidate 434064.
- Moira Foot: Emby 10130, TMDB 2979580; TVDB candidate 8012483.

These candidates must remain evidence, not silently become authoritative matches.

## TVDB external-ID findings

- TVDB extended records can expose `remoteIds` for series, movies, episodes and people.
- Availability is record-specific. Some people have IMDb, TMDB, Wikidata and other IDs; plausible exact-name people may have none.
- Store TVDB's literal `id`, numeric `type` and `sourceName`; do not infer meaning solely from the numeric type.
- Current archive statistics showed substantial but incomplete IMDb, TMDB and Wikidata coverage for TVDB people.

## Next work: direct TMDB capture and corroboration

The user supplied a TMDB v3 API key during the prior chat. Add a persisted configuration field and UI input; keep the source default empty and do not hardcode it.

Add a distinct TMDB client/cache namespace and raw archive. Candidate endpoints:

- `/3/find/{external_id}?external_source=imdb_id`
- `/3/person/{person_id}`
- `/3/person/{person_id}/external_ids`
- `/3/person/{person_id}/combined_credits`
- `/3/tv/{series_id}/external_ids`
- `/3/tv/{series_id}/season/{season}/episode/{episode}/external_ids`
- `/3/movie/{movie_id}/external_ids`

TMDB supplies person IMDb and Wikidata IDs. Cross-source corroboration should use, in descending strength where available:

1. Exact IMDb agreement.
2. Exact Wikidata agreement.
3. Exact TMDB person ID when TVDB supplies it.
4. Production crosswalks through IMDb/TMDB/Wikidata external IDs.
5. Filmography title, year, media type, season/episode coordinates, role and character.
6. Biographical fields and aliases.
7. Co-credit graph evidence.

Store TMDB evidence and crosswalk results separately from TVDB observations and from the final inference. Keep every candidate and signal auditable. First evaluate the new signals on a subset of known-good Emby TVDB people; do not immediately alter confidence thresholds or auto-accept uncertain people.

TMDB work is an enhancement to rejected/unresolved identity resolution. It must not interrupt or contaminate the ongoing TVDB archive export.

## Current progress interpretation

The full manifest total observed was 187,286 items:

- Series: 1,194 with TVDB ID; 2 without.
- Movies: 3,117 with TVDB ID; 106 without.
- People: 75,845 with TVDB ID; 57,579 without.
- Regular episodes: 49,260 with TVDB ID; 183 without.

Processing order is series, movies, people, episodes. Preview results may make later areas show a few examined items before the full task reaches them. The people area is expected to dominate runtime.

Use:

```sql
SELECT entity_type,id_area,total_items,examined_items,accepted_dumps,
       review_items,failed_items,percent_examined
FROM export_area_progress
WHERE task_key='TvdbArchiveFull'
ORDER BY CASE entity_type WHEN 'series' THEN 1 WHEN 'movie' THEN 2
                          WHEN 'person' THEN 3 ELSE 4 END,id_area;
```

## Collaboration expectations

- Explain data from first principles: distinguish Emby facts, stored TVDB/TMDB responses, and plugin inference.
- Be plain about defects and provenance. Do not obscure behaviour with vague terminology.
- Preserve candidates and contradictory evidence for human review.
- Do not modify provider IDs in Emby as part of this archive/resolution work.
- The user wants working probes and small evaluations before any broad new inference rule is enabled.
