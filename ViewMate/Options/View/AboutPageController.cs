using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using ViewMate.Options.Store;
using ViewMate.Options.UIBaseClasses;
using System.Threading.Tasks;

namespace ViewMate.Options.View
{
    internal class AboutPageController : ControllerBase
    {
        private readonly PluginInfo _pluginInfo;
        private readonly PluginOptionsStore _mainOptionsStore;

        public AboutPageController(PluginInfo pluginInfo, PluginOptionsStore mainOptionsStore)
            : base(pluginInfo.Id)
        {
            _pluginInfo = pluginInfo;
            _mainOptionsStore = mainOptionsStore;

            PageInfo = new PluginPageInfo
            {
                Name = "About",
                EnableInMainMenu = false,
                DisplayName = "关于",
                MenuIcon = "info",
                IsMainConfigPage = false,
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            IPluginUIView view = new AboutPageView(_pluginInfo, _mainOptionsStore);
            return Task.FromResult(view);
        }
    }
}
