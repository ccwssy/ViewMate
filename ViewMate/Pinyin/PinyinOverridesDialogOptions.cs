using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;

namespace ViewMate.Pinyin
{
    /// <summary>
    /// 多音字配置弹窗的表单数据。
    /// </summary>
    public class PinyinOverridesDialogOptions : EditableObjectBase
    {
        public override string EditorTitle => PinyinOverridesFileService.UiText("PinyinOverrides_EditorTitle");

        /// <summary>弹窗副标题：状态或错误摘要（弹窗模板默认隐藏该行，等价信息由 MessageStatus 呈现）。</summary>
        public override string EditorDescription => MessageStatus == null
            ? string.Empty
            : MessageStatus.Caption + "：" + MessageStatus.StatusText;

        [DisplayName("文件内容")]
        [EditMultiline(18)]
        public string Content { get; set; }

        public ButtonItem RestoreButton { get; set; } = new ButtonItem("恢复原内容") { CommandId = "RestoreOriginal" };

        public ButtonItem CancelButton { get; set; } = new ButtonItem("取消") { CommandId = "CancelEdit" };

        /// <summary>文件状态 / 校验错误：客户端渲染为带图标的信息条，保存失败时承载错误文案。</summary>
        public StatusItem MessageStatus { get; set; } = new StatusItem();
    }
}
