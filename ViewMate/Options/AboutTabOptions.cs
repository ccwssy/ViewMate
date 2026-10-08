using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;

namespace ViewMate.Options
{
    /// <summary>
    /// 关于 tab 的 ContentData：版本检查开关，外加运行时构建的
    /// 关于列表；此处做了扁平化，使一个 tab 绑定一个可编辑对象，
    /// 而 PluginOptions（唯一的 JSON 容器）保持其四段式结构。
    /// </summary>
    public class AboutTabOptions : EditableOptionsBase
    {
        public override string EditorTitle => "关于";

        [DisplayName("启用版本检查")]
        [Description("Emby 启动 5 分钟后检查 GitHub 最新版本，最多重试 3 次")]
        public bool EnableVersionCheck { get; set; } = false;

        [DisplayName("立即检查更新")]
        [Description("勾选后点击保存，立即检查 GitHub 最新版本（检查完成后自动复位）")]
        [VisibleCondition("EnableVersionCheck", SimpleCondition.IsTrue)]
        public bool TriggerManualCheck { get; set; } = false;

        public GenericItemList VersionInfoList { get; set; } = new GenericItemList();
    }
}
