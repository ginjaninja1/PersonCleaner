# Person Housekeeping Algorithm Review Handoff

## Purpose

This document is the focused handoff for reviewing and improving the person-housekeeping signal,
recommendation, and scoring algorithm. It supplements `PROJECT_HANDOFF.md`; it is not an operational
or provider-acquisition handoff.

## Review baseline

- Historical review baseline: `person-housekeeping` version `normalized-v2`.
- Current implementation: `normalized-v6`, anchored on the Emby person and complete linked-media
  evidence, with symmetric evidence-gap TMDB/TVDB candidate acquisition, a per-relationship evidence
  ledger, merge detection for candidates already owned by Emby, and persistent provider issues.
- Review run: housekeeping run `4` (the first Emby-executed run after the normalized redesign).
- Base truth: truth `1` (`Emby baseline`).
- Output: a frozen delta truth referenced by `housekeeping_run.derived_truth_id` for run 4.
- Live Emby and truth 1 were not changed.
- Provider requests: normalized-v6 audits all linked people from the archive, then makes bounded,
  cached requests only for missing, unavailable, duplicated, unsupported, partial, or unresolved
  identity evidence. Cast/media lookup precedes name/external-ID search; this is not an
  unavailable-only pathway. All responses and per-person acquisition checkpoints use the provider
  caches. Historical run 4 remained archive-only.
- Development acquisition is frozen to the original 1,000-person cohort per provider, reconstructed
  from the earliest person-audit checkpoints. Re-running Evaluate Truth must not roll automatically
  into another 1,000 people. TMDB/TVDB person-detail 404s are negative-cached candidate evidence and
  skip only that candidate; they do not fail the task.
- A linked-media-supported TMDB person may supply its archived IMDb ID to TVDB remote-ID candidate
  discovery before loose name search. The derived IMDb provenance is displayed explicitly and does
  not replace TVDB-native media acceptance evidence. Unsupported search-only candidate pairs no
  longer create provider-person-split review rows.
- Person-name compatibility now supports safe normalization, optional quoted/parenthesized nickname
  removal, provider aliases, and an editable non-transitive given-name equivalence list surfaced in
  plugin configuration. Each run snapshots the configured list. Supported provider consensus emits
  one rename row; provider disagreement or an exact healthy-provider anchor retains the Emby name.
  Derived IMDb IDs are displayed with source provenance rather than as missing or Emby-stored IDs.
- Historical normalized-v2 runtime on the full 10.86 GB archive: approximately 13 seconds.
- Historical run 4 UI result count: 3,406 recommendations. A normalized-v6 run has not yet been
  executed inside Emby.
- Stored split example: Emby 160974 David Cameron must produce `review-split` /
  `cross-provider-media-partition` when TMDB 1220273/IMDb nm2090098 supports the Brexit movie while
  TVDB 9148336 -> TMDB 1235383/IMDb nm0131538 supports three disjoint episodes. TVDB 293547/Emby
  79297 is review context, not a proven politician replacement.
- TMDB episode regression requirement: capture both root `guest_stars` and appended `credits.cast`.
  TMDB 1235383 David S. Cameron is a root guest star on all three Emby 160974 episodes. Failure of
  TMDB `/find/{tvdbEpisodeId}` is an unresolved crosswalk, not negative cast evidence; fall back to
  the exact TMDB series/season/episode route when Emby has that hierarchy.
- Named rename regression: Emby 12148 Don Barry must produce one provider-consensus rename to
  `Don 'Red' Barry`, showing TMDB 103789 support 1/7, TVDB 7871284 support 7/7, and derived IMDb
  provenance `TMDB 103789 -> IMDb nm0057983`. Optional quoted-nickname removal establishes strong
  compatibility; configured `Don=Donald` is supplementary only. The executable name-safety fixture
  is `tests/PersonCleaner.NameRegression`.
- Named split/consolidation regression: Emby 129559 Juan Fernandez must produce one actionable
  `review-split` case: retain TMDB 1607 / TVDB 9126505 / IMDb nm0273592 for the movie relationships;
  create TMDB 1284938 / TVDB 7876703 / IMDb nm1537814 and move Money Heist. Suppress separate
  provider-duplicate, same-provider split, and unresolved-ID rows for the same decision.
