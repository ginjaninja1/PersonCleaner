# PersonCleaner

PersonCleaner is an Emby person entity-resolution plugin. It treats local media relationships as the durable identity anchor, hydrates TMDB and TVDB evidence in a scheduled task, and persists both safe corrections and problem cases requiring operator oversight.

The old whole-library implementation is intentionally excluded from compilation. The active implementation is under `src/PersonCleaner/V2`.

## Safety boundaries

- Evidence collection is read-only. Live Emby writes occur either after case-specific operator approval or through the separately gated **Mass Corrections** task. That task is disabled by default and selects only complete, persisted `SATISFIED_CHANGE` cases; it never selects problem cases.
- The commit path validates every reviewed person, provider binding and credit relationship immediately before writing. It can create only a provider-identified person with assigned media, set/remove person provider IDs, and move only the listed credit relationships; it never calls a person-delete API or deletes media.
- Raw provider responses, flattened indexes, persistent provider corrections, manual bridges, run history, and decisions live under Emby's data directory in `personcleaner-v2/`.
- API keys remain in Emby's normal plugin configuration. They are not written to the evidence database, raw cache, or logs.
- An `ORPHAN` result never recommends deleting an Emby person. It may recommend review of removing only a named current provider binding after that provider authoritatively returns `404/410`.
- Automatic matches remain shadow decisions until either an operator explicitly commits the listed changes or the enabled Mass Corrections task applies the same persisted plan.

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
7. Persist every pair feature, cluster membership, decision and evidence line, then persist one case-wide final projection containing existing/new/unresolved identities, final provider IDs, exact `KEEP`/`MOVE` credit assignments, correction questions, and one presentation purpose: `PROBLEM`, `SATISFIED_CHANGE`, or `SATISFIED_NO_CHANGE`. UI navigation filters these rows directly in SQL and performs no provider HTTP requests.

Person enrichment is locally scoped: a discovered provider credit is graph-eligible only when it shares a current provider person ID with a locally credited Emby person, or when its normalized name matches a local person on the same selected title. Current TMDB/TVDB bindings are also fetched for those locally credited people, but validation-only responses do not become graph nodes. The complete person payload is still cached and flattened so names, aliases, birth dates and external IDs remain available if later media evidence or an operator correction makes that record graph-eligible.

Fresh cache entries perform no network request and normally require no JSON parsing. Each manifest also stores the version of the materializer that interpreted its raw payload. A model upgrade re-flattens the cached payload once without a provider request, then records the new version. When only the TTL expires, the response is hashed; an unchanged payload with the current materializer refreshes its TTL without re-flattening. Authoritative `404/410` responses have their own TTL cache. Every queued entity records exactly one resolution-facing state for the run: `PRESENT`, `ABSENT`, or `UNAVAILABLE`; technical failure details remain operational diagnostics.

TMDB and TVDB hydration run as independent parallel pipelines. Each provider uses a fixed-size worker pool (defaults: TMDB 4, TVDB 2), its own request-start interval, and bounded retry behavior. TVDB token refresh is single-flight. The media phase remains a hard barrier before person discovery, and the person phase remains a hard barrier before offline resolution.

## Decision meanings

- `MATCH`: provider evidence supports one shadow identity.
- `MATCH_WITH_CONFLICT`: independent evidence establishes one identity while provider attributes or secondary identifiers disagree; the link is retained and the metadata conflict remains visible.
- `MERGE`: one constrained provider identity is anchored to more than one in-scope Emby person. Exact attributable credits may be moved to its selected direct owner; ambiguous attribution remains human review.
- `REALIGNMENT`: multiple provider components and multiple Emby people form one connected local-attribution region. Components joined by an unresolved positive identity edge are not asserted to be distinct; the case is presented as a possible cross-provider identity match and the final Person Builder layout settles both identity and resulting credit ownership in one Apply.
- `DRIFT`: the current provider key disappeared or changed, but compatible naming and unchanged local-media mass preserve the historical Emby anchor.
- `CONFLATION`: two provider profiles share evidence but remain below the automatic threshold.
- `SPLIT`: one Emby person points to disconnected provider components.
- `ORPHAN`: no media-derived provider node supports a locally credited Emby person. A provider-confirmed absent current binding may produce `REVIEW_REMOVE_STALE_PROVIDER_ID`; a present but unsupported binding remains ordinary human review, and an unavailable required acquisition withholds the decision.

