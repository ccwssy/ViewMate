using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;

namespace ViewMate.Options
{
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => "观影助手";

        public override string EditorDescription => string.Empty;

        [DisplayName("拼音搜索")]
        public Pinyin.PinyinOptions PinyinOptions { get; set; } = new Pinyin.PinyinOptions();

        [DisplayName("片头尾跳过")]
        public IntroSkipOptions IntroSkipOptions { get; set; } = new IntroSkipOptions();

        [DisplayName("版本更新")]
        public VersionCheckSubOptions VersionCheckOptions { get; set; } = new VersionCheckSubOptions();

        [DisplayName("关于")]
        public AboutOptions AboutOptions { get; set; } = new AboutOptions();

        public void Initialize()
        {
        }
    }
}
