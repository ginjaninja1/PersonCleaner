using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Serialization;
using PersonCleaner.Configuration;
using PersonCleaner.V2.UI;
using System;
using System.Collections.Generic;
using System.IO; 

namespace PersonCleaner
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasThumbImage, IHasUIPages
    {
        private readonly IServerApplicationHost applicationHost;
        private readonly ILogger logger;

        private List<IPluginUIPageController> pages;


        public Plugin(
            IServerApplicationHost applicationHost,
            ILogManager logManager)
            : base(
                applicationHost.Resolve<IApplicationPaths>(),
                applicationHost.Resolve<IXmlSerializer>())
        {
            this.applicationHost = applicationHost;

            // Create the plugin logger once.
            this.logger = logManager.GetLogger(this.Name);

            Instance = this;
        }


        /// <summary>
        /// Gets the running instance of this plugin. Configuration is
        /// accessed via Instance.Configuration / SaveConfiguration() /
        /// UpdateConfiguration() - inherited from BasePlugin&lt;T&gt;, no
        /// custom store needed.
        /// </summary>
        public static Plugin Instance { get; private set; }


        public override string Description =>
            "Builds a read-only, media-first evidence graph for resolving Emby person identities.";


        public override Guid Id =>
            new Guid("ea91a15e-d1fe-4226-9b30-20d5f999fa1b");


        public override string Name => "PersonCleaner";


        public ImageFormat ThumbImageFormat =>
            ImageFormat.Png;


        public Stream GetThumbImage()
            => this.GetType()
                .Assembly
                .GetManifestResourceStream(
                    this.GetType().Namespace + ".thumb.png");


        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers
        {
            get
            {
                if (this.pages == null)
                {
                    this.pages = new List<IPluginUIPageController>();

                    this.pages.Add(
                        new MainPageController(
                            this.GetPluginInfo(),
                            this.applicationHost,
                            this.logger));
                }

                return this.pages.AsReadOnly();
            }
        }
    }
}
