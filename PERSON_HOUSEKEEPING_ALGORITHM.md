# Person housekeeping algorithm

Person housekeeping evaluates every in-scope Emby person, not only people whose provider identity
has failed. A dead provider identity is one trigger and one piece of evidence within the same
consistent workflow.

The pass reads Emby observations and provider evidence and produces reviewed proposals against a
truth. It does not update Emby. Human review is required initially.

## Objectives

For each person, determine whether to:

- retain the person and relationships unchanged;
- replace or remove a provider identity;
- change the preferred name while retaining prior names as aliases/evidence;
- remove a bad relationship;
- merge duplicate Emby people;
- split relationships between distinct real people; or
- remove an unsupported person.

Avoiding an incorrect merge or split is more important than retaining an unsupported person. A
removed person can be recreated by a later media refresh.

## Evidence order

1. Snapshot the Emby person, all external identities, and every linked media relationship.
2. Evaluate TMDB and TVDB symmetrically through the common evidence contract. Their acquisition
   routes differ, but neither provider's absence substitutes for checking the other provider.
3. Use a healthy provider's external IDs to nominate candidates on the other provider where its API
   permits; still require provider-native linked-media support before accepting that candidate.
4. For each provider, validate an asserted identity as live, unavailable, wrong-type, or transiently
   uncheckable.
5. Examine the person's complete set of linked media on that provider. Prefer movies over series
   over episodes when ordering work, but do not impose a three-item cap.
6. A provider name search may discover candidates. It cannot validate a candidate. A candidate is
   useful only when corroborated by media linked to the Emby person.
7. Before proposing a rename, identity replacement, merge, or deletion, check Emby for another
   person already carrying the candidate identity or the same media/name evidence.

## Linked-media evidence

Preserve the scope of every Emby relationship. Compare movie credits with provider movie credits,
series credits with provider series or aggregate-series credits, and episode credits with the cast
of the corresponding provider episode. These are `exact-scope` observations. When an Emby episode
cannot be confirmed at episode scope but the candidate has a credit on the correct provider series,
record a separate `broader-series-scope` observation. It is positive but less discriminating
evidence; it is not an exact episode match. Never use a provider's aggregate `episode_count` to
infer that arbitrary Emby episodes match.

For TMDB episodes, exact cast acquisition must combine the episode's root `guest_stars` collection
with any appended `credits.cast` collection. TMDB legitimately places episodic actors such as David
S. Cameron in `guest_stars`; checking only `credits.cast` creates false `not-present` evidence. A
failed TVDB-episode-ID lookup through TMDB `/find` is only a failed cross-provider ID mapping. When
Emby supplies the TMDB series plus season and episode numbers, query that exact TMDB episode route
before classifying the media as unavailable or the person as absent.

For the initial truth experiment, an exact-scope match contributes `1.0` and a broader-series-scope
episode match contributes `0.9` to coverage. Both raw counts and the weighting must be displayed in
the evidence so this assumption can be evaluated and recalibrated rather than hidden.

Every relationship contributes one of these explicit results:

- `confirmed`: candidate is credited on the resolved media with compatible name and role;
- `name-only`: compatible name is present but role differs or is unavailable;
- `role-only`: compatible role is present but the candidate name is incompatible;
- `not-present`: resolved media was checked and no compatible candidate was present;
- `media-unavailable`: the linked media could not be resolved on that provider; or
- `unchecked`: no provider observation was available.

Do not treat `media-unavailable` or `unchecked` as negative identity evidence. Negative summaries
must distinguish:

- `not present on any linked media`; and
- `not present on any linked media; some linked media not present on provider`.

The denominator matters. One strong match across a person's only linked production is stronger than
one match among many contradictory productions. The algorithm records the complete relationship
coverage, not merely a match count.

Candidate acquisition and recommendation acceptance are separate. Every compatible candidate found
by provider name search, provider reverse lookup, or an owned-media cast is retained even when it
does not clear a recommendation threshold. `housekeeping_signal` stores one candidate/provider/media
observation, so checked, positive, negative, and unresolved counts are derived from evidence rather
than implied by recommendation wording.

## Name and role rules

- Exact name on linked media is strong evidence.
- A near name on linked media is strong evidence.
- A near name in the same role on linked media is very strong evidence and may eventually qualify
  for automatic acceptance after evaluation.
