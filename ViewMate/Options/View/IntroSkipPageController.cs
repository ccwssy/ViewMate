using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using ViewMate.Options.Store;
using ViewMate.Options.UIBaseClasses;
using System.Threading.Tasks;

namespace ViewMate.Options.View
{
    internal class IntroSkipPageController : ControllerBase
    {
        private readonly PluginInfo _pluginInfo;
        private readonly PluginOptionsStore _mainOptionsStore;

        public IntroSkipPageController(PluginInfo pluginInfo, PluginOptionsStore mainOptionsStore)
            : base(pluginInfo.Id)
        {
            _pluginInfo = pluginInfo;
            _mainOptionsStore = mainOptionsStore;

            PageInfo = new PluginPageInfo
            {
                Name = "IntroSkip",
                EnableInMainMenu = false,
                DisplayName = "片头尾跳过",
                MenuIcon = "skip_next",
                IsMainConfigPage = false,
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            IPluginUIView view = new IntroSkipPageView(_pluginInfo, _mainOptionsStore);
            return Task.FromResult(view);
        }
    }
}
