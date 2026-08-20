using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Serialization;
using PersonCleaner.Configuration;
using PersonCleaner.UIBaseClasses.Views;
using PersonCleaner.UI.Config;
using MediaBrowser.Model.Tasks;

namespace PersonCleaner.UI
{
    /// <summary>
    /// Config page. Deliberately kept to just: construction, page settings,
    /// and command handlers (including OnSaveCommand) that read/write the
    /// persisted configuration via Plugin.Instance.
    ///
    /// Everything else has been split out:
    ///   - PluginConfiguration    : the actual persisted schema (no UI members)
    ///   - ConfigUI               : the on-screen view-model (never persisted)
    ///   - RelevantLibraryTypes   : which CollectionTypes this plugin cares about
    ///   - LibraryFilterCommands  : command id constants + build/parse
    ///   - LibraryPathReconciler  : domain logic for reconciling paths
    ///   - ConfigViewBuilder      : builds the on-screen GenericItemList
    /// </summary>
    internal class ConfigPageView : PluginPageView
    {
        private readonly ILibraryManager libraryManager;
        private readonly IJsonSerializer jsonSerializer;
        private readonly ILogger logger;
        private readonly ITaskManager taskManager;

        public ConfigPageView(
            PluginInfo pluginInfo,
            IServerApplicationHost applicationHost,
            ILogger logger)
            : base(pluginInfo.Id)
        {
            this.logger = logger;
            this.libraryManager = applicationHost.Resolve<ILibraryManager>();
            this.jsonSerializer = applicationHost.Resolve<IJsonSerializer>();
            this.taskManager = applicationHost.Resolve<ITaskManager>();
            this.ShowSave = true;
            this.ShowBack = true;
            this.AllowBack = true;
            RebuildContentData();
        }

        /// <summary>
        /// Only libraries relevant to this plugin (movies/TV shows) - see
        /// RelevantLibraryTypes. Playlists, Music, etc. never appear.
        /// </summary>
        private IReadOnlyList<VirtualFolderInfo> GetRelevantFolders()
        {
            return RelevantLibraryTypes.Filter(this.libraryManager.GetVirtualFolders());
        }

        /// <summary>
        /// Reloads the persisted config, reconciles it against Emby's current
        /// (relevant) library layout, and rebuilds ContentData from it.
        ///
        /// NOTE: ContentData is always a freshly-built ConfigUI display
        /// object, never Plugin.Instance.Configuration itself - that's what
        /// stops visual elements from ever being written to disk.
        /// </summary>
        private void RebuildContentData()
        {
            var config = Plugin.Instance.Configuration;
            var currentFolders = GetRelevantFolders();

            LibraryPathReconciler.EnsureDiscoveredPaths(config, currentFolders);

            this.ContentData = ConfigViewBuilder.BuildDisplayConfig(config, currentFolders, this.taskManager);
        }

        public override Task<IPluginUIView> OnSaveCommand(
            string itemId,
            string commandId,
            string data)
        {
            return RunCommand(itemId, commandId, data);
        }

        public override Task<IPluginUIView> RunCommand(
            string itemId,
            string commandId,
            string data)
        {

            if (!string.IsNullOrEmpty(data) && commandId == "updateconfig")
            {
                // Leverage your existing HandleSave workflow to map the boolean state
                // and commit the modified PluginConfiguration to disk.
                HandleSave(data);

                // Return this view instance. Emby's framework will re-render the 
                // options page cleanly using the rebuilt ContentData state.
                return Task.FromResult<IPluginUIView>(this);
            }


            if (!string.IsNullOrEmpty(data) &&
                commandId == LibraryFilterCommands.PageSave)
            {
                HandleSave(data);
                return Task.FromResult<IPluginUIView>(this);
            }



            if (LibraryFilterCommands.TryParseLibraryToggle(commandId, out var libraryName))
            {
                HandleLibraryToggle(libraryName);
                return Task.FromResult<IPluginUIView>(this);
            }

            if (LibraryFilterCommands.TryParsePathToggle(commandId, out var pathLibraryName, out var path))
            {
                HandlePathToggle(pathLibraryName, path);
                return Task.FromResult<IPluginUIView>(this);
            }

            return Task.FromResult<IPluginUIView>(this);
        }

        private void HandleSave(string data)
        {
            var config = Plugin.Instance.Configuration;

            try
            {
                // GenericUI posts back the entire rendered ConfigUI object.
                // Only the real settings on it are copied onto the persisted
                // PluginConfiguration instance - headings/LibraryList from
                // the incoming payload are discarded here.
                var incoming = this.jsonSerializer.DeserializeFromString<ConfigUI>(data);

                if (incoming != null)
                {
                    config.EnablePlugin = incoming.EnablePlugin;
                    config.TvdbApiKey = incoming.TvdbApiKey?.Trim() ?? string.Empty;
                    config.TvdbSubscriberPin = incoming.TvdbSubscriberPin?.Trim() ?? string.Empty;
                    config.TmdbApiKey = incoming.TmdbApiKey?.Trim() ?? string.Empty;
                    config.SuccessCacheDays = Math.Max(1, incoming.SuccessCacheDays);
                    config.FailureRetryMinutes = Math.Max(1, incoming.FailureRetryMinutes);
                    config.MinimumRequestIntervalMilliseconds = Math.Max(0, incoming.MinimumRequestIntervalMilliseconds);
                    config.TvdbMaximumConcurrentRequests = Math.Max(1, Math.Min(8, incoming.TvdbMaximumConcurrentRequests));
                    config.TmdbMaximumConcurrentRequests = Math.Max(1, Math.Min(16, incoming.TmdbMaximumConcurrentRequests));
                    config.TmdbMinimumRequestIntervalMilliseconds = Math.Max(0, incoming.TmdbMinimumRequestIntervalMilliseconds);
                    config.GivenNameEquivalences = incoming.GivenNameEquivalences?.Trim() ?? string.Empty;
                    config.LibraryPaths = incoming.LibraryPaths ?? new List<LibraryPathFilterItem>();

                    Plugin.Instance.SaveConfiguration();

                    this.logger.Info("Provider archive configuration saved (TVDB key present: {0}; TMDB key present: {1})", !string.IsNullOrWhiteSpace(config.TvdbApiKey), !string.IsNullOrWhiteSpace(config.TmdbApiKey));
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("Error saving Poster To Folder configuration", ex);
            }

            RebuildContentData();
        }

        private void HandleLibraryToggle(string libraryName)
        {
            var config = Plugin.Instance.Configuration;
            var currentFolders = GetRelevantFolders();

            if (LibraryPathReconciler.ToggleLibrary(config, currentFolders, libraryName))
            {
                Plugin.Instance.SaveConfiguration();
            }

            RebuildContentData();
            RaiseUIViewInfoChanged();
        }

        private void HandlePathToggle(string libraryName, string path)
        {
            var config = Plugin.Instance.Configuration;
            var currentFolders = GetRelevantFolders();

            if (LibraryPathReconciler.TogglePath(config, currentFolders, libraryName, path))
            {
                Plugin.Instance.SaveConfiguration();
            }

            RebuildContentData();
            RaiseUIViewInfoChanged();
        }
    }
}
