using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Plugins.UI.Views;
using System.Text.Json;
using System.Threading.Tasks;
using ViewMate.Options.UIBaseClasses.Views;
using ViewMate.Options.View;

namespace ViewMate.Pinyin
{
    /// <summary>
    /// 多音字配置弹窗：加载覆盖文件、处理保存/恢复/取消。
    /// 保存成功或取消时返回页面视图（客户端据此关闭弹窗），失败时返回自身把错误留在弹窗内。
    /// 表单数据一律经 ContentData 访问：框架在 RunCommand 前会用提交的 Data 重建 ContentData。
    /// </summary>
    internal class PinyinOverridesDialogView : PluginDialogView
    {
        private readonly PinyinOverridesFileService _fileService;
        private readonly PinyinSearchPageView _pageView;
        private readonly string _originalContent;

        public PinyinOverridesDialogView(string pluginId, PinyinOverridesFileService fileService, PinyinSearchPageView pageView)
            : base(pluginId)
        {
            _fileService = fileService;
            _pageView = pageView;
            _originalContent = fileService.ReadAll();
            ContentData = new PinyinOverridesDialogOptions
            {
                Content = _originalContent,
                RestoreButton = new ButtonItem(PinyinOverridesFileService.UiText("PinyinOverrides_RestoreButton")) { CommandId = "RestoreOriginal" },
                CancelButton = new ButtonItem(PinyinOverridesFileService.UiText("PinyinOverrides_CancelButton")) { CommandId = "CancelEdit" },
            };

            OKButtonCaption = PinyinOverridesFileService.UiText("PinyinOverrides_SaveButton");
            ShowFileStatus();
        }

        private PinyinOverridesDialogOptions Options => (PinyinOverridesDialogOptions)ContentData;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (commandId == "DialogOk")
            {
                return Task.FromResult(Save(data));
            }

            if (commandId == "RestoreOriginal")
            {
                Options.Content = _originalContent;
                ShowFileStatus();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId == "CancelEdit")
            {
                return Task.FromResult<IPluginUIView>(_pageView);
            }

            return base.RunCommand(itemId, commandId, data);
        }

        // 保存：宽松解析 + 自动修正 + 护栏 → 备份 → 原子写入。
        // 成功返回新建的页面视图（主机状态行是构造时快照，必须重建才能显示最新状态与重启提示），
        // 失败返回自身（弹窗保留并把错误显示在状态条上）。
        private IPluginUIView Save(string data)
        {
            var content = ExtractContent(data) ?? Options.Content;
            Options.Content = content;

            var error = _fileService.Save(content, out var repairNote);
            if (error != null)
            {
                SetStatus(PinyinOverridesFileService.UiText("PinyinOverrides_ErrorTitle"), error, ItemStatus.Failed);
                return this;
            }

            var savedView = _pageView.CreateSavedView();
            if (!string.IsNullOrEmpty(repairNote))
            {
                // 内容被自动修正过：保存成功也要让用户知道程序改了什么（页面状态条）。
                var options = savedView.ContentData as PinyinOptions;
                if (options != null)
                {
                    options.MessageStatus = new StatusItem(
                        PinyinOverridesFileService.UiText("PinyinOverrides_StatusTitle"),
                        repairNote,
                        ItemStatus.Succeeded);
                }
            }

            return savedView;
        }

        // 客户端提交的 Data 是弹窗 EditObjectContainer.Object 的 JSON 串，多行文本在 Content 字段。
        private static string ExtractContent(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return null;
            }

            try
            {
                using (var document = JsonDocument.Parse(data))
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty(nameof(PinyinOverridesDialogOptions.Content), out var value)
                        && value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString();
                    }
                }
            }
            catch (JsonException)
            {
            }

            return null;
        }

        private void ShowFileStatus()
        {
            SetStatus(PinyinOverridesFileService.UiText("PinyinOverrides_StatusTitle"),
                PinyinOverridesFileService.DescribeStatus(),
                PinyinOverridesFileService.TryGetRuleCount() >= 0 ? ItemStatus.Succeeded : ItemStatus.Warning);
        }

        private void SetStatus(string caption, string statusText, ItemStatus status)
        {
            Options.MessageStatus = new StatusItem(caption, statusText, status);
        }
    }
}
