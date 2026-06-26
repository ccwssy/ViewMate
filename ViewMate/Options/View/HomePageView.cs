using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using ViewMate.Options.Store;
using ViewMate.Options.UIBaseClasses.Views;
using System.Threading.Tasks;

namespace ViewMate.Options.View
{
    internal class HomePageView : PluginPageView
    {
        private readonly PluginInfo _pluginInfo;
        private readonly PluginOptionsStore _store;

        public HomePageView(PluginInfo pluginInfo, PluginOptionsStore store)
            : base(pluginInfo.Id)
        {
            _pluginInfo = pluginInfo;
            _store = store;
            ContentData = store.GetOptions();

            PluginOptions.Initialize();
            PluginOptions.IntroSkipOptions.Initialize();
            PluginOptions.PinyinOptions.Initialize();
            PluginOptions.AboutOptions.Initialize();

            // Load values from PluginConfiguration
            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            PluginOptions.IntroSkipOptions.EnableIntroSkip = config.EnableIntroSkip;
            PluginOptions.IntroSkipOptions.MaxIntroDurationSeconds = config.MaxIntroDurationSeconds;
            PluginOptions.IntroSkipOptions.MaxCreditsDurationSeconds = config.MaxCreditsDurationSeconds;
            PluginOptions.PinyinOptions.EnablePinyinSearch = config.EnablePinyinSearch;
            PluginOptions.IntroSkipOptions.EnableIntroBackfill = config.EnableIntroBackfill;
            PluginOptions.VersionCheckOptions.EnableVersionCheck = config.EnableVersionCheck;
        }

        public PluginOptions PluginOptions => ContentData as PluginOptions;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            _store.SetOptions(PluginOptions);

            // Sync values to PluginConfiguration
            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            config.EnableIntroSkip = PluginOptions.IntroSkipOptions.EnableIntroSkip;
            config.MaxIntroDurationSeconds = PluginOptions.IntroSkipOptions.MaxIntroDurationSeconds;
            config.MaxCreditsDurationSeconds = PluginOptions.IntroSkipOptions.MaxCreditsDurationSeconds;
            config.EnablePinyinSearch = PluginOptions.PinyinOptions.EnablePinyinSearch;
            config.EnableIntroBackfill = PluginOptions.IntroSkipOptions.EnableIntroBackfill;
            config.EnableVersionCheck = PluginOptions.VersionCheckOptions.EnableVersionCheck;
            Plugin.Instance.UpdateConfiguration(config);

            // ── Manual version check ──
            if (PluginOptions.VersionCheckOptions.TriggerManualCheck)
            {
                PluginOptions.VersionCheckOptions.TriggerManualCheck = false;
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
