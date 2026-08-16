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
- TVDB v4 supports reverse lookup by both IMDb ID and TMDB ID through `GET /search/remoteid/{external_id}`. The response can contain multiple entity types, so consumers must filter by expected type and validate name/year or stronger evidence; a bare numeric ID is not globally unique.
- Availability is record-specific. Some people have IMDb, TMDB, Wikidata and other IDs; plausible exact-name people may have none.
- Store TVDB's literal `id`, numeric `type` and `sourceName`; do not infer meaning solely from the numeric type.
- Current archive statistics showed substantial but incomplete IMDb, TMDB and Wikidata coverage for TVDB people.

### Recovery opportunity demonstrated by the 2026-08-15 failed export

The completed export reported 50 distinct `NotFound` failures. A live TVDB reverse-lookup pass against every available Emby IMDb and TMDB ID demonstrated that a 404 from the stored TVDB entity ID does not necessarily mean the identity is absent from TVDB. Treat reverse-lookup results as persisted evidence/signals, not as automatic mutations.

Eleven items had a strong same-type replacement whose current TVDB entity endpoint was also verified:

| Emby ID | Type | Emby identity | Stale TVDB ID | Current TVDB ID | Evidence |
|---:|---|---|---:|---:|---|
| 66663 | series | Stephen Hawking and the Theory of Everything | 31657 | 81719 | Exact TMDB `213057`, compatible name/year |
| 36042 | person | Renée Taylor | 7910281 | 343928 | IMDb `nm0853041` and TMDB `56105` converge |
| 57995 | person | Nicholas Sidi | 391163 | 7894971 | Exact IMDb `nm0796594`; TVDB name `Nick Sidi` |
| 66191 | person | Nae Yuuki | none | 8302286 | Exact TMDB `142869`; TVDB name `Nae` |
| 106895 | person | Dermot O'Leary | 7866725 | 9120093 | IMDb `nm0641581` and TMDB `1216116` converge |
| 133149 | person | Charlie Adler | 276646 | 263034 | IMDb `nm0012121` and TMDB `81178` converge; TVDB name `Charles Adler` |
| 143662 | person | Luo Jin | 472247 | 8545511 | IMDb `nm2950480` and TMDB `1115666` converge; TVDB name order is `Jin Luo` |
| 167491 | person | Ingrid Unnur Giæver | 645870 | 8009040 | Exact TMDB `1908557`; compatible shortened name |
| 168170 | person | Zehra Leverman | 407808 | 8347913 | Exact IMDb `nm0505358`; TVDB spelling `Zerha Leverman` |
| 177774 | person | Andrew Lauer | 339403 | 7967760 | Exact IMDb `nm0490774`; TVDB name `Andy Lauer` |
| 285649 | person | Trinity Bliss | 8174490 | 9098746 | Exact TMDB `1895788`; TVDB full name `Trinity Jo-Li Bliss` |

Three more items had a strong external-ID match under a different TVDB entity type. These are valuable classification signals but must not be applied as same-type TVDB-ID replacements:

| Emby ID | Emby type | Identity | Stale TVDB ID | Live TVDB identity |
|---:|---|---|---:|---|
| 47264 | series | Tales from the Far Side | 122781 | movie 117283 via IMDb `tt0109873` |
| 294584 | movie | Maggie Simpson in "The Longest Daycare" | 354146 | episode 4531854 via IMDb `tt2175842` |
| 398581 | episode | My Scientology Movie | 6848718 | movie 11563 via IMDb `tt5111874` |

Ten records had external IDs but no usable TVDB crosswalk: Das Boot (1985), Bernard Zilinskas, James V. Scott, Mark Allan Staubach, Bohdan Poraj, Raoul Max Trujillo, Jerrod Carmichael, Aaron Schwartz, Jim J. Poslof and Marvin Campbell. Some numeric TMDB searches returned unrelated movies or series; those collisions reinforce the requirement to retain the source namespace and expected entity type. These ten are candidates for the planned direct-TMDB capture: TMDB identity, aliases, external IDs, credits and production crosswalks may provide additional evidence even where TVDB's reverse index is empty.

The remaining 26 failed records had no IMDb or TMDB ID available for this reverse-lookup pass.

Stephen Hawking is an important edge case. TVDB series 81719 exists and is returned by direct lookup and TMDB `213057`, but an exact TVDB name search returns zero results, so it is effectively unindexed/orphaned from UI search. The stale Emby value `31657` is actually season 1's TVDB season ID, not the series ID. This proves that direct entity lookup, name search and external-ID reverse lookup are distinct evidence channels and their disagreement must be preserved. Do not reject an otherwise verified entity solely because TVDB UI/name search cannot surface it.

Implementation opportunity:

- For any direct TVDB 404, query every available IMDb and TMDB ID through TVDB remote-ID search before final classification.
- Persist the raw remote-search response, source namespace, requested external ID, returned entity type, candidate TVDB ID and validation result.
- Record same-type replacements and cross-type matches as evidence. Do not silently overwrite Emby provider IDs.
- Require type compatibility plus corroboration. IMDb/TMDB convergence is stronger than one external ID; name/year, aliases, production structure and credit overlap can strengthen or contradict it.
- Feed empty or ambiguous TVDB crosswalks into the later direct-TMDB evidence stage rather than treating them as exhausted identities.
- Evaluate this recovery path on known-good withheld records before defining confidence thresholds or permitting configurable auto-application.

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

## Materialized evidence and cleansing architecture

Adopt this design principle for the next phase:

