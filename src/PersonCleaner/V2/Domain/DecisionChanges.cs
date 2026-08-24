using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PersonCleaner.V2.Domain
{
    public static class EmbyChangeKinds
    {
        public const string SetPersonProviderId = "set-person-provider-id";
        public const string RemovePersonProviderId = "remove-person-provider-id";
        public const string MoveCredit = "move-credit";
    }

    public static class ResolutionActions
    {
        public const string IncompleteScope = "INCOMPLETE_SCOPE";
        public const string AutoRealignCredits = "AUTO_REALIGN_CREDITS";
    }

    public sealed class EmbyChangeProposal
    {
        public string ChangeId { get; set; }
        public string Kind { get; set; }
        public long SourcePersonId { get; set; }
        public long? TargetPersonId { get; set; }
        public long? MediaId { get; set; }
        public string Provider { get; set; }
        public string CurrentValue { get; set; }
        public string ProposedValue { get; set; }
        public string Role { get; set; }
        public string Summary { get; set; }
        public string EvidenceNote { get; set; }
        public bool ManualReviewOnly { get; set; }
    }

    public sealed class DecisionChangeContext
    {
        public ResolutionDecision Decision { get; set; }
        public List<LocalPerson> LocalPeople { get; set; } = new List<LocalPerson>();
        public List<LocalPerson> GlobalLocalPeople { get; set; } = new List<LocalPerson>();
        public List<LocalCredit> LocalCredits { get; set; } = new List<LocalCredit>();
        public List<ResolutionCreditAssignment> CreditAssignments { get; set; } = new List<ResolutionCreditAssignment>();
        public List<PersonAcquisition> Acquisitions { get; set; } = new List<PersonAcquisition>();
        public List<ProviderPerson> ProposedProviderPeople { get; set; } = new List<ProviderPerson>();
    }

    public sealed class DecisionChangePlan
    {
        public string DecisionId { get; set; }
        public string DisplayName { get; set; }
        public string DecisionSummary { get; set; }
        public string NoChangeSummary { get; set; }
        public string NoChangeExplanation { get; set; }
        public List<long> InScopePersonIds { get; set; } = new List<long>();
        public List<EmbyChangeProposal> Changes { get; set; } = new List<EmbyChangeProposal>();
        public ProviderCorrection RecommendedCorrection { get; set; }
    }

    public static class DecisionChangePlanner
    {
        public static DecisionChangePlan Build(DecisionChangeContext context)
        {
            if (context == null || context.Decision == null) throw new ArgumentException("The decision is unavailable.");
            var decision = context.Decision;
            var plan = new DecisionChangePlan { DecisionId = decision.DecisionId, DisplayName = decision.DisplayName, DecisionSummary = decision.Headline, InScopePersonIds = context.LocalPeople.Select(x => x.EmbyId).Distinct().ToList() };
            var keys = ProviderKeys(decision.ProviderKeys).ToList();
            var anchor = decision.AnchorEmbyPersonId.HasValue ? context.LocalPeople.FirstOrDefault(x => x.EmbyId == decision.AnchorEmbyPersonId.Value) : null;
            if (decision.Action == ResolutionActions.IncompleteScope || HasOutOfScopeOwner(context, anchor, keys)) return plan;

            if (anchor != null && decision.Action == "REVIEW_REMOVE_STALE_PROVIDER_ID")
            {
                AddAbsentRemoval(plan, context, anchor, ProviderNames.Tmdb, anchor.TmdbId);
                AddAbsentRemoval(plan, context, anchor, ProviderNames.Tvdb, anchor.TvdbId);
            }
            else if (anchor != null && decision.Status == "ORPHAN" && decision.Action == "HUMAN_REVIEW")
            {
                AddUnsubstantiatedRemoval(plan, anchor, ProviderNames.Tmdb, anchor.TmdbId, decision);
                AddUnsubstantiatedRemoval(plan, anchor, ProviderNames.Tvdb, anchor.TvdbId, decision);
            }
            else if (anchor != null && (AppliesProviderBindings(decision.Action) || decision.Status == "DRIFT" && decision.Action == "HUMAN_REVIEW"))
            {
                foreach (var key in ProposedBindings(context, keys))
                {
                    var current = Binding(anchor, key.Provider);
                    if (string.Equals(current, key.Id, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrWhiteSpace(current) || decision.Status == "DRIFT")
                    {
                        var currentAbsent = !string.IsNullOrWhiteSpace(current) && IsAbsent(context, key.Provider, current);
                        AddProviderChange(plan, anchor.EmbyId, key.Provider, current, key.Id,
                            decision.Action == "HUMAN_REVIEW" || decision.Status == "DRIFT" && !currentAbsent,
                            key.IsExternal
                                ? "The proposed " + key.SourceProvider.ToUpperInvariant() + " person record identifies this " + key.Provider.ToUpperInvariant() + " person ID. It is a dependent part of the same identity change."
                                : decision.Status == "DRIFT" && !currentAbsent
                                    ? "The current provider record still exists. This replacement is offered for explicit manual approval because sampled media supports the proposed identity; it is not an automatic recommendation."
                                    : "The proposed provider binding follows the resolved media-backed identity.");
                        if (!key.IsExternal && !string.IsNullOrWhiteSpace(current) && plan.RecommendedCorrection == null)
                            plan.RecommendedCorrection = LocalBinding(anchor.EmbyId, key.Provider, current, key.Id, decision);
                    }
                }
            }

            if ((decision.Action ?? string.Empty).StartsWith("AUTO_MERGE_SHADOW", StringComparison.Ordinal) || decision.Action == ResolutionActions.AutoRealignCredits)
            {
                if ((decision.Action ?? string.Empty).StartsWith("AUTO_MERGE_SHADOW", StringComparison.Ordinal) && anchor != null)
                    AddShadowProviderReleases(plan, context, anchor, keys);
                foreach (var assignment in context.CreditAssignments.Where(x => x.Disposition == "MOVE").OrderBy(x => x.MediaEmbyId).ThenBy(x => x.Role, StringComparer.Ordinal).ThenBy(x => x.SourcePersonEmbyId))
                {
                    plan.Changes.Add(new EmbyChangeProposal
                    {
                        ChangeId = "credit:" + assignment.SourcePersonEmbyId.ToString(CultureInfo.InvariantCulture) + ":" + assignment.MediaEmbyId.ToString(CultureInfo.InvariantCulture) + ":" + (assignment.Role ?? string.Empty),
                        Kind = EmbyChangeKinds.MoveCredit,
                        SourcePersonId = assignment.SourcePersonEmbyId,
                        TargetPersonId = assignment.TargetPersonEmbyId,
                        MediaId = assignment.MediaEmbyId,
                        Role = assignment.Role,
                        Summary = "Move " + (string.IsNullOrWhiteSpace(assignment.Role) ? "credit" : assignment.Role) + " on Emby media " + assignment.MediaEmbyId + " from person " + assignment.SourcePersonEmbyId + " to person " + assignment.TargetPersonEmbyId + ".",
                        EvidenceNote = assignment.Rationale + " Component: " + assignment.ComponentKey + "."
                    });
                }
            }

            if (plan.RecommendedCorrection == null && decision.Status == "MATCH_WITH_CONFLICT")
            {
                var pair = keys.SelectMany((left, index) => keys.Skip(index + 1).Select(right => new { left, right })).FirstOrDefault(x => x.left.Provider != x.right.Provider);
                if (pair != null)
                    plan.RecommendedCorrection = new ProviderCorrection { Kind = CorrectionKinds.IdentityRelation, Operation = CorrectionOperations.Same, Provider = pair.left.Provider, ProviderPersonId = pair.left.Id, SecondaryProvider = pair.right.Provider, SecondaryId = pair.right.Id, Reason = "PROVIDER_MISMATCH", Note = "Recommended from decision " + decision.DecisionId + ": " + decision.Headline, Enabled = true };
            }
            if (plan.RecommendedCorrection == null && decision.Status == "CONFLATION")
                plan.RecommendedCorrection = EvidenceCorrection(decision);

            return Complete(plan, decision, context, anchor, keys);
        }

        private static void AddShadowProviderReleases(DecisionChangePlan plan, DecisionChangeContext context, LocalPerson anchor, IEnumerable<ProviderKey> keys)
        {
            var finalBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb })
            {
                var current = Binding(anchor, provider);
                if (!string.IsNullOrWhiteSpace(current)) finalBindings[provider] = current;
            }
            foreach (var key in ProposedBindings(context, keys)) finalBindings[key.Provider] = key.Id;

            var sources = new HashSet<long>(context.CreditAssignments.Where(x => x.Disposition == "MOVE").Select(x => x.SourcePersonEmbyId));
            foreach (var source in context.LocalPeople.Where(x => sources.Contains(x.EmbyId) && x.EmbyId != anchor.EmbyId).OrderBy(x => x.EmbyId))
            {
                foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb })
                {
                    var current = Binding(source, provider);
                    string survivor;
                    if (string.IsNullOrWhiteSpace(current) || !finalBindings.TryGetValue(provider, out survivor) || !string.Equals(current, survivor, StringComparison.OrdinalIgnoreCase)) continue;
                    plan.Changes.Add(new EmbyChangeProposal
                    {
                        ChangeId = "provider-release:" + source.EmbyId.ToString(CultureInfo.InvariantCulture) + ":" + provider,
                        Kind = EmbyChangeKinds.RemovePersonProviderId,
                        SourcePersonId = source.EmbyId,
                        Provider = provider,
                        CurrentValue = current,
                        Summary = "Release duplicate " + provider.ToUpperInvariant() + " person ID " + current + " from shadow Emby person " + source.EmbyId + " before moving its credits.",
                        EvidenceNote = "Emby resolves UpdatePeople by provider identity rather than the supplied PersonInfo ID. The survivor retains this binding; releasing it from the shadow is required for an unambiguous credit move."
                    });
                }
            }
        }

        private static DecisionChangePlan Complete(DecisionChangePlan plan, ResolutionDecision decision, DecisionChangeContext context, LocalPerson anchor, IEnumerable<ProviderKey> keys)
        {
            if (plan.Changes.Count != 0) return plan;
            var proposed = anchor == null ? new List<ProviderKey>() : ProposedBindings(context, keys).ToList();
            var alreadyAligned = proposed.Count > 0 && proposed.All(x => string.Equals(Binding(anchor, x.Provider), x.Id, StringComparison.OrdinalIgnoreCase));
            if (alreadyAligned && decision.Status == "MATCH" && decision.Action == "CROSS_PROVIDER_IDENTITY")
            {
                plan.NoChangeSummary = "Emby is already aligned; no changes are needed";
                plan.NoChangeExplanation = "The resolved provider identities already match the current Emby provider IDs, and no credit relationships need to move. No update or operator action is required.";
            }
            else if (alreadyAligned && decision.Status == "MATCH_WITH_CONFLICT" && decision.Action == "CROSS_PROVIDER_IDENTITY_WITH_METADATA_CONFLICT")
            {
                plan.NoChangeSummary = "No Emby changes are needed";
                plan.NoChangeExplanation = "The current Emby provider IDs already match the resolved identity. The provider metadata disagreement remains visible as evidence and may have a separate correction recommendation.";
            }
            else if (decision.Status == "CONFLATION")
            {
                plan.NoChangeSummary = "No direct Emby edit is proposed";
                plan.NoChangeExplanation = plan.RecommendedCorrection == null
                    ? "The provider records remain separate. The evidence identifies the disagreement, but it does not support a single safe provider correction."
                    : "This is a provider-data attribution issue. Review the pre-filled provider correction; Emby itself does not need to be edited for this decision.";
            }
            return plan;
        }

        private static ProviderCorrection EvidenceCorrection(ResolutionDecision decision)
        {
            var evidence = (decision.Evidence ?? new List<EvidenceLine>()).FirstOrDefault(x => x.SignalType == "RECOMMENDED_PROVIDER_CORRECTION");
            if (evidence == null) return null;
            var fields = (evidence.Metric ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => new { Separator = x.IndexOf('='), Value = x })
                .Where(x => x.Separator > 0)
                .ToDictionary(x => x.Value.Substring(0, x.Separator), x => x.Value.Substring(x.Separator + 1), StringComparer.OrdinalIgnoreCase);
            string kind; string operation; string provider; string mediaType; string mediaId; string personId; string replacement;
            if (!fields.TryGetValue("kind", out kind) || kind != CorrectionKinds.MediaCredit ||
                !fields.TryGetValue("operation", out operation) || operation != CorrectionOperations.Replace ||
                !fields.TryGetValue("provider", out provider) || !fields.TryGetValue("media_type", out mediaType) ||
                !fields.TryGetValue("provider_media_id", out mediaId) || !fields.TryGetValue("provider_person_id", out personId) ||
                !fields.TryGetValue("replacement_value", out replacement)) return null;
            return new ProviderCorrection
            {
                Kind = kind,
                Operation = operation,
                Provider = provider,
                MediaType = mediaType,
                ProviderMediaId = mediaId,
                ProviderPersonId = personId,
                ReplacementValue = replacement,
                Reason = "PROVIDER_CREDIT_ATTRIBUTION",
                Note = "Recommended from decision " + decision.DecisionId + ": " + evidence.Narrative,
                Enabled = true
            };
        }

        private static void AddAbsentRemoval(DecisionChangePlan plan, DecisionChangeContext context, LocalPerson person, string provider, string current)
        {
            if (string.IsNullOrWhiteSpace(current) || !IsAbsent(context, provider, current)) return;
            plan.Changes.Add(new EmbyChangeProposal
            {
                ChangeId = "provider:" + person.EmbyId.ToString(CultureInfo.InvariantCulture) + ":" + provider,
                Kind = EmbyChangeKinds.RemovePersonProviderId,
                SourcePersonId = person.EmbyId,
                Provider = provider,
                CurrentValue = current,
                Summary = "Remove provider-confirmed absent " + provider.ToUpperInvariant() + " person ID " + current + " from Emby person " + person.EmbyId + ".",
                EvidenceNote = "The provider returned an authoritative absence for this exact current binding."
            });
            if (plan.RecommendedCorrection == null) plan.RecommendedCorrection = LocalBinding(person.EmbyId, provider, current, null, context.Decision);
        }

        private static void AddProviderChange(DecisionChangePlan plan, long embyId, string provider, string current, string proposed, bool manualReviewOnly, string evidenceNote)
        {
            plan.Changes.Add(new EmbyChangeProposal
            {
                ChangeId = "provider:" + embyId.ToString(CultureInfo.InvariantCulture) + ":" + provider,
                Kind = EmbyChangeKinds.SetPersonProviderId,
                SourcePersonId = embyId,
                Provider = provider,
                CurrentValue = current,
                ProposedValue = proposed,
                Summary = (string.IsNullOrWhiteSpace(current) ? "Set " : "Replace " + provider.ToUpperInvariant() + " person ID " + current + " with ") + (string.IsNullOrWhiteSpace(current) ? provider.ToUpperInvariant() + " person ID " : string.Empty) + proposed + " on Emby person " + embyId + ".",
                ManualReviewOnly = manualReviewOnly,
                EvidenceNote = evidenceNote
            });
        }

        private static void AddUnsubstantiatedRemoval(DecisionChangePlan plan, LocalPerson person, string provider, string current, ResolutionDecision decision)
        {
            if (string.IsNullOrWhiteSpace(current)) return;
            plan.Changes.Add(new EmbyChangeProposal
            {
                ChangeId = "provider:" + person.EmbyId.ToString(CultureInfo.InvariantCulture) + ":" + provider,
                Kind = EmbyChangeKinds.RemovePersonProviderId,
                SourcePersonId = person.EmbyId,
                Provider = provider,
                CurrentValue = current,
                Summary = "Remove unsubstantiated " + provider.ToUpperInvariant() + " person ID " + current + " from Emby person " + person.EmbyId + ".",
                ManualReviewOnly = true,
                EvidenceNote = "The provider record exists, but no media-derived provider identity supports this binding in the evaluated sample. Removal requires explicit operator judgment."
            });
            if (plan.RecommendedCorrection == null) plan.RecommendedCorrection = LocalBinding(person.EmbyId, provider, current, null, decision);
        }

        private static ProviderCorrection LocalBinding(long embyId, string provider, string current, string replacement, ResolutionDecision decision) => new ProviderCorrection
        {
            Kind = CorrectionKinds.LocalPersonBinding,
            Operation = string.IsNullOrWhiteSpace(replacement) ? CorrectionOperations.Unusable : CorrectionOperations.Replace,
            Provider = provider,
            EmbyId = embyId,
            CurrentValue = current,
            ReplacementValue = replacement,
            Reason = "PROVIDER_MISMATCH",
            Note = "Recommended from decision " + decision.DecisionId + ": " + decision.Headline,
            Enabled = true
        };

        private static bool IsAbsent(DecisionChangeContext context, string provider, string id) => context.Acquisitions.Any(x => x.Provider == provider && x.ProviderId == id && x.State == AcquisitionStates.Absent);
        private static bool HasOutOfScopeOwner(DecisionChangeContext context, LocalPerson anchor, IEnumerable<ProviderKey> primaryKeys)
        {
            if (anchor == null || context.GlobalLocalPeople == null || context.GlobalLocalPeople.Count == 0) return false;
            var inScope = new HashSet<long>(context.LocalPeople.Select(x => x.EmbyId));
            var proposed = ProposedBindings(context, primaryKeys).ToList();
            return context.GlobalLocalPeople.Where(x => x.EmbyId != anchor.EmbyId && !inScope.Contains(x.EmbyId))
                .Any(owner => proposed.Any(x => string.Equals(Binding(owner, x.Provider), x.Id, StringComparison.OrdinalIgnoreCase)));
        }
        private static bool AppliesProviderBindings(string action) => action == "CROSS_PROVIDER_IDENTITY" || action == "CROSS_PROVIDER_IDENTITY_WITH_METADATA_CONFLICT" || action == "AUTO_MERGE_SHADOW" || action == "AUTO_MERGE_SHADOW_WITH_METADATA_CONFLICT" || action == "RETAINED_BY_MASS_ID_DRIFT";
        private static string Binding(LocalPerson person, string provider) => provider == ProviderNames.Tmdb ? person.TmdbId : provider == ProviderNames.Tvdb ? person.TvdbId : provider == ProviderNames.Imdb ? person.ImdbId : null;
        private static IEnumerable<string> CurrentKeys(LocalPerson person)
        {
            if (!string.IsNullOrWhiteSpace(person.TmdbId)) yield return ProviderNames.Tmdb + ":" + person.TmdbId;
            if (!string.IsNullOrWhiteSpace(person.TvdbId)) yield return ProviderNames.Tvdb + ":" + person.TvdbId;
        }
        private static IEnumerable<ProviderKey> ProviderKeys(string value)
        {
            foreach (var token in (value ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var split = token.IndexOf(':');
                if (split <= 0 || split == token.Length - 1) continue;
                yield return new ProviderKey { Provider = token.Substring(0, split).Trim().ToLowerInvariant(), Id = token.Substring(split + 1).Trim() };
            }
        }
        private static IEnumerable<ProviderKey> ProposedBindings(DecisionChangeContext context, IEnumerable<ProviderKey> primaryKeys)
        {
            var primary = primaryKeys.Where(x => x.Provider == ProviderNames.Tmdb || x.Provider == ProviderNames.Tvdb).ToList();
            var candidates = new List<ProviderKey>(primary);
            var keySet = new HashSet<string>(primary.Select(x => x.Provider + ":" + x.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var person in context.ProposedProviderPeople.Where(x => keySet.Contains(x.Key)))
            foreach (var external in person.ExternalIds.Where(x => x.Key == ProviderNames.Tmdb || x.Key == ProviderNames.Tvdb || x.Key == ProviderNames.Imdb))
                candidates.Add(new ProviderKey { Provider = external.Key, Id = external.Value, IsExternal = true, SourceProvider = person.Provider });
            foreach (var group in candidates.Where(x => !string.IsNullOrWhiteSpace(x.Id)).GroupBy(x => x.Provider, StringComparer.OrdinalIgnoreCase))
            {
                var values = group.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (values.Count != 1) continue;
                var preferred = group.FirstOrDefault(x => !x.IsExternal) ?? group.First();
                yield return new ProviderKey { Provider = group.Key.ToLowerInvariant(), Id = values[0], IsExternal = preferred.IsExternal, SourceProvider = preferred.SourceProvider };
            }
        }
        private sealed class ProviderKey { public string Provider { get; set; } public string Id { get; set; } public bool IsExternal { get; set; } public string SourceProvider { get; set; } }
    }
}
