using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;

namespace ViewMate.Options
{
    public class VersionCheckSubOptions : EditableOptionsBase
    {
        public override string EditorTitle => "版本更新检查";

        [DisplayName("启用版本检查")]
        [Description("Emby 启动 5 分钟后检查 GitHub 最新版本，最多重试 3 次")]
        public bool EnableVersionCheck { get; set; } = true;

        [DisplayName("立即检查更新")]
        [Description("勾选后点击保存，立即检查 GitHub 最新版本（检查完成后自动复位）")]
        public bool TriggerManualCheck { get; set; } = false;
    }
}
