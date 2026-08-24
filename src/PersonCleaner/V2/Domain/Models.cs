using System;
using System.Collections.Generic;

namespace PersonCleaner.V2.Domain
{
    public static class ProviderNames
    {
        public const string Tmdb = "tmdb";
        public const string Tvdb = "tvdb";
        public const string Imdb = "imdb";
        public const string Wikidata = "wikidata";
    }

    public static class MediaTypes
    {
        public const string Movie = "movie";
        public const string Series = "series";
    }

    public static class AcquisitionStates
    {
        public const string Present = "PRESENT";
        public const string Absent = "ABSENT";
        public const string Unavailable = "UNAVAILABLE";
    }

    public sealed class MediaSeed
    {
        public long EmbyId { get; set; }
        public string MediaType { get; set; }
        public string Name { get; set; }
        public int? Year { get; set; }
        public string TmdbId { get; set; }
        public string TvdbId { get; set; }
        public string ImdbId { get; set; }
    }

    public sealed class MediaExternalIdentity
    {
        public string Provider { get; set; }
        public string Id { get; set; }
    }

    public sealed class ProviderMediaIdentity
    {
        public string Provider { get; set; }
        public string MediaType { get; set; }
        public string ProviderMediaId { get; set; }
        public List<MediaExternalIdentity> ExternalIds { get; set; } = new List<MediaExternalIdentity>();
    }

    public sealed class LocalPerson
    {
        public long EmbyId { get; set; }
        public string Name { get; set; }
        public string TmdbId { get; set; }
        public string TvdbId { get; set; }
        public string ImdbId { get; set; }
    }

    public sealed class LocalCredit
    {
        public long PersonEmbyId { get; set; }
        public long MediaEmbyId { get; set; }
        public string Role { get; set; }
    }

    public sealed class ProviderPerson
    {
        public string Provider { get; set; }
        public string ProviderId { get; set; }
        public string Name { get; set; }
        public string CleanName { get; set; }
        public string Birthday { get; set; }
        public List<string> Aliases { get; set; } = new List<string>();
        public Dictionary<string, string> ExternalIds { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> CanonicalMediaKeys { get; set; } =
            new HashSet<string>(StringComparer.Ordinal);
        public List<ObservedProviderCredit> Credits { get; set; } =
            new List<ObservedProviderCredit>();

        public string Key => Provider + ":" + ProviderId;
    }

    public sealed class ManualBridge
    {
        public string ProviderA { get; set; }
        public string ProviderIdA { get; set; }
        public string ProviderB { get; set; }
        public string ProviderIdB { get; set; }
        public bool IsRejected { get; set; }
    }

    public sealed class PersonAcquisition
    {
        public string Provider { get; set; }
        public string ProviderId { get; set; }
        public string State { get; set; }
        public bool GraphEligible { get; set; }
        public string Source { get; set; }
        public string Detail { get; set; }

        public string Key => Provider + ":" + ProviderId;
    }

    public sealed class MediaAcquisition
    {
        public string Provider { get; set; }
        public string MediaType { get; set; }
        public string ProviderId { get; set; }
        public string State { get; set; }

        public string Key => Provider + ":" + MediaType + ":" + ProviderId;
    }

    public sealed class ResolutionInput
    {
        public bool AcquisitionTrackingEnabled { get; set; }
        public List<ProviderPerson> ProviderPeople { get; set; } = new List<ProviderPerson>();
        public List<ObservedProviderCredit> ProviderCredits { get; set; } = new List<ObservedProviderCredit>();
        public List<LocalPerson> LocalPeople { get; set; } = new List<LocalPerson>();
        public List<LocalPerson> GlobalLocalPeople { get; set; } = new List<LocalPerson>();
        public List<LocalCredit> LocalCredits { get; set; } = new List<LocalCredit>();
        public List<MediaSeed> Media { get; set; } = new List<MediaSeed>();
        public List<ManualBridge> Bridges { get; set; } = new List<ManualBridge>();
        public List<PersonAcquisition> PersonAcquisitions { get; set; } = new List<PersonAcquisition>();
        public List<MediaAcquisition> MediaAcquisitions { get; set; } = new List<MediaAcquisition>();
        public List<CorrectionApplication> CorrectionApplications { get; set; } = new List<CorrectionApplication>();
    }

    public sealed class ResolutionSettings
    {
        public double AutomaticMatchThreshold { get; set; } = 0.75;
        public double HumanReviewThreshold { get; set; } = 0.40;
        public int MaximumMediaExamples { get; set; } = 25;
    }

    public sealed class ResolutionProgress
    {
        public string Stage { get; set; }
        public int Completed { get; set; }
        public int Total { get; set; }
        public double Fraction { get; set; }
        public int ExaminedPairs { get; set; }
        public int AdmittedCandidates { get; set; }
    }