- A generic role without compatible name evidence is weak and commonly ambiguous.
- A provider alias matching the Emby name is name agreement, not a conflict.
- Different canonical names on TMDB and TVDB do not imply a split when identities and linked-media
  roles converge. TMDB supplies the preferred name by default; provider-native names remain aliases
  and evidence.
- Name compatibility is explainable and layered: Unicode/case/punctuation normalization, exact
  provider aliases, removal of optional quoted or parenthesized nicknames, and finally an editable
  set of direct given-name equivalence pairs. Equivalence pairs require identical remaining name
  tokens and are not transitively expanded. They corroborate compatibility only and can never
  establish an identity, replacement, merge, split, removal, or rename without provider-native
  media support. The active list is visible and editable in plugin configuration and snapshotted in
  each experiment run.

### Anchor rule

The anchor is the current Emby person together with its complete linked-media relationship set. A
provider's canonical spelling is never the anchor by itself. Provider identities, canonical names,
aliases, credits, roles, birth data, and cross-provider external IDs are evidence about the anchored
library person.

Retain the current Emby name when at least one current provider identity both:

- confirms that name as its canonical name or an alias; and
- is credit-backed on linked media belonging to the Emby person.

This vetoes a rename proposed merely because another healthy provider prefers a different spelling.
For example, a media-backed TMDB `Sarah Seegar` identity and a media-backed TVDB `Sara Seegar`
identity describe a healthy spelling disagreement; whichever spelling is already used by Emby is
retained when that spelling is confirmed by either anchored provider. A rename is reviewable only
when the current name is unsupported by every healthy media-backed identity and the proposed name is
itself backed by linked-media credits. Name search alone never passes this rule.

When every healthy media-backed provider agrees on the same canonical name and no healthy provider
exactly preserves the current Emby name as a canonical name or alias, emit one consolidated rename
recommendation showing every provider's coverage. If healthy providers disagree, or one exactly
anchors the current spelling, retain the current Emby name. Structural or configured name
compatibility explains why the forms can describe the same anchored person; it does not select the
preferred spelling by itself.

## Provider identity decisions

TMDB and TVDB use one provider-neutral decision pipeline. Each provider adapter may use the search
and reverse-lookup routes its API supplies, but those routes only nominate candidates. A replacement
candidate must independently cover at least 80% of the Emby person's linked media using the
scope-aware coverage above. Exact-scope and broader-series-scope counts remain separately visible.
Tied or near-tied candidates remain unresolved. Names and aliases establish compatibility; IMDb,
TMDB/TVDB cross-links, Wikidata, birth data, and biography are recorded separately as corroboration
and may raise confidence, but cannot substitute for provider-native media overlap. Targeted TMDB and
TVDB requests run concurrently and use their normal response caches.

Provider-native media evidence is the strongest evidence for that provider. Confirmed identities
from the other provider are corroboration and candidate-discovery inputs, not substitutes for the
provider's own media evidence.

When a current provider identity is directly supported on at least one linked media relationship,
its archived external IDs may nominate candidates on the other provider. In particular, a
TMDB-supplied IMDb ID is tried through TVDB remote-ID search before loose TVDB name candidates are
considered. A successful empty remote-ID search is cached. This chain is displayed with provenance
(for example `TMDB 1244700 -> IMDb nm0151258`) and never masquerades as an IMDb ID stored by Emby.
It remains discovery and corroboration only: the nominated TVDB person must still pass TVDB-native
linked-media coverage. If remote-ID discovery has no supported result, name search may still run,
but unsupported search candidates remain internal acquisition evidence.

Every run audits every linked Emby person independently against both providers. The archive pass
records whether the asserted ID exists, whether another Emby person shares it, and exact-scope,
broader-series-scope, absent, and unresolved media counts. Healthy identities produce audit signals
but no review row. Missing, unavailable, duplicated, unsupported, partially supported, or
incompletely checked identities enter a bounded evidence-gap queue. That queue is capped at 1,000
people per provider per run and checkpointed in the normal provider cache. For each queued person,
owned-media cast lookup comes first; provider name and external-ID search are nomination backstops,
and only nominated people receive full person/filmography acquisition. This is not an
unavailable-ID side algorithm.

