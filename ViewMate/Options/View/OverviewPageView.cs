using MediaBrowser.Model.Plugins;
using ViewMate.Options.Store;
using ViewMate.Options.UIBaseClasses.Views;

namespace ViewMate.Options.View
{
    /// <summary>
    /// 「观影助手」总览 tab（主控制器的默认视图）。纯只读展示：没有可编辑
    /// 字段，也不显示保存按钮，因此不写任何配置。
    /// </summary>
    internal class OverviewPageView : PluginPageView
    {
        public OverviewPageView(PluginInfo pluginInfo, PluginOptionsStore store)
            : base(pluginInfo.Id)
        {
            ShowSave = false;

            var options = new OverviewOptions();
            options.Initialize();
            ContentData = options;
        }
    }
}