    public sealed class ScoreBreakdown
    {
        public string ModelVersion { get; set; } = "person-evidence-v5";
        public double PositiveEvidenceScore { get; set; }
        public double MetadataConflictPenalty { get; set; }
        public double FilmographyJaccard { get; set; }
        public double FilmographyContainment { get; set; }
        public int LeftMediaCount { get; set; }
        public int RightMediaCount { get; set; }
        public int SharedMediaCount { get; set; }
        public int ExactRoleMatches { get; set; }
        public int CompatibleRoleMatches { get; set; }
        public double RoleAgreement { get; set; }
        public int CompetingAttributionCount { get; set; }
        public int NameFrequency { get; set; } = 1;
        public string BirthdayState { get; set; } = "missing";
        public string BirthdayDetail { get; set; }
        public string ExternalIdState { get; set; } = "missing";
        public string IdentifierMatchDetail { get; set; }
        public string IdentifierConflictDetail { get; set; }
        public bool BirthdayMatch { get; set; }
        public bool BirthdayConflict { get; set; }
        public bool ExactNameMatch { get; set; }
        public bool AliasMatch { get; set; }
        public bool HardIdentifierMatch { get; set; }
        public bool StableIdentifierMatch { get; set; }
        public bool NativeProviderCrosswalkMatch { get; set; }
        public bool IdentifierConflict { get; set; }
        public bool MediaAttributionDominant { get; set; }
        public bool HasMetadataConflict => BirthdayConflict || IdentifierConflict;
        public double Score { get; set; }
    }

    public sealed class ResolutionDiagnostics
    {
        public int BlockedCrossProviderPairs { get; set; }
        public int RejectedByOperator { get; set; }
        public int HardIdentityCandidates { get; set; }
        public int NameCompatibleCandidates { get; set; }
        public int AutomaticCandidates { get; set; }
        public int ReviewCandidates { get; set; }
        public int BelowReviewCandidates { get; set; }
        public int ConstraintBlockedCandidates { get; set; }
        public int GraphComponents { get; set; }
        public int AdmittedCandidates => HardIdentityCandidates + NameCompatibleCandidates;
    }

    public sealed class EvidenceLine
    {
        public int SortOrder { get; set; }
        public string SignalType { get; set; }
        public string Verdict { get; set; }
        public string Narrative { get; set; }
        public string Metric { get; set; }
    }

    public sealed class MediaExample
    {
        public long EmbyMediaId { get; set; }
        public string MediaType { get; set; }
        public string DisplayName { get; set; }
        public string Role { get; set; }
    }

    public sealed class ResolutionDecision
    {
        public string DecisionId { get; set; }
        public string Status { get; set; }
        public string Action { get; set; }
        public string DisplayName { get; set; }
        public long? AnchorEmbyPersonId { get; set; }
        public string ProviderKeys { get; set; }
        public double Confidence { get; set; }
        public double LocalAnchorConfidence { get; set; }
        public int ImpactedMediaCount { get; set; }
        public string Headline { get; set; }
        public string Explanation { get; set; }
        public List<EvidenceLine> Evidence { get; set; } = new List<EvidenceLine>();
        public List<MediaExample> ImpactedMedia { get; set; } = new List<MediaExample>();
        public List<MediaExample> MediaExamples { get; set; } = new List<MediaExample>();
        public List<ResolutionCreditAssignment> CreditAssignments { get; set; } = new List<ResolutionCreditAssignment>();
    }

    public sealed class ResolutionCreditAssignment
    {
        public long SourcePersonEmbyId { get; set; }
        public long TargetPersonEmbyId { get; set; }
        public long MediaEmbyId { get; set; }
        public string Role { get; set; }
        public string Disposition { get; set; }
        public string ComponentKey { get; set; }
        public string Rationale { get; set; }
    }

    public sealed class ProviderCredit
    {
        public string ProviderPersonId { get; set; }
        public string PersonName { get; set; }
        public string Role { get; set; }
        public string RoleCategory { get; set; }
        public string RoleName { get; set; }
    }

    public sealed class ObservedProviderCredit
    {
        public string Provider { get; set; }
        public string ProviderPersonId { get; set; }
        public string PersonName { get; set; }
        public string CleanPersonName { get; set; }
        public string MediaType { get; set; }
        public string ProviderMediaId { get; set; }
        public string CanonicalMediaKey { get; set; }
        public string Role { get; set; }
        public string RoleCategory { get; set; }
        public string RoleName { get; set; }
        public string PersonKey => Provider + ":" + ProviderPersonId;
    }

    public sealed class ResolutionPairEvaluation
    {
        public string PairId { get; set; }
        public string LeftProvider { get; set; }
        public string LeftProviderId { get; set; }
        public string RightProvider { get; set; }
        public string RightProviderId { get; set; }
        public string Disposition { get; set; }
        public ScoreBreakdown Score { get; set; }
    }

    public sealed class ResolutionClusterSnapshot
    {
        public string ClusterId { get; set; }
        public List<string> ProviderKeys { get; set; } = new List<string>();
        public long? AnchorEmbyPersonId { get; set; }
        public double IdentityConfidence { get; set; }
        public double LocalAnchorConfidence { get; set; }
    }

    public sealed class FlattenedMedia
    {
        public string Provider { get; set; }
        public string MediaType { get; set; }
        public string ProviderMediaId { get; set; }
        public string Name { get; set; }
        public string Slug { get; set; }
        public Dictionary<string, string> ExternalIds { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<ProviderCredit> Credits { get; set; } = new List<ProviderCredit>();
    }

    public sealed class FlattenedPerson
    {
        public string Provider { get; set; }
        public string ProviderPersonId { get; set; }
        public string Name { get; set; }
        public string Birthday { get; set; }
        public Dictionary<string, string> ExternalIds { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> Aliases { get; set; } = new List<string>();
    }
}
