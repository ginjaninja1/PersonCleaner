using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using PersonCleaner.Housekeeping;
using PersonCleaner.UIBaseClasses.Views;
using System;
using System.Linq;

namespace PersonCleaner.UI.Housekeeping
{
    internal sealed class HousekeepingResultsPageView : PluginPageView
    {
        public HousekeepingResultsPageView(PluginInfo pluginInfo, IApplicationPaths paths, ILogger logger) : base(pluginInfo.Id)
        {
            ShowSave=false; ShowBack=true; AllowBack=true;
            var runSummary = "No completed housekeeping run";
            try
            {
                using (var repository = new HousekeepingRepository(paths))
                {
                    HousekeepingResultsCache.Replace(repository.LatestResults().ToArray());
                    runSummary = repository.LatestRunSummary();
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("Unable to load the housekeeping results page", ex);
            }
            ContentData = HousekeepingResultsUI.Build(HousekeepingResultsCache.Rows, runSummary);
        }
    }
}
