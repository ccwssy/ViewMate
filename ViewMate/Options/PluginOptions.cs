using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;

namespace ViewMate.Options
{
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => "观影助手";

        public override string EditorDescription => string.Empty;

        [DisplayName("片头片尾跳过")]
        public IntroSkipOptions IntroSkipOptions { get; set; } = new IntroSkipOptions();

        [DisplayName("拼音搜索")]
        public Pinyin.PinyinOptions PinyinOptions { get; set; } = new Pinyin.PinyinOptions();

        [DisplayName("关于")]
        public AboutOptions AboutOptions { get; set; } = new AboutOptions();

        public void Initialize()
        {
        }
    }
}
