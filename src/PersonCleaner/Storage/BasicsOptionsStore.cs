
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using PersonCleaner.UI.Config;
using PersonCleaner.UIBaseClasses.Store;

namespace PersonCleaner.Storage
{
    public class BasicsOptionsStore : SimpleFileStore<ConfigUI>
    {
        public BasicsOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
        : base(applicationHost, logger, pluginFullName)
        {
        }
    }
}