During algorithm development, the bounded acquisition set is a frozen provider-specific evaluation
cohort, not a rolling backlog window. On first use, reconstruct the original cohort from the earliest
1,000 `person-evidence-audit` checkpoints where available; otherwise freeze the first selected
evidence-gap set. Subsequent Evaluate Truth runs must reuse those same Emby people even after their
evidence improves. Provider response caching remains independent: repeated cohort evaluation should
mostly read cached evidence. Do not advance to later people merely because the current cohort has
been fetched; advancement requires a separate explicit operator workflow.

A provider person-detail `404` is retained as unavailable candidate evidence and negatively cached
for the configured success-cache period. It must skip that candidate and continue the remaining
person and cohort. One stale cast/search candidate must never abort the complete Evaluate Truth task.

TMDB episode absence is negative evidence only after the cached exact episode-details response has
been normalized completely. That response already contains root `guest_stars` and appended
`credits.cast`; no second episode or season request is normally required. Migration 7 rebuilds the
materialized provider-neutral credit index and the legacy TMDB credit indexes directly from those
preserved response bodies. Raw and normalized counts are recorded per production, and a mismatch is
an explicit data-quality condition rather than negative evidence. Episode-count aggregates are
never treated as exact overlap.

- Live asserted ID with coherent linked-media evidence: retain it.
- Dead asserted ID with a media-backed replacement: propose replacement.
- When stored resolution has no replacement, mine every linked provider production's archived cast
  before proposing removal. Promote a unique strongest canonical/alias match; a conservative
  near-name candidate is eligible only with complete coverage across at least two provider-resolved
  linked media. Preserve the current Emby name when another healthy provider anchors it.
- Dead asserted ID with no replacement, while the other provider confirms the person: propose
  removing only the dead provider ID.
- No candidate on any resolved linked media and no remaining confirmed identity: propose removing
  the person and relationships, subject to the Emby duplicate check and human review.
- Candidate evidence partitions relationships between different people: propose a split rather than
  forcing a single identity.
- Another Emby person already represents the media-backed candidate: do not rename the unsupported
  person into that candidate. Emit `review-merge`, display both Emby people and all identities, and
  propose relationship reassignment to a human-selected survivor.
- Positive candidate evidence below the replacement threshold is retained as
  `review-unresolved-provider-id`; it never collapses into "no candidate found".
- Emit `remove-provider-id` only when every linked relationship is provider-resolved and checked,
  no compatible candidate has positive media evidence, and no replacement or merge review exists.

## Provider identity issues

The library decision and provider-quality finding are separate outputs. When multiple TMDB or TVDB
person IDs plausibly represent the same real person, persist a `person-split` provider issue. When
one provider ID appears to conflate distinct people, persist `person-conflation`. Provider issues
record provider IDs, related Emby IDs, evidence, confidence, review status, preferred local ID, and
operator notes. Confirming an issue affects later local reasoning but never edits the provider.

TMDB and TVDB share the same classifications. Their adapters acquire evidence sympathetically to
their APIs: TMDB uses person search, external-ID find, exact movie/series/episode credits and person
combined credits; TVDB uses name/remote-ID search, extended people records, series character feeds,
episode IDs and movie/series credits. Missing API capabilities are recorded as unresolved rather
than simulated through the other provider.

Graph paths are displayed but are not unrestricted transitive proof. A merge still requires direct
media support for the candidate on the affected Emby person's relationships, or a previously
human-confirmed provider issue. One erroneous assignment must not merge an entire connected
component automatically.

## Human-review identity envelope

Every merge or split row must display its recommendation ID and the current Emby person ID(s). It
must also show all currently archived TMDB, TVDB, and IMDb IDs for every participating Emby person,
the proposed provider IDs, and the linked media title, Emby media ID, role, and character. Missing
IDs remain visibly missing; a provider ID must never be displayed as though it were an Emby ID.

A shared provider ID alone is not sufficient justification for a merge. Every participating Emby
person must have provider-native credit support on at least one of that person's linked media before
`review-merge` is emitted. IMDb convergence, birth data, aliases, and external IDs should be shown as
corroboration where archived, but an IMDb-only conclusion remains a human tie-break because this
workflow has no IMDb API. A merge survivor and provider lock are human decisions, not implied by the
lowest Emby ID. Similar-name provider people with no owned-library media are negative candidate
context and must not be treated as evidence that they represent a library person.

