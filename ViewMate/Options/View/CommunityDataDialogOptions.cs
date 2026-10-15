using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using System.ComponentModel;
using ViewMate.Properties;

namespace ViewMate.Options.View
{
    /// <summary>
    /// 弹窗 2「社区数据配置」（T17 §B.1.2）：共享子开关 + 两把 API key + 说明文字。
    /// 「获取」已在主 tab 直挂（文案「自动补打」，同一个 EnableCommunityFetch 字段），本弹窗不再重复放置。
    /// API 基址不做用户可配（代码常量两域）。
    /// </summary>
    public class CommunityDataDialogOptions : EditableOptionsBase
    {
        public override string EditorTitle => Resources.Community_Dialog_Title;

        public override string EditorDescription => Resources.Community_Dialog_Intro;

        /// <summary>共享子开关：把本地检测结果提交回社区（计划任务，12h 一趟，本版 dry-run）。</summary>
        [DisplayName("共享本机检测结果")]
        [Description("每 12 小时把本地检测到的片头/片尾（不含手动锁定值与内嵌章节映射值）提交回社区库")]
        public bool EnableCommunityShare { get; set; } = false;

        [DisplayName("TheIntroDB API Key")]
        [Description("在 theintrodb.org 官网申请。留空 = 匿名查询（额度较低），且无法提交")]
        public string TheIntroDbApiKey { get; set; } = string.Empty;

        [DisplayName("IntroDB API Key")]
        [Description("在 introdb.app 官网申请。留空 = 匿名查询，且无法提交")]
        public string IntroDbApiKey { get; set; } = string.Empty;

        /// <summary>弹窗内状态条（非 null，避免客户端 spinner 永不结束）。</summary>
        public StatusItem MessageStatus { get; set; } = new StatusItem();
    }
}
