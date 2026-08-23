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
        public override string EditorTitle => "Media-first person truth";
        private string snapshotDescription;
        public override string EditorDescription => snapshotDescription ?? "No completed housekeeping run is loaded.";

        [GridDataSource(nameof(ResultRows))]
        public DxDataGrid Results { get; set; }

        public HousekeepingResultRow[] ResultRows { get; set; } = Array.Empty<HousekeepingResultRow>();

        public CaptionItem AcquisitionSummary { get; set; }

        public static HousekeepingResultsUI Build(HousekeepingResultRow[] rows, string runSummary, string acquisitionSummary)
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
                groupPanel = new { visible = true, emptyPanelText = "Drag a column here to group" },
                searchPanel = new { visible = true, width = 320, placeholder = "Search summary, media, people or IDs" },
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
                noDataText = "No completed media-first truth exists yet. Run 'PersonCleaner - Rebuild media-first person truth' from Scheduled Tasks."
            };
            var detailOptions=new DxGridOptions(new HousekeepingCaseDetailRow(),"DetailId",false,true,true,false)
            {
                columnAutoWidth=true,showBorders=true,showRowLines=true,rowAlternationEnabled=true,wordWrapEnabled=true,
                paging=new DxGridPaging{enabled=true,pageSize=25},filterRow=new DxGridFilterRow{visible=false},
                headerFilter=new DxGridHeaderFilter{visible=false},searchPanel=new{visible=false},
                grouping=new DxGridGrouping{allowCollapsing=true,autoExpandAll=true}
            };
            if(detailOptions.columns!=null)foreach(var column in detailOptions.columns)
            {
                column.allowEditing=false;column.allowGrouping=false;column.allowHeaderFiltering=false;
                if(column.dataField==nameof(HousekeepingCaseDetailRow.DetailId))column.visible=false;
                if(column.dataField==nameof(HousekeepingCaseDetailRow.Order))column.visible=false;
                if(column.dataField==nameof(HousekeepingCaseDetailRow.Section)){column.groupIndex=0;column.showWhenGrouped=true;}
                if(column.dataField==nameof(HousekeepingCaseDetailRow.Result))column.caption="Status / result";
                if(column.dataField==nameof(HousekeepingCaseDetailRow.Scope))column.caption="Dependency / scope";
                if(column.dataField==nameof(HousekeepingCaseDetailRow.Detail))column.caption="Action or evidence detail";
            }
            options.masterDetail=new DxGridMasterDetail{enabled=true,autoExpandAll=false,childRowsFieldName=nameof(HousekeepingResultRow.DetailRows),detailGridOptions=detailOptions};

            if (options.columns != null)
            {
                foreach (var column in options.columns)
                {
                    column.allowEditing = false;
                    column.allowGrouping = true;
                    column.allowHeaderFiltering = true;
                    if (column.dataField == nameof(HousekeepingResultRow.ProposalId)) column.caption = "Result ID";
                    if (column.dataField == nameof(HousekeepingResultRow.EmbyPersonId)) column.visible = false;
                    if (column.dataField == nameof(HousekeepingResultRow.Person)) column.visible = false;
                    if (column.dataField == nameof(HousekeepingResultRow.CurrentValue)) column.visible = false;
                    if (column.dataField == nameof(HousekeepingResultRow.ProposedValue)) column.visible = false;
                    if (column.dataField == nameof(HousekeepingResultRow.DetailRows)) column.visible = false;
                    if (column.dataField == nameof(HousekeepingResultRow.Provider)) column.visible = false;
                    if (column.dataField == nameof(HousekeepingResultRow.Recommendation)) { column.caption = "Decision class"; column.groupIndex = 0; column.showWhenGrouped = true; }
                    if (column.dataField == nameof(HousekeepingResultRow.SignalType)) column.caption = "Truth status";
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
                    if (column.dataField == nameof(HousekeepingResultRow.LinkedMediaEvidence)) column.visible = false;
                    if (column.dataField == nameof(HousekeepingResultRow.Evidence)) column.visible = false;
                    if (column.dataField == nameof(HousekeepingResultRow.AcceptancePath)) column.visible = false;
                    if (column.dataField == nameof(HousekeepingResultRow.IdentityConfidence)) column.caption = "Identity confidence";
                    if (column.dataField == nameof(HousekeepingResultRow.RelationshipConfidence)) column.caption = "Relationship confidence";
                    if (column.dataField == nameof(HousekeepingResultRow.OperationConfidence)) column.caption = "Operation confidence";
                }
            }

            return new HousekeepingResultsUI { Results = new DxDataGrid(options), ResultRows = rows ?? Array.Empty<HousekeepingResultRow>(), AcquisitionSummary = new CaptionItem(acquisitionSummary ?? "Acquisition measurements are unavailable for this run."), snapshotDescription = (runSummary ?? "No completed media-first run") + ". Auto-commit rows have already changed the new derived truth. Human-review rows preserve the current Emby relationship in that truth and expose the unresolved evidence. Expand a summary line for identities, exact media relationships and ordered truth changes. Live Emby is never changed here." };
        }
    }
}