A provider-side person-split issue may be attached to an Emby anchor only when at least one of the
implicated provider identities is currently asserted by that Emby person or has positive
provider-native support on its linked media. Candidate-to-candidate name or birth agreement cannot
by itself create a human-review row. Unsupported search-only candidates remain internal evidence.

## Worked decisions from the archive

## Normalized-v12 relationship, identity and ownership decision model

`normalized-v12` preserves Migration 7 acquisition, archives and normalized provider observations,
but replaces the final action-selection order. An Emby person is an immutable investigation anchor
for the duration of a run, not the guaranteed surviving record.

1. Freeze all Emby people, their current TMDB/TVDB/IMDb identities, and every linked media
   relationship. No conclusion may change the snapshot used by another conclusion in the same run.
2. Evaluate every relationship at matching provider scope: movie to movie, series to series, and
   episode to exact episode. TMDB episodes combine root `guest_stars` and appended `credits.cast`.
3. Record positive, negative, contradictory and unresolved provider facts. Episode absence is
   negative only when acquisition is complete and raw and normalized screen-credit counts agree.
4. Nominate identities from native credits, current IDs, direct external IDs, existing owners,
   aliases/names and finally provider search. Names nominate and corroborate; they do not create an
   identity cluster alone.
5. For every media-supported alternative, complete the justified identity landscape through direct
   TMDB, TVDB, IMDb and Wikidata links. Read local/cache evidence first and acquire missing direct
   provider identities when supported by the provider API. Successful, empty and 404 outcomes are
   cached. Other-provider media support is evaluated separately from identity corroboration: a
   shared IMDb/Wikidata/provider ID may establish identity even when the other provider lacks the
   particular credit.
6. Build explicit identity clusters only from direct identifiers and provider-native evidence. Do
   not infer unrestricted transitive merges. Conflicting direct identifiers remain contradictory
   evidence rather than being silently joined.
7. Build a relationship-support matrix showing which cluster supports each Emby media link.
8. Before choosing any rename or provider-ID replacement, check every supported cluster against all
   existing Emby people using indexed TMDB, TVDB, IMDb and Wikidata identity keys.
9. Choose the operator action only after relationship partitioning and ownership are known:
   - current cluster supported: retain, hydrate or rename;
   - one alternative with an existing owner: move relationships to that owner and emit one
     consolidated merge/reassignment case;
   - one alternative without an owner: transmute the anchor by replacing/hydrating IDs and name;
   - disjoint relationship partitions: move to existing owners or propose new people in one split;
   - incomplete evidence: internal audit/suppression;
   - complete stale ID with no supported alternative: remove-ID or unresolved-relationship review.
10. Identity confidence, relationship confidence and operation confidence are distinct. The grid's
    confidence must describe the proposed operator action, not merely the strongest media fact.

James Graven, recommendation 8340 / Emby 421928 from normalized-v11, is the acceptance example for
the existing-owner branch. TVDB 9140765 James Craven supports exact episode 11677 and carries IMDb
nm0186609. That IMDb ID maps to TMDB 1063289, already owned by Emby 165106 James Craven. The correct
case moves the `The One-Armed Man` relationship from Emby 421928 to Emby 165106 and shows both Emby
records and the full identity cluster. It must not transmute Emby 421928 into a duplicate identity.

- Sara Seegar, Emby 12733: dead TMDB 1213185 must not become `remove-provider-id`. Cast-first media
  evidence discovers TMDB 1161029 Sarah Seegar on all 11 linked productions; the alias Sara Seegar
  preserves the current Emby name, while exact IMDb nm0781504 is corroboration rather than the
  acceptance test. Expected recommendation: `replace-provider-id`, TMDB 1213185 -> 1161029, with
  11/11 checked and supported media displayed.
- Anthony D. Call, Emby 21359: dead TMDB 1556119 has direct media support for TMDB 206048 on one of
  two linked productions. Because Emby 23172 already owns TMDB 206048, expected recommendation is
  `review-merge` naming both Emby IDs, not removal. TMDB 12207770 Anthony D. Call, found by name and
  matching birth date, remains a separate provider `person-split` issue with IDs 206048 and
  12207770; it does not gain media support transitively.
