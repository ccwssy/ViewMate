using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;

namespace ViewMate.Options.View
{
    /// <summary>
    /// 剧集标记编辑弹窗的表单数据。
    /// 三个时间字段接受 mm:ss / hh:mm:ss / 纯秒数，长度上限 8（即 hh:mm:ss）。
    /// 片头必须成对填写（两者都留空 = 不写片头）；片尾留空 = 不写入。
    /// 勾选「应用到整季」时覆盖同季全部可播放集（含手动锁定集）；锁定只挡自动逻辑。
    /// </summary>
    public class IntroMarkerDialogOptions : EditableObjectBase
    {
        public override string EditorTitle => "剧集标记";

        public override string EditorDescription =>
            "片头需同时填写开始与结束，两者都留空则不写片头；片尾留空 = 不写入。"
            + "保存后本集会被锁定，自动检测与漏集补打都会跳过。";

        [DisplayName("片头开始")]
        [Description("格式：mm:ss / hh:mm:ss / 秒数；留空 = 不写入")]
        [MaxLength(8)]
        public string IntroStart { get; set; } = string.Empty;

        [DisplayName("片头结束")]
        [Description("格式：mm:ss / hh:mm:ss / 秒数；留空 = 不写入片头")]
        [MaxLength(8)]
        public string IntroEnd { get; set; } = string.Empty;

        [DisplayName("片尾开始")]
        [Description("格式：mm:ss / hh:mm:ss / 秒数；留空 = 不写入")]
        [MaxLength(8)]
        public string CreditsStart { get; set; } = string.Empty;

        // DisplayName 必须是编译期常量，不能写 Resources.xxx，故文案与资源键手工同步；
        // 资源文件仍是运行时解析（host 序列化 EditorItems 时按属性名取 resx 的 DisplayName/Description）。
        [DisplayName("锁定本集（自动检测不再覆盖）")]
        public bool LockEpisode { get; set; } = true;

        // T11：整季应用覆盖同季全部可播放集（含手动锁定集）——文案必须明示覆盖范围。
        // 字面量与资源键 IntroMarker_Dialog_ApplySeason 保持一致（含「本季 N 集」的语义：
        // 弹窗状态行还会用该资源键渲染实际集数）。
        [DisplayName("应用到整季（本季 N 集）：会覆盖这些集的标记（含手动锁定）")]
        public bool ApplyToSeason { get; set; }

        public ButtonItem ClearButton { get; set; } = new ButtonItem("清除标记") { CommandId = "ClearEpisodeMarker" };

        /// <summary>当前集/季摘要与操作结果（客户端渲染为带图标的信息条）。</summary>
        public StatusItem MessageStatus { get; set; } = new StatusItem();
    }
}
