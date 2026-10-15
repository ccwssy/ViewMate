using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Serialization;
using ViewMate.Properties;

namespace ViewMate.Options.View
{
    /// <summary>
    /// 弹窗 1「手动标记校正」（剧集标记管理）的选项容器（T17 §B.1.3）。
    /// 字段与语义**整体迁自** T15 的 IntroSkipOptions 标记管理区块（搜索 / 两个过滤 /
    /// 三级联动下拉 / 集列表 / 状态卡），只是换了容器 —— 数据链路
    /// （<see cref="ViewMate.IntroSkip.IntroMarkerAdminService"/> 的查询与 Summarize）一字未动。
    /// 全是视图态：关闭弹窗即丢，保存链不写盘。
    /// </summary>
    public class MarkerManagerDialogOptions : EditableOptionsBase
    {
        public override string EditorTitle => Resources.IntroMarker_ManagerTitle;

        [DisplayName("剧名搜索")]
        [Description("按剧名过滤「2 剧集」列表（回车或失焦触发刷新）")]
        [AutoPostBack("RefreshSeries", nameof(MarkerSearchText))]
        public string MarkerSearchText { get; set; } = string.Empty;

        [DisplayName("只看标记异常")]
        [Description("片头缺失、片头 ≥ 5 分钟或片尾缺失的剧集")]
        [AutoPostBack("RefreshSeries", nameof(MarkerOnlyAnomalous))]
        public bool MarkerOnlyAnomalous { get; set; }

        [DisplayName("只看最近播放")]
        [Description("最近 30 天内有播放记录的剧集")]
        [AutoPostBack("RefreshSeries", nameof(MarkerOnlyRecentlyPlayed))]
        public bool MarkerOnlyRecentlyPlayed { get; set; }

        [DisplayName("1 媒体库")]
        [SelectItemsSource(nameof(MarkerLibraryChoices))]
        [AutoPostBack("RefreshSeries", nameof(MarkerLibrary))]
        public string MarkerLibrary { get; set; } = "all";

        [Browsable(false), XmlIgnore]
        public IEnumerable<EditorSelectOption> MarkerLibraryChoices { get; set; } = new List<EditorSelectOption>();

        [DisplayName("2 剧集")]
        [SelectItemsSource(nameof(MarkerSeriesChoices))]
        [AutoPostBack("RefreshSeasons", nameof(MarkerSeries))]
        public string MarkerSeries { get; set; } = string.Empty;

        [Browsable(false), XmlIgnore]
        public IEnumerable<EditorSelectOption> MarkerSeriesChoices { get; set; } = new List<EditorSelectOption>();

        [DisplayName("3 季")]
        [SelectItemsSource(nameof(MarkerSeasonChoices))]
        [AutoPostBack("RefreshEpisodes", nameof(MarkerSeason))]
        public string MarkerSeason { get; set; } = string.Empty;

        [Browsable(false), XmlIgnore]
        public IEnumerable<EditorSelectOption> MarkerSeasonChoices { get; set; } = new List<EditorSelectOption>();

        [DisplayName("4 单集标记")]
        public GenericItemList MarkerEpisodeRows { get; set; } = new GenericItemList();

        /// <summary>
        /// 弹窗内操作结果提示。必须保持非 null：客户端 genericedit 只在拿到该值时才会
        /// 按状态切换图标/转圈（Status != InProgress 时隐藏 spinner）。
        /// </summary>
        public StatusItem MarkerMessageStatus { get; set; } = new StatusItem();
    }
}
