using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;

namespace ViewMate.Pinyin
{
    public class PinyinOptions : EditableOptionsBase
    {
        public override string EditorTitle => "拼音搜索";

        [DisplayName("启用拼音搜索")]
        [Description("自动为新入库的中文媒体生成拼音索引，支持拼音搜索")]
        [Required]
        public bool EnablePinyinSearch { get; set; } = true;

        [DisplayName("拼音排序名")]
        [Description("将中文媒体的排序名替换为拼音首字母（如「功夫」→「GF」），字母排序区 A-Z 正常显示")]
        [Required]
        [VisibleCondition("EnablePinyinSearch", SimpleCondition.IsTrue)]
        public bool EnablePinyinSortName { get; set; } = true;
    }
}
