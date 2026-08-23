using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Plugins.UI.Views.Enums;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PersonCleaner.V2.UI
{
    internal abstract class PageControllerBase : IPluginUIPageController
    {
        protected PageControllerBase(string pluginId) { PluginId = pluginId; }
        public string PluginId { get; }
        public abstract PluginPageInfo PageInfo { get; }
        public virtual Task Initialize(CancellationToken token) => Task.CompletedTask;
        public abstract Task<IPluginUIView> CreateDefaultPageView();
    }

    internal abstract class PageViewBase : IPluginPageView, IPluginViewWithOptions
    {
        protected PageViewBase(string pluginId) { PluginId = pluginId; }
        public event EventHandler<GenericEventArgs<IPluginUIView>> UIViewInfoChanged;
        public string PluginId { get; }
        public virtual string Caption => ContentData.EditorTitle;
        public virtual string SubCaption => ContentData.EditorDescription;
        public IEditableObject ContentData { get; set; }
        public UserDto User { get; set; }
        public string RedirectViewUrl { get; set; }
        public Uri HelpUrl { get; set; }
        public QueryCloseAction QueryCloseAction { get; set; }
        public WizardHidingBehavior WizardHidingBehavior { get; set; }
        public CompactViewAppearance CompactViewAppearance { get; set; }
        public DialogSize DialogSize { get; set; }
        public string OKButtonCaption { get; set; }
        public DialogAction PrimaryDialogAction { get; set; }
        public bool ShowSave { get; set; }
        public bool ShowBack { get; set; } = true;
        public bool AllowSave { get; set; } = true;
        public bool AllowBack { get; set; } = true;
        public virtual bool IsCommandAllowed(string commandKey) => true;
        public virtual Task<IPluginUIView> RunCommand(string itemId, string commandId, string data) => Task.FromResult<IPluginUIView>(this);
        public virtual Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data) => RunCommand(itemId, commandId, data);
        public virtual Task Cancel() => Task.CompletedTask;
        public virtual void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data) { }
        protected void Refresh() => UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
        public PluginViewOptions ViewOptions => new PluginViewOptions { HelpUrl = HelpUrl, CompactViewAppearance = CompactViewAppearance, QueryCloseAction = QueryCloseAction, DialogSize = DialogSize, OKButtonCaption = OKButtonCaption, PrimaryDialogAction = PrimaryDialogAction, WizardHidingBehavior = WizardHidingBehavior };
    }

    internal abstract class DialogViewBase : IPluginDialogView, IPluginViewWithOptions
    {
        protected DialogViewBase(string pluginId) { PluginId = pluginId; AllowCancel = true; AllowOk = false; }
        public event EventHandler<GenericEventArgs<IPluginUIView>> UIViewInfoChanged;
        public string PluginId { get; }
        public virtual string Caption => ContentData == null ? string.Empty : ContentData.EditorTitle;
        public virtual string SubCaption => ContentData == null ? string.Empty : ContentData.EditorDescription;
        public IEditableObject ContentData { get; set; }
        public UserDto User { get; set; }
        public string RedirectViewUrl { get; set; }
        public Uri HelpUrl { get; set; }
        public QueryCloseAction QueryCloseAction { get; set; }
        public WizardHidingBehavior WizardHidingBehavior { get; set; }
        public CompactViewAppearance CompactViewAppearance { get; set; }
        public DialogSize DialogSize { get; set; }
        public string OKButtonCaption { get; set; }
        public DialogAction PrimaryDialogAction { get; set; }
        public bool AllowCancel { get; set; }
        public bool AllowOk { get; set; }
        public virtual bool ShowDialogFullScreen => true;
        public virtual bool IsCommandAllowed(string commandKey) => true;
        public virtual Task<IPluginUIView> RunCommand(string itemId, string commandId, string data) => Task.FromResult<IPluginUIView>(null);
        public virtual Task OnCancelCommand() => Task.CompletedTask;
        public virtual Task OnOkCommand(string providerId, string commandId, string data) => Task.CompletedTask;
        public virtual Task Cancel() => Task.CompletedTask;
        public virtual void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data) { }
        protected void Refresh() => UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
        public PluginViewOptions ViewOptions => new PluginViewOptions { HelpUrl = HelpUrl, CompactViewAppearance = CompactViewAppearance, QueryCloseAction = QueryCloseAction, DialogSize = DialogSize, OKButtonCaption = OKButtonCaption, PrimaryDialogAction = PrimaryDialogAction, WizardHidingBehavior = WizardHidingBehavior };
    }
}
