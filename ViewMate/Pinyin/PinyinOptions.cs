using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;

namespace ViewMate.Pinyin
{
    public class PinyinOptions : EditableOptionsBase
    {
        public override string EditorTitle => "拼音搜索";

        // 页头说明堆清空 —— 不覆写 EditorDescription（基类默认 null，
        // genericedit 的 renderForm 只在 root.Description 非空时渲染 ge-section-description 段落）。
        // 原先挤在页头的「语义说明 + 文件状态行」现在分别由按钮下方的 OverridesNotice
        // 与列表末尾的 MessageStatus 承载；两个开关及其字段说明一字未动、位置未动。

        [DisplayName("启用拼音搜索")]
        [Description("自动为新入库的中文媒体生成拼音索引，支持拼音搜索")]
        [Required]
        public bool EnablePinyinSearch { get; set; } = true;

        [DisplayName("拼音排序名")]
        [Description("将中文媒体的排序名替换为拼音首字母（如「功夫」→「GF」），字母排序区 A-Z 正常显示")]
        [Required]
        [VisibleCondition("EnablePinyinSearch", SimpleCondition.IsTrue)]
        public bool EnablePinyinSortName { get; set; } = false;

        // 按钮文案改「编辑多音字列表」——原名「编辑文件」不点开看不出改的是什么文件。
        // 文案走资源键 PinyinOverrides_EditButton（四件套已同步），命令链路不变。
        [DisplayName("多音字配置")]
        public ButtonItem EditOverridesButton { get; set; } =
            new ButtonItem(PinyinOverridesFileService.UiText("PinyinOverrides_EditButton"))
            {
                CommandId = "EditPinyinOverrides",
            };

        /// <summary>
        /// 多音字语义说明文本，从页头下移到「编辑多音字列表」按钮下方。
        /// genericedit 的 ButtonItem 渲染器（renderButtonItemCore）不输出 fieldDescription，
        /// 按钮本身挂不了说明，所以用 LabelItem 承载：渲染为
        /// &lt;p&gt;&lt;a class="labelText"&gt;…&lt;/a&gt;&lt;/p&gt; 正文样式，无输入控件。
        /// 文案仍取资源键 PinyinOverrides_Description（内容一字未改）；
        /// 值由视图每次构造时重建、保存前清空，不落插件配置文件。
        /// </summary>
        public LabelItem OverridesNotice { get; set; } =
            new LabelItem(PinyinOverridesFileService.UiText("PinyinOverrides_Description"));

        /// <summary>
        /// 页面状态条。必须保持非 null：客户端 genericedit 只在拿到该值时才会
        /// 按状态切换图标/转圈（Status != InProgress 时隐藏 spinner），
        /// 为 null 会让初始 HTML 里的 spinner 一直转。
        /// 视图构造时由 PinyinSearchPageView 覆盖为真实文件状态。
        /// </summary>
        public StatusItem MessageStatus { get; set; } = new StatusItem();
    }
}
