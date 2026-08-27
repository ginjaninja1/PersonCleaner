using PersonCleaner.V2.Domain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PersonCleaner.V2.Storage
{
    public static class DashboardCaseBuilder
    {
        private const string RecommendedCorrectionSignal = "RECOMMENDED_PROVIDER_CORRECTION";

        public static DashboardDecision[] Build(IEnumerable<DashboardDecision> source)
        {
            var rows = (source ?? Enumerable.Empty<DashboardDecision>()).ToList();
            if (rows.Count == 0) return new DashboardDecision[0];
            var groups = ConnectedGroups(rows);
            return groups.OrderBy(x => x.Min(index => index)).Select(group => BuildCase(group.Select(index => rows[index]).ToList())).ToArray();
        }

        private static List<List<int>> ConnectedGroups(IReadOnlyList<DashboardDecision> rows)
        {
            var result = new List<List<int>>();
            var conflation = Enumerable.Range(0, rows.Count).Where(x => rows[x].Status == "CONFLATION").ToList();
            var byProviderKey = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var index in conflation)
            foreach (var key in ProviderKeys(rows[index].ProviderIdentities))
            {
                List<int> matches;
                if (!byProviderKey.TryGetValue(key, out matches)) byProviderKey[key] = matches = new List<int>();
                matches.Add(index);
            }

            var visited = new HashSet<int>();
            foreach (var start in conflation)
            {
                if (!visited.Add(start)) continue;
                var group = new List<int>(); var pending = new Queue<int>(); pending.Enqueue(start);
                while (pending.Count > 0)
                {
                    var index = pending.Dequeue(); group.Add(index);
                    foreach (var key in ProviderKeys(rows[index].ProviderIdentities))
                    {
                        List<int> matches;
                        if (!byProviderKey.TryGetValue(key, out matches)) continue;
                        foreach (var match in matches) if (visited.Add(match)) pending.Enqueue(match);
                    }
                }
                result.Add(group);
            }
            foreach (var index in Enumerable.Range(0, rows.Count).Where(x => rows[x].Status != "CONFLATION")) result.Add(new List<int> { index });
            return result;
        }

        private static DashboardDecision BuildCase(IReadOnlyList<DashboardDecision> members)
        {
            var ordered = members.OrderBy(x => x.DecisionId, StringComparer.Ordinal).ToList();
            var providerKeys = ordered.SelectMany(x => ProviderKeys(x.ProviderIdentities)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var decisionIds = ordered.Select(x => x.DecisionId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
            var corrections = ordered.SelectMany(RecommendedCorrections).GroupBy(x => x.Metric, StringComparer.Ordinal).Select(x => new CorrectionEvidence { Metric = x.Key, Narrative = x.Select(y => y.Narrative).FirstOrDefault(y => !string.IsNullOrWhiteSpace(y)), DecisionCount = x.Select(y => y.DecisionId).Distinct(StringComparer.Ordinal).Count() }).ToList();
            var blocked = ordered.Any(x => x.Action == ResolutionActions.IncompleteScope) || ordered.SelectMany(x => x.Details ?? new DashboardDetail[0]).Any(x => x.Signal == "GLOBAL_BINDING_OWNER" || x.Signal == "ACQUISITION_INCOMPLETE" || x.Signal == "MEDIA_ACQUISITION_INCOMPLETE");
            var automatic = ordered.All(x => x.Status == "MATCH" || x.Status == "MATCH_WITH_CONFLICT" || (x.Action ?? string.Empty).StartsWith("AUTO_", StringComparison.Ordinal));
            var allRelationshipsRecommendOneCorrection = corrections.Count == 1 && corrections[0].DecisionCount == ordered.Count;

            string automation; string automationReason; string action;
            if (blocked)
            {
                automation = "Blocked"; action = ResolutionActions.IncompleteScope;
                automationReason = "Automatic resolution is withheld because the evaluated scope does not contain every relevant Emby owner or required provider observation.";
            }
            else if (allRelationshipsRecommendOneCorrection)
            {
                automation = "Would auto-resolve"; action = "SUGGESTED_PROVIDER_CORRECTION";
                automationReason = "Every underlying relationship converges on one exact provider correction. No scope blocker was recorded; the correction remains subject to shadow recalculation and live preflight before unattended application.";
            }
            else if (automatic)
            {
                automation = "Automatically resolved"; action = ordered.Select(x => x.Action).Distinct(StringComparer.Ordinal).Count() == 1 ? ordered[0].Action : "AUTOMATIC";
                automationReason = "The identity graph already accepted this outcome automatically. Any Emby mutation remains separately validated before it is written.";
            }
            else
            {
                automation = "Review required"; action = ordered.Select(x => x.Action).Distinct(StringComparer.Ordinal).Count() == 1 ? ordered[0].Action : "HUMAN_REVIEW";
                automationReason = corrections.Count > 1
                    ? "The relationships produce more than one proposed correction, so PersonCleaner will not choose between them automatically."
                    : corrections.Count == 1
                        ? "One correction is suggested, but not every relationship in the case is explained by it. The remaining relationship evidence still requires review."
                        : "The provider problem is visible, but the evidence does not identify one exact, safe correction that resolves every relationship in the case.";
            }

            var caseId = ordered.Count == 1 ? ordered[0].DecisionId : "case:" + ordered[0].DecisionId;
            var names = ordered.SelectMany(x => (x.Person ?? string.Empty).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var anchors = ordered.Select(x => x.EmbyAnchor).Where(x => !string.IsNullOrWhiteSpace(x) && x != "—").Distinct(StringComparer.Ordinal).ToList();
            var currentIds = ordered.Select(x => x.CurrentProviderIds).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var media = ordered.SelectMany(x => x.Details ?? new DashboardDetail[0]).Where(x => x.EmbyMediaId.HasValue)
                .GroupBy(x => x.EmbyMediaId.Value.ToString(CultureInfo.InvariantCulture) + "|" + (x.Verdict ?? string.Empty), StringComparer.Ordinal).Select(x => x.First()).ToList();
            var details = CaseDetails(caseId, ordered, providerKeys, corrections, automation, automationReason, media);
            var summary = CaseSummary(ordered, providerKeys, corrections, blocked);

            return new DashboardDecision
            {
                CaseId = caseId,
                DecisionId = decisionIds.FirstOrDefault(),
                UnderlyingDecisionIds = decisionIds,
                UnderlyingDecisionLabels = ordered.Select(RelationshipLabel).ToArray(),
                Status = FriendlyCaseType(ordered.Select(x => x.Status).Distinct(StringComparer.Ordinal).Count() == 1 ? ordered[0].Status : "MIXED"),
                Action = FriendlySafetyMode(action),
                Automation = automation,
                AutomationReason = automationReason,
                Person = names.Count == 0 ? ordered[0].Person : string.Join(" / ", names),
                EmbyAnchor = anchors.Count == 0 ? "—" : anchors.Count == 1 ? anchors[0] : string.Join(", ", anchors),
                ProviderIdentities = string.Join(", ", providerKeys),
                CurrentProviderIds = string.Join("; ", currentIds),
                Relationships = ordered.Count,
                ProviderRecords = providerKeys.Count,
                Confidence = Range(ordered.Select(x => x.Confidence)),
                LocalAnchorConfidence = Range(ordered.Select(x => x.LocalAnchorConfidence)),
                ImpactedTitles = Math.Max(media.Count, ordered.Max(x => x.ImpactedTitles)),
                Decision = summary,
                Why = automationReason,
                Details = details.ToArray()
            };
        }

        private static string CaseSummary(IReadOnlyList<DashboardDecision> members, IReadOnlyList<string> providerKeys, IReadOnlyList<CorrectionEvidence> corrections, bool blocked)
        {
            if (corrections.Count == 1 && !string.IsNullOrWhiteSpace(corrections[0].Narrative)) return corrections[0].Narrative;
            if (members.Count == 1) return members[0].Decision;
            var evidence = members.SelectMany(x => x.Details ?? new DashboardDetail[0]).ToList();
            var owner = evidence.FirstOrDefault(x => x.Signal == "GLOBAL_BINDING_OWNER");
            if (blocked && owner != null)
                return "These provider records are related, but the case cannot be resolved safely because " + LowerFirst(owner.Explanation);
            var competing = evidence.Any(x => x.Signal == "COMPETING_ATTRIBUTION");
            var stableIdConflict = evidence.Any(x => x.Signal == "EXTERNAL_ID" && string.Equals(x.Verdict, "conflicts", StringComparison.OrdinalIgnoreCase));
            if (competing && stableIdConflict)
                return "The providers assign overlapping titles to different same-name people, and at least one pair also has conflicting stable person IDs. No single correction explains the whole case.";
            if (competing)
                return "The providers assign title credits to competing same-name person records. The evidence connects " + providerKeys.Count + " provider records, but does not identify one correction that resolves every disputed credit.";
            return members.Count + " disputed provider relationships connect " + providerKeys.Count + " provider person records; review the relationship evidence to see exactly where they agree and disagree.";
        }

        private static string FriendlyCaseType(string status)
        {
            switch (status)
            {
                case "CONFLATION": return "Provider attribution disagreement";
                case "DRIFT": return "Emby provider-ID drift";
                case "MATCH": return "Provider records agree";
                case "MATCH_WITH_CONFLICT": return "Identity aligned; provider metadata warning";
                case "ORPHAN": return "Provider identity missing";
                case "REALIGNMENT": return "Credits assigned to the wrong Emby person";
                case "SPLIT": return "Possible combined identities";
                case "MIXED": return "Mixed identity issues";
                default: return status;
            }
        }

        private static string FriendlySafetyMode(string action)
        {
            switch (action)
            {
                case "SUGGESTED_PROVIDER_CORRECTION": return "Suggested provider correction";
                case "INCOMPLETE_SCOPE": return "Blocked — incomplete scope";
                case "HUMAN_REVIEW": return "Human review";
                case "FORCE_SPLIT_REVIEW": return "Human review — possible split";
                case "REVIEW_REMOVE_STALE_PROVIDER_ID": return "Human review — stale provider ID";
                case "RETAINED_BY_MASS_ID_DRIFT": return "Human review — possible provider-ID drift";
                case "CROSS_PROVIDER_IDENTITY": return "Automatic — identity accepted";
                case "CROSS_PROVIDER_IDENTITY_WITH_METADATA_CONFLICT": return "Automatic — identity accepted; metadata retained";
                case "AUTO_REALIGN_CREDITS": return "Automatic — credit realignment";
                case "AUTOMATIC": return "Automatic";
                default: return action;
            }
        }

        private static string LowerFirst(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "another Emby person owns a relevant provider ID.";
            return char.ToLowerInvariant(value[0]) + value.Substring(1);
        }

        private static List<DashboardDetail> CaseDetails(string caseId, IReadOnlyList<DashboardDecision> members, IReadOnlyList<string> providerKeys, IReadOnlyList<CorrectionEvidence> corrections, string automation, string automationReason, IReadOnlyList<DashboardDetail> media)
        {
            var result = new List<DashboardDetail>
            {
                new DashboardDetail { DetailId = caseId + ":assessment", Section = "Case assessment", Order = 0, Signal = "AUTOMATION", Verdict = automation, Explanation = automationReason, RawMetric = "relationships=" + members.Count + ";provider_records=" + providerKeys.Count },
                new DashboardDetail { DetailId = caseId + ":scope", Section = "Case assessment", Order = 1, Signal = "CASE_SCOPE", Verdict = "info", Explanation = members.Count + " underlying relationship(s) connect " + providerKeys.Count + " provider person record(s): " + string.Join(", ", providerKeys) + ".", RawMetric = string.Join(",", members.Select(x => x.DecisionId)) }
            };
            if (corrections.Count == 1)
                result.Add(new DashboardDetail { DetailId = caseId + ":correction", Section = "Case assessment", Order = 2, Signal = "SUGGESTED_CORRECTION", Verdict = corrections[0].DecisionCount == members.Count ? "converged" : "partial", Explanation = corrections[0].Narrative, RawMetric = corrections[0].Metric });
            else if (corrections.Count > 1)
                result.Add(new DashboardDetail { DetailId = caseId + ":corrections", Section = "Case assessment", Order = 2, Signal = "SUGGESTED_CORRECTIONS", Verdict = "conflicts", Explanation = corrections.Count + " different provider corrections are proposed; none will be selected automatically.", RawMetric = string.Join(" | ", corrections.Select(x => x.Metric)) });

            for (var index = 0; index < members.Count; index++)
            {
                var member = members[index]; var section = "Relationship " + (index + 1) + " — " + member.ProviderIdentities;
                result.Add(new DashboardDetail { DetailId = caseId + ":relationship:" + index, Section = section, Order = index * 1000, Signal = member.Action, Verdict = member.Status, Explanation = member.Decision + " " + member.Why, RawMetric = member.DecisionId });
                foreach (var detail in (member.Details ?? new DashboardDetail[0]).Where(x => !x.EmbyMediaId.HasValue))
                    result.Add(Copy(detail, caseId + ":relationship:" + index + ":" + detail.DetailId, section, index * 1000 + Math.Max(1, detail.Order)));
            }
            foreach (var detail in media.OrderBy(x => x.Explanation, StringComparer.OrdinalIgnoreCase))
                result.Add(Copy(detail, caseId + ":media:" + detail.DetailId, "Affected titles", 100000 + result.Count));
            return result;
        }

        private static DashboardDetail Copy(DashboardDetail source, string id, string section, int order) => new DashboardDetail
        {
            DetailId = id, Section = section, Order = order, Signal = source.Signal, Verdict = source.Verdict, Explanation = source.Explanation, RawMetric = source.RawMetric,
            EmbyMediaId = source.EmbyMediaId, MediaType = source.MediaType, TmdbId = source.TmdbId, TvdbId = source.TvdbId, TvdbSlug = source.TvdbSlug, ImdbId = source.ImdbId, ProviderObjects = source.ProviderObjects
        };

        private static IEnumerable<CorrectionEvidence> RecommendedCorrections(DashboardDecision decision) =>
            (decision.Details ?? new DashboardDetail[0]).Where(x => x.Signal == RecommendedCorrectionSignal && !string.IsNullOrWhiteSpace(x.RawMetric))
                .Select(x => new CorrectionEvidence { DecisionId = decision.DecisionId, Metric = x.RawMetric, Narrative = x.Explanation });

        private static string RelationshipLabel(DashboardDecision row)
        {
            var summary = row.ProviderIdentities + " — " + row.Status;
            return summary.Length <= 110 ? summary : summary.Substring(0, 107) + "...";
        }

        private static IEnumerable<string> ProviderKeys(string value) => (value ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Contains(":"));

        private static string Range(IEnumerable<string> source)
        {
            var values = source.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
            if (values.Count <= 1) return values.FirstOrDefault() ?? "—";
            var percentages = values.Select(ParsePercent).Where(x => x.HasValue).Select(x => x.Value).OrderBy(x => x).ToList();
            return percentages.Count == values.Count ? percentages.First().ToString("0", CultureInfo.InvariantCulture) + "–" + percentages.Last().ToString("0", CultureInfo.InvariantCulture) + "%" : string.Join("–", values);
        }

        private static double? ParsePercent(string value)
        {
            double result;
            return double.TryParse((value ?? string.Empty).Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : (double?)null;
        }

        private sealed class CorrectionEvidence
        {
            public string DecisionId { get; set; }
            public string Metric { get; set; }
            public string Narrative { get; set; }
            public int DecisionCount { get; set; }
        }
    }
}
