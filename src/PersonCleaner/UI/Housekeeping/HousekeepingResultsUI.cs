using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.DxGrid;
using MediaBrowser.Model.Attributes;
using PersonCleaner.Housekeeping;
using System;

namespace PersonCleaner.UI.Housekeeping
{
    public sealed class HousekeepingResultsUI : EditableOptionsBase
    {
        public override string EditorTitle => "Baseline person housekeeping results";
        private string snapshotDescription;
        public override string EditorDescription => snapshotDescription ?? "No completed housekeeping run is loaded.";

        [GridDataSource(nameof(ResultRows))]
        public DxDataGrid Results { get; set; }

        public HousekeepingResultRow[] ResultRows { get; set; } = Array.Empty<HousekeepingResultRow>();

        public static HousekeepingResultsUI Build(HousekeepingResultRow[] rows, string runSummary)
        {
            var options = new DxGridOptions(new HousekeepingResultRow(), "ProposalId", false, true, true, false)
            {
                heightMode = DxGridOptions.GridHeightMode.large,
                allowColumnReordering = true,
                allowColumnResizing = true,
                columnAutoWidth = true,
                showBorders = true,
                showRowLines = true,
                rowAlternationEnabled = true,
                wordWrapEnabled = false,
                cellHintEnabled = true,
                grouping = new DxGridGrouping { allowCollapsing = true, autoExpandAll = false, contextMenuEnabled = true },
                groupPanel = new { visible = true, emptyPanelText = "Drag Recommendation or Signal here to group" },
                searchPanel = new { visible = true, width = 320, placeholder = "Search people, IDs, signals or evidence" },
                filterRow = new DxGridFilterRow { visible = true },
                headerFilter = new DxGridHeaderFilter { visible = true },
                paging = new DxGridPaging { enabled = false, pageSize = 10000 },
                scrolling = new DxGridScrolling
                {
                    scrollByContent = true,
                    scrollByThumb = true,
                    showScrollbar = DxGridScrolling.ShowScrollbarMode.always,
                    columnRenderingMode = DxGridScrolling.ColumnRenderingMode.standard,
                    mode = DxGridScrolling.ScrollingMode.@virtual,
                    rowRenderingMode = DxGridScrolling.RowRenderingMode.@virtual,
                    preloadEnabled = false
                },
                noDataText = "No completed housekeeping pass exists yet. Run 'PersonCleaner - Evaluate baseline person truth' from Scheduled Tasks."
            };

            if (options.columns != null)
            {
                foreach (var column in options.columns)
                {
                    column.allowEditing = false;
                    column.allowGrouping = true;
                    column.allowHeaderFiltering = true;
                    if (column.dataField == nameof(HousekeepingResultRow.ProposalId)) column.caption = "Review reference";
                    if (column.dataField == nameof(HousekeepingResultRow.EmbyPersonId)) column.visible = false;
                    if (column.dataField == nameof(HousekeepingResultRow.Person)) column.visible = false;
                    if (column.dataField == nameof(HousekeepingResultRow.CurrentValue)) column.visible = false;
                    if (column.dataField == nameof(HousekeepingResultRow.ProposedValue)) column.visible = false;
                    if (column.dataField == nameof(HousekeepingResultRow.Provider)) column.caption = "Evidence provider";
                    if (column.dataField == nameof(HousekeepingResultRow.Recommendation)) { column.caption = "Recommendation"; column.groupIndex = 0; column.showWhenGrouped = true; }
                    if (column.dataField == nameof(HousekeepingResultRow.SignalType)) column.caption = "Reason";
                    if (column.dataField == nameof(HousekeepingResultRow.CurrentEmbyIds)) column.caption = "Current Emby ID(s)";
                    if (column.dataField == nameof(HousekeepingResultRow.ProposedEmbyIds)) column.caption = "Proposed Emby ID(s)";
                    if (column.dataField == nameof(HousekeepingResultRow.CurrentName)) column.caption = "Current name(s)";
                    if (column.dataField == nameof(HousekeepingResultRow.ProposedName)) column.caption = "Proposed name(s)";
                    if (column.dataField == nameof(HousekeepingResultRow.CurrentTmdbIds)) column.caption = "Current TMDB ID(s)";
                    if (column.dataField == nameof(HousekeepingResultRow.ProposedTmdbIds)) column.caption = "Proposed TMDB ID(s)";
                    if (column.dataField == nameof(HousekeepingResultRow.CurrentTvdbIds)) column.caption = "Current TVDB ID(s)";
                    if (column.dataField == nameof(HousekeepingResultRow.ProposedTvdbIds)) column.caption = "Proposed TVDB ID(s)";
                    if (column.dataField == nameof(HousekeepingResultRow.CurrentImdbIds)) column.caption = "Current IMDb ID(s)";
                    if (column.dataField == nameof(HousekeepingResultRow.ProposedImdbIds)) column.caption = "Proposed IMDb ID(s)";
                    if (column.dataField == nameof(HousekeepingResultRow.LinkedMediaEvidence)) column.caption = "Linked media evidence";
                }
            }

            return new HousekeepingResultsUI { Results = new DxDataGrid(options), ResultRows = rows ?? Array.Empty<HousekeepingResultRow>(), snapshotDescription = (runSummary ?? "No completed housekeeping run") + ". All recommendations are review-only." };
        }
    }
}
