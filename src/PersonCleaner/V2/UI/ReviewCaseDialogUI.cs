using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using PersonCleaner.V2.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PersonCleaner.V2.UI
{
    public sealed class ReviewCaseDialogUI : EditableOptionsBase
    {
        public override string EditorTitle => null;
        public override string EditorDescription => null;
        public CaptionItem CaseHeading { get; set; } = new CaptionItem("Review case");
        public LabelItem Person { get; set; }
        public LabelItem Summary { get; set; }
        public CaptionItem AutomationHeading { get; set; } = new CaptionItem("Automation assessment");
        public LabelItem Automation { get; set; }
        public LabelItem AutomationReason { get; set; }
        public CaptionItem ScopeHeading { get; set; } = new CaptionItem("Case scope");
        public LabelItem Scope { get; set; }
        public ButtonItem ReviewRelationship { get; set; }
        public LabelItem Result { get; set; }

        public static ReviewCaseDialogUI Build(DashboardDecision row, string result = null)
        {
            var ids = row?.UnderlyingDecisionIds ?? new string[0];
            var labels = row?.UnderlyingDecisionLabels ?? new string[0];
            var ui = new ReviewCaseDialogUI
            {
                Person = new LabelItem(row?.Person ?? "Selected review case"),
                Summary = new LabelItem(row?.Decision ?? "No case summary is available."),
                Automation = new LabelItem(row?.Automation ?? "Not assessed"),
                AutomationReason = new LabelItem(row?.AutomationReason ?? row?.Why ?? "No automation explanation is available."),
                Scope = new LabelItem((row?.Relationships ?? ids.Length) + " underlying relationship(s) · " + (row?.ProviderRecords ?? 0) + " provider person record(s) · Emby anchor " + (row?.EmbyAnchor ?? "—") + "."),
                Result = new LabelItem(result ?? "Choose an underlying relationship to inspect its exact evidence, suggested provider correction, or validated Emby changes.")
            };
            var buttons = ids.Select((id, index) => new ButtonItem(index < labels.Length && !string.IsNullOrWhiteSpace(labels[index]) ? labels[index] : "Relationship " + (index + 1)) { CommandId = ReviewCaseCommands.Relationship + id }).ToList();
            if (buttons.Count == 1) ui.ReviewRelationship = buttons[0];
            else if (buttons.Count > 1) ui.ReviewRelationship = new ButtonItem("Review underlying relationship") { SubMenuButtons = buttons };
            return ui;
        }
    }

    internal static class ReviewCaseCommands
    {
        public const string Relationship = "case-review-relationship:";
    }

    internal sealed class ReviewCaseDialogView : DialogViewBase
    {
        private readonly PluginInfo plugin;
        private readonly IServerApplicationHost host;
        private readonly IApplicationPaths paths;
        private readonly ILogger logger;
        private readonly IPluginUIView parent;
        private readonly Action rebuildParent;
        private DashboardDecision reviewCase;
        private readonly HashSet<string> originalDecisionIds;
        private string result;

        public ReviewCaseDialogView(PluginInfo plugin, IServerApplicationHost host, ILogger logger, IPluginUIView parent, Action rebuildParent, DashboardDecision reviewCase) : base(plugin.Id)
        {
            this.plugin = plugin; this.host = host; this.paths = host.Resolve<IApplicationPaths>(); this.logger = logger; this.parent = parent; this.rebuildParent = rebuildParent; this.reviewCase = reviewCase;
            originalDecisionIds = new HashSet<string>(reviewCase?.UnderlyingDecisionIds ?? new string[0], StringComparer.Ordinal);
            AllowCancel = true; AllowOk = false; Rebuild();
        }

        public override bool ShowDialogFullScreen => false;
        public override string Caption => "Review identity case";

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (string.Equals(commandId, "DialogCancel", StringComparison.OrdinalIgnoreCase)) { rebuildParent(); return Task.FromResult(parent); }
            if ((commandId ?? string.Empty).StartsWith(ReviewCaseCommands.Relationship, StringComparison.Ordinal))
            {
                var decisionId = commandId.Substring(ReviewCaseCommands.Relationship.Length);
                if (!(reviewCase?.UnderlyingDecisionIds ?? new string[0]).Contains(decisionId, StringComparer.Ordinal))
                {
                    result = "That relationship is no longer part of this review case."; Rebuild(); Refresh(); return Task.FromResult<IPluginUIView>(this);
                }
                logger.Info("PersonCleaner opening underlying decision relationship {0} from review case {1}.", decisionId, reviewCase.CaseId);
                return Task.FromResult<IPluginUIView>(new DecisionChangeDialogView(plugin, host, logger, this, Rebuild, decisionId));
            }
            return Task.FromResult<IPluginUIView>(this);
        }

        public override void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data) { Rebuild(); Refresh(); }

        private void Rebuild()
        {
            try
            {
                using (var repository = new ResolutionRepository(paths))
                {
                    repository.Initialize();
                    var current = repository.Dashboard(PersonCleaner.Plugin.Instance.Configuration.MaximumMediaExamplesPerDecision)
                        .FirstOrDefault(x => x.CaseId == reviewCase?.CaseId || (x.UnderlyingDecisionIds ?? new string[0]).Any(originalDecisionIds.Contains));
                    if (current != null) reviewCase = current;
                    else result = "The review case is no longer present after recalculation. Close this dialog to refresh the evidence dashboard.";
                }
            }
            catch (Exception ex) { result = "The review case could not be reloaded: " + ex.Message; logger.ErrorException("Unable to rebuild the PersonCleaner review case dialog", ex); }
            ContentData = ReviewCaseDialogUI.Build(reviewCase, result);
        }
    }
}
