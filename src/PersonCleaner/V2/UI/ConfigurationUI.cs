using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Controller;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using PersonCleaner.Configuration;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace PersonCleaner.V2.UI
{
    public sealed class ConfigurationUI : EditableOptionsBase
    {
        public override string EditorTitle => "PersonCleaner configuration";
        public override string EditorDescription => "Evidence calculation is read-only and runs as an Emby scheduled task. Raw provider payloads, flattened evidence and persisted case plans live in PersonCleaner's private workspace; the separately gated Mass Corrections task can apply satisfied changes.";

        public CaptionItem ScopeHeading { get; set; } = new CaptionItem("Scope and safety");
        [DisplayName("Enable PersonCleaner")]
        public bool EnablePlugin { get; set; }
        [DisplayName("Enabled Mass Corrections Task")]
        [Description("Off by default. When enabled, the Mass Corrections scheduled task applies only complete, persisted changes from the latest evidence run. Problem cases are never applied automatically.")]
        public bool EnableMassCorrectionsTask { get; set; }
        [DisplayName("Sandbox mode")]
        [Description("Recommended while developing: select a stable, deterministic sample from each media pool instead of scanning the whole library.")]
        public bool SandboxMode { get; set; } = true;
        [DisplayName("Samples per media pool")]
        [Description("Sandbox selects this many movies and this many series. The default is 50 + 50.")]
        public int SandboxSampleSizePerMediaType { get; set; } = 50;
        [DisplayName("Stable sample seed")]
        [Description("Keep this unchanged for repeatable test runs; change it to evaluate a different cohort.")]
        public int SandboxSeed { get; set; }
        [DisplayName("Always include Emby media IDs")]
        [Description("Optional comma- or space-separated Emby movie/series IDs. These titles are added to the sandbox without reducing the normal sample.")]
        public string SandboxIncludedMediaIds { get; set; }
        [DisplayName("Always include Emby person IDs")]
        [Description("Optional comma- or space-separated Emby person IDs. Movies/series directly associated with these people are added explicitly; this never expands transitively to further titles.")]
        public string SandboxIncludedPersonIds { get; set; }
        [DisplayName("Auto-expand affected-person media")]
        [Description("Recommended. After selecting the sandbox subset, add every provider-addressable movie and series credited to its affected people. Recommendations remain limited to those people; co-credited people encountered only during expansion do not enter the decision scope.")]
        public bool SandboxAutoExpandPersonMedia { get; set; } = true;
        [DisplayName("Populate Case Review with out of scope media items")]
        [Description("When a case review opens, query Emby for media relationships belonging to its existing people and add relationships missing from the gathered evidence. This does not expand or slow evidence gathering.")]
        public bool PopulateCaseReviewWithOutOfScopeMediaItems { get; set; } = true;

        public CaptionItem ProviderHeading { get; set; } = new CaptionItem("Provider access and caching");
        [DisplayName("TMDB v3 API key")]
        [Description("Stored only in Emby's plugin configuration. It is never written to the evidence database, payload cache or logs.")]
        public string TmdbApiKey { get; set; }
        [DisplayName("TVDB v4 API key")]
        [Description("Stored only in Emby's plugin configuration.")]
        public string TvdbApiKey { get; set; }
        [DisplayName("TVDB subscriber PIN (optional)")]
        public string TvdbSubscriberPin { get; set; }
        [DisplayName("Successful payload TTL (days)")]
        [Description("Fresh payloads bypass the network and JSON parsing. Expired responses are hashed; unchanged payloads refresh the TTL without re-flattening.")]
        public int CacheTtlDays { get; set; } = 7;
        [DisplayName("Failed request retry delay (minutes)")]
        public int FailureRetryMinutes { get; set; } = 30;
        [DisplayName("TMDB maximum concurrent requests")]
        [Description("Bounded TMDB worker count. TMDB and TVDB run in parallel; 4 is the conservative default.")]
        public int TmdbMaximumConcurrentRequests { get; set; } = 4;
        [DisplayName("TVDB maximum concurrent requests")]
        [Description("Bounded TVDB worker count with one shared bearer token; 2 is the conservative default.")]
        public int TvdbMaximumConcurrentRequests { get; set; } = 2;
        [DisplayName("TMDB minimum interval (milliseconds)")]
        public int TmdbMinimumRequestIntervalMilliseconds { get; set; } = 40;
        [DisplayName("TVDB minimum interval (milliseconds)")]
        public int TvdbMinimumRequestIntervalMilliseconds { get; set; } = 150;

        public CaptionItem ScoringHeading { get; set; } = new CaptionItem("Evidence decision thresholds");
        [DisplayName("Automatic alignment threshold")]
        [Description("The evidence contributions are fixed and versioned. This threshold controls when a conflict-free pair may join the constrained shadow graph.")]
        public double AutomaticMatchThreshold { get; set; }
        [DisplayName("Human-review threshold")]
        [Description("Conflict-free candidates below the automatic threshold but at or above this value are retained for review.")]
        public double HumanReviewThreshold { get; set; }

        public GenericItemList ScheduledTaskLink { get; set; } = new GenericItemList();
    }

    internal sealed class ConfigurationPageView : PageViewBase
    {
        private readonly IJsonSerializer json;
        private readonly ITaskManager tasks;
        private readonly ILogger logger;
        public ConfigurationPageView(PluginInfo plugin, IServerApplicationHost host, ILogger logger) : base(plugin.Id)
        {
            this.logger = logger; json = host.Resolve<IJsonSerializer>(); tasks = host.Resolve<ITaskManager>(); ShowSave = true; Rebuild();
        }

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (!string.IsNullOrWhiteSpace(data))
            {
                try
                {
                    var incoming = json.DeserializeFromString<ConfigurationUI>(data);
                    if (incoming != null) Save(incoming);
                }
                catch (Exception ex) { logger.ErrorException("Unable to save PersonCleaner configuration", ex); }
                Rebuild(); Refresh();
            }
            return Task.FromResult<IPluginUIView>(this);
        }

        private void Save(ConfigurationUI source)
        {
            var target = Plugin.Instance.Configuration;
            target.EnablePlugin = source.EnablePlugin;
            target.EnableMassCorrectionsTask = source.EnableMassCorrectionsTask;
            target.ExecutionMode = source.SandboxMode ? "Sandbox" : "Full";
            target.SandboxSampleSizePerMediaType = Clamp(source.SandboxSampleSizePerMediaType, 1, 500);
            target.SandboxSeed = source.SandboxSeed;
            target.SandboxIncludedMediaIds = NormalizeIdList(source.SandboxIncludedMediaIds);
            target.SandboxIncludedPersonIds = NormalizeIdList(source.SandboxIncludedPersonIds);
            target.SandboxAutoExpandPersonMedia = source.SandboxAutoExpandPersonMedia;
            target.PopulateCaseReviewWithOutOfScopeMediaItems = source.PopulateCaseReviewWithOutOfScopeMediaItems;
            target.TmdbApiKey = (source.TmdbApiKey ?? string.Empty).Trim();
            target.TvdbApiKey = (source.TvdbApiKey ?? string.Empty).Trim();
            target.TvdbSubscriberPin = (source.TvdbSubscriberPin ?? string.Empty).Trim();
            target.CacheTtlDays = Clamp(source.CacheTtlDays, 1, 365);
            target.FailureRetryMinutes = Clamp(source.FailureRetryMinutes, 1, 10080);
            target.TmdbMaximumConcurrentRequests = Clamp(source.TmdbMaximumConcurrentRequests, 1, 16);
            target.TvdbMaximumConcurrentRequests = Clamp(source.TvdbMaximumConcurrentRequests, 1, 8);
            target.TmdbMinimumRequestIntervalMilliseconds = Clamp(source.TmdbMinimumRequestIntervalMilliseconds, 0, 10000);
            target.TvdbMinimumRequestIntervalMilliseconds = Clamp(source.TvdbMinimumRequestIntervalMilliseconds, 0, 10000);
            target.AutomaticMatchThreshold = Unit(source.AutomaticMatchThreshold);
            target.HumanReviewThreshold = Math.Min(Unit(source.HumanReviewThreshold), target.AutomaticMatchThreshold);
            Plugin.Instance.SaveConfiguration();
            logger.Info("PersonCleaner configuration saved: mode={0}, mass corrections enabled={1}, sample={2}+{2}, explicit media={3}, explicit people={4}, complete affected people={5}, populate case review media={6}, TMDB key={7}, TVDB key={8}, TMDB concurrency={9}, TVDB concurrency={10}", target.ExecutionMode, target.EnableMassCorrectionsTask, target.SandboxSampleSizePerMediaType, CountIds(target.SandboxIncludedMediaIds), CountIds(target.SandboxIncludedPersonIds), target.SandboxAutoExpandPersonMedia, target.PopulateCaseReviewWithOutOfScopeMediaItems, !string.IsNullOrWhiteSpace(target.TmdbApiKey), !string.IsNullOrWhiteSpace(target.TvdbApiKey), target.TmdbMaximumConcurrentRequests, target.TvdbMaximumConcurrentRequests);
        }

        private void Rebuild()
        {
            var c = Plugin.Instance.Configuration;
            var worker = tasks.ScheduledTasks.FirstOrDefault(x => string.Equals(x.ScheduledTask.Key, "PersonCleanerEntityResolutionV2", StringComparison.Ordinal));
            var link = worker == null ? "/scheduledtasks" : "/scheduledtask?id=" + worker.Id;
            var massWorker = tasks.ScheduledTasks.FirstOrDefault(x => string.Equals(x.ScheduledTask.Key, "PersonCleanerMassCorrectionsV2", StringComparison.Ordinal));
            var massLink = massWorker == null ? "/scheduledtasks" : "/scheduledtask?id=" + massWorker.Id;
            ContentData = new ConfigurationUI
            {
                EnablePlugin = c.EnablePlugin, EnableMassCorrectionsTask = c.EnableMassCorrectionsTask, SandboxMode = !string.Equals(c.ExecutionMode, "Full", StringComparison.OrdinalIgnoreCase), SandboxSampleSizePerMediaType = c.SandboxSampleSizePerMediaType, SandboxSeed = c.SandboxSeed,
                SandboxIncludedMediaIds = c.SandboxIncludedMediaIds, SandboxIncludedPersonIds = c.SandboxIncludedPersonIds, SandboxAutoExpandPersonMedia = c.SandboxAutoExpandPersonMedia,
                PopulateCaseReviewWithOutOfScopeMediaItems = c.PopulateCaseReviewWithOutOfScopeMediaItems,
                TmdbApiKey = c.TmdbApiKey, TvdbApiKey = c.TvdbApiKey, TvdbSubscriberPin = c.TvdbSubscriberPin, CacheTtlDays = c.CacheTtlDays, FailureRetryMinutes = c.FailureRetryMinutes,
                TmdbMaximumConcurrentRequests = c.TmdbMaximumConcurrentRequests, TvdbMaximumConcurrentRequests = c.TvdbMaximumConcurrentRequests,
                TmdbMinimumRequestIntervalMilliseconds = c.TmdbMinimumRequestIntervalMilliseconds, TvdbMinimumRequestIntervalMilliseconds = c.TvdbMinimumRequestIntervalMilliseconds,
                AutomaticMatchThreshold = c.AutomaticMatchThreshold, HumanReviewThreshold = c.HumanReviewThreshold,
                ScheduledTaskLink = new GenericItemList
                {
                    new GenericListItem { PrimaryText = "Run or schedule evidence calculation", SecondaryText = "Hydration and calculation are background work; the dashboard stays query-only.", Icon = IconNames.schedule, Status = ItemStatus.Succeeded, HyperLink = link, HyperLinkTargetExternal = false },
                    new GenericListItem { PrimaryText = "Run or schedule Mass Corrections", SecondaryText = c.EnableMassCorrectionsTask ? "Enabled: applies only persisted satisfied changes from the latest completed run." : "Disabled by configuration (default): running the task makes no changes.", Icon = IconNames.schedule, Status = ItemStatus.Succeeded, HyperLink = massLink, HyperLinkTargetExternal = false }
                }
            };
        }

        private static int Clamp(int value, int minimum, int maximum) => Math.Max(minimum, Math.Min(maximum, value));
        private static double Unit(double value) => Math.Max(0, Math.Min(1, value));
        private static string NormalizeIdList(string value) => string.Join(",", ParseIds(value));
        private static int CountIds(string value) => ParseIds(value).Count();
        private static long[] ParseIds(string value) => (value ?? string.Empty)
            .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => long.TryParse(x, out var id) && id > 0 ? id : 0)
            .Where(x => x > 0).Distinct().OrderBy(x => x).ToArray();
    }
}
