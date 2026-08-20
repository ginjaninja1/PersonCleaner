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

        public string TmdbApiKey { get; set; } = string.Empty;

        public int SuccessCacheDays { get; set; } = 30;

        public int FailureRetryMinutes { get; set; } = 30;

        public int MinimumRequestIntervalMilliseconds { get; set; } = 100;

        public int TvdbMaximumConcurrentRequests { get; set; } = 2;

        public int TmdbMaximumConcurrentRequests { get; set; } = 4;

        public int TmdbMinimumRequestIntervalMilliseconds { get; set; } = 30;

        public string GivenNameEquivalences { get; set; } =
            "Don=Donald;Bill=William;Will=William;Bob=Robert;Rob=Robert;Dick=Richard;Rick=Richard;Jim=James;Jack=John;Ed=Edward;Eddie=Edward;Charlie=Charles;Tom=Thomas;Mike=Michael;Joe=Joseph;Dave=David;Dan=Daniel;Ben=Benjamin;Sam=Samuel;Alex=Alexander;Chris=Christopher;Matt=Matthew;Nick=Nicholas;Tony=Anthony;Andy=Andrew;Steve=Steven;Steve=Stephen;Liz=Elizabeth;Beth=Elizabeth;Kate=Katherine;Kate=Catherine;Katie=Katherine;Katie=Catherine;Becky=Rebecca;Jenny=Jennifer;Sue=Susan;Debbie=Deborah;Maggie=Margaret";

        /// <summary>
        /// The real, persisted library/path filter data.
        /// </summary>
        public List<LibraryPathFilterItem> LibraryPaths { get; set; } =
            new List<LibraryPathFilterItem>();
    }
}
