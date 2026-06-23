using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;

namespace ViewMate.Options
{
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => "观影助手";

        public override string EditorDescription => string.Empty;

        [DisplayName("启用片头跳过检测")]
        [Description("播放时拖进度条跳过片头，自动写入 IntroSkip marker")]
        [Required]
        public bool EnableIntroSkip { get; set; } = false;

        [DisplayName("检测参数")]
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
