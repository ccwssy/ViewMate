using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using ViewMate.Options.Store;
using ViewMate.Options.UIBaseClasses.Views;
using System.Threading.Tasks;

namespace ViewMate.Options.View
{
    /// <summary>
    /// 片头尾跳过 tab。ContentData 只承载 IntroSkipOptions 段；保存时会重新加载
    /// 整个 JSON 容器，替换自己那一段后写回，
    /// 然后同步 PluginConfiguration（XML）中对应的字段。
    /// </summary>
    internal class IntroSkipPageView : PluginPageView
    {
        private readonly PluginOptionsStore _store;

        public IntroSkipPageView(PluginInfo pluginInfo, PluginOptionsStore store)
            : base(pluginInfo.Id)
        {
            _store = store;

            var options = store.GetOptions();
            // 运行时开关以 PluginConfiguration（XML）为准。
            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            options.IntroSkipOptions.EnableIntroSkip = config.EnableIntroSkip;
            options.IntroSkipOptions.MaxIntroDurationSeconds = config.MaxIntroDurationSeconds;
            options.IntroSkipOptions.MaxCreditsDurationSeconds = config.MaxCreditsDurationSeconds;
            options.IntroSkipOptions.EnableIntroBackfill = config.EnableIntroBackfill;

            ContentData = options.IntroSkipOptions;
        }

        private IntroSkipOptions Options => (IntroSkipOptions)ContentData;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var options = _store.ReloadOptions();
            options.IntroSkipOptions = Options;
            _store.SetOptions(options);

            // 把取值同步到 PluginConfiguration
            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            config.EnableIntroSkip = Options.EnableIntroSkip;
            config.MaxIntroDurationSeconds = Options.MaxIntroDurationSeconds;
            config.MaxCreditsDurationSeconds = Options.MaxCreditsDurationSeconds;
            config.EnableIntroBackfill = Options.EnableIntroBackfill;
            Plugin.Instance.UpdateConfiguration(config);

            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
