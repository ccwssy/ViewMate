using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using ViewMate.Common;
using ViewMate.IntroSkip;
using ViewMate.Options.Store;
using ViewMate.Options.View;
using ViewMate.Pinyin;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
#nullable disable
namespace ViewMate
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasUIPages, IServerEntryPoint, IHasThumbImage
    {
        private List<IPluginUIPageController> _pages;
        public readonly PluginOptionsStore MainOptionsStore;
        public static Plugin Instance { get; private set; }

        private readonly Guid _id = new Guid("63c322b7-a371-41a3-b11f-04f8418b37d8");
        public readonly ILogger Logger;
        public readonly IApplicationHost ApplicationHost;
        public new readonly IApplicationPaths ApplicationPaths;
        public readonly IServerConfigurationManager ConfigurationManager;
        public readonly ILibraryManager LibraryManager;
        private readonly IXmlSerializer _xmlSerializer;
        private readonly IItemRepository _itemRepository;

        // ── IntroSkip ──
        public static ChapterMarkerApi ChapterMarkerApi { get; private set; }
        public static PlaySessionMonitor PlaySessionMonitor { get; private set; }

        // ── PinyinSearch ──
        public static PinyinSearchService PinyinSearch { get; private set; }

        // ── IntroBackfill ──
        public static IntroBackfillService IntroBackfill { get; private set; }

        public Plugin(IApplicationHost applicationHost, IApplicationPaths applicationPaths, ILogManager logManager,
            IServerConfigurationManager configurationManager,
            ILibraryManager libraryManager, IXmlSerializer xmlSerializer, IItemRepository itemRepository,
            ISessionManager sessionManager)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            Logger = logManager.GetLogger(Name);
            Logger.Info("观影助手 Start");
            ApplicationHost = applicationHost;
            ApplicationPaths = applicationPaths;
            ConfigurationManager = configurationManager;

            MainOptionsStore = new PluginOptionsStore(applicationHost, Logger, Name);

            LibraryManager = libraryManager;
            _xmlSerializer = xmlSerializer;
            _itemRepository = itemRepository;

            DefaultUICulture = new CultureInfo(configurationManager.Configuration.UICulture);

            // ── Initialise IntroSkip components ──
            ChapterMarkerApi = new ChapterMarkerApi(libraryManager, itemRepository, Logger);
            PlaySessionMonitor = new PlaySessionMonitor(libraryManager, sessionManager, Logger);
        }

        public void Run() => Initialize();
        public void Dispose()
        {
            PlaySessionMonitor?.Dispose();
            PinyinSearch?.Dispose();
        }

        private void Initialize()
        {
            var config = Configuration as PluginConfiguration ?? new PluginConfiguration();

            // ── Start IntroSkip (if enabled in config) ──
            if (config.EnableIntroSkip)
            {
                Logger.Info("[IntroSkip] Starting PlaySessionMonitor...");
                PlaySessionMonitor.MaxIntroDurationTicks = TimeSpan.FromSeconds(config.MaxIntroDurationSeconds).Ticks;
                PlaySessionMonitor.MaxCreditsDurationTicks = TimeSpan.FromSeconds(config.MaxCreditsDurationSeconds).Ticks;
                PlaySessionMonitor.Start();
            }
            else
            {
                Logger.Info("[IntroSkip] Disabled by configuration");
            }

            // ── Start PinyinSearch (if enabled) ──
            if (config.EnablePinyinSearch)
            {
                Logger.Info("[PinyinSearch] Starting PinyinSearchService...");
                PinyinSearch = new PinyinSearchService(LibraryManager, Logger);
                try { PinyinSearch.ProcessAllPending(); }
                catch (Exception ex) { Logger.Error("[PinyinSearch] Initial scan failed", ex); }
            }
            else
            {
                Logger.Info("[PinyinSearch] Disabled by configuration");
                PinyinSearch = null;
            }

            // ── Start IntroBackfill (if enabled) ──
            if (config.EnableIntroBackfill)
            {
                Logger.Info("[IntroBackfill] Starting IntroBackfillService...");
                IntroBackfill = new IntroBackfillService(ChapterMarkerApi, Logger);
                try { IntroBackfill.BackfillMissing(); }
                catch (Exception ex) { Logger.Error("[IntroBackfill] Initial scan failed", ex); }
            }
            else
            {
                Logger.Info("[IntroBackfill] Disabled by configuration");
                IntroBackfill = null;
            }
        }

        public override void OnUninstalling()
        {
            base.OnUninstalling();
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;
        public override string Description => "观影助手 v1.2.9.0 — 拼音搜索、FTS5 拼音注入、片头片尾跳过、漏集补打";
        public override Guid Id => _id;
        public sealed override string Name => "观影助手";
        public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;
        public CultureInfo DefaultUICulture { get; private set; }
        public Stream GetThumbImage()
        {
            var assembly = typeof(Plugin).Assembly;
            return assembly.GetManifestResourceStream("ViewMate.Properties.thumb.png");
        }

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers
        {
            get
            {
                if (_pages == null)
                {
                    PluginInfo basePluginInfo = base.GetPluginInfo();
                    _pages = new List<IPluginUIPageController>
                    {
                        new MainPageController(basePluginInfo, MainOptionsStore)
                    };
                }
                return _pages.AsReadOnly();
            }
        }
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        // ── IntroSkip configuration ──
        public bool EnableIntroSkip { get; set; } = false;
        public int MaxIntroDurationSeconds { get; set; } = 150;
        public int MaxCreditsDurationSeconds { get; set; } = 180;

        // ── PinyinSearch configuration ──
        public bool EnablePinyinSearch { get; set; } = true;

        // ── IntroBackfill configuration ──
        public bool EnableIntroBackfill { get; set; } = false;
    }
}
