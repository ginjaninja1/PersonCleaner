# Provider archive schema

## Unified acquisition

`PersonCleanerProviderUpdate` is the only registered archive task. It snapshots Emby once and then
runs TVDB and TMDB workers concurrently. Each provider retains its own throttle, concurrency limit,
response cache, retry state, and normalized tables. `provider_update_run` records task status;
`provider_work` records the latest route and outcome for every provider/Emby pair.

The manifest is provider-neutral orchestration, not provider-neutral evidence. Exact responses and
provider-native normalized data remain separate.

The archive keeps TVDB and TMDB observations separate. Functionally equivalent concepts have
provider-specific storage plus provider-neutral read views. Provider-specific API details are not
discarded merely to make the schemas look identical.

| Concept | TVDB storage | TMDB storage | Unified view |
|---|---|---|---|
| Entity identity and dates | `tvdb_entity` | `tmdb_entity` | `provider_entity` |
| External IDs | `remote_id` | `tmdb_external_id` | `provider_external_id` |
| Aliases/alternative names | `tvdb_alias` | `tmdb_alias` | `provider_alias` |
| Credit observations and fetch route | `tvdb_credit_observation` | `tmdb_credit_observation` | `provider_credit_observation` |
| Current normalized credits | `credit` | `tmdb_credit` | none; observation view is preferred for evidence analysis |
| Exact current API responses | `api_response_cache` | `tmdb_api_response_cache` | none |
| Replaced response history | `api_response_archive` | `tmdb_api_response_archive` | none |
| Fetch outcome/retry state | `fetch_cache` | `tmdb_fetch_cache` | none |
| Per-Emby provider answer | `item_resolution` | `tmdb_item_resolution` | `provider_identity_signals` |

## Intentional differences

- TVDB external IDs retain TVDB's numeric `source_type`; TMDB does not supply an equivalent value.
- TVDB aliases carry a language code. TMDB alternative titles commonly carry a country code, while
  person `also_known_as` values may carry neither. `provider_alias.locale` preserves whichever
  provider-native locale dimension was supplied.
- TVDB credits expose TVDB people/character types, featured state and sort order. TMDB credits expose
  cast/crew kind, department, job/character, credit ID and sometimes aggregate episode counts. The
  unified observation view exposes their common analytical shape; provider tables retain the rest.
- TVDB currently has mature candidate scoring, evaluation and decision-history tables. TMDB capture
  currently retains direct/coordinate/IMDb-find provenance and candidates without confidence scores.
  Those schemas should not be forced into equivalence before TMDB-native signal evaluation.
- TVDB series episode feeds and TMDB episode coordinate calls are provider-specific acquisition
  mechanisms and remain visible in their raw response/cache namespaces.
- TMDB person details already include `also_known_as`, so the canonical person request appends only
  `external_ids,combined_credits`. Cache lookup also recognizes the earlier
  `external_ids,combined_credits,alternative_names` path; an unexpired legacy response is reused
  without an internet request, while the leaner path is used when a genuine refresh becomes due.

## Rebuild behavior

Exact responses in the provider cache/archive tables are the durable source of truth. Normalized
tables are rebuildable indexes. Potentially large transformations must never run during plugin or
repository initialization because Emby constructs scheduled tasks while the server is starting.

Repository initialization is validation-only. The plugin must not create or alter persistent
tables, indexes, views, migration records, or historical data at Emby startup. It opens the existing
archive, verifies TVDB/TMDB schema version 1 and archive migrations 1-4, and fails with an explicit
offline-migration error when anything is missing or incompatible. Schema preparation, migrations,
repairs, and rebuilds are performed offline while Emby is stopped.

Existing TVDB credits predate endpoint-level provenance. An explicit offline rebuild may mark those
rows `source_entity_type='legacy-normalized'` where the original route cannot be reconstructed;
future refreshed TVDB responses record their real source entity type and source TVDB ID.

Provider acquisition does not create new accepted TVDB inferences. It may reuse an already accepted
archived TVDB resolution. TMDB unique IMDb `/find` results retain `external-id` provenance and
ambiguous results remain candidates. Neither path writes to Emby or automatically changes a truth.
