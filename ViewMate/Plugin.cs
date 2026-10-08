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

        // ── IntroSkip（片头尾跳过） ──
        public static ChapterMarkerApi ChapterMarkerApi { get; private set; }
        public static PlaySessionMonitor PlaySessionMonitor { get; private set; }

        // ── PinyinSearch（拼音搜索） ──
        public static PinyinSearchService PinyinSearch { get; private set; }

        // ── PinyinSortName（拼音排序名） ──
        public static PinyinSortNameService PinyinSortName { get; private set; }

        /// <summary>
        /// PinyinSortName 的运行时开关。由配置保存时调用。
        /// 可安全重复调用 —— 内部自行处理创建与销毁。
        /// </summary>
        public static void SetPinyinSortNameEnabled(bool enable)
        {
            if (enable && PinyinSortName == null)
            {
                var logger = Instance?.Logger;
                var libMgr = Instance?.LibraryManager;
                if (logger == null || libMgr == null) return;
                logger.Info("[PinyinSortName] Enabling from config save...");
                PinyinSortName = new PinyinSortNameService(libMgr, logger);
                PinyinSortName.BackfillAll();
            }
            else if (!enable && PinyinSortName != null)
            {
                PinyinSortName.SetEnabled(false);
                PinyinSortName.Dispose();
                PinyinSortName = null;
            }
            else if (enable && PinyinSortName != null)
            {
                // 已启用 —— 需要时重新应用一次
                PinyinSortName.SetEnabled(true);
            }
        }

        // ── IntroBackfill（片头尾回填） ──
        public static IntroBackfillService IntroBackfill { get; private set; }

        // ── 版本检查 ──
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

            DefaultUICulture = new CultureInfo(configurationManager.Configuration.UICulture);

            // ── 初始化 IntroSkip 各组件 ──
            ChapterMarkerApi = new ChapterMarkerApi(libraryManager, itemRepository, Logger);
            PlaySessionMonitor = new PlaySessionMonitor(libraryManager, sessionManager, Logger);
        }

        public void Run() => Initialize();
        public void Dispose()
        {
            PlaySessionMonitor?.Dispose();
            PinyinSearch?.Dispose();
            PinyinSortName?.Dispose();
        }

        private void Initialize()
        {
            var config = Configuration as PluginConfiguration ?? new PluginConfiguration();

            // ── 启动 IntroSkip（若配置中已启用） ──
            if (config.EnableIntroSkip)
            {
                Logger.Info("[IntroSkip] Starting PlaySessionMonitor...");
                PlaySessionMonitor.MaxIntroDurationTicks = TimeSpan.FromSeconds(config.MaxIntroDurationSeconds).Ticks;
                PlaySessionMonitor.MaxCreditsDurationTicks = TimeSpan.FromSeconds(config.MaxCreditsDurationSeconds).Ticks;
                PlaySessionMonitor.Start();            }
            else
            {
                Logger.Info("[IntroSkip] Disabled by configuration");
            }

            // ── 启动 PinyinSearch（若已启用） ──
            if (config.EnablePinyinSearch)
            {
                Logger.Info("[PinyinSearch] Starting PinyinSearchService...");
                PinyinSearch = new PinyinSearchService(LibraryManager, Logger);
                // 延后的后台扫描 —— 非阻塞、分批执行。
                // 避免在性能较弱的 ARM 硬件上造成 SQLite 写锁拥塞。
                PinyinSearch.ProcessAllPendingDeferred();
            }
            else
            {
                Logger.Info("[PinyinSearch] Disabled by configuration");
                PinyinSearch = null;
            }

            // ── 启动 PinyinSortName（若已启用） ──
            if (config.EnablePinyinSortName)
            {
                Logger.Info("[PinyinSortName] Starting PinyinSortNameService...");
                PinyinSortName = new PinyinSortNameService(LibraryManager, Logger);
                PinyinSortName.BackfillAll(startupDelayMs: 60000); // 启动延后 60 秒，给 Emby 启动让路
            }
            else
            {
                Logger.Info("[PinyinSortName] Disabled by configuration");
                PinyinSortName = null;
            }

            // ── 启动 IntroBackfill（若已启用） ──
            if (config.EnableIntroBackfill)
            {
                Logger.Info("[IntroBackfill] Starting IntroBackfillService...");
                var introBackfill = new IntroBackfillService(ChapterMarkerApi, Logger);
                IntroBackfill = introBackfill;
                // 延后的后台扫描 —— 非阻塞、全库读写，
                // 绝不能阻塞 Emby 启动（与 PinyinSearch / PinyinSortName 相同的
                // 60 秒模式）。IntroBackfillService 内部没有自己的延时，
                // 因此这里是唯一的延时 —— 不会出现启动延时叠加。
                Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(60));
                    try { introBackfill.BackfillMissing(); }
                    catch (Exception ex) { Logger.Error("[IntroBackfill] Initial scan failed", ex); }
                });
            }
            else
            {
                Logger.Info("[IntroBackfill] Disabled by configuration");
                IntroBackfill = null;
            }

            // ── 版本检查（延后 5 分钟，最多重试 3 次） ──
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

        public ImageFormat ThumbImageFormat => ImageFormat.Png;
        public override string Description => $"观影助手 v{Assembly.GetExecutingAssembly().GetName().Version} — 拼音搜索、中文子串搜索、词组级多音字校正、片头片尾跳过、漏集补打、WAL checkpoint";
        public override Guid Id => _id;
        public sealed override string Name => "观影助手";
        public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;
        public CultureInfo DefaultUICulture { get; private set; }
        public Stream GetThumbImage()
        {
            var type = typeof(Plugin);
            // 先试新的属性名，不行再逐个回退
            var assembly = type.Assembly;
            // 搜索常见的资源键名
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
        // ── IntroSkip 配置 ──
        public bool EnableIntroSkip { get; set; } = false;
        public int MaxIntroDurationSeconds { get; set; } = IntroSkipDefaults.MaxIntroDurationSeconds;
        public int MaxCreditsDurationSeconds { get; set; } = IntroSkipDefaults.MaxCreditsDurationSeconds;

        // ── PinyinSearch 配置 ──
        public bool EnablePinyinSearch { get; set; } = true;

        // ── PinyinSortName 配置 ──
        public bool EnablePinyinSortName { get; set; } = false;

        // ── IntroBackfill 配置 ──
        public bool EnableIntroBackfill { get; set; } = false;

        // ── 版本检查配置 ──
        public bool EnableVersionCheck { get; set; } = false;
    }
}