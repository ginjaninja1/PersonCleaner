using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
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
        public CaptionItem DecisionHeading { get; set; } = new CaptionItem("Decision");
        public LabelItem DecisionName { get; set; }
        public LabelItem DecisionSummary { get; set; }
        public CaptionItem ScopedChangesHeading { get; set; } = new CaptionItem("Proposed action");
        public LabelItem ScopedChanges { get; set; }
        public ButtonItem UpdateEmby { get; set; }
        public ButtonItem AddProviderCorrection { get; set; }
        public CaptionItem ResultHeading { get; set; } = new CaptionItem("Result");
        public LabelItem ResultSummary { get; set; }

        public static DecisionChangeDialogUI Build(DecisionChangePlan plan, string result)
        {
            var ui = new DecisionChangeDialogUI();
            ui.DecisionName = new LabelItem(plan.DisplayName ?? "Selected decision");
            ui.DecisionSummary = new LabelItem(plan.DecisionSummary ?? "No summary is available.");
            var changes = plan.Changes.Select((change, index) =>
                (plan.Changes.Count > 1 ? (index + 1) + ". " : string.Empty) + change.Summary + " " +
                (change.ManualReviewOnly ? "This requires a manual decision. " : string.Empty) +
                (change.EvidenceNote ?? string.Empty) + " Preconditions will be checked against live Emby immediately before the update.").ToList();
            if (plan.Changes.Count == 0)
                ui.ScopedChanges = new LabelItem((plan.NoChangeSummary ?? "No safe Emby change is recommended") + ". " + (plan.NoChangeExplanation ?? "This decision remains evidence for operator review; PersonCleaner will not infer a destructive change."));
            else
            {
                ui.ScopedChanges = new LabelItem(string.Join(Environment.NewLine + Environment.NewLine, changes));
                ui.UpdateEmby = new ButtonItem("Update Emby") { CommandId = "decision-update-emby", ConfirmationPrompt = plan.Changes.Any(x => x.ManualReviewOnly) ? "Apply every listed change to live Emby? One or more changes are manual judgments rather than automatic recommendations." : "Apply every listed change to live Emby after validating the current records?" };
            }
            ui.ResultSummary = new LabelItem(result ?? (plan.Changes.Count == 0 ? "Nothing has been written to Emby." : "Nothing has been written. Review the exact changes above; they are applied only after Update Emby is pressed."));
            if (plan.RecommendedCorrection != null)
                ui.AddProviderCorrection = new ButtonItem("Review suggested provider correction") { CommandId = "decision-add-correction" };
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
        public override string Caption => "Review decision and changes";

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
                    }
                    logger.Info("PersonCleaner applied {0} validated Emby change(s) for decision {1}. The cached whole-run graph was not rebuilt interactively; the next PersonCleaner evidence task will refresh related decisions.", fresh.Changes.Count, fresh.DecisionId);
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
        private const string ResolverTokenProvider = "PersonCleanerMergeToken";
        private readonly ILibraryManager library;
        public EmbyDecisionChangeExecutor(ILibraryManager library) { this.library = library ?? throw new ArgumentNullException(nameof(library)); }

        public void Apply(DecisionChangePlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            Preflight(plan);
            foreach (var personGroup in plan.Changes.Where(x => x.Kind == EmbyChangeKinds.RemovePersonProviderId).GroupBy(x => x.SourcePersonId)) ApplyProviderIds(personGroup.Key, personGroup.ToList());
            foreach (var personGroup in plan.Changes.Where(x => x.Kind == EmbyChangeKinds.SetPersonProviderId).GroupBy(x => x.SourcePersonId)) ApplyProviderIds(personGroup.Key, personGroup.ToList());
            foreach (var mediaGroup in plan.Changes.Where(x => x.Kind == EmbyChangeKinds.MoveCredit).GroupBy(x => x.MediaId.Value)) ApplyCreditMoves(mediaGroup.Key, mediaGroup.ToList());
            Postflight(plan);
        }

        private void Preflight(DecisionChangePlan plan)
        {
            var inScope = new HashSet<long>(plan.InScopePersonIds ?? new List<long>());
            var requestedIds = plan.Changes.Where(x => x.Kind == EmbyChangeKinds.SetPersonProviderId && !string.IsNullOrWhiteSpace(x.ProposedValue))
                .Select(x => new KeyValuePair<string, string>(ProviderName(x.Provider), x.ProposedValue)).Distinct(ProviderIdPairComparer.Instance).ToList();
            var globalPeople = requestedIds.Count > 0
                ? library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { typeof(Person).Name }, Recursive = true, AnyProviderIdEquals = requestedIds }, CancellationToken.None).OfType<Person>().ToList()
                : new List<Person>();
            var mediaPeople = plan.Changes.Where(x => x.Kind == EmbyChangeKinds.MoveCredit && x.MediaId.HasValue).Select(x => x.MediaId.Value).Distinct().ToDictionary(x => x, ReadPeople);
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
                    var livePeople = mediaPeople[media.InternalId];
                    if (!livePeople.Any(x => x.Id == change.SourcePersonId && RoleText(x) == (change.Role ?? string.Empty)))
                    {
                        var liveRoles = livePeople.Where(x => x.Id == change.SourcePersonId).Select(RoleText).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                        throw new InvalidOperationException(liveRoles.Count == 0
                            ? "Cannot move the expected credit '" + (change.Role ?? string.Empty) + "' on Emby media " + change.MediaId.Value + ": source person " + change.SourcePersonId + " is no longer credited on that item. Rebuild the evidence before applying."
                            : "Cannot move the expected credit '" + (change.Role ?? string.Empty) + "' on Emby media " + change.MediaId.Value + ": source person " + change.SourcePersonId + " now has " + string.Join(", ", liveRoles.Select(x => "'" + x + "'")) + ". Rebuild the evidence before applying.");
                    }
                }
                else throw new InvalidOperationException("Unsupported Emby change " + change.Kind + ".");
            }
        }

        private void ApplyCreditMoves(long mediaId, List<EmbyChangeProposal> changes)
        {
            var media = library.GetItemById(mediaId) ?? throw new InvalidOperationException("Emby media " + mediaId + " no longer exists.");
            var people = ReadPeople(mediaId);
            var targets = changes.Select(x => x.TargetPersonId.Value).Distinct().ToDictionary(x => x, x => (Person)library.GetItemById(x));
            var tokens = targets.ToDictionary(x => x.Key, x => Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var target in targets)
                {
                    target.Value.ProviderIds[ResolverTokenProvider] = tokens[target.Key];
                    library.UpdateItem(target.Value, null, ItemUpdateType.MetadataEdit);
                }
                foreach (var row in people.Where(x => targets.ContainsKey(x.Id))) SetResolverIdentity(row, targets[row.Id], tokens[row.Id]);

                foreach (var change in changes)
                {
                    var target = targets[change.TargetPersonId.Value];
                    var sourceRows = people.Where(x => x.Id == change.SourcePersonId && RoleText(x) == (change.Role ?? string.Empty)).ToList();
                    foreach (var source in sourceRows)
                    {
                        var targetAlreadyPresent = people.Any(x => x.Id == target.InternalId && x.Type == source.Type && string.Equals(x.Role ?? string.Empty, source.Role ?? string.Empty, StringComparison.Ordinal));
                        if (targetAlreadyPresent) people.Remove(source);
                        else SetResolverIdentity(source, target, tokens[target.InternalId]);
                    }
                }
                library.UpdatePeople(media, people, false);
            }
            finally
            {
                foreach (var targetId in targets.Keys)
                {
                    var target = library.GetItemById(targetId) as Person;
                    if (target == null) continue;
                    target.ProviderIds.Remove(ResolverTokenProvider);
                    library.UpdateItem(target, null, ItemUpdateType.MetadataEdit);
                }
            }
        }

        private static void SetResolverIdentity(PersonInfo row, Person target, string token)
        {
            row.Id = target.InternalId;
            row.Guid = target.Id;
            row.Name = target.Name;
            row.ProviderIds = new ProviderIdDictionary { [ResolverTokenProvider] = token };
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

        private void Postflight(DecisionChangePlan plan)
        {
            foreach (var change in plan.Changes)
            {
                if (change.Kind == EmbyChangeKinds.SetPersonProviderId || change.Kind == EmbyChangeKinds.RemovePersonProviderId)
                {
                    var person = library.GetItemById(change.SourcePersonId) as Person ?? throw new InvalidOperationException("Post-update verification failed: Emby person " + change.SourcePersonId + " no longer exists.");
                    var expected = change.Kind == EmbyChangeKinds.RemovePersonProviderId ? string.Empty : change.ProposedValue ?? string.Empty;
                    var actual = ProviderId(person, change.Provider) ?? string.Empty;
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Post-update verification failed: Emby person " + change.SourcePersonId + " has " + change.Provider.ToUpperInvariant() + " ID '" + actual + "' instead of expected '" + expected + "'. No success was recorded.");
                }
                else if (change.Kind == EmbyChangeKinds.MoveCredit)
                {
                    var people = ReadPeople(change.MediaId.Value);
                    var expectedRole = change.Role ?? string.Empty;
                    if (people.Any(x => x.Id == change.SourcePersonId && RoleText(x) == expectedRole) || !people.Any(x => x.Id == change.TargetPersonId.Value && RoleText(x) == expectedRole))
                        throw new InvalidOperationException("Post-update verification failed: Emby media " + change.MediaId.Value + " did not move '" + expectedRole + "' from person " + change.SourcePersonId + " to person " + change.TargetPersonId.Value + ". No success was recorded.");
                }
            }
        }

        private List<PersonInfo> ReadPeople(long mediaId) => library.GetItemPeople(new InternalPeopleQuery
        {
            ItemIds = new[] { mediaId },
            EnableIds = true,
            EnableProviderIds = true,
            EnableGroupByName = false
        });

        private static string ProviderId(Person person, string provider) => provider == ProviderNames.Tmdb ? person.GetProviderId(MetadataProviders.Tmdb) : provider == ProviderNames.Tvdb ? person.GetProviderId(MetadataProviders.Tvdb) : provider == ProviderNames.Imdb ? person.GetProviderId(MetadataProviders.Imdb) : null;
        private static string ProviderName(string provider) => provider == ProviderNames.Tmdb ? MetadataProviders.Tmdb.ToString() : provider == ProviderNames.Tvdb ? MetadataProviders.Tvdb.ToString() : MetadataProviders.Imdb.ToString();
        private static string RoleText(PersonInfo person) => person.Type + (string.IsNullOrWhiteSpace(person.Role) ? string.Empty : ": " + person.Role);
        private sealed class ProviderIdPairComparer : IEqualityComparer<KeyValuePair<string, string>>
        {
            public static readonly ProviderIdPairComparer Instance = new ProviderIdPairComparer();
            public bool Equals(KeyValuePair<string, string> x, KeyValuePair<string, string> y) => string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase);
            public int GetHashCode(KeyValuePair<string, string> value) => StringComparer.OrdinalIgnoreCase.GetHashCode(value.Key ?? string.Empty) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(value.Value ?? string.Empty);
        }
    }
}
