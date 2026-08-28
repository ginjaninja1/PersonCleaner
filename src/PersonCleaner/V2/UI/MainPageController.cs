using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PersonCleaner.V2.UI
{
    internal sealed class MainPageController : PageControllerBase, IHasTabbedUIPages
    {
        private readonly PluginInfo plugin;
        private readonly IServerApplicationHost host;
        private readonly ILogger logger;
        private readonly List<IPluginUIPageController> tabs;

        public MainPageController(PluginInfo plugin, IServerApplicationHost host, ILogger logger) : base(plugin.Id)
        {
            this.plugin = plugin; this.host = host; this.logger = logger;
            PageInfo = new PluginPageInfo { Name = "personcleaner", DisplayName = "Person Cleaner", EnableInMainMenu = true, MenuIcon = "people", IsMainConfigPage = true };
            tabs = new List<IPluginUIPageController>
            {
                //new TabController(plugin, "personcleaner-config-v2", "Configuration", x => new ConfigurationPageView(x, host, logger)),
                new TabController(plugin, "personcleaner-evidence-v2", "Decision evidence", x => new EvidencePageView(x, host, logger)),
                new TabController(plugin, "personcleaner-corrections-v2", "Provider corrections", x => new CorrectionsPageView(x, host, logger))
            };
        }
        public override PluginPageInfo PageInfo { get; }
        public IReadOnlyList<IPluginUIPageController> TabPageControllers => tabs.AsReadOnly();
        public override Task<IPluginUIView> CreateDefaultPageView() => Task.FromResult<IPluginUIView>(new ConfigurationPageView(plugin, host, logger));
    }

    internal sealed class TabController : PageControllerBase
    {
        private readonly PluginInfo plugin;
        private readonly Func<PluginInfo, IPluginUIView> factory;
        public TabController(PluginInfo plugin, string name, string displayName, Func<PluginInfo, IPluginUIView> factory) : base(plugin.Id) { this.plugin = plugin; this.factory = factory; PageInfo = new PluginPageInfo { Name = name, DisplayName = displayName }; }
        public override PluginPageInfo PageInfo { get; }
        public override Task<IPluginUIView> CreateDefaultPageView() => Task.FromResult(factory(plugin));
    }
}
