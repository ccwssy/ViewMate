using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using ViewMate.Options.Store;
using ViewMate.Options.UIBaseClasses.Views;
using System.Threading.Tasks;

namespace ViewMate.Options.View
{
    /// <summary>
    /// 片头尾跳过 tab. ContentData carries only the IntroSkipOptions section; save
    /// reloads the whole JSON container, replaces its own section, writes it back
    /// and then syncs the matching PluginConfiguration (XML) fields.
    /// </summary>
    internal class IntroSkipPageView : PluginPageView
    {
        private readonly PluginOptionsStore _store;

        public IntroSkipPageView(PluginInfo pluginInfo, PluginOptionsStore store)
            : base(pluginInfo.Id)
        {
            _store = store;

            var options = store.GetOptions();
            // PluginConfiguration (XML) is authoritative for the runtime switches.
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

            // Sync values to PluginConfiguration
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
