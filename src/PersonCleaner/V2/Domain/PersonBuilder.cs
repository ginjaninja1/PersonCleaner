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
                    ImdbId = IdentityCasePlanner.PreferredProviderId(x, ProviderNames.Imdb)
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
    }

    public sealed class PersonBuilderCredit
    {
        public string AssignmentId { get; set; }
        public string TargetOutcomeId { get; set; }
    }

    public sealed class PersonBuilderCompilation
    {
        public IdentityCasePlan Plan { get; set; }
        public List<ProviderCorrection> Corrections { get; set; } = new List<ProviderCorrection>();
        public int RecordedDecisions { get; set; }
        public int EmbyChanges { get; set; }
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
            var activeIds = new HashSet<string>(people.Values.Where(x => x.Include && (x.TargetKind != IdentityTargetKinds.New || referenced.Contains(x.OutcomeId))).Select(x => x.OutcomeId), StringComparer.Ordinal);
            var existingTargets = people.Values.Where(x => activeIds.Contains(x.OutcomeId) && x.TargetKind == IdentityTargetKinds.Existing).ToList();
            var duplicateTarget = existingTargets.GroupBy(x => x.TargetEmbyId.Value).FirstOrDefault(x => x.Count() > 1);
            if (duplicateTarget != null) throw new InvalidOperationException("Emby person " + duplicateTarget.Key + " can only be represented by one final person row.");
            var missingExisting = source.CurrentPeople.FirstOrDefault(x => existingTargets.All(y => y.TargetEmbyId != x.EmbyId));
            if (missingExisting != null) throw new InvalidOperationException("Choose one final person row to maintain Emby person " + missingExisting.EmbyId + ".");
            foreach (var person in people.Values.Where(x => activeIds.Contains(x.OutcomeId) && x.TargetKind == IdentityTargetKinds.New))
                if (person.TmdbId.Length == 0 && person.TvdbId.Length == 0)
                    throw new InvalidOperationException("New person '" + person.DisplayName + "' needs a TMDB or TVDB person ID.");
            foreach (var credit in credits.Values)
                if (!activeIds.Contains(credit.TargetOutcomeId)) throw new InvalidOperationException("A media credit cannot target an unused suggested person.");

            ValidateUniqueProviderIds(people.Values.Where(x => activeIds.Contains(x.OutcomeId)));
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
                plan.Credits.Add(new IdentityCreditOutcome
                {
                    AssignmentId = original.AssignmentId, SourcePersonEmbyId = original.SourcePersonEmbyId, TargetOutcomeId = targetId,
                    MediaEmbyId = original.MediaEmbyId, MediaType = original.MediaType, MediaName = original.MediaName, Role = original.Role,
                    TmdbId = original.TmdbId, TvdbId = original.TvdbId, TvdbSlug = original.TvdbSlug, ImdbId = original.ImdbId,
                    Disposition = disposition, CorrectionRequired = false,
                    Rationale = disposition == "KEEP" ? "Operator confirmed this Emby attribution in the person builder." : "Operator assigned this media credit in the person builder.",
                    Attributions = original.Attributions.Where(x => active.ContainsKey(x.OutcomeId)).Select(Clone).ToList()
                });
            }

            var compilation = new PersonBuilderCompilation { Plan = plan };
            compilation.Corrections.AddRange(BuildIdentityCorrections(source, plan));
            compilation.Corrections.AddRange(BuildCreditCorrections(plan));
            compilation.Corrections = compilation.Corrections.GroupBy(CorrectionKey, StringComparer.Ordinal).Select(x => x.Last()).ToList();
            compilation.RecordedDecisions = plan.Credits.Count + plan.Outcomes.Sum(x => Providers.Count(p => !string.IsNullOrWhiteSpace(Preferred(x, p))));
            compilation.EmbyChanges = CompleteProjection(plan);
            plan.PlanHash = Hash(Canonical(plan));
            return compilation;
        }

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
            var creates = plan.Outcomes.Count(x => x.TargetKind == IdentityTargetKinds.New);
            var moves = plan.Credits.Count(x => x.Disposition == "MOVE");
            var changes = 0;
            foreach (var outcome in plan.Outcomes.Where(x => x.TargetEmbyId.HasValue))
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
            plan.Summary = "Person builder result: maintain " + plan.Outcomes.Count(x => x.TargetKind == IdentityTargetKinds.Existing) + " existing person(s), create " + creates + " person(s), move " + moves + " media credit(s), and change " + changes + " provider ID(s).";
            return creates + moves + changes;
        }

        private static IEnumerable<ProviderCorrection> BuildIdentityCorrections(IdentityCasePlan source, IdentityCasePlan plan)
        {
            var note = plan.DisplayName;
            foreach (var outcome in plan.Outcomes)
            {
                var native = outcome.ProviderIds.Where(x => x.Source == "native" && (x.Provider == ProviderNames.Tmdb || x.Provider == ProviderNames.Tvdb)).ToList();
                var target = outcome.TargetKind == IdentityTargetKinds.Existing ? "existing:" + outcome.TargetEmbyId.Value.ToString(CultureInfo.InvariantCulture) : "new";
                var original = source.Outcomes.FirstOrDefault(x => x.OutcomeId == outcome.OutcomeId);
                var targetChanged = original == null || original.TargetKind != outcome.TargetKind || original.TargetEmbyId != outcome.TargetEmbyId;
                // "new" is a one-time Apply instruction, not a durable identity fact. Persisting it would
                // propose another new Emby person after the first one had already been created. A changed
                // existing destination needs only one representative provider key because same-relations
                // below make the override apply to the complete provider component.
                if (targetChanged && outcome.TargetKind == IdentityTargetKinds.Existing && native.Count > 0)
                {
                    var id = native.OrderBy(x => x.Provider, StringComparer.Ordinal).ThenBy(x => x.ProviderId, StringComparer.Ordinal).First();
                    yield return new ProviderCorrection { Kind = CorrectionKinds.IdentityTarget, Operation = CorrectionOperations.Replace, Provider = id.Provider, ProviderPersonId = id.ProviderId, ReplacementValue = target, Reason = "OPERATOR_PERSON_BUILDER", Note = note, Enabled = true };
                }
                for (var i = 0; i < native.Count; i++)
                for (var j = i + 1; j < native.Count; j++)
                    if (native[i].Provider != native[j].Provider)
                        yield return Relation(native[i], native[j], CorrectionOperations.Same, note);

                if (!outcome.TargetEmbyId.HasValue) continue;
                var current = source.CurrentPeople.FirstOrDefault(x => x.EmbyId == outcome.TargetEmbyId.Value);
                if (current == null) continue;
                foreach (var provider in Providers)
                {
                    var before = LocalId(current, provider); var after = Preferred(outcome, provider);
                    if (Same(before, after)) continue;
                    yield return new ProviderCorrection
                    {
                        Kind = CorrectionKinds.LocalPersonBinding, Operation = string.IsNullOrWhiteSpace(after) ? CorrectionOperations.Unusable : CorrectionOperations.Replace,
                        Provider = provider, EmbyId = current.EmbyId, CurrentValue = before, ReplacementValue = after,
                        Reason = "OPERATOR_PERSON_BUILDER", Note = note, Enabled = true
                    };
                }
            }
            var rows = plan.Outcomes.Select(x => x.ProviderIds.Where(y => y.Source == "native" && (y.Provider == ProviderNames.Tmdb || y.Provider == ProviderNames.Tvdb)).ToList()).ToList();
            for (var left = 0; left < rows.Count; left++)
            for (var right = left + 1; right < rows.Count; right++)
            foreach (var a in rows[left])
            foreach (var b in rows[right])
                if (a.Provider != b.Provider) yield return Relation(a, b, CorrectionOperations.Different, note);
        }

        private static IEnumerable<ProviderCorrection> BuildCreditCorrections(IdentityCasePlan plan)
        {
            var outcomes = plan.Outcomes.ToDictionary(x => x.OutcomeId, StringComparer.Ordinal);
            // KEEP is a reviewed decision in the case plan, but it is not a correction. Only persist an
            // override when the operator actually chose a different local credit destination.
            foreach (var credit in plan.Credits.Where(x => x.Disposition == "MOVE"))
            {
                var target = outcomes[credit.TargetOutcomeId];
                var replacement = target.TargetKind == IdentityTargetKinds.Existing
                    ? "existing:" + target.TargetEmbyId.Value.ToString(CultureInfo.InvariantCulture)
                    : ProviderTarget(target);
                yield return new ProviderCorrection
                {
                    Kind = CorrectionKinds.LocalCreditTarget, Operation = CorrectionOperations.Replace, EmbyId = credit.MediaEmbyId,
                    CurrentValue = credit.SourcePersonEmbyId.ToString(CultureInfo.InvariantCulture) + "|" + credit.Role, ReplacementValue = replacement,
                    Reason = "OPERATOR_PERSON_BUILDER", Note = plan.DisplayName, Enabled = true
                };
            }
        }

        private static string ProviderTarget(IdentityOutcome outcome)
        {
            var id = outcome.ProviderIds.FirstOrDefault(x => x.Source == "native" && (x.Provider == ProviderNames.Tmdb || x.Provider == ProviderNames.Tvdb));
            if (id == null) throw new InvalidOperationException("A new person requires a native provider identity.");
            return "provider:" + id.Provider + ":" + id.ProviderId;
        }

        private static ProviderCorrection Relation(IdentityProviderId a, IdentityProviderId b, string operation, string note) => new ProviderCorrection
        {
            Kind = CorrectionKinds.IdentityRelation, Operation = operation, Provider = a.Provider, ProviderPersonId = a.ProviderId,
            SecondaryProvider = b.Provider, SecondaryId = b.ProviderId, Reason = "OPERATOR_PERSON_BUILDER", Note = note, Enabled = true
        };

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

        private static void ValidateUniqueProviderIds(IEnumerable<PersonBuilderIdentity> people)
        {
            var duplicates = people.SelectMany(x => new[]
            {
                Pair(ProviderNames.Tmdb, x.TmdbId, x.OutcomeId), Pair(ProviderNames.Tvdb, x.TvdbId, x.OutcomeId), Pair(ProviderNames.Imdb, x.ImdbId, x.OutcomeId)
            }).Where(x => x != null).GroupBy(x => x.Provider + ":" + x.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Select(y => y.OutcomeId).Distinct(StringComparer.Ordinal).Count() > 1);
            if (duplicates != null) throw new InvalidOperationException("Provider person ID " + duplicates.Key + " cannot belong to more than one final Emby person.");
        }

        private static ProviderAssignment Pair(string provider, string id, string outcomeId) => string.IsNullOrWhiteSpace(id) ? null : new ProviderAssignment { Provider = provider, Id = id, OutcomeId = outcomeId };
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