- René Rivera, Emby 419857 and 65914: a merge review must identify both Emby IDs and show TMDB,
  TVDB, and IMDb values per participant. TMDB 101026/IMDb nm0729401 and TVDB 305606 support
  Carlito's Way; TVDB 7874817 is separate bad/incomplete provider data for the NYPD Blue credit.
  IMDb's combined filmography is useful human corroboration, but the plugin must expose the direct
  provider/media gap and must not pretend IMDb API verification occurred. A provider lock and the
  winning TVDB ID remain operator actions.
- David Cameron, Emby 160974: the current record conflates two people through its relationships and
  provider IDs. TMDB 1220273 / IMDb nm2090098 (born 1966) supports only `Brexit: The Uncivil War`.
  TVDB 9148336 supports the Stargate episode `One False Step` and The X-Files episodes `Space` and
  `Small Potatoes`; TVDB itself links 9148336 to the different TMDB 1235383 and IMDb nm0131538.
  The two clusters support 1 and 3 linked media respectively with zero shared supported media, so
  emit `review-split` with `cross-provider-media-partition`. Retain the politician cluster and movie
  on the existing person; propose the actor cluster and three episodes for a new person unless that
  cluster is subsequently found on another Emby person. TVDB 293547 is already owned by Emby 79297
  and has no archived/direct remote-ID evidence connecting it to the politician, so show it as
  same-name context rather than asserting it as a replacement. This case also requires partial
  current-provider coverage to trigger cast-first candidate acquisition: one matching production
  must not suppress investigation of the remaining contradictory relationships.

- Don Barry, Emby 12148: current TMDB 103789 and TVDB 7871284 both use the canonical name
  `Don 'Red' Barry`. TVDB supports 7/7 linked media and TMDB supports 1/7, with all seven TMDB media
  checked. Emit one provider-consensus rename rather than separate provider rows. `Don Barry` is
  strongly compatible after safe removal of the optional quoted nickname; configurable
  `Don=Donald` only corroborates the archived `Donald Barry` alias and is not needed for the
  decision. TMDB supplies IMDb nm0057983, displayed as `TMDB 103789 -> IMDb nm0057983` when Emby
  has no stored IMDb ID. Preserve the six TMDB `not-present` observations.
- Annie Karstens, Emby 47116 and 429373: TMDB 1137005 / IMDb nm2622011 supports Quiz Lady and
  identifies one episode of You, while TVDB 7890097 supports exact episode 7446892 A Fresh Start.
  The cached TMDB episode response for TMDB episode 1944821 already contains Annie in root
  `guest_stars`; normalization must retain it alongside appended cast and emit one
  `review-merge` case naming both Emby IDs and both media relationships; never leave the operator
  with an unnamed `emby-name-collision` row.
- Kimberly Hidalgo, Emby 439699: unavailable TVDB 7886958 must not lead directly to removal. Exact
  episode cast discovery nominates people regardless of canonical-name compatibility. TVDB 393526
  Kimberly Daugherty supports The Beach, aliases `Kim Hidalgo`, and links TMDB 1385322 / IMDb
  nm2583683. Exact cached episode cast must evaluate TMDB 1385322 without surname gating. With
  exact native support, emit one identity-repair case retaining Emby 439699, replacing
  TVDB, hydrating TMDB/IMDb provenance, renaming, and retaining the relationship.
- Juan Fernandez, Emby 129559: the existing record contains two disjoint people. Retain the movie
  cluster TMDB 1607 / TVDB 9126505 / IMDb nm0273592 on the current Emby person. Create a new person
  for TMDB 1284938 / TVDB 7876703 / IMDb nm1537814 and move the Money Heist relationship to it.
  Emit one `review-split` case containing both clusters and their media titles. Same-birth-date and
  provider-duplicate detectors are supporting diagnostics, not additional review rows.
- Shawn Murray, Emby 448634: retain healthy TMDB 3164799 / IMDb nm0615273 and replace TVDB 9102233
  with TVDB 7876353 when the latter matches those external IDs and supports 30/35 linked media.
  TVDB 9102233 belongs coherently to Emby 34565 as TMDB 1733372 / IMDb nm2556500. Its isolated
  credit on The Grocery Store Bank is provider-credit-misattribution evidence, not grounds to split
  or merge the anchored Emby person. Emit one replacement case and preserve all contrary credits.
