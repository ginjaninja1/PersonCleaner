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
        public ResolutionDiagnostics Diagnostics { get; private set; } = new ResolutionDiagnostics();

        public IReadOnlyList<ResolutionDecision> Resolve(ResolutionInput input, ResolutionSettings settings)
        {
            Diagnostics = new ResolutionDiagnostics();
            input = input ?? new ResolutionInput();
            settings = settings ?? new ResolutionSettings();
            PreparePeople(input.ProviderPeople);

            var peopleByKey = input.ProviderPeople
                .Where(x => !string.IsNullOrWhiteSpace(x.Provider) && !string.IsNullOrWhiteSpace(x.ProviderId))
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToDictionary(x => x.Key, StringComparer.Ordinal);
            var rejected = new HashSet<string>(input.Bridges.Where(x => x.IsRejected).Select(BridgeKey), StringComparer.Ordinal);
            var graph = new DisjointSet();
            foreach (var key in peopleByKey.Keys) graph.Add(key);

            foreach (var bridge in input.Bridges.Where(x => !x.IsRejected))
            {
                var left = bridge.ProviderA + ":" + bridge.ProviderIdA;
                var right = bridge.ProviderB + ":" + bridge.ProviderIdB;
                if (peopleByKey.ContainsKey(left) && peopleByKey.ContainsKey(right)) graph.Union(left, right);
            }

            var candidates = BuildCandidates(peopleByKey.Values, settings, rejected);
            Diagnostics.AutomaticCandidates = candidates.Count(x => x.Score.HardIdentifierMatch || x.Score.Score >= settings.AutomaticMatchThreshold);
            Diagnostics.ReviewCandidates = candidates.Count(x => !x.Score.HardIdentifierMatch && x.Score.Score >= settings.HumanReviewThreshold && x.Score.Score < settings.AutomaticMatchThreshold);
            Diagnostics.BelowReviewCandidates = candidates.Count - Diagnostics.AutomaticCandidates - Diagnostics.ReviewCandidates;
            foreach (var candidate in candidates.Where(x => x.Score.HardIdentifierMatch || x.Score.Score >= settings.AutomaticMatchThreshold))
                graph.Union(candidate.Left.Key, candidate.Right.Key);

            var components = peopleByKey.Values.GroupBy(x => graph.Find(x.Key), StringComparer.Ordinal)
                .Select(x => x.OrderBy(y => y.Provider, StringComparer.Ordinal).ThenBy(y => y.ProviderId, StringComparer.Ordinal).ToList())
                .ToList();
            Diagnostics.GraphComponents = components.Count;
            var componentByProviderKey = components.SelectMany((items, index) => items.Select(item => new { item.Key, Index = index }))
                .ToDictionary(x => x.Key, x => x.Index, StringComparer.Ordinal);
            var localIndex = new LocalIndex(input);

            var decisions = new List<ResolutionDecision>();
            var resolvedLocalPeople = new HashSet<long>();
            foreach (var component in components)
            {
                var localMatches = RankLocalAnchors(component, localIndex);
                if (localMatches.Count == 0) continue;
                foreach (var match in localMatches) resolvedLocalPeople.Add(match.Person.EmbyId);
                decisions.Add(BuildComponentDecision(component, localMatches, input, candidates, settings));
            }

            var reviewCandidates = candidates.Where(x => !x.Score.HardIdentifierMatch && x.Score.Score >= settings.HumanReviewThreshold && x.Score.Score < settings.AutomaticMatchThreshold).ToList();
            var reviewPairs = new HashSet<string>(reviewCandidates.Select(x => PairKey(x.Left.Key, x.Right.Key)), StringComparer.Ordinal);
            foreach (var review in reviewCandidates)
                decisions.Add(BuildReviewDecision(review, input, settings));

            foreach (var local in input.LocalPeople)
            {
                var linkedKeys = CurrentProviderKeys(local)
                    .Where(componentByProviderKey.ContainsKey)
                    .Distinct(StringComparer.Ordinal).ToList();
                var linkedComponents = linkedKeys.Select(x => componentByProviderKey[x]).Distinct().ToList();
                if (linkedComponents.Count > 1)
                {
                    // A review candidate already describes the uncertain cross-provider pair. Emitting a
                    // second SPLIT row for the same Emby person makes one condition look like two problems.
                    // Explicit rejection or evidence below the review threshold still produces SPLIT.
                    var representedByReview = linkedKeys.SelectMany((left, index) => linkedKeys.Skip(index + 1).Select(right => PairKey(left, right))).Any(reviewPairs.Contains);
                    if (!representedByReview)
                        decisions.Add(BuildSplitDecision(local, linkedComponents.Select(x => components[x]).ToList(), input, settings));
                }
                else if (linkedComponents.Count == 0 && !resolvedLocalPeople.Contains(local.EmbyId) && input.LocalCredits.Any(x => x.PersonEmbyId == local.EmbyId))
                    decisions.Add(BuildOrphanDecision(local, input, settings));
            }

            return decisions.GroupBy(x => x.DecisionId, StringComparer.Ordinal).Select(x => x.First())
                .OrderBy(x => StatusOrder(x.Status)).ThenBy(x => x.Confidence).ThenByDescending(x => x.ImpactedMediaCount)
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static ScoreBreakdown Score(ProviderPerson left, ProviderPerson right, ResolutionSettings settings)
        {
            var intersection = left.CanonicalMediaKeys.Intersect(right.CanonicalMediaKeys, StringComparer.Ordinal).Count();
            var union = left.CanonicalMediaKeys.Union(right.CanonicalMediaKeys, StringComparer.Ordinal).Count();
            var jaccard = union == 0 ? 0 : intersection / (double)union;
            var birthdayKnown = !string.IsNullOrWhiteSpace(left.Birthday) && !string.IsNullOrWhiteSpace(right.Birthday);
            var birthdayMatch = birthdayKnown && string.Equals(left.Birthday, right.Birthday, StringComparison.Ordinal);
            var birthdayConflict = birthdayKnown && !birthdayMatch;
            var exactName = !string.IsNullOrWhiteSpace(left.CleanName) && string.Equals(left.CleanName, right.CleanName, StringComparison.Ordinal);
            var aliases = new HashSet<string>(left.Aliases.Select(TextNormalizer.PersonName).Where(x => x.Length > 0), StringComparer.Ordinal);
            aliases.Add(left.CleanName ?? string.Empty);
            var otherAliases = new HashSet<string>(right.Aliases.Select(TextNormalizer.PersonName).Where(x => x.Length > 0), StringComparer.Ordinal);
            otherAliases.Add(right.CleanName ?? string.Empty);
            var aliasMatch = aliases.Overlaps(otherAliases) && !exactName;
            var hard = SharesExternalIdentity(left, right);
            var score = hard ? 1.0 :
                settings.FilmographyWeight * jaccard +
                settings.BirthdayWeight * (birthdayMatch ? 1 : 0) +
                settings.ExactNameWeight * (exactName ? 1 : 0) +
                settings.AliasWeight * (aliasMatch ? 1 : 0) -
                (birthdayConflict ? settings.BirthdayMismatchPenalty : 0);
            return new ScoreBreakdown
            {
                FilmographyJaccard = jaccard,
                SharedMediaCount = intersection,
                BirthdayMatch = birthdayMatch,
                BirthdayConflict = birthdayConflict,
                ExactNameMatch = exactName,
                AliasMatch = aliasMatch,
                HardIdentifierMatch = hard,
                Score = Math.Max(0, Math.Min(1, score))
            };
        }

        private List<Candidate> BuildCandidates(IEnumerable<ProviderPerson> people, ResolutionSettings settings, HashSet<string> rejected)
        {
            var tmdb = people.Where(x => x.Provider == ProviderNames.Tmdb).ToList();
            var tvdb = people.Where(x => x.Provider == ProviderNames.Tvdb).ToList();
            var byMedia = new Dictionary<string, List<ProviderPerson>>(StringComparer.Ordinal);
            var byExternal = new Dictionary<string, List<ProviderPerson>>(StringComparer.OrdinalIgnoreCase);
            foreach (var person in tvdb)
            {
                foreach (var media in person.CanonicalMediaKeys) AddIndex(byMedia, media, person);
                foreach (var provider in new[] { ProviderNames.Imdb, ProviderNames.Wikidata })
                    if (person.ExternalIds.TryGetValue(provider, out var id) && !string.IsNullOrWhiteSpace(id)) AddIndex(byExternal, provider + ":" + id, person);
            }
            var result = new List<Candidate>();
            foreach (var left in tmdb)
            {
                var possible = new HashSet<ProviderPerson>();
                foreach (var media in left.CanonicalMediaKeys) if (byMedia.TryGetValue(media, out var matches)) possible.UnionWith(matches);
                foreach (var provider in new[] { ProviderNames.Imdb, ProviderNames.Wikidata })
                    if (left.ExternalIds.TryGetValue(provider, out var id) && !string.IsNullOrWhiteSpace(id) && byExternal.TryGetValue(provider + ":" + id, out var matches)) possible.UnionWith(matches);
                foreach (var right in possible)
                {
                    Diagnostics.BlockedCrossProviderPairs++;
                    if (rejected.Contains(PairKey(left.Key, right.Key))) { Diagnostics.RejectedByOperator++; continue; }
                    var score = Score(left, right, settings);
                    // Sharing a production only means two people worked on the same title. It is useful
                    // corroboration after a name/alias bridge, but must never create a person candidate by
                    // itself or every TMDB cast member is compared with every TVDB cast member.
                    if (score.HardIdentifierMatch || (score.SharedMediaCount > 0 && (score.ExactNameMatch || score.AliasMatch)))
                    {
                        if (score.HardIdentifierMatch) Diagnostics.HardIdentityCandidates++;
                        else Diagnostics.NameCompatibleCandidates++;
                        result.Add(new Candidate { Left = left, Right = right, Score = score });
                    }
                }
            }
            return result;
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
                var direct = CurrentProviderKeys(local).Any(providerKeys.Contains) ||
                    component.Any(x => !string.IsNullOrWhiteSpace(local.ImdbId) && x.ExternalIds.TryGetValue(ProviderNames.Imdb, out var imdb) && imdb == local.ImdbId);
                var credits = index.CreditsByPerson.TryGetValue(local.EmbyId, out var localCredits) ? localCredits : EmptyCredits;
                var mass = credits.Select(x => index.MediaKeysById.TryGetValue(x.MediaEmbyId, out var keys) && keys.Overlaps(mediaKeys) ? x.MediaEmbyId : 0)
                    .Where(x => x != 0).Distinct().Count();
                var nameCompatible = component.Any(x => x.CleanName == TextNormalizer.PersonName(local.Name));
                if (direct || (mass > 0 && nameCompatible)) ranks.Add(new Anchor { Person = local, Mass = mass, Direct = direct });
            }
            return ranks.OrderByDescending(x => x.Mass).ThenByDescending(x => x.Direct).ThenBy(x => x.Person.EmbyId).ToList();
        }

        private static ResolutionDecision BuildComponentDecision(List<ProviderPerson> component, List<Anchor> anchors, ResolutionInput input, List<Candidate> candidates, ResolutionSettings settings)
        {
            var winner = anchors[0];
            var providerKeys = component.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var pairScores = candidates.Where(x => providerKeys.Contains(x.Left.Key) && providerKeys.Contains(x.Right.Key)).Select(x => x.Score).ToList();
            var confidence = pairScores.Count == 0 ? (anchors.Any(x => x.Direct) ? 1 : 0.5) : pairScores.Max(x => x.Score);
            var merge = anchors.Count > 1;
            var drift = !winner.Direct;
            var names = string.Join(" / ", component.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));
            var decision = new ResolutionDecision
            {
                DecisionId = StableId("cluster", string.Join("|", providerKeys)),
                Status = drift ? "DRIFT" : "MATCH",
                Action = drift ? "RETAINED_BY_MASS_ID_DRIFT" : merge ? "AUTO_MERGE_SHADOW" : "RETAINED_BY_MASS",
                DisplayName = string.IsNullOrWhiteSpace(winner.Person.Name) ? names : winner.Person.Name,
                AnchorEmbyPersonId = winner.Person.EmbyId,
                ProviderKeys = string.Join(", ", providerKeys),
                Confidence = confidence,
                Headline = drift
                    ? "The current provider key no longer reaches this identity, but " + winner.Mass + " sampled title(s) pull the new provider profile back to Emby person " + winner.Person.EmbyId + "."
                    : merge
                    ? anchors.Count + " Emby people resolve to one provider identity; Emby person " + winner.Person.EmbyId + " has the strongest local-media anchor."
                    : "Provider identity remains anchored to Emby person " + winner.Person.EmbyId + " by " + winner.Mass + " sampled media title(s).",
                Explanation = drift
                    ? "This is an upstream identifier drift proposal, not a name-only match. The unchanged local filmography supplies the identity continuity."
                    : merge
                    ? "The shadow result groups the provider records and retains the Emby person with the largest number of matching local titles. No live Emby record is changed."
                    : "Stable local media relationships preserve this identity even if an upstream provider identifier later drifts."
            };
            AddPairEvidence(decision, pairScores.OrderByDescending(x => x.Score).FirstOrDefault());
            decision.Evidence.Add(new EvidenceLine { SortOrder = 50, SignalType = "LOCAL_MEDIA_MASS", Verdict = "supports", Narrative = "Emby person " + winner.Person.EmbyId + " is the gravitational anchor with " + winner.Mass + " matching sampled title(s).", Metric = "mass=" + winner.Mass });
            if (drift) decision.Evidence.Add(new EvidenceLine { SortOrder = 5, SignalType = "PROVIDER_ID_DRIFT", Verdict = "changed", Narrative = "None of the current Emby provider keys directly identifies this provider component; local-media mass and compatible naming establish the proposed continuity.", Metric = "direct_current_id=false" });
            var manual = input.Bridges.FirstOrDefault(x => !x.IsRejected && providerKeys.Contains(x.ProviderA + ":" + x.ProviderIdA) && providerKeys.Contains(x.ProviderB + ":" + x.ProviderIdB));
            if (manual != null) decision.Evidence.Add(new EvidenceLine { SortOrder = 2, SignalType = "OPERATOR_BRIDGE", Verdict = "proves", Narrative = "An operator explicitly confirmed this cross-provider alignment.", Metric = manual.ProviderA + ":" + manual.ProviderIdA + "=" + manual.ProviderB + ":" + manual.ProviderIdB });
            AddMediaExamples(decision, anchors.Select(x => x.Person.EmbyId), input, settings.MaximumMediaExamples);
            return decision;
        }

        private static ResolutionDecision BuildReviewDecision(Candidate candidate, ResolutionInput input, ResolutionSettings settings)
        {
            var decision = new ResolutionDecision
            {
                DecisionId = StableId("review", PairKey(candidate.Left.Key, candidate.Right.Key)),
                Status = "CONFLATION",
                Action = "HUMAN_REVIEW",
                DisplayName = candidate.Left.Name + " / " + candidate.Right.Name,
                ProviderKeys = candidate.Left.Key + ", " + candidate.Right.Key,
                Confidence = candidate.Score.Score,
                Headline = "The providers share " + candidate.Score.SharedMediaCount + " title(s), but the combined evidence is not strong enough to join the identities.",
                Explanation = candidate.Score.BirthdayConflict
                    ? "The birth dates conflict. This is a strong warning that one provider profile may conflate different people."
                    : "The overlap is meaningful but ambiguous. Confirm the alignment or reject it; the decision can then be recalculated without refetching provider data."
            };
            AddPairEvidence(decision, candidate.Score);
            var localIds = input.LocalPeople.Where(x => CurrentProviderKeys(x).Contains(candidate.Left.Key) || CurrentProviderKeys(x).Contains(candidate.Right.Key)).Select(x => x.EmbyId).ToList();
            AddMediaExamples(decision, localIds, input, settings.MaximumMediaExamples);
            return decision;
        }

        private static ResolutionDecision BuildSplitDecision(LocalPerson local, List<List<ProviderPerson>> components, ResolutionInput input, ResolutionSettings settings)
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
                Headline = "Emby person " + local.EmbyId + " points at " + components.Count + " disconnected provider identities.",
                Explanation = "The provider identifiers do not share a hard bridge or sufficient filmography evidence. Review the impacted titles and choose which identifier belongs in each media context."
            };
            decision.Evidence.Add(new EvidenceLine { SortOrder = 1, SignalType = "DISCONNECTED_GRAPH", Verdict = "conflicts", Narrative = components.Count + " independent provider components are attached to one Emby person.", Metric = "components=" + components.Count });
            AddMediaExamples(decision, new[] { local.EmbyId }, input, settings.MaximumMediaExamples);
            return decision;
        }

        private static ResolutionDecision BuildOrphanDecision(LocalPerson local, ResolutionInput input, ResolutionSettings settings)
        {
            var decision = new ResolutionDecision
            {
                DecisionId = StableId("orphan", local.EmbyId.ToString(CultureInfo.InvariantCulture)),
                Status = "ORPHAN",
                Action = "HUMAN_REVIEW",
                DisplayName = local.Name,
                AnchorEmbyPersonId = local.EmbyId,
                ProviderKeys = CurrentProviderKeys(local).Any()
                    ? string.Join(", ", CurrentProviderKeys(local))
                    : "No current TMDB/TVDB person ID",
                Confidence = 0,
                Headline = "No hydrated provider identity supports this Emby person in the current sample.",
                Explanation = "This is not a deletion instruction. It may indicate a stale person, missing provider identifiers, incomplete credits, or a provider fetch failure."
            };
            decision.Evidence.Add(new EvidenceLine { SortOrder = 1, SignalType = "NO_PROVIDER_SUPPORT", Verdict = "missing", Narrative = "The sampled provider graph contains no matching identity node.", Metric = "provider_nodes=0" });
            AddMediaExamples(decision, new[] { local.EmbyId }, input, settings.MaximumMediaExamples);
            return decision;
        }

        private static void AddPairEvidence(ResolutionDecision decision, ScoreBreakdown score)
        {
            if (score == null) return;
            decision.Evidence.Add(new EvidenceLine { SortOrder = 10, SignalType = "FILMOGRAPHY", Verdict = score.SharedMediaCount > 0 ? "supports" : "neutral", Narrative = score.SharedMediaCount + " shared canonical title(s); Jaccard overlap " + score.FilmographyJaccard.ToString("0.000", CultureInfo.InvariantCulture) + ".", Metric = "shared=" + score.SharedMediaCount + ";jaccard=" + score.FilmographyJaccard.ToString("0.000000", CultureInfo.InvariantCulture) });
            decision.Evidence.Add(new EvidenceLine { SortOrder = 20, SignalType = "BIRTHDAY", Verdict = score.BirthdayConflict ? "conflicts" : score.BirthdayMatch ? "supports" : "unknown", Narrative = score.BirthdayConflict ? "Both providers supplied different birth dates." : score.BirthdayMatch ? "Both providers supplied the same birth date." : "A comparable birth date was not available from both providers.", Metric = score.BirthdayConflict ? "mismatch" : score.BirthdayMatch ? "exact" : "missing" });
            decision.Evidence.Add(new EvidenceLine { SortOrder = 30, SignalType = "NAME", Verdict = score.ExactNameMatch || score.AliasMatch ? "supports" : "neutral", Narrative = score.ExactNameMatch ? "Normalized primary names match exactly." : score.AliasMatch ? "A provider alias matches the other provider's name." : "Names did not add positive evidence.", Metric = score.ExactNameMatch ? "exact" : score.AliasMatch ? "alias" : "none" });
            if (score.HardIdentifierMatch) decision.Evidence.Add(new EvidenceLine { SortOrder = 1, SignalType = "EXTERNAL_ID", Verdict = "proves", Narrative = "The provider profiles share a stable IMDb or Wikidata identifier.", Metric = "hard_bridge=true" });
        }

        private static void AddMediaExamples(ResolutionDecision decision, IEnumerable<long> people, ResolutionInput input, int maximum)
        {
            var ids = new HashSet<long>(people);
            var media = input.Media.ToDictionary(x => x.EmbyId);
            var examples = input.LocalCredits.Where(x => ids.Contains(x.PersonEmbyId) && media.ContainsKey(x.MediaEmbyId))
                .GroupBy(x => x.MediaEmbyId).Select(x => new { Credit = x.First(), Media = media[x.Key] })
                .OrderBy(x => x.Media.MediaType).ThenBy(x => x.Media.Name, StringComparer.OrdinalIgnoreCase).ToList();
            decision.ImpactedMediaCount = examples.Count;
            decision.ImpactedMedia = examples.Select(x => new MediaExample
            {
                EmbyMediaId = x.Media.EmbyId,
                MediaType = x.Media.MediaType,
                DisplayName = x.Media.Name + (x.Media.Year.HasValue ? " (" + x.Media.Year.Value + ")" : string.Empty),
                Role = x.Credit.Role
            }).ToList();
            decision.MediaExamples = decision.ImpactedMedia.Take(Math.Max(0, maximum)).ToList();
            if (examples.Count > decision.MediaExamples.Count)
                decision.Evidence.Add(new EvidenceLine { SortOrder = 99, SignalType = "MEDIA_SCOPE", Verdict = "info", Narrative = decision.MediaExamples.Count + " representative titles are shown; " + (examples.Count - decision.MediaExamples.Count) + " more are retained in the database.", Metric = "total=" + examples.Count });
        }

        private static void PreparePeople(IEnumerable<ProviderPerson> people)
        {
            foreach (var person in people)
            {
                person.CleanName = TextNormalizer.PersonName(string.IsNullOrWhiteSpace(person.CleanName) ? person.Name : person.CleanName);
                person.Aliases = (person.Aliases ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                person.ExternalIds = person.ExternalIds ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                person.CanonicalMediaKeys = person.CanonicalMediaKeys ?? new HashSet<string>(StringComparer.Ordinal);
            }
        }

        private static bool SharesExternalIdentity(ProviderPerson left, ProviderPerson right)
        {
            foreach (var provider in new[] { ProviderNames.Imdb, ProviderNames.Wikidata })
                if (left.ExternalIds.TryGetValue(provider, out var a) && right.ExternalIds.TryGetValue(provider, out var b) && !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
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
            switch (status) { case "SPLIT": return 0; case "CONFLATION": return 1; case "DRIFT": return 2; case "ORPHAN": return 3; default: return 4; }
        }

        private sealed class Candidate { public ProviderPerson Left { get; set; } public ProviderPerson Right { get; set; } public ScoreBreakdown Score { get; set; } }
        private sealed class Anchor { public LocalPerson Person { get; set; } public int Mass { get; set; } public bool Direct { get; set; } }
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
