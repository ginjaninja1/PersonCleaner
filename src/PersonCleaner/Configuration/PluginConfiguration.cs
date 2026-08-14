using System.Collections.Generic;
using MediaBrowser.Model.Plugins;
using PersonCleaner.UI.Config;

namespace PersonCleaner.Configuration
{
    /// <summary>
    /// The plugin's persisted settings - and the ONLY class involved in
    /// persistence. This uses Emby's standard BasePlugin&lt;T&gt; mechanism:
    /// Plugin.Instance.Configuration / SaveConfiguration() / UpdateConfiguration(),
    /// which serializes to XML in the plugin configurations folder
    /// automatically. No custom store, no hand-rolled JSON round-trip.
    ///
    /// This class has no UI/visual members, by construction - it isn't
    /// rendered by GenericEdit and is never assigned as ContentData, so
    /// there's nothing for it to accidentally leak. The config page instead
    /// builds a separate view-model, ConfigUI, fresh from this class every
    /// time it's shown - see PersonCleaner.UI.Config.ConfigViewBuilder.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        public bool EnablePlugin { get; set; } = true;

        public string TvdbApiKey { get; set; } = string.Empty;

        public string TvdbSubscriberPin { get; set; } = string.Empty;

        public int SuccessCacheDays { get; set; } = 30;

        public int FailureRetryMinutes { get; set; } = 30;

        public int MinimumRequestIntervalMilliseconds { get; set; } = 250;

        public int PreviewItemLimit { get; set; } = 5;

        public bool RequireSuccessfulPreview { get; set; } = true;

        public int ResolutionEvaluationItemsPerType { get; set; } = 25;

        public double AutoResolutionMinimumConfidence { get; set; } = 0.95;

        /// <summary>
        /// The real, persisted library/path filter data.
        /// </summary>
        public List<LibraryPathFilterItem> LibraryPaths { get; set; } =
            new List<LibraryPathFilterItem>();
    }
}
