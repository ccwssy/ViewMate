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

        public void Initialize()
        {
        }
    }
}
