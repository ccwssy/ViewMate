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
            PluginOptions.ModOptions.Initialize();
            PluginOptions.NetworkOptions.Initialize();
            PluginOptions.AboutOptions.Initialize();
            PluginOptions.IntroSkipOptions.Initialize();

            // Load IntroSkip values from PluginConfiguration
            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            PluginOptions.IntroSkipOptions.EnableIntroSkip = config.EnableIntroSkip;
            PluginOptions.IntroSkipOptions.MaxIntroDurationSeconds = config.MaxIntroDurationSeconds;
            PluginOptions.IntroSkipOptions.MaxCreditsDurationSeconds = config.MaxCreditsDurationSeconds;
        }

        public PluginOptions PluginOptions => ContentData as PluginOptions;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            switch (commandId)
            {
                case "DisclaimerDialog":
                    var disclaimerDialog = new DisclaimerDialogView(_pluginInfo);
                    return Task.FromResult<IPluginUIView>(disclaimerDialog);
            }

            return base.RunCommand(itemId, commandId, data);
        }

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            if (ContentData is PluginOptions options)
            {
                //options.GeneralOptions.ValidateOrThrow();
                options.NetworkOptions.ValidateOrThrow();
            }

            _store.SetOptions(PluginOptions);

            // Sync IntroSkip values to PluginConfiguration
            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            config.EnableIntroSkip = PluginOptions.IntroSkipOptions.EnableIntroSkip;
            config.MaxIntroDurationSeconds = PluginOptions.IntroSkipOptions.MaxIntroDurationSeconds;
            config.MaxCreditsDurationSeconds = PluginOptions.IntroSkipOptions.MaxCreditsDurationSeconds;
            Plugin.Instance.UpdateConfiguration(config);

            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
