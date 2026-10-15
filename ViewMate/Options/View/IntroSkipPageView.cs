using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using System;
using System.Text.Json;
using System.Threading.Tasks;
using ViewMate.Community;
using ViewMate.IntroSkip;
using ViewMate.Options.Store;
using ViewMate.Options.UIBaseClasses.Views;
using ViewMate.Properties;

namespace ViewMate.Options.View
{
    /// <summary>
    /// 「片头尾跳过」tab（T17 §B.1.1 第三版）：只承载运行时开关、两个入口按钮与社区状态行；
    /// 剧集标记管理整体迁入弹窗 1（<see cref="MarkerManagerDialogView"/>），
    /// 本页零标记管理区字段残留。
    /// 条件显隐（R1/R3/R4）由 <see cref="IntroSkipOptions"/> 的 VisibleCondition 声明，
    /// 本视图只负责「R1 连同时长两字段一起折叠」所需的视图态归零。
    /// </summary>
    internal class IntroSkipPageView : PluginPageView, IMarkerHostView
    {
        private readonly PluginInfo _pluginInfo;
        private readonly PluginOptionsStore _store;

        private string _saveNotice;

        /// <summary>
        /// 弹窗 1 需要它做「媒体库 / 剧集 / 季 / 集」查询；服务本身无状态、可安全共享。
        /// </summary>
        private readonly IntroMarkerAdminService _admin;

        public IntroSkipPageView(PluginInfo pluginInfo, PluginOptionsStore store, bool deferRebuild = false)
            : base(pluginInfo.Id)
        {
            _pluginInfo = pluginInfo;
            _store = store;

            var options = store.GetOptions();
            // 运行时开关以 PluginConfiguration（XML）为准。
            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            options.IntroSkipOptions.EnableIntroSkip = config.EnableIntroSkip;
            options.IntroSkipOptions.MaxIntroDurationSeconds = config.MaxIntroDurationSeconds;
            options.IntroSkipOptions.MaxCreditsDurationSeconds = config.MaxCreditsDurationSeconds;
            options.IntroSkipOptions.EnableIntroBackfill = config.EnableIntroBackfill;
            options.IntroSkipOptions.EnableCommunityData = config.EnableCommunityData;
            options.IntroSkipOptions.EnableCommunityFetch = config.EnableCommunityFetch;

            ContentData = options.IntroSkipOptions;

            var plugin = Plugin.Instance;
            _admin = new IntroMarkerAdminService(
                plugin.LibraryManager,
                plugin.ItemRepository,
                plugin.UserDataManager,
                plugin.UserManager,
                plugin.Logger);

            NormalizeViewState();
            ApplyCommunityStatus();
        }

        private IntroSkipOptions Options => (IntroSkipOptions)ContentData;

        /// <summary>
        /// T16 §2.2（F2）：页头说明为了与主开关隔行，在 <see cref="IntroSkipOptions.EditorDescription"/>
        /// 末尾追加了一个 U+00A0 占位行（仅用于表单顶部渲染）。SubCaption 是页面副标题，
        /// 复用它会把那个空白行带过去，这里回退成不带占位的纯文案（内容一字未改）；
        /// 保存成功后显示回执（与拼音页同一机制）。
        /// </summary>
        public override string SubCaption => string.IsNullOrEmpty(_saveNotice)
            ? Resources.IntroMarker_Page_RefreshMetadataHint
            : _saveNotice;

        // ── IMarkerHostView（兼容旧容器路径：页面直接承载列表时回执走这里） ──

        public IPluginUIView HostView => this;

        public IPluginUIView CreateMarkersRefreshedView(string notice)
        {
            var view = new IntroSkipPageView(_pluginInfo, _store);
            view._saveNotice = notice;
            return view;
        }

        /// <summary>弹窗 2 保存成功后回主 tab：重建视图以刷新状态行，并带上保存回执。</summary>
        internal IntroSkipPageView CreateConfigSavedView(string notice)
        {
            var view = new IntroSkipPageView(_pluginInfo, _store);
            view._saveNotice = notice;
            return view;
        }

        // ── 命令路由 ──

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            // 页面级保存/返回必须交给基类：宿主只有拿到 null 才会转去调 OnSaveCommand。
            if (string.Equals(commandId, "PageSave", StringComparison.Ordinal)
                || string.Equals(commandId, "PageBack", StringComparison.Ordinal))
            {
                return base.RunCommand(itemId, commandId, data);
            }

            ApplyFormData(data);

            if (string.Equals(commandId, "EditMarkerManager", StringComparison.Ordinal))
            {
                // 弹窗 1：剧集标记管理（搜索 / 过滤 / 三级下拉 / 集列表 / 状态卡）。
                return Task.FromResult<IPluginUIView>(
                    new MarkerManagerDialogView(PluginId, _pluginInfo, _store, _admin, this));
            }

            if (string.Equals(commandId, "EditCommunityData", StringComparison.Ordinal))
            {
                // 弹窗 2：社区数据配置（共享开关 + 两把 key + 说明）。
                return Task.FromResult<IPluginUIView>(
                    new CommunityDataDialogView(PluginId, _store, this));
            }

