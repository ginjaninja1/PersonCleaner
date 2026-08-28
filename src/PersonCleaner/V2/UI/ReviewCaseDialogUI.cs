using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.DxGrid;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Serialization;
using PersonCleaner.V2.Domain;
using PersonCleaner.V2.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.V2.UI
{
    public sealed class ReviewCaseDialogUI : EditableObjectBase
    {
        public override string EditorTitle => null;

        public ButtonItem Apply { get; set; }
        public ButtonItem BackToAllCases { get; set; } = new ButtonItem("Back to all cases") { CommandId = ReviewCaseCommands.Back };
        public ButtonItem LastAction { get; set; } = new ButtonItem(string.Empty) { IsEnabled = false };
        [GridDataSource(nameof(Rows))]
        public DxDataGrid PersonBuilder { get; set; }
        public ReviewIdentityRow[] Rows { get; set; } = new ReviewIdentityRow[0];

        public static ReviewCaseDialogUI Build(IdentityCasePlan plan, PersonBuilderDraft draft, string serverId, string result)
        {
            draft = draft ?? PersonBuilderDraft.FromPlan(plan);
            var desiredPeople = draft.People.ToDictionary(x => x.OutcomeId, StringComparer.Ordinal);
            var desiredCredits = draft.Credits.ToDictionary(x => x.AssignmentId, StringComparer.Ordinal);
            var personOutcomes = plan.Outcomes.Where(x => desiredPeople[x.OutcomeId].Include).OrderBy(x => x.SortOrder).ThenBy(x => x.OutcomeId, StringComparer.Ordinal).ToList();
            var newOrdinals = personOutcomes.Where(x => desiredPeople[x.OutcomeId].TargetKind != IdentityTargetKinds.Existing)
                .Select((x, index) => new { x.OutcomeId, Ordinal = index + 1 }).ToDictionary(x => x.OutcomeId, x => x.Ordinal, StringComparer.Ordinal);
            var rows = personOutcomes
                .Select(x => BuildIdentity(plan, x, desiredPeople[x.OutcomeId], desiredCredits, serverId, NewOrdinal(newOrdinals, x.OutcomeId)))
                .Concat(InformationRows(plan, draft, result)).ToArray();
            var targets = personOutcomes.Select(x => new ReviewTargetChoice { Value = x.OutcomeId, Caption = TargetCaption(desiredPeople[x.OutcomeId], NewOrdinal(newOrdinals, x.OutcomeId)) }).ToArray();
            var personTargets = plan.CurrentPeople.OrderBy(x => x.EmbyId).Select(x => new ReviewTargetChoice { Value = "existing:" + x.EmbyId, Caption = "Maintain " + x.EmbyId })
                .Concat(new[] { new ReviewTargetChoice { Value = "new", Caption = "Create" }, new ReviewTargetChoice { Value = "remove", Caption = "Remove" } }).ToArray();
            var master = Grid(new ReviewIdentityRow(), nameof(ReviewIdentityRow.RowId), ReviewCaseCommands.IdentityGrid);
            master.heightMode = DxGridOptions.GridHeightMode.fullHeight;
            master.editing.allowAdding = true; master.editing.allowDeleting = true; master.editing.useIcons = true;
            var detail = Grid(new ReviewMediaRow(), nameof(ReviewMediaRow.RowId), ReviewCaseCommands.MediaGrid);
            detail.heightMode = DxGridOptions.GridHeightMode.auto;
            detail.scrolling = Scrolling();
            ConfigureIdentityColumns(master, personTargets);
            ConfigureMediaColumns(detail, targets);
            master.masterDetail = new DxGridMasterDetail { enabled = true, autoExpandAll = false, childRowsFieldName = nameof(ReviewIdentityRow.Media), detailGridOptions = detail };
            var ui = new ReviewCaseDialogUI { PersonBuilder = new DxDataGrid(master), Rows = rows, LastAction = new ButtonItem(result ?? string.Empty) { IsEnabled = false } };
            try
            {
                var compilation = IdentityCasePersonBuilder.Compile(plan, draft);
                var preview = compilation.Plan;
                if (plan.State != IdentityPlanStates.Applied && plan.State != IdentityPlanStates.Blocked && preview.State == IdentityPlanStates.Complete)
                {
                    var caption = IdentityCaseExecutor.HasMutations(preview) ? preview.ApplyCaption : "Apply: confirm layout";
                    var rules = compilation.Corrections.Count;
                    ui.Apply = new ButtonItem(caption)
                    {
                        CommandId = ReviewCaseCommands.Apply,
                        ConfirmationPrompt = "Apply exactly this person-ID and media-credit layout after re-reading live Emby? " + rules + " minimum correction rule(s) will be recorded only after the apply commits."
                    };
                }
            }
            catch { }
            return ui;
        }

        public static PersonBuilderDraft Capture(PersonBuilderDraft current, ReviewCaseDialogUI incoming, bool detectDeletedPeople)
        {
            var draft = new PersonBuilderDraft
            {
                RunId = current.RunId, CaseId = current.CaseId, ReviewedPlanHash = current.ReviewedPlanHash,
                People = current.People.Select(Clone).ToList(), Credits = current.Credits.Select(Clone).ToList()
            };
            var people = draft.People.ToDictionary(x => x.OutcomeId, StringComparer.Ordinal);
            var credits = draft.Credits.ToDictionary(x => x.AssignmentId, StringComparer.Ordinal);
            foreach (var row in incoming?.Rows ?? new ReviewIdentityRow[0])
            {
                if (people.TryGetValue(row.OutcomeId ?? string.Empty, out var person))
                {
                    if (person.TargetKind == IdentityTargetKinds.New && !string.IsNullOrWhiteSpace(row.Name)) person.DisplayName = row.Name.Trim();
                    person.TmdbId = row.TmdbId; person.TvdbId = row.TvdbId; person.ImdbId = row.ImdbId; person.PlannerNotes = row.PlannerNotes;
                    SetPersonTarget(person, row.PersonTarget);
                }
                foreach (var media in row.Media ?? new ReviewMediaRow[0])
                    if (credits.TryGetValue(media.AssignmentId ?? string.Empty, out var credit) && !string.IsNullOrWhiteSpace(media.TargetOutcomeId)) credit.TargetOutcomeId = media.TargetOutcomeId;
            }
            var assignedToRemoved = draft.People.FirstOrDefault(x => !x.Include && draft.Credits.Any(y => y.TargetOutcomeId == x.OutcomeId));
            if (assignedToRemoved != null) throw new InvalidOperationException("Move every media credit away from this person before removing its row.");
            if (detectDeletedPeople && incoming?.Rows != null)
            {
                var visible = new HashSet<string>(incoming.Rows.Select(x => x.OutcomeId).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.Ordinal);
                foreach (var person in draft.People.Where(x => !visible.Contains(x.OutcomeId)))
                {
                    if (person.TargetKind == IdentityTargetKinds.Existing) throw new InvalidOperationException("An existing Emby person row cannot be removed; move its media and clear or change its IDs instead.");
                    if (draft.Credits.Any(x => x.TargetOutcomeId == person.OutcomeId)) throw new InvalidOperationException("Move every media credit away from this suggested person before removing its row.");
                    person.Include = false;
                }
            }
            return draft;
        }

        public static string ExpandCreateSelections(IdentityCasePlan plan, PersonBuilderDraft draft, ReviewCaseDialogUI incoming)
        {
            if (plan == null || draft == null || incoming?.Rows == null) return null;
            var people = draft.People.ToDictionary(x => x.OutcomeId, StringComparer.Ordinal);
            var addedRows = new List<ReviewIdentityRow>();
            string result = null;
            foreach (var row in incoming.Rows.Where(x => !x.IsInformation && !string.IsNullOrWhiteSpace(x.OutcomeId)))
            {
                if (!people.TryGetValue(row.OutcomeId, out var current) || current.TargetKind != IdentityTargetKinds.Existing || !current.TargetEmbyId.HasValue || !string.Equals(row.PersonTarget, "new", StringComparison.Ordinal)) continue;
                row.PersonTarget = "existing:" + current.TargetEmbyId.Value;
                var id = "builder:new:" + Guid.NewGuid().ToString("N");
                var displayName = string.IsNullOrWhiteSpace(current.DisplayName) ? plan.DisplayName : current.DisplayName;
                plan.Outcomes.Add(new IdentityOutcome
                {
                    OutcomeId = id, SortOrder = plan.Outcomes.Count, TargetKind = IdentityTargetKinds.New,
                    DisplayName = displayName, Outcome = "Operator-added Emby person"
                });
                draft.People.Add(new PersonBuilderIdentity
                {
                    OutcomeId = id, Include = true, DisplayName = displayName, TargetKind = IdentityTargetKinds.New,
                    TmdbId = string.Empty, TvdbId = string.Empty, ImdbId = string.Empty, PlannerNotes = string.Empty
                });
                addedRows.Add(new ReviewIdentityRow
                {
                    RowId = id, OutcomeId = id, PersonTarget = "new", Name = displayName,
                    TmdbId = string.Empty, TvdbId = string.Empty, ImdbId = string.Empty, PlannerNotes = string.Empty
                });
                var ordinal = draft.People.Count(x => x.Include && x.TargetKind != IdentityTargetKinds.Existing);
                result = "New " + ordinal + " Created - Allocate IDs and Associate Media";
            }
            if (addedRows.Count > 0) incoming.Rows = incoming.Rows.Concat(addedRows).ToArray();
            return result;
        }

        private static PersonBuilderIdentity Clone(PersonBuilderIdentity x) => new PersonBuilderIdentity { OutcomeId = x.OutcomeId, Include = x.Include, DisplayName = x.DisplayName, TargetKind = x.TargetKind, TargetEmbyId = x.TargetEmbyId, TmdbId = x.TmdbId, TvdbId = x.TvdbId, ImdbId = x.ImdbId, PlannerNotes = x.PlannerNotes };
        private static PersonBuilderCredit Clone(PersonBuilderCredit x) => new PersonBuilderCredit { AssignmentId = x.AssignmentId, TargetOutcomeId = x.TargetOutcomeId };
        private static void SetPersonTarget(PersonBuilderIdentity person, string value)
        {
            if (string.Equals(value, "remove", StringComparison.Ordinal))
            {
                if (person.TargetKind == IdentityTargetKinds.Existing) throw new InvalidOperationException("Only an unassigned new-person row can be removed.");
                person.Include = false; return;
            }
            person.Include = true;
            if (string.Equals(value, "new", StringComparison.Ordinal)) { person.TargetKind = IdentityTargetKinds.New; person.TargetEmbyId = null; return; }
            if ((value ?? string.Empty).StartsWith("existing:", StringComparison.Ordinal) && long.TryParse(value.Substring(9), out var id)) { person.TargetKind = IdentityTargetKinds.Existing; person.TargetEmbyId = id; return; }
            person.TargetKind = null; person.TargetEmbyId = null;
        }

        private static DxGridOptions Grid(object row, string key, string command)
        {
            return new DxGridOptions(row, key, false, true, true, true)
            {
                heightMode = DxGridOptions.GridHeightMode.large, columnAutoWidth = false, allowColumnReordering = true, allowColumnResizing = true,
                showBorders = true, showRowLines = true, rowAlternationEnabled = true, wordWrapEnabled = true, cellHintEnabled = true,
                paging = new DxGridPaging { enabled = false }, editing = new DxGridEditing { mode = DxGridEditing.GridEditMode.cell, allowUpdating = true },
                onChangeCommand = new DxGridOnChangeCommand { commandId = command }, scrolling = Scrolling()
            };
        }

        private static DxGridScrolling Scrolling() => new DxGridScrolling { mode = DxGridScrolling.ScrollingMode.standard, rowRenderingMode = DxGridScrolling.RowRenderingMode.standard, showScrollbar = DxGridScrolling.ShowScrollbarMode.always, scrollByContent = true, scrollByThumb = true, useNative = "false" };

        private static void ConfigureIdentityColumns(DxGridOptions grid, ReviewTargetChoice[] targets)
        {
            foreach (var c in grid.columns)
            {
                c.allowEditing = false; c.allowGrouping = false; c.allowHeaderFiltering = false;
                if (c.dataField == nameof(ReviewIdentityRow.RowId) || c.dataField == nameof(ReviewIdentityRow.OutcomeId) || c.dataField == nameof(ReviewIdentityRow.IsInformation) || c.dataField == nameof(ReviewIdentityRow.Media)) c.visible = false;
                if (c.dataField == nameof(ReviewIdentityRow.Media)) c.isSecondaryGridDataSource = true;
                if (c.dataField == nameof(ReviewIdentityRow.PersonTarget))
                {
                    c.caption = "Desired Emby person"; c.width = 245; c.allowEditing = true; c.showEditorAlways = true;
                    c.lookup = new DxGridLookup { dataSource = targets, valueExpr = nameof(ReviewTargetChoice.Value), displayExpr = nameof(ReviewTargetChoice.Caption), allowClearing = false };
                }
                if (c.dataField == nameof(ReviewIdentityRow.Name)) { c.caption = "Name"; c.width = 155; c.encodeHtml = false; c.allowEditing = true; }
                if (c.dataField == nameof(ReviewIdentityRow.CurrentIds)) { c.caption = "Current IDs"; c.width = 230; c.encodeHtml = false; }
                if (c.dataField == nameof(ReviewIdentityRow.TmdbId)) { c.caption = "Desired TMDB"; c.width = 120; c.allowEditing = true; }
                if (c.dataField == nameof(ReviewIdentityRow.TvdbId)) { c.caption = "Desired TVDB"; c.width = 120; c.allowEditing = true; }
                if (c.dataField == nameof(ReviewIdentityRow.ImdbId)) { c.caption = "Desired IMDb"; c.width = 130; c.allowEditing = true; }
                if (c.dataField == nameof(ReviewIdentityRow.PlannerNotes)) { c.caption = "Planner notes"; c.width = 240; c.allowEditing = true; }
                if (c.dataField == nameof(ReviewIdentityRow.ChangeSummary)) { c.caption = "ID result"; c.width = 260; }
                if (c.dataField == nameof(ReviewIdentityRow.Status)) { c.caption = "Result"; c.width = 215; }
            }
        }

        private static void ConfigureMediaColumns(DxGridOptions grid, ReviewTargetChoice[] targets)
        {
            foreach (var c in grid.columns)
            {
                c.allowEditing = false; c.allowGrouping = false; c.allowHeaderFiltering = false;
                if (c.dataField == nameof(ReviewMediaRow.RowId) || c.dataField == nameof(ReviewMediaRow.AssignmentId)) c.visible = false;
                if (c.dataField == nameof(ReviewMediaRow.Media)) { c.width = 260; c.encodeHtml = false; }
                if (c.dataField == nameof(ReviewMediaRow.Role)) c.width = 210;
                if (c.dataField == nameof(ReviewMediaRow.CurrentPerson)) { c.caption = "Current Emby person"; c.width = 190; c.encodeHtml = false; }
                if (c.dataField == nameof(ReviewMediaRow.TargetOutcomeId))
                {
                    c.caption = "Desired Emby person"; c.width = 240; c.allowEditing = true; c.showEditorAlways = true;
                    c.lookup = new DxGridLookup { dataSource = targets, valueExpr = nameof(ReviewTargetChoice.Value), displayExpr = nameof(ReviewTargetChoice.Caption), allowClearing = false };
                }
                if (c.dataField == nameof(ReviewMediaRow.TmdbOwner)) { c.caption = "TMDB evidence"; c.width = 230; c.encodeHtml = false; }
                if (c.dataField == nameof(ReviewMediaRow.TvdbOwner)) { c.caption = "TVDB evidence"; c.width = 230; c.encodeHtml = false; }
                if (c.dataField == nameof(ReviewMediaRow.Attribution)) { c.caption = "Evidence assessment"; c.width = 190; }
            }
        }

        private static ReviewIdentityRow BuildIdentity(IdentityCasePlan plan, IdentityOutcome outcome, PersonBuilderIdentity desired, IDictionary<string, PersonBuilderCredit> credits, string serverId, int newOrdinal)
        {
            LocalPerson current = null;
            if (desired.TargetKind == IdentityTargetKinds.Existing && desired.TargetEmbyId.HasValue) current = plan.CurrentPeople.FirstOrDefault(x => x.EmbyId == desired.TargetEmbyId.Value);
            if (current == null) current = outcome.SourceEmbyIds.Select(id => plan.CurrentPeople.FirstOrDefault(x => x.EmbyId == id)).FirstOrDefault(x => x != null);
            var row = new ReviewIdentityRow
            {
                RowId = outcome.OutcomeId, OutcomeId = outcome.OutcomeId,
                PersonTarget = desired.TargetKind == IdentityTargetKinds.Existing && desired.TargetEmbyId.HasValue ? "existing:" + desired.TargetEmbyId : desired.TargetKind == IdentityTargetKinds.New ? "new" : null,
                Name = desired.TargetKind == IdentityTargetKinds.Existing && desired.TargetEmbyId.HasValue
                    ? CaseLinks.Emby(desired.TargetEmbyId.Value, serverId, current?.Name ?? desired.DisplayName)
                    : desired.DisplayName,
                CurrentIds = desired.TargetKind == IdentityTargetKinds.New ? "New " + newOrdinal : current == null ? "Suggested person" : CurrentIds(current),
                TmdbId = desired.TmdbId, TvdbId = desired.TvdbId, ImdbId = desired.ImdbId,
                PlannerNotes = desired.PlannerNotes ?? string.Empty,
                ChangeSummary = Changes(desired.TargetKind == IdentityTargetKinds.New ? null : current, desired),
                Status = desired.TargetKind == IdentityTargetKinds.Existing ? "Maintain existing person" : desired.TargetKind == IdentityTargetKinds.New ? credits.Values.Any(x => x.TargetOutcomeId == outcome.OutcomeId) ? "Create suggested person" : outcome.Outcome == "Operator-added Emby person" ? "Allocate IDs and associate media" : "Do not create — no assigned media" : "Choose maintain or create"
            };
            row.Media = plan.Credits.Where(x => credits[x.AssignmentId].TargetOutcomeId == outcome.OutcomeId)
                .OrderBy(x => x.MediaName, StringComparer.Ordinal).ThenBy(x => x.MediaEmbyId).ThenBy(x => x.Role, StringComparer.Ordinal)
                .Select(x => MediaRow(plan, x, credits[x.AssignmentId], serverId)).ToArray();
            return row;
        }

        private static ReviewMediaRow MediaRow(IdentityCasePlan plan, IdentityCreditOutcome credit, PersonBuilderCredit desired, string serverId)
        {
            var source = plan.CurrentPeople.FirstOrDefault(x => x.EmbyId == credit.SourcePersonEmbyId);
            return new ReviewMediaRow
            {
                RowId = credit.AssignmentId, AssignmentId = credit.AssignmentId,
                Media = MediaLink(credit, serverId), Role = DisplayRole(credit.Role),
                CurrentPerson = source == null ? credit.SourcePersonEmbyId.ToString() : CaseLinks.Emby(source.EmbyId, serverId, source.EmbyId.ToString()),
                TargetOutcomeId = desired.TargetOutcomeId,
                TmdbOwner = ProviderOwners(credit, ProviderNames.Tmdb), TvdbOwner = ProviderOwners(credit, ProviderNames.Tvdb), Attribution = AttributionVerdict(credit)
            };
        }

        private static string MediaLink(IdentityCreditOutcome credit, string serverId)
        {
            var media = CaseLinks.Emby(credit.MediaEmbyId, serverId, credit.MediaName);
            if (!string.Equals(credit.MediaType, "episode", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(credit.SeriesName)) return media;
            var series = credit.SeriesEmbyId.HasValue && credit.SeriesEmbyId.Value > 0
                ? CaseLinks.Emby(credit.SeriesEmbyId.Value, serverId, credit.SeriesName)
                : WebUtility.HtmlEncode(credit.SeriesName);
            return media + " - " + series;
        }

        private static string CurrentIds(LocalPerson person)
        {
            var values = new[] { Tuple.Create(ProviderNames.Tmdb, person.TmdbId), Tuple.Create(ProviderNames.Tvdb, person.TvdbId), Tuple.Create(ProviderNames.Imdb, person.ImdbId) }
                .Where(x => !string.IsNullOrWhiteSpace(x.Item2)).Select(x => x.Item1.ToUpperInvariant() + " " + CaseLinks.Person(x.Item1, x.Item2));
            return string.Join("<br/>", values.DefaultIfEmpty("No provider IDs"));
        }

        private static string ProviderOwners(IdentityCreditOutcome credit, string provider)
        {
            var ids = credit.Attributions.Where(x => x.Provider == provider).Select(x => x.ProviderPersonId).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            return ids.Count == 0 ? "—" : string.Join("<br/>", ids.Select(x => CaseLinks.Person(provider, x)));
        }

        private static string DisplayRole(string role)
        {
            var value = role ?? string.Empty;
            foreach (var prefix in new[] { "Actor:", "Director:" })
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return value.Substring(prefix.Length).TrimStart();
            return value;
        }

        private static string AttributionVerdict(IdentityCreditOutcome credit)
        {
            if (credit.IsReviewSupplemental) return "Outside evidence scope";
            var rows = credit.Attributions ?? new List<IdentityCreditAttribution>();
            if (rows.Count == 0) return "No provider assertion";
            if (rows.GroupBy(x => x.Provider, StringComparer.Ordinal).Any(x => x.Select(y => y.OutcomeId).Distinct(StringComparer.Ordinal).Count() > 1)) return "Competing owners within one provider";
            if (rows.Select(x => x.OutcomeId).Distinct(StringComparer.Ordinal).Count() > 1) return "Providers disagree";
            return rows.Select(x => x.Provider).Distinct(StringComparer.Ordinal).Count() > 1 ? "Providers agree" : "One-provider support";
        }

        private static string Changes(LocalPerson current, PersonBuilderIdentity desired)
        {
            if (current == null) return "Set IDs on creation";
            var changes = new List<string>();
            Change(changes, "TMDB", current.TmdbId, desired.TmdbId); Change(changes, "TVDB", current.TvdbId, desired.TvdbId); Change(changes, "IMDb", current.ImdbId, desired.ImdbId);
            return changes.Count == 0 ? "Keep all IDs" : string.Join("; ", changes);
        }

        private static void Change(List<string> changes, string provider, string before, string after)
        {
            if (string.Equals(before ?? string.Empty, after ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return;
            changes.Add(string.IsNullOrWhiteSpace(after) ? "Remove " + provider + " " + before : string.IsNullOrWhiteSpace(before) ? "Add " + provider + " " + after : provider + " " + before + " → " + after);
        }

        private static IEnumerable<ReviewIdentityRow> InformationRows(IdentityCasePlan plan, PersonBuilderDraft draft, string result)
        {
            var ids = plan.Outcomes.SelectMany(x => x.ProviderIds.Select(y => new { y.Provider, Id = y.ProviderId }))
                .Concat(draft.People.Where(x => x.Include).SelectMany(x => new[]
                {
                    new { Provider = ProviderNames.Tmdb, Id = x.TmdbId },
                    new { Provider = ProviderNames.Tvdb, Id = x.TvdbId },
                    new { Provider = ProviderNames.Imdb, Id = x.ImdbId }
                }))
                .Where(x => !string.IsNullOrWhiteSpace(x.Id)).GroupBy(x => x.Provider + ":" + x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.First())
                .OrderBy(x => x.Provider, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
            if (ids.Count == 0) yield break;
            yield return new ReviewIdentityRow
            {
                RowId = "information", IsInformation = true, Name = "Information",
                Status = "Participating provider IDs",
                Media = ids.Select(x => new ReviewMediaRow
                {
                    RowId = "information:id:" + x.Provider + ":" + x.Id,
                    Media = WebUtility.HtmlEncode(x.Provider.ToUpperInvariant()) + " — " + CaseLinks.Person(x.Provider, x.Id)
                }).Concat(new[] { new ReviewMediaRow { RowId = "information:spacer", Media = "spacer" } }).ToArray()
            };
        }

        private static int NewOrdinal(IDictionary<string, int> ordinals, string outcomeId) => ordinals.TryGetValue(outcomeId, out var ordinal) ? ordinal : 0;
        private static string TargetCaption(PersonBuilderIdentity desired, int newOrdinal) => desired.TargetKind == IdentityTargetKinds.Existing ? desired.TargetEmbyId.ToString() : "New " + newOrdinal;
    }

    public sealed class ReviewIdentityRow
    {
        public string RowId { get; set; }
        public string OutcomeId { get; set; }
        public bool IsInformation { get; set; }
        public string PersonTarget { get; set; }
        public string Name { get; set; }
        public string CurrentIds { get; set; }
        public string TmdbId { get; set; }
        public string TvdbId { get; set; }
        public string ImdbId { get; set; }
        public string PlannerNotes { get; set; }
        public string ChangeSummary { get; set; }
        public string Status { get; set; }
        public ReviewMediaRow[] Media { get; set; } = new ReviewMediaRow[0];
    }

    public sealed class ReviewMediaRow
    {
        public string RowId { get; set; }
        public string AssignmentId { get; set; }
        public string Media { get; set; }
        public string Role { get; set; }
        public string CurrentPerson { get; set; }
        public string TargetOutcomeId { get; set; }
        public string TmdbOwner { get; set; }
        public string TvdbOwner { get; set; }
        public string Attribution { get; set; }
    }

    public sealed class ReviewTargetChoice
    {
        public string Value { get; set; }
        public string Caption { get; set; }
    }

    internal static class ReviewCaseCommands
    {
        public const string Back = "case-back-to-all";
        public const string Apply = "case-apply";
        public const string IdentityGrid = "case-person-builder-identities";
        public const string MediaGrid = "case-person-builder-media";
    }

    internal sealed class ReviewCaseDialogView : DialogViewBase
    {
        private readonly IServerApplicationHost host;
        private readonly IApplicationPaths paths;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private readonly IPluginUIView parent;
        private readonly Action rebuildParent;
        private string caseId;
        private readonly HashSet<long> originalEmbyIds = new HashSet<long>();
        private IdentityCasePlan plan;
        private PersonBuilderDraft draft;
        private List<ReviewLiveCredit> liveReviewCredits = new List<ReviewLiveCredit>();
        private bool populateReviewMedia;
        private string result;

        public ReviewCaseDialogView(PluginInfo plugin, IServerApplicationHost host, ILogger logger, IPluginUIView parent, Action rebuildParent, DashboardDecision reviewCase) : base(plugin.Id)
        {
            this.host = host; this.paths = host.Resolve<IApplicationPaths>(); this.json = host.Resolve<IJsonSerializer>(); this.logger = logger; this.parent = parent; this.rebuildParent = rebuildParent; caseId = reviewCase.CaseId;
            AllowCancel = true; AllowOk = false; Rebuild(true);
        }

        public override bool ShowDialogFullScreen => true;
        public override string Caption => "Build Emby people";
        public override Task OnCancelCommand() => Task.CompletedTask;
        public override Task OnOkCommand(string providerId, string commandId, string data) => Task.CompletedTask;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (string.Equals(commandId, "DialogCancel", StringComparison.OrdinalIgnoreCase) || commandId == ReviewCaseCommands.Back) { rebuildParent(); return Task.FromResult(parent); }
            var applyCommitted = false;
            PendingReviewAction pendingAction = null;
            try
            {
                if (commandId == ReviewCaseCommands.IdentityGrid || commandId == ReviewCaseCommands.MediaGrid)
                {
                    var incoming = json.DeserializeFromString<ReviewCaseDialogUI>(data);
                    pendingAction = DescribeAction(incoming);
                    if (commandId == ReviewCaseCommands.IdentityGrid)
                    {
                        var createResult = ReviewCaseDialogUI.ExpandCreateSelections(plan, draft, incoming);
                        var addedResult = PrepareAddedPeople(incoming);
                        if (!string.IsNullOrWhiteSpace(createResult)) pendingAction = new PendingReviewAction { Success = createResult };
                        else if (!string.IsNullOrWhiteSpace(addedResult)) pendingAction = new PendingReviewAction { Success = addedResult };
                    }
                    draft = ReviewCaseDialogUI.Capture(draft, incoming, commandId == ReviewCaseCommands.IdentityGrid);
                    IdentityCasePersonBuilder.Compile(plan, draft);
                    if (!string.IsNullOrWhiteSpace(pendingAction?.Success)) result = pendingAction.Success;
                    Render(); Refresh(); return Task.FromResult<IPluginUIView>(this);
                }
                if (commandId == ReviewCaseCommands.Apply)
                {
                    if (!string.IsNullOrWhiteSpace(data)) draft = ReviewCaseDialogUI.Capture(draft, json.DeserializeFromString<ReviewCaseDialogUI>(data), false);
                    var compilation = IdentityCasePersonBuilder.Compile(plan, draft);
                    var fresh = LoadPlan();
                    if (fresh.PlanHash != compilation.ReviewedPlanHash) throw new InvalidOperationException("The evidence changed after this layout was displayed. Review the latest case before applying.");
                    if (fresh.State == IdentityPlanStates.Applied) throw new InvalidOperationException("This exact case has already been applied.");
                    if (fresh.State == IdentityPlanStates.Blocked) throw new InvalidOperationException("This case is blocked because the evaluated scope is incomplete.");
                    var appliedPlan = compilation.Plan;
                    var library = host.Resolve<ILibraryManager>();
                    var beforeMetadata = IdentityApplyAudit.CaptureBefore(appliedPlan, library);
                    var executor = new IdentityCaseExecutor(library);
                    IdentityCaseApplyReceipt receipt;
                    using (var repository = Open()) { receipt = executor.Apply(appliedPlan, committed => repository.CommitIdentityCase(compilation, committed)); applyCommitted = true; }
                    IdentityApplyAudit.Log(appliedPlan, receipt, beforeMetadata, library, logger);
                    logger.Info("PersonCleaner applied person-builder case {0}: {1}", appliedPlan.CaseId, receipt.Summary);
                    rebuildParent(); return Task.FromResult(parent);
                }
            }
            catch (Exception ex)
            {
                result = !applyCommitted && !string.IsNullOrWhiteSpace(pendingAction?.Failure)
                    ? pendingAction.Failure
                    : applyCommitted ? "Apply committed, but the follow-up workflow failed: " + ex.Message : ex.Message.IndexOf("rollback also failed", StringComparison.OrdinalIgnoreCase) >= 0 ? "Apply failed and Emby may contain partial changes: " + ex.Message : "Nothing was written: " + ex.Message;
                logger.ErrorException("Unable to process PersonCleaner person-builder case " + caseId, ex);
                Render(); Refresh();
            }
            return Task.FromResult<IPluginUIView>(this);
        }

        public override void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data) { Rebuild(false); Refresh(); }
        private ResolutionRepository Open() { var repository = new ResolutionRepository(paths); repository.Initialize(); return repository; }
        private IdentityCasePlan LoadPlan() { using (var repository = Open()) { var current = repository.IdentityCaseByReference(caseId, originalEmbyIds); caseId = current.CaseId; foreach (var id in current.CurrentPeople.Select(x => x.EmbyId)) originalEmbyIds.Add(id); return current; } }

        private void Rebuild(bool resetDraft)
        {
            try
            {
                var loaded = LoadPlan();
                var resetReviewSnapshot = resetDraft || draft == null || !string.Equals(draft.ReviewedPlanHash, loaded.PlanHash, StringComparison.Ordinal);
                if (resetReviewSnapshot)
                {
                    populateReviewMedia = Plugin.Instance.Configuration.PopulateCaseReviewWithOutOfScopeMediaItems;
                    liveReviewCredits = new List<ReviewLiveCredit>();
                    if (populateReviewMedia)
                    {
                        try { liveReviewCredits = ReadLiveReviewCredits(loaded.CurrentPeople); }
                        catch (Exception ex)
                        {
                            logger.ErrorException("Unable to populate out-of-scope media for PersonCleaner case " + loaded.CaseId, ex);
                            result = "The case evidence loaded, but out-of-scope Emby media could not be populated: " + ex.Message;
                        }
                    }
                }
                plan = loaded;
                try { PopulateEpisodeContext(plan.Credits); }
                catch (Exception ex) { logger.Warn("Unable to load episode-series labels for PersonCleaner case {0}: {1}", plan.CaseId, ex.Message); }
                if (populateReviewMedia)
                {
                    var supplemental = ReviewCaseCreditInventory.Missing(plan, liveReviewCredits);
                    plan.Credits.AddRange(supplemental);
                    logger.Info("PersonCleaner case {0} review inventory: read {1} live Emby relationship(s), added {2} relationship(s) outside evidence scope.", plan.CaseId, liveReviewCredits.Count, supplemental.Count);
                }
                if (resetReviewSnapshot) draft = PersonBuilderDraft.FromPlan(plan);
                Render();
            }
            catch (Exception ex)
            {
                logger.ErrorException("Unable to rebuild the PersonCleaner person-builder dialog", ex);
                plan = plan ?? new IdentityCasePlan { CaseId = caseId, DisplayName = "Selected identity case", CaseType = "Unavailable", Summary = "The case is no longer available.", State = IdentityPlanStates.Blocked };
                draft = draft ?? PersonBuilderDraft.FromPlan(plan);
                result = result ?? "The case could not be reloaded: " + ex.Message; Render();
            }
        }

        private List<ReviewLiveCredit> ReadLiveReviewCredits(IEnumerable<LocalPerson> currentPeople)
        {
            var personIds = currentPeople.Select(x => x.EmbyId).Where(x => x > 0).Distinct().ToArray();
            if (personIds.Length == 0) return new List<ReviewLiveCredit>();
            var library = host.Resolve<ILibraryManager>();
            var media = new Dictionary<long, BaseItem>();
            for (var offset = 0; offset < personIds.Length; offset += 250)
            {
                var rows = library.GetItemList(new InternalItemsQuery { Recursive = true, PersonIds = personIds.Skip(offset).Take(250).ToArray() }, CancellationToken.None);
                foreach (var item in rows.Where(x => x != null && !(x is Person) && x.InternalId > 0)) media[item.InternalId] = item;
            }
            var involved = new HashSet<long>(personIds);
            var result = new List<ReviewLiveCredit>();
            var mediaIds = media.Keys.OrderBy(x => x).ToArray();
            for (var offset = 0; offset < mediaIds.Length; offset += 250)
            {
                var rows = library.GetItemPeople(new InternalPeopleQuery { ItemIds = mediaIds.Skip(offset).Take(250).ToArray(), EnableIds = true, EnableProviderIds = true, EnableGroupByName = false });
                foreach (var row in rows.Where(x => x.Id > 0 && involved.Contains(x.Id)))
                {
                    if (!media.TryGetValue(row.ItemId, out var item)) continue;
                    result.Add(new ReviewLiveCredit
                    {
                        PersonEmbyId = row.Id, MediaEmbyId = row.ItemId, MediaType = item.GetType().Name.ToLowerInvariant(), MediaName = item.Name,
                        SeriesEmbyId = item is Episode episode ? EpisodeSeriesId(episode) : null,
                        SeriesName = item is Episode namedEpisode ? namedEpisode.SeriesName ?? namedEpisode.Series?.Name : null,
                        Role = row.Type + (string.IsNullOrWhiteSpace(row.Role) ? string.Empty : ": " + row.Role)
                    });
                }
            }
            return result;
        }

        private void PopulateEpisodeContext(IEnumerable<IdentityCreditOutcome> credits)
        {
            var episodes = (credits ?? Enumerable.Empty<IdentityCreditOutcome>()).Where(x => string.Equals(x.MediaType, "episode", StringComparison.OrdinalIgnoreCase) && x.MediaEmbyId > 0).ToList();
            var ids = episodes.Select(x => x.MediaEmbyId).Distinct().ToArray();
            if (ids.Length == 0) return;
            var library = host.Resolve<ILibraryManager>();
            var items = new Dictionary<long, Episode>();
            for (var offset = 0; offset < ids.Length; offset += 250)
                foreach (var episode in library.GetItemList(new InternalItemsQuery { ItemIds = ids.Skip(offset).Take(250).ToArray() }, CancellationToken.None).OfType<Episode>())
                    items[episode.InternalId] = episode;
            foreach (var credit in episodes)
                if (items.TryGetValue(credit.MediaEmbyId, out var episode))
                {
                    credit.SeriesEmbyId = EpisodeSeriesId(episode);
                    credit.SeriesName = episode.SeriesName ?? episode.Series?.Name;
                }
        }

        private void Render()
        {
            string serverId = null; try { serverId = host.GetPublicSystemInfo(CancellationToken.None).GetAwaiter().GetResult()?.Id; } catch { }
            ContentData = ReviewCaseDialogUI.Build(plan, draft, serverId, result);
        }

        private string PrepareAddedPeople(ReviewCaseDialogUI incoming)
        {
            string result = null;
            foreach (var row in incoming?.Rows?.Where(x => !x.IsInformation && string.IsNullOrWhiteSpace(x.OutcomeId)).ToList() ?? new List<ReviewIdentityRow>())
            {
                var id = "builder:new:" + Guid.NewGuid().ToString("N");
                row.RowId = id; row.OutcomeId = id;
                var name = string.IsNullOrWhiteSpace(row.Name) ? plan.DisplayName : row.Name.Trim();
                plan.Outcomes.Add(new IdentityOutcome { OutcomeId = id, SortOrder = plan.Outcomes.Count, TargetKind = IdentityTargetKinds.New, DisplayName = name, Outcome = "Operator-added Emby person" });
                draft.People.Add(new PersonBuilderIdentity { OutcomeId = id, Include = true, DisplayName = name, TargetKind = IdentityTargetKinds.New, TmdbId = row.TmdbId, TvdbId = row.TvdbId, ImdbId = row.ImdbId, PlannerNotes = row.PlannerNotes });
                row.PersonTarget = "new"; row.Name = name;
                var ordinal = draft.People.Count(x => x.Include && x.TargetKind != IdentityTargetKinds.Existing);
                result = "New " + ordinal + " Created - Allocate IDs and Associate Media";
            }
            return result;
        }

        private PendingReviewAction DescribeAction(ReviewCaseDialogUI incoming)
        {
            if (incoming?.Rows == null) return null;
            var rows = incoming.Rows.Where(x => !x.IsInformation && !string.IsNullOrWhiteSpace(x.OutcomeId)).ToDictionary(x => x.OutcomeId, StringComparer.Ordinal);
            foreach (var person in draft.People.Where(x => x.Include))
            {
                if (!rows.TryGetValue(person.OutcomeId, out var row) || string.Equals(row.PersonTarget, "remove", StringComparison.Ordinal))
                {
                    if (person.TargetKind != IdentityTargetKinds.Existing)
                    {
                        var label = PersonLabel(person);
                        return new PendingReviewAction { Success = label + " removed", Failure = label + " not removed - disassociate media first" };
                    }
                    continue;
                }
                var provider = !Same(person.TmdbId, row.TmdbId) ? "TMDB" : !Same(person.TvdbId, row.TvdbId) ? "TVDB" : !Same(person.ImdbId, row.ImdbId) ? "IMDb" : null;
                if (provider != null) return new PendingReviewAction { Success = PersonLabel(person) + " - " + provider + "ID Changed" };
                if (person.TargetKind == IdentityTargetKinds.New && !string.IsNullOrWhiteSpace(row.Name) && !string.Equals(person.DisplayName ?? string.Empty, row.Name.Trim(), StringComparison.Ordinal))
                    return new PendingReviewAction { Success = PersonLabel(person) + " - Name Changed to " + row.Name.Trim() };
                if (!string.Equals(person.PlannerNotes ?? string.Empty, row.PlannerNotes ?? string.Empty, StringComparison.Ordinal))
                    return new PendingReviewAction { Success = PersonLabel(person) + " - Planner Note Updated" };
                if (!string.Equals(TargetValue(person), row.PersonTarget ?? string.Empty, StringComparison.Ordinal) && (row.PersonTarget ?? string.Empty).StartsWith("existing:", StringComparison.Ordinal))
                {
                    long id;
                    var label = long.TryParse(row.PersonTarget.Substring(9), out id) ? plan.CurrentPeople.FirstOrDefault(x => x.EmbyId == id)?.Name : null;
                    return new PendingReviewAction { Success = (label ?? "Emby person") + " selected as Desired Emby Person" };
                }
            }
            var media = incoming.Rows.SelectMany(x => x.Media ?? new ReviewMediaRow[0]).Where(x => !string.IsNullOrWhiteSpace(x.AssignmentId)).ToDictionary(x => x.AssignmentId, StringComparer.Ordinal);
            foreach (var credit in draft.Credits)
                if (media.TryGetValue(credit.AssignmentId, out var row) && !string.Equals(credit.TargetOutcomeId, row.TargetOutcomeId, StringComparison.Ordinal))
                {
                    var source = plan.Credits.FirstOrDefault(x => x.AssignmentId == credit.AssignmentId);
                    var target = draft.People.FirstOrDefault(x => x.OutcomeId == row.TargetOutcomeId);
                    return new PendingReviewAction { Success = (source?.MediaName ?? "Media") + " Association to " + PersonLabel(target) };
                }
            return null;
        }

        private string PersonLabel(PersonBuilderIdentity person)
        {
            if (person == null) return "person";
            if (person.TargetKind == IdentityTargetKinds.Existing)
                return plan.CurrentPeople.FirstOrDefault(x => x.EmbyId == person.TargetEmbyId)?.Name ?? person.DisplayName ?? "Emby person";
            var activeNew = draft.People.Where(x => x.Include && x.TargetKind != IdentityTargetKinds.Existing).ToList();
            var ordinal = activeNew.FindIndex(x => x.OutcomeId == person.OutcomeId) + 1;
            return ordinal > 0 ? "New " + ordinal : person.DisplayName ?? "new person";
        }

        private static bool Same(string a, string b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        private static string TargetValue(PersonBuilderIdentity person) => person.TargetKind == IdentityTargetKinds.Existing && person.TargetEmbyId.HasValue ? "existing:" + person.TargetEmbyId.Value : person.TargetKind == IdentityTargetKinds.New ? "new" : string.Empty;
        private static long? EpisodeSeriesId(Episode episode) => episode == null ? null : episode.SeriesId > 0 ? (long?)episode.SeriesId : episode.Series != null && episode.Series.InternalId > 0 ? (long?)episode.Series.InternalId : null;
        private sealed class PendingReviewAction { public string Success { get; set; } public string Failure { get; set; } }
    }

    internal static class CaseLinks
    {
        public static string Emby(long id, string serverId, string label) => Anchor(string.IsNullOrWhiteSpace(serverId) ? null : "#!/item?id=" + id + "&serverId=" + Uri.EscapeDataString(serverId), label);
        public static string Person(string provider, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "—";
            return Anchor(PersonUrl(provider, id), id);
        }
        public static string PersonUrl(string provider, string id) => provider == ProviderNames.Tmdb ? "https://www.themoviedb.org/person/" + Uri.EscapeDataString(id) : provider == ProviderNames.Tvdb ? "https://thetvdb.com/people/" + Uri.EscapeDataString(id) : "https://www.imdb.com/name/" + Uri.EscapeDataString(id) + "/";
        public static string Media(string provider, string type, string id, string slug = null)
        {
            if (string.IsNullOrWhiteSpace(id)) return "—";
            var url = provider == ProviderNames.Tmdb ? type == MediaTypes.Episode ? "https://www.themoviedb.org/search?query=" + Uri.EscapeDataString(id) : "https://www.themoviedb.org/" + (type == MediaTypes.Series ? "tv/" : "movie/") + Uri.EscapeDataString(id) : provider == ProviderNames.Tvdb ? string.IsNullOrWhiteSpace(slug) || type == MediaTypes.Episode ? "https://thetvdb.com/search?query=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(slug) ? id : slug) : "https://thetvdb.com/" + (type == MediaTypes.Series ? "series/" : "movies/") + Uri.EscapeDataString(slug) : "https://www.imdb.com/title/" + Uri.EscapeDataString(id) + "/";
            return Anchor(url, id);
        }
        private static string Anchor(string url, string label) { var safe = WebUtility.HtmlEncode(label ?? string.Empty); return string.IsNullOrWhiteSpace(url) ? safe : "<a href=\"" + WebUtility.HtmlEncode(url) + "\" target=\"_blank\" rel=\"noopener noreferrer\">" + safe + "</a>"; }
    }
}