Any otherwise automatic decision becomes `INCOMPLETE_SCOPE` when its proposed TMDB/TVDB/IMDb identity is already held by an Emby person outside the evaluated cohort, so it cannot enter automatic application. It is projected as an ordinary problem case, **Provider ID also exists outside calculated scope**, and the evidence names the existing owner. The operator uses the same Person Builder and Apply workflow as every other problem case; there is no blocked review state.

Missing provider fields and unmatched filmography are neutral, not contradictions. A smaller provider filmography may be wholly contained in a larger one. Repeated shared credits accumulate support, compatible role/category evidence strengthens it, and common names are discounted. Different known birthdays or external IDs reduce evidence strength but do not by themselves force provider profiles apart. An explicit provider-native person cross-reference is candidate evidence, but cannot establish identity automatically without independent stable-ID support or compatible role-aware shared-media attribution. Compatible normalized naming plus role-aware shared media can dominate correlated metadata errors only when no alternative same-envelope provider person has a compatible attribution on any observed title.

Identity evidence strength describes the provider cluster and is a deterministic decision score, not a calibrated probability. Local-anchor confidence separately describes how securely that cluster maps back to an Emby person. A stable one-provider/one-Emby binding is therefore not emitted as a misleading `100% MATCH`.

The Decision Evidence tab exposes separate full-screen views for problem cases, pending satisfied changes, and all cases. Problem cases are only persisted `PROBLEM` rows; safe pending changes no longer leak into that view. Its **Open case** Boolean command opens a full-screen master/detail projection comparing current and resulting Emby people and provider IDs; collapsed child rows show every relevant media credit as keep, move, receive or correction-required. A valid layout can be applied directly; there is no separate save-plan step. Apply is offered even when the confirmed layout requires no Emby mutation, and it atomically records the receipt plus only the minimum contextual correction rules needed by future runs. Apply is a normal dialog `ButtonItem`, not a built-in OK handle.

The **Enabled Mass Corrections Task** configuration option defaults to off. When enabled, `PersonCleanerMassCorrectionsV2` reads unapplied `SATISFIED_CHANGE` case IDs from the latest completed run, refuses to overlap an evidence build, rechecks the persisted plan and live Emby state, and commits each case through the same preflight, postflight, rollback, receipt, and audit path used by manual Apply. Independent preflight failures do not prevent other safe cases from running; an unsafe rollback failure stops the task immediately.

The Provider corrections tab stores a persistent operator overlay without editing raw payloads or flattened source facts. Separate add dialogs cover media-person attribution, credit role, person/media cross-references, person name or birthday, local provider bindings and explicit identity relationships. A blank replacement marks the selected fact unusable; a supplied replacement substitutes it only for effective analysis. Corrections can be edited, disabled, re-enabled or removed. Every active rule records whether it triggered in each calculation run, and triggered rules write an informational log line.

Schema migrations are offline operations. Stop Emby and back up `entity-resolution.db` before applying every migration after the database's current `schema_info.version`, in numeric order. A schema-10 workspace requires `011_case_presentation_purpose.sql`; an older workspace must apply the earlier numbered files first. Use SQLite's fail-fast mode, for example `sqlite3.exe -bail entity-resolution.db ".read C:/path/to/PersonCleaner/migrations/011_case_presentation_purpose.sql"`. The plugin validates `schema_info` before touching the existing structure and refuses to start resolution against an older or incomplete schema. Migration 011 classifies the retained latest run in place; the next evidence run regenerates the same purpose directly from the plan.

## Build and test

```powershell
dotnet build .\src\PersonCleaner\PersonCleaner.csproj -c Release -p:SkipPluginDeploy=true
dotnet run --project .\tests\PersonCleaner.EngineTests\PersonCleaner.EngineTests.csproj -p:SkipPluginDeploy=true
```

Omit `SkipPluginDeploy` only when intentionally deploying the compiled plugin into the local Emby installation.

See [the architecture](docs/ARCHITECTURE.md) for the data model, scoring rules, cache semantics, performance characteristics, and extension points.
