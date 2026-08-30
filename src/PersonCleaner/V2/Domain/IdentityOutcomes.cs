using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PersonCleaner.V2.Domain
{
    public static class IdentityTargetKinds
    {
        public const string Existing = "EXISTING";
        public const string New = "NEW";
        public const string Unresolved = "UNRESOLVED";
    }

    public static class IdentityPlanStates
    {
        public const string Complete = "COMPLETE";
        public const string CorrectionRequired = "CORRECTION_REQUIRED";
        public const string Applied = "APPLIED";
    }

    public static class CasePresentationPurposes
    {
        public const string Problem = "PROBLEM";
        public const string SatisfiedChange = "SATISFIED_CHANGE";
        public const string SatisfiedNoChange = "SATISFIED_NO_CHANGE";
    }

    public sealed class IdentityCasePlan
    {
        public long RunId { get; set; }
        public string CaseId { get; set; }
        public string PlanHash { get; set; }
        public string DisplayName { get; set; }
        public string CaseType { get; set; }
        public string Summary { get; set; }
        public string Warning { get; set; }
        public string State { get; set; }
        public string PresentationPurpose { get; set; }
        public bool RequiresManualReview { get; set; }
        public string ApplyCaption { get; set; }
        public List<string> DecisionIds { get; set; } = new List<string>();
        public List<LocalPerson> CurrentPeople { get; set; } = new List<LocalPerson>();
        public List<IdentityOutcome> Outcomes { get; set; } = new List<IdentityOutcome>();
        public List<IdentityCreditOutcome> Credits { get; set; } = new List<IdentityCreditOutcome>();
        public List<IdentityQuestion> Questions { get; set; } = new List<IdentityQuestion>();
    }

    public sealed class IdentityOutcome
    {
        public string OutcomeId { get; set; }
        public int SortOrder { get; set; }
        public string ClusterKey { get; set; }
        public string TargetKind { get; set; }
        public long? TargetEmbyId { get; set; }
        public string DisplayName { get; set; }
        public string Outcome { get; set; }
        public List<long> SourceEmbyIds { get; set; } = new List<long>();
        public List<IdentityProviderId> ProviderIds { get; set; } = new List<IdentityProviderId>();
    }

    public sealed class IdentityProviderId
    {
        public string Provider { get; set; }
        public string ProviderId { get; set; }
        public string Source { get; set; }
    }

    public sealed class IdentityCreditOutcome
    {
        public string AssignmentId { get; set; }
        public long SourcePersonEmbyId { get; set; }
        public string TargetOutcomeId { get; set; }
        public long MediaEmbyId { get; set; }
        public string MediaType { get; set; }
        public string MediaName { get; set; }
        public long? SeriesEmbyId { get; set; }
        public string SeriesName { get; set; }
        public string Role { get; set; }
        public string TmdbId { get; set; }
        public string TvdbId { get; set; }
        public string TvdbSlug { get; set; }
        public string ImdbId { get; set; }
        public string Disposition { get; set; }
        public string Rationale { get; set; }
        public bool CorrectionRequired { get; set; }
        public bool IsReviewSupplemental { get; set; }
        public List<IdentityCreditAttribution> Attributions { get; set; } = new List<IdentityCreditAttribution>();
    }

    public sealed class IdentityCreditAttribution
    {
        public string Provider { get; set; }
        public string ProviderMediaId { get; set; }
        public string ProviderPersonId { get; set; }
        public string PersonName { get; set; }
        public string Role { get; set; }
        public string RoleCategory { get; set; }
        public string OutcomeId { get; set; }
    }

    public sealed class IdentityQuestion
    {
        public string QuestionId { get; set; }
        public string Kind { get; set; }
        public string OutcomeId { get; set; }
        public string AssignmentId { get; set; }
        public string Narrative { get; set; }
        public List<IdentityQuestionChoice> Choices { get; set; } = new List<IdentityQuestionChoice>();
    }

    public sealed class IdentityQuestionChoice
    {
        public string ChoiceId { get; set; }
        public string Caption { get; set; }
        public string Effect { get; set; }
        public ProviderCorrection Correction { get; set; }
    }

    public sealed class IdentityCaseApplyReceipt
    {
        public string Summary { get; set; }
        public Dictionary<string, long> OutcomeEmbyIds { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);
        public List<IdentityCaseAppliedChange> Changes { get; set; } = new List<IdentityCaseAppliedChange>();
    }

    public sealed class IdentityCaseAppliedChange
    {
        public string Kind { get; set; }
        public long? SourceEmbyId { get; set; }
        public long? TargetEmbyId { get; set; }
        public string OutcomeId { get; set; }
        public long? MediaEmbyId { get; set; }
        public string Role { get; set; }
        public string Provider { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
        public string Summary { get; set; }
    }

    public static class IdentityCasePlanner
    {
        public static List<IdentityCasePlan> Build(long runId, ResolutionInput input, IReadOnlyCollection<ResolutionDecision> decisions, IReadOnlyCollection<ResolutionClusterSnapshot> clusters)
        {
            input = input ?? new ResolutionInput();
            var decisionList = (decisions ?? new ResolutionDecision[0]).OrderBy(x => x.DecisionId, StringComparer.Ordinal).ToList();
            var clusterList = (clusters ?? new ResolutionClusterSnapshot[0]).ToList();
            var planner = new PlannerIndex(input);
            var clusterIndex = new ClusterIndex(clusterList);
            var result = new List<IdentityCasePlan>();
            foreach (var group in CaseGroups(decisionList))
                result.Add(BuildCase(runId, input, planner, group, clusterIndex));
            return result;
        }

        private static IdentityCasePlan BuildCase(long runId, ResolutionInput input, PlannerIndex planner, List<ResolutionDecision> decisions, ClusterIndex clusterIndex)
        {
            var providerKeys = new HashSet<string>(decisions.SelectMany(x => Keys(x.ProviderKeys)), StringComparer.Ordinal);
            var personIds = new HashSet<long>(decisions.Where(x => x.AnchorEmbyPersonId.HasValue).Select(x => x.AnchorEmbyPersonId.Value));
            foreach (var assignment in decisions.SelectMany(x => x.CreditAssignments ?? new List<ResolutionCreditAssignment>()))
            {
                personIds.Add(assignment.SourcePersonEmbyId);
                personIds.Add(assignment.TargetPersonEmbyId);
            }
            foreach (var key in providerKeys)
                if (planner.LocalPeopleByProviderKey.TryGetValue(key, out var owners)) foreach (var person in owners) personIds.Add(person.EmbyId);

            var clusters = clusterIndex.Find(providerKeys, personIds);
            if (clusters.Count == 0)
                clusters.AddRange(providerKeys.Select((key, index) => new ResolutionClusterSnapshot { ClusterId = "synthetic-" + index, ProviderKeys = new List<string> { key }, AnchorEmbyPersonId = decisions.Select(x => x.AnchorEmbyPersonId).FirstOrDefault(x => x.HasValue) }));

            var displayName = decisions.Select(x => x.DisplayName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "Identity case";
            var caseId = decisions.Count == 1 ? decisions[0].DecisionId : "case-" + StableHash(string.Join("|", decisions.Select(x => x.DecisionId)));
            var plan = new IdentityCasePlan
            {
                RunId = runId,
                CaseId = caseId,
                DisplayName = displayName,
                CaseType = FriendlyType(decisions),
                RequiresManualReview = decisions.Any(x => string.Equals(x.Status, "SPLIT", StringComparison.OrdinalIgnoreCase)),
                DecisionIds = decisions.Select(x => x.DecisionId).ToList()
            };
            foreach (var warning in decisions.SelectMany(x => x.Evidence ?? new List<EvidenceLine>())
                .Where(x => string.Equals(x.SignalType, "BIRTHDAY", StringComparison.OrdinalIgnoreCase) && (string.Equals(x.Verdict, "conflicts", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Verdict, "informational", StringComparison.OrdinalIgnoreCase)))
                .Select(x => x.Narrative).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
                AppendWarning(plan, "Informational metadata warning: " + warning);

            var clusterGroups = clusters.Select(x => new List<ResolutionClusterSnapshot> { x }).ToList();
            var sameAnchor = clusterGroups.Count > 1 && personIds.Count == 1;
            if (sameAnchor)
            {
                LocalPerson current;
                if (planner.LocalPeopleById.TryGetValue(personIds.Single(), out current))
                {
                    var currentKeys = new HashSet<string>(CurrentKeys(current), StringComparer.Ordinal);
                    var boundGroups = clusterGroups.Where(x => x.Any(y => y.ProviderKeys.Any(currentKeys.Contains))).ToList();
                    var boundClusters = boundGroups.SelectMany(x => x).ToList();
                    if (boundGroups.Count > 1 && boundGroups.Count < clusterGroups.Count && CanRemainOneIdentity(boundClusters, input, planner))
                    {
                        var insertAt = clusterGroups.FindIndex(boundGroups.Contains);
                        clusterGroups.RemoveAll(boundGroups.Contains);
                        clusterGroups.Insert(insertAt, boundClusters.OrderBy(x => x.ClusterId, StringComparer.Ordinal).ToList());
                        AppendWarning(plan, "Compatible provider records already bound to Emby person " + current.EmbyId + " remain together; conflicting provider alternatives remain separate.");
                    }
                }
                if (clusterGroups.Count > 1 && CanRemainOneIdentity(clusterGroups.SelectMany(x => x).ToList(), input, planner))
                {
                    clusterGroups = new List<List<ResolutionClusterSnapshot>> { clusters };
                    AppendWarning(plan, "Nothing independently links every provider record in this case, but there is no counter-evidence and Emby currently treats them as the same person.");
                }
            }

            var currentPeople = personIds.Select(x => planner.LocalPeopleById.TryGetValue(x, out var person) ? person : null).Where(x => x != null).OrderBy(x => x.EmbyId).ToList();
            plan.CurrentPeople.AddRange(currentPeople.Select(x => new LocalPerson { EmbyId = x.EmbyId, Name = x.Name, TmdbId = x.TmdbId, TvdbId = x.TvdbId, ImdbId = x.ImdbId }));
            var usedTargets = new HashSet<long>();
            var provisional = new List<OutcomeBuilder>();
            foreach (var group in clusterGroups)
            {
                var keys = new HashSet<string>(group.SelectMany(x => x.ProviderKeys), StringComparer.Ordinal);
                var candidates = currentPeople.Select(x => new { Person = x, Score = CurrentKeys(x).Count(keys.Contains), Credits = planner.LocalCreditCounts.TryGetValue(x.EmbyId, out var count) ? count : 0 })
                    .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenByDescending(x => x.Credits).ThenBy(x => x.Person.EmbyId).ToList();
                var selected = candidates.Select(x => x.Person).FirstOrDefault(x => !usedTargets.Contains(x.EmbyId));
                if (selected == null)
                {
                    // A provider-ID drift case deliberately has no current-key match. Reuse the
                    // resolver's unique local media anchor instead of creating a replacement Emby
                    // person and moving every already-correct credit to it.
                    var anchored = group.Where(x => x.AnchorEmbyPersonId.HasValue).Select(x => x.AnchorEmbyPersonId.Value).Distinct()
                        .Where(x => !usedTargets.Contains(x)).Select(x => currentPeople.FirstOrDefault(y => y.EmbyId == x)).Where(x => x != null).ToList();
                    if (anchored.Count == 1)
                    {
                        var directInAnotherGroup = clusters.Except(group).Any(x => CurrentKeys(anchored[0]).Any(y => x.ProviderKeys.Contains(y)));
                        if (!directInAnotherGroup) selected = anchored[0];
                    }
                }
                var targetOverride = FindIdentityOverride(input.ActiveCorrections, keys);
                var builder = new OutcomeBuilder { Clusters = group, Keys = keys, Override = targetOverride };
                if (targetOverride != null && targetOverride.ReplacementValue.StartsWith("existing:", StringComparison.Ordinal))
                {
                    long id;
                    if (long.TryParse(targetOverride.ReplacementValue.Substring(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) selected = planner.PersonById(id);
                }
                if (targetOverride != null && targetOverride.ReplacementValue == "new") selected = null;
                if (selected != null) usedTargets.Add(selected.EmbyId);
                builder.Selected = selected;
                provisional.Add(builder);
            }

            var providerPeople = planner.ProviderPeople;
            for (var index = 0; index < provisional.Count; index++)
            {
                var builder = provisional[index];
                var projection = FinalProviderIds(builder.Keys, providerPeople, builder.Selected);
                var ids = projection.ProviderIds;
                foreach (var warning in projection.Warnings) AppendWarning(plan, warning);
                var targetKind = builder.Selected != null ? IdentityTargetKinds.Existing : ids.Any(x => x.Source == "native") ? IdentityTargetKinds.New : IdentityTargetKinds.Unresolved;
                if (HasNativeProviderIdConflict(ids)) targetKind = IdentityTargetKinds.Unresolved;
                // Existing-person Apply changes provider IDs and credit ownership, not names.
                // Keep the result name honest about what Emby will actually retain.
                var identityName = builder.Selected?.Name ?? builder.Keys.Select(x => providerPeople.ContainsKey(x) ? providerPeople[x].Name : null).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? displayName;
                var outcomeId = (targetKind == IdentityTargetKinds.Existing ? "existing:" + builder.Selected.EmbyId : targetKind == IdentityTargetKinds.New ? "new:" : "unresolved:") + StableHash(string.Join("|", builder.Keys.OrderBy(x => x, StringComparer.Ordinal)));
                var matchingSourceIds = currentPeople.Where(x => CurrentKeys(x).Any(builder.Keys.Contains)).Select(x => x.EmbyId).ToList();
                var sourceIds = targetKind == IdentityTargetKinds.Existing && builder.Selected != null ? matchingSourceIds.Where(x => x == builder.Selected.EmbyId).ToList() : targetKind == IdentityTargetKinds.Unresolved ? matchingSourceIds : new List<long>();
                plan.Outcomes.Add(new IdentityOutcome
                {
                    OutcomeId = outcomeId,
                    SortOrder = index,
                    ClusterKey = string.Join(",", builder.Clusters.Select(x => x.ClusterId)),
                    TargetKind = targetKind,
                    TargetEmbyId = targetKind == IdentityTargetKinds.Existing ? builder.Selected?.EmbyId : null,
                    DisplayName = identityName,
                    SourceEmbyIds = sourceIds,
                    ProviderIds = ids,
                    Outcome = targetKind == IdentityTargetKinds.Existing ? "Retain Emby person " + builder.Selected.EmbyId : targetKind == IdentityTargetKinds.New ? "Create provider-identified Emby person" : "Retain the current Emby person while the conflicting provider assertions are corrected; no Emby destination change is proposed"
                });
            }

            AddEmptyExistingOutcomes(plan, currentPeople);
            BuildCredits(plan, input, planner, provisional);
            foreach (var outcome in plan.Outcomes.Where(x => x.TargetKind == IdentityTargetKinds.Existing && plan.Credits.Any(c => c.TargetOutcomeId == x.OutcomeId)))
                outcome.Outcome = "Retain Emby person " + outcome.TargetEmbyId;
            PreserveUnopposedExistingIds(plan, input, currentPeople);
            BuildIdentityRelationQuestions(plan, decisions);
            BuildIdentityQuestions(plan, input, provisional);

            PruneUnassignedNewOutcomes(plan);
            var incompleteScope = decisions.Any(x => x.Action == ResolutionActions.IncompleteScope);
            plan.State = incompleteScope || plan.Questions.Count > 0 || plan.Outcomes.Any(x => x.TargetKind == IdentityTargetKinds.Unresolved) || plan.Credits.Any(x => x.CorrectionRequired) ? IdentityPlanStates.CorrectionRequired : IdentityPlanStates.Complete;
            CompleteSummaries(plan, planner);
            if (incompleteScope) plan.CaseType = "Provider ID also exists outside calculated scope";
            else if (plan.State == IdentityPlanStates.Complete)
                plan.CaseType = plan.RequiresManualReview && !HasMutations(plan)
                    ? "Unverified combined identity — no changes proposed"
                    : SatisfiedCaseType(plan, planner);
            plan.PresentationPurpose = PresentationPurpose(plan);
            plan.PlanHash = StableHash(Canonical(plan));
            return plan;
        }

        public static bool HasMutations(IdentityCasePlan plan)
        {
            if (plan == null) return false;
            if (plan.Credits.Any(x => x.Disposition == "MOVE") || plan.Outcomes.Any(x => x.TargetKind == IdentityTargetKinds.New)) return true;
            foreach (var snapshot in plan.CurrentPeople)
            {
                var outcome = plan.Outcomes.FirstOrDefault(x => x.TargetEmbyId == snapshot.EmbyId);
                if (!SameValue(snapshot.TmdbId, PreferredProviderId(outcome, ProviderNames.Tmdb)) || !SameValue(snapshot.TvdbId, PreferredProviderId(outcome, ProviderNames.Tvdb)) || !SameValue(snapshot.ImdbId, PreferredProviderId(outcome, ProviderNames.Imdb))) return true;
            }
            return false;
        }

        public static string PresentationPurpose(IdentityCasePlan plan)
        {
            if (plan == null || plan.State != IdentityPlanStates.Complete) return CasePresentationPurposes.Problem;
            var hasMutations = HasMutations(plan);
            if (plan.RequiresManualReview && hasMutations) return CasePresentationPurposes.Problem;
            return hasMutations ? CasePresentationPurposes.SatisfiedChange : CasePresentationPurposes.SatisfiedNoChange;
        }

        private static bool SameValue(string left, string right) => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        private static string SatisfiedCaseType(IdentityCasePlan plan, PlannerIndex planner)
        {
            var creates = plan.Outcomes.Any(x => x.TargetKind == IdentityTargetKinds.New);
            var moves = plan.Credits.Any(x => x.Disposition == "MOVE");
            var ids = ProviderIdChangeCount(plan, planner) > 0;
            if (creates && moves) return ids ? "Person creation, credit realignment and provider ID alignment" : "Person creation and credit realignment";
            if (moves) return ids ? "Credit realignment and provider ID alignment" : "Credit realignment";
            if (creates) return ids ? "Person creation and provider ID alignment" : "Person creation";
            if (ids) return "Provider ID alignment";
            return "No Emby changes required";
        }

        private static void BuildCredits(IdentityCasePlan plan, ResolutionInput input, PlannerIndex index, List<OutcomeBuilder> builders)
        {
            var mediaById = index.MediaById;
            var outcomeByBuilder = builders.Select((x, i) => new { Builder = x, Outcome = plan.Outcomes[i] }).ToList();
            var outcomeByProviderKey = outcomeByBuilder.SelectMany(x => x.Builder.Keys.Select(y => new { Key = y, x.Outcome })).GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First().Outcome, StringComparer.Ordinal);
            var providerCreditIndex = index.ProviderCreditsByMedia;
            var relevantCredits = plan.CurrentPeople.SelectMany(x => index.LocalCreditsByPerson.TryGetValue(x.EmbyId, out var rows) ? rows : Enumerable.Empty<LocalCredit>());
            foreach (var credit in relevantCredits.Where(x => mediaById.ContainsKey(x.MediaEmbyId)).OrderBy(x => x.MediaEmbyId).ThenBy(x => x.Role, StringComparer.Ordinal).ThenBy(x => x.PersonEmbyId))
            {
                var media = mediaById[credit.MediaEmbyId];
                var observed = new List<ObservedProviderCredit>();
                AddProviderCredits(observed, providerCreditIndex, ProviderNames.Tmdb, media.MediaType, media.ProviderAcquisitionId(ProviderNames.Tmdb));
                AddProviderCredits(observed, providerCreditIndex, ProviderNames.Tvdb, media.MediaType, media.ProviderAcquisitionId(ProviderNames.Tvdb));
                var roleCategory = RoleCategory(credit.Role);
                var matches = outcomeByBuilder.Where(x => observed.Any(c => x.Builder.Keys.Contains(c.PersonKey) && CompatibleAttribution(media.MediaType, roleCategory, c.RoleCategory))).Select(x => x.Outcome).Distinct().ToList();
                var correction = FindCreditOverride(input.ActiveCorrections, credit);
                IdentityOutcome target = null;
                if (correction != null) target = ResolveOverrideTarget(plan, correction.ReplacementValue);
                if (target == null && matches.Count == 1) target = matches[0];
                if (target == null && matches.Count == 0) target = plan.Outcomes.FirstOrDefault(x => x.TargetEmbyId == credit.PersonEmbyId) ?? plan.Outcomes.FirstOrDefault(x => x.SourceEmbyIds.Contains(credit.PersonEmbyId));
                var repairableProviderDisagreement = target != null && matches.Count > 1 && ProviderAttributionCorrection(plan, credit, media, target, observed, outcomeByProviderKey) != null;
                var ambiguous = target == null || matches.Count > 1 && (correction == null || repairableProviderDisagreement);
                if (target == null) target = plan.Outcomes.FirstOrDefault(x => x.SourceEmbyIds.Contains(credit.PersonEmbyId)) ?? plan.Outcomes.First();
                var assignmentId = "credit-" + StableHash(credit.PersonEmbyId + "|" + credit.MediaEmbyId + "|" + credit.Role);
                var disposition = target.TargetKind == IdentityTargetKinds.Unresolved && target.SourceEmbyIds.Contains(credit.PersonEmbyId) || target.TargetKind == IdentityTargetKinds.Existing && target.TargetEmbyId == credit.PersonEmbyId ? "KEEP" : "MOVE";
                var attributions = observed.Where(x => CompatibleAttribution(media.MediaType, roleCategory, x.RoleCategory) && outcomeByProviderKey.ContainsKey(x.PersonKey))
                    .GroupBy(x => x.Provider + "|" + x.ProviderMediaId + "|" + x.ProviderPersonId + "|" + x.Role, StringComparer.Ordinal).Select(x => x.First())
                    .OrderBy(x => x.Provider, StringComparer.Ordinal).ThenBy(x => x.ProviderPersonId, StringComparer.Ordinal).ThenBy(x => x.Role, StringComparer.Ordinal)
                    .Select(x => new IdentityCreditAttribution
                    {
                        Provider = x.Provider, ProviderMediaId = x.ProviderMediaId, ProviderPersonId = x.ProviderPersonId,
                        PersonName = x.PersonName, Role = x.Role, RoleCategory = x.RoleCategory,
                        OutcomeId = outcomeByProviderKey[x.PersonKey].OutcomeId
                    }).ToList();
                plan.Credits.Add(new IdentityCreditOutcome
                {
                    AssignmentId = assignmentId, SourcePersonEmbyId = credit.PersonEmbyId, TargetOutcomeId = target.OutcomeId, MediaEmbyId = media.EmbyId,
                    MediaType = media.MediaType, MediaName = media.Name, Role = credit.Role, TmdbId = media.TmdbId, TvdbId = media.TvdbId, TvdbSlug = media.TvdbSlug, ImdbId = media.ImdbId,
                    Disposition = disposition, CorrectionRequired = ambiguous,
                    Rationale = repairableProviderDisagreement ? "A local assignment exists, but a conflicting provider title attribution still requires correction." : ambiguous ? "More than one materially different identity can receive this credit." : AttributionRationale(attributions, target) ,
                    Attributions = attributions
                });
                if (ambiguous) BuildCreditQuestion(plan, input, credit, media, assignmentId, observed, outcomeByProviderKey);
            }
        }

        private static void BuildCreditQuestion(IdentityCasePlan plan, ResolutionInput input, LocalCredit credit, MediaSeed media, string assignmentId, List<ObservedProviderCredit> observed, IReadOnlyDictionary<string, IdentityOutcome> outcomeByProviderKey)
        {
            var q = new IdentityQuestion { QuestionId = "question-" + assignmentId, Kind = CorrectionKinds.LocalCreditTarget, AssignmentId = assignmentId, Narrative = "Which person should receive " + media.Name + " — " + credit.Role + "?" };
            foreach (var outcome in plan.Outcomes.Where(x => x.TargetKind != IdentityTargetKinds.Unresolved && x.ProviderIds.Any()))
            {
                var target = outcome.TargetKind == IdentityTargetKinds.Existing ? "existing:" + outcome.TargetEmbyId : "outcome:" + outcome.OutcomeId;
                var correction = ProviderAttributionCorrection(plan, credit, media, outcome, observed, outcomeByProviderKey)
                    ?? new ProviderCorrection { Kind = CorrectionKinds.LocalCreditTarget, Operation = CorrectionOperations.Replace, EmbyId = media.EmbyId, CurrentValue = credit.PersonEmbyId + "|" + credit.Role, ReplacementValue = target, Reason = "OPERATOR_MEDIA_ASSIGNMENT", Note = "Selected from identity case " + plan.CaseId, Enabled = true };
                var providerAssertion = correction.Kind == CorrectionKinds.MediaCredit;
                var caption = (providerAssertion ? "Provider credit belongs to " : "Assign to ") + TargetCaption(outcome);
                var effect = providerAssertion
                    ? correction.Operation == CorrectionOperations.Unusable
                        ? correction.Provider.ToUpperInvariant() + " " + correction.MediaType + " " + correction.ProviderMediaId + " attribution to person " + correction.ProviderPersonId + " will be ignored; the complete identity case will then be recalculated."
                        : correction.Provider.ToUpperInvariant() + " " + correction.MediaType + " " + correction.ProviderMediaId + " will replace person " + correction.ProviderPersonId + " with " + correction.ReplacementValue + "; the complete identity case will then be recalculated."
                    : "The complete projection will be recalculated with this media credit assigned to " + TargetCaption(outcome) + ".";
                q.Choices.Add(Choice(q.QuestionId, target, caption, effect, correction));
            }
            plan.Questions.Add(q);
        }

        private static ProviderCorrection ProviderAttributionCorrection(IdentityCasePlan plan, LocalCredit credit, MediaSeed media, IdentityOutcome selected, IEnumerable<ObservedProviderCredit> observed, IReadOnlyDictionary<string, IdentityOutcome> outcomeByProviderKey)
        {
            var roleCategory = RoleCategory(credit.Role);
            var conflicts = observed.Where(x => CompatibleAttribution(media.MediaType, roleCategory, x.RoleCategory) && outcomeByProviderKey.TryGetValue(x.PersonKey, out var owner) && owner.OutcomeId != selected.OutcomeId)
                .GroupBy(x => x.Provider + "|" + x.MediaType + "|" + x.ProviderMediaId + "|" + x.ProviderPersonId + "|" + x.Role, StringComparer.Ordinal).Select(x => x.First()).ToList();
            if (conflicts.Count != 1) return null;
            var conflict = conflicts[0];
            var replacements = selected.ProviderIds.Where(x => x.Source == "native" && x.Provider == conflict.Provider).Select(x => x.ProviderId).Distinct(StringComparer.Ordinal).ToList();
            if (replacements.Count == 0)
                return UnusableAttribution(plan, credit, media, conflict.Provider, conflict.MediaType, conflict.ProviderMediaId, conflict.ProviderPersonId, conflict.Role);
            if (replacements.Count != 1 || replacements[0] == conflict.ProviderPersonId) return null;
            return new ProviderCorrection
            {
                Kind = CorrectionKinds.MediaCredit, Operation = CorrectionOperations.Replace, Provider = conflict.Provider, MediaType = conflict.MediaType,
                ProviderMediaId = conflict.ProviderMediaId, ProviderPersonId = conflict.ProviderPersonId, CurrentValue = conflict.Role, ReplacementValue = replacements[0],
                Reason = "OPERATOR_PROVIDER_ATTRIBUTION", Note = "Selected from identity case " + plan.CaseId + " for Emby media " + media.EmbyId, Enabled = true
            };
        }

        private static ProviderCorrection UnusableAttribution(IdentityCasePlan plan, LocalCredit credit, MediaSeed media, string provider, string mediaType, string providerMediaId, string providerPersonId, string role) => new ProviderCorrection
        {
            Kind = CorrectionKinds.MediaCredit, Operation = CorrectionOperations.Unusable, Provider = provider, MediaType = mediaType,
            ProviderMediaId = providerMediaId, ProviderPersonId = providerPersonId, CurrentValue = role,
            Reason = "OPERATOR_PROVIDER_ATTRIBUTION", Note = "Selected from identity case " + plan.CaseId + " for Emby media " + media.EmbyId + " and current Emby person " + credit.PersonEmbyId, Enabled = true
        };

        private static void BuildIdentityQuestions(IdentityCasePlan plan, ResolutionInput input, List<OutcomeBuilder> builders)
        {
            for (var index = 0; index < builders.Count; index++)
            {
                var outcome = plan.Outcomes[index];
                if (outcome.TargetKind != IdentityTargetKinds.Unresolved) continue;
                if (plan.Questions.Any(q => q.Kind == CorrectionKinds.MediaCredit && !string.IsNullOrWhiteSpace(q.AssignmentId) && plan.Credits.Any(c => c.AssignmentId == q.AssignmentId && c.TargetOutcomeId == outcome.OutcomeId))) continue;
                var key = builders[index].Keys.OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
                var split = (key ?? ":").Split(new[] { ':' }, 2);
                var conflicts = outcome.ProviderIds.GroupBy(x => x.Provider, StringComparer.OrdinalIgnoreCase).Where(x => x.Select(y => y.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1).ToList();
                if (conflicts.Count > 0)
                {
                    var externalQuestion = new IdentityQuestion { QuestionId = "question-external-" + StableHash(key), Kind = CorrectionKinds.PersonExternalId, OutcomeId = outcome.OutcomeId, Narrative = "Provider person records contain conflicting cross-references. Which specific cross-reference correction is true? Choose 'discard' when the provider has no valid replacement ID." };
                    foreach (var conflict in conflicts)
                    foreach (var owner in outcome.ProviderIds.Where(x => x.Source == "native" && x.Provider != conflict.Key))
                    foreach (var current in conflict.Where(x => x.Source == "external").GroupBy(x => x.ProviderId, StringComparer.OrdinalIgnoreCase).Select(x => x.First()))
                    {
                        foreach (var candidate in conflict.Where(x => x.Source == "native" && !string.Equals(x.ProviderId, current.ProviderId, StringComparison.OrdinalIgnoreCase)).GroupBy(x => x.ProviderId, StringComparer.OrdinalIgnoreCase).Select(x => x.First()))
                            externalQuestion.Choices.Add(Choice(externalQuestion.QuestionId, owner.Provider + ":" + owner.ProviderId + ":" + current.ProviderId + "->" + candidate.ProviderId,
                                owner.Provider.ToUpperInvariant() + " " + owner.ProviderId + ": " + candidate.Provider.ToUpperInvariant() + " cross-reference " + current.ProviderId + " → " + candidate.ProviderId,
                                "Replace this exact provider-person cross-reference and recalculate the complete case.",
                                new ProviderCorrection { Kind = CorrectionKinds.PersonExternalId, Operation = CorrectionOperations.Replace, Provider = owner.Provider, ProviderPersonId = owner.ProviderId, FieldName = candidate.Provider, CurrentValue = current.ProviderId, ReplacementValue = candidate.ProviderId, Reason = "OPERATOR_EXTERNAL_ID", Note = "Selected from identity case " + plan.CaseId, Enabled = true }));
                        externalQuestion.Choices.Add(Choice(externalQuestion.QuestionId, owner.Provider + ":" + owner.ProviderId + ":" + current.Provider + ":" + current.ProviderId + ":unusable",
                            owner.Provider.ToUpperInvariant() + " " + owner.ProviderId + ": discard " + current.Provider.ToUpperInvariant() + " cross-reference " + current.ProviderId + " (no replacement)",
                            "Ignore this exact provider-person cross-reference and recalculate the complete case.",
                            new ProviderCorrection { Kind = CorrectionKinds.PersonExternalId, Operation = CorrectionOperations.Unusable, Provider = owner.Provider, ProviderPersonId = owner.ProviderId, FieldName = current.Provider, CurrentValue = current.ProviderId, Reason = "OPERATOR_EXTERNAL_ID", Note = "Selected from identity case " + plan.CaseId, Enabled = true }));
                    }
                    if (externalQuestion.Choices.Count > 0)
                    {
                        plan.Questions.Add(externalQuestion);
                        continue;
                    }
                }
                var q = new IdentityQuestion { QuestionId = "question-target-" + StableHash(key), Kind = CorrectionKinds.IdentityTarget, OutcomeId = outcome.OutcomeId, Narrative = "Which Emby person should represent " + (key ?? outcome.DisplayName) + "?" };
                foreach (var person in plan.CurrentPeople.OrderBy(x => x.Name).ThenBy(x => x.EmbyId))
                    q.Choices.Add(Choice(q.QuestionId, "existing:" + person.EmbyId, "Emby person: " + person.Name + " / " + person.EmbyId, "This provider identity and its media will be assigned to the existing Emby person.", IdentityTargetCorrection(split, "existing:" + person.EmbyId, plan.CaseId)));
                if (outcome.ProviderIds.Any(x => x.Source == "native"))
                    q.Choices.Add(Choice(q.QuestionId, "new", "Emby person: New provider-identified person", "A new person will be created with the listed provider-native identity and at least one assigned media credit.", IdentityTargetCorrection(split, "new", plan.CaseId)));
                plan.Questions.Add(q);
            }
        }

        private static void BuildIdentityRelationQuestions(IdentityCasePlan plan, IEnumerable<ResolutionDecision> decisions)
        {
            EnsureIdentityRelationQuestions(plan, (decisions ?? Enumerable.Empty<ResolutionDecision>())
                .SelectMany(x => x.IdentityRelationReviews ?? new List<IdentityRelationReview>()));
        }

        public static void EnsureIdentityRelationQuestions(IdentityCasePlan plan, IEnumerable<IdentityRelationReview> reviews)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var existing = new HashSet<string>(plan.Questions.SelectMany(x => x.Choices)
                .Where(x => x?.Correction?.Kind == CorrectionKinds.IdentityRelation)
                .Select(x => RelationKey(x.Correction.Provider, x.Correction.ProviderPersonId, x.Correction.SecondaryProvider, x.Correction.SecondaryId)), StringComparer.Ordinal);
            var relations = (reviews ?? Enumerable.Empty<IdentityRelationReview>())
                .GroupBy(RelationKey, StringComparer.Ordinal)
                .Select(x => x.First())
                .Where(x => !existing.Contains(RelationKey(x)))
                .OrderBy(RelationKey, StringComparer.Ordinal)
                .ToList();
            foreach (var relation in relations)
            {
                var key = RelationKey(relation);
                var questionId = "question-relation-" + StableHash(key);
                var left = relation.LeftProvider.ToUpperInvariant() + " " + relation.LeftProviderPersonId;
                var right = relation.RightProvider.ToUpperInvariant() + " " + relation.RightProviderPersonId;
                var question = new IdentityQuestion
                {
                    QuestionId = questionId,
                    Kind = CorrectionKinds.IdentityRelation,
                    Narrative = left + " and " + right + " have positive but non-automatic identity evidence. Arrange the final Person Builder layout to confirm whether they represent the same person or different people."
                };
                question.Choices.Add(Choice(questionId, "same", "Same person", "Place both provider IDs on one active Person Builder row. Apply will persist the confirmed identity relation with the final Emby layout.", IdentityRelationCorrection(relation, CorrectionOperations.Same, plan.CaseId)));
                question.Choices.Add(Choice(questionId, "different", "Different people", "Keep the provider IDs on different active Person Builder rows. Apply will persist the rejected identity relation with the final Emby layout.", IdentityRelationCorrection(relation, CorrectionOperations.Different, plan.CaseId)));
                plan.Questions.Add(question);
            }
        }

        private static ProviderCorrection IdentityRelationCorrection(IdentityRelationReview relation, string operation, string caseId) => new ProviderCorrection
        {
            Kind = CorrectionKinds.IdentityRelation,
            Operation = operation,
            Provider = relation.LeftProvider,
            ProviderPersonId = relation.LeftProviderPersonId,
            SecondaryProvider = relation.RightProvider,
            SecondaryId = relation.RightProviderPersonId,
            Reason = "OPERATOR_IDENTITY_RELATION",
            Note = "Selected from final Person Builder layout for identity case " + caseId,
            Enabled = true
        };

        private static string RelationKey(IdentityRelationReview relation)
            => RelationKey(relation.LeftProvider, relation.LeftProviderPersonId, relation.RightProvider, relation.RightProviderPersonId);

        private static string RelationKey(string leftProvider, string leftId, string rightProvider, string rightId)
        {
            var left = (leftProvider ?? string.Empty) + ":" + (leftId ?? string.Empty);
            var right = (rightProvider ?? string.Empty) + ":" + (rightId ?? string.Empty);
            return string.CompareOrdinal(left, right) <= 0 ? left + "|" + right : right + "|" + left;
        }

        private static ProviderCorrection IdentityTargetCorrection(string[] key, string replacement, string caseId) => new ProviderCorrection
        {
            Kind = CorrectionKinds.IdentityTarget, Operation = CorrectionOperations.Replace, Provider = key.Length > 0 ? key[0] : ProviderNames.Tmdb,
            ProviderPersonId = key.Length > 1 ? key[1] : string.Empty, ReplacementValue = replacement, Reason = "OPERATOR_IDENTITY_TARGET", Note = "Selected from identity case " + caseId, Enabled = true
        };

        private static IdentityQuestionChoice Choice(string questionId, string id, string caption, string effect, ProviderCorrection correction) => new IdentityQuestionChoice { ChoiceId = questionId + ":" + StableHash(id), Caption = caption, Effect = effect, Correction = correction };

        private static void AddEmptyExistingOutcomes(IdentityCasePlan plan, IEnumerable<LocalPerson> currentPeople)
        {
            foreach (var person in currentPeople.Where(p => !plan.Outcomes.Any(x => x.TargetEmbyId == p.EmbyId || x.TargetKind == IdentityTargetKinds.Unresolved && x.SourceEmbyIds.Contains(p.EmbyId))))
                plan.Outcomes.Add(new IdentityOutcome { OutcomeId = "existing-empty:" + person.EmbyId, SortOrder = 10000 + plan.Outcomes.Count, TargetKind = IdentityTargetKinds.Existing, TargetEmbyId = person.EmbyId, DisplayName = person.Name, SourceEmbyIds = new List<long> { person.EmbyId }, Outcome = "No media is assigned to this existing Emby person in the reviewed result" });
        }

        private static void PreserveUnopposedExistingIds(IdentityCasePlan plan, ResolutionInput input, IEnumerable<LocalPerson> currentPeople)
        {
            var byId = currentPeople.ToDictionary(x => x.EmbyId);
            foreach (var outcome in plan.Outcomes.Where(x => x.TargetEmbyId.HasValue && plan.Credits.Any(c => c.TargetOutcomeId == x.OutcomeId)))
            {
                if (!byId.TryGetValue(outcome.TargetEmbyId.Value, out var current)) continue;
                foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb })
                {
                    if (outcome.ProviderIds.Any(x => x.Provider == provider)) continue;
                    var currentId = LocalId(current, provider);
                    if (string.IsNullOrWhiteSpace(currentId)) continue;
                    if (plan.Outcomes.Where(x => x != outcome).SelectMany(x => x.ProviderIds).Any(x => x.Provider == provider && string.Equals(x.ProviderId, currentId, StringComparison.OrdinalIgnoreCase))) continue;
                    if (BindingExplicitlyDiscredited(input, current, provider, currentId)) continue;
                    outcome.ProviderIds.Add(new IdentityProviderId { Provider = provider, ProviderId = currentId, Source = provider == ProviderNames.Imdb ? "external" : "native" });
                }
                outcome.ProviderIds = outcome.ProviderIds.OrderBy(x => x.Provider, StringComparer.Ordinal).ThenBy(x => x.ProviderId, StringComparer.Ordinal).ToList();
            }
        }

        private static void CompleteSummaries(IdentityCasePlan plan, PlannerIndex planner)
        {
            var creates = plan.Outcomes.Count(x => x.TargetKind == IdentityTargetKinds.New);
            var moves = plan.Credits.Count(x => x.Disposition == "MOVE" && !x.CorrectionRequired);
            var changes = ProviderIdChangeCount(plan, planner);
            var retained = plan.Outcomes.Count(x => x.TargetKind == IdentityTargetKinds.Existing && plan.Credits.Any(c => c.TargetOutcomeId == x.OutcomeId));
            var resultParts = new List<string>();
            var mutationParts = new List<string>();
            if (retained > 0) resultParts.Add("retain " + retained + " person" + (retained == 1 ? string.Empty : "s"));
            if (creates > 0) mutationParts.Add("create " + creates + " person" + (creates == 1 ? string.Empty : "s"));
            if (moves > 0) mutationParts.Add("move " + moves + " credit" + (moves == 1 ? string.Empty : "s"));
            if (changes > 0) mutationParts.Add("change " + changes + " ID" + (changes == 1 ? string.Empty : "s"));
            resultParts.AddRange(mutationParts);
            if (plan.State == IdentityPlanStates.CorrectionRequired && plan.Outcomes.Any(x => x.TargetKind == IdentityTargetKinds.Unresolved))
            {
                plan.ApplyCaption = "Apply Person Builder layout";
                plan.Summary = plan.DisplayName + " contains a provider identity whose destination was not determined automatically. Set the final person, provider-ID and credit layout in Person Builder, then Apply once.";
                return;
            }
            var relationQuestions = plan.Questions.Where(x => x.Kind == CorrectionKinds.IdentityRelation).ToList();
            if (plan.State == IdentityPlanStates.CorrectionRequired && relationQuestions.Count > 0)
            {
                var people = plan.CurrentPeople.Count;
                plan.ApplyCaption = "Apply reviewed Person Builder layout";
                plan.Summary = plan.DisplayName + " currently has " + people + " existing Emby " + (people == 1 ? "person" : "people") + " in this case. " +
                    (relationQuestions.Count == 1 ? "One cross-provider relationship" : relationQuestions.Count + " cross-provider relationships") +
                    " has positive but non-automatic identity evidence. Review the final person, provider-ID and credit layout, then Apply once.";
                return;
            }
            if (plan.State == IdentityPlanStates.CorrectionRequired)
            {
                var people = plan.CurrentPeople.Count;
                var ambiguousCredits = plan.Credits.Count(x => x.CorrectionRequired);
                plan.ApplyCaption = "Apply reviewed Person Builder layout";
                plan.Summary = plan.DisplayName + " currently has " + people + " existing Emby " + (people == 1 ? "person" : "people") + " in this case. " +
                    (ambiguousCredits > 0
                        ? ambiguousCredits + " local credit " + (ambiguousCredits == 1 ? "destination is" : "destinations are") + " not determined automatically. "
                        : "The automatic evidence did not determine one complete final layout. ") +
                    "Review the final person, provider-ID and credit layout, then Apply once.";
                return;
            }
            plan.ApplyCaption = mutationParts.Count == 0 ? "No Emby changes required" : "Apply: " + string.Join(", ", mutationParts);
            var outcomes = plan.Outcomes.Count(x => x.TargetKind != IdentityTargetKinds.Unresolved && plan.Credits.Any(c => c.TargetOutcomeId == x.OutcomeId));
            plan.Summary = plan.DisplayName + " will become " + outcomes + " provider-identified Emby " + (outcomes == 1 ? "person" : "people") + ". " + (mutationParts.Count == 0 ? "No Emby changes are required." : char.ToUpperInvariant(resultParts[0][0]) + resultParts[0].Substring(1) + (resultParts.Count > 1 ? ", " + string.Join(", ", resultParts.Skip(1)) : string.Empty) + ".");
            if (plan.CaseType == "Provider records agree" && changes > 0)
                AppendWarning(plan, "The provider records agree with each other, but the current Emby person still differs by " + changes + " provider ID" + (changes == 1 ? string.Empty : "s") + "; the reviewed plan shows that pending Emby alignment explicitly.");
            if (plan.State == IdentityPlanStates.CorrectionRequired) plan.Summary += " The final Person Builder layout remains an operator decision.";
        }

        private static bool BindingExplicitlyDiscredited(ResolutionInput input, LocalPerson person, string provider, string providerPersonId)
        {
            return ProviderCorrectionOverlay.ExplicitlyDiscreditedLocalBindings(input, person).Contains(provider + ":" + providerPersonId);
        }

        private static void PruneUnassignedNewOutcomes(IdentityCasePlan plan)
        {
            var referenced = new HashSet<string>(plan.Questions.Where(x => !string.IsNullOrWhiteSpace(x.OutcomeId)).Select(x => x.OutcomeId), StringComparer.Ordinal);
            foreach (var value in plan.Questions.SelectMany(x => x.Choices).Select(x => x.Correction?.ReplacementValue).Where(x => (x ?? string.Empty).StartsWith("outcome:", StringComparison.Ordinal))) referenced.Add(value.Substring(8));
            if (plan.Questions.Any(x => x.Kind == CorrectionKinds.LocalCreditTarget))
                foreach (var outcome in plan.Outcomes.Where(x => x.TargetKind == IdentityTargetKinds.New)) referenced.Add(outcome.OutcomeId);
            plan.Outcomes.RemoveAll(x => x.TargetKind == IdentityTargetKinds.New && !plan.Credits.Any(c => c.TargetOutcomeId == x.OutcomeId) && !referenced.Contains(x.OutcomeId));
        }

        private static int ProviderIdChangeCount(IdentityCasePlan plan, PlannerIndex planner)
        {
            var count = 0;
            foreach (var outcome in plan.Outcomes.Where(x => x.TargetEmbyId.HasValue))
            {
                var current = planner.PersonById(outcome.TargetEmbyId.Value);
                if (current == null) continue;
                foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb })
                {
                    var before = LocalId(current, provider);
                    var after = PreferredProviderId(outcome, provider);
                    if (!string.Equals(before ?? string.Empty, after ?? string.Empty, StringComparison.OrdinalIgnoreCase)) count++;
                }
            }
            return count;
        }

        private static List<List<ResolutionDecision>> CaseGroups(List<ResolutionDecision> decisions)
        {
            var parent = Enumerable.Range(0, decisions.Count).ToArray();
            var rank = new byte[decisions.Count];
            var byProviderKey = new Dictionary<string, int>(StringComparer.Ordinal);
            var byAnchor = new Dictionary<long, int>();

            for (var index = 0; index < decisions.Count; index++)
            {
                foreach (var key in Keys(decisions[index].ProviderKeys).Distinct(StringComparer.Ordinal))
                {
                    if (byProviderKey.TryGetValue(key, out var existing)) Union(parent, rank, index, existing);
                    else byProviderKey[key] = index;
                }
                if (!decisions[index].AnchorEmbyPersonId.HasValue) continue;
                var anchor = decisions[index].AnchorEmbyPersonId.Value;
                if (byAnchor.TryGetValue(anchor, out var anchored)) Union(parent, rank, index, anchored);
                else byAnchor[anchor] = index;
            }

            return decisions.Select((decision, index) => new { Decision = decision, Root = Find(parent, index) })
                .GroupBy(x => x.Root)
                .Select(x => x.Select(y => y.Decision).OrderBy(y => y.DecisionId, StringComparer.Ordinal).ToList())
                .OrderBy(x => x[0].DecisionId, StringComparer.Ordinal)
                .ToList();
        }

        private static int Find(int[] parent, int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }
            return value;
        }

        private static void Union(int[] parent, byte[] rank, int left, int right)
        {
            var leftRoot = Find(parent, left);
            var rightRoot = Find(parent, right);
            if (leftRoot == rightRoot) return;
            if (rank[leftRoot] < rank[rightRoot]) parent[leftRoot] = rightRoot;
            else
            {
                parent[rightRoot] = leftRoot;
                if (rank[leftRoot] == rank[rightRoot]) rank[leftRoot]++;
            }
        }

        public static string PreferredProviderId(IdentityOutcome outcome, string provider) => outcome?.ProviderIds.Where(x => x.Provider == provider).OrderBy(x => x.Source == "native" ? 0 : 1).Select(x => x.ProviderId).FirstOrDefault();

        private static bool HasNativeProviderIdConflict(IEnumerable<IdentityProviderId> ids) => ids.Where(x => x.Source == "native").GroupBy(x => x.Provider, StringComparer.OrdinalIgnoreCase).Any(x => x.Select(y => y.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);

        private static bool CanRemainOneIdentity(List<ResolutionClusterSnapshot> clusters, ResolutionInput input, PlannerIndex index)
        {
            var keys = new HashSet<string>(clusters.SelectMany(x => x.ProviderKeys), StringComparer.Ordinal);
            var people = keys.Select(x => index.ProviderPeople.TryGetValue(x, out var person) ? person : null).Where(x => x != null).ToList();
            if (people.GroupBy(x => x.Provider).Any(x => x.Count() > 1)) return false;
            var stable = people.SelectMany(x => x.ExternalIds.Where(y => string.Equals(y.Key, ProviderNames.Imdb, StringComparison.OrdinalIgnoreCase) || string.Equals(y.Key, ProviderNames.Wikidata, StringComparison.OrdinalIgnoreCase)).Select(y => y.Key.ToLowerInvariant() + ":" + y.Value)).ToList();
            if (stable.GroupBy(x => x.Substring(0, x.IndexOf(':'))).Any(x => x.Select(y => y.Substring(y.IndexOf(':') + 1)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)) return false;
            var birthdays = people.Select(x => x.Birthday).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
            if (birthdays.Count > 1) return false;
            return !input.Bridges.Any(x => x.IsRejected && keys.Contains(x.ProviderA + ":" + x.ProviderIdA) && keys.Contains(x.ProviderB + ":" + x.ProviderIdB));
        }

        private static ProviderIdProjection FinalProviderIds(IEnumerable<string> keys, IDictionary<string, ProviderPerson> people, LocalPerson selected)
        {
            var result = new List<IdentityProviderId>();
            var keyList = keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
            foreach (var key in keyList)
            {
                var split = key.Split(new[] { ':' }, 2);
                if (split.Length == 2 && (split[0] == ProviderNames.Tmdb || split[0] == ProviderNames.Tvdb)) result.Add(new IdentityProviderId { Provider = split[0], ProviderId = split[1], Source = "native" });
            }

            var warnings = new List<string>();
            var providerPeople = keyList.Select(x => people.TryGetValue(x, out var person) ? person : null).Where(x => x != null).ToList();
            var nativeByProvider = result.Where(x => x.Source == "native").GroupBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Select(y => y.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
            var crosswalkClaims = providerPeople.SelectMany(person => person.ExternalIds.Where(x => string.Equals(x.Key, ProviderNames.Tmdb, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Key, ProviderNames.Tvdb, StringComparison.OrdinalIgnoreCase))
                .Select(x => new { Claimant = person, Provider = x.Key.ToLowerInvariant(), Id = x.Value })).Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToList();
            foreach (var group in crosswalkClaims.GroupBy(x => x.Provider, StringComparer.OrdinalIgnoreCase))
            {
                List<string> native;
                var claims = group.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (!nativeByProvider.TryGetValue(group.Key, out native) || native.Count == 0)
                {
                    if (claims.Count == 1) result.Add(new IdentityProviderId { Provider = group.Key, ProviderId = claims[0], Source = "external" });
                    else if (claims.Count > 1) warnings.Add("Provider crosswalk warning: the role-aligned records claim multiple " + group.Key.ToUpperInvariant() + " person IDs (" + string.Join(", ", claims) + "). No " + group.Key.ToUpperInvariant() + " ID will be inferred from the disagreement. Credit ownership remains unchanged.");
                    continue;
                }
                if (native.Count != 1) continue;
                foreach (var crosswalk in group.Where(x => !string.Equals(native[0], x.Id, StringComparison.OrdinalIgnoreCase)))
                    warnings.Add("Provider crosswalk warning: " + crosswalk.Claimant.Provider.ToUpperInvariant() + " person " + crosswalk.Claimant.ProviderId + " claims " + group.Key.ToUpperInvariant() + " person " + crosswalk.Id + ", while the role-aligned native " + group.Key.ToUpperInvariant() + " person is " + native[0] + ". Credit ownership remains unchanged.");
            }

            var imdbClaims = providerPeople.Where(x => x.ExternalIds.ContainsKey(ProviderNames.Imdb)).Select(x => new { x.Provider, x.ProviderId, Id = x.ExternalIds[ProviderNames.Imdb] })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToList();
            var imdbIds = imdbClaims.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (imdbIds.Count == 1) result.Add(new IdentityProviderId { Provider = ProviderNames.Imdb, ProviderId = imdbIds[0], Source = "external" });
            else if (imdbIds.Count > 1)
            {
                var claims = string.Join("; ", imdbClaims.OrderBy(x => x.Provider, StringComparer.Ordinal).ThenBy(x => x.ProviderId, StringComparer.Ordinal).Select(x => x.Provider.ToUpperInvariant() + " " + x.ProviderId + " → " + x.Id));
                var retained = selected == null || string.IsNullOrWhiteSpace(selected.ImdbId) ? "No IMDb ID will be inferred from the disagreement." : "The current Emby IMDb ID " + selected.ImdbId + " is retained.";
                warnings.Add("Provider IMDb warning: the role-aligned person records claim different IMDb IDs (" + claims + "). " + retained + " Credit ownership remains unchanged.");
            }

            return new ProviderIdProjection
            {
                ProviderIds = result.GroupBy(x => x.Provider + ":" + x.ProviderId, StringComparer.OrdinalIgnoreCase).Select(x => x.OrderBy(y => y.Source == "native" ? 0 : 1).First()).OrderBy(x => x.Provider, StringComparer.Ordinal).ThenBy(x => x.ProviderId, StringComparer.Ordinal).ToList(),
                Warnings = warnings.Distinct(StringComparer.Ordinal).ToList()
            };
        }

        private static string AttributionRationale(IReadOnlyCollection<IdentityCreditAttribution> attributions, IdentityOutcome target)
        {
            if (attributions == null || attributions.Count == 0) return "No provider counter-attribution changes the current Emby assignment.";
            var providers = attributions.Select(x => x.Provider).Distinct(StringComparer.Ordinal).Count();
            var outcomes = attributions.Select(x => x.OutcomeId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
            if (providers > 1 && outcomes.Count == 1 && target != null && outcomes[0] == target.OutcomeId) return "The providers agree on the title-credit owner; profile and crosswalk metadata are assessed separately.";
            if (outcomes.Count == 1 && target != null && outcomes[0] == target.OutcomeId) return "One provider title credit identifies the resulting person; missing opposite-provider support is neutral.";
            return "Provider title credits identify the resulting person.";
        }

        private static ProviderCorrection FindIdentityOverride(IEnumerable<ProviderCorrection> corrections, HashSet<string> keys) => (corrections ?? Enumerable.Empty<ProviderCorrection>()).LastOrDefault(x => x.Enabled && x.Kind == CorrectionKinds.IdentityTarget && keys.Contains(x.Provider + ":" + x.ProviderPersonId));
        private static ProviderCorrection FindCreditOverride(IEnumerable<ProviderCorrection> corrections, LocalCredit credit) => (corrections ?? Enumerable.Empty<ProviderCorrection>()).LastOrDefault(x => x.Enabled && x.Kind == CorrectionKinds.LocalCreditTarget && x.EmbyId == credit.MediaEmbyId && x.CurrentValue == credit.PersonEmbyId + "|" + credit.Role);
        private static IdentityOutcome ResolveOverrideTarget(IdentityCasePlan plan, string value)
        {
            if ((value ?? string.Empty).StartsWith("outcome:", StringComparison.Ordinal)) return plan.Outcomes.FirstOrDefault(x => x.OutcomeId == value.Substring(8));
            if ((value ?? string.Empty).StartsWith("existing:", StringComparison.Ordinal)) { long id; return long.TryParse(value.Substring(9), out id) ? plan.Outcomes.FirstOrDefault(x => x.TargetEmbyId == id) : null; }
            if ((value ?? string.Empty).StartsWith("provider:", StringComparison.Ordinal))
            {
                var parts = value.Split(new[] { ':' }, 3);
                return parts.Length == 3 ? plan.Outcomes.FirstOrDefault(x => x.ProviderIds.Any(y => y.Provider == parts[1] && y.ProviderId == parts[2])) : null;
            }
            return null;
        }

        private static void AddProviderCredits(List<ObservedProviderCredit> target, IDictionary<string, List<ObservedProviderCredit>> index, string provider, string type, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            List<ObservedProviderCredit> rows;
            if (index.TryGetValue(provider + ":" + type + ":" + id, out rows)) target.AddRange(rows);
        }
        private static bool CompatibleRole(string a, string b) => string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) || a == "Unknown" || b == "Unknown" || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static bool CompatibleAttribution(string mediaType, string localRole, string providerRole) => mediaType == MediaTypes.Episode || CompatibleRole(localRole, providerRole);
        private static string RoleCategory(string role)
        {
            var value = role ?? string.Empty;
            var colon = value.IndexOf(':');
            return colon > 0 ? value.Substring(0, colon).Trim() : value.Trim();
        }
        private static string TargetCaption(IdentityOutcome outcome) => outcome.TargetKind == IdentityTargetKinds.Existing ? outcome.DisplayName + " — Emby " + outcome.TargetEmbyId : "New person — " + outcome.DisplayName + " — " + string.Join(", ", outcome.ProviderIds.Where(x => x.Source == "native").Select(x => x.Provider.ToUpperInvariant() + " " + x.ProviderId));
        private static IEnumerable<string> Keys(string value) => (value ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Contains(":"));
        private static IEnumerable<string> CurrentKeys(LocalPerson person)
        {
            if (!string.IsNullOrWhiteSpace(person.TmdbId)) yield return ProviderNames.Tmdb + ":" + person.TmdbId;
            if (!string.IsNullOrWhiteSpace(person.TvdbId)) yield return ProviderNames.Tvdb + ":" + person.TvdbId;
        }
        private static string LocalId(LocalPerson person, string provider) => provider == ProviderNames.Tmdb ? person.TmdbId : provider == ProviderNames.Tvdb ? person.TvdbId : person.ImdbId;
        private static string FriendlyType(IEnumerable<ResolutionDecision> decisions)
        {
            var decisionList = decisions.ToList();
            var relations = decisionList.SelectMany(x => x.IdentityRelationReviews ?? new List<IdentityRelationReview>()).ToList();
            if (relations.Any(x => !x.HasConflict)) return "Possible cross-provider identity match";
            if (relations.Count > 0) return "Cross-provider identity requires review";
            var values = decisionList.Select(x => x.Status).Distinct(StringComparer.Ordinal).ToList();
            if (values.Count > 1) return "Mixed identity issues";
            switch (values.FirstOrDefault()) { case "SPLIT": return "Possible combined identities"; case "CONFLATION": return "Provider attribution disagreement"; case "REALIGNMENT": return "Local credit ownership requires review"; case "DRIFT": return "Emby provider-ID drift"; case "ORPHAN": return "Provider identity missing"; case "MATCH_WITH_CONFLICT": return "Identity aligned; provider metadata warning"; case "MATCH": return "Provider records agree"; default: return values.FirstOrDefault() ?? "Identity case"; }
        }
        private static void AppendWarning(IdentityCasePlan plan, string warning)
        {
            if (string.IsNullOrWhiteSpace(warning)) return;
            plan.Warning = string.IsNullOrWhiteSpace(plan.Warning) ? warning : plan.Warning + Environment.NewLine + warning;
        }
        private static string Canonical(IdentityCasePlan plan) => plan.CaseId + "|" + plan.State + "|" + string.Join(";", plan.Outcomes.OrderBy(x => x.OutcomeId).Select(x => x.OutcomeId + ":" + x.TargetKind + ":" + x.TargetEmbyId + ":" + string.Join(",", x.ProviderIds.Select(y => y.Provider + "=" + y.ProviderId)))) + "|" + string.Join(";", plan.Credits.OrderBy(x => x.AssignmentId).Select(x => x.AssignmentId + ":" + x.TargetOutcomeId + ":" + x.CorrectionRequired + ":" + string.Join(",", x.Attributions.OrderBy(y => y.Provider, StringComparer.Ordinal).ThenBy(y => y.ProviderPersonId, StringComparer.Ordinal).Select(y => y.Provider + "=" + y.ProviderPersonId + "@" + y.OutcomeId + "#" + y.Role)))) + "|" + string.Join(";", plan.Questions.OrderBy(x => x.QuestionId, StringComparer.Ordinal).Select(x => x.QuestionId + ":" + x.Kind + ":" + string.Join(",", x.Choices.OrderBy(y => y.ChoiceId, StringComparer.Ordinal).Select(y => y.Correction?.Kind + "=" + y.Correction?.Operation))));
        private static string StableHash(string value)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)).Take(8).Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
        }
        private sealed class OutcomeBuilder { public List<ResolutionClusterSnapshot> Clusters { get; set; } public HashSet<string> Keys { get; set; } public LocalPerson Selected { get; set; } public ProviderCorrection Override { get; set; } }
        private sealed class ProviderIdProjection { public List<IdentityProviderId> ProviderIds { get; set; } = new List<IdentityProviderId>(); public List<string> Warnings { get; set; } = new List<string>(); }
        private sealed class ClusterIndex
        {
            private readonly Dictionary<string, List<ResolutionClusterSnapshot>> byProviderKey = new Dictionary<string, List<ResolutionClusterSnapshot>>(StringComparer.Ordinal);
            private readonly Dictionary<long, List<ResolutionClusterSnapshot>> byAnchor = new Dictionary<long, List<ResolutionClusterSnapshot>>();

            public ClusterIndex(IEnumerable<ResolutionClusterSnapshot> clusters)
            {
                foreach (var cluster in clusters ?? Enumerable.Empty<ResolutionClusterSnapshot>())
                {
                    foreach (var key in cluster.ProviderKeys ?? new List<string>()) Add(byProviderKey, key, cluster);
                    if (cluster.AnchorEmbyPersonId.HasValue) Add(byAnchor, cluster.AnchorEmbyPersonId.Value, cluster);
                }
            }

            public List<ResolutionClusterSnapshot> Find(IEnumerable<string> providerKeys, IEnumerable<long> anchors)
            {
                var matches = new HashSet<ResolutionClusterSnapshot>();
                foreach (var key in providerKeys)
                    if (byProviderKey.TryGetValue(key, out var keyed)) matches.UnionWith(keyed);
                foreach (var anchor in anchors)
                    if (byAnchor.TryGetValue(anchor, out var anchored)) matches.UnionWith(anchored);
                return matches.OrderBy(x => x.ClusterId, StringComparer.Ordinal).ToList();
            }

            private static void Add<TKey>(Dictionary<TKey, List<ResolutionClusterSnapshot>> index, TKey key, ResolutionClusterSnapshot cluster)
            {
                if (!index.TryGetValue(key, out var rows)) index[key] = rows = new List<ResolutionClusterSnapshot>();
                rows.Add(cluster);
            }
        }

        private sealed class PlannerIndex
        {
            private readonly IEnumerable<LocalPerson> globalPeople;
            private Dictionary<long, LocalPerson> globalPeopleById;
            public Dictionary<long, LocalPerson> LocalPeopleById { get; }
            public Dictionary<string, List<LocalPerson>> LocalPeopleByProviderKey { get; } = new Dictionary<string, List<LocalPerson>>(StringComparer.Ordinal);
            public Dictionary<long, int> LocalCreditCounts { get; }
            public Dictionary<long, List<LocalCredit>> LocalCreditsByPerson { get; }
            public Dictionary<long, MediaSeed> MediaById { get; }
            public Dictionary<string, ProviderPerson> ProviderPeople { get; }
            public Dictionary<string, List<ObservedProviderCredit>> ProviderCreditsByMedia { get; }
            public PlannerIndex(ResolutionInput input)
            {
                globalPeople = input.GlobalLocalPeople ?? Enumerable.Empty<LocalPerson>();
                LocalPeopleById = input.LocalPeople.GroupBy(x => x.EmbyId).ToDictionary(x => x.Key, x => x.First());
                foreach (var person in LocalPeopleById.Values)
                foreach (var key in CurrentKeys(person))
                {
                    if (!LocalPeopleByProviderKey.TryGetValue(key, out var rows)) LocalPeopleByProviderKey[key] = rows = new List<LocalPerson>();
                    rows.Add(person);
                }
                LocalCreditCounts = input.LocalCredits.GroupBy(x => x.PersonEmbyId).ToDictionary(x => x.Key, x => x.Count());
                LocalCreditsByPerson = input.LocalCredits.GroupBy(x => x.PersonEmbyId).ToDictionary(x => x.Key, x => x.ToList());
                MediaById = input.Media.GroupBy(x => x.EmbyId).ToDictionary(x => x.Key, x => x.First());
                ProviderPeople = input.ProviderPeople.GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
                ProviderCreditsByMedia = input.ProviderCredits.Where(x => !string.IsNullOrWhiteSpace(x.MediaType) && !string.IsNullOrWhiteSpace(x.ProviderMediaId)).GroupBy(x => x.Provider + ":" + x.MediaType + ":" + x.ProviderMediaId, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
            }

            public LocalPerson PersonById(long id)
            {
                LocalPerson person;
                if (LocalPeopleById.TryGetValue(id, out person)) return person;
                if (globalPeopleById == null) globalPeopleById = globalPeople.GroupBy(x => x.EmbyId).ToDictionary(x => x.Key, x => x.First());
                return globalPeopleById.TryGetValue(id, out person) ? person : null;
            }
        }
    }
}
