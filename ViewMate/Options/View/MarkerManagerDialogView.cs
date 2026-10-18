using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ViewMate.IntroSkip;
using ViewMate.Options.Store;
using ViewMate.Options.UIBaseClasses.Views;
using ViewMate.Properties;

namespace ViewMate.Options.View
{
    /// <summary>
    /// 弹窗 1「手动标记校正」：把主 tab 上的「剧集标记管理」区块整体搬进弹窗，
    /// 字段、命令链、渲染语义一字未改（搜索 / 只看异常 / 只看最近播放 / 三级联动 / 集列表 / 状态卡）。
    /// 点某集行内按钮 → 打开单集编辑弹窗（<see cref="IntroMarkerDialogView"/>，即"孙弹窗"）：
    /// 宿主对 dialog → dialog 是一等公民（CreateUIViewHost 的 IPluginDialogView 分支），
    /// 客户端表现为"关旧开新"的单层替换，保存/取消经 OriginalUIView 回到本弹窗并回调
    /// <see cref="OnDialogResult"/>，列表随之自刷新。
    /// </summary>
    internal class MarkerManagerDialogView : PluginDialogView, IMarkerHostView
    {
        internal const string CommandRefreshSeries = "RefreshSeries";
        internal const string CommandRefreshSeasons = "RefreshSeasons";
        internal const string CommandRefreshEpisodes = "RefreshEpisodes";
        internal const string CommandCancelEdit = "CancelEdit";
        internal const string CommandEditPrefix = "EditEpisodeMarker:";

        private readonly PluginInfo _pluginInfo;
        private readonly PluginOptionsStore _store;
        private readonly IntroMarkerAdminService _admin;
        private readonly IPluginUIView _pageView;

        private string _notice;
        private string _error;

        public MarkerManagerDialogView(
            string pluginId,
            PluginInfo pluginInfo,
            PluginOptionsStore store,
            IntroMarkerAdminService admin,
            IPluginUIView pageView,
            bool deferRebuild = false)
            : base(pluginId)
        {
            _pluginInfo = pluginInfo;
            _store = store;
            _admin = admin;
            _pageView = pageView;

            ContentData = new MarkerManagerDialogOptions();
            OKButtonCaption = Resources.IntroMarker_ManagerCloseButton;
            DialogSize = MediaBrowser.Model.Plugins.UI.Views.Enums.DialogSize.FullScreenBorder;

            if (!deferRebuild)
            {
                RebuildAll();
            }
        }

        private MarkerManagerDialogOptions Options => (MarkerManagerDialogOptions)ContentData;

        // ── IMarkerHostView ──

        public IPluginUIView HostView => this;

        public IPluginUIView CreateMarkersRefreshedView(string notice)
        {
            var view = new MarkerManagerDialogView(PluginId, _pluginInfo, _store, _admin, _pageView, true);
            var target = (MarkerManagerDialogOptions)view.ContentData;

            target.MarkerSearchText = Options.MarkerSearchText;
            target.MarkerOnlyAnomalous = Options.MarkerOnlyAnomalous;
            target.MarkerOnlyRecentlyPlayed = Options.MarkerOnlyRecentlyPlayed;
            target.MarkerLibrary = Options.MarkerLibrary;
            target.MarkerSeries = Options.MarkerSeries;
            target.MarkerSeason = Options.MarkerSeason;

            view._notice = notice;
            view.RebuildAll();
            return view;
        }

        // ── 命令路由 ──

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            // 关闭 / 取消：回主页面（弹窗 1 没有需要保存的字段）。
            if (string.Equals(commandId, "DialogOk", StringComparison.Ordinal)
                || string.Equals(commandId, "DialogCancel", StringComparison.Ordinal)
                || string.Equals(commandId, CommandCancelEdit, StringComparison.Ordinal))
            {
                return Task.FromResult(_pageView);
            }

            ApplyFormData(data);

            if (!string.IsNullOrEmpty(commandId) && commandId.StartsWith(CommandEditPrefix, StringComparison.Ordinal))
            {
                var episode = ResolveEpisode(commandId.Substring(CommandEditPrefix.Length));
                if (episode == null)
                {
                    _error = Resources.IntroMarker_Error_EpisodeNotFound;
                    _notice = null;
                    ApplyStatus();
                    return Task.FromResult<IPluginUIView>(this);
                }

                // 孙弹窗：命令链与字段仍是现役 IntroMarkerDialogView（零语义改动），
                // 只是把「回到哪里」交给 IMarkerHostView（此处 = 本弹窗）。
                var dialog = new IntroMarkerDialogView(
                    PluginId,
                    this,
                    episode,
                    BuildSeasonTargets(episode),
                    CountSeasonEpisodes(episode));
                return Task.FromResult<IPluginUIView>(dialog);
            }

