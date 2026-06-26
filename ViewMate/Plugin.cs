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
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
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

        // ── Version check ──
        public static string LatestVersion { get; private set; }
        public static bool HasUpdate { get; private set; }
        public static bool VersionCheckFailed { get; private set; }
        public static string VersionCheckStatus { get; private set; } = "";
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static bool _httpInitialized;
        private static readonly object _versionLock = new object();
        private static void InitHttpClient()
        {
            if (!_httpInitialized)
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ViewMate/1.0");
                _httpInitialized = true;
            }
        }

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
                // Deferred background scan — non-blocking, batched.
                // Prevents SQLite write-lock congestion on slow ARM hardware.
                PinyinSearch.ProcessAllPendingDeferred();
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

            // ── Version check (deferred 5 min, max 3 retries) ──
            bool versionCheckEnabled = config.EnableVersionCheck;
            if (versionCheckEnabled)
            {
                VersionCheckStatus = ""; // 未开始检查
                Instance?.Logger?.Info("[VersionCheck] Will check in 5 minutes...");
                Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(5));
                    await CheckForUpdatesAsync(maxRetries: 3, retryDelayMs: 10000);
                });
            }
            else
            {
                Instance?.Logger?.Info("[VersionCheck] Disabled by configuration");
                VersionCheckStatus = "已禁用";
            }
        }

        public static async Task CheckForUpdatesAsync(int maxRetries = 3, int retryDelayMs = 10000)
        {
            lock (_versionLock)
            {
                VersionCheckFailed = false;
                VersionCheckStatus = "检查中…";
            }

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    InitHttpClient();
                    var response = await _httpClient.GetStringAsync(
                        "https://api.github.com/repos/ccwssy/ViewMate/releases/latest");
                    var json = System.Text.Json.JsonDocument.Parse(response);
                    var tagName = json.RootElement.GetProperty("tag_name").GetString();

                    lock (_versionLock)
                    {
                        LatestVersion = tagName?.TrimStart('v') ?? "unknown";
                        HasUpdate = Version.TryParse(LatestVersion, out var latestVer)
                            && CurrentVersion != null
                            && latestVer > CurrentVersion;
                        VersionCheckFailed = false;
                        VersionCheckStatus = HasUpdate
                            ? $"v{LatestVersion} ⬆ 有更新"
                            : $"v{LatestVersion} ✅ 已是最新";
                    }

                    if (HasUpdate)
                        Instance?.Logger?.Info($"[VersionCheck] New version available: {tagName} (current: v{CurrentVersion})");
                    else
                        Instance?.Logger?.Info($"[VersionCheck] Up-to-date: v{LatestVersion}");
                    return;
                }
                catch (Exception ex)
                {
                    Instance?.Logger?.Info($"[VersionCheck] Attempt {attempt}/{maxRetries} failed: {ex.Message}");
                    if (attempt < maxRetries)
                        await Task.Delay(retryDelayMs);
                }
            }

            lock (_versionLock)
            {
                VersionCheckFailed = true;
                VersionCheckStatus = $"❌ 检查失败";
            }
            Instance?.Logger?.Info($"[VersionCheck] All {maxRetries} attempts failed — giving up");
        }

        public override void OnUninstalling()
        {
            base.OnUninstalling();
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;
        public override string Description => "观影助手 v1.2.13.2 — 拼音搜索、中文子串搜索、词组级多音字校正、片头片尾跳过、漏集补打、WAL checkpoint";
        public override Guid Id => _id;
        public sealed override string Name => "观影助手";
        public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;
        public CultureInfo DefaultUICulture { get; private set; }
        public Stream GetThumbImage()
        {
            var type = typeof(Plugin);
            // Try the new property name first, then fallbacks
            var assembly = type.Assembly;
            // Search common resource names
            var names = assembly.GetManifestResourceNames();
            foreach (var n in names)
            {
                if (n.EndsWith("thumb.png", StringComparison.OrdinalIgnoreCase))
                    return assembly.GetManifestResourceStream(n);
            }
            return null;
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

        // ── Version check configuration ──
        public bool EnableVersionCheck { get; set; } = false;
    }
}