            return Task.FromResult<IPluginUIView>(this);
        }

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            ApplyFormData(data);

            var options = _store.ReloadOptions();

            // 只把运行时开关写回存储：两个入口按钮/状态行/时长配置开关都是渲染态或视图态，
            // 不进 JSON 配置（ShowDurationSettings 属视图态，白名单刻意不含它）。
            var clean = new IntroSkipOptions
            {
                EnableIntroSkip = Options.EnableIntroSkip,
                MaxIntroDurationSeconds = Options.MaxIntroDurationSeconds,
                MaxCreditsDurationSeconds = Options.MaxCreditsDurationSeconds,
                EnableIntroBackfill = Options.EnableIntroBackfill,
                EnableCommunityData = Options.EnableCommunityData,
                EnableCommunityFetch = Options.EnableCommunityFetch,
                // T9 起状态条必须非 null（否则客户端 spinner 永不结束），落盘前清掉，
                // 写完立刻由 ApplyCommunityStatus() 恢复。
                CommunityStatus = null,
            };

            Options.CommunityStatus = null;
            options.IntroSkipOptions = clean;
            _store.SetOptions(options);

            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            config.EnableIntroSkip = clean.EnableIntroSkip;
            config.MaxIntroDurationSeconds = clean.MaxIntroDurationSeconds;
            config.MaxCreditsDurationSeconds = clean.MaxCreditsDurationSeconds;
            config.EnableIntroBackfill = clean.EnableIntroBackfill;
            config.EnableCommunityData = clean.EnableCommunityData;
            config.EnableCommunityFetch = clean.EnableCommunityFetch;
            Plugin.Instance.UpdateConfiguration(config);

            // 状态行读的是 PluginConfiguration（XML，运行时权威），必须等 UpdateConfiguration
            // 之后再刷新 —— 否则刚保存的开关要等下一次渲染才反映到状态行（自证时实测到这个顺序缺陷）。
            ApplyCommunityStatus();

            return base.OnSaveCommand(itemId, commandId, data);
        }

        // ── 视图态与状态行 ──

        /// <summary>
        /// R1：检测关 → 时长配置开关归零，让两时长字段一并折叠（客户端 applyPropertyConditions
        /// 只看 ShowDurationSettings 一条条件）。检测开着时保留会话内值（视图态）。
        /// </summary>
        private void NormalizeViewState()
        {
            if (!Options.EnableIntroSkip)
            {
                Options.ShowDurationSettings = false;
            }
        }

        /// <summary>
        /// 第 11 行状态行（§B.1.1 / R3）：社区增强关闭时显示「社区数据未启用」，
        /// 开启时显示「获取：开/关 ｜ 共享：开/关 ｜ Key：已配置/未配置」。
        /// 必须非 null（客户端 spinner）。
        /// </summary>
        private void ApplyCommunityStatus()
        {
            if (!CommunityOptions.DataEnabled)
            {
                Options.CommunityStatus = new StatusItem(
                    Resources.Community_StatusTitle,
                    Resources.Community_StatusDisabled,
                    ItemStatus.None);
                return;
            }

            var summary = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Resources.Community_StatusSummary,
                Options.EnableCommunityFetch ? Resources.Community_State_On : Resources.Community_State_Off,
                CommunityOptions.ShareEnabled ? Resources.Community_State_On : Resources.Community_State_Off,
                DescribeKeys());

            Options.CommunityStatus = new StatusItem(
                Resources.Community_StatusTitle,
                summary,
                ItemStatus.Succeeded);
        }

        private static string DescribeKeys()
        {
            var theIntroDb = string.IsNullOrWhiteSpace(CommunityOptions.TheIntroDbKey)
                ? Resources.Community_Key_NotSet
                : Resources.Community_Key_Set;
            var introDb = string.IsNullOrWhiteSpace(CommunityOptions.IntroDbKey)
                ? Resources.Community_Key_NotSet
                : Resources.Community_Key_Set;
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, "TheIntroDB {0} / IntroDB {1}", theIntroDb, introDb);
        }

        /// <summary>把提交上来的表单值写回选项对象（postback 后用户已选值不丢）。</summary>
        private void ApplyFormData(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;

            try
            {
                using (var document = JsonDocument.Parse(data))
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return;

                    Options.EnableIntroSkip = FormDataReader.ReadBool(root, nameof(Options.EnableIntroSkip), Options.EnableIntroSkip);
                    Options.ShowDurationSettings = FormDataReader.ReadBool(root, nameof(Options.ShowDurationSettings), Options.ShowDurationSettings);
                    Options.EnableIntroBackfill = FormDataReader.ReadBool(root, nameof(Options.EnableIntroBackfill), Options.EnableIntroBackfill);
                    Options.EnableCommunityData = FormDataReader.ReadBool(root, nameof(Options.EnableCommunityData), Options.EnableCommunityData);
                    Options.EnableCommunityFetch = FormDataReader.ReadBool(root, nameof(Options.EnableCommunityFetch), Options.EnableCommunityFetch);
                }
            }
            catch (JsonException)
            {
                // 提交内容不是 JSON：保持当前选项，不阻断命令。
            }

            NormalizeViewState();
        }
    }
}
