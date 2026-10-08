using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using ViewMate.Options.Store;
using ViewMate.Options.UIBaseClasses.Views;
using ViewMate.Pinyin;
using System.Threading.Tasks;

namespace ViewMate.Options.View
{
    /// <summary>
    /// 拼音搜索 tab。ContentData 只承载 PinyinOptions 段；保存时会重新加载
    /// 整个 JSON 容器，替换自己那一段后写回，
    /// 然后同步 PluginConfiguration（XML）中对应的字段。
    /// </summary>
    internal class PinyinSearchPageView : PluginPageView
    {
        private readonly PluginOptionsStore _store;

        public PinyinSearchPageView(PluginInfo pluginInfo, PluginOptionsStore store)
            : base(pluginInfo.Id)
        {
            _store = store;

            var options = store.GetOptions();
            // 运行时开关以 PluginConfiguration（XML）为准。
            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            options.PinyinOptions.EnablePinyinSearch = config.EnablePinyinSearch;
            options.PinyinOptions.EnablePinyinSortName = config.EnablePinyinSortName;

            ContentData = options.PinyinOptions;
        }

        private PinyinOptions Options => (PinyinOptions)ContentData;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var options = _store.ReloadOptions();
            options.PinyinOptions = Options;
            _store.SetOptions(options);

            // 把取值同步到 PluginConfiguration
            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            config.EnablePinyinSearch = Options.EnablePinyinSearch;
            config.EnablePinyinSortName = Options.EnablePinyinSortName;
            Plugin.Instance.UpdateConfiguration(config);

            // ── PinyinSortName 的运行时开关 ──
            Plugin.SetPinyinSortNameEnabled(config.EnablePinyinSortName);

            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
