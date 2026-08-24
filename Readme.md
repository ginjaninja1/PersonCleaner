# PersonCleaner

PersonCleaner is an Emby person entity-resolution plugin. It treats local media relationships as the durable identity anchor, hydrates TMDB and TVDB evidence in a scheduled task, and presents pre-calculated decisions for human review and explicit operator-approved updates.

The old whole-library implementation is intentionally excluded from compilation. The active implementation is under `src/PersonCleaner/V2`.

## Safety boundaries

- Evidence collection is read-only. Live Emby writes occur only after an operator ticks a decision, reviews the separate scoped-change dialog, and presses **Update Emby**.
- The commit path validates every current provider binding and credit relationship immediately before writing. It can set/remove person provider IDs and move only the sampled credit relationships listed in the dialog; it never deletes a person or media item.
- Raw provider responses, flattened indexes, persistent provider corrections, manual bridges, run history, and decisions live under Emby's data directory in `personcleaner-v2/`.
- API keys remain in Emby's normal plugin configuration. They are not written to the evidence database, raw cache, or logs.
- An `ORPHAN` result never recommends deleting an Emby person. It may recommend review of removing only a named current provider binding after that provider authoritatively returns `404/410`.
- Automatic matches remain shadow decisions until an operator explicitly commits the listed changes.

## Development sandbox

Sandbox mode is the default. It chooses a stable deterministic cohort of:

- 50 movies; and
- 50 series.

Every available TMDB and TVDB media ID on those same titles is queued. This avoids provider-biased samples and makes repeated runs directly comparable. Change the sample seed to evaluate another cohort. Full mode is available explicitly from configuration.

Sandbox configuration can also name Emby movie/series IDs and Emby person IDs that must be included. Explicit titles are added to the deterministic sample without reducing its 50+50 allocation. An explicit person adds only provider-addressable movies/series directly associated with that person in Emby; the cohort does not expand transitively through other people on those titles. This keeps edge-case tests deliberate and prevents scope snowballing.

## Scheduled pipeline

1. Select the bounded media cohort, add any explicit test titles/person-title relationships, and snapshot only that cohort's local people/credit relationships. Separately snapshot every Emby person's current TMDB/TVDB/IMDb bindings as a safety index; those global rows never participate in anchor scoring.
2. Fetch or reuse cached provider media records and flatten their credits and media crosswalk IDs.
3. Queue the unique people discovered by those credits for graph enrichment, and separately queue the current TMDB/TVDB IDs of the same in-scope local people for binding validation.
4. Fetch or reuse cached provider person records and flatten their names, aliases, birth dates and all recognized external IDs. Validation-only records are retained for future use but cannot seed the current identity graph.
5. Resolve media through the transitive graph of all provider-native and external IDs, evaluate only index-blocked cross-provider person pairs with the fixed `person-evidence-v5` model, then join them through component-level identity constraints.
6. Build the sparse bipartite reconciliation graph between constrained provider components and in-scope Emby people. Resolve each connected region once, classify one-to-many, many-to-one and many-to-many structure, and assign each relevant local credit only when canonical media, compatible naming and role attribution identify exactly one component owner.
7. Persist every pair feature, cluster membership, decision, evidence line, impacted title and exact `KEEP`/`MOVE` credit assignment. The commit planner consumes this stored plan; it does not reconstruct moves from provider IDs.

Person enrichment is locally scoped: a discovered provider credit is graph-eligible only when it shares a current provider person ID with a locally credited Emby person, or when its normalized name matches a local person on the same selected title. Current TMDB/TVDB bindings are also fetched for those locally credited people, but validation-only responses do not become graph nodes. The complete person payload is still cached and flattened so names, aliases, birth dates and external IDs remain available if later media evidence or an operator correction makes that record graph-eligible.

Fresh cache entries perform no network request and normally require no JSON parsing. Each manifest also stores the version of the materializer that interpreted its raw payload. A model upgrade re-flattens the cached payload once without a provider request, then records the new version. When only the TTL expires, the response is hashed; an unchanged payload with the current materializer refreshes its TTL without re-flattening. Authoritative `404/410` responses have their own TTL cache. Every queued entity records exactly one resolution-facing state for the run: `PRESENT`, `ABSENT`, or `UNAVAILABLE`; technical failure details remain operational diagnostics.

TMDB and TVDB hydration run as independent parallel pipelines. Each provider uses a fixed-size worker pool (defaults: TMDB 4, TVDB 2), its own request-start interval, and bounded retry behavior. TVDB token refresh is single-flight. The media phase remains a hard barrier before person discovery, and the person phase remains a hard barrier before offline resolution.

## Decision meanings