- Named provider-credit regression: Emby 448634 Shawn Murray must produce one TVDB replacement
  9102233 -> 7876353, preserving the isolated conflicting episode credit as provider-misattribution
  evidence. It must not become a split or a merge with Emby 34565.
- Named no-action regressions: Emby 10573 Elton John (TVDB 277872) and Emby 10223 David McKail
  (TMDB 1231421 / TVDB 376540) retain their identities. Partial but healthy current-provider coverage
  without a supported alternative remains audit evidence and must not enter the review-case grid.

Do not use benchmark runs 1 or 2 as algorithm examples. Run 1 failed during the superseded
full-truth implementation. Run 2 is retained but marked failed/superseded because it incorrectly
allowed rejected TVDB candidates to become replacement recommendations. Run 3 is the final offline
benchmark; run 4 is the equivalent Emby task result to review.

## Normalized review data

### `housekeeping_run`

Persistent execution state: algorithm version, base/derived truth, phase, progress, heartbeat,
status, timestamps, and error.

### `housekeeping_signal`

Normalized observations keyed by run, person, provider, and signal type. Important columns include:

- `person_emby_id`, `subject_truth_entity_id`
- `provider`, `signal_type`
- `current_external_id`, `candidate_external_id`
- `current_name`, `candidate_name`
- optional linked-media/role fields
- `score`, `confidence`, `evidence_text`, `evidence_json`

Current identity-status signal types are `identity-live`, `identity-unavailable`, and
`identity-unresolved`. A provider saying an ID is unavailable is deliberately distinct from an
unresolved/provider-failure condition.

### `housekeeping_recommendation`

The review queue. Important columns include:

- `operation`
- `recommendation_type`
- `primary_signal_type`
- `provider`
- `current_value`, `proposed_value`
- `linked_media_count`, `checked_media_count`, `supported_media_count`
- `score`, `confidence`
- `review_status`, `evidence_summary`

The DXGrid reads the latest completed run through `housekeeping_latest_results` and supports grouping
by recommendation and primary signal type.

### Delta truths

Derived housekeeping truths no longer copy the baseline graph. Changes are stored in:

- `truth_entity_delta`
- `truth_identity_delta`
- `truth_relationship_delta`

Run 4 applies only unambiguous single-person name/identity recommendations to its delta truth.
Merge and split recommendations remain pending human review and do not mutate the delta graph.

## Current recommendation rules

### Replace provider ID

- Recommendation: `replace-provider-id`
- Primary signal: `linked-media-identity`
- A differing TVDB resolution is eligible only when its resolution provenance is `inferred`.
- It must have positive provider-native linked-media support.
- Rows classified `rejected` never become recommendations or truth deltas.
- Run 4 should contain 787 TVDB replacements, each with `supported_media_count >= 1`.

### Remove provider ID

- Recommendation: `remove-provider-id`
- Primary signal: `identity-unavailable`
- Emitted when the provider explicitly reports the assigned identity unavailable and no supported
  replacement was acquired.
- Expected run-4 counts: 321 TMDB IDs and 27 TVDB IDs.

### Rename person

- Recommendation: `rename-person`
- Primary signal: `same-id-provider-rename`
- A same-ID provider-name difference is candidate evidence, not sufficient rename evidence.
- The current Emby name is retained when any current provider identity confirms it as a canonical
  name or alias and that identity is backed by credits on the person's linked media.
- A rename is emitted only when no healthy media-backed current provider supports the current name
  and the proposing provider's identity/name is itself supported on linked media.
- TMDB preference applies only after this anchor rule; it does not override a healthy TVDB-backed
  current spelling (or vice versa).
- A provider-name collision with another Emby person becomes `review-existing-emby-person`, not an
  automatic rename.

### Merge review

- Recommendation: `review-merge`
- Primary signal: `shared-provider-identity`
- Multiple Emby people converge on one provider identity and each participating Emby person has at
  least one linked-media credit supporting that identity on that provider.
