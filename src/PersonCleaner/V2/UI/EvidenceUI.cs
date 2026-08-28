using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using PersonCleaner.V2.Storage;
using System;
using System.Threading.Tasks;

namespace PersonCleaner.V2.UI
{
    public sealed class EvidenceUI : EditableOptionsBase
    {
        private string description;
        public override string EditorTitle => "Person resolution decisions";
        public override string EditorDescription => description ?? "No calculation run is available.";
        public CaptionItem RunSummary { get; set; }
        public ButtonItem OpenProblemEvidence { get; set; } = new ButtonItem("Open Problem Cases") { CommandId = "open-problem-evidence" };
        public ButtonItem OpenSatisfiedEvidence { get; set; } = new ButtonItem("Open Cases Resolved By Task") { CommandId = "open-satisfied-evidence" };
        public ButtonItem OpenEvidence { get; set; } = new ButtonItem("Open All cases (Dev)") { CommandId = "open-evidence" };

        public static EvidenceUI Build(RunStatus run)
        {
            var summary = run == null ? "No task run exists. Open Configuration and run the scheduled task."
                : "Run " + run.RunId + " · " + run.Status + " · " + run.Mode + " · " + run.SelectedMovies + " movies + " + run.SelectedSeries + " series · " + run.MediaFetched + " media API fetches · " + run.PeopleFetched + " person API fetches · " + run.CacheHits + " cache hits · " + run.Failures + " failures · " + run.Cases + " cases · \nMATCH=" + run.AutoApplicableCases + " queued for Mass Corrections · " + run.AppliedCases + " applied · " + run.SatisfiedNoChangeCases + " satisfied/no change · " + run.ProblemCases + " problem cases \n(" + run.DecisionBreakdown + ")";
            return new EvidenceUI
            {
                RunSummary = new CaptionItem(summary),
                description = "Open the SQL-filtered problem, satisfied-change, or all-case evidence grid. Expand a row for evidence, or tick Open case to preview the exact validated Emby operations in a separate dialog."
            };
        }
    }

    internal sealed class EvidencePageView : PageViewBase
    {
        private readonly IApplicationPaths paths;
        private readonly ILogger logger;
        private readonly PluginInfo plugin;
        private readonly IServerApplicationHost host;
        public EvidencePageView(PluginInfo plugin, IServerApplicationHost host, ILogger logger) : base(plugin.Id)
        {
            this.plugin = plugin; this.host = host; this.paths = host.Resolve<IApplicationPaths>(); this.logger = logger; ShowSave = false; Rebuild();
        }

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            try
            {
                if (commandId == "open-problem-evidence")
                    return Task.FromResult<IPluginUIView>(new EvidenceDialogView(plugin, host, logger, EvidenceCaseFilter.Problem));
                if (commandId == "open-satisfied-evidence")
                    return Task.FromResult<IPluginUIView>(new EvidenceDialogView(plugin, host, logger, EvidenceCaseFilter.SatisfiedChange));
                if (commandId == "open-evidence")
                    return Task.FromResult<IPluginUIView>(new EvidenceDialogView(plugin, host, logger, EvidenceCaseFilter.All));
            }
            catch (Exception ex) { logger.ErrorException("Unable to open the PersonCleaner evidence dialog", ex); }
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
