using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using PersonCleaner.V2.Domain;
using PersonCleaner.V2.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.V2.UI
{
    public sealed class DecisionChangeDialogUI : EditableOptionsBase
    {
        public override string EditorTitle => null;
        public override string EditorDescription => null;
        public CaptionItem Decision { get; set; }
        public CaptionItem Result { get; set; }
        public CaptionItem ExactChanges { get; set; }
        public GenericItemList ScopedChanges { get; set; } = new GenericItemList();
        public ButtonItem UpdateEmby { get; set; }
        public ButtonItem AddProviderCorrection { get; set; }

        public static DecisionChangeDialogUI Build(DecisionChangePlan plan, string result)
        {
            var ui = new DecisionChangeDialogUI
            {
                Decision = new CaptionItem((plan.DisplayName ?? "Selected decision") + ": " + (plan.DecisionSummary ?? "No summary is available.")),
                ExactChanges = new CaptionItem(plan.Changes.Count == 0
                    ? "Exact in-scope changes: none."
                    : "Exact in-scope changes:\r\n" + string.Join("\r\n", plan.Changes.Select((change, index) => (index + 1) + ". " + change.Summary))),
                Result = new CaptionItem(result ?? "Review the exact in-scope changes below. Nothing is written until Update Emby is pressed.")
            };
            foreach (var change in plan.Changes)
                ui.ScopedChanges.Add(new GenericListItem { PrimaryText = change.Summary, SecondaryText = (change.ManualReviewOnly ? "Manual decision. " : string.Empty) + (change.EvidenceNote ?? string.Empty) + " Preconditions will be checked against live Emby immediately before the update.", Icon = IconNames.person, Status = change.ManualReviewOnly ? ItemStatus.Unavailable : ItemStatus.Succeeded });
            if (plan.Changes.Count == 0)
                ui.ScopedChanges.Add(new GenericListItem { PrimaryText = "No safe Emby mutation is recommended", SecondaryText = "This decision remains evidence for operator review; PersonCleaner will not infer a destructive change.", Icon = IconNames.person, Status = ItemStatus.Unavailable });
            else
                ui.UpdateEmby = new ButtonItem("Update Emby") { CommandId = "decision-update-emby", ConfirmationPrompt = plan.Changes.Any(x => x.ManualReviewOnly) ? "Apply every listed change to live Emby? One or more changes are manual judgments rather than automatic recommendations." : "Apply every listed change to live Emby after validating the current records?" };
            if (plan.RecommendedCorrection != null)
                ui.AddProviderCorrection = new ButtonItem("Add recommended provider correction") { CommandId = "decision-add-correction" };
            return ui;
        }
    }

    internal sealed class DecisionChangeDialogView : DialogViewBase
    {
        private readonly PluginInfo plugin;
        private readonly IServerApplicationHost host;
        private readonly IApplicationPaths paths;
        private readonly ILogger logger;
        private readonly IPluginUIView parent;
        private readonly Action rebuildParent;
        private readonly string decisionId;
        private DecisionChangePlan plan;
        private string result;

        public DecisionChangeDialogView(PluginInfo plugin, IServerApplicationHost host, ILogger logger, IPluginUIView parent, Action rebuildParent, string decisionId) : base(plugin.Id)
        {
            this.plugin = plugin; this.host = host; this.paths = host.Resolve<IApplicationPaths>(); this.logger = logger; this.parent = parent; this.rebuildParent = rebuildParent; this.decisionId = decisionId;
            AllowCancel = true; AllowOk = false; Rebuild();
        }

        public override bool ShowDialogFullScreen => false;
        public override string Caption => "Review Emby changes";

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (string.Equals(commandId, "DialogCancel", StringComparison.OrdinalIgnoreCase)) { rebuildParent(); return Task.FromResult(parent); }
            try
            {
                if (commandId == "decision-add-correction")
                {
                    var fresh = LoadPlan();
                    if (fresh.RecommendedCorrection == null) throw new InvalidOperationException("This decision no longer has a recommended provider correction.");
                    plan = fresh;
                    return Task.FromResult<IPluginUIView>(new CorrectionDialogView(plugin, host, logger, this, Rebuild, fresh.RecommendedCorrection));
                }
                if (commandId == "decision-update-emby")
                {
                    var fresh = LoadPlan();
                    if (fresh.Changes.Count == 0) throw new InvalidOperationException("This decision no longer contains an in-scope Emby change.");
                    var executor = new EmbyDecisionChangeExecutor(host.Resolve<ILibraryManager>());
                    executor.Apply(fresh);
                    using (var repository = Open())
                    {
                        repository.RecordCommittedEmbyChanges(fresh);
                        CorrectionRuntime.Recalculate(repository, logger);
                    }
                    logger.Info("PersonCleaner applied {0} validated Emby change(s) for decision {1}.", fresh.Changes.Count, fresh.DecisionId);
                    rebuildParent();
                    return Task.FromResult(parent);
                }
            }
            catch (Exception ex)
            {
                result = "No changes were applied: " + ex.Message;
                logger.ErrorException("Unable to apply PersonCleaner decision " + decisionId, ex);
                Rebuild(); Refresh();
            }
            return Task.FromResult<IPluginUIView>(this);
        }

        public override void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data) { Rebuild(); Refresh(); }

        private ResolutionRepository Open() { var repository = new ResolutionRepository(paths); repository.Initialize(); return repository; }
        private DecisionChangePlan LoadPlan() { using (var repository = Open()) return DecisionChangePlanner.Build(repository.DecisionChangeContext(decisionId)); }
        private void Rebuild()
        {
            try { plan = LoadPlan(); ContentData = DecisionChangeDialogUI.Build(plan, result); }
            catch (Exception ex)
            {
                logger.ErrorException("Unable to rebuild the PersonCleaner decision change dialog", ex);
                plan = plan ?? new DecisionChangePlan { DecisionId = decisionId, DisplayName = "Selected decision", DecisionSummary = "The decision is no longer available." };
                ContentData = DecisionChangeDialogUI.Build(plan, result ?? "The decision could not be reloaded: " + ex.Message);
            }
        }
    }

    internal sealed class EmbyDecisionChangeExecutor
    {
        private readonly ILibraryManager library;
        public EmbyDecisionChangeExecutor(ILibraryManager library) { this.library = library ?? throw new ArgumentNullException(nameof(library)); }

        public void Apply(DecisionChangePlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            Preflight(plan);
            foreach (var mediaGroup in plan.Changes.Where(x => x.Kind == EmbyChangeKinds.MoveCredit).GroupBy(x => x.MediaId.Value)) ApplyCreditMoves(mediaGroup.Key, mediaGroup.ToList());
            foreach (var personGroup in plan.Changes.Where(x => x.Kind == EmbyChangeKinds.SetPersonProviderId || x.Kind == EmbyChangeKinds.RemovePersonProviderId).GroupBy(x => x.SourcePersonId)) ApplyProviderIds(personGroup.Key, personGroup.ToList());
        }

        private void Preflight(DecisionChangePlan plan)
        {
            var inScope = new HashSet<long>(plan.InScopePersonIds ?? new List<long>());
            var globalPeople = plan.Changes.Any(x => x.Kind == EmbyChangeKinds.SetPersonProviderId)
                ? library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(Person).Name }, Recursive = true }, CancellationToken.None).OfType<Person>().ToList()
                : new List<Person>();
            foreach (var change in plan.Changes)
            {
                if (change.Kind == EmbyChangeKinds.SetPersonProviderId || change.Kind == EmbyChangeKinds.RemovePersonProviderId)
                {
                    var person = library.GetItemById(change.SourcePersonId) as Person ?? throw new InvalidOperationException("Emby person " + change.SourcePersonId + " no longer exists.");
                    var current = ProviderId(person, change.Provider);
                    if (!string.Equals(current ?? string.Empty, change.CurrentValue ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Emby person " + change.SourcePersonId + " now has a different " + change.Provider.ToUpperInvariant() + " ID; refresh the evidence before applying.");
                    if (change.Kind == EmbyChangeKinds.SetPersonProviderId)
                    {
                        var outsideOwner = globalPeople.FirstOrDefault(x => x.InternalId != change.SourcePersonId && !inScope.Contains(x.InternalId) && string.Equals(ProviderId(x, change.Provider), change.ProposedValue, StringComparison.OrdinalIgnoreCase));
                        if (outsideOwner != null)
                            throw new InvalidOperationException(change.Provider.ToUpperInvariant() + " person ID " + change.ProposedValue + " is already held by out-of-scope Emby person " + outsideOwner.InternalId + (string.IsNullOrWhiteSpace(outsideOwner.Name) ? string.Empty : " (" + outsideOwner.Name + ")") + "; add that person or relevant media to the explicit sandbox scope and rebuild evidence.");
                    }
                }
                else if (change.Kind == EmbyChangeKinds.MoveCredit)
                {
                    if (!change.MediaId.HasValue || !change.TargetPersonId.HasValue) throw new InvalidOperationException("A proposed credit move is incomplete.");
                    var media = library.GetItemById(change.MediaId.Value) ?? throw new InvalidOperationException("Emby media " + change.MediaId.Value + " no longer exists.");
                    if (!(library.GetItemById(change.SourcePersonId) is Person)) throw new InvalidOperationException("Source Emby person " + change.SourcePersonId + " no longer exists.");
                    if (!(library.GetItemById(change.TargetPersonId.Value) is Person)) throw new InvalidOperationException("Target Emby person " + change.TargetPersonId.Value + " no longer exists.");
                    if (!library.GetItemPeople(media).Any(x => x.Id == change.SourcePersonId && RoleText(x) == (change.Role ?? string.Empty)))
                        throw new InvalidOperationException("The scoped credit on Emby media " + change.MediaId.Value + " has changed; refresh the evidence before applying.");
                }
                else throw new InvalidOperationException("Unsupported Emby change " + change.Kind + ".");
            }
        }

        private void ApplyCreditMoves(long mediaId, List<EmbyChangeProposal> changes)
        {
            var media = library.GetItemById(mediaId) ?? throw new InvalidOperationException("Emby media " + mediaId + " no longer exists.");
            var people = library.GetItemPeople(media);
            foreach (var change in changes)
            {
                var target = (Person)library.GetItemById(change.TargetPersonId.Value);
                var sourceRows = people.Where(x => x.Id == change.SourcePersonId && RoleText(x) == (change.Role ?? string.Empty)).ToList();
                foreach (var source in sourceRows)
                {
                    var targetAlreadyPresent = people.Any(x => x.Id == target.InternalId && x.Type == source.Type && string.Equals(x.Role ?? string.Empty, source.Role ?? string.Empty, StringComparison.Ordinal));
                    if (targetAlreadyPresent) people.Remove(source);
                    else { source.Id = target.InternalId; source.Guid = target.Id; source.Name = target.Name; source.ProviderIds = target.ProviderIds; }
                }
            }
            library.UpdatePeople(media, people, false);
        }

        private void ApplyProviderIds(long personId, List<EmbyChangeProposal> changes)
        {
            var person = (Person)library.GetItemById(personId);
            foreach (var change in changes)
            {
                var provider = change.Provider == ProviderNames.Tmdb ? MetadataProviders.Tmdb : change.Provider == ProviderNames.Tvdb ? MetadataProviders.Tvdb : MetadataProviders.Imdb;
                if (change.Kind == EmbyChangeKinds.RemovePersonProviderId) person.ProviderIds.Remove(provider.ToString());
                else person.SetProviderId(provider, change.ProposedValue);
            }
            library.UpdateItem(person, null, ItemUpdateType.MetadataEdit);
        }

        private static string ProviderId(Person person, string provider) => provider == ProviderNames.Tmdb ? person.GetProviderId(MetadataProviders.Tmdb) : provider == ProviderNames.Tvdb ? person.GetProviderId(MetadataProviders.Tvdb) : provider == ProviderNames.Imdb ? person.GetProviderId(MetadataProviders.Imdb) : null;
        private static string RoleText(PersonInfo person) => person.Type + (string.IsNullOrWhiteSpace(person.Role) ? string.Empty : ": " + person.Role);
    }
}
