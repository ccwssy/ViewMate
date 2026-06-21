using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using EmbyChineseSearch.Options.Store;
using EmbyChineseSearch.Options.UIBaseClasses;
using EmbyChineseSearch.Properties;
using System.Threading.Tasks;

namespace EmbyChineseSearch.Options.View
{
    internal class MainPageController : ControllerBase
    {
        private readonly PluginInfo _pluginInfo;
        private readonly PluginOptionsStore _mainOptionsStore;
        public MainPageController(PluginInfo pluginInfo,PluginOptionsStore mainOptionsStore
            )
            : base(pluginInfo.Id)
        {
            _pluginInfo = pluginInfo;
            _mainOptionsStore = mainOptionsStore;

            PageInfo = new PluginPageInfo
            {
                Name = "Settings",
                EnableInMainMenu = true,
                DisplayName = Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
                    Plugin.Instance.DefaultUICulture),
                MenuIcon = "video_settings",
                IsMainConfigPage = false,
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            IPluginUIView view = new HomePageView(_pluginInfo, _mainOptionsStore);
            return Task.FromResult(view);
        }

    }
}
