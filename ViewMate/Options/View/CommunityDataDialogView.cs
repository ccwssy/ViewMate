using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Plugins.UI.Views;
using System;
using System.Text.Json;
using System.Threading.Tasks;
using ViewMate.Options.Store;
using ViewMate.Options.UIBaseClasses.Views;
using ViewMate.Properties;

namespace ViewMate.Options.View
{
    /// <summary>
    /// 弹窗 2「社区数据配置」（T17 §B.1.2）。命令链：EditCommunityData（页面打开）→
    /// SaveCommunityData / CancelCommunityData（保存 / 关闭）。
    /// 保存走既有 Options 存储链（JSON 段）并同步 PluginConfiguration（XML，运行时读取口），
    /// 回主 tab 时重建页面视图以刷新状态行。
    /// </summary>
    internal class CommunityDataDialogView : PluginDialogView
    {
        private readonly PluginOptionsStore _store;
        private readonly IntroSkipPageView _pageView;

        public CommunityDataDialogView(string pluginId, PluginOptionsStore store, IntroSkipPageView pageView)
            : base(pluginId)
        {
            _store = store;
            _pageView = pageView;

            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            ContentData = new CommunityDataDialogOptions
            {
                EnableCommunityShare = config.EnableCommunityShare,
                TheIntroDbApiKey = config.TheIntroDbApiKey ?? string.Empty,
                IntroDbApiKey = config.IntroDbApiKey ?? string.Empty,
                MessageStatus = new StatusItem(
                    Resources.Community_StatusTitle,
                    Resources.Community_Dialog_StatusHint,
                    ItemStatus.None),
            };

            OKButtonCaption = Resources.Community_SaveButton;
        }

        private CommunityDataDialogOptions Options => (CommunityDataDialogOptions)ContentData;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            ApplyFormData(data);

            if (string.Equals(commandId, "DialogOk", StringComparison.Ordinal))
            {
                return Task.FromResult(Save());
            }

            if (string.Equals(commandId, "CancelCommunityData", StringComparison.Ordinal)
                || string.Equals(commandId, "CancelEdit", StringComparison.Ordinal)
                || string.Equals(commandId, "DialogCancel", StringComparison.Ordinal))
            {
                // 取消：不写盘、不改任何配置，直接回页面。
                return Task.FromResult<IPluginUIView>(_pageView);
            }

            return base.RunCommand(itemId, commandId, data);
        }

        private IPluginUIView Save()
        {
            var config = Plugin.Instance.Configuration as PluginConfiguration ?? new PluginConfiguration();
            config.EnableCommunityShare = Options.EnableCommunityShare;
            config.TheIntroDbApiKey = (Options.TheIntroDbApiKey ?? string.Empty).Trim();
            config.IntroDbApiKey = (Options.IntroDbApiKey ?? string.Empty).Trim();

            // §C-REV 修正 02（凱 2026-10-15 拍板）：保存前自动关共享 ——
            // 两把 key trim 后都为空时，共享开关置 false 后再走写盘（不弹额外提示框，
            // 回主 tab 状态行显示「共享 关」即为用户可见反馈）。
            // 依据：CommunityShareTask 在两把 key 都空时本就跳过（不提交），
            // 旧行为会让状态行显示「共享 开」而实际无效（假开状态）。
            // 边界（本版取「都为空才关」）：只配其中一把时共享保持开着。
            if (string.IsNullOrWhiteSpace(config.TheIntroDbApiKey)
                && string.IsNullOrWhiteSpace(config.IntroDbApiKey)
                && config.EnableCommunityShare)
            {
                config.EnableCommunityShare = false;
                Plugin.Instance.Logger?.Info("[Community] 两把 key 未配置，共享开关已自动关闭（保存时前置检查）");
            }

            Plugin.Instance.UpdateConfiguration(config);

            // 同步 Options JSON 段：与页面保存同一套白名单（视图态字段一律不落盘）。
            var options = _store.ReloadOptions();
            options.IntroSkipOptions = new IntroSkipOptions
            {
                EnableIntroSkip = config.EnableIntroSkip,
                MaxIntroDurationSeconds = config.MaxIntroDurationSeconds,
                MaxCreditsDurationSeconds = config.MaxCreditsDurationSeconds,
                EnableIntroBackfill = config.EnableIntroBackfill,
                EnableCommunityData = config.EnableCommunityData,
                EnableCommunityFetch = config.EnableCommunityFetch,
            };
            _store.SetOptions(options);

            Plugin.Instance.Logger?.Info(
                "[Community] 社区配置已保存：共享={0}，TheIntroDB key={1}，IntroDB key={2}",
                config.EnableCommunityShare,
                string.IsNullOrWhiteSpace(config.TheIntroDbApiKey) ? "未配置" : "已配置",
                string.IsNullOrWhiteSpace(config.IntroDbApiKey) ? "未配置" : "已配置");

            return _pageView.CreateConfigSavedView(Resources.Community_SavedHint);
        }

        private void ApplyFormData(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;

            try
            {
                using (var document = JsonDocument.Parse(data))
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return;

                    Options.EnableCommunityShare = FormDataReader.ReadBool(
                        root, nameof(Options.EnableCommunityShare), Options.EnableCommunityShare);
                    Options.TheIntroDbApiKey = FormDataReader.ReadString(
                        root, nameof(Options.TheIntroDbApiKey), Options.TheIntroDbApiKey);
                    Options.IntroDbApiKey = FormDataReader.ReadString(
                        root, nameof(Options.IntroDbApiKey), Options.IntroDbApiKey);
                }
            }
            catch (JsonException)
            {
                // 提交内容不是 JSON：保持当前值，不阻断命令。
            }
        }
    }
}
