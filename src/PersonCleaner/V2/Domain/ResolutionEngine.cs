using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PersonCleaner.V2.Domain
{
    public sealed class ResolutionEngine
    {
        private const string EvidenceModelVersion = "person-evidence-v5";
        private const double ExactNameContribution = 0.35;
        private const double AliasContribution = 0.20;
        private const double ContainmentContribution = 0.25;
        private const double SharedCreditContribution = 0.20;
        private const double RoleContribution = 0.15;
        private const double BirthdayContribution = 0.20;
        private const double CorroboratedMetadataConflictPenalty = 0.15;
        private const double IdentifierConflictPenalty = 0.30;
        private const double BirthdayConflictPenalty = 0.25;
        private const double DominantMediaConflictPenaltyCap = 0.15;

        public ResolutionDiagnostics Diagnostics { get; private set; } = new ResolutionDiagnostics();
        public IReadOnlyList<ResolutionPairEvaluation> PairEvaluations { get; private set; } = new ResolutionPairEvaluation[0];
        public IReadOnlyList<ResolutionClusterSnapshot> Clusters { get; private set; } = new ResolutionClusterSnapshot[0];

        public IReadOnlyList<ResolutionDecision> Resolve(ResolutionInput input, ResolutionSettings settings)
        {
            Diagnostics = new ResolutionDiagnostics();
            PairEvaluations = new ResolutionPairEvaluation[0];
            Clusters = new ResolutionClusterSnapshot[0];
            input = input ?? new ResolutionInput();
            settings = settings ?? new ResolutionSettings();
            PreparePeople(input.ProviderPeople);
            PrepareCredits(input);

            var peopleByKey = input.ProviderPeople
                .Where(x => !string.IsNullOrWhiteSpace(x.Provider) && !string.IsNullOrWhiteSpace(x.ProviderId))
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToDictionary(x => x.Key, StringComparer.Ordinal);
            var rejected = new HashSet<string>(input.Bridges.Where(x => x.IsRejected).Select(BridgeKey), StringComparer.Ordinal);
            var graph = new DisjointSet();
            foreach (var key in peopleByKey.Keys) graph.Add(key);

            var candidates = BuildCandidates(peopleByKey.Values, input, rejected);

            foreach (var bridge in input.Bridges.Where(x => !x.IsRejected).OrderBy(BridgeKey, StringComparer.Ordinal))
            {
                var left = bridge.ProviderA + ":" + bridge.ProviderIdA;
                var right = bridge.ProviderB + ":" + bridge.ProviderIdB;
                if (!peopleByKey.ContainsKey(left) || !peopleByKey.ContainsKey(right)) continue;
                var candidate = FindOrAddManualCandidate(candidates, peopleByKey[left], peopleByKey[right], input);
                candidate.Disposition = graph.Union(left, right, (a, b) => CanMergeComponents(a, b, peopleByKey, rejected, true))
                    ? "operator-confirmed"
                    : "constraint-blocked";
                if (candidate.Disposition == "constraint-blocked") Diagnostics.ConstraintBlockedCandidates++;
            }

            foreach (var candidate in candidates
                .Where(x => string.IsNullOrWhiteSpace(x.Disposition))
                .Where(x => IsAutomatic(x.Score, settings))
                .OrderByDescending(x => x.Score.HardIdentifierMatch)
                .ThenByDescending(x => x.Score.Score)
                .ThenBy(x => x.PairKey, StringComparer.Ordinal))
            {
                if (graph.Find(candidate.Left.Key) == graph.Find(candidate.Right.Key))
                {
                    candidate.Disposition = "component-connected";
                    continue;
                }
                if (graph.Union(candidate.Left.Key, candidate.Right.Key, (a, b) => CanMergeComponents(a, b, peopleByKey, rejected, false)))
                    candidate.Disposition = "automatic";
                else
                {
                    candidate.Disposition = "constraint-blocked";
                    Diagnostics.ConstraintBlockedCandidates++;
                }
            }

            foreach (var candidate in candidates.Where(x => string.IsNullOrWhiteSpace(x.Disposition)))
            {
                if (rejected.Contains(candidate.PairKey)) candidate.Disposition = "operator-rejected";
                else if (graph.Find(candidate.Left.Key) == graph.Find(candidate.Right.Key)) candidate.Disposition = "component-connected";
                else if (candidate.Score.Score >= settings.HumanReviewThreshold) candidate.Disposition = "human-review";
                else candidate.Disposition = "below-review";
            }

            Diagnostics.AutomaticCandidates = candidates.Count(x => x.Disposition == "automatic" || x.Disposition == "operator-confirmed");
            Diagnostics.ReviewCandidates = candidates.Count(x => x.Disposition == "human-review");
            Diagnostics.BelowReviewCandidates = candidates.Count(x => x.Disposition == "below-review" || x.Disposition == "operator-rejected" || x.Disposition == "constraint-blocked");
            PairEvaluations = candidates.OrderBy(x => x.PairKey, StringComparer.Ordinal).Select(ToPairEvaluation).ToList();

            var components = peopleByKey.Values.GroupBy(x => graph.Find(x.Key), StringComparer.Ordinal)
                .Select(x => x.OrderBy(y => y.Provider, StringComparer.Ordinal).ThenBy(y => y.ProviderId, StringComparer.Ordinal).ToList())
                .ToList();
            Diagnostics.GraphComponents = components.Count;
            var localIndex = new LocalIndex(input);
            var states = components.Select((component, index) => new ComponentState
            {
                Index = index,
                People = component,
                Anchors = RankLocalAnchors(component, localIndex),
                ProviderKeys = new HashSet<string>(component.Select(x => x.Key), StringComparer.Ordinal),
                MediaKeys = new HashSet<string>(component.SelectMany(x => x.CanonicalMediaKeys), StringComparer.Ordinal)
            }).ToList();
            var regions = BuildReconciliationRegions(states);

            var decisions = new List<ResolutionDecision>();
            var clusters = new List<ResolutionClusterSnapshot>();
            var resolvedLocalPeople = new HashSet<long>();
            var reviewCoveredProviderKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var region in regions)
            {
                foreach (var local in region.LocalPeople) resolvedLocalPeople.Add(local.EmbyId);
                if (region.Components.Count > 1 && region.LocalPeople.Count > 1)
                {
                    var decision = BuildRealignmentDecision(region, input, candidates, localIndex);
                    if (region.LocalPeople.All(x => EvidenceIsCompleteForLocal(x, input))) decisions.Add(decision);
                    foreach (var key in region.Components.SelectMany(x => x.ProviderKeys)) reviewCoveredProviderKeys.Add(key);
                }
                else if (region.Components.Count > 1 && region.LocalPeople.Count == 1)
                {
                    var local = region.LocalPeople[0];
                    var keys = region.Components.SelectMany(x => x.ProviderKeys).ToList();
                    var representedByReview = candidates.Any(x => x.Disposition == "human-review" && keys.Contains(x.Left.Key) && keys.Contains(x.Right.Key));
                    if (!representedByReview && EvidenceIsCompleteForLocal(local, input))
                        decisions.Add(BuildSplitDecision(local, region.Components.Select(x => x.People).ToList(), input, candidates, settings));
                }
                else
                {
                    var state = region.Components[0];
                    if (state.Anchors.Count > 0 && ShouldEmitComponentDecision(state.People, state.Anchors))
                    {
                        var decision = BuildComponentDecision(state.People, state.Anchors, input, candidates, settings);
                        if (state.Anchors.All(x => EvidenceIsCompleteForLocal(x.Person, input))) decisions.Add(decision);
                    }
                }
            }

            foreach (var region in regions)
            foreach (var state in region.Components)
            {
                if (state.Anchors.Count == 0) continue;
                if (state.People.Count <= 1 && state.Anchors.Count <= 1 && state.Anchors[0].Direct && region.Components.Count <= 1) continue;
                var providerKeys = state.ProviderKeys.OrderBy(x => x, StringComparer.Ordinal).ToList();
                clusters.Add(new ResolutionClusterSnapshot
                {
                    ClusterId = StableId("cluster", string.Join("|", providerKeys)),
                    ProviderKeys = providerKeys,
                    AnchorEmbyPersonId = state.Anchors[0].Person.EmbyId,
                    IdentityConfidence = ComponentIdentityConfidence(providerKeys, candidates),
                    LocalAnchorConfidence = AnchorConfidence(state.Anchors[0])
                });
            }
            Clusters = clusters.GroupBy(x => x.ClusterId, StringComparer.Ordinal).Select(x => x.First()).ToList();

            var reviewCandidates = candidates.Where(x => x.Disposition == "human-review").ToList();
            foreach (var review in reviewCandidates)
            {
                if (reviewCoveredProviderKeys.Contains(review.Left.Key) && reviewCoveredProviderKeys.Contains(review.Right.Key)) continue;
                var decision = BuildReviewDecision(review, input, settings);
                var local = decision.AnchorEmbyPersonId.HasValue ? input.LocalPeople.FirstOrDefault(x => x.EmbyId == decision.AnchorEmbyPersonId.Value) : null;
                if (local == null || EvidenceIsCompleteForLocal(local, input)) decisions.Add(decision);
            }

            foreach (var local in input.LocalPeople)
            {
                if (!resolvedLocalPeople.Contains(local.EmbyId) && input.LocalCredits.Any(x => x.PersonEmbyId == local.EmbyId) && EvidenceIsCompleteForLocal(local, input))
                    decisions.Add(BuildOrphanDecision(local, input, settings));
            }

            var result = decisions.GroupBy(x => x.DecisionId, StringComparer.Ordinal).Select(x => x.First()).ToList();
            ApplyGlobalScopeGuards(result, input);
            return result
                .OrderBy(x => StatusOrder(x.Status)).ThenBy(x => x.Confidence).ThenByDescending(x => x.ImpactedMediaCount)
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void ApplyGlobalScopeGuards(IEnumerable<ResolutionDecision> decisions, ResolutionInput input)
        {
            if (input.GlobalLocalPeople == null || input.GlobalLocalPeople.Count == 0) return;
            var inScope = new HashSet<long>(input.LocalPeople.Select(x => x.EmbyId));
            var providerPeople = input.ProviderPeople.GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
            foreach (var decision in decisions.Where(x => x.AnchorEmbyPersonId.HasValue))
            {
                var proposed = ProposedIdentityBindings(decision.ProviderKeys, providerPeople);
                if (proposed.Count == 0) continue;
                var collisions = new List<string>();
                foreach (var owner in input.GlobalLocalPeople.Where(x => x.EmbyId != decision.AnchorEmbyPersonId.Value && !inScope.Contains(x.EmbyId)))
                foreach (var binding in proposed)
                {
                    var current = LocalBinding(owner, binding.Key);
                    if (string.IsNullOrWhiteSpace(current) || !string.Equals(current, binding.Value, StringComparison.OrdinalIgnoreCase)) continue;
                    collisions.Add(binding.Key + ":" + binding.Value + " on Emby person " + owner.EmbyId.ToString(CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(owner.Name) ? string.Empty : " (" + owner.Name + ")"));
                }
                collisions = collisions.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                if (collisions.Count == 0) continue;

                decision.Action = ResolutionActions.IncompleteScope;
                decision.Headline = "The proposed identity is already held by an Emby person outside the evaluated scope.";
                decision.Explanation = "No Emby change is offered. Add the named person or relevant media IDs to the explicit sandbox scope and rerun, or use Full mode. PersonCleaner does not expand the cohort automatically.";
                decision.Evidence.Add(new EvidenceLine
                {
                    SortOrder = 0,
                    SignalType = "GLOBAL_BINDING_OWNER",
                    Verdict = "withheld",
                    Narrative = "Global Emby provider-binding safety check found " + string.Join("; ", collisions) + ".",
                    Metric = "owners=" + collisions.Count.ToString(CultureInfo.InvariantCulture)
                });
            }
        }

        private static Dictionary<string, string> ProposedIdentityBindings(string providerKeys, IReadOnlyDictionary<string, ProviderPerson> people)
        {
            var candidates = new List<KeyValuePair<string, string>>();
            foreach (var token in (providerKeys ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = token.IndexOf(':');
                if (separator <= 0 || separator == token.Length - 1) continue;
                var provider = token.Substring(0, separator).Trim().ToLowerInvariant();
                var id = token.Substring(separator + 1).Trim();
                if (provider == ProviderNames.Tmdb || provider == ProviderNames.Tvdb) candidates.Add(new KeyValuePair<string, string>(provider, id));
                if (!people.TryGetValue(provider + ":" + id, out var person)) continue;
                foreach (var external in person.ExternalIds)
                    if (external.Key == ProviderNames.Tmdb || external.Key == ProviderNames.Tvdb || external.Key == ProviderNames.Imdb)
                        candidates.Add(new KeyValuePair<string, string>(external.Key, external.Value));
            }
            return candidates.Where(x => !string.IsNullOrWhiteSpace(x.Value)).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Select(y => y.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                .ToDictionary(x => x.Key.ToLowerInvariant(), x => x.First().Value, StringComparer.OrdinalIgnoreCase);
        }

        private static string LocalBinding(LocalPerson person, string provider) => provider == ProviderNames.Tmdb ? person.TmdbId : provider == ProviderNames.Tvdb ? person.TvdbId : provider == ProviderNames.Imdb ? person.ImdbId : null;

        public static ScoreBreakdown Score(ProviderPerson left, ProviderPerson right, ResolutionSettings settings)
        {
            PreparePeople(new[] { left, right });
            var input = new ResolutionInput { ProviderPeople = new List<ProviderPerson> { left, right } };
            input.ProviderCredits.AddRange((left.Credits ?? new List<ObservedProviderCredit>()).Concat(right.Credits ?? new List<ObservedProviderCredit>()));
            PrepareCredits(input);
            return Score(left, right, input, 1);
        }

        private static ScoreBreakdown Score(ProviderPerson left, ProviderPerson right, ResolutionInput input, int nameFrequency)
        {
            var sharedKeys = new HashSet<string>(left.CanonicalMediaKeys.Intersect(right.CanonicalMediaKeys, StringComparer.Ordinal), StringComparer.Ordinal);
            var intersection = sharedKeys.Count;
            var union = left.CanonicalMediaKeys.Union(right.CanonicalMediaKeys, StringComparer.Ordinal).Count();
            var minimum = Math.Min(left.CanonicalMediaKeys.Count, right.CanonicalMediaKeys.Count);
            var jaccard = union == 0 ? 0 : intersection / (double)union;
            var containment = minimum == 0 ? 0 : intersection / (double)minimum;

            var birthdayKnown = !string.IsNullOrWhiteSpace(left.Birthday) && !string.IsNullOrWhiteSpace(right.Birthday);
            var birthdayMatch = birthdayKnown && string.Equals(left.Birthday, right.Birthday, StringComparison.Ordinal);
            var birthdayConflict = birthdayKnown && !birthdayMatch;
            var exactName = !string.IsNullOrWhiteSpace(left.CleanName) && string.Equals(left.CleanName, right.CleanName, StringComparison.Ordinal);
            var aliases = PersonNames(left);
            var otherAliases = PersonNames(right);
            var aliasMatch = aliases.Overlaps(otherAliases) && !exactName;

            var identifier = IdentifierEvidence(left, right);
            var roles = RoleEvidence(left, right, sharedKeys);
            var competing = CountCompetingAttributions(left, right, input, aliases.Union(otherAliases));
            var mediaAttributionDominant = (exactName || aliasMatch) && intersection > 0 && roles.Agreement > 0 && competing == 0;

            var rarity = 1.0 / Math.Sqrt(Math.Max(1, nameFrequency));
            var positiveScore = identifier.Match ? 1.0 :
                (exactName ? ExactNameContribution * rarity : aliasMatch ? AliasContribution * rarity : 0) +
                ContainmentContribution * containment +
                SharedCreditContribution * (1.0 - Math.Exp(-intersection)) +
                RoleContribution * roles.Agreement +
                (birthdayMatch ? BirthdayContribution : 0);

            var metadataPenalty = 0.0;
            if (identifier.Conflict) metadataPenalty += identifier.Match ? CorroboratedMetadataConflictPenalty : IdentifierConflictPenalty;
            if (birthdayConflict) metadataPenalty += identifier.Match ? CorroboratedMetadataConflictPenalty : BirthdayConflictPenalty;
            if (mediaAttributionDominant) metadataPenalty = Math.Min(metadataPenalty, DominantMediaConflictPenaltyCap);
            var score = positiveScore - metadataPenalty;
            if (competing > 0) score = Math.Min(score, 0.55);

            return new ScoreBreakdown
            {
                ModelVersion = EvidenceModelVersion,
                PositiveEvidenceScore = Math.Max(0, Math.Min(1, positiveScore)),
                MetadataConflictPenalty = metadataPenalty,
                FilmographyJaccard = jaccard,
                FilmographyContainment = containment,
                LeftMediaCount = left.CanonicalMediaKeys.Count,
                RightMediaCount = right.CanonicalMediaKeys.Count,
                SharedMediaCount = intersection,
                ExactRoleMatches = roles.Exact,
                CompatibleRoleMatches = roles.Compatible,
                RoleAgreement = roles.Agreement,
                CompetingAttributionCount = competing,
                NameFrequency = Math.Max(1, nameFrequency),
                BirthdayState = birthdayMatch ? "exact" : birthdayConflict ? "conflict" : "missing",
                BirthdayDetail = birthdayKnown ? left.Provider + ":" + left.Birthday + ";" + right.Provider + ":" + right.Birthday : null,
                ExternalIdState = identifier.Match && identifier.Conflict ? "mixed" : identifier.Match ? "exact" : identifier.Conflict ? "conflict" : identifier.Any ? "missing-opposite" : "missing",
                IdentifierMatchDetail = string.Join(";", identifier.Matches.Distinct(StringComparer.OrdinalIgnoreCase)),
                IdentifierConflictDetail = string.Join(";", identifier.Conflicts.Distinct(StringComparer.OrdinalIgnoreCase)),
                BirthdayMatch = birthdayMatch,
                BirthdayConflict = birthdayConflict,
                ExactNameMatch = exactName,
                AliasMatch = aliasMatch,
                HardIdentifierMatch = identifier.Match,
                StableIdentifierMatch = identifier.StableMatch,
                NativeProviderCrosswalkMatch = identifier.NativeCrosswalkMatch,
                IdentifierConflict = identifier.Conflict,
                MediaAttributionDominant = mediaAttributionDominant,
                Score = Math.Max(0, Math.Min(1, score))
            };
        }

        private List<Candidate> BuildCandidates(IEnumerable<ProviderPerson> people, ResolutionInput input, HashSet<string> rejected)
        {
            var allPeople = people.ToList();
            var tmdb = allPeople.Where(x => x.Provider == ProviderNames.Tmdb).ToList();
            var tvdb = allPeople.Where(x => x.Provider == ProviderNames.Tvdb).ToList();
            var byMedia = new Dictionary<string, List<ProviderPerson>>(StringComparer.Ordinal);
            var byExternal = new Dictionary<string, List<ProviderPerson>>(StringComparer.OrdinalIgnoreCase);
            foreach (var person in tvdb)
            {
                foreach (var media in person.CanonicalMediaKeys) AddIndex(byMedia, media, person);
                AddIndex(byExternal, person.Provider + ":" + person.ProviderId, person);
                foreach (var provider in NativePersonIdProviders)
                    if (person.ExternalIds.TryGetValue(provider, out var id) && !string.IsNullOrWhiteSpace(id)) AddIndex(byExternal, provider + ":" + id, person);
                foreach (var provider in StableIdProviders)
                    if (person.ExternalIds.TryGetValue(provider, out var id) && !string.IsNullOrWhiteSpace(id)) AddIndex(byExternal, provider + ":" + id, person);
            }

            var nameFrequency = allPeople.Where(x => !string.IsNullOrWhiteSpace(x.CleanName))
                .GroupBy(x => x.Provider + "|" + x.CleanName, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            var result = new List<Candidate>();
            foreach (var left in tmdb)
            {
                var possible = new HashSet<ProviderPerson>();
                foreach (var media in left.CanonicalMediaKeys) if (byMedia.TryGetValue(media, out var matches)) possible.UnionWith(matches);
                if (byExternal.TryGetValue(left.Provider + ":" + left.ProviderId, out var nativeMatches)) possible.UnionWith(nativeMatches);
                foreach (var provider in NativePersonIdProviders)
                    if (left.ExternalIds.TryGetValue(provider, out var id) && !string.IsNullOrWhiteSpace(id) && byExternal.TryGetValue(provider + ":" + id, out var matches)) possible.UnionWith(matches);
                foreach (var provider in StableIdProviders)
                    if (left.ExternalIds.TryGetValue(provider, out var id) && !string.IsNullOrWhiteSpace(id) && byExternal.TryGetValue(provider + ":" + id, out var matches)) possible.UnionWith(matches);
                foreach (var right in possible)
                {
                    Diagnostics.BlockedCrossProviderPairs++;
                    var frequency = Math.Max(Frequency(nameFrequency, left), Frequency(nameFrequency, right));
                    var score = Score(left, right, input, frequency);
                    if (score.HardIdentifierMatch || (score.SharedMediaCount > 0 && (score.ExactNameMatch || score.AliasMatch)))
                    {
                        if (score.HardIdentifierMatch) Diagnostics.HardIdentityCandidates++; else Diagnostics.NameCompatibleCandidates++;
                        var candidate = new Candidate { Left = left, Right = right, Score = score };
                        if (rejected.Contains(candidate.PairKey)) { candidate.Disposition = "operator-rejected"; Diagnostics.RejectedByOperator++; }
                        result.Add(candidate);
                    }
                }
            }
            return result;
        }

        private static Candidate FindOrAddManualCandidate(List<Candidate> candidates, ProviderPerson left, ProviderPerson right, ResolutionInput input)
        {
            var key = PairKey(left.Key, right.Key);
            var candidate = candidates.FirstOrDefault(x => x.PairKey == key);
            if (candidate != null) return candidate;
            candidate = new Candidate { Left = left, Right = right, Score = Score(left, right, input, 1), ManualOnly = true };
            candidates.Add(candidate);
            return candidate;
        }

        private static bool IsAutomatic(ScoreBreakdown score, ResolutionSettings settings)
        {
            if (score.CompetingAttributionCount > 0) return false;
            if (score.NativeProviderCrosswalkMatch && !score.StableIdentifierMatch && !score.MediaAttributionDominant) return false;
            if (score.HasMetadataConflict && score.MediaAttributionDominant && score.PositiveEvidenceScore >= settings.AutomaticMatchThreshold) return true;
            if (score.HasMetadataConflict)
                return (score.ExactNameMatch || score.AliasMatch || score.SharedMediaCount > 0) && score.Score >= settings.AutomaticMatchThreshold;
            return score.HardIdentifierMatch || score.Score >= settings.AutomaticMatchThreshold;
        }

        private static bool CanMergeComponents(IReadOnlyCollection<string> leftKeys, IReadOnlyCollection<string> rightKeys, IDictionary<string, ProviderPerson> people, HashSet<string> rejected, bool operatorConfirmed)
        {
            foreach (var leftKey in leftKeys)
            foreach (var rightKey in rightKeys)
            {
                if (rejected.Contains(PairKey(leftKey, rightKey))) return false;
                if (operatorConfirmed) continue;
                var left = people[leftKey]; var right = people[rightKey];
                if (left.Provider == right.Provider && left.ProviderId != right.ProviderId) return false;
            }
            return true;
        }

        private static List<Anchor> RankLocalAnchors(IReadOnlyCollection<ProviderPerson> component, LocalIndex index)
        {
            var providerKeys = new HashSet<string>(component.Select(x => x.Key), StringComparer.Ordinal);
            var mediaKeys = new HashSet<string>(component.SelectMany(x => x.CanonicalMediaKeys), StringComparer.Ordinal);
            var candidates = new HashSet<LocalPerson>();
            foreach (var key in providerKeys) if (index.ByProviderKey.TryGetValue(key, out var direct)) candidates.UnionWith(direct);
            foreach (var person in component)
            {
                if (person.ExternalIds.TryGetValue(ProviderNames.Imdb, out var imdb) && index.ByImdb.TryGetValue(imdb, out var direct)) candidates.UnionWith(direct);
                if (index.ByCleanName.TryGetValue(person.CleanName ?? string.Empty, out var sameName)) candidates.UnionWith(sameName);
            }
            var ranks = new List<Anchor>();
            foreach (var local in candidates)
            {
                var direct = CurrentProviderKeys(local).Any(providerKeys.Contains) || component.Any(x => !string.IsNullOrWhiteSpace(local.ImdbId) && x.ExternalIds.TryGetValue(ProviderNames.Imdb, out var imdb) && imdb == local.ImdbId);
                var credits = index.CreditsByPerson.TryGetValue(local.EmbyId, out var localCredits) ? localCredits : EmptyCredits;
                var mass = credits.Select(x => index.MediaKeysById.TryGetValue(x.MediaEmbyId, out var keys) && keys.Overlaps(mediaKeys) ? x.MediaEmbyId : 0).Where(x => x != 0).Distinct().Count();
                var nameCompatible = component.Any(x => x.CleanName == TextNormalizer.PersonName(local.Name));
                if (direct || (mass > 0 && nameCompatible)) ranks.Add(new Anchor { Person = local, Mass = mass, Direct = direct });
            }
            return ranks.OrderByDescending(x => x.Mass).ThenByDescending(x => x.Direct).ThenBy(x => x.Person.EmbyId).ToList();
        }

        private static List<ReconciliationRegion> BuildReconciliationRegions(IReadOnlyCollection<ComponentState> states)
        {
            var active = states.Where(x => x.Anchors.Count > 0).ToList();
            var byPerson = active.SelectMany(x => x.Anchors.Select(a => new { a.Person.EmbyId, State = x }))
                .GroupBy(x => x.EmbyId).ToDictionary(x => x.Key, x => x.Select(y => y.State).Distinct().ToList());
            var visited = new HashSet<int>();
            var result = new List<ReconciliationRegion>();
            foreach (var start in active.OrderBy(x => x.Index))
            {
                if (!visited.Add(start.Index)) continue;
                var queue = new Queue<ComponentState>(); queue.Enqueue(start);
                var components = new List<ComponentState>();
                var people = new Dictionary<long, LocalPerson>();
                while (queue.Count > 0)
                {
                    var state = queue.Dequeue(); components.Add(state);
                    foreach (var anchor in state.Anchors)
                    {
                        people[anchor.Person.EmbyId] = anchor.Person;
                        foreach (var neighbor in byPerson[anchor.Person.EmbyId])
                            if (visited.Add(neighbor.Index)) queue.Enqueue(neighbor);
                    }
                }
                result.Add(new ReconciliationRegion { Components = components.OrderBy(x => x.Index).ToList(), LocalPeople = people.Values.OrderBy(x => x.EmbyId).ToList() });
            }
            return result;
        }

        private static ResolutionDecision BuildRealignmentDecision(ReconciliationRegion region, ResolutionInput input, List<Candidate> candidates, LocalIndex index)
        {
            var ownerByComponent = new Dictionary<int, Anchor>();
            var ownerAmbiguity = 0;
            foreach (var state in region.Components)
            {
                var direct = state.Anchors.Where(x => x.Direct).ToList();
                if (direct.Count == 1) ownerByComponent[state.Index] = direct[0];
                else ownerAmbiguity++;
            }

            var assignments = new List<ResolutionCreditAssignment>();
            var ambiguousCredits = 0;
            var regionMediaKeys = new HashSet<string>(region.Components.SelectMany(x => x.MediaKeys), StringComparer.Ordinal);
            var componentsByMedia = region.Components.SelectMany(x => x.MediaKeys.Select(media => new { Media = media, Component = x }))
                .GroupBy(x => x.Media, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Select(y => y.Component).Distinct().ToList(), StringComparer.Ordinal);
            foreach (var local in region.LocalPeople)
            foreach (var credit in input.LocalCredits.Where(x => x.PersonEmbyId == local.EmbyId))
            {
                if (!index.MediaKeysById.TryGetValue(credit.MediaEmbyId, out var mediaKeys) || !mediaKeys.Overlaps(regionMediaKeys)) continue;
                var possible = mediaKeys.Where(componentsByMedia.ContainsKey).SelectMany(x => componentsByMedia[x]).Distinct().ToList();
                var matching = possible.Where(x => CreditMatchesComponent(local, credit, x, mediaKeys)).ToList();
                if (matching.Count != 1 || !ownerByComponent.TryGetValue(matching.FirstOrDefault()?.Index ?? -1, out var owner))
                {
                    ambiguousCredits++;
                    continue;
                }
                assignments.Add(new ResolutionCreditAssignment
                {
                    SourcePersonEmbyId = local.EmbyId,
                    TargetPersonEmbyId = owner.Person.EmbyId,
                    MediaEmbyId = credit.MediaEmbyId,
                    Role = credit.Role,
                    Disposition = local.EmbyId == owner.Person.EmbyId ? "KEEP" : "MOVE",
                    ComponentKey = string.Join(", ", matching[0].ProviderKeys.OrderBy(x => x, StringComparer.Ordinal)),
                    Rationale = "Canonical media, compatible person naming and role attribution resolve this credit to the component's direct Emby owner."
                });
            }

            var moves = assignments.Where(x => x.Disposition == "MOVE").ToList();
            var ownersDistinct = ownerByComponent.Count == region.Components.Count && ownerByComponent.Values.Select(x => x.Person.EmbyId).Distinct().Count() == region.Components.Count;
            var unresolvedBoundary = candidates.Any(x => x.Disposition == "human-review" && region.Components.Any(c => c.ProviderKeys.Contains(x.Left.Key)) && region.Components.Any(c => c.ProviderKeys.Contains(x.Right.Key)));
            var automatic = ownerAmbiguity == 0 && ownersDistinct && ambiguousCredits == 0 && moves.Count > 0 && !unresolvedBoundary;
            var providerKeys = region.Components.SelectMany(x => x.ProviderKeys).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var primary = moves.GroupBy(x => x.TargetPersonEmbyId).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).Select(x => (long?)x.Key).FirstOrDefault()
                ?? ownerByComponent.Values.Select(x => (long?)x.Person.EmbyId).OrderBy(x => x).FirstOrDefault();
            var names = string.Join(" / ", region.LocalPeople.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));
            var decision = new ResolutionDecision
            {
                DecisionId = StableId("realignment", string.Join("|", providerKeys) + "|" + string.Join("|", region.LocalPeople.Select(x => x.EmbyId).OrderBy(x => x))),
                Status = "REALIGNMENT",
                Action = automatic ? ResolutionActions.AutoRealignCredits : "HUMAN_REVIEW",
                DisplayName = string.IsNullOrWhiteSpace(names) ? "Local person reconciliation" : names,
                AnchorEmbyPersonId = primary,
                ProviderKeys = string.Join(", ", providerKeys),
                Confidence = automatic ? 1 : 0.5,
                LocalAnchorConfidence = ownerAmbiguity == 0 ? 1 : 0,
                Headline = automatic
                    ? moves.Count + " local credit relationship(s) should be redistributed between " + region.LocalPeople.Count + " existing Emby people while preserving each provider identity."
                    : "Multiple provider identities and Emby people overlap, but the exact local credit redistribution is not uniquely determined.",
                Explanation = automatic
                    ? "Each provider component has one distinct direct Emby owner, and every affected credit maps to exactly one component by canonical media, compatible naming and role attribution. Provider IDs remain unchanged."
                    : "No automatic mutation is offered unless every component has a distinct direct owner and every relevant local credit has one unambiguous component assignment.",
                CreditAssignments = assignments
            };
            decision.Evidence.Add(new EvidenceLine { SortOrder = 1, SignalType = "LOCAL_RECONCILIATION", Verdict = automatic ? "supports" : "review", Narrative = region.Components.Count + " provider component(s) and " + region.LocalPeople.Count + " Emby people form one connected local-attribution region; exact moves=" + moves.Count + ", ambiguous credits=" + ambiguousCredits + ", ambiguous owners=" + ownerAmbiguity + ".", Metric = "components=" + region.Components.Count + ";people=" + region.LocalPeople.Count + ";moves=" + moves.Count + ";ambiguous_credits=" + ambiguousCredits + ";ambiguous_owners=" + ownerAmbiguity });
            decision.Evidence.Add(new EvidenceLine { SortOrder = 2, SignalType = "COMPONENT_OWNERS", Verdict = ownersDistinct ? "supports" : "review", Narrative = ownerByComponent.Count == 0 ? "No component has a unique direct Emby owner." : string.Join("; ", ownerByComponent.OrderBy(x => x.Key).Select(x => string.Join(", ", region.Components.Single(c => c.Index == x.Key).ProviderKeys.OrderBy(y => y, StringComparer.Ordinal)) + " → Emby person " + x.Value.Person.EmbyId)), Metric = "resolved=" + ownerByComponent.Count + ";required=" + region.Components.Count });
            PopulateImpactedFromAssignments(decision, input);
            return decision;
        }

        private static bool CreditMatchesComponent(LocalPerson local, LocalCredit credit, ComponentState state, HashSet<string> mediaKeys)
        {
            if (!state.MediaKeys.Overlaps(mediaKeys)) return false;
            var cleanName = TextNormalizer.PersonName(local.Name);
            var nameCompatible = state.People.Any(x => PersonNames(x).Contains(cleanName));
            var direct = state.Anchors.Any(x => x.Person.EmbyId == local.EmbyId && x.Direct);
            if (!nameCompatible && !direct) return false;
            var providerCredits = state.People.SelectMany(x => x.Credits).Where(x => mediaKeys.Contains(x.CanonicalMediaKey)).ToList();
            if (providerCredits.Count == 0) return nameCompatible || direct;
            var localCredit = new ObservedProviderCredit { Role = credit.Role, RoleCategory = NormalizeRoleCategory(null, credit.Role), RoleName = RoleNameFromDisplay(credit.Role) };
            return providerCredits.Any(x => RoleCompatibility(localCredit, x) > 0);
        }

        private static List<ResolutionCreditAssignment> BuildComponentAssignments(List<ProviderPerson> component, List<Anchor> anchors, LocalPerson owner, ResolutionInput input, LocalIndex index)
        {
            var state = new ComponentState { People = component, Anchors = anchors, ProviderKeys = new HashSet<string>(component.Select(x => x.Key), StringComparer.Ordinal), MediaKeys = new HashSet<string>(component.SelectMany(x => x.CanonicalMediaKeys), StringComparer.Ordinal) };
            var result = new List<ResolutionCreditAssignment>();
            foreach (var anchor in anchors)
            foreach (var credit in input.LocalCredits.Where(x => x.PersonEmbyId == anchor.Person.EmbyId))
            {
                if (!index.MediaKeysById.TryGetValue(credit.MediaEmbyId, out var mediaKeys) || !CreditMatchesComponent(anchor.Person, credit, state, mediaKeys)) continue;
                result.Add(new ResolutionCreditAssignment { SourcePersonEmbyId = anchor.Person.EmbyId, TargetPersonEmbyId = owner.EmbyId, MediaEmbyId = credit.MediaEmbyId, Role = credit.Role, Disposition = anchor.Person.EmbyId == owner.EmbyId ? "KEEP" : "MOVE", ComponentKey = string.Join(", ", state.ProviderKeys.OrderBy(x => x, StringComparer.Ordinal)), Rationale = "This credit is attributable to the resolved provider component and its selected Emby owner." });
            }
            return result.GroupBy(x => x.SourcePersonEmbyId + "|" + x.TargetPersonEmbyId + "|" + x.MediaEmbyId + "|" + x.Role, StringComparer.Ordinal).Select(x => x.First()).ToList();
        }

        private static void PopulateImpactedFromAssignments(ResolutionDecision decision, ResolutionInput input)
        {
            var media = input.Media.ToDictionary(x => x.EmbyId);
            var moves = decision.CreditAssignments.Where(x => x.Disposition == "MOVE" && media.ContainsKey(x.MediaEmbyId)).ToList();
            decision.ImpactedMedia = moves.Select(x => new MediaExample { EmbyMediaId = x.MediaEmbyId, MediaType = media[x.MediaEmbyId].MediaType, DisplayName = media[x.MediaEmbyId].Name + (media[x.MediaEmbyId].Year.HasValue ? " (" + media[x.MediaEmbyId].Year.Value + ")" : string.Empty), Role = x.Role }).ToList();
            decision.MediaExamples = decision.ImpactedMedia.ToList();
            decision.ImpactedMediaCount = decision.ImpactedMedia.Select(x => x.EmbyMediaId).Distinct().Count();
        }

        private static bool ShouldEmitComponentDecision(IReadOnlyCollection<ProviderPerson> component, IReadOnlyList<Anchor> anchors)
        {
            return component.Count > 1 || anchors.Count > 1 || !anchors[0].Direct;
        }

        private static ResolutionDecision BuildComponentDecision(List<ProviderPerson> component, List<Anchor> anchors, ResolutionInput input, List<Candidate> candidates, ResolutionSettings settings)
        {
            var winner = anchors[0];
            var providerKeys = component.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var accepted = candidates.Where(x => providerKeys.Contains(x.Left.Key) && providerKeys.Contains(x.Right.Key) && AcceptedDisposition(x.Disposition)).ToList();
            var confidence = ComponentIdentityConfidence(providerKeys, candidates);
            var merge = anchors.Count > 1;
            var drift = !winner.Direct;
            var metadataConflict = accepted.Any(x => x.Score.HasMetadataConflict);
            var dominantAttribution = accepted.Any(x => x.Score.MediaAttributionDominant);
            var conflictCount = accepted.Sum(x => (x.Score.BirthdayConflict ? 1 : 0) + (x.Score.IdentifierConflict ? 1 : 0));
            var currentKeys = CurrentProviderKeys(winner.Person).ToList();
            var currentAcquisitions = CurrentAcquisitions(winner.Person, input);
            var confirmedAbsent = currentKeys.Count > 0 && currentKeys.All(x => currentAcquisitions.TryGetValue(x, out var acquisition) && acquisition.State == AcquisitionStates.Absent);
            var names = string.Join(" / ", component.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));
            var decision = new ResolutionDecision
            {
                DecisionId = StableId("cluster", string.Join("|", providerKeys)),
                Status = merge ? "MERGE" : drift ? "DRIFT" : metadataConflict ? "MATCH_WITH_CONFLICT" : "MATCH",
                Action = merge ? !winner.Direct ? "HUMAN_REVIEW" : metadataConflict ? "AUTO_MERGE_SHADOW_WITH_METADATA_CONFLICT" : "AUTO_MERGE_SHADOW" : drift ? (confirmedAbsent || currentKeys.Count == 0 ? "RETAINED_BY_MASS_ID_DRIFT" : "HUMAN_REVIEW") : metadataConflict ? "CROSS_PROVIDER_IDENTITY_WITH_METADATA_CONFLICT" : "CROSS_PROVIDER_IDENTITY",
                DisplayName = string.IsNullOrWhiteSpace(winner.Person.Name) ? names : winner.Person.Name,
                AnchorEmbyPersonId = winner.Person.EmbyId,
                ProviderKeys = string.Join(", ", providerKeys),
                Confidence = confidence,
                LocalAnchorConfidence = AnchorConfidence(winner),
                Headline = merge
                    ? anchors.Count + " Emby people contribute credits to one constrained provider identity; Emby person " + winner.Person.EmbyId + " is the selected owner."
                    : drift
                    ? confirmedAbsent
                    ? "The provider confirmed the current ID is absent, while " + winner.Mass + " sampled title(s) support a media-backed replacement identity for Emby person " + winner.Person.EmbyId + "."
                    : "The current provider key does not reach this media-backed identity; " + winner.Mass + " sampled title(s) support review of the proposed continuity for Emby person " + winner.Person.EmbyId + "."
                    : metadataConflict
                    ? component.Count + " provider profiles resolve to one identity despite " + conflictCount + " provider metadata disagreement(s); the disagreement remains visible for correction."
                    : component.Count + " provider profiles resolve to one identity anchored to Emby person " + winner.Person.EmbyId + ".",
                Explanation = merge
                    ? "Exact local credit assignments are calculated from this provider component and persisted with the decision; the preview does not infer source people from provider keys."
                    : drift
                    ? confirmedAbsent
                    ? "This is an upstream identifier drift proposal backed by authoritative absence of the current provider binding. Local-anchor confidence is reported separately from provider-identity confidence."
                    : "The current provider binding still exists or no current binding is available to invalidate. The media-backed alternative is retained for human review and no live Emby record is changed."
                    : metadataConflict
                    ? (dominantAttribution ? "Compatible normalized naming and role-aware shared-media attribution establish the identity with no competing same-envelope provider attribution. " : "Independent identifier, name, media and role evidence establishes the identity strongly enough to retain the link. ") + "Conflicting provider attributes reduce evidence strength but are not treated as logical separation constraints; no source metadata is changed."
                    : "Shared identifiers or role-compatible media evidence establish the provider identity; the Emby binding is evaluated independently."
            };
            var weakest = accepted.OrderBy(EdgeConfidence).FirstOrDefault();
            AddPairEvidence(decision, weakest?.Score);
            decision.Evidence.Add(new EvidenceLine { SortOrder = 50, SignalType = "LOCAL_MEDIA_ANCHOR", Verdict = "supports", Narrative = "Emby person " + winner.Person.EmbyId + " is the local anchor with " + winner.Mass + " matching sampled title(s); local-anchor confidence " + AnchorConfidence(winner).ToString("0.000", CultureInfo.InvariantCulture) + ".", Metric = "mass=" + winner.Mass + ";direct=" + winner.Direct.ToString().ToLowerInvariant() + ";confidence=" + AnchorConfidence(winner).ToString("0.000000", CultureInfo.InvariantCulture) });
            if (drift) decision.Evidence.Add(new EvidenceLine { SortOrder = 5, SignalType = "PROVIDER_ID_DRIFT", Verdict = "changed", Narrative = "None of the current Emby provider keys directly identifies this component; compatible naming and local-media mass establish the proposed continuity.", Metric = "direct_current_id=false" });
            AddAcquisitionEvidence(decision, winner.Person, input);
            AddMediaAcquisitionEvidence(decision, winner.Person, input);
            var manual = input.Bridges.FirstOrDefault(x => !x.IsRejected && providerKeys.Contains(x.ProviderA + ":" + x.ProviderIdA) && providerKeys.Contains(x.ProviderB + ":" + x.ProviderIdB));
            if (manual != null) decision.Evidence.Add(new EvidenceLine { SortOrder = 2, SignalType = "OPERATOR_BRIDGE", Verdict = "proves", Narrative = "An operator explicitly confirmed this cross-provider alignment.", Metric = manual.ProviderA + ":" + manual.ProviderIdA + "=" + manual.ProviderB + ":" + manual.ProviderIdB });
            if (merge)
            {
                decision.CreditAssignments = BuildComponentAssignments(component, anchors, winner.Person, input, new LocalIndex(input));
                PopulateImpactedFromAssignments(decision, input);
                var moves = decision.CreditAssignments.Count(x => x.Disposition == "MOVE");
                decision.Evidence.Add(new EvidenceLine { SortOrder = 4, SignalType = "CREDIT_ASSIGNMENT_PLAN", Verdict = moves > 0 ? "supports" : "review", Narrative = moves + " exact local credit move(s) were derived for the selected component owner.", Metric = "moves=" + moves + ";assignments=" + decision.CreditAssignments.Count });
            }
            else AddMediaExamplesForComponent(decision, anchors.Select(x => x.Person.EmbyId), component, input, settings.MaximumMediaExamples);
            return decision;
        }

        private static ResolutionDecision BuildReviewDecision(Candidate candidate, ResolutionInput input, ResolutionSettings settings)
        {
            var conflict = candidate.Score.HasMetadataConflict || candidate.Score.CompetingAttributionCount > 0;
            var uncorroboratedNativeCrosswalk = candidate.Score.NativeProviderCrosswalkMatch && !candidate.Score.StableIdentifierMatch && !candidate.Score.MediaAttributionDominant;
            var decision = new ResolutionDecision
            {
                DecisionId = StableId("review", candidate.PairKey),
                Status = "CONFLATION",
                Action = "HUMAN_REVIEW",
                DisplayName = candidate.Left.Name + " / " + candidate.Right.Name,
                ProviderKeys = candidate.Left.Key + ", " + candidate.Right.Key,
                Confidence = candidate.Score.Score,
                Headline = uncorroboratedNativeCrosswalk
                    ? "A provider-native person cross-reference exists, but role-aware shared-media attribution does not corroborate it."
                    : conflict
                    ? "The profiles share identity evidence, but a conflicting or competing attribution prevents an automatic join."
                    : "The providers share " + candidate.Score.SharedMediaCount + " role-aware title attribution(s), but the evidence strength remains below the automatic threshold.",
                Explanation = uncorroboratedNativeCrosswalk
                    ? "Native provider cross-references are useful candidate evidence but cannot establish identity alone; review compatible naming and media attribution before confirming the link."
                    : conflict
                    ? "Review the explicit conflict below. Missing fields are neutral; only observed disagreement or competing attribution reduces confidence."
                    : "Containment, evidence count, name rarity and role agreement are evaluated independently; missing provider fields do not reduce the score."
            };
            AddPairEvidence(decision, candidate.Score);
            var localIds = input.LocalPeople.Where(x => CurrentProviderKeys(x).Contains(candidate.Left.Key) || CurrentProviderKeys(x).Contains(candidate.Right.Key)).Select(x => x.EmbyId).Distinct().ToList();
            if (localIds.Count == 1) { decision.AnchorEmbyPersonId = localIds[0]; decision.LocalAnchorConfidence = 1; }
            AddMediaExamples(decision, localIds, input, settings.MaximumMediaExamples);
            return decision;
        }

        private static ResolutionDecision BuildSplitDecision(LocalPerson local, List<List<ProviderPerson>> components, ResolutionInput input, List<Candidate> candidates, ResolutionSettings settings)
        {
            var keys = components.SelectMany(x => x).Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var decision = new ResolutionDecision
            {
                DecisionId = StableId("split", local.EmbyId + "|" + string.Join("|", keys)),
                Status = "SPLIT",
                Action = "FORCE_SPLIT_REVIEW",
                DisplayName = local.Name,
                AnchorEmbyPersonId = local.EmbyId,
                ProviderKeys = string.Join(", ", keys),
                Confidence = 0,
                LocalAnchorConfidence = 1,
                Headline = "Emby person " + local.EmbyId + " points at " + components.Count + " constraint-separated provider identities.",
                Explanation = "The records were not joined because evidence is insufficient, an observed conflict exists, or an operator rejection must remain valid through transitive clustering."
            };
            decision.Evidence.Add(new EvidenceLine { SortOrder = 1, SignalType = "DISCONNECTED_GRAPH", Verdict = "conflicts", Narrative = components.Count + " independent provider components are attached to one Emby person.", Metric = "components=" + components.Count });
            var relevant = candidates.Where(x => keys.Contains(x.Left.Key) && keys.Contains(x.Right.Key)).OrderBy(x => x.Score.Score).FirstOrDefault();
            AddPairEvidence(decision, relevant?.Score);
            AddMediaExamples(decision, new[] { local.EmbyId }, input, settings.MaximumMediaExamples);
            return decision;
        }

        private static ResolutionDecision BuildOrphanDecision(LocalPerson local, ResolutionInput input, ResolutionSettings settings)
        {
            var currentKeys = CurrentProviderKeys(local).ToList();
            var acquisitions = CurrentAcquisitions(local, input);
            var absent = currentKeys.Where(x => acquisitions.TryGetValue(x, out var acquisition) && acquisition.State == AcquisitionStates.Absent).ToList();
            var confirmedStale = absent.Count > 0;
            var decision = new ResolutionDecision
            {
                DecisionId = StableId("orphan", local.EmbyId.ToString(CultureInfo.InvariantCulture)),
                Status = "ORPHAN",
                Action = "HUMAN_REVIEW",
                DisplayName = local.Name,
                AnchorEmbyPersonId = local.EmbyId,
                ProviderKeys = "No hydrated provider identity",
                Confidence = confirmedStale ? 1 : 0,
                LocalAnchorConfidence = 0,
                Headline = confirmedStale
                    ? "The provider confirmed " + string.Join(", ", absent) + " does not exist, and no media-backed replacement identity was found in the current sample."
                    : currentKeys.Count == 0
                    ? "No current TMDB/TVDB person ID or hydrated provider identity supports this Emby person in the current sample."
                    : "The current provider ID exists, but no media-backed provider identity supports this Emby person in the current sample.",
                Explanation = confirmedStale
                    ? "Review removal of only the provider binding(s) explicitly confirmed absent. This does not recommend deleting the Emby person, and no live Emby record is changed."
                    : "This is a media-attribution review state, not a provider-ID removal instruction. All required current-ID acquisitions supplied usable answers."
            };
            decision.Action = confirmedStale ? "REVIEW_REMOVE_STALE_PROVIDER_ID" : "HUMAN_REVIEW";
            decision.Evidence.Add(new EvidenceLine { SortOrder = 1, SignalType = "NO_PROVIDER_SUPPORT", Verdict = "missing", Narrative = "The media-derived provider graph contains no matching identity node.", Metric = "provider_nodes=0" });
            AddAcquisitionEvidence(decision, local, input);
            AddMediaAcquisitionEvidence(decision, local, input);
            AddMediaExamples(decision, new[] { local.EmbyId }, input, settings.MaximumMediaExamples);
            return decision;
        }

        private static void AddPairEvidence(ResolutionDecision decision, ScoreBreakdown score)
        {
            if (score == null) return;
            decision.Evidence.Add(new EvidenceLine { SortOrder = 10, SignalType = "FILMOGRAPHY", Verdict = score.SharedMediaCount > 0 ? "supports" : "neutral", Narrative = score.SharedMediaCount + " shared canonical title(s); containment " + score.FilmographyContainment.ToString("0.000", CultureInfo.InvariantCulture) + "; Jaccard " + score.FilmographyJaccard.ToString("0.000", CultureInfo.InvariantCulture) + ". Unmatched titles are not negative evidence.", Metric = "shared=" + score.SharedMediaCount + ";left=" + score.LeftMediaCount + ";right=" + score.RightMediaCount + ";containment=" + score.FilmographyContainment.ToString("0.000000", CultureInfo.InvariantCulture) + ";jaccard=" + score.FilmographyJaccard.ToString("0.000000", CultureInfo.InvariantCulture) });
            decision.Evidence.Add(new EvidenceLine { SortOrder = 15, SignalType = "ROLE_AGREEMENT", Verdict = score.RoleAgreement > 0 ? "supports" : "unknown", Narrative = score.ExactRoleMatches + " exact and " + score.CompatibleRoleMatches + " compatible shared-title role match(es).", Metric = "exact=" + score.ExactRoleMatches + ";compatible=" + score.CompatibleRoleMatches + ";agreement=" + score.RoleAgreement.ToString("0.000000", CultureInfo.InvariantCulture) });
            decision.Evidence.Add(new EvidenceLine { SortOrder = 20, SignalType = "BIRTHDAY", Verdict = score.BirthdayConflict ? "conflicts" : score.BirthdayMatch ? "supports" : "missing", Narrative = score.BirthdayConflict ? "Both providers supplied different birth dates (" + score.BirthdayDetail + "). This is negative metadata evidence, not by itself proof of separate identities." : score.BirthdayMatch ? "Both providers supplied the same birth date (" + score.BirthdayDetail + ")." : "A comparable birth date was not available; this contributes neither support nor a penalty.", Metric = score.BirthdayState + (string.IsNullOrWhiteSpace(score.BirthdayDetail) ? string.Empty : ";" + score.BirthdayDetail) });
            decision.Evidence.Add(new EvidenceLine { SortOrder = 25, SignalType = "EXTERNAL_ID", Verdict = score.IdentifierConflict ? score.HardIdentifierMatch ? "mixed" : "conflicts" : score.HardIdentifierMatch ? "proves" : "missing", Narrative = score.IdentifierConflict && score.HardIdentifierMatch ? "Identity support (" + score.IdentifierMatchDetail + ") coexists with an external-ID disagreement (" + score.IdentifierConflictDetail + "); the disagreement reduces evidence strength but does not erase the independent match." : score.IdentifierConflict ? "The profiles supply different known values in a comparable external-ID namespace (" + score.IdentifierConflictDetail + "); this is negative evidence." : score.NativeProviderCrosswalkMatch ? "A provider explicitly cross-references the other provider's person ID (" + score.IdentifierMatchDetail + ")." : score.HardIdentifierMatch ? "The profiles share a stable IMDb or Wikidata identifier (" + score.IdentifierMatchDetail + ")." : "No comparable stable person identifier was available; this is neutral.", Metric = score.ExternalIdState + (string.IsNullOrWhiteSpace(score.IdentifierMatchDetail) ? string.Empty : ";matches=" + score.IdentifierMatchDetail) + (string.IsNullOrWhiteSpace(score.IdentifierConflictDetail) ? string.Empty : ";conflicts=" + score.IdentifierConflictDetail) });
            decision.Evidence.Add(new EvidenceLine { SortOrder = 30, SignalType = "NAME", Verdict = score.ExactNameMatch || score.AliasMatch ? "supports" : "neutral", Narrative = score.ExactNameMatch ? "Normalized primary names match exactly; cohort frequency " + score.NameFrequency + "." : score.AliasMatch ? "A provider alias matches the other provider's name." : "Names did not add positive evidence.", Metric = (score.ExactNameMatch ? "exact" : score.AliasMatch ? "alias" : "none") + ";frequency=" + score.NameFrequency });
            if (score.CompetingAttributionCount > 0) decision.Evidence.Add(new EvidenceLine { SortOrder = 5, SignalType = "COMPETING_ATTRIBUTION", Verdict = "conflicts", Narrative = score.CompetingAttributionCount + " same-envelope, role-compatible attribution(s) point to a different provider person on observed media.", Metric = "count=" + score.CompetingAttributionCount });
            if (score.MediaAttributionDominant) decision.Evidence.Add(new EvidenceLine { SortOrder = 8, SignalType = "MEDIA_ATTRIBUTION_DOMINANCE", Verdict = "supports", Narrative = "Compatible normalized naming and role-aware shared-media attribution identify this pair, with no competing same-envelope person attribution in the observed provider credits.", Metric = "dominant=true;shared=" + score.SharedMediaCount + ";role_agreement=" + score.RoleAgreement.ToString("0.000000", CultureInfo.InvariantCulture) + ";competing=0" });
            decision.Evidence.Add(new EvidenceLine { SortOrder = 40, SignalType = "EVIDENCE_MODEL", Verdict = "info", Narrative = "Evidence strength is a deterministic, versioned decision score rather than a calibrated probability. Missing observations are neutral; metadata disagreements reduce the score, while dominant role-aware media attribution and structural cluster constraints are evaluated explicitly.", Metric = "model=" + score.ModelVersion + ";positive=" + score.PositiveEvidenceScore.ToString("0.000000", CultureInfo.InvariantCulture) + ";metadata_penalty=" + score.MetadataConflictPenalty.ToString("0.000000", CultureInfo.InvariantCulture) + ";score=" + score.Score.ToString("0.000000", CultureInfo.InvariantCulture) });
        }

        private static void AddMediaExamples(ResolutionDecision decision, IEnumerable<long> people, ResolutionInput input, int maximum)
        {
            var ids = new HashSet<long>(people);
            var media = input.Media.ToDictionary(x => x.EmbyId);
            var examples = input.LocalCredits.Where(x => ids.Contains(x.PersonEmbyId) && media.ContainsKey(x.MediaEmbyId))
                .GroupBy(x => x.MediaEmbyId).Select(x => new { Credit = x.First(), Media = media[x.Key] })
                .OrderBy(x => x.Media.MediaType).ThenBy(x => x.Media.Name, StringComparer.OrdinalIgnoreCase).ToList();
            decision.ImpactedMediaCount = examples.Count;
            decision.ImpactedMedia = examples.Select(x => new MediaExample { EmbyMediaId = x.Media.EmbyId, MediaType = x.Media.MediaType, DisplayName = x.Media.Name + (x.Media.Year.HasValue ? " (" + x.Media.Year.Value + ")" : string.Empty), Role = x.Credit.Role }).ToList();
            decision.MediaExamples = decision.ImpactedMedia.Take(Math.Max(0, maximum)).ToList();
            if (examples.Count > decision.MediaExamples.Count) decision.Evidence.Add(new EvidenceLine { SortOrder = 99, SignalType = "MEDIA_SCOPE", Verdict = "info", Narrative = decision.MediaExamples.Count + " representative titles are shown; " + (examples.Count - decision.MediaExamples.Count) + " more are retained in the database.", Metric = "total=" + examples.Count });
        }

        private static void AddMediaExamplesForComponent(ResolutionDecision decision, IEnumerable<long> people, IEnumerable<ProviderPerson> component, ResolutionInput input, int maximum)
        {
            var ids = new HashSet<long>(people);
            var componentMedia = new HashSet<string>(component.SelectMany(x => x.CanonicalMediaKeys), StringComparer.Ordinal);
            var media = input.Media.ToDictionary(x => x.EmbyId);
            var examples = input.LocalCredits.Where(x => ids.Contains(x.PersonEmbyId) && media.ContainsKey(x.MediaEmbyId) && MediaKeys(media[x.MediaEmbyId]).Overlaps(componentMedia))
                .GroupBy(x => x.MediaEmbyId).Select(x => new { Credit = x.First(), Media = media[x.Key] })
                .OrderBy(x => x.Media.MediaType).ThenBy(x => x.Media.Name, StringComparer.OrdinalIgnoreCase).ToList();
            decision.ImpactedMediaCount = examples.Count;
            decision.ImpactedMedia = examples.Select(x => new MediaExample { EmbyMediaId = x.Media.EmbyId, MediaType = x.Media.MediaType, DisplayName = x.Media.Name + (x.Media.Year.HasValue ? " (" + x.Media.Year.Value + ")" : string.Empty), Role = x.Credit.Role }).ToList();
            decision.MediaExamples = decision.ImpactedMedia.Take(Math.Max(0, maximum)).ToList();
            if (examples.Count > decision.MediaExamples.Count) decision.Evidence.Add(new EvidenceLine { SortOrder = 99, SignalType = "MEDIA_SCOPE", Verdict = "info", Narrative = decision.MediaExamples.Count + " representative component title(s) are shown; " + (examples.Count - decision.MediaExamples.Count) + " more are retained in the database.", Metric = "total=" + examples.Count });
        }

        private static void PreparePeople(IEnumerable<ProviderPerson> people)
        {
            foreach (var person in people ?? new ProviderPerson[0])
            {
                if (person == null) continue;
                person.CleanName = TextNormalizer.PersonName(string.IsNullOrWhiteSpace(person.CleanName) ? person.Name : person.CleanName);
                person.Aliases = (person.Aliases ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                person.ExternalIds = person.ExternalIds ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                person.CanonicalMediaKeys = person.CanonicalMediaKeys ?? new HashSet<string>(StringComparer.Ordinal);
                person.Credits = person.Credits ?? new List<ObservedProviderCredit>();
            }
        }

        private static void PrepareCredits(ResolutionInput input)
        {
            input.ProviderCredits = input.ProviderCredits ?? new List<ObservedProviderCredit>();
            if (input.ProviderCredits.Count == 0) input.ProviderCredits.AddRange(input.ProviderPeople.SelectMany(x => x.Credits ?? new List<ObservedProviderCredit>()));
            foreach (var credit in input.ProviderCredits)
            {
                credit.CleanPersonName = TextNormalizer.PersonName(string.IsNullOrWhiteSpace(credit.CleanPersonName) ? credit.PersonName : credit.CleanPersonName);
                credit.RoleCategory = NormalizeRoleCategory(credit.RoleCategory, credit.Role);
                credit.RoleName = string.IsNullOrWhiteSpace(credit.RoleName) ? RoleNameFromDisplay(credit.Role) : credit.RoleName;
            }
            var byPerson = input.ProviderPeople.ToDictionary(x => x.Key, StringComparer.Ordinal);
            foreach (var credit in input.ProviderCredits)
                if (byPerson.TryGetValue(credit.PersonKey, out var person) && !person.Credits.Contains(credit)) person.Credits.Add(credit);
        }

        private static IdentifierResult IdentifierEvidence(ProviderPerson left, ProviderPerson right)
        {
            var result = new IdentifierResult();
            CompareNativeCrosswalk(left, right, result);
            CompareNativeCrosswalk(right, left, result);
            foreach (var provider in StableIdProviders)
            {
                var hasLeft = left.ExternalIds.TryGetValue(provider, out var a) && !string.IsNullOrWhiteSpace(a);
                var hasRight = right.ExternalIds.TryGetValue(provider, out var b) && !string.IsNullOrWhiteSpace(b);
                result.Any |= hasLeft || hasRight;
                if (!hasLeft || !hasRight) continue;
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) { result.Match = true; result.StableMatch = true; result.Matches.Add(provider + ":" + a); }
                else { result.Conflict = true; result.Conflicts.Add(provider + ":" + a + "!=" + b); }
            }
            return result;
        }

        private static void CompareNativeCrosswalk(ProviderPerson source, ProviderPerson target, IdentifierResult result)
        {
            if (!source.ExternalIds.TryGetValue(target.Provider, out var externalId) || string.IsNullOrWhiteSpace(externalId)) return;
            result.Any = true;
            if (string.Equals(externalId, target.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                result.Match = true;
                result.NativeCrosswalkMatch = true;
                result.Matches.Add(source.Provider + "->" + target.Provider + ":" + externalId);
            }
            else { result.Conflict = true; result.Conflicts.Add(source.Provider + "->" + target.Provider + ":" + externalId + "!=" + target.ProviderId); }
        }

        private static RoleResult RoleEvidence(ProviderPerson left, ProviderPerson right, HashSet<string> sharedKeys)
        {
            var exact = 0; var compatible = 0;
            foreach (var key in sharedKeys)
            {
                var leftCredits = left.Credits.Where(x => x.CanonicalMediaKey == key).ToList();
                var rightCredits = right.Credits.Where(x => x.CanonicalMediaKey == key).ToList();
                var best = 0;
                foreach (var a in leftCredits) foreach (var b in rightCredits) best = Math.Max(best, RoleCompatibility(a, b));
                if (best == 2) exact++; else if (best == 1) compatible++;
            }
            return new RoleResult { Exact = exact, Compatible = compatible, Agreement = sharedKeys.Count == 0 ? 0 : (exact + compatible * 0.67) / sharedKeys.Count };
        }

        private static int CountCompetingAttributions(ProviderPerson left, ProviderPerson right, ResolutionInput input, IEnumerable<string> compatibleNames)
        {
            var names = new HashSet<string>(compatibleNames.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.Ordinal);
            var credits = input.ProviderCredits ?? new List<ObservedProviderCredit>();
            var conflicts = new HashSet<string>(StringComparer.Ordinal);
            CountCompeting(left, right, credits, names, conflicts);
            CountCompeting(right, left, credits, names, conflicts);
            return conflicts.Count;
        }

        private static void CountCompeting(ProviderPerson source, ProviderPerson expected, IEnumerable<ObservedProviderCredit> allCredits, HashSet<string> names, HashSet<string> conflicts)
        {
            foreach (var sourceCredit in source.Credits)
            foreach (var other in allCredits.Where(x => x.Provider == expected.Provider && x.CanonicalMediaKey == sourceCredit.CanonicalMediaKey && x.PersonKey != expected.Key && names.Contains(x.CleanPersonName)))
                if (RoleCompatibility(sourceCredit, other) > 0) conflicts.Add(other.PersonKey + "|" + other.CanonicalMediaKey);
        }

        private static int RoleCompatibility(ObservedProviderCredit left, ObservedProviderCredit right)
        {
            var leftName = TextNormalizer.PersonName(left.RoleName);
            var rightName = TextNormalizer.PersonName(right.RoleName);
            var categoryMatch = !string.IsNullOrWhiteSpace(left.RoleCategory) && left.RoleCategory != "Unknown" && string.Equals(left.RoleCategory, right.RoleCategory, StringComparison.OrdinalIgnoreCase);
            if (categoryMatch && leftName.Length > 0 && leftName == rightName) return 2;
            if (leftName.Length > 0 && leftName == rightName) return 2;
            return categoryMatch ? 1 : 0;
        }

        private static string NormalizeRoleCategory(string category, string role)
        {
            if (!string.IsNullOrWhiteSpace(category) && !category.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return category.Trim();
            var value = role ?? string.Empty;
            if (value.StartsWith("Actor", StringComparison.OrdinalIgnoreCase) || value.StartsWith("Guest Star", StringComparison.OrdinalIgnoreCase)) return "Actor";
            if (value.IndexOf("Director", StringComparison.OrdinalIgnoreCase) >= 0) return "Director";
            if (value.IndexOf("Writer", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Screenplay", StringComparison.OrdinalIgnoreCase) >= 0) return "Writer";
            if (value.IndexOf("Producer", StringComparison.OrdinalIgnoreCase) >= 0) return "Producer";
            if (value.IndexOf("Creator", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Showrunner", StringComparison.OrdinalIgnoreCase) >= 0) return "Creator";
            return "Unknown";
        }

        private static string RoleNameFromDisplay(string role)
        {
            var value = role ?? string.Empty;
            var separator = value.IndexOf(':');
            return separator >= 0 ? value.Substring(separator + 1).Trim() : value.Trim();
        }

        private static HashSet<string> PersonNames(ProviderPerson person)
        {
            var result = new HashSet<string>((person.Aliases ?? new List<string>()).Select(TextNormalizer.PersonName).Where(x => x.Length > 0), StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(person.CleanName)) result.Add(person.CleanName);
            return result;
        }

        private static double ComponentIdentityConfidence(IReadOnlyCollection<string> providerKeys, IEnumerable<Candidate> candidates)
        {
            var accepted = candidates.Where(x => providerKeys.Contains(x.Left.Key) && providerKeys.Contains(x.Right.Key) && AcceptedDisposition(x.Disposition)).ToList();
            return accepted.Count == 0 ? 0.5 : accepted.Min(EdgeConfidence);
        }

        private static double EdgeConfidence(Candidate candidate) => candidate.Disposition == "operator-confirmed" ? 1 : candidate.Score.Score;
        private static bool AcceptedDisposition(string value) => value == "automatic" || value == "operator-confirmed";
        private static double AnchorConfidence(Anchor anchor) => anchor.Direct ? 1 : anchor.Mass <= 0 ? 0 : anchor.Mass / (anchor.Mass + 1.0);

        private static ResolutionPairEvaluation ToPairEvaluation(Candidate candidate) => new ResolutionPairEvaluation
        {
            PairId = StableId("pair", candidate.PairKey),
            LeftProvider = candidate.Left.Provider,
            LeftProviderId = candidate.Left.ProviderId,
            RightProvider = candidate.Right.Provider,
            RightProviderId = candidate.Right.ProviderId,
            Disposition = candidate.Disposition ?? "unknown",
            Score = candidate.Score
        };

        private static int Frequency(IDictionary<string, int> frequencies, ProviderPerson person)
        {
            return frequencies.TryGetValue(person.Provider + "|" + person.CleanName, out var value) ? value : 1;
        }

        private static HashSet<string> MediaKeys(MediaSeed media)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(media.ImdbId)) keys.Add("imdb:" + media.ImdbId);
            if (!string.IsNullOrWhiteSpace(media.TmdbId)) keys.Add("tmdb:" + media.MediaType + ":" + media.TmdbId);
            if (!string.IsNullOrWhiteSpace(media.TvdbId)) keys.Add("tvdb:" + media.MediaType + ":" + media.TvdbId);
            return keys;
        }

        private static IEnumerable<string> CurrentProviderKeys(LocalPerson person)
        {
            if (!string.IsNullOrWhiteSpace(person.TmdbId)) yield return ProviderNames.Tmdb + ":" + person.TmdbId;
            if (!string.IsNullOrWhiteSpace(person.TvdbId)) yield return ProviderNames.Tvdb + ":" + person.TvdbId;
        }

        private static Dictionary<string, PersonAcquisition> CurrentAcquisitions(LocalPerson person, ResolutionInput input)
        {
            var current = new HashSet<string>(CurrentProviderKeys(person), StringComparer.Ordinal);
            return input.PersonAcquisitions.Where(x => current.Contains(x.Key)).GroupBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Last()).ToDictionary(x => x.Key, StringComparer.Ordinal);
        }

        private static bool CurrentBindingsAreUsable(LocalPerson person, ResolutionInput input)
        {
            var keys = CurrentProviderKeys(person).ToList();
            if (keys.Count == 0) return true;
            var acquisitions = CurrentAcquisitions(person, input);
            if (!input.AcquisitionTrackingEnabled && acquisitions.Count == 0) return true;
            return keys.All(x => acquisitions.TryGetValue(x, out var acquisition) && acquisition.State != AcquisitionStates.Unavailable);
        }

        private static bool EvidenceIsCompleteForLocal(LocalPerson person, ResolutionInput input)
        {
            return CurrentBindingsAreUsable(person, input) && MediaAcquisitionsAreUsable(person, input);
        }

        private static bool MediaAcquisitionsAreUsable(LocalPerson person, ResolutionInput input)
        {
            if (!input.AcquisitionTrackingEnabled && input.MediaAcquisitions.Count == 0) return true;
            var required = RequiredMediaAcquisitionKeys(person, input);
            var observed = input.MediaAcquisitions.GroupBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Last()).ToDictionary(x => x.Key, StringComparer.Ordinal);
            return required.All(x => observed.TryGetValue(x, out var acquisition) && acquisition.State != AcquisitionStates.Unavailable);
        }

        private static List<string> RequiredMediaAcquisitionKeys(LocalPerson person, ResolutionInput input)
        {
            var creditedMedia = new HashSet<long>(input.LocalCredits.Where(x => x.PersonEmbyId == person.EmbyId).Select(x => x.MediaEmbyId));
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var media in input.Media.Where(x => creditedMedia.Contains(x.EmbyId)))
            {
                if (!string.IsNullOrWhiteSpace(media.TmdbId)) keys.Add(ProviderNames.Tmdb + ":" + media.MediaType + ":" + media.TmdbId);
                if (!string.IsNullOrWhiteSpace(media.TvdbId)) keys.Add(ProviderNames.Tvdb + ":" + media.MediaType + ":" + media.TvdbId);
            }
            return keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        private static void AddAcquisitionEvidence(ResolutionDecision decision, LocalPerson person, ResolutionInput input)
        {
            var keys = CurrentProviderKeys(person).ToList();
            if (keys.Count == 0)
            {
                decision.Evidence.Add(new EvidenceLine { SortOrder = 3, SignalType = "CURRENT_ID_ACQUISITION", Verdict = "missing", Narrative = "The Emby person has no current TMDB/TVDB person ID to validate.", Metric = "state=no-current-id" });
                return;
            }
            var acquisitions = CurrentAcquisitions(person, input);
            var order = 3;
            foreach (var key in keys)
            {
                if (!acquisitions.TryGetValue(key, out var acquisition)) continue;
                var narrative = acquisition.State == AcquisitionStates.Present
                    ? "The provider supplied usable person data for the current Emby binding " + key + "."
                    : "The provider authoritatively confirmed that the current Emby binding " + key + " does not exist.";
                decision.Evidence.Add(new EvidenceLine { SortOrder = order++, SignalType = "CURRENT_ID_ACQUISITION", Verdict = acquisition.State == AcquisitionStates.Present ? "present" : "absent", Narrative = narrative, Metric = "key=" + key + ";state=" + acquisition.State + ";source=" + acquisition.Source });
            }
        }

        private static void AddMediaAcquisitionEvidence(ResolutionDecision decision, LocalPerson person, ResolutionInput input)
        {
            if (!input.AcquisitionTrackingEnabled) return;
            var required = RequiredMediaAcquisitionKeys(person, input);
            var observed = input.MediaAcquisitions.GroupBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Last()).ToDictionary(x => x.Key, StringComparer.Ordinal);
            var present = required.Count(x => observed.TryGetValue(x, out var acquisition) && acquisition.State == AcquisitionStates.Present);
            var absent = required.Count(x => observed.TryGetValue(x, out var acquisition) && acquisition.State == AcquisitionStates.Absent);
            decision.Evidence.Add(new EvidenceLine { SortOrder = 7, SignalType = "MEDIA_ACQUISITION", Verdict = "complete", Narrative = "All " + required.Count + " provider-media observation(s) required by this local attribution supplied usable answers; " + present + " present and " + absent + " provider-confirmed absent.", Metric = "required=" + required.Count + ";present=" + present + ";absent=" + absent });
        }

        private static string BridgeKey(ManualBridge bridge) => PairKey(bridge.ProviderA + ":" + bridge.ProviderIdA, bridge.ProviderB + ":" + bridge.ProviderIdB);
        private static string PairKey(string left, string right) => string.CompareOrdinal(left, right) <= 0 ? left + "|" + right : right + "|" + left;

        private static void AddIndex(Dictionary<string, List<ProviderPerson>> index, string key, ProviderPerson person)
        {
            if (!index.TryGetValue(key, out var items)) index[key] = items = new List<ProviderPerson>();
            items.Add(person);
        }

        private static string StableId(string prefix, string source)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(source ?? string.Empty));
                return prefix + "-" + string.Concat(bytes.Take(8).Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static int StatusOrder(string status)
        {
            switch (status) { case "SPLIT": return 0; case "REALIGNMENT": return 1; case "MERGE": return 2; case "CONFLATION": return 3; case "DRIFT": return 4; case "ORPHAN": return 5; case "MATCH_WITH_CONFLICT": return 6; default: return 7; }
        }

        private sealed class Candidate
        {
            public ProviderPerson Left { get; set; }
            public ProviderPerson Right { get; set; }
            public ScoreBreakdown Score { get; set; }
            public string Disposition { get; set; }
            public bool ManualOnly { get; set; }
            public string PairKey => ResolutionEngine.PairKey(Left.Key, Right.Key);
        }

        private sealed class Anchor { public LocalPerson Person { get; set; } public int Mass { get; set; } public bool Direct { get; set; } }
        private sealed class ComponentState
        {
            public int Index { get; set; }
            public List<ProviderPerson> People { get; set; } = new List<ProviderPerson>();
            public List<Anchor> Anchors { get; set; } = new List<Anchor>();
            public HashSet<string> ProviderKeys { get; set; } = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> MediaKeys { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        }
        private sealed class ReconciliationRegion
        {
            public List<ComponentState> Components { get; set; } = new List<ComponentState>();
            public List<LocalPerson> LocalPeople { get; set; } = new List<LocalPerson>();
        }
        private sealed class IdentifierResult { public bool Match { get; set; } public bool StableMatch { get; set; } public bool NativeCrosswalkMatch { get; set; } public bool Conflict { get; set; } public bool Any { get; set; } public List<string> Matches { get; } = new List<string>(); public List<string> Conflicts { get; } = new List<string>(); }
        private sealed class RoleResult { public int Exact { get; set; } public int Compatible { get; set; } public double Agreement { get; set; } }
        private static readonly string[] StableIdProviders = { ProviderNames.Imdb, ProviderNames.Wikidata };
        private static readonly string[] NativePersonIdProviders = { ProviderNames.Tmdb, ProviderNames.Tvdb };
        private static readonly List<LocalCredit> EmptyCredits = new List<LocalCredit>();

        private sealed class LocalIndex
        {
            public Dictionary<string, List<LocalPerson>> ByProviderKey { get; } = new Dictionary<string, List<LocalPerson>>(StringComparer.Ordinal);
            public Dictionary<string, List<LocalPerson>> ByImdb { get; } = new Dictionary<string, List<LocalPerson>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<LocalPerson>> ByCleanName { get; } = new Dictionary<string, List<LocalPerson>>(StringComparer.Ordinal);
            public Dictionary<long, List<LocalCredit>> CreditsByPerson { get; }
            public Dictionary<long, HashSet<string>> MediaKeysById { get; }

            public LocalIndex(ResolutionInput input)
            {
                CreditsByPerson = input.LocalCredits.GroupBy(x => x.PersonEmbyId).ToDictionary(x => x.Key, x => x.ToList());
                MediaKeysById = input.Media.ToDictionary(x => x.EmbyId, MediaKeys);
                foreach (var person in input.LocalPeople)
                {
                    foreach (var key in CurrentProviderKeys(person)) Add(ByProviderKey, key, person);
                    if (!string.IsNullOrWhiteSpace(person.ImdbId)) Add(ByImdb, person.ImdbId, person);
                    Add(ByCleanName, TextNormalizer.PersonName(person.Name), person);
                }
            }

            private static void Add(Dictionary<string, List<LocalPerson>> index, string key, LocalPerson person)
            {
                if (!index.TryGetValue(key ?? string.Empty, out var items)) index[key ?? string.Empty] = items = new List<LocalPerson>();
                items.Add(person);
            }
        }
    }
}
