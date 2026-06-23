using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;

namespace ViewMate.Options
{
    public class IntroSkipOptions : EditableOptionsBase
    {
        public override string EditorTitle => "检测参数";

        [DisplayName("最长片头时长 (秒)")]
        [Description("跳转起点在此秒数之内才识别为片头跳过，默认 150")]
        [Required, MinValue(30), MaxValue(600)]
        public int MaxIntroDurationSeconds { get; set; } = 150;

        [DisplayName("最长片尾时长 (秒)")]
        [Description("默认 180")]
        [Required, MinValue(30), MaxValue(1200)]
        public int MaxCreditsDurationSeconds { get; set; } = 180;

        [DisplayName("启用漏集补打")]
        [Description("启动时自动检测缺少片头片尾标记的剧集，从同季已有标记的集复制补打。需要启用片头跳过检测才有数据源")]
        [Required]
        public bool EnableIntroBackfill { get; set; } = false;

        public void Initialize()
        {
        }
    }
}