- A shared assigned ID without provider-native media support is no longer enough to emit a row.
- The review grid displays recommendation ID, participant Emby IDs, each participant's current
  TMDB/TVDB/IMDb IDs, proposed provider identity, and linked media/role evidence. The reviewer still
  chooses the survivor and any provider lock.

### Split review

- Recommendation: `review-split`
- Primary signal: `linked-media-partition`
- Requires multiple name-compatible provider identities supported by linked-media credits.
- Provider disagreement or provider-name difference alone is not split evidence.

## Established algorithm principles

- TMDB is checked first and generally wins provider disagreements, but provider-native linked-media
  evidence is strongest for resolving that provider's identity.
- Search all linked media until support is found; do not cap obscure people at three items.
- Movies, shows, then episodes is the preferred evidence order, but coverage must remain explicit.
- General provider name search is candidate discovery only. A person existing somewhere on a provider
  does not establish that they belong to library media.
- Near name on linked media is strong; near name plus compatible role is stronger.
- Cross-provider IDs are corroboration, not a mandatory route when linked media already confirms the
  person.
- Check Emby duplicates before recommending rename, merge, split, or removal.
- Removing an unsupported person is safer than performing a wrong merge or split because media
  refresh can recreate removed people.
- Human review remains required. No result mutates live Emby.

## Known limitations to target during review

These are algorithm limitations, not archive-performance limitations:

1. Name matching in `normalized-v2` is still mostly exact/case-normalized. It does not yet implement a
   durable near-name score for punctuation, token order, initials, transliteration, or aliases.
2. Rename evidence exposes linked-media counts, but provider-role compatibility is not yet a first-
   class scored signal in every recommendation.
3. Replacement confidence is currently coarse. It needs scoring based on media type, role match,
   name similarity, coverage, negative evidence, and cross-provider corroboration.
4. Negative evidence needs explicit complete/incomplete coverage wording:
   `not present on any linked media`, with `and linked media not present` when provider media coverage
   is incomplete.
5. Person-removal recommendations are not yet fully implemented from combined provider and linked-
   media evidence. Current removal rows remove unavailable provider IDs, not necessarily the person.
6. Merge evidence currently starts from a shared provider ID. It needs explicit compatible versus
   contradictory linked-media/role evidence.
7. Split evidence is deliberately conservative and currently exact-name oriented. It needs reviewed
   examples before expanding candidate generation.
8. Evidence should eventually classify whether acquisition requires a user API key or can be supplied
   through Emby's native provider integration. The current run uses only archived data and therefore
   performs no acquisition.

## Recommended feedback format

For each reviewed row, capture:

```text
Person / Emby ID:
Recommendation ID:
Provider:
Current ID/name:
Proposed ID/name:
Recommendation shown:
Correct outcome:
Linked media checked:
Positive signals:
Negative/contradictory signals:
Was Emby duplicate state relevant?:
Expected confidence or confidence band:
Rule learned / rule changed:
```

Use concrete media titles, provider IDs, credited names, roles, and whether every linked media item was
available on the provider. These reviewed examples should become named regression fixtures before
changing scores.

## Useful review queries

Latest run summary:

```sql
SELECT recommendation_type, provider, COUNT(*)
FROM housekeeping_latest_results
GROUP BY recommendation_type, provider
ORDER BY recommendation_type, provider;
```

One person's recommendations and signals:

```sql
SELECT *
FROM housekeeping_recommendation
WHERE run_id = 4 AND person_emby_id = :emby_person_id;

SELECT *
FROM housekeeping_signal
WHERE run_id = 4 AND person_emby_id = :emby_person_id
ORDER BY provider, signal_type, media_emby_id;
```

Review outcomes by recommendation and signal type:

```sql
SELECT recommendation_type, primary_signal_type, review_status, COUNT(*)
FROM housekeeping_recommendation
WHERE run_id = 4
GROUP BY recommendation_type, primary_signal_type, review_status;
```

The next chat should read this file, `PERSON_HOUSEKEEPING_ALGORITHM.md`, and relevant portions of
`PROJECT_HANDOFF.md` before modifying the algorithm.
