# PersonCleaner

PersonCleaner is a read-only Emby person entity-resolution plugin. It treats local media relationships as the durable identity anchor, hydrates TMDB and TVDB evidence in a scheduled task, and presents pre-calculated decisions for human review.

The old whole-library implementation is intentionally excluded from compilation. The active implementation is under `src/PersonCleaner/V2`.

## Safety boundaries

- Emby is queried through `ILibraryManager`; this version never writes Emby items, people, provider IDs, images, or relationships.
- Raw provider responses, flattened indexes, manual bridges, run history, and decisions live under Emby's data directory in `personcleaner-v2/`.
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
5. Build hard-link graph components, score only index-blocked cross-provider candidates, and resolve components back to historical Emby people by local-media mass.
6. Persist plain-language summaries, ordered evidence lines, raw metrics and every impacted title for fast UI reads.

Person enrichment is locally scoped: a discovered provider credit is enriched only when it shares a current provider person ID with a locally credited Emby person, or when its normalized name matches a local person on the same selected title. The complete provider credit lists remain flattened for evidence, but unrelated aggregate-series cast and crew do not generate thousands of unnecessary person API calls.

Fresh cache entries perform no network request and no JSON parsing. When an entry expires, the response is hashed; an unchanged payload refreshes its TTL without re-flattening. Failed requests have a persisted negative-cache window.

TMDB and TVDB hydration run as independent parallel pipelines. Each provider uses a fixed-size worker pool (defaults: TMDB 4, TVDB 2), its own request-start interval, and bounded retry behavior. TVDB token refresh is single-flight. The media phase remains a hard barrier before person discovery, and the person phase remains a hard barrier before offline resolution.

## Decision meanings

- `MATCH`: provider evidence supports one shadow identity.
- `DRIFT`: the current provider key disappeared or changed, but compatible naming and unchanged local-media mass preserve the historical Emby anchor.
- `CONFLATION`: two provider profiles share evidence but remain below the automatic threshold.
- `SPLIT`: one Emby person points to disconnected provider components.
- `ORPHAN`: no hydrated provider node supports a locally credited Emby person; review fetch failures and missing IDs before taking any action.

The dashboard is sorted by risk and uncertainty and returns up to the configured row limit from every decision class, rather than applying one global limit before grouping. The summary always shows the uncapped class totals. Each row leads with the decision in ordinary language; expansion reveals supporting/conflicting signals and representative impacted titles. The complete title attribution remains in SQLite. Operators can confirm or reject a TMDB↔TVDB alignment and recalculate immediately from flattened evidence without refetching.

## Build and test

```powershell
dotnet build .\src\PersonCleaner\PersonCleaner.csproj -c Release -p:SkipPluginDeploy=true
dotnet run --project .\tests\PersonCleaner.EngineTests\PersonCleaner.EngineTests.csproj -p:SkipPluginDeploy=true
```

Omit `SkipPluginDeploy` only when intentionally deploying the compiled plugin into the local Emby installation.

See [the architecture](docs/ARCHITECTURE.md) for the data model, scoring rules, cache semantics, performance characteristics, and extension points.