- Elton John, Emby 10573: current TVDB 277872 has exact provider-native support on 11 relationships,
  with two not-present and one unavailable observation, and agrees with TMDB 11370 / IMDb nm0005056.
  It is healthy incomplete coverage, so emit no review case.
- David McKail, Emby 10223: TMDB 1231421 supports 4/9, TVDB 376540 supports 7/9, and their combined
  evidence supports 8/9. With no supported alternative identity, retain both IDs and emit no review
  case; the remaining unsupported relationship is internal audit evidence.

- Alexander Terentyev: replace dead TMDB ID with the exact-name candidate appearing in both linked
  movies and matching both roles.
- Lester Prendergast: the person's only linked movie yields `Lester Pendergast` in the same role.
  Complete one-of-one coverage makes this a high-confidence replacement and rename proposal.
- Becca Lish and John Savoca: exact name and role on their linked media are sufficient strong
  evidence. Cross-provider or IMDb convergence is optional corroboration, not a requirement.
- Don Ames and Dean Ciallella: no compatible person is present on any resolved linked media. With no
  remaining confirmed identity, propose removal for human review.
- Trinity Bliss: TMDB confirms the same ID, near canonical name, and same roles; propose the TMDB
  rename. TVDB alias/name and linked-media evidence then identify the replacement TVDB person. There
  is no split signal.
- Aaron Schwartz: TMDB confirms the person, while exhaustive TVDB linked-media checks find no Aaron
  Schwartz evidence. Remove only the dead TVDB identity.
- Andrew Lauer: TMDB confirms Andrew; TVDB linked media identifies Andy Lauer by surname, media, and
  matching role. Treat the provider name difference as an alias/canonical-name difference and
  replace the TVDB identity. Absence from another production is retained as relationship coverage,
  not treated as a different person by itself.
- Ashley Ender: linked media identifies Ashley Edner, who already exists separately in Emby for that
  production. Do not transform Ashley Ender into Ashley Edner; propose removing the unsupported
  duplicate after checking relationship ownership.

## Access classification

Each observation or activity records whether Emby can supply it without PersonCleaner's provider
key. Archive responses are used to design and evaluate the algorithm initially. Once the workflow is
stable, every required operation must be tested against Emby's built-in provider interfaces and
classified as Emby-stored, Emby-derived, Emby-provider-mediated, or requiring a PersonCleaner API
key.

## Normalized-v13 materialized evidence and production corroboration

`normalized-v13` adds a second, deliberately narrower provider-identity acceptance route. Exact
provider-native support on Emby-linked media remains strongest, but is no longer the only way to
accept a replacement. A candidate can also qualify when all of the following hold:

- the other current provider identity is live and supports at least one anchored Emby relationship;
- the candidate directly links to that exact provider-person ID;
- the candidate and anchored identity share the same IMDb ID;
- no direct identifier contradicts that closed identity cluster; and
- at least two provider-native productions carried by both people are joined through exact
  TMDB/TVDB production external IDs.

Production titles never establish overlap. They are display labels only after the production IDs
have been crosswalked. Acquisition is targeted to exact person-identity candidates and stops after
two exact production confirmations; cache hits, provider calls and the maximum productions examined
for one person remain in the acquisition summary. The destination provider's absence on the
specific Emby media remains separate negative or unresolved relationship evidence and is not
silently converted into support.

Recommendation evidence is materialized once at consolidation in
`housekeeping_recommendation_evidence`. The main review row contains the proposed operator action,
acceptance path, and separate identity, relationship and operation confidences. Expandable evidence
rows expose person links, linked-library support and absence, exact off-library production
crosswalks, contradictions and unresolved observations. The page no longer aggregates the complete
`housekeeping_signal` ledger when it loads.

Acceptance case: recommendation 12557 / Emby 106895 Dermot O'Leary from normalized-v12 must become
a reviewed TVDB replacement from unavailable TVDB 7866725 to TVDB 9120093. TVDB 9120093 directly
links TMDB 1216116 and IMDb nm0641581; TMDB 1216116 independently carries IMDb nm0641581 and supports
the anchored Emby media. Exact production crosswalks include TVDB 78493 -> TMDB 31017 (`Big
Brother's Little Brother`) and TVDB 74626 <-> TMDB 13999 through the production identifiers for
`The X Factor`. TVDB's missing credit on production 5196666 remains visible as negative
provider-coverage evidence rather than blocking the strongly corroborated person identity.
