using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.DxGrid;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using PersonCleaner.V2.Storage;
using System;
using System.Linq;
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

        public static EvidenceDialogUI Build(DashboardDecision[] rows, RunStatus run)
        {
            var grid = new DxGridOptions(new DashboardDecision(), nameof(DashboardDecision.DecisionId), false, true, true, true)
            {
                heightMode = DxGridOptions.GridHeightMode.fullHeight,
                allowColumnReordering = true,
                allowColumnResizing = true,
                columnAutoWidth = false,
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
                paging = new DxGridPaging { enabled = true, pageSize = 50 },
                scrolling = new DxGridScrolling { showScrollbar = DxGridScrolling.ShowScrollbarMode.always, mode = DxGridScrolling.ScrollingMode.standard },
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
                grouping = new DxGridGrouping { autoExpandAll = true, allowCollapsing = true }
            };

            if (detail.columns != null) foreach (var column in detail.columns)
            {
                column.allowEditing = false;
                column.allowGrouping = false;
                column.allowHeaderFiltering = false;
                if (column.dataField == nameof(DashboardDetail.DetailId) || column.dataField == nameof(DashboardDetail.Order)) column.visible = false;
                if (column.dataField == nameof(DashboardDetail.Section)) { column.groupIndex = 0; column.showWhenGrouped = true; }
                if (column.dataField == nameof(DashboardDetail.Signal)) column.width = 150;
                if (column.dataField == nameof(DashboardDetail.Verdict)) column.width = 110;
                if (column.dataField == nameof(DashboardDetail.RawMetric)) { column.caption = "Stored metric"; column.width = 220; }
            }

            grid.masterDetail = new DxGridMasterDetail
            {
                enabled = true,
                autoExpandAll = false,
                childRowsFieldName = nameof(DashboardDecision.Details),
                detailGridOptions = detail
            };

            if (grid.columns != null) foreach (var column in grid.columns)
            {
                column.allowEditing = false;
                column.allowGrouping = true;
                column.allowHeaderFiltering = true;
                if (column.dataField == nameof(DashboardDecision.DecisionId)) column.visible = false;
                if (column.dataField == nameof(DashboardDecision.Details)) { column.visible = false; column.isSecondaryGridDataSource = true; }
                if (column.dataField == nameof(DashboardDecision.Status)) { column.caption = "Decision class"; column.groupIndex = 0; column.showWhenGrouped = true; column.width = 120; }
                if (column.dataField == nameof(DashboardDecision.Action)) column.width = 190;
                if (column.dataField == nameof(DashboardDecision.Person)) column.width = 190;
                if (column.dataField == nameof(DashboardDecision.EmbyAnchor)) { column.caption = "Emby anchor"; column.width = 100; }
                if (column.dataField == nameof(DashboardDecision.ProviderIdentities)) { column.caption = "Provider IDs"; column.width = 230; }
                if (column.dataField == nameof(DashboardDecision.Confidence)) column.width = 90;
                if (column.dataField == nameof(DashboardDecision.ImpactedTitles)) { column.caption = "Titles"; column.width = 75; }
                if (column.dataField == nameof(DashboardDecision.Decision)) { column.caption = "Plain-language decision"; column.width = 420; }
                if (column.dataField == nameof(DashboardDecision.Why)) column.width = 480;
            }

            var summary = run == null ? "No completed run is available."
                : "Run " + run.RunId + " · " + run.Decisions + " decisions (" + run.DecisionBreakdown + ") · up to " + Plugin.Instance.Configuration.MaximumDashboardRows + " rows shown per decision class";
            return new EvidenceDialogUI { RunSummary = new CaptionItem(summary), Decisions = new DxDataGrid(grid), Rows = rows ?? Array.Empty<DashboardDecision>() };
        }
    }

    internal sealed class EvidenceDialogView : DialogViewBase
    {
        private readonly ILogger logger;

        public EvidenceDialogView(PluginInfo plugin, IApplicationPaths paths, ILogger logger) : base(plugin.Id)
        {
            this.logger = logger;
            AllowOk = false;
            AllowCancel = true;
            using (var repository = new ResolutionRepository(paths))
            {
                repository.Initialize();
                var rows = repository.Dashboard(Plugin.Instance.Configuration.MaximumDashboardRows, Plugin.Instance.Configuration.MaximumMediaExamplesPerDecision);
                var run = repository.LatestRun();
                logger.Info("PersonCleaner full-screen evidence dialog loaded {0} decision row(s) in {1} status group(s), with {2} attached evidence/title detail row(s), from run {3}.", rows.Length, rows.Select(x => x.Status).Distinct(StringComparer.Ordinal).Count(), rows.Sum(x => x.Details == null ? 0 : x.Details.Length), run == null ? 0 : run.RunId);
                ContentData = EvidenceDialogUI.Build(rows, run);
            }
        }

        public override string Caption => "Person resolution evidence";
        public override bool ShowDialogFullScreen => true;
        public override Task OnCancelCommand() => Task.CompletedTask;
        public override Task OnOkCommand(string providerId, string commandId, string data) => Task.CompletedTask;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            logger.Debug("PersonCleaner evidence dialog command {0}; delegating to dialog host.", commandId ?? "(null)");
            return base.RunCommand(itemId, commandId, data);
        }
    }
}
