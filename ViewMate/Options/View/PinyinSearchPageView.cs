using Emby.Web.GenericEdit.Elements;
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
        private readonly PluginInfo _pluginInfo;
        private readonly PluginOptionsStore _store;
        private readonly PinyinOverridesFileService _overridesFileService;
        private string _saveNotice;

        public PinyinSearchPageView(PluginInfo pluginInfo, PluginOptionsStore store)
            : base(pluginInfo.Id)
        {
            _pluginInfo = pluginInfo;
            _store = store;
            _overridesFileService = new PinyinOverridesFileService(Plugin.Instance.Logger);

            var options = store.GetOptions();
            // 运行时开关以 PluginConfiguration（XML）为准。
            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            options.PinyinOptions.EnablePinyinSearch = config.EnablePinyinSearch;
            options.PinyinOptions.EnablePinyinSortName = config.EnablePinyinSortName;

            ContentData = options.PinyinOptions;
            EnsureEditButtonCaption();
            EnsureNotice();
            ApplyFileStatus();
        }

        private PinyinOptions Options => (PinyinOptions)ContentData;

        /// <summary>
        /// T15 §2.1（F1）：ButtonItem 是 ContentData 里的可序列化字段，插件配置文件会残留上一版的
        /// Caption（"编辑文件"），反序列化后盖掉 <see cref="PinyinOptions"/> 的字段初始化器 ——
        /// 只改资源键的话，老用户升级后仍看到旧文案（T15 首轮自证即复现）。
        /// 视图构造时按资源键重建按钮，保证 UI 文案始终随版本走；命令链路不变。
        /// </summary>
        private void EnsureEditButtonCaption()
        {
            Options.EditOverridesButton = new ButtonItem(PinyinOverridesFileService.UiText("PinyinOverrides_EditButton"))
            {
                CommandId = "EditPinyinOverrides",
            };
        }

        /// <summary>
        /// T16 §2.1（F1）：语义说明文本（LabelItem）与按钮 Caption 同理 ——
        /// 它是 ContentData 里的可序列化字段，配置文件残留值会盖掉字段初始化器，
        /// 且语言切换后必须随版本走。视图构造时按资源键重建；保存前清空不落盘。
        /// </summary>
        private void EnsureNotice()
        {
            Options.OverridesNotice = new LabelItem(PinyinOverridesFileService.UiText("PinyinOverrides_Description"));
        }

        /// <summary>
        /// 每次视图渲染前给 MessageStatus 赋真实文件状态，绝不留 null：
        /// null 会让客户端保留初始 HTML 里的转圈图标（genericedit 只在拿到 StatusItem
        /// 时才按 Status 隐藏 spinner）。文案与页面状态行（EditorDescription）同源。
        /// </summary>
        private void ApplyFileStatus()
        {
            var path = PinyinOverridesFileService.ResolvePath();
            var ruleCount = PinyinOverridesFileService.TryGetRuleCount();

            Options.MessageStatus = ruleCount >= 0
                ? new StatusItem(
                    PinyinOverridesFileService.UiText("PinyinOverrides_StatusTitle"),
                    string.Format(
                        PinyinOverridesFileService.UiText("PinyinOverrides_StatusExists"),
                        path,
                        ruleCount),
                    ItemStatus.Succeeded)
                : new StatusItem(
                    PinyinOverridesFileService.UiText("PinyinOverrides_StatusTitle"),
                    string.Format(
                        PinyinOverridesFileService.UiText("PinyinOverrides_StatusMissingShort"),
                        path),
                    ItemStatus.Warning);
        }

        /// <summary>
        /// T16 §2.1（F1）：页头说明堆已清空（EditorDescription 不再覆写），
        /// SubCaption 只保留保存回执（「已保存，需重启 Emby 生效」，跟随保存动作出现）。
        /// </summary>
        public override string SubCaption => string.IsNullOrEmpty(_saveNotice) ? null : _saveNotice;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (commandId == "EditPinyinOverrides")
            {
                return Task.FromResult<IPluginUIView>(new PinyinOverridesDialogView(PluginId, _overridesFileService, this));
            }

            return base.RunCommand(itemId, commandId, data);
        }

        /// <summary>
        /// 弹窗保存成功后重建页面视图：主机缓存的状态行是构造时快照，
        /// 只有返回新的视图实例，客户端才会渲染出最新文件状态与「需重启生效」提示。
        /// 构造函数已重新赋过 MessageStatus（真实文件状态，非 null）。
        /// </summary>
        internal PinyinSearchPageView CreateSavedView()
        {
            var view = new PinyinSearchPageView(_pluginInfo, _store);
            var options = (PinyinOptions)view.ContentData;
            options.EnablePinyinSearch = Options.EnablePinyinSearch;
            options.EnablePinyinSortName = Options.EnablePinyinSortName;
            view._saveNotice = PinyinOverridesFileService.UiText("PinyinOverrides_SavedRestartHint");
            return view;
        }

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            var options = _store.ReloadOptions();

            // T10：写回磁盘之前清掉页面的 UI 状态快照 —— MessageStatus 是渲染态
            // （T9 起必须非 null，见 ApplyFileStatus 注释），不该留在插件配置文件里。
            // SetOptions 在锁内同步序列化，所以先置 null 即保证落盘内容不含该字段；
            // 返回视图前再由 ApplyFileStatus() 恢复成真实文件状态（否则客户端转圈图标会回来）。
            // T16 F1：OverridesNotice（语义说明文本载体）同理，属于视图态、不落盘。
            Options.MessageStatus = null;
            Options.OverridesNotice = null;
            options.PinyinOptions = Options;
            _store.SetOptions(options);
            ApplyFileStatus();
            EnsureNotice();

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
