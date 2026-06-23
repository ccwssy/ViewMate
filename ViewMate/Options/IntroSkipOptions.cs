using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;

namespace ViewMate.Options
{
    public class IntroSkipOptions : EditableOptionsBase
    {
        public override string EditorTitle => "片头片尾跳过";

        [DisplayName("启用片头跳过检测")]
        [Description("播放时拖进度条跳过片头，自动写入 IntroSkip marker")]
        [Required]
        public bool EnableIntroSkip { get; set; } = false;

        [DisplayName("最长片头时长 (秒)")]
        [Description("跳转终点超过此秒数则忽略，默认 150")]
        [Required, MinValue(30), MaxValue(600)]
        public int MaxIntroDurationSeconds { get; set; } = 150;

        [DisplayName("最长片尾时长 (秒)")]
        [Description("默认 360")]
        [Required, MinValue(30), MaxValue(1200)]
        public int MaxCreditsDurationSeconds { get; set; } = 360;

        public void Initialize()
        {
        }
    }
}
