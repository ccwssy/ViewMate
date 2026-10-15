namespace ViewMate.Options.UIBaseClasses.Views
{
    using System.Threading.Tasks;

    using MediaBrowser.Model.Plugins.UI.Views;

    /// <summary>
    /// 弹窗视图基类：默认允许取消与确定；具体命令行为由子类覆写。
    /// </summary>
    internal abstract class PluginDialogView : PluginViewBase, IPluginDialogView
    {
        protected PluginDialogView(string pluginId)
        : base(pluginId)
        {
        }

        public bool AllowCancel { get; set; } = true;

        public bool AllowOk { get; set; } = true;

        public virtual bool ShowDialogFullScreen => false;

        public virtual Task OnCancelCommand()
        {
            return Task.CompletedTask;
        }

        public virtual Task OnOkCommand(string providerId, string commandId, string data)
        {
            return Task.CompletedTask;
        }
    }
}
