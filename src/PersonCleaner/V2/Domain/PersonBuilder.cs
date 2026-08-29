using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PersonCleaner.V2.Domain
{
    public sealed class PersonBuilderDraft
    {
        public long RunId { get; set; }
        public string CaseId { get; set; }
        public string ReviewedPlanHash { get; set; }
        public List<PersonBuilderIdentity> People { get; set; } = new List<PersonBuilderIdentity>();
        public List<PersonBuilderCredit> Credits { get; set; } = new List<PersonBuilderCredit>();

        public static PersonBuilderDraft FromPlan(IdentityCasePlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            return new PersonBuilderDraft
            {
                RunId = plan.RunId,
                CaseId = plan.CaseId,
                ReviewedPlanHash = plan.PlanHash,
                People = plan.Outcomes.Select(x => new PersonBuilderIdentity
                {
                    OutcomeId = x.OutcomeId,
                    Include = true,
                    DisplayName = x.DisplayName,
                    TargetKind = x.TargetKind,
                    TargetEmbyId = x.TargetEmbyId,
                    TmdbId = IdentityCasePlanner.PreferredProviderId(x, ProviderNames.Tmdb),
                    TvdbId = IdentityCasePlanner.PreferredProviderId(x, ProviderNames.Tvdb),
                    ImdbId = IdentityCasePlanner.PreferredProviderId(x, ProviderNames.Imdb),
                    PlannerNotes = string.Empty
                }).ToList(),
                Credits = plan.Credits.Select(x => new PersonBuilderCredit { AssignmentId = x.AssignmentId, TargetOutcomeId = x.TargetOutcomeId }).ToList()
            };
        }
    }

    public sealed class PersonBuilderIdentity
    {
        public string OutcomeId { get; set; }
        public bool Include { get; set; } = true;
        public string DisplayName { get; set; }
        public string TargetKind { get; set; }
        public long? TargetEmbyId { get; set; }
        public string TmdbId { get; set; }
        public string TvdbId { get; set; }
        public string ImdbId { get; set; }
        public string PlannerNotes { get; set; }
    }

    public sealed class PersonBuilderCredit
    {
        public string AssignmentId { get; set; }
        public string TargetOutcomeId { get; set; }
    }

    public sealed class PersonBuilderCompilation
    {
        public IdentityCasePlan Plan { get; set; }
        public string ReviewedPlanHash { get; set; }
        public List<PersonBuilderCorrectionSelection> CorrectionSelections { get; set; } = new List<PersonBuilderCorrectionSelection>();
        public List<ProviderCorrection> Corrections { get; set; } = new List<ProviderCorrection>();
        public int EmbyChanges { get; set; }
    }

    public sealed class PersonBuilderCorrectionSelection
    {
        public string QuestionId { get; set; }
        public string ChoiceId { get; set; }
        public ProviderCorrection Correction { get; set; }
    }

    public static class IdentityCasePersonBuilder
    {
        private static readonly string[] Providers = { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb };

        public static PersonBuilderCompilation Compile(IdentityCasePlan source, PersonBuilderDraft draft)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            if (source.RunId != draft.RunId || !string.Equals(source.CaseId, draft.CaseId, StringComparison.Ordinal))
                throw new InvalidOperationException("The person-builder draft belongs to a different identity case.");
            if (!string.Equals(source.PlanHash, draft.ReviewedPlanHash, StringComparison.Ordinal))
                throw new InvalidOperationException("The identity case changed after this builder draft was opened. Reload it before saving.");

            var sourceOutcomes = source.Outcomes.ToDictionary(x => x.OutcomeId, StringComparer.Ordinal);
            var people = Unique(draft.People, x => x.OutcomeId, "person");
            var credits = Unique(draft.Credits, x => x.AssignmentId, "media assignment");
            if (sourceOutcomes.Keys.Except(people.Keys, StringComparer.Ordinal).Any() || people.Keys.Except(sourceOutcomes.Keys, StringComparer.Ordinal).Any())
                throw new InvalidOperationException("The builder must contain every person proposed by the reviewed case exactly once.");
            if (source.Credits.Select(x => x.AssignmentId).Except(credits.Keys, StringComparer.Ordinal).Any() || credits.Keys.Except(source.Credits.Select(x => x.AssignmentId), StringComparer.Ordinal).Any())
                throw new InvalidOperationException("The builder must assign every affected media credit exactly once.");

            foreach (var person in people.Values)
            {
                person.DisplayName = Clean(person.DisplayName);
                person.TmdbId = Clean(person.TmdbId); person.TvdbId = Clean(person.TvdbId); person.ImdbId = Clean(person.ImdbId);
                var original = sourceOutcomes[person.OutcomeId];
                if (person.DisplayName.Length == 0) person.DisplayName = original.DisplayName;
                if (!person.Include) continue;
                if (person.TargetKind != IdentityTargetKinds.Existing && person.TargetKind != IdentityTargetKinds.New)
                    throw new InvalidOperationException("Choose whether '" + person.DisplayName + "' should maintain an existing Emby person or be created as a new person.");
                if (person.TargetKind == IdentityTargetKinds.Existing && (!person.TargetEmbyId.HasValue || !source.CurrentPeople.Any(x => x.EmbyId == person.TargetEmbyId.Value)))
                    throw new InvalidOperationException("The selected existing Emby person is not part of this reviewed case.");
                if (person.TargetKind == IdentityTargetKinds.New) person.TargetEmbyId = null;
            }
            foreach (var credit in credits.Values)
                if (string.IsNullOrWhiteSpace(credit.TargetOutcomeId) || !people.ContainsKey(credit.TargetOutcomeId))
                    throw new InvalidOperationException("Every media credit must be assigned to one of the people in this case.");

            var referenced = new HashSet<string>(credits.Values.Select(x => x.TargetOutcomeId), StringComparer.Ordinal);
            var excludedTarget = people.Values.FirstOrDefault(x => !x.Include && referenced.Contains(x.OutcomeId));
            if (excludedTarget != null) throw new InvalidOperationException("Move every media credit away from '" + excludedTarget.DisplayName + "' before removing that person row.");
            // The executable plan contains final Emby identities, not every row
            // displayed by the builder. An existing row with no assigned media is
            // retained in the draft for operator context, but Emby will remove that
            // person after its last credit moves away.
            var activeIds = new HashSet<string>(people.Values.Where(x => x.Include && referenced.Contains(x.OutcomeId)).Select(x => x.OutcomeId), StringComparer.Ordinal);
            var existingTargets = people.Values.Where(x => activeIds.Contains(x.OutcomeId) && x.TargetKind == IdentityTargetKinds.Existing).ToList();
            var duplicateTarget = existingTargets.GroupBy(x => x.TargetEmbyId.Value).FirstOrDefault(x => x.Count() > 1);
            if (duplicateTarget != null) throw new InvalidOperationException("Emby person " + duplicateTarget.Key + " can only be represented by one final person row.");
            foreach (var person in people.Values.Where(x => referenced.Contains(x.OutcomeId)))
                if (person.TmdbId.Length == 0 && person.TvdbId.Length == 0)
                    throw new InvalidOperationException("Person '" + person.DisplayName + "' has assigned media and needs a TMDB or TVDB person ID.");
            foreach (var credit in credits.Values)
                if (!activeIds.Contains(credit.TargetOutcomeId)) throw new InvalidOperationException("A media credit cannot target an unused suggested person.");

            var plan = CloneHeader(source);
            foreach (var original in source.Outcomes.OrderBy(x => x.SortOrder).ThenBy(x => x.OutcomeId, StringComparer.Ordinal))
            {
                if (!activeIds.Contains(original.OutcomeId)) continue;
                var desired = people[original.OutcomeId];
                plan.Outcomes.Add(new IdentityOutcome
                {
                    OutcomeId = original.OutcomeId, SortOrder = plan.Outcomes.Count, ClusterKey = original.ClusterKey,
                    TargetKind = desired.TargetKind, TargetEmbyId = desired.TargetEmbyId, DisplayName = desired.DisplayName,
                    Outcome = desired.TargetKind == IdentityTargetKinds.Existing ? "Maintain Emby person " + desired.TargetEmbyId : "Create provider-identified Emby person",
                    SourceEmbyIds = original.SourceEmbyIds.ToList(), ProviderIds = DesiredProviderIds(original, desired)
                });
            }
            var active = plan.Outcomes.ToDictionary(x => x.OutcomeId, StringComparer.Ordinal);
            foreach (var original in source.Credits)
            {
                var targetId = credits[original.AssignmentId].TargetOutcomeId;
                var target = active[targetId];
                var disposition = target.TargetKind == IdentityTargetKinds.Existing && target.TargetEmbyId == original.SourcePersonEmbyId ? "KEEP" : "MOVE";
                if (original.IsReviewSupplemental && disposition == "KEEP") continue;
                plan.Credits.Add(new IdentityCreditOutcome
                {
                    AssignmentId = original.AssignmentId, SourcePersonEmbyId = original.SourcePersonEmbyId, TargetOutcomeId = targetId,
                    MediaEmbyId = original.MediaEmbyId, MediaType = original.MediaType, MediaName = original.MediaName, Role = original.Role,
                    SeriesEmbyId = original.SeriesEmbyId, SeriesName = original.SeriesName,
                    TmdbId = original.TmdbId, TvdbId = original.TvdbId, TvdbSlug = original.TvdbSlug, ImdbId = original.ImdbId,
                    Disposition = disposition, CorrectionRequired = false,
                    Rationale = original.IsReviewSupplemental
                        ? "Operator reassigned a live Emby relationship that was outside the gathered evidence scope."
                        : disposition == "KEEP" ? "Operator confirmed this Emby attribution in the person builder." : "Operator assigned this media credit in the person builder.",
                    IsReviewSupplemental = original.IsReviewSupplemental,
                    Attributions = original.Attributions.Where(x => active.ContainsKey(x.OutcomeId)).Select(Clone).ToList()
                });
            }

            var compilation = new PersonBuilderCompilation { Plan = plan, ReviewedPlanHash = source.PlanHash };
            compilation.CorrectionSelections.AddRange(BuildCorrectionSelections(source, plan, people, credits));
            compilation.CorrectionSelections = compilation.CorrectionSelections
                .GroupBy(x => CorrectionKey(x.Correction), StringComparer.Ordinal).Select(x => x.First()).ToList();
            compilation.Corrections = compilation.CorrectionSelections.Select(x => x.Correction).ToList();
            compilation.EmbyChanges = CompleteProjection(plan);
            plan.PlanHash = Hash(Canonical(plan));
            return compilation;
        }

        private static IEnumerable<PersonBuilderCorrectionSelection> BuildCorrectionSelections(IdentityCasePlan source, IdentityCasePlan result, IDictionary<string, PersonBuilderIdentity> people, IDictionary<string, PersonBuilderCredit> credits)
        {
            foreach (var question in source.Questions.OrderBy(x => x.QuestionId, StringComparer.Ordinal))
            {
                IdentityQuestionChoice selected = null;
                IdentityOutcome selectedOutcome = null;
                if (!string.IsNullOrWhiteSpace(question.AssignmentId) && credits.TryGetValue(question.AssignmentId, out var credit))
                {
                    selectedOutcome = result.Outcomes.FirstOrDefault(x => x.OutcomeId == credit.TargetOutcomeId);
                    selected = question.Choices.Where(x => CreditChoiceTargets(x?.Correction, selectedOutcome, credit.TargetOutcomeId))
                        .OrderBy(x => x.Correction.Kind == CorrectionKinds.MediaCredit ? 0 : 1).ThenBy(x => x.ChoiceId, StringComparer.Ordinal).FirstOrDefault();
                }
                else if (!string.IsNullOrWhiteSpace(question.OutcomeId) && people.TryGetValue(question.OutcomeId, out var person))
                {
                    // Questions belonging only to a person with no final media do
                    // not describe a correction that will be written to Emby.
                    if (!result.Outcomes.Any(x => x.OutcomeId == question.OutcomeId)) continue;
                    var original = source.Outcomes.FirstOrDefault(x => x.OutcomeId == question.OutcomeId);
                    selected = question.Choices.Where(x => IdentityChoiceMatches(x?.Correction, original, person)).OrderBy(x => x.ChoiceId, StringComparer.Ordinal).FirstOrDefault();
                }

                if (selected?.Correction == null)
                {
                    if (QuestionResolvedBySelectedIdentity(source, question, selectedOutcome)) continue;
                    if (question.Choices.Count > 0)
                        throw new InvalidOperationException("The selected layout does not fully resolve: " + question.Narrative + " Review that media destination and the provider IDs on its person.");
                    continue;
                }
                yield return new PersonBuilderCorrectionSelection
                {
                    QuestionId = question.QuestionId,
                    ChoiceId = selected.ChoiceId,
                    Correction = Clone(selected.Correction)
                };
            }
        }

        private static bool QuestionResolvedBySelectedIdentity(IdentityCasePlan source, IdentityQuestion question, IdentityOutcome selectedOutcome)
        {
            if (source == null || question == null || selectedOutcome == null || string.IsNullOrWhiteSpace(question.AssignmentId)) return false;
            var credit = source.Credits.FirstOrDefault(x => x.AssignmentId == question.AssignmentId);
            var assertions = (credit?.Attributions ?? new List<IdentityCreditAttribution>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Provider) && !string.IsNullOrWhiteSpace(x.ProviderPersonId))
                .GroupBy(x => x.Provider + ":" + x.ProviderPersonId, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
            return assertions.Count > 0 && assertions.All(assertion => selectedOutcome.ProviderIds.Any(x => x.Source == "native" && Same(x.Provider, assertion.Provider) && Same(x.ProviderId, assertion.ProviderPersonId)));
        }

        private static bool CreditChoiceTargets(ProviderCorrection correction, IdentityOutcome outcome, string outcomeId)
        {
            if (correction == null || outcome == null) return false;
            if (correction.Kind == CorrectionKinds.LocalCreditTarget)
            {
                var target = outcome.TargetKind == IdentityTargetKinds.Existing && outcome.TargetEmbyId.HasValue
                    ? "existing:" + outcome.TargetEmbyId.Value.ToString(CultureInfo.InvariantCulture)
                    : "outcome:" + outcomeId;
                return Same(correction.ReplacementValue, target);
            }
            if (correction.Kind != CorrectionKinds.MediaCredit) return false;
            if (correction.Operation == CorrectionOperations.Replace)
                return outcome.ProviderIds.Any(x => x.Source == "native" && Same(x.Provider, correction.Provider) && Same(x.ProviderId, correction.ReplacementValue));
            if (correction.Operation == CorrectionOperations.Unusable)
                return !outcome.ProviderIds.Any(x => x.Source == "native" && Same(x.Provider, correction.Provider));
            return false;
        }

        private static bool IdentityChoiceMatches(ProviderCorrection correction, IdentityOutcome original, PersonBuilderIdentity desired)
        {
            if (correction == null || desired == null) return false;
            if (correction.Kind == CorrectionKinds.IdentityTarget)
            {
                var target = desired.TargetKind == IdentityTargetKinds.Existing && desired.TargetEmbyId.HasValue
                    ? "existing:" + desired.TargetEmbyId.Value.ToString(CultureInfo.InvariantCulture)
                    : desired.TargetKind == IdentityTargetKinds.New ? "new" : string.Empty;
                return Same(correction.ReplacementValue, target);
            }
            if (correction.Kind != CorrectionKinds.PersonExternalId) return false;
            var after = DesiredId(desired, correction.FieldName);
            if (correction.Operation == CorrectionOperations.Replace) return Same(after, correction.ReplacementValue);
            if (correction.Operation != CorrectionOperations.Unusable || !string.IsNullOrWhiteSpace(after)) return false;
            return original != null && original.ProviderIds.Any(x => Same(x.Provider, correction.FieldName) && Same(x.ProviderId, correction.CurrentValue));
        }

        private static string DesiredId(PersonBuilderIdentity person, string provider)
        {
            if (Same(provider, ProviderNames.Tmdb)) return person.TmdbId;
            if (Same(provider, ProviderNames.Tvdb)) return person.TvdbId;
            if (Same(provider, ProviderNames.Imdb)) return person.ImdbId;
            return null;
        }

        private static ProviderCorrection Clone(ProviderCorrection x) => new ProviderCorrection
        {
            Kind = x.Kind, Operation = x.Operation, Provider = x.Provider, MediaType = x.MediaType,
            ProviderMediaId = x.ProviderMediaId, ProviderPersonId = x.ProviderPersonId, FieldName = x.FieldName,
            CurrentValue = x.CurrentValue, ReplacementValue = x.ReplacementValue, SecondaryProvider = x.SecondaryProvider,
            SecondaryId = x.SecondaryId, EmbyId = x.EmbyId, Reason = x.Reason, Note = x.Note, Enabled = true
        };

        private static IdentityCasePlan CloneHeader(IdentityCasePlan source)
        {
            return new IdentityCasePlan
            {
                RunId = source.RunId, CaseId = source.CaseId, DisplayName = source.DisplayName,
                CaseType = source.CaseType, Warning = source.Warning, State = IdentityPlanStates.Complete,
                DecisionIds = source.DecisionIds.ToList(),
                CurrentPeople = source.CurrentPeople.Select(x => new LocalPerson { EmbyId = x.EmbyId, Name = x.Name, TmdbId = x.TmdbId, TvdbId = x.TvdbId, ImdbId = x.ImdbId }).ToList()
            };
        }

        private static int CompleteProjection(IdentityCasePlan plan)
        {
            var finalOutcomes = MediaBearingOutcomes(plan);
            var creates = finalOutcomes.Count(x => x.TargetKind == IdentityTargetKinds.New);
            var moves = plan.Credits.Count(x => x.Disposition == "MOVE");
            var changes = 0;
            foreach (var outcome in finalOutcomes.Where(x => x.TargetEmbyId.HasValue))
            {
                var current = plan.CurrentPeople.FirstOrDefault(x => x.EmbyId == outcome.TargetEmbyId.Value);
                if (current == null) continue;
                if (!Same(current.TmdbId, Preferred(outcome, ProviderNames.Tmdb))) changes++;
                if (!Same(current.TvdbId, Preferred(outcome, ProviderNames.Tvdb))) changes++;
                if (!Same(current.ImdbId, Preferred(outcome, ProviderNames.Imdb))) changes++;
            }
            var parts = new List<string>();
            if (creates > 0) parts.Add("create " + creates + " person" + (creates == 1 ? string.Empty : "s"));
            if (moves > 0) parts.Add("move " + moves + " credit" + (moves == 1 ? string.Empty : "s"));
            if (changes > 0) parts.Add("change " + changes + " ID" + (changes == 1 ? string.Empty : "s"));
            plan.ApplyCaption = parts.Count == 0 ? "No Emby changes required" : "Apply: " + string.Join(", ", parts);
            plan.Summary = "Person builder result: maintain " + finalOutcomes.Count(x => x.TargetKind == IdentityTargetKinds.Existing) + " existing person(s), create " + creates + " person(s), move " + moves + " media credit(s), and change " + changes + " provider ID(s).";
            return creates + moves + changes;
        }

        public static List<IdentityOutcome> MediaBearingOutcomes(IdentityCasePlan plan)
        {
            if (plan == null) return new List<IdentityOutcome>();
            var outcomeIds = new HashSet<string>((plan.Credits ?? new List<IdentityCreditOutcome>()).Select(x => x.TargetOutcomeId).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.Ordinal);
            return (plan.Outcomes ?? new List<IdentityOutcome>()).Where(x => outcomeIds.Contains(x.OutcomeId)).ToList();
        }

        private static List<IdentityProviderId> DesiredProviderIds(IdentityOutcome original, PersonBuilderIdentity desired)
        {
            var result = new List<IdentityProviderId>();
            Add(result, original, ProviderNames.Tmdb, desired.TmdbId, "native");
            Add(result, original, ProviderNames.Tvdb, desired.TvdbId, "native");
            Add(result, original, ProviderNames.Imdb, desired.ImdbId, "external");
            return result;
        }

        private static void Add(List<IdentityProviderId> target, IdentityOutcome original, string provider, string id, string fallbackSource)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var existing = original.ProviderIds.FirstOrDefault(x => x.Provider == provider && x.ProviderId == id);
            target.Add(new IdentityProviderId { Provider = provider, ProviderId = id, Source = existing?.Source ?? fallbackSource });
        }

        private static IdentityCreditAttribution Clone(IdentityCreditAttribution x) => new IdentityCreditAttribution
        {
            Provider = x.Provider, ProviderMediaId = x.ProviderMediaId, ProviderPersonId = x.ProviderPersonId, PersonName = x.PersonName,
            Role = x.Role, RoleCategory = x.RoleCategory, OutcomeId = x.OutcomeId
        };

        public static List<string> DuplicateProviderIdKeys(PersonBuilderDraft draft)
        {
            if (draft == null) return new List<string>();
            var referenced = new HashSet<string>((draft.Credits ?? new List<PersonBuilderCredit>()).Select(x => x.TargetOutcomeId).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.Ordinal);
            var active = (draft.People ?? new List<PersonBuilderIdentity>()).Where(x => x.Include && referenced.Contains(x.OutcomeId));
            return active.SelectMany(x => new[]
            {
                Pair(ProviderNames.Tmdb, x.TmdbId, x.OutcomeId), Pair(ProviderNames.Tvdb, x.TvdbId, x.OutcomeId), Pair(ProviderNames.Imdb, x.ImdbId, x.OutcomeId)
            }).Where(x => x != null)
                .GroupBy(x => x.Provider + ":" + x.Id, StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Select(y => y.OutcomeId).Distinct(StringComparer.Ordinal).Count() > 1)
                .Select(x => x.First().Provider + ":" + x.First().Id)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static ProviderAssignment Pair(string provider, string id, string outcomeId) => string.IsNullOrWhiteSpace(id) ? null : new ProviderAssignment { Provider = provider, Id = id.Trim(), OutcomeId = outcomeId };
        private sealed class ProviderAssignment { public string Provider { get; set; } public string Id { get; set; } public string OutcomeId { get; set; } }

        private static Dictionary<string, T> Unique<T>(IEnumerable<T> rows, Func<T, string> key, string label)
        {
            var result = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var row in rows ?? Enumerable.Empty<T>())
            {
                var value = key(row);
                if (string.IsNullOrWhiteSpace(value) || result.ContainsKey(value)) throw new InvalidOperationException("The builder contains a missing or duplicate " + label + ".");
                result[value] = row;
            }
            return result;
        }

        private static string Canonical(IdentityCasePlan plan) => string.Join("|", plan.Outcomes.Select(x => x.OutcomeId + ":" + x.TargetKind + ":" + x.TargetEmbyId + ":" + x.DisplayName + ":" + string.Join(",", x.ProviderIds.Select(y => y.Provider + ":" + y.ProviderId).OrderBy(y => y, StringComparer.Ordinal))).Concat(plan.Credits.Select(x => x.AssignmentId + ":" + x.TargetOutcomeId)).Concat(new[] { plan.Summary, plan.ApplyCaption }));
        private static string Hash(string value) { using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)).Take(8).Select(x => x.ToString("x2", CultureInfo.InvariantCulture))); }
        private static string CorrectionKey(ProviderCorrection x) => string.Join("|", new[] { x.Kind, x.Operation, x.Provider, x.MediaType, x.ProviderMediaId, x.ProviderPersonId, x.FieldName, x.CurrentValue, x.ReplacementValue, x.SecondaryProvider, x.SecondaryId, x.EmbyId?.ToString(CultureInfo.InvariantCulture) }.Select(y => y ?? string.Empty));
        private static string Preferred(IdentityOutcome outcome, string provider) => IdentityCasePlanner.PreferredProviderId(outcome, provider) ?? string.Empty;
        private static string LocalId(LocalPerson person, string provider) => provider == ProviderNames.Tmdb ? person.TmdbId : provider == ProviderNames.Tvdb ? person.TvdbId : person.ImdbId;
        private static bool Same(string a, string b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        private static string Clean(string value) => (value ?? string.Empty).Trim();
    }
}
