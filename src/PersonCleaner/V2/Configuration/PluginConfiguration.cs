using MediaBrowser.Model.Plugins;

namespace PersonCleaner.Configuration
{
    public sealed class PluginConfiguration : BasePluginConfiguration
    {
        public bool EnablePlugin { get; set; } = true;
        public string ExecutionMode { get; set; } = "Sandbox";
        public int SandboxSampleSizePerMediaType { get; set; } = 50;
        public int SandboxSeed { get; set; } = 20260823;
        public string SandboxIncludedMediaIds { get; set; } = string.Empty;
        public string SandboxIncludedPersonIds { get; set; } = string.Empty;
        public bool SandboxAutoExpandPersonMedia { get; set; } = true;

        public string TvdbApiKey { get; set; } = string.Empty;
        public string TvdbSubscriberPin { get; set; } = string.Empty;
        public string TmdbApiKey { get; set; } = string.Empty;

        public int CacheTtlDays { get; set; } = 7;
        public int FailureRetryMinutes { get; set; } = 30;
        public int TvdbMaximumConcurrentRequests { get; set; } = 2;
        public int TmdbMaximumConcurrentRequests { get; set; } = 4;
        public int TvdbMinimumRequestIntervalMilliseconds { get; set; } = 150;
        public int TmdbMinimumRequestIntervalMilliseconds { get; set; } = 40;

        // Legacy schema-2 settings remain solely so existing XML configuration
        // deserializes without data loss. Evidence model v2 ignores these weights.
        public double FilmographyWeight { get; set; } = 0.45;
        public double BirthdayWeight { get; set; } = 0.25;
        public double ExactNameWeight { get; set; } = 0.20;
        public double AliasWeight { get; set; } = 0.10;
        public double BirthdayMismatchPenalty { get; set; } = 0.50;
        public double AutomaticMatchThreshold { get; set; } = 0.75;
        public double HumanReviewThreshold { get; set; } = 0.40;

        // Retained only so existing configuration XML remains compatible.
        // The evidence dialog now loads every decision from the latest run.
        public int MaximumDashboardRows { get; set; } = 100;
        public int MaximumMediaExamplesPerDecision { get; set; } = 25;
    }
}
