using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using ViewMate.Options.Store;
using ViewMate.Options.UIBaseClasses;
using ViewMate.Properties;
using System.Threading.Tasks;

namespace ViewMate.Options.View
{
    internal class PinyinSearchPageController : ControllerBase
    {
        private readonly PluginInfo _pluginInfo;
        private readonly PluginOptionsStore _mainOptionsStore;

        public PinyinSearchPageController(PluginInfo pluginInfo, PluginOptionsStore mainOptionsStore)
            : base(pluginInfo.Id)
        {
            _pluginInfo = pluginInfo;
            _mainOptionsStore = mainOptionsStore;

            PageInfo = new PluginPageInfo
            {
                Name = "PinyinSearch",
                EnableInMainMenu = false,
                DisplayName = Resources.ResourceManager.GetString("PluginOptions_EditorTitle_PinyinSearch",
                    Plugin.Instance.DefaultUICulture),
                MenuIcon = "search",
                IsMainConfigPage = false,
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            IPluginUIView view = new PinyinSearchPageView(_pluginInfo, _mainOptionsStore);
            return Task.FromResult(view);
        }
    }
}
