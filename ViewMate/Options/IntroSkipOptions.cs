using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;
using ViewMate.Common;
using ViewMate.Properties;

namespace ViewMate.Options
{
    /// <summary>
    /// 「片头尾跳过」tab（自上而下）：
    ///   标题 → 顶部说明（与开关隔行）→ 启用检测 → 时长配置开关 → 两时长 →
    ///   启用漏集补打 → 社区数据增强 → 自动补打 → 社区数据配置按钮 → 手动标记校正按钮 → 状态行。
    /// 「手动标记校正」打开弹窗 1（剧集标记管理，视图态整体迁到 MarkerManagerDialogOptions），
    /// 本类只保留运行时开关与两个入口按钮 —— 主 tab 零标记管理区字段残留。
    /// 条件显隐全部用官方 VisibleCondition 特性声明，由服务端输出 PropertyConditions；
    /// 客户端 genericedit 的 applyPropertyConditions 消费（见报告行号）。
    /// </summary>
    public class IntroSkipOptions : EditableOptionsBase
    {
        public override string EditorTitle => "片头尾跳过";

        /// <summary>
        /// Emby 的「刷新元数据」会重新探测媒体，Chapters3 被全量替换 →
        /// 插件的片头/片尾标记会暂时消失（手动标记由自愈在下一轮 ≤10 分钟内补回）。
        /// 这句提示给用户看，避免误以为标记丢失而重复手写。
        /// 段落末尾追加一行「不换行空格」（U+00A0）——genericedit 的
        /// formatDescription 把 "\n" 转成 &lt;br /&gt;，纯空白行会被 HTML 折叠，故用 U+00A0 占位，
        /// 在说明与主开关之间撑出一行间距。资源文案本身一字未改（四件套无需变更）。
        /// </summary>
        public override string EditorDescription => Resources.IntroMarker_Page_RefreshMetadataHint + "\n\u00a0";

        [DisplayName("启用片头尾跳过检测")]
        [Description("播放时拖进度条跳过片头，自动写入 IntroSkip marker")]
        [Required]
        public bool EnableIntroSkip { get; set; } = false;

        // ── 时长配置开关（持久化字段；默认折叠）──

        /// <summary>
        /// 时长配置开关。
        /// 原实现是视图态（保存链白名单刻意不含它），导致勾选后保存、切出再切回
        /// 又变回未勾选（旧值只活在当次会话）。现与其余 intro 跳过页配置字段同规格持久化 ——
        /// 保存链写入、打开页面时从 <c>观影助手.json</c> 的 IntroSkipOptions 段读回。
        /// 默认值 false 与「默认关」（README 字段表「时长配置 | 关」）一致，
        /// 老配置里没有该字段时行为不变。
        /// 检测关时本字段在客户端被 VisibleCondition 折叠隐藏，服务端**不再**把值归零 ——
        /// 归零会让客户端随表单带回隐藏值、把持久化状态抹掉（实测）；保持真实值后，
        /// 检测重新打开时时长配置自然就是上次保存的勾选状态。
        /// </summary>
        [DisplayName("时长配置")]
        [Description("展开「最长片头时长 / 最长片尾时长」两个参数（默认折叠）")]
        [VisibleCondition("EnableIntroSkip", SimpleCondition.IsTrue)]
        public bool ShowDurationSettings { get; set; } = false;

        // 仅当「时长配置」开着时显示；检测关的折叠由视图侧把 ShowDurationSettings 归零来保证。
        [DisplayName("最长片头时长 (秒)")]
        [Description("跳转起点在此秒数之内才识别为片头跳过，默认 150")]
        [Required, MinValue(30), MaxValue(600)]
        [VisibleCondition("ShowDurationSettings", SimpleCondition.IsTrue)]
        public int MaxIntroDurationSeconds { get; set; } = IntroSkipDefaults.MaxIntroDurationSeconds;

        [DisplayName("最长片尾时长 (秒)")]
        [Description("默认 180")]
        [Required, MinValue(30), MaxValue(1200)]
        [VisibleCondition("ShowDurationSettings", SimpleCondition.IsTrue)]
        public int MaxCreditsDurationSeconds { get; set; } = IntroSkipDefaults.MaxCreditsDurationSeconds;

        [DisplayName("启用漏集补打")]
        [Description("启动时自动检测缺少片头片尾标记的剧集，从同季已有标记的集复制补打。需要启用片头跳过检测才有数据源")]
        [Required]
        [VisibleCondition("EnableIntroSkip", SimpleCondition.IsTrue)]
        public bool EnableIntroBackfill { get; set; } = false;

        // ── 社区数据增强 ──

        [DisplayName("社区数据增强")]
        [Description("从 TheIntroDB / IntroDB 社区库获取片头片尾数据，并可把本地检测结果共享回社区；需在各媒体库的「元数据下载器」里勾选对应 Provider")]
        [Required]
        public bool EnableCommunityData { get; set; } = false;

        /// <summary>
        /// 「自动补打」：与弹窗 2 的「获取」是**同一个 Options 字段**
        /// （EnableCommunityFetch，UI 文案在主 tab 叫「自动补打」），弹窗 2 不再重复放置。
        /// </summary>
        [DisplayName("自动补打")]
        [Description("刷新元数据时自动从社区查询缺失的片头/片尾并写入标记（仅当本地缺该片段时才查询）")]
        [Required]
        [VisibleCondition("EnableCommunityData", SimpleCondition.IsTrue)]
        public bool EnableCommunityFetch { get; set; } = false;

        [DisplayName("社区数据配置")]
        [Description("配置共享开关与两个社区库的 API Key")]
        [VisibleCondition("EnableCommunityData", SimpleCondition.IsTrue)]
        public ButtonItem EditCommunityDataButton { get; set; } =
            new ButtonItem(Resources.Community_ConfigButton) { CommandId = "EditCommunityData" };

        /// <summary>打开弹窗 1（剧集标记管理：搜索/过滤/三级下拉/集列表/状态卡）。</summary>
        [DisplayName("手动标记校正")]
        [Description("按剧集/季浏览并校正片头片尾标记（含单集编辑、整季应用、清除标记）")]
        public ButtonItem EditMarkerManagerButton { get; set; } =
            new ButtonItem(Resources.IntroMarker_ManagerButton) { CommandId = "EditMarkerManager" };

        /// <summary>
        /// 社区状态行（获取/共享/Key 摘要；社区增强关闭时显示「社区数据未启用」）。
        /// 必须非 null：客户端 genericedit 只在拿到 StatusItem 时才隐藏 spinner（实测教训）。
        /// 视图侧每次构造/保存后由 ApplyCommunityStatus() 覆盖。
        /// </summary>
        public StatusItem CommunityStatus { get; set; } = new StatusItem();
    }
}
