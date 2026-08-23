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

    public sealed class ResolutionInput
    {
        public List<ProviderPerson> ProviderPeople { get; set; } = new List<ProviderPerson>();
        public List<LocalPerson> LocalPeople { get; set; } = new List<LocalPerson>();
        public List<LocalCredit> LocalCredits { get; set; } = new List<LocalCredit>();
        public List<MediaSeed> Media { get; set; } = new List<MediaSeed>();
        public List<ManualBridge> Bridges { get; set; } = new List<ManualBridge>();
    }

    public sealed class ResolutionSettings
    {
        public double FilmographyWeight { get; set; } = 0.45;
        public double BirthdayWeight { get; set; } = 0.25;
        public double ExactNameWeight { get; set; } = 0.20;
        public double AliasWeight { get; set; } = 0.10;
        public double BirthdayMismatchPenalty { get; set; } = 0.50;
        public double AutomaticMatchThreshold { get; set; } = 0.75;
        public double HumanReviewThreshold { get; set; } = 0.40;
        public int MaximumMediaExamples { get; set; } = 25;
    }

    public sealed class ScoreBreakdown
    {
        public double FilmographyJaccard { get; set; }
        public int SharedMediaCount { get; set; }
        public bool BirthdayMatch { get; set; }
        public bool BirthdayConflict { get; set; }
        public bool ExactNameMatch { get; set; }
        public bool AliasMatch { get; set; }
        public bool HardIdentifierMatch { get; set; }
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
        public int ImpactedMediaCount { get; set; }
        public string Headline { get; set; }
        public string Explanation { get; set; }
        public List<EvidenceLine> Evidence { get; set; } = new List<EvidenceLine>();
        public List<MediaExample> ImpactedMedia { get; set; } = new List<MediaExample>();
        public List<MediaExample> MediaExamples { get; set; } = new List<MediaExample>();
    }

    public sealed class ProviderCredit
    {
        public string ProviderPersonId { get; set; }
        public string PersonName { get; set; }
        public string Role { get; set; }
    }

    public sealed class FlattenedMedia
    {
        public string Provider { get; set; }
        public string MediaType { get; set; }
        public string ProviderMediaId { get; set; }
        public string Name { get; set; }
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
