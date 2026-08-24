using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.DxGrid;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Attributes;
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
    public sealed class EvidenceDialogUI : EditableObjectBase
    {
        public override string EditorTitle => null;
        public CaptionItem RunSummary { get; set; }

        [GridDataSource(nameof(Rows))]
        public DxDataGrid Decisions { get; set; }

        public DashboardDecision[] Rows { get; set; } = Array.Empty<DashboardDecision>();

        public static EvidenceDialogUI Build(DashboardDecision[] rows, RunStatus run, string focusedDecisionId = null)
        {
            var grid = new DxGridOptions(new DashboardDecision(), nameof(DashboardDecision.CaseId), false, true, true, true)
            {
                // GenericEdit implements fullHeight as 100% inside nested 100%
                // containers. In a dialog that can leave the horizontal bar at
                // an unresolved container edge. A bounded grid keeps both
                // scrollbars inside the visible full-screen dialog.
                heightMode = DxGridOptions.GridHeightMode.large,
                allowColumnReordering = true,
                allowColumnResizing = true,
                columnAutoWidth = false,
                columnHidingEnabled = false,
                focusedRowEnabled = !string.IsNullOrWhiteSpace(focusedDecisionId),
                focusStateEnabled = !string.IsNullOrWhiteSpace(focusedDecisionId),
                autoNavigateToFocusedRow = !string.IsNullOrWhiteSpace(focusedDecisionId),
                focusedRowKey = focusedDecisionId,
                showBorders = true,
                showRowLines = true,
                rowAlternationEnabled = true,
                wordWrapEnabled = true,
                cellHintEnabled = true,
                grouping = new DxGridGrouping { allowCollapsing = true, autoExpandAll = false, contextMenuEnabled = true },
                groupPanel = new { visible = true, emptyPanelText = "Drag a column here to group" },
                searchPanel = new { visible = true, width = 420, placeholder = "Search names, provider IDs, decisions or reasons" },
                filterRow = new DxGridFilterRow { visible = true },
                headerFilter = new DxGridHeaderFilter { visible = true },
                paging = new DxGridPaging { enabled = false },
                scrolling = new DxGridScrolling
                {
                    scrollByContent = true,
                    scrollByThumb = true,
                    showScrollbar = DxGridScrolling.ShowScrollbarMode.always,
                    columnRenderingMode = DxGridScrolling.ColumnRenderingMode.standard,
                    mode = DxGridScrolling.ScrollingMode.@virtual,
                    rowRenderingMode = DxGridScrolling.RowRenderingMode.@virtual,
                    preloadEnabled = false,
                    useNative = "false"
                },
                editing = new DxGridEditing { mode = DxGridEditing.GridEditMode.cell, allowUpdating = true },
                onChangeCommand = new DxGridOnChangeCommand { commandId = "decision-review-changes" },
                noDataText = "No completed person evidence is available. Run 'PersonCleaner - Build person evidence' from Scheduled Tasks."
            };

            var detail = new DxGridOptions(new DashboardDetail(), nameof(DashboardDetail.DetailId), false, false, false, false)
            {
                heightMode = DxGridOptions.GridHeightMode.auto,
                columnAutoWidth = false,
                showBorders = true,
                showRowLines = true,
                rowAlternationEnabled = true,
                wordWrapEnabled = true,
                grouping = new DxGridGrouping { autoExpandAll = true, allowCollapsing = true },
                paging = new DxGridPaging { enabled = false },
                scrolling = new DxGridScrolling { scrollByContent = true, scrollByThumb = true, showScrollbar = DxGridScrolling.ShowScrollbarMode.always, mode = DxGridScrolling.ScrollingMode.standard, useNative = "false" }
            };

            if (detail.columns != null) foreach (var column in detail.columns)
            {
                column.allowEditing = false;
                column.allowGrouping = false;
                column.allowHeaderFiltering = false;
                if (column.dataField == nameof(DashboardDetail.DetailId) || column.dataField == nameof(DashboardDetail.Order)) column.visible = false;
                if (column.dataField == nameof(DashboardDetail.EmbyMediaId) || column.dataField == nameof(DashboardDetail.MediaType) || column.dataField == nameof(DashboardDetail.TmdbId) || column.dataField == nameof(DashboardDetail.TvdbId) || column.dataField == nameof(DashboardDetail.TvdbSlug) || column.dataField == nameof(DashboardDetail.ImdbId)) column.visible = false;
                if (column.dataField == nameof(DashboardDetail.Section)) { column.groupIndex = 0; column.showWhenGrouped = true; }
                if (column.dataField == nameof(DashboardDetail.Signal)) column.width = 150;
                if (column.dataField == nameof(DashboardDetail.Verdict)) column.width = 110;
                if (column.dataField == nameof(DashboardDetail.RawMetric)) { column.caption = "Stored metric"; column.width = 220; }
                if (column.dataField == nameof(DashboardDetail.Explanation)) { column.encodeHtml = false; column.caption = "Evidence / Emby title"; column.width = 650; }
                if (column.dataField == nameof(DashboardDetail.ProviderObjects)) { column.encodeHtml = false; column.caption = "Provider pages"; column.width = 300; }
            }

            if (grid.columns != null) foreach (var column in grid.columns)
            {
                column.allowEditing = false;
                column.allowGrouping = true;
                column.allowHeaderFiltering = true;
                if (column.dataField == nameof(DashboardDecision.CaseId) || column.dataField == nameof(DashboardDecision.DecisionId) || column.dataField == nameof(DashboardDecision.UnderlyingDecisionIds) || column.dataField == nameof(DashboardDecision.UnderlyingDecisionLabels) || column.dataField == nameof(DashboardDecision.AutomationReason)) column.visible = false;
                if (column.dataField == nameof(DashboardDecision.ReviewChanges)) { column.caption = "Review / update"; column.width = 110; column.allowEditing = true; column.allowGrouping = false; column.allowHeaderFiltering = false; }
                if (column.dataField == nameof(DashboardDecision.Details)) { column.visible = false; column.isSecondaryGridDataSource = true; }
                if (column.dataField == nameof(DashboardDecision.Status)) { column.caption = "Case type"; column.groupIndex = 0; column.showWhenGrouped = true; column.width = 120; }
                if (column.dataField == nameof(DashboardDecision.Action)) { column.caption = "Safety mode"; column.width = 190; }
                if (column.dataField == nameof(DashboardDecision.Automation)) { column.caption = "Automation assessment"; column.width = 170; }
                if (column.dataField == nameof(DashboardDecision.Person)) column.width = 190;
                if (column.dataField == nameof(DashboardDecision.EmbyAnchor)) { column.caption = "Emby anchor"; column.width = 100; }
                if (column.dataField == nameof(DashboardDecision.Person)) column.encodeHtml = false;
                if (column.dataField == nameof(DashboardDecision.EmbyAnchor)) { column.encodeHtml = false; column.caption = "Emby anchor"; column.width = 100; }
                if (column.dataField == nameof(DashboardDecision.ProviderIdentities)) { column.encodeHtml = false; column.caption = "Connected provider IDs"; column.width = 300; }
                if (column.dataField == nameof(DashboardDecision.CurrentProviderIds)) { column.encodeHtml = false; column.caption = "Current Emby IDs"; column.width = 300; }
                if (column.dataField == nameof(DashboardDecision.Relationships)) { column.caption = "Relationships"; column.width = 95; }
                if (column.dataField == nameof(DashboardDecision.ProviderRecords)) { column.caption = "Record count"; column.width = 105; }
                if (column.dataField == nameof(DashboardDecision.Confidence)) { column.caption = "Evidence strength"; column.width = 115; }
                if (column.dataField == nameof(DashboardDecision.LocalAnchorConfidence)) { column.caption = "Local anchor"; column.width = 100; }
                if (column.dataField == nameof(DashboardDecision.ImpactedTitles)) { column.caption = "Titles"; column.width = 75; }
                if (column.dataField == nameof(DashboardDecision.Decision)) { column.caption = "Case summary"; column.width = 480; }
                if (column.dataField == nameof(DashboardDecision.Why)) { column.caption = "Automation reason"; column.width = 520; }
            }

            // Mark the secondary source before assigning masterDetail. The GenericEdit
            // host uses this metadata during its first serialization pass.
            grid.masterDetail = new DxGridMasterDetail
            {
                enabled = true,
                autoExpandAll = false,
                childRowsFieldName = nameof(DashboardDecision.Details),
                detailGridOptions = detail
            };

            var rowCount = rows == null ? 0 : rows.Length;
            var summary = run == null ? "No completed run is available."
                : "Run " + run.RunId + " · " + rowCount + " review case(s) from " + run.Decisions + " stored decision relationship(s) (" + run.DecisionBreakdown + ")";
            return new EvidenceDialogUI { RunSummary = new CaptionItem(summary), Decisions = new DxDataGrid(grid), Rows = rows ?? Array.Empty<DashboardDecision>() };
        }
    }

    internal sealed class EvidenceDialogView : DialogViewBase
    {
        private readonly PluginInfo plugin;
        private readonly IServerApplicationHost host;
        private readonly IApplicationPaths paths;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;
        private string focusedDecisionId;

        public EvidenceDialogView(PluginInfo plugin, IServerApplicationHost host, ILogger logger) : base(plugin.Id)
        {
            this.plugin = plugin; this.host = host; this.logger = logger; this.paths = host.Resolve<IApplicationPaths>(); this.json = host.Resolve<IJsonSerializer>();
            AllowOk = false;
            AllowCancel = true;
            Rebuild();
        }

        private void Rebuild()
        {
            using (var repository = new ResolutionRepository(paths))
            {
                repository.Initialize();
                var rows = repository.Dashboard(Plugin.Instance.Configuration.MaximumMediaExamplesPerDecision);
                var run = repository.LatestRun();
                string serverId = null;
                try { serverId = host.GetPublicSystemInfo(CancellationToken.None).GetAwaiter().GetResult()?.Id; }
                catch (Exception ex) { logger.Warn("PersonCleaner could not resolve the Emby server ID; provider links will remain available but Emby item links will be plain text. {0}", ex.Message); }
                EvidenceLinks.Apply(rows, serverId);
                logger.Info("PersonCleaner full-screen evidence dialog loaded {0} review case row(s) in {1} status group(s), with {2} attached assessment/relationship/title detail row(s), from run {3}.", rows.Length, rows.Select(x => x.Status).Distinct(StringComparer.Ordinal).Count(), rows.Sum(x => x.Details == null ? 0 : x.Details.Length), run == null ? 0 : run.RunId);
                var focused = rows.FirstOrDefault(x => string.Equals(x.CaseId, focusedDecisionId, StringComparison.Ordinal) || (x.UnderlyingDecisionIds ?? new string[0]).Contains(focusedDecisionId, StringComparer.Ordinal));
                var focus = focused == null ? null : focused.CaseId;
                ContentData = EvidenceDialogUI.Build(rows, run, focus);
            }
        }

        public override string Caption => "Person resolution evidence";
        public override bool ShowDialogFullScreen => true;
        public override Task OnCancelCommand() => Task.CompletedTask;
        public override Task OnOkCommand(string providerId, string commandId, string data) => Task.CompletedTask;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (commandId == "decision-review-changes" && !string.IsNullOrWhiteSpace(data))
            {
                var incoming = json.DeserializeFromString<EvidenceDialogUI>(data);
                var selected = incoming?.Rows?.FirstOrDefault(x => x.ReviewChanges && !string.IsNullOrWhiteSpace(x.CaseId));
                if (selected != null)
                {
                    focusedDecisionId = selected.CaseId;
                    logger.Info("PersonCleaner opening review case {0} with {1} underlying decision relationship(s).", selected.CaseId, (selected.UnderlyingDecisionIds ?? new string[0]).Length);
                    return Task.FromResult<IPluginUIView>(new ReviewCaseDialogView(plugin, host, logger, this, Rebuild, selected));
                }
            }
            logger.Debug("PersonCleaner evidence dialog command {0}; delegating to dialog host.", commandId ?? "(null)");
            return base.RunCommand(itemId, commandId, data);
        }

        public override void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data) { Rebuild(); Refresh(); }
    }

    internal static class EvidenceLinks
    {
        public static void Apply(DashboardDecision[] rows, string serverId)
        {
            foreach (var row in rows ?? Array.Empty<DashboardDecision>())
            {
                var rawName = row.Person;
                var rawAnchor = row.EmbyAnchor;
                var anchorId = ParseLong(rawAnchor);
                if (anchorId.HasValue)
                {
                    row.Person = Anchor(EmbyUrl(anchorId.Value, serverId), rawName);
                    row.EmbyAnchor = Anchor(EmbyUrl(anchorId.Value, serverId), rawAnchor);
                }
                else
                {
                    row.Person = WebUtility.HtmlEncode(rawName ?? string.Empty);
                    row.EmbyAnchor = WebUtility.HtmlEncode(rawAnchor ?? string.Empty);
                }
                row.ProviderIdentities = PersonProviderLinks(row.ProviderIdentities);
                row.CurrentProviderIds = PersonProviderLinks(row.CurrentProviderIds);

                foreach (var detail in row.Details ?? Array.Empty<DashboardDetail>())
                {
                    if (!detail.EmbyMediaId.HasValue)
                    {
                        detail.Explanation = WebUtility.HtmlEncode(detail.Explanation ?? string.Empty);
                        continue;
                    }
                    detail.Explanation = Anchor(EmbyUrl(detail.EmbyMediaId.Value, serverId), detail.Explanation);
                    var links = new List<string>();
                    Add(links, TmdbMediaUrl(detail.MediaType, detail.TmdbId), "TMDB " + detail.TmdbId, detail.TmdbId);
                    Add(links, TvdbMediaUrl(detail.MediaType, detail.TvdbSlug), "TVDB " + detail.TvdbId, detail.TvdbId);
                    Add(links, ImdbTitleUrl(detail.ImdbId), "IMDb " + detail.ImdbId, detail.ImdbId);
                    detail.ProviderObjects = links.Count == 0 ? "—" : string.Join(" · ", links);
                }
            }
        }

        private static string PersonProviderLinks(string value)
        {
            var links = new List<string>();
            foreach (var token in (value ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var item = token.Trim();
                var split = item.IndexOf(':');
                if (split <= 0 || split == item.Length - 1) { links.Add(WebUtility.HtmlEncode(item)); continue; }
                var provider = item.Substring(0, split).Trim().ToLowerInvariant();
                var id = item.Substring(split + 1).Trim();
                string url = null;
                if (provider == ProviderNames.Tmdb) url = "https://www.themoviedb.org/person/" + Uri.EscapeDataString(id);
                else if (provider == ProviderNames.Tvdb) url = "https://thetvdb.com/people/" + Uri.EscapeDataString(id);
                else if (provider == ProviderNames.Imdb) url = "https://www.imdb.com/name/" + Uri.EscapeDataString(id) + "/";
                else if (provider == ProviderNames.Wikidata) url = "https://www.wikidata.org/wiki/" + Uri.EscapeDataString(id);
                links.Add(Anchor(url, provider.ToUpperInvariant() + " " + id));
            }
            return links.Count == 0 ? WebUtility.HtmlEncode(value ?? string.Empty) : string.Join(" · ", links);
        }

        private static long? ParseLong(string value) => long.TryParse(value, out var result) ? result : (long?)null;
        private static string EmbyUrl(long id, string serverId) => string.IsNullOrWhiteSpace(serverId) ? null : "#!/item?id=" + id + "&serverId=" + Uri.EscapeDataString(serverId);
        private static string TmdbMediaUrl(string type, string id) => string.IsNullOrWhiteSpace(id) ? null : "https://www.themoviedb.org/" + (type == MediaTypes.Series ? "tv/" : "movie/") + Uri.EscapeDataString(id);
        private static string TvdbMediaUrl(string type, string slug) => string.IsNullOrWhiteSpace(slug) ? null : "https://thetvdb.com/" + (type == MediaTypes.Series ? "series/" : "movies/") + Uri.EscapeDataString(slug);
        private static string ImdbTitleUrl(string id) => string.IsNullOrWhiteSpace(id) ? null : "https://www.imdb.com/title/" + Uri.EscapeDataString(id) + "/";
        private static void Add(List<string> links, string url, string label, string rawId) { if (!string.IsNullOrWhiteSpace(rawId)) links.Add(Anchor(url, label)); }
        private static string Anchor(string url, string label)
        {
            var safeLabel = WebUtility.HtmlEncode(label ?? string.Empty);
            if (string.IsNullOrWhiteSpace(url)) return safeLabel;
            return "<a href=\"" + WebUtility.HtmlEncode(url) + "\" target=\"_blank\" rel=\"noopener noreferrer\">" + safeLabel + "</a>";
        }
    }
}