- `MATCH`: provider evidence supports one shadow identity.
- `MATCH_WITH_CONFLICT`: independent evidence establishes one identity while provider attributes or secondary identifiers disagree; the link is retained and the metadata conflict remains visible.
- `MERGE`: one constrained provider identity is anchored to more than one in-scope Emby person. Exact attributable credits may be moved to its selected direct owner; ambiguous attribution remains human review.
- `REALIGNMENT`: multiple distinct provider identities and multiple Emby people form one connected local-attribution region. Each provider component keeps its distinct direct owner and only uniquely attributable credits are moved between them.
- `DRIFT`: the current provider key disappeared or changed, but compatible naming and unchanged local-media mass preserve the historical Emby anchor.
- `CONFLATION`: two provider profiles share evidence but remain below the automatic threshold.
- `SPLIT`: one Emby person points to disconnected provider components.
- `ORPHAN`: no media-derived provider node supports a locally credited Emby person. A provider-confirmed absent current binding may produce `REVIEW_REMOVE_STALE_PROVIDER_ID`; a present but unsupported binding remains ordinary human review, and an unavailable required acquisition withholds the decision.

Any otherwise actionable decision becomes `INCOMPLETE_SCOPE` when its proposed TMDB/TVDB/IMDb identity is already held by an Emby person outside the evaluated cohort. No change is offered and the cohort is never expanded automatically; the evidence names the existing owner so the operator can add that person or relevant media explicitly and rerun. Commit preflight repeats the same ownership check against live Emby.

Missing provider fields and unmatched filmography are neutral, not contradictions. A smaller provider filmography may be wholly contained in a larger one. Repeated shared credits accumulate support, compatible role/category evidence strengthens it, and common names are discounted. Different known birthdays or external IDs reduce evidence strength but do not by themselves force provider profiles apart. An explicit provider-native person cross-reference is candidate evidence, but cannot establish identity automatically without independent stable-ID support or compatible role-aware shared-media attribution. Compatible normalized naming plus role-aware shared media can dominate correlated metadata errors only when no alternative same-envelope provider person has a compatible attribution on any observed title.

Identity evidence strength describes the provider cluster and is a deterministic decision score, not a calibrated probability. Local-anchor confidence separately describes how securely that cluster maps back to an Emby person. A stable one-provider/one-Emby binding is therefore not emitted as a misleading `100% MATCH`.

The full-screen evidence dialog is sorted by risk and uncertainty and returns up to the configured row limit from every decision class, rather than applying one global limit before grouping. The grid has no pager: virtual row rendering keeps filters and the horizontal scrollbar available while detail grids are rendered when their parent is expanded. The summary always shows the uncapped class totals. Each row leads with the decision in ordinary language; expansion reveals supporting/conflicting signals and representative impacted titles. Emby people and media link to the current server, while TMDB, TVDB and IMDb identifiers link to their provider pages in a new tab. Ticking **Change** opens a separate preview of the exact in-scope Emby mutations and, when appropriate, offers a prefilled provider-correction dialog.

The Provider corrections tab stores a persistent operator overlay without editing raw payloads or flattened source facts. Separate add dialogs cover media-person attribution, credit role, person/media cross-references, person name or birthday, local provider bindings and explicit identity relationships. A blank replacement marks the selected fact unusable; a supplied replacement substitutes it only for effective analysis. Corrections can be edited, disabled, re-enabled or removed. Every active rule records whether it triggered in each calculation run, and triggered rules write an informational log line.

Schema migrations are offline operations. Stop Emby and back up `entity-resolution.db` before applying every migration after the database's current `schema_info.version`, in numeric order. Existing schema 7 workspaces require `008_credit_assignment_plan.sql`; schema 6 workspaces require `007_global_person_scope.sql` followed by `008_credit_assignment_plan.sql`. Use SQLite's fail-fast mode, for example `sqlite3.exe -bail entity-resolution.db ".read C:/path/to/PersonCleaner/migrations/008_credit_assignment_plan.sql"`. The plugin validates `schema_info` before touching the existing structure and refuses to start resolution against an older or incomplete schema. Run the evidence task once after migration so the global binding safety index, acquisition observations and exact credit-assignment plans are populated.

## Build and test

```powershell
dotnet build .\src\PersonCleaner\PersonCleaner.csproj -c Release -p:SkipPluginDeploy=true
dotnet run --project .\tests\PersonCleaner.EngineTests\PersonCleaner.EngineTests.csproj -p:SkipPluginDeploy=true
```

Omit `SkipPluginDeploy` only when intentionally deploying the compiled plugin into the local Emby installation.

See [the architecture](docs/ARCHITECTURE.md) for the data model, scoring rules, cache semantics, performance characteristics, and extension points.
