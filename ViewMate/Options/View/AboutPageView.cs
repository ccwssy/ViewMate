using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using ViewMate.Options.Store;
using ViewMate.Options.UIBaseClasses.Views;
using System.Threading.Tasks;

namespace ViewMate.Options.View
{
    /// <summary>
    /// 关于 tab。ContentData 是扁平化后的 AboutTabOptions；保存时会重新加载
    /// 整个 JSON 容器，把版本检查字段与关于列表写回各自的段，
    /// 然后同步 PluginConfiguration（XML）。
    /// </summary>
    internal class AboutPageView : PluginPageView
    {
        private readonly PluginOptionsStore _store;

        public AboutPageView(PluginInfo pluginInfo, PluginOptionsStore store)
            : base(pluginInfo.Id)
        {
            _store = store;

            var options = store.GetOptions();
            options.AboutOptions.Initialize();

            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            ContentData = new AboutTabOptions
            {
                EnableVersionCheck = config.EnableVersionCheck,
                TriggerManualCheck = options.VersionCheckOptions.TriggerManualCheck,
                VersionInfoList = options.AboutOptions.VersionInfoList,
            };
        }

        private AboutTabOptions Options => (AboutTabOptions)ContentData;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var options = _store.ReloadOptions();
            options.VersionCheckOptions.EnableVersionCheck = Options.EnableVersionCheck;
            options.VersionCheckOptions.TriggerManualCheck = Options.TriggerManualCheck;
            options.AboutOptions.VersionInfoList = Options.VersionInfoList;
            _store.SetOptions(options);

            // 把取值同步到 PluginConfiguration
            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            config.EnableVersionCheck = Options.EnableVersionCheck;
            Plugin.Instance.UpdateConfiguration(config);

            // ── 手动版本检查 ──
            if (Options.TriggerManualCheck)
            {
                Options.TriggerManualCheck = false;
                Plugin.Instance.Logger.Info("[VersionCheck] Manual check triggered");
                Task.Run(async () =>
                {
                    await Plugin.CheckForUpdatesAsync(maxRetries: 3, retryDelayMs: 10000);
                    Plugin.Instance.Logger.Info("[VersionCheck] Manual check complete");
                });
            }

            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
