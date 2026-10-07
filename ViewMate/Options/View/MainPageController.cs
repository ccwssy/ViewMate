using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using ViewMate.Options.Store;
using ViewMate.Options.UIBaseClasses;
using ViewMate.Properties;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ViewMate.Options.View
{
    internal class MainPageController : ControllerBase, IHasTabbedUIPages
    {
        private readonly PluginInfo _pluginInfo;
        private readonly PluginOptionsStore _mainOptionsStore;
        private readonly IReadOnlyList<IPluginUIPageController> _tabPageControllers;

        public MainPageController(PluginInfo pluginInfo, PluginOptionsStore mainOptionsStore)
            : base(pluginInfo.Id)
        {
            _pluginInfo = pluginInfo;
            _mainOptionsStore = mainOptionsStore;

            PageInfo = new PluginPageInfo
            {
                Name = "Settings",
                EnableInMainMenu = true,
                DisplayName = Resources.ResourceManager.GetString("PluginOptions_EditorTitle_PinyinSearch",
                    Plugin.Instance.DefaultUICulture),
                MenuIcon = "video_settings",
                IsMainConfigPage = false,
            };

            // tab 顺序即此列表顺序。框架通过 UIPageControllers 链注册这些页面；
            // 不要把各 tab 直接注册到顶层。
            _tabPageControllers = new List<IPluginUIPageController>
            {
                new IntroSkipPageController(pluginInfo, mainOptionsStore),
                new AboutPageController(pluginInfo, mainOptionsStore),
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public IReadOnlyList<IPluginUIPageController> TabPageControllers => _tabPageControllers;

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            IPluginUIView view = new HomePageView(_pluginInfo, _mainOptionsStore);
            return Task.FromResult(view);
        }

    }
}
