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
        public const string Blocked = "BLOCKED";
        public const string Applied = "APPLIED";
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
        public string Role { get; set; }
        public string TmdbId { get; set; }
        public string TvdbId { get; set; }
        public string TvdbSlug { get; set; }
        public string ImdbId { get; set; }
        public string Disposition { get; set; }
        public string Rationale { get; set; }
        public bool CorrectionRequired { get; set; }
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
            var result = new List<IdentityCasePlan>();
            foreach (var group in CaseGroups(decisionList))
                result.Add(BuildCase(runId, input, planner, group, clusterList));
            return result;
        }

        private static IdentityCasePlan BuildCase(long runId, ResolutionInput input, PlannerIndex planner, List<ResolutionDecision> decisions, List<ResolutionClusterSnapshot> allClusters)
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

            var clusters = allClusters.Where(x => x.ProviderKeys.Any(providerKeys.Contains) || x.AnchorEmbyPersonId.HasValue && personIds.Contains(x.AnchorEmbyPersonId.Value)).OrderBy(x => x.ClusterId, StringComparer.Ordinal).ToList();
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
                DecisionIds = decisions.Select(x => x.DecisionId).ToList()
            };
            foreach (var warning in decisions.SelectMany(x => x.Evidence ?? new List<EvidenceLine>())
                .Where(x => string.Equals(x.SignalType, "BIRTHDAY", StringComparison.OrdinalIgnoreCase) && string.Equals(x.Verdict, "conflicts", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Narrative).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
                AppendWarning(plan, "Informational metadata warning: " + warning);

            var clusterGroups = clusters.Select(x => new List<ResolutionClusterSnapshot> { x }).ToList();
            var sameAnchor = clusterGroups.Count > 1 && personIds.Count == 1;
            if (sameAnchor && CanRemainOneIdentity(clusterGroups.SelectMany(x => x).ToList(), input, planner))
            {
                clusterGroups = new List<List<ResolutionClusterSnapshot>> { clusters };
                AppendWarning(plan, "Nothing independently links every provider record in this case, but there is no counter-evidence and Emby currently treats them as the same person.");
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
                    if (long.TryParse(targetOverride.ReplacementValue.Substring(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) selected = input.GlobalLocalPeople.Concat(input.LocalPeople).FirstOrDefault(x => x.EmbyId == id);
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
                var ids = FinalProviderIds(builder.Keys, providerPeople);
                var targetKind = builder.Selected != null ? IdentityTargetKinds.Existing : ids.Any(x => x.Source == "native") ? IdentityTargetKinds.New : IdentityTargetKinds.Unresolved;
                if (ids.GroupBy(x => x.Provider, StringComparer.OrdinalIgnoreCase).Any(x => x.Select(y => y.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)) targetKind = IdentityTargetKinds.Unresolved;
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
                    Outcome = targetKind == IdentityTargetKinds.Existing ? "Retain Emby person " + builder.Selected.EmbyId : targetKind == IdentityTargetKinds.New ? "Create provider-identified Emby person" : "Correction required before an Emby target can be selected"
                });
            }

            AddEmptyExistingOutcomes(plan, currentPeople);
            BuildCredits(plan, input, planner, provisional);
            PreserveUnopposedExistingIds(plan, currentPeople);
            BuildIdentityQuestions(plan, input, provisional);

            var blocked = decisions.Any(x => x.Action == ResolutionActions.IncompleteScope);
            plan.State = blocked ? IdentityPlanStates.Blocked : plan.Questions.Count > 0 || plan.Outcomes.Any(x => x.TargetKind == IdentityTargetKinds.Unresolved) || plan.Credits.Any(x => x.CorrectionRequired) ? IdentityPlanStates.CorrectionRequired : IdentityPlanStates.Complete;
            CompleteSummaries(plan, input);
            plan.PlanHash = StableHash(Canonical(plan));
            return plan;
        }

        private static void BuildCredits(IdentityCasePlan plan, ResolutionInput input, PlannerIndex index, List<OutcomeBuilder> builders)
        {
            var mediaById = index.MediaById;
            var outcomeByBuilder = builders.Select((x, i) => new { Builder = x, Outcome = plan.Outcomes[i] }).ToList();
            var providerCreditIndex = index.ProviderCreditsByMedia;
            var relevantCredits = plan.CurrentPeople.SelectMany(x => index.LocalCreditsByPerson.TryGetValue(x.EmbyId, out var rows) ? rows : Enumerable.Empty<LocalCredit>());
            foreach (var credit in relevantCredits.Where(x => mediaById.ContainsKey(x.MediaEmbyId)).OrderBy(x => x.MediaEmbyId).ThenBy(x => x.Role, StringComparer.Ordinal).ThenBy(x => x.PersonEmbyId))
            {
                var media = mediaById[credit.MediaEmbyId];
                var observed = new List<ObservedProviderCredit>();
                AddProviderCredits(observed, providerCreditIndex, ProviderNames.Tmdb, media.MediaType, media.TmdbId);
                AddProviderCredits(observed, providerCreditIndex, ProviderNames.Tvdb, media.MediaType, media.TvdbId);
                var roleCategory = RoleCategory(credit.Role);
                var matches = outcomeByBuilder.Where(x => observed.Any(c => x.Builder.Keys.Contains(c.PersonKey) && CompatibleRole(roleCategory, c.RoleCategory))).Select(x => x.Outcome).Distinct().ToList();
                var correction = FindCreditOverride(input.ActiveCorrections, credit);
                IdentityOutcome target = null;
                if (correction != null) target = ResolveOverrideTarget(plan, correction.ReplacementValue);
                if (target == null && matches.Count == 1) target = matches[0];
                if (target == null && matches.Count == 0) target = plan.Outcomes.FirstOrDefault(x => x.TargetEmbyId == credit.PersonEmbyId) ?? plan.Outcomes.FirstOrDefault(x => x.SourceEmbyIds.Contains(credit.PersonEmbyId));
                var ambiguous = target == null || matches.Count > 1 && correction == null;
                if (target == null) target = plan.Outcomes.FirstOrDefault(x => x.SourceEmbyIds.Contains(credit.PersonEmbyId)) ?? plan.Outcomes.First();
                var assignmentId = "credit-" + StableHash(credit.PersonEmbyId + "|" + credit.MediaEmbyId + "|" + credit.Role);
                var disposition = target.TargetKind == IdentityTargetKinds.Existing && target.TargetEmbyId == credit.PersonEmbyId ? "KEEP" : "MOVE";
                plan.Credits.Add(new IdentityCreditOutcome
                {
                    AssignmentId = assignmentId, SourcePersonEmbyId = credit.PersonEmbyId, TargetOutcomeId = target.OutcomeId, MediaEmbyId = media.EmbyId,
                    MediaType = media.MediaType, MediaName = media.Name, Role = credit.Role, TmdbId = media.TmdbId, TvdbId = media.TvdbId, TvdbSlug = media.TvdbSlug, ImdbId = media.ImdbId,
                    Disposition = disposition, CorrectionRequired = ambiguous,
                    Rationale = ambiguous ? "More than one materially different identity can receive this credit." : matches.Count == 1 ? "Provider title credits identify the resulting person." : "No provider counter-attribution changes the current Emby assignment."
                });
                if (ambiguous) BuildCreditQuestion(plan, input, credit, media, assignmentId);
            }
        }

        private static void BuildCreditQuestion(IdentityCasePlan plan, ResolutionInput input, LocalCredit credit, MediaSeed media, string assignmentId)
        {
            var q = new IdentityQuestion { QuestionId = "question-" + assignmentId, Kind = CorrectionKinds.LocalCreditTarget, AssignmentId = assignmentId, Narrative = "Which person should receive " + media.Name + " — " + credit.Role + "?" };
            foreach (var outcome in plan.Outcomes.Where(x => x.TargetKind != IdentityTargetKinds.Unresolved && x.ProviderIds.Any()))
            {
                var target = outcome.TargetKind == IdentityTargetKinds.Existing ? "existing:" + outcome.TargetEmbyId : "outcome:" + outcome.OutcomeId;
                q.Choices.Add(Choice(q.QuestionId, target, "Assign to " + TargetCaption(outcome), "The complete projection will be recalculated with this media credit assigned to " + TargetCaption(outcome) + ".",
                    new ProviderCorrection { Kind = CorrectionKinds.LocalCreditTarget, Operation = CorrectionOperations.Replace, EmbyId = media.EmbyId, CurrentValue = credit.PersonEmbyId + "|" + credit.Role, ReplacementValue = target, Reason = "OPERATOR_MEDIA_ASSIGNMENT", Note = "Selected from identity case " + plan.CaseId, Enabled = true }));
            }
            plan.Questions.Add(q);
        }

        private static void BuildIdentityQuestions(IdentityCasePlan plan, ResolutionInput input, List<OutcomeBuilder> builders)
        {
            for (var index = 0; index < builders.Count; index++)
            {
                var outcome = plan.Outcomes[index];
                if (outcome.TargetKind != IdentityTargetKinds.Unresolved) continue;
                var key = builders[index].Keys.OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
                var split = (key ?? ":").Split(new[] { ':' }, 2);
                var conflicts = outcome.ProviderIds.GroupBy(x => x.Provider, StringComparer.OrdinalIgnoreCase).Where(x => x.Select(y => y.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1).ToList();
                if (conflicts.Count > 0)
                {
                    var externalQuestion = new IdentityQuestion { QuestionId = "question-external-" + StableHash(key), Kind = CorrectionKinds.PersonExternalId, OutcomeId = outcome.OutcomeId, Narrative = "Which provider person ID is the correct external-ID association for this identity?" };
                    foreach (var conflict in conflicts)
                    foreach (var owner in outcome.ProviderIds.Where(x => x.Source == "native" && x.Provider != conflict.Key))
                    foreach (var candidate in conflict.GroupBy(x => x.ProviderId, StringComparer.OrdinalIgnoreCase).Select(x => x.First()))
                        externalQuestion.Choices.Add(Choice(externalQuestion.QuestionId, owner.Provider + ":" + owner.ProviderId + "->" + candidate.Provider + ":" + candidate.ProviderId,
                            "External ID: " + owner.Provider.ToUpperInvariant() + " " + owner.ProviderId + " → " + candidate.Provider.ToUpperInvariant() + " " + candidate.ProviderId,
                            "The provider cross-reference will be corrected and the complete case recalculated.",
                            new ProviderCorrection { Kind = CorrectionKinds.PersonExternalId, Operation = CorrectionOperations.Replace, Provider = owner.Provider, ProviderPersonId = owner.ProviderId, FieldName = candidate.Provider, ReplacementValue = candidate.ProviderId, Reason = "OPERATOR_EXTERNAL_ID", Note = "Selected from identity case " + plan.CaseId, Enabled = true }));
                    plan.Questions.Add(externalQuestion);
                    continue;
                }
                var q = new IdentityQuestion { QuestionId = "question-target-" + StableHash(key), Kind = CorrectionKinds.IdentityTarget, OutcomeId = outcome.OutcomeId, Narrative = "Which Emby person should represent " + (key ?? outcome.DisplayName) + "?" };
                foreach (var person in plan.CurrentPeople.OrderBy(x => x.Name).ThenBy(x => x.EmbyId))
                    q.Choices.Add(Choice(q.QuestionId, "existing:" + person.EmbyId, "Emby person: " + person.Name + " / " + person.EmbyId, "This provider identity and its media will be assigned to the existing Emby person.", IdentityTargetCorrection(split, "existing:" + person.EmbyId, plan.CaseId)));
                if (outcome.ProviderIds.Any(x => x.Source == "native"))
                    q.Choices.Add(Choice(q.QuestionId, "new", "Emby person: New provider-identified person", "A new person will be created with the listed provider-native identity and at least one assigned media credit.", IdentityTargetCorrection(split, "new", plan.CaseId)));
                plan.Questions.Add(q);
            }
        }

        private static ProviderCorrection IdentityTargetCorrection(string[] key, string replacement, string caseId) => new ProviderCorrection
        {
            Kind = CorrectionKinds.IdentityTarget, Operation = CorrectionOperations.Replace, Provider = key.Length > 0 ? key[0] : ProviderNames.Tmdb,
            ProviderPersonId = key.Length > 1 ? key[1] : string.Empty, ReplacementValue = replacement, Reason = "OPERATOR_IDENTITY_TARGET", Note = "Selected from identity case " + caseId, Enabled = true
        };

        private static IdentityQuestionChoice Choice(string questionId, string id, string caption, string effect, ProviderCorrection correction) => new IdentityQuestionChoice { ChoiceId = questionId + ":" + StableHash(id), Caption = caption, Effect = effect, Correction = correction };

        private static void AddEmptyExistingOutcomes(IdentityCasePlan plan, IEnumerable<LocalPerson> currentPeople)
        {
            foreach (var person in currentPeople.Where(p => !plan.Outcomes.Any(x => x.TargetEmbyId == p.EmbyId)))
                plan.Outcomes.Add(new IdentityOutcome { OutcomeId = "existing-empty:" + person.EmbyId, SortOrder = 10000 + plan.Outcomes.Count, TargetKind = IdentityTargetKinds.Existing, TargetEmbyId = person.EmbyId, DisplayName = person.Name, SourceEmbyIds = new List<long> { person.EmbyId }, Outcome = "Emby will remove this person as no media is assigned" });
        }

        private static void PreserveUnopposedExistingIds(IdentityCasePlan plan, IEnumerable<LocalPerson> currentPeople)
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
                    outcome.ProviderIds.Add(new IdentityProviderId { Provider = provider, ProviderId = currentId, Source = provider == ProviderNames.Imdb ? "external" : "native" });
                }
                outcome.ProviderIds = outcome.ProviderIds.OrderBy(x => x.Provider, StringComparer.Ordinal).ThenBy(x => x.ProviderId, StringComparer.Ordinal).ToList();
            }
        }

        private static void CompleteSummaries(IdentityCasePlan plan, ResolutionInput input)
        {
            foreach (var outcome in plan.Outcomes.Where(x => x.TargetKind == IdentityTargetKinds.New && !plan.Credits.Any(c => c.TargetOutcomeId == x.OutcomeId)))
            {
                outcome.TargetKind = IdentityTargetKinds.Unresolved;
                outcome.Outcome = "Correction required — a new person cannot be created without an assigned media credit";
            }
            var creates = plan.Outcomes.Count(x => x.TargetKind == IdentityTargetKinds.New);
            var moves = plan.Credits.Count(x => x.Disposition == "MOVE" && !x.CorrectionRequired);
            var changes = ProviderIdChangeCount(plan, input);
            var retained = plan.Outcomes.Count(x => x.TargetKind == IdentityTargetKinds.Existing && plan.Credits.Any(c => c.TargetOutcomeId == x.OutcomeId));
            var parts = new List<string>();
            if (retained > 0) parts.Add("retain " + retained + " person" + (retained == 1 ? string.Empty : "s"));
            if (creates > 0) parts.Add("create " + creates + " person" + (creates == 1 ? string.Empty : "s"));
            if (moves > 0) parts.Add("move " + moves + " credit" + (moves == 1 ? string.Empty : "s"));
            if (changes > 0) parts.Add("change " + changes + " ID" + (changes == 1 ? string.Empty : "s"));
            plan.ApplyCaption = "Apply: " + (parts.Count == 0 ? "no Emby changes" : string.Join(", ", parts));
            var outcomes = plan.Outcomes.Count(x => x.TargetKind != IdentityTargetKinds.Unresolved && plan.Credits.Any(c => c.TargetOutcomeId == x.OutcomeId));
            plan.Summary = plan.DisplayName + " will become " + outcomes + " provider-identified Emby " + (outcomes == 1 ? "person" : "people") + ". " + (parts.Count == 0 ? "No Emby changes are required." : char.ToUpperInvariant(parts[0][0]) + parts[0].Substring(1) + (parts.Count > 1 ? ", " + string.Join(", ", parts.Skip(1)) : string.Empty) + ".");
            if (plan.CaseType == "Provider records agree" && changes > 0)
                AppendWarning(plan, "The provider records agree with each other, but the current Emby person still differs by " + changes + " provider ID" + (changes == 1 ? string.Empty : "s") + "; the reviewed plan shows that pending Emby alignment explicitly.");
            if (plan.State == IdentityPlanStates.CorrectionRequired) plan.Summary += " A human correction is required before Apply is available.";
            if (plan.State == IdentityPlanStates.Blocked) plan.Summary += " The current scope is incomplete, so Apply is unavailable.";
        }

        private static int ProviderIdChangeCount(IdentityCasePlan plan, ResolutionInput input)
        {
            var count = 0;
            foreach (var outcome in plan.Outcomes.Where(x => x.TargetEmbyId.HasValue))
            {
                var current = input.LocalPeople.FirstOrDefault(x => x.EmbyId == outcome.TargetEmbyId.Value) ?? input.GlobalLocalPeople.FirstOrDefault(x => x.EmbyId == outcome.TargetEmbyId.Value);
                if (current == null) continue;
                foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb })
                {
                    var before = LocalId(current, provider);
                    var after = outcome.ProviderIds.FirstOrDefault(x => x.Provider == provider)?.ProviderId;
                    if (!string.Equals(before ?? string.Empty, after ?? string.Empty, StringComparison.OrdinalIgnoreCase)) count++;
                }
            }
            return count;
        }

        private static List<List<ResolutionDecision>> CaseGroups(List<ResolutionDecision> decisions)
        {
            var result = new List<List<ResolutionDecision>>();
            var remaining = new HashSet<ResolutionDecision>(decisions);
            while (remaining.Count > 0)
            {
                var first = remaining.OrderBy(x => x.DecisionId, StringComparer.Ordinal).First();
                remaining.Remove(first);
                var group = new List<ResolutionDecision> { first };
                var changed = true;
                while (changed)
                {
                    changed = false;
                    var keys = new HashSet<string>(group.SelectMany(x => Keys(x.ProviderKeys)), StringComparer.Ordinal);
                    var anchors = new HashSet<long>(group.Where(x => x.AnchorEmbyPersonId.HasValue).Select(x => x.AnchorEmbyPersonId.Value));
                    foreach (var candidate in remaining.ToList())
                    {
                        if (!Keys(candidate.ProviderKeys).Any(keys.Contains) && (!candidate.AnchorEmbyPersonId.HasValue || !anchors.Contains(candidate.AnchorEmbyPersonId.Value))) continue;
                        remaining.Remove(candidate); group.Add(candidate); changed = true;
                    }
                }
                result.Add(group.OrderBy(x => x.DecisionId, StringComparer.Ordinal).ToList());
            }
            return result;
        }

        private static bool CanRemainOneIdentity(List<ResolutionClusterSnapshot> clusters, ResolutionInput input, PlannerIndex index)
        {
            var keys = new HashSet<string>(clusters.SelectMany(x => x.ProviderKeys), StringComparer.Ordinal);
            var people = keys.Select(x => index.ProviderPeople.TryGetValue(x, out var person) ? person : null).Where(x => x != null).ToList();
            if (people.GroupBy(x => x.Provider).Any(x => x.Count() > 1)) return false;
            var stable = people.SelectMany(x => x.ExternalIds.Where(y => y.Key == ProviderNames.Imdb || y.Key == ProviderNames.Wikidata).Select(y => y.Key + ":" + y.Value)).ToList();
            if (stable.GroupBy(x => x.Substring(0, x.IndexOf(':'))).Any(x => x.Select(y => y.Substring(y.IndexOf(':') + 1)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)) return false;
            var birthdays = people.Select(x => x.Birthday).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
            if (birthdays.Count > 1) return false;
            return !input.Bridges.Any(x => x.IsRejected && keys.Contains(x.ProviderA + ":" + x.ProviderIdA) && keys.Contains(x.ProviderB + ":" + x.ProviderIdB));
        }

        private static List<IdentityProviderId> FinalProviderIds(IEnumerable<string> keys, IDictionary<string, ProviderPerson> people)
        {
            var result = new List<IdentityProviderId>();
            foreach (var key in keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                var split = key.Split(new[] { ':' }, 2);
                if (split.Length == 2 && (split[0] == ProviderNames.Tmdb || split[0] == ProviderNames.Tvdb)) result.Add(new IdentityProviderId { Provider = split[0], ProviderId = split[1], Source = "native" });
                ProviderPerson person;
                if (!people.TryGetValue(key, out person)) continue;
                foreach (var external in person.ExternalIds.Where(x => x.Key == ProviderNames.Tmdb || x.Key == ProviderNames.Tvdb || x.Key == ProviderNames.Imdb))
                    result.Add(new IdentityProviderId { Provider = external.Key, ProviderId = external.Value, Source = "external" });
            }
            return result.GroupBy(x => x.Provider + ":" + x.ProviderId, StringComparer.OrdinalIgnoreCase).Select(x => x.OrderBy(y => y.Source == "native" ? 0 : 1).First()).OrderBy(x => x.Provider, StringComparer.Ordinal).ThenBy(x => x.ProviderId, StringComparer.Ordinal).ToList();
        }

        private static ProviderCorrection FindIdentityOverride(IEnumerable<ProviderCorrection> corrections, HashSet<string> keys) => (corrections ?? Enumerable.Empty<ProviderCorrection>()).LastOrDefault(x => x.Enabled && x.Kind == CorrectionKinds.IdentityTarget && keys.Contains(x.Provider + ":" + x.ProviderPersonId));
        private static ProviderCorrection FindCreditOverride(IEnumerable<ProviderCorrection> corrections, LocalCredit credit) => (corrections ?? Enumerable.Empty<ProviderCorrection>()).LastOrDefault(x => x.Enabled && x.Kind == CorrectionKinds.LocalCreditTarget && x.EmbyId == credit.MediaEmbyId && x.CurrentValue == credit.PersonEmbyId + "|" + credit.Role);
        private static IdentityOutcome ResolveOverrideTarget(IdentityCasePlan plan, string value)
        {
            if ((value ?? string.Empty).StartsWith("outcome:", StringComparison.Ordinal)) return plan.Outcomes.FirstOrDefault(x => x.OutcomeId == value.Substring(8));
            if ((value ?? string.Empty).StartsWith("existing:", StringComparison.Ordinal)) { long id; return long.TryParse(value.Substring(9), out id) ? plan.Outcomes.FirstOrDefault(x => x.TargetEmbyId == id) : null; }
            return null;
        }

        private static void AddProviderCredits(List<ObservedProviderCredit> target, IDictionary<string, List<ObservedProviderCredit>> index, string provider, string type, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            List<ObservedProviderCredit> rows;
            if (index.TryGetValue(provider + ":" + type + ":" + id, out rows)) target.AddRange(rows);
        }
        private static bool CompatibleRole(string a, string b) => string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) || a == "Unknown" || b == "Unknown" || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
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
            var values = decisions.Select(x => x.Status).Distinct(StringComparer.Ordinal).ToList();
            if (values.Count > 1) return "Mixed identity issues";
            switch (values.FirstOrDefault()) { case "SPLIT": return "Possible combined identities"; case "CONFLATION": return "Provider attribution disagreement"; case "REALIGNMENT": return "Credits assigned to the wrong Emby person"; case "DRIFT": return "Emby provider-ID drift"; case "ORPHAN": return "Provider identity missing"; case "MATCH_WITH_CONFLICT": return "Identity match; metadata differs"; case "MATCH": return "Provider records agree"; default: return values.FirstOrDefault() ?? "Identity case"; }
        }
        private static void AppendWarning(IdentityCasePlan plan, string warning)
        {
            if (string.IsNullOrWhiteSpace(warning)) return;
            plan.Warning = string.IsNullOrWhiteSpace(plan.Warning) ? warning : plan.Warning + Environment.NewLine + warning;
        }
        private static string Canonical(IdentityCasePlan plan) => plan.CaseId + "|" + plan.State + "|" + string.Join(";", plan.Outcomes.OrderBy(x => x.OutcomeId).Select(x => x.OutcomeId + ":" + x.TargetKind + ":" + x.TargetEmbyId + ":" + string.Join(",", x.ProviderIds.Select(y => y.Provider + "=" + y.ProviderId)))) + "|" + string.Join(";", plan.Credits.OrderBy(x => x.AssignmentId).Select(x => x.AssignmentId + ":" + x.TargetOutcomeId + ":" + x.CorrectionRequired));
        private static string StableHash(string value)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)).Take(8).Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
        }
        private sealed class OutcomeBuilder { public List<ResolutionClusterSnapshot> Clusters { get; set; } public HashSet<string> Keys { get; set; } public LocalPerson Selected { get; set; } public ProviderCorrection Override { get; set; } }
        private sealed class PlannerIndex
        {
            public Dictionary<long, LocalPerson> LocalPeopleById { get; }
            public Dictionary<string, List<LocalPerson>> LocalPeopleByProviderKey { get; } = new Dictionary<string, List<LocalPerson>>(StringComparer.Ordinal);
            public Dictionary<long, int> LocalCreditCounts { get; }
            public Dictionary<long, List<LocalCredit>> LocalCreditsByPerson { get; }
            public Dictionary<long, MediaSeed> MediaById { get; }
            public Dictionary<string, ProviderPerson> ProviderPeople { get; }
            public Dictionary<string, List<ObservedProviderCredit>> ProviderCreditsByMedia { get; }
            public PlannerIndex(ResolutionInput input)
            {
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
        }
    }
}
