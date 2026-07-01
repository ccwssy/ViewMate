using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;

namespace ViewMate.IntroSkip
{
    public class IntroBackfillOptions : EditableOptionsBase
    {
        public override string EditorTitle => "漏集补打";

        [DisplayName("启用漏集补打")]
        [Description("启动时自动检测缺少片头片尾标记的剧集，从同季已有标记的集复制补打")]
        [Required]
        public bool EnableIntroBackfill { get; set; } = false;
    }
}
