namespace ViewMate.Options.UIBaseClasses
{
    using System.Threading;
    using System.Threading.Tasks;

    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI;
    using MediaBrowser.Model.Plugins.UI.Views;

    public abstract class ControllerBase : IPluginUIPageController
    {
        /// <summary>初始化 <see cref="ControllerBase"/> 类的新实例。</summary>
        /// <param name="pluginId">插件标识符。</param>
        protected ControllerBase(string pluginId)
        {
            this.PluginId = pluginId;
        }

        public abstract PluginPageInfo PageInfo { get; }

        public string PluginId { get; }

        public virtual Task Initialize(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public abstract Task<IPluginUIView> CreateDefaultPageView();
    }
}
