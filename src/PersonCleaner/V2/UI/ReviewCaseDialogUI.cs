using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.DxGrid;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Serialization;
using PersonCleaner.V2.Domain;
using PersonCleaner.V2.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.V2.UI
{
    public sealed class ReviewCaseDialogUI : EditableObjectBase
    {
        public override string EditorTitle => null;
        public CaptionItem CaseHeading { get; set; }
        public LabelItem ProposedResult { get; set; }
        public LabelItem InformationalWarnings { get; set; }
        public LabelItem CorrectionQuestions { get; set; }
        public ButtonItem SuggestedProviderCorrections { get; set; }
        public ButtonItem RecalculatePendingCorrections { get; set; }
        public ButtonItem BackToAllCases { get; set; } = new ButtonItem("Back to all cases") { CommandId = ReviewCaseCommands.Back };
        public ButtonItem Apply { get; set; }
        [GridDataSource(nameof(Rows))]
        public DxDataGrid Identities { get; set; }
        public ReviewIdentityRow[] Rows { get; set; } = new ReviewIdentityRow[0];

        public static ReviewCaseDialogUI Build(IdentityCasePlan plan, LocalPerson[] people, string serverId, string result, IReadOnlyList<ProviderCorrection> suggestedCorrections = null, int pendingCorrections = 0)
        {
            var byId = (people ?? new LocalPerson[0]).GroupBy(x => x.EmbyId).ToDictionary(x => x.Key, x => x.First());
            var rows = plan.Outcomes.OrderBy(x => x.SortOrder).ThenBy(x => x.OutcomeId, StringComparer.Ordinal).Select(x => BuildIdentity(plan, x, byId, serverId)).ToArray();
            var master = Grid(new ReviewIdentityRow(), nameof(ReviewIdentityRow.RowId), ReviewCaseCommands.IdentityGrid);
            var detail = Grid(new ReviewMediaRow(), nameof(ReviewMediaRow.RowId), ReviewCaseCommands.MediaGrid);
            detail.heightMode = DxGridOptions.GridHeightMode.auto;
            detail.scrolling = new DxGridScrolling { mode = DxGridScrolling.ScrollingMode.standard, showScrollbar = DxGridScrolling.ShowScrollbarMode.always, scrollByContent = true, scrollByThumb = true, useNative = "false" };
            ConfigureIdentityColumns(master);
            ConfigureMediaColumns(detail);
            master.masterDetail = new DxGridMasterDetail { enabled = true, autoExpandAll = false, childRowsFieldName = nameof(ReviewIdentityRow.Media), detailGridOptions = detail };
            var questions = plan.Questions.Select(x => x.Narrative).Distinct(StringComparer.Ordinal).ToList();
            var ui = new ReviewCaseDialogUI
            {
                CaseHeading = new CaptionItem(plan.DisplayName + " — " + plan.CaseType),
                ProposedResult = new LabelItem((result == null ? string.Empty : result + Environment.NewLine + Environment.NewLine) + plan.Summary),
                InformationalWarnings = new LabelItem(string.IsNullOrWhiteSpace(plan.Warning) ? "No informational warnings." : plan.Warning),
                CorrectionQuestions = new LabelItem(questions.Count == 0 ? "No genuine correction questions remain." : string.Join(Environment.NewLine, questions.Select((x, i) => (i + 1) + ". " + x))),
                Identities = new DxDataGrid(master), Rows = rows
            };
            var corrections = suggestedCorrections ?? new ProviderCorrection[0];
            var correctionButtons = corrections.Select((x, i) => new ButtonItem(SuggestedCorrectionCaption(plan, x)) { CommandId = ReviewCaseCommands.ProviderCorrection + i.ToString(CultureInfo.InvariantCulture) }).ToList();
            if (correctionButtons.Count == 1) ui.SuggestedProviderCorrections = correctionButtons[0];
            if (correctionButtons.Count > 1) ui.SuggestedProviderCorrections = new ButtonItem("Review one of " + correctionButtons.Count + " suggested provider corrections") { SubMenuButtons = correctionButtons };
            if (pendingCorrections > 0) ui.RecalculatePendingCorrections = new ButtonItem("Recalculate " + pendingCorrections + " saved correction" + (pendingCorrections == 1 ? string.Empty : "s")) { CommandId = ReviewCaseCommands.RecalculatePending, ConfirmationPrompt = "Recalculate the complete evidence graph once using every saved correction?" };
            if (plan.State == IdentityPlanStates.Complete && IdentityCaseExecutor.HasMutations(plan))
                ui.Apply = new ButtonItem(plan.ApplyCaption) { CommandId = ReviewCaseCommands.Apply, ConfirmationPrompt = "Apply exactly the reviewed person-ID and media-credit changes after re-reading live Emby?" };
            return ui;
        }

        private static string SuggestedCorrectionCaption(IdentityCasePlan plan, ProviderCorrection correction)
        {
            if (correction.Kind == CorrectionKinds.MediaCredit)
            {
                var credit = plan.Credits.FirstOrDefault(x => x.MediaType == correction.MediaType && (correction.Provider == ProviderNames.Tmdb ? x.TmdbId : x.TvdbId) == correction.ProviderMediaId);
                var media = credit?.MediaName ?? correction.MediaType + " " + correction.ProviderMediaId;
                var action = correction.Operation == CorrectionOperations.Replace ? correction.ProviderPersonId + " → " + correction.ReplacementValue : "remove " + correction.ProviderPersonId;
                return "Fix " + correction.Provider.ToUpperInvariant() + " credit: " + media + " — " + action;
            }
            return "Review suggested " + (correction.Kind ?? "provider correction").Replace('-', ' ');
        }

        private static DxGridOptions Grid(object row, string key, string command)
        {
            return new DxGridOptions(row, key, false, true, true, true)
            {
                // fullHeight resolves to nested 100% containers in GenericEdit and can
                // put the grid's bottom edge outside the dialog. Keep the largest safe
                // bounded height and use standard rendering so its bottom is reachable.
                heightMode = DxGridOptions.GridHeightMode.large, columnAutoWidth = false, allowColumnReordering = true, allowColumnResizing = true,
                showBorders = true, showRowLines = true, rowAlternationEnabled = true, wordWrapEnabled = true, cellHintEnabled = true,
                paging = new DxGridPaging { enabled = false }, editing = new DxGridEditing { mode = DxGridEditing.GridEditMode.cell, allowUpdating = true },
                onChangeCommand = new DxGridOnChangeCommand { commandId = command },
                scrolling = new DxGridScrolling { mode = DxGridScrolling.ScrollingMode.standard, rowRenderingMode = DxGridScrolling.RowRenderingMode.standard, showScrollbar = DxGridScrolling.ShowScrollbarMode.always, scrollByContent = true, scrollByThumb = true, useNative = "false" }
            };
        }

        private static void ConfigureIdentityColumns(DxGridOptions grid)
        {
            foreach (var c in grid.columns)
            {
                c.allowEditing = false; c.allowGrouping = false; c.allowHeaderFiltering = false;
                if (c.dataField == nameof(ReviewIdentityRow.RowId) || c.dataField == nameof(ReviewIdentityRow.OutcomeId) || c.dataField == nameof(ReviewIdentityRow.Media)) c.visible = false;
                if (c.dataField == nameof(ReviewIdentityRow.Media)) c.isSecondaryGridDataSource = true;
                if (c.dataField == nameof(ReviewIdentityRow.CorrectIdentity)) { c.caption = "Override identity"; c.allowEditing = true; c.width = 115; }
                if (c.dataField == nameof(ReviewIdentityRow.CurrentName)) { c.caption = "Current name"; c.encodeHtml = false; c.width = 150; }
                if (c.dataField == nameof(ReviewIdentityRow.CurrentEmby)) { c.caption = "Current Emby"; c.encodeHtml = false; c.width = 95; }
                if (c.dataField == nameof(ReviewIdentityRow.CurrentTmdb)) { c.caption = "Current TMDB"; c.encodeHtml = false; c.width = 105; }
                if (c.dataField == nameof(ReviewIdentityRow.CurrentTvdb)) { c.caption = "Current TVDB"; c.encodeHtml = false; c.width = 105; }
                if (c.dataField == nameof(ReviewIdentityRow.CurrentImdb)) { c.caption = "Current IMDb"; c.encodeHtml = false; c.width = 115; }
                if (c.dataField == nameof(ReviewIdentityRow.ResultName)) { c.caption = "Result name"; c.encodeHtml = false; c.width = 150; }
                if (c.dataField == nameof(ReviewIdentityRow.ResultEmby)) { c.caption = "Result Emby"; c.encodeHtml = false; c.width = 95; }
                if (c.dataField == nameof(ReviewIdentityRow.ResultTmdb)) { c.caption = "Result TMDB"; c.encodeHtml = false; c.width = 105; }
                if (c.dataField == nameof(ReviewIdentityRow.ResultTvdb)) { c.caption = "Result TVDB"; c.encodeHtml = false; c.width = 105; }
                if (c.dataField == nameof(ReviewIdentityRow.ResultImdb)) { c.caption = "Result IMDb"; c.encodeHtml = false; c.width = 115; }
                if (c.dataField == nameof(ReviewIdentityRow.IdChanges)) { c.caption = "ID changes"; c.width = 230; }
                if (c.dataField == nameof(ReviewIdentityRow.Outcome)) { c.caption = "Outcome"; c.width = 250; }
            }
        }

        private static void ConfigureMediaColumns(DxGridOptions grid)
        {
            foreach (var c in grid.columns)
            {
                c.allowEditing = false; c.allowGrouping = false; c.allowHeaderFiltering = false;
                if (c.dataField == nameof(ReviewMediaRow.RowId) || c.dataField == nameof(ReviewMediaRow.AssignmentId)) c.visible = false;
                if (c.dataField == nameof(ReviewMediaRow.Media)) { c.encodeHtml = false; c.width = 260; }
                if (c.dataField == nameof(ReviewMediaRow.Role)) c.width = 210;
                if (c.dataField == nameof(ReviewMediaRow.Tmdb) || c.dataField == nameof(ReviewMediaRow.Tvdb) || c.dataField == nameof(ReviewMediaRow.Imdb)) { c.encodeHtml = false; c.width = 105; }
                if (c.dataField == nameof(ReviewMediaRow.Action)) c.width = 330;
                if (c.dataField == nameof(ReviewMediaRow.CorrectAssignment)) { c.caption = "Correct attribution"; c.allowEditing = true; c.width = 125; }
                if (c.dataField == nameof(ReviewMediaRow.CorrectRole)) { c.caption = "Override role"; c.allowEditing = true; c.width = 95; }
            }
        }

        private static ReviewIdentityRow BuildIdentity(IdentityCasePlan plan, IdentityOutcome outcome, IDictionary<long, LocalPerson> people, string serverId)
        {
            LocalPerson current = null;
            if (outcome.TargetEmbyId.HasValue) people.TryGetValue(outcome.TargetEmbyId.Value, out current);
            if (current == null) current = outcome.SourceEmbyIds.Select(x => people.TryGetValue(x, out var p) ? p : null).FirstOrDefault(x => x != null);
            var row = new ReviewIdentityRow
            {
                RowId = outcome.OutcomeId, OutcomeId = outcome.OutcomeId,
                CurrentName = current == null ? "—" : CaseLinks.Emby(current.EmbyId, serverId, current.Name), CurrentEmby = current == null ? "—" : CaseLinks.Emby(current.EmbyId, serverId, current.EmbyId.ToString(CultureInfo.InvariantCulture)),
                CurrentTmdb = current == null ? "—" : CaseLinks.Person(ProviderNames.Tmdb, current.TmdbId), CurrentTvdb = current == null ? "—" : CaseLinks.Person(ProviderNames.Tvdb, current.TvdbId), CurrentImdb = current == null ? "—" : CaseLinks.Person(ProviderNames.Imdb, current.ImdbId),
                ResultName = WebUtility.HtmlEncode(outcome.DisplayName), ResultEmby = outcome.TargetKind == IdentityTargetKinds.Existing ? CaseLinks.Emby(outcome.TargetEmbyId.Value, serverId, outcome.TargetEmbyId.Value.ToString(CultureInfo.InvariantCulture)) : outcome.TargetKind == IdentityTargetKinds.New ? "New" : current == null ? "Pending review" : CaseLinks.Emby(current.EmbyId, serverId, "Pending — retain " + current.EmbyId.ToString(CultureInfo.InvariantCulture)),
                ResultTmdb = CaseLinks.Person(ProviderNames.Tmdb, Id(outcome, ProviderNames.Tmdb)), ResultTvdb = CaseLinks.Person(ProviderNames.Tvdb, Id(outcome, ProviderNames.Tvdb)), ResultImdb = CaseLinks.Person(ProviderNames.Imdb, Id(outcome, ProviderNames.Imdb)),
                IdChanges = Changes(current, outcome), Outcome = outcome.Outcome
            };
            var media = new List<ReviewMediaRow>();
            foreach (var credit in plan.Credits.OrderByDescending(x => x.CorrectionRequired).ThenBy(x => x.MediaName, StringComparer.Ordinal).ThenBy(x => x.MediaEmbyId))
            {
                var sourceHere = outcome.SourceEmbyIds.Contains(credit.SourcePersonEmbyId);
                var targetHere = credit.TargetOutcomeId == outcome.OutcomeId;
                if (!sourceHere && !targetHere) continue;
                if (sourceHere) media.Add(MediaRow(plan, credit, outcome, serverId, false));
                if (targetHere && !sourceHere) media.Add(MediaRow(plan, credit, outcome, serverId, true));
            }
            row.Media = media.ToArray();
            return row;
        }

        private static ReviewMediaRow MediaRow(IdentityCasePlan plan, IdentityCreditOutcome credit, IdentityOutcome owner, string serverId, bool received)
        {
            var target = plan.Outcomes.First(x => x.OutcomeId == credit.TargetOutcomeId);
            var source = plan.Outcomes.FirstOrDefault(x => x.SourceEmbyIds.Contains(credit.SourcePersonEmbyId));
            string action;
            var question = plan.Questions.FirstOrDefault(x => x.AssignmentId == credit.AssignmentId);
            if (credit.CorrectionRequired && question?.Kind == CorrectionKinds.MediaCredit) action = "Correction required — choose which provider title-credit assertion is wrong";
            else if (credit.CorrectionRequired) action = "Correction required — choose the receiving person";
            else if (received) action = "Projected — receive from " + (source?.DisplayName ?? "Emby person") + " — Emby " + credit.SourcePersonEmbyId;
            else if (credit.Disposition == "KEEP") action = "Projected — keep";
            else action = "Projected — move to " + (target.TargetKind == IdentityTargetKinds.New ? "New person — " : string.Empty) + target.DisplayName + (target.TargetEmbyId.HasValue ? " — Emby " + target.TargetEmbyId : string.Empty);
            return new ReviewMediaRow
            {
                RowId = credit.AssignmentId + (received ? ":receive:" : ":source:") + owner.OutcomeId, AssignmentId = credit.AssignmentId,
                Media = CaseLinks.Emby(credit.MediaEmbyId, serverId, credit.MediaName), Role = credit.Role,
                Tmdb = CaseLinks.Media(ProviderNames.Tmdb, credit.MediaType, credit.TmdbId), Tvdb = CaseLinks.Media(ProviderNames.Tvdb, credit.MediaType, credit.TvdbId, credit.TvdbSlug), Imdb = CaseLinks.Media(ProviderNames.Imdb, credit.MediaType, credit.ImdbId), Action = action
            };
        }

        private static string Id(IdentityOutcome outcome, string provider) => IdentityCasePlanner.PreferredProviderId(outcome, provider);
        private static string Changes(LocalPerson current, IdentityOutcome outcome)
        {
            if (outcome.TargetKind == IdentityTargetKinds.Unresolved) return "No Emby change proposed until the provider assertion is corrected";
            var changes = new List<string>();
            foreach (var provider in new[] { ProviderNames.Tmdb, ProviderNames.Tvdb, ProviderNames.Imdb })
            {
                var before = current == null ? null : provider == ProviderNames.Tmdb ? current.TmdbId : provider == ProviderNames.Tvdb ? current.TvdbId : current.ImdbId;
                var after = Id(outcome, provider);
                if (string.Equals(before ?? string.Empty, after ?? string.Empty, StringComparison.OrdinalIgnoreCase)) continue;
                var name = provider.ToUpperInvariant();
                changes.Add(string.IsNullOrWhiteSpace(before) ? "Add " + name + " " + after : string.IsNullOrWhiteSpace(after) ? "Remove " + name + " " + before : name + " " + before + " → " + after);
            }
            return changes.Count == 0 ? "No ID changes" : string.Join("; ", changes);
        }
    }

    public sealed class ReviewIdentityRow
    {
        public string RowId { get; set; }
        public string OutcomeId { get; set; }
        public bool CorrectIdentity { get; set; }
        public string CurrentName { get; set; }
        public string CurrentEmby { get; set; }
        public string CurrentTmdb { get; set; }
        public string CurrentTvdb { get; set; }
        public string CurrentImdb { get; set; }
        public string ResultName { get; set; }
        public string ResultEmby { get; set; }
        public string ResultTmdb { get; set; }
        public string ResultTvdb { get; set; }
        public string ResultImdb { get; set; }
        public string IdChanges { get; set; }
        public string Outcome { get; set; }
        public ReviewMediaRow[] Media { get; set; } = new ReviewMediaRow[0];
    }

    public sealed class ReviewMediaRow
    {
        public string RowId { get; set; }
        public string AssignmentId { get; set; }
        public string Media { get; set; }
        public string Role { get; set; }
        public string Tmdb { get; set; }
        public string Tvdb { get; set; }
        public string Imdb { get; set; }
        public string Action { get; set; }
        public bool CorrectAssignment { get; set; }
        public bool CorrectRole { get; set; }
    }

    internal static class ReviewCaseCommands
    {
        public const string Back = "case-back-to-all";
        public const string Apply = "case-apply";
        public const string IdentityGrid = "case-correct-identity";
        public const string MediaGrid = "case-correct-media";
        public const string ProviderCorrection = "case-provider-correction:";
        public const string RecalculatePending = "case-recalculate-pending";
    }

    internal sealed class ReviewCaseDialogView : DialogViewBase
    {
        private readonly PluginInfo plugin;
        private readonly IServerApplicationHost host;
        private readonly IApplicationPaths paths;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private readonly IPluginUIView parent;
        private readonly Action rebuildParent;
        private string caseId;
        private readonly HashSet<long> originalEmbyIds = new HashSet<long>();
        private IdentityCasePlan plan;
        private List<ProviderCorrection> suggestedCorrections = new List<ProviderCorrection>();
        private string result;

        public ReviewCaseDialogView(PluginInfo plugin, IServerApplicationHost host, ILogger logger, IPluginUIView parent, Action rebuildParent, DashboardDecision reviewCase) : base(plugin.Id)
        {
            this.plugin = plugin; this.host = host; this.paths = host.Resolve<IApplicationPaths>(); this.json = host.Resolve<IJsonSerializer>(); this.logger = logger; this.parent = parent; this.rebuildParent = rebuildParent; this.caseId = reviewCase.CaseId;
            AllowCancel = true; AllowOk = false; Rebuild();
        }

        public override bool ShowDialogFullScreen => true;
        public override string Caption => "Review identity case";
        public override Task OnCancelCommand() => Task.CompletedTask;
        public override Task OnOkCommand(string providerId, string commandId, string data) => Task.CompletedTask;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (string.Equals(commandId, "DialogCancel", StringComparison.OrdinalIgnoreCase) || commandId == ReviewCaseCommands.Back) { rebuildParent(); return Task.FromResult(parent); }
            var applyCommitted = false;
            try
            {
                if (commandId == ReviewCaseCommands.IdentityGrid)
                {
                    var incoming = json.DeserializeFromString<ReviewCaseDialogUI>(data);
                    var selected = incoming?.Rows?.FirstOrDefault(x => x.CorrectIdentity);
                    if (selected != null) return Task.FromResult<IPluginUIView>(CorrectionChoiceDialogView.ForIdentity(plugin, host, logger, this, Rebuild, plan, selected.OutcomeId));
                }
                if (commandId == ReviewCaseCommands.MediaGrid)
                {
                    var incoming = json.DeserializeFromString<ReviewCaseDialogUI>(data);
                    var selected = incoming?.Rows?.SelectMany(x => x.Media ?? new ReviewMediaRow[0]).FirstOrDefault(x => x.CorrectAssignment || x.CorrectRole);
                    if (selected != null) return Task.FromResult<IPluginUIView>(CorrectionChoiceDialogView.ForMedia(plugin, host, logger, this, Rebuild, plan, selected.AssignmentId, selected.CorrectRole));
                }
                if ((commandId ?? string.Empty).StartsWith(ReviewCaseCommands.ProviderCorrection, StringComparison.Ordinal))
                {
                    int index;
                    if (!int.TryParse(commandId.Substring(ReviewCaseCommands.ProviderCorrection.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0 || index >= suggestedCorrections.Count)
                        throw new InvalidOperationException("The selected provider correction is no longer available.");
                    return Task.FromResult<IPluginUIView>(new CorrectionDialogView(plugin, host, logger, this, Rebuild, suggestedCorrections[index]));
                }
                if (commandId == ReviewCaseCommands.RecalculatePending)
                {
                    using (var repository = Open()) CorrectionRuntime.Recalculate(repository, logger);
                    result = "Saved corrections were recalculated together.";
                    Rebuild(); Refresh(); return Task.FromResult<IPluginUIView>(this);
                }
                if (commandId == ReviewCaseCommands.Apply)
                {
                    var reviewedHash = plan.PlanHash;
                    var fresh = LoadPlan();
                    if (fresh.PlanHash != reviewedHash) throw new InvalidOperationException("The persisted projection changed after it was displayed. Review the recalculated case before applying.");
                    if (fresh.State != IdentityPlanStates.Complete) throw new InvalidOperationException("This case still contains a genuine correction question.");
                    if (!IdentityCaseExecutor.HasMutations(fresh)) throw new InvalidOperationException("This case requires no Emby changes and therefore has nothing to apply.");
                    var library = host.Resolve<ILibraryManager>();
                    var beforeMetadata = IdentityApplyAudit.CaptureBefore(fresh, library);
                    var executor = new IdentityCaseExecutor(library);
                    IdentityCaseApplyReceipt receipt;
                    using (var repository = Open())
                    {
                        receipt = executor.Apply(fresh, committed => repository.CommitIdentityCase(fresh, committed));
                        applyCommitted = true;
                    }
                    IdentityApplyAudit.Log(fresh, receipt, beforeMetadata, library, logger);
                    logger.Info("PersonCleaner applied identity case {0}: {1} The cached whole-run graph was not rebuilt interactively; the next PersonCleaner evidence task will refresh related cases.", fresh.CaseId, receipt.Summary);
                    rebuildParent(); return Task.FromResult(parent);
                }
            }
            catch (Exception ex)
            {
                result = applyCommitted ? "Apply committed, but the follow-up workflow failed: " + ex.Message : ex.Message.IndexOf("rollback also failed", StringComparison.OrdinalIgnoreCase) >= 0 ? "Apply failed and Emby may contain partial changes: " + ex.Message : "Nothing was written: " + ex.Message;
                logger.ErrorException("Unable to process PersonCleaner identity case " + caseId, ex);
                Rebuild(); Refresh();
            }
            return Task.FromResult<IPluginUIView>(this);
        }

        public override void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data) { Rebuild(); Refresh(); }
        private ResolutionRepository Open() { var repository = new ResolutionRepository(paths); repository.Initialize(); return repository; }
        private IdentityCasePlan LoadPlan() { using (var repository = Open()) { var current = repository.IdentityCaseByReference(caseId, originalEmbyIds); caseId = current.CaseId; foreach (var id in current.CurrentPeople.Select(x => x.EmbyId)) originalEmbyIds.Add(id); return current; } }
        private void Rebuild()
        {
            try
            {
                using (var repository = Open())
                {
                    plan = repository.IdentityCaseByReference(caseId, originalEmbyIds); caseId = plan.CaseId; foreach (var id in plan.CurrentPeople.Select(x => x.EmbyId)) originalEmbyIds.Add(id);
                    suggestedCorrections = SuggestedCorrections(repository, plan);
                    var pendingCorrections = repository.PendingCorrectionSelections(caseId);
                    string serverId = null; try { serverId = host.GetPublicSystemInfo(CancellationToken.None).GetAwaiter().GetResult()?.Id; } catch { }
                    ContentData = ReviewCaseDialogUI.Build(plan, repository.LocalPeople(), serverId, result, suggestedCorrections, pendingCorrections);
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("Unable to rebuild the PersonCleaner identity case dialog", ex);
                plan = plan ?? new IdentityCasePlan { CaseId = caseId, DisplayName = "Selected identity case", CaseType = "Unavailable", Summary = "The case is no longer available.", State = IdentityPlanStates.Blocked };
                var people = plan.CurrentPeople.ToArray();
                string serverId = null; try { serverId = host.GetPublicSystemInfo(CancellationToken.None).GetAwaiter().GetResult()?.Id; } catch { }
                ContentData = ReviewCaseDialogUI.Build(plan, people, serverId, result ?? "The case could not be reloaded: " + ex.Message);
            }
        }

        private static List<ProviderCorrection> SuggestedCorrections(ResolutionRepository repository, IdentityCasePlan currentPlan)
        {
            return currentPlan.DecisionIds.Select(x => DecisionChangePlanner.Build(repository.DecisionChangeContext(x)).RecommendedCorrection).Where(x => x != null)
                .GroupBy(CorrectionKey, StringComparer.Ordinal).Select(x => x.First()).ToList();
        }

        private static string CorrectionKey(ProviderCorrection x)
        {
            return string.Join("|", new[] { x.Kind, x.Operation, x.Provider, x.MediaType, x.ProviderMediaId, x.ProviderPersonId, x.FieldName, x.CurrentValue, x.ReplacementValue, x.SecondaryProvider, x.SecondaryId, x.EmbyId?.ToString(CultureInfo.InvariantCulture) }.Select(y => y ?? string.Empty));
        }
    }

    internal static class CaseLinks
    {
        public static string Emby(long id, string serverId, string label) => Anchor(string.IsNullOrWhiteSpace(serverId) ? null : "#!/item?id=" + id + "&serverId=" + Uri.EscapeDataString(serverId), label);
        public static string Person(string provider, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "—";
            var url = provider == ProviderNames.Tmdb ? "https://www.themoviedb.org/person/" + Uri.EscapeDataString(id) : provider == ProviderNames.Tvdb ? "https://thetvdb.com/people/" + Uri.EscapeDataString(id) : "https://www.imdb.com/name/" + Uri.EscapeDataString(id) + "/";
            return Anchor(url, id);
        }
        public static string Media(string provider, string type, string id, string slug = null)
        {
            if (string.IsNullOrWhiteSpace(id)) return "—";
            var url = provider == ProviderNames.Tmdb ? "https://www.themoviedb.org/" + (type == MediaTypes.Series ? "tv/" : "movie/") + Uri.EscapeDataString(id) : provider == ProviderNames.Tvdb ? string.IsNullOrWhiteSpace(slug) ? "https://thetvdb.com/search?query=" + Uri.EscapeDataString(id) : "https://thetvdb.com/" + (type == MediaTypes.Series ? "series/" : "movies/") + Uri.EscapeDataString(slug) : "https://www.imdb.com/title/" + Uri.EscapeDataString(id) + "/";
            return Anchor(url, id);
        }
        private static string Anchor(string url, string label) { var safe = WebUtility.HtmlEncode(label ?? string.Empty); return string.IsNullOrWhiteSpace(url) ? safe : "<a href=\"" + WebUtility.HtmlEncode(url) + "\" target=\"_blank\" rel=\"noopener noreferrer\">" + safe + "</a>"; }
    }
}