> Any fact used in an identity decision should first be materialized in the plugin SQLite database, with its source, observation/fetch time, expiry or revalidation time, and a reference to the raw evidence where applicable.

This applies to inexpensive local Emby observations as well as paid/remote TVDB and TMDB responses. The plugin database should contain enough evidence to reproduce and audit a decision without querying the live Emby library during analysis. Emby-derived observations still require a TTL or equivalent revalidation timestamp so duplicated local facts do not silently become stale. A short TTL or refresh at the start of an analysis task is appropriate; unchanged content may be fingerprinted to avoid unnecessary history rows.

Do not design the final observation/assertion schema speculatively. First implement direct TMDB capture and run a small Emby/TMDB evidence probe, then use the real payloads and available Emby relationship fields to decide:

- current-state rows plus history versus append-only observations;
- field-level provenance and evidence references;
- TTLs/revalidation rules for each source;
- proposed versus applied Emby mutations;
- normalized identity assertions and contradiction handling.

Add the resulting schema through explicit numbered transactional migrations, rather than continuing indefinitely with ad hoc `CREATE TABLE IF NOT EXISTS` initialization.

One concrete early addition may be justified alongside the probe: persist the complete local Emby person-to-production observation, including person Emby ID, media Emby ID, media type, production provider IDs, person type, role/character, observation time and revalidation/expiry time. The current `person_local_production` table records the TVDB production link but not the local media ID or role, losing useful evidence used to compare Emby and provider credits.

### Identity-cleansing pipeline

Keep canonical-name cleansing separate from foreign-provider-ID enrichment. The intended order is:

1. Snapshot and validate the person's existing Emby name, provider IDs and production/role relationships.
2. Ask the provider owning each existing ID whether the entity still exists and record its current canonical name, aliases and external IDs.
3. Resolve the canonical identity and propose or apply an auditable name cleansing decision.
4. Resolve missing foreign provider IDs using the cleansed identity and already validated IDs.
5. Check production and role consistency as corroboration and, especially, as contradiction detection.

Do not let a weak foreign-provider name result drive the initial rename. Proposed and applied Emby mutations must remain distinguishable and auditable.

### Coco Sumner / Eliot Sumner design case

Emby person 173844 is stored as Coco Sumner with IMDb `nm1091393`, TMDB `1586573`, and a local relationship to RIPLEY (Emby 338517, TVDB series 372727). TVDB name search cannot connect `Coco Sumner` to `Eliot Sumner`: TVDB person 8192078 only supplies the alias `Eliot Paulina Sumner`, not `Coco Sumner`.

The identity is nevertheless strongly corroborated by independent routes available to the proposed pipeline:

- TVDB remote lookup of IMDb `nm1091393` uniquely returns Eliot Sumner, TVDB person 8192078.
- TVDB remote lookup of TMDB `1586573` returns that same TVDB person.
- The TVDB extended person record owns both external IDs.
- RIPLEY credits TVDB person 8192078 as Freddie Miles.
- Direct TMDB capture is expected to supply the missing canonical-name/alias explanation and must be archived rather than assumed.

This case currently remains rejected because remote lookups are deliberately non-authoritative and the names conflict. Use it as a primary probe for the new pipeline. A production link alone is very weak evidence and a matching role/character is only low evidence; exact external-ID agreement is much stronger. Multiple agreeing IDs, a provider-supported alias/canonical-name transition, and consistent production/role evidence together are suitable for very-high-confidence or configurable auto-accept handling, while any contradiction must force review. Call this very high confidence rather than an absolute guarantee because providers can share upstream errors.

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

## Direct TMDB implementation (2026-08-15)

- Added a persisted, source-empty `TmdbApiKey` configuration field and Generic UI input.
- Added independent preview/full resumable tasks: `TmdbArchivePreview` and `TmdbArchiveFull`.
- Direct TMDB capture covers shows, movies, people, and regular episodes. Episodes use their parent
  TMDB series ID plus season/episode coordinates, preserving the returned TMDB episode ID.
- Missing direct TMDB IDs can be queried through `/find/{imdb_id}`. All typed candidates are retained;
  only a unique typed result is fetched, and ambiguity remains explicit evidence.
- TMDB entities, raw payloads, literal external IDs, aliases, credits, response history/cache, failures,
  candidates and per-Emby provenance live in separate `tmdb_*` tables in `tvdb-archive.db`.
- `provider_identity_signals` provides a first side-by-side answer for TMDB versus TVDB by Emby ID.
- `TMDB_ARCHIVE_VERIFY.sql` contains inspection queries, including the Coco/Eliot Sumner case.
- This capture does not modify Emby metadata or feed TMDB evidence into TVDB confidence thresholds.
- Added normalized `tvdb_alias` and `tvdb_credit_observation` tables to mirror functionally equivalent
  TMDB concepts. Rebuild/backfill must be an explicit scheduled operation over cached responses,
  never repository initialization; scanning the multi-GB cache during task construction blocks Emby
  startup. Legacy normalized credits may be explicitly marked `legacy-normalized` where their
  original endpoint route can no longer be reconstructed.
- Added provider-neutral `provider_entity`, `provider_external_id`, `provider_alias`, and
  `provider_credit_observation` views. See `PROVIDER_SCHEMA.md` for field mappings and intentional
  differences that preserve each API's own semantics.
- Removed the ineffective TMDB person `alternative_names` append while retaining cache-key
  compatibility: unexpired responses stored under the old request path are reused, and the leaner
  path is fetched only when refresh is due.
