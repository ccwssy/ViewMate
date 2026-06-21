namespace StrmAssistant.Options.UIBaseClasses.Views
{
    using System;
    using System.Threading.Tasks;

    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Events;
    using MediaBrowser.Model.GenericEdit;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaBrowser.Model.Plugins.UI.Views.Enums;

    internal abstract class PluginViewBase : IPluginUIView, IPluginViewWithOptions
    {
        protected PluginViewBase(string pluginId)
        {
            this.PluginId = pluginId;
        }

        public event EventHandler<GenericEventArgs<IPluginUIView>> UIViewInfoChanged;

        public virtual string Caption => this.ContentData.EditorTitle;

        public virtual string SubCaption => this.ContentData.EditorDescription;

        public string PluginId { get; protected set; }

        public IEditableObject ContentData
        {
            get
            {
                return this.ContentDataCore;
            }

            set
            {
                this.ContentDataCore = value;
            }
        }

        public UserDto User { get; set; }

        public string RedirectViewUrl { get; set; }

        public Uri HelpUrl { get; set; }

        public QueryCloseAction QueryCloseAction { get; set; }

        public WizardHidingBehavior WizardHidingBehavior { get; set; }

        public CompactViewAppearance CompactViewAppearance { get; set; }

        public DialogSize DialogSize { get; set; }

        public string OKButtonCaption { get; set; }

        public DialogAction PrimaryDialogAction { get; set; }

        protected virtual IEditableObject ContentDataCore { get; set; }

        public virtual bool IsCommandAllowed(string commandKey)
        {
            return true;
        }

        public virtual Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            return Task.FromResult<IPluginUIView>(null);
        }

        public virtual Task Cancel()
        {
            return Task.CompletedTask;
        }

        public virtual void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data)
        {
        }

        protected virtual void RaiseUIViewInfoChanged()
        {
            this.UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
        }

        protected virtual void RaiseUIViewInfoChanged(GenericEventArgs<IPluginUIView> e)
        {
            this.UIViewInfoChanged?.Invoke(this, e);
        }

        public virtual PluginViewOptions ViewOptions
        {
            get
            {
                return new PluginViewOptions
                {
                    HelpUrl = this.HelpUrl,
                    CompactViewAppearance = this.CompactViewAppearance,
                    QueryCloseAction = this.QueryCloseAction,
                    DialogSize = this.DialogSize,
                    OKButtonCaption = this.OKButtonCaption,
                    PrimaryDialogAction = this.PrimaryDialogAction,
                    WizardHidingBehavior = this.WizardHidingBehavior,
                };
            }
        }
    }
}
