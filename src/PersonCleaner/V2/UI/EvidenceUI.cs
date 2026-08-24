using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Serialization;
using PersonCleaner.V2.Domain;
using PersonCleaner.V2.Storage;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace PersonCleaner.V2.UI
{
    public sealed class EvidenceUI : EditableOptionsBase
    {
        private string description;
        public override string EditorTitle => "Person resolution decisions";
        public override string EditorDescription => description ?? "No calculation run is available.";
        public CaptionItem RunSummary { get; set; }
        public ButtonItem OpenEvidence { get; set; } = new ButtonItem("Open decision evidence full screen") { CommandId = "open-evidence" };

        public CaptionItem OverrideHeading { get; set; } = new CaptionItem("Correct an alignment without refetching");
        [DisplayName("First provider")]
        [Description("Enter tmdb or tvdb.")]
        public string OverrideProviderA { get; set; }
        [DisplayName("First person ID")]
        public string OverridePersonIdA { get; set; }
        [DisplayName("Second provider")]
        [Description("Enter the other provider: tvdb or tmdb.")]
        public string OverrideProviderB { get; set; }
        [DisplayName("Second person ID")]
        public string OverridePersonIdB { get; set; }
        public ButtonItem ConfirmAlignment { get; set; } = new ButtonItem("Confirm these are the same person") { CommandId = "confirm-bridge", ConfirmationPrompt = "Treat these provider profiles as the same physical person and recalculate from cached evidence?" };
        public ButtonItem RejectAlignment { get; set; } = new ButtonItem("Keep these as different people") { CommandId = "reject-bridge", ConfirmationPrompt = "Permanently reject this provider alignment and recalculate from cached evidence?" };
        public CaptionItem OverrideResult { get; set; } = new CaptionItem("Enter the two provider person IDs shown in a decision row. Confirming or rejecting recalculates from flattened evidence and performs no API calls.");

        public static EvidenceUI Build(RunStatus run)
        {
            var summary = run == null ? "No task run exists. Open Configuration and run the scheduled task."
                : "Run " + run.RunId + " · " + run.Status + " · " + run.Mode + " · " + run.SelectedMovies + " movies + " + run.SelectedSeries + " series · " + run.MediaFetched + " media API fetches · " + run.PeopleFetched + " person API fetches · " + run.CacheHits + " cache hits · " + run.Failures + " failures · " + run.Decisions + " decisions (" + run.DecisionBreakdown + ")";
            return new EvidenceUI
            {
                RunSummary = new CaptionItem(summary),
                description = "Open the evidence viewer for a full-screen, read-only decision grid. The background task pre-calculates all rows; opening the viewer performs indexed reads only."
            };
        }
    }

    internal sealed class EvidencePageView : PageViewBase
    {
        private readonly IApplicationPaths paths;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private readonly PluginInfo plugin;
        private readonly IServerApplicationHost host;
        private string overrideResult;
        public EvidencePageView(PluginInfo plugin, IServerApplicationHost host, ILogger logger) : base(plugin.Id)
        {
            this.plugin = plugin; this.host = host; this.paths = host.Resolve<IApplicationPaths>(); this.json = host.Resolve<IJsonSerializer>(); this.logger = logger; ShowSave = false; Rebuild();
        }

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            try
            {
                if (commandId == "open-evidence")
                    return Task.FromResult<IPluginUIView>(new EvidenceDialogView(plugin, host, logger));

                if ((commandId == "confirm-bridge" || commandId == "reject-bridge") && !string.IsNullOrWhiteSpace(data))
                {
                    var values = json.DeserializeFromString<EvidenceUI>(data);
                    using (var repository = new ResolutionRepository(paths))
                    {
                        repository.Initialize();
                        repository.SaveBridge(values.OverrideProviderA, values.OverridePersonIdA, values.OverrideProviderB, values.OverridePersonIdB, commandId == "reject-bridge");
                        var run = repository.LatestRun();
                        if (run != null)
                        {
                            var c = Plugin.Instance.Configuration;
                            var settings = new ResolutionSettings { AutomaticMatchThreshold = c.AutomaticMatchThreshold, HumanReviewThreshold = c.HumanReviewThreshold, MaximumMediaExamples = c.MaximumMediaExamplesPerDecision };
                            var engine = new ResolutionEngine();
                            var decisions = engine.Resolve(repository.LoadResolutionInput(), settings);
                            repository.SaveDecisions(run.RunId, decisions, engine.PairEvaluations, engine.Clusters);
                        }
                    }
                    overrideResult = commandId == "reject-bridge" ? "Alignment rejected and cached decisions recalculated." : "Alignment confirmed and cached decisions recalculated.";
                }
            }
            catch (Exception ex) { overrideResult = "Override was not applied: " + ex.Message; logger.ErrorException("Unable to apply the PersonCleaner alignment override", ex); }
            Rebuild(); Refresh();
            return Task.FromResult<IPluginUIView>(this);
        }

        private void Rebuild()
        {
            try
            {
                using (var repository = new ResolutionRepository(paths))
                {
                    repository.Initialize();
                    var run = repository.LatestRun();
                    ContentData = EvidenceUI.Build(run);
                    if (!string.IsNullOrWhiteSpace(overrideResult)) ((EvidenceUI)ContentData).OverrideResult = new CaptionItem(overrideResult);
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("Unable to load PersonCleaner decision evidence", ex);
                ContentData = EvidenceUI.Build(null);
            }
        }
    }
}