            switch (commandId)
            {
                case CommandRefreshSeries:
                    RebuildSeries();
                    RebuildSeasons();
                    RebuildEpisodes();
                    break;
                case CommandRefreshSeasons:
                    RebuildSeasons();
                    RebuildEpisodes();
                    break;
                case CommandRefreshEpisodes:
                    RebuildEpisodes();
                    break;
            }

            ApplyStatus();
            return Task.FromResult<IPluginUIView>(this);
        }

        /// <summary>
        /// 孙弹窗（单集编辑）关闭后的回调：completedOk=true 时列表内容已变化，重建自身数据；
        /// false（取消）时保持原样。宿主在 DialogOk/DialogCancel 时都会走这里。
        /// </summary>
        public override void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data)
        {
            if (!completedOk)
            {
                return;
            }

            try
            {
                RebuildSeries();
                RebuildSeasons();
                RebuildEpisodes();
                ApplyStatus();
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("[IntroMarker] 弹窗回调刷新失败：{0}", ex.Message);
            }
        }

        // ── 四级联动（与现役实现逐行一致，只换容器） ──

        internal void RebuildAll()
        {
            RebuildLibraries();

            if (Options.MarkerLibraryChoices == null || !Options.MarkerLibraryChoices.Any())
            {
                _error = Resources.IntroMarker_Page_NoLibrary;
                Options.MarkerSeriesChoices = new List<EditorSelectOption>();
                Options.MarkerSeasonChoices = new List<EditorSelectOption>();
                Options.MarkerEpisodeRows = new GenericItemList();
                ApplyStatus();
                return;
            }

            RebuildSeries();
            RebuildSeasons();
            RebuildEpisodes();
            ApplyStatus();
        }

        private void RebuildLibraries()
        {
            var choices = new List<EditorSelectOption>
            {
                new EditorSelectOption { Value = "all", Name = Resources.IntroMarker_Library_All, IsEnabled = true },
            };

            foreach (var library in _admin.GetLibraries())
            {
                choices.Add(new EditorSelectOption
                {
                    Value = library.Id,
                    Name = string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}（{1} 剧 / {2} 集）",
                        library.Name,
                        library.SeriesCount,
                        library.EpisodeCount),
                    IsEnabled = true,
                });
            }

            Options.MarkerLibraryChoices = choices;
            if (!choices.Any(c => string.Equals(c.Value, Options.MarkerLibrary, StringComparison.OrdinalIgnoreCase)))
            {
                Options.MarkerLibrary = choices[0].Value;
            }
        }

        private void RebuildSeries()
        {
            var rows = _admin.GetSeries(
                Options.MarkerLibrary,
                Options.MarkerSearchText,
                Options.MarkerOnlyAnomalous,
                Options.MarkerOnlyRecentlyPlayed);

            var choices = new List<EditorSelectOption>();
            foreach (var row in rows)
            {
                choices.Add(new EditorSelectOption
                {
                    Value = row.Id,
                    Name = row.HasAnomaly ? row.Name + " ⚠" : row.Name,
                    IsEnabled = true,
                });
            }

            Options.MarkerSeriesChoices = choices;
            if (!choices.Any(c => c.Value == Options.MarkerSeries))
            {
                Options.MarkerSeries = choices.Count > 0 ? choices[0].Value : string.Empty;
            }
        }

        private void RebuildSeasons()
        {
            var choices = new List<EditorSelectOption>();
            if (!string.IsNullOrEmpty(Options.MarkerSeries))
            {
                foreach (var season in _admin.GetSeasons(Options.MarkerSeries))
                {
                    choices.Add(new EditorSelectOption
                    {
                        Value = season.SeasonIndex.ToString(CultureInfo.InvariantCulture),
                        Name = string.Format(CultureInfo.CurrentCulture, "{0}（{1} 集）", season.Name, season.EpisodeCount),
                        IsEnabled = true,
                    });
                }
            }

            Options.MarkerSeasonChoices = choices;
            if (!choices.Any(c => c.Value == Options.MarkerSeason))
            {
                Options.MarkerSeason = choices.Count > 0 ? choices[0].Value : string.Empty;
            }
        }

        private void RebuildEpisodes()
        {
            var list = new GenericItemList();
            var seasonIndex = ParseSeasonIndex(Options.MarkerSeason);

            if (!string.IsNullOrEmpty(Options.MarkerSeries) && seasonIndex.HasValue)
            {
                foreach (var row in _admin.GetEpisodeRows(Options.MarkerSeries, seasonIndex.Value))
                {
                    var hasMarkers = row.IntroStartTicks.HasValue
                                     || row.IntroEndTicks.HasValue
                                     || row.CreditsStartTicks.HasValue;

                    var item = new GenericListItem
                    {
                        PrimaryText = row.Label + "　" + row.Title,
                        SecondaryText = BuildSummary(row),
                        Status = ToStatus(row.StateKind),
                        Button1 = new ButtonItem(hasMarkers
                            ? Resources.IntroMarker_Button_Edit
                            : Resources.IntroMarker_Button_Add)
                        {
                            CommandId = CommandEditPrefix + row.Id,
                        },
                    };

                    list.Add(item);
                }
            }

            Options.MarkerEpisodeRows = list;
        }

        private static string BuildSummary(IntroMarkerAdminService.EpisodeRow row)
        {
            var parts = new List<string>();

            if (row.IntroStartTicks.HasValue && row.IntroEndTicks.HasValue)
            {
                parts.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.IntroMarker_Summary_Intro,
                    MarkerTimeUtil.Format(row.IntroStartTicks.Value),
                    MarkerTimeUtil.Format(row.IntroEndTicks.Value)));
            }
            else
            {
                parts.Add(Resources.IntroMarker_Summary_Unmarked);
            }

            parts.Add(row.CreditsStartTicks.HasValue
                ? string.Format(CultureInfo.CurrentCulture, Resources.IntroMarker_Summary_Credits, MarkerTimeUtil.Format(row.CreditsStartTicks.Value))
                : Resources.IntroMarker_Summary_NoCredits);

            if (row.StateKind == "LongIntro" || row.StateKind == "NoCredits"
                || row.StateKind == "ManualLocked" || row.StateKind == "Suppressed")
            {
                parts.Add(row.StateText);
            }

            return string.Join(" ｜ ", parts);
        }

        private static ItemStatus ToStatus(string stateKind)
        {
            switch (stateKind)
            {
                case "ManualLocked":
                case "Suppressed":
                    return ItemStatus.Succeeded;
                case "Auto":
                    return ItemStatus.None;
                default:
                    return ItemStatus.Warning;
            }
        }

        private static int? ParseSeasonIndex(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                ? index
                : (int?)null;
        }

        private Episode ResolveEpisode(string id)
        {
            if (!long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var internalId))
            {
                return null;
            }

            try
            {
                return Plugin.Instance.LibraryManager.GetItemById(internalId) as Episode;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 同季可写入的集 —— 弹窗「应用到整季」的目标集合（口径不变）：
        /// 用户主动发起的整季应用覆盖同季**全部**可播放集（含手动锁定/抑制的集）。
        /// </summary>
        private List<Episode> BuildSeasonTargets(Episode episode)
        {
            var targets = new List<Episode>();

            try
            {
                var season = episode.Season;
                if (season == null) return targets;

                targets = season.GetEpisodes(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    HasPath = true,
                    MediaTypes = new[] { MediaBrowser.Model.Entities.MediaType.Video },
                }).Items.OfType<Episode>().OrderBy(e => e.IndexNumber ?? 0).ToList();
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("[IntroMarker] 读取同季剧集失败：{0}", ex.Message);
            }

            return targets;
        }

        private int CountSeasonEpisodes(Episode episode)
        {
            try
            {
                var season = episode.Season;
                if (season == null) return 1;

                return season.GetEpisodes(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    HasPath = true,
                }).Items.Count();
            }
            catch (Exception)
            {
                return 1;
            }
        }

        /// <summary>把提交上来的表单值写回选项对象（否则用户已选值在 postback 后丢失）。</summary>
        private void ApplyFormData(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;

            try
            {
                using (var document = JsonDocument.Parse(data))
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return;

                    Options.MarkerSearchText = FormDataReader.ReadString(root, nameof(Options.MarkerSearchText), Options.MarkerSearchText);
                    Options.MarkerOnlyAnomalous = FormDataReader.ReadBool(root, nameof(Options.MarkerOnlyAnomalous), Options.MarkerOnlyAnomalous);
                    Options.MarkerOnlyRecentlyPlayed = FormDataReader.ReadBool(root, nameof(Options.MarkerOnlyRecentlyPlayed), Options.MarkerOnlyRecentlyPlayed);
                    Options.MarkerLibrary = FormDataReader.ReadString(root, nameof(Options.MarkerLibrary), Options.MarkerLibrary);
                    Options.MarkerSeries = FormDataReader.ReadString(root, nameof(Options.MarkerSeries), Options.MarkerSeries);
                    Options.MarkerSeason = FormDataReader.ReadString(root, nameof(Options.MarkerSeason), Options.MarkerSeason);
                }
            }
            catch (JsonException)
            {
                // 提交内容不是 JSON：保持当前选项，不阻断命令。
            }
        }

        private void ApplyStatus()
        {
            if (!string.IsNullOrEmpty(_error))
            {
                Options.MarkerMessageStatus = new StatusItem(Resources.IntroMarker_Page_Title, _error, ItemStatus.Failed);
                return;
            }

            // 无错误也无提示时给中性状态，绝不置 null（否则客户端 spinner 永不结束）。
            Options.MarkerMessageStatus = string.IsNullOrEmpty(_notice)
                ? new StatusItem(Resources.IntroMarker_Page_Title, string.Empty, ItemStatus.None)
                : new StatusItem(Resources.IntroMarker_Page_Title, _notice, ItemStatus.Succeeded);
        }
    }
}
