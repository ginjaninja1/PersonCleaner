# PersonCleaner

PersonCleaner is a read-only Emby person entity-resolution plugin. It treats local media relationships as the durable identity anchor, hydrates TMDB and TVDB evidence in a scheduled task, and presents pre-calculated decisions for human review.

The old whole-library implementation is intentionally excluded from compilation. The active implementation is under `src/PersonCleaner/V2`.

## Safety boundaries

- Emby is queried through `ILibraryManager`; this version never writes Emby items, people, provider IDs, images, or relationships.
- Raw provider responses, flattened indexes, persistent provider corrections, manual bridges, run history, and decisions live under Emby's data directory in `personcleaner-v2/`.
- API keys remain in Emby's normal plugin configuration. They are not written to the evidence database, raw cache, or logs.
- An `ORPHAN` result is a review warning, never a deletion instruction.
- Automatic matches update only the plugin's shadow decisions.

## Development sandbox

Sandbox mode is the default. It chooses a stable deterministic cohort of:

- 50 movies; and
- 50 series.

Every available TMDB and TVDB media ID on those same titles is queued. This avoids provider-biased samples and makes repeated runs directly comparable. Change the sample seed to evaluate another cohort. Full mode is available explicitly from configuration.

## Scheduled pipeline

1. Select the bounded media cohort and snapshot only its local people/credit relationships.
2. Fetch or reuse cached provider media records and flatten their credits and media crosswalk IDs.
3. Queue the unique people discovered by those credits.
4. Fetch or reuse cached provider person records and flatten names, aliases, birth dates, IMDb IDs and Wikidata IDs.
5. Resolve media through the transitive graph of all provider-native and external IDs, evaluate only index-blocked cross-provider person pairs with the fixed `person-evidence-v3` model, then join them through component-level identity constraints.
6. Resolve constrained components back to historical Emby people by local-media mass and persist every pair feature, cluster membership, decision, evidence line and impacted title.

Person enrichment is locally scoped: a discovered provider credit is enriched only when it shares a current provider person ID with a locally credited Emby person, or when its normalized name matches a local person on the same selected title. The complete provider credit lists remain flattened for evidence, but unrelated aggregate-series cast and crew do not generate thousands of unnecessary person API calls.

Fresh cache entries perform no network request and normally require no JSON parsing. Each manifest also stores the version of the materializer that interpreted its raw payload. A model upgrade re-flattens the cached payload once without a provider request, then records the new version. When only the TTL expires, the response is hashed; an unchanged payload with the current materializer refreshes its TTL without re-flattening. Failed requests have a persisted negative-cache window.

TMDB and TVDB hydration run as independent parallel pipelines. Each provider uses a fixed-size worker pool (defaults: TMDB 4, TVDB 2), its own request-start interval, and bounded retry behavior. TVDB token refresh is single-flight. The media phase remains a hard barrier before person discovery, and the person phase remains a hard barrier before offline resolution.

## Decision meanings

- `MATCH`: provider evidence supports one shadow identity.
- `DRIFT`: the current provider key disappeared or changed, but compatible naming and unchanged local-media mass preserve the historical Emby anchor.
- `CONFLATION`: two provider profiles share evidence but remain below the automatic threshold.
- `SPLIT`: one Emby person points to disconnected provider components.
- `ORPHAN`: no hydrated provider node supports a locally credited Emby person; review fetch failures and missing IDs before taking any action.

Missing provider fields and unmatched filmography are neutral, not contradictions. A smaller provider filmography may be wholly contained in a larger one. Repeated shared credits accumulate support, compatible role/category evidence strengthens it, and common names are discounted. Different known birthdays or stable external IDs are explicit conflicts. A same-name, role-compatible attribution to a different person on the corresponding provider is competing evidence and prevents an automatic join.

Identity confidence describes the provider cluster. Local-anchor confidence separately describes how securely that cluster maps back to an Emby person. A stable one-provider/one-Emby binding is therefore not emitted as a misleading `100% MATCH`.

The full-screen evidence dialog is sorted by risk and uncertainty and returns up to the configured row limit from every decision class, rather than applying one global limit before grouping. The summary always shows the uncapped class totals. Each row leads with the decision in ordinary language; expansion reveals supporting/conflicting signals and representative impacted titles. Emby people and media link to the current server, while TMDB, TVDB and IMDb identifiers link to their provider pages in a new tab. TVDB media slugs are flattened from cached payloads by the background task and persisted in SQLite; the dialog never opens or parses provider payload files. The complete title attribution remains in SQLite. Operators can confirm or reject a TMDB↔TVDB alignment and recalculate immediately from flattened evidence without refetching.

The Provider corrections tab stores a persistent operator overlay without editing raw payloads or flattened source facts. Separate add dialogs cover media-person attribution, credit role, person/media cross-references, person name or birthday, local provider bindings and explicit identity relationships. A blank replacement marks the selected fact unusable; a supplied replacement substitutes it only for effective analysis. Corrections can be edited, disabled, re-enabled or removed. Every active rule records whether it triggered in each calculation run, and triggered rules write an informational log line.

Schema migrations are offline operations. Stop Emby and back up `entity-resolution.db` before applying every migration after the database's current `schema_info.version`, in numeric order. Schema 2 requires `003_evidence_model_v2.sql`, `004_materializer_version.sql`, then `005_provider_corrections.sql`; schema 3 requires `004` then `005`; schema 4 requires only `005_provider_corrections.sql`. Use SQLite's fail-fast mode, for example `sqlite3.exe -bail entity-resolution.db ".read C:/path/to/PersonCleaner/migrations/005_provider_corrections.sql"`. The plugin validates `schema_info` before touching the existing structure and refuses to start resolution against an older or incomplete schema.

## Build and test

```powershell
dotnet build .\src\PersonCleaner\PersonCleaner.csproj -c Release -p:SkipPluginDeploy=true
dotnet run --project .\tests\PersonCleaner.EngineTests\PersonCleaner.EngineTests.csproj -p:SkipPluginDeploy=true
```

Omit `SkipPluginDeploy` only when intentionally deploying the compiled plugin into the local Emby installation.

See [the architecture](docs/ARCHITECTURE.md) for the data model, scoring rules, cache semantics, performance characteristics, and extension points.
