using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Plugins.UI.Views;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ViewMate.IntroSkip;
using ViewMate.Options.UIBaseClasses.Views;
using ViewMate.Properties;

namespace ViewMate.Options.View
{
    /// <summary>
    /// 剧集标记编辑弹窗。保存/清除成功后返回主页视图（列表刷新 + 状态提示），
    /// 解析或校验失败时返回自身（仍是可取消的 Dialog，带具体原因），失败一律不写盘。
    /// 失败路径不得返回 null、也不得抛异常 —— 否则框架报 Command is not implemented，
    /// 弹窗卡死；必须让 DialogCancel（X / 取消）与「清除标记」按钮继续可用。
    /// </summary>
    internal class IntroMarkerDialogView : PluginDialogView
    {
        internal const string CommandClearPrefix = "ClearEpisodeMarker:";

        /// <summary>无片头标记时的「片头开始」预填值（定长 hh:mm:ss，与输入框 MaxLength 一致）。</summary>
        internal const string DefaultIntroStart = "00:00:00";

        // T17 §B.1.3：列表容器改为抽象（页面 or 弹窗 1），弹窗内部逻辑一字未改。
        private readonly IMarkerHostView _host;
        private readonly Episode _episode;
        private readonly IReadOnlyList<Episode> _seasonTargets;
        private readonly int _seasonEpisodeCount;

        public IntroMarkerDialogView(
            string pluginId,
            IMarkerHostView host,
            Episode episode,
            IReadOnlyList<Episode> seasonTargets,
            int seasonEpisodeCount)
            : base(pluginId)
        {
            _host = host;
            _episode = episode;
            _seasonTargets = seasonTargets ?? new List<Episode>();
            _seasonEpisodeCount = seasonEpisodeCount;

            var chapterApi = Plugin.ChapterMarkerApi;
            var introStart = chapterApi?.GetIntroStart(episode);
            var introEnd = chapterApi?.GetIntroEnd(episode);
            var credits = chapterApi?.GetCreditsStart(episode);

            ContentData = new IntroMarkerDialogOptions
            {
                // 无片头标记 → 预填 00:00:00，省得用户猜时间怎么写；已有标记 → 显示实际值。
                // 片头结束/片尾开始保持留空（留空 = 不写入），由用户按需填写。
                IntroStart = introStart.HasValue ? MarkerTimeUtil.FormatHms(introStart.Value) : DefaultIntroStart,
                IntroEnd = introEnd.HasValue ? MarkerTimeUtil.FormatHms(introEnd.Value) : string.Empty,
                CreditsStart = credits.HasValue ? MarkerTimeUtil.FormatHms(credits.Value) : string.Empty,
                LockEpisode = true,
                ApplyToSeason = false,
                MessageStatus = new StatusItem(
                    Resources.IntroMarker_Dialog_StatusTitle,
                    DescribeCurrent(introStart, introEnd, credits),
                    ItemStatus.None),
            };

            OKButtonCaption = "保存";
            EnsureClearButton();
        }

        private IntroMarkerDialogOptions Options => (IntroMarkerDialogOptions)ContentData;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            ApplyFormData(data);
            EnsureClearButton();

            if (string.Equals(commandId, "DialogOk", StringComparison.Ordinal))
            {
                return Task.FromResult(Save());
            }

            if (!string.IsNullOrEmpty(commandId) && commandId.StartsWith(CommandClearPrefix, StringComparison.Ordinal))
            {
                return Task.FromResult(Clear());
            }

            if (string.Equals(commandId, "CancelEdit", StringComparison.Ordinal)
                || string.Equals(commandId, "DialogCancel", StringComparison.Ordinal))
            {
                return Task.FromResult(_host.HostView);
            }

            return base.RunCommand(itemId, commandId, data);
        }

        // ── 保存 ──

        private IPluginUIView Save()
        {
            var hasIntro = !string.IsNullOrWhiteSpace(Options.IntroStart)
                           || !string.IsNullOrWhiteSpace(Options.IntroEnd);
            var hasCredits = !string.IsNullOrWhiteSpace(Options.CreditsStart);

            if (!hasIntro && !hasCredits)
            {
                return Fail(Resources.IntroMarker_Error_Nothing);
            }

            long introStart = 0;
            long introEnd = 0;
            long creditsStart = 0;

            if (hasIntro)
            {
                if (string.IsNullOrWhiteSpace(Options.IntroStart) || string.IsNullOrWhiteSpace(Options.IntroEnd))
                {
                    return Fail(Resources.IntroMarker_Error_IntroPair);
                }

                if (!MarkerTimeUtil.TryParse(Options.IntroStart, out introStart))
                {
                    return Fail(string.Format(CultureInfo.CurrentCulture, Resources.IntroMarker_Error_Parse, Options.IntroStart));
                }

                if (!MarkerTimeUtil.TryParse(Options.IntroEnd, out introEnd))
                {
                    return Fail(string.Format(CultureInfo.CurrentCulture, Resources.IntroMarker_Error_Parse, Options.IntroEnd));
                }
            }

            if (hasCredits && !MarkerTimeUtil.TryParse(Options.CreditsStart, out creditsStart))
            {
                return Fail(string.Format(CultureInfo.CurrentCulture, Resources.IntroMarker_Error_Parse, Options.CreditsStart));
            }

            var chapterApi = Plugin.ChapterMarkerApi;
            if (chapterApi == null)
            {
                return Fail(Resources.IntroMarker_Error_EpisodeNotFound);
            }

            var targets = Targets();

            if (hasIntro)
            {
                var error = targets.Count == 1
                    ? chapterApi.UpdateIntroSingle(targets[0], introStart, introEnd)
                    : chapterApi.UpdateIntroSeason(targets, introStart, introEnd);

                if (error != null)
                {
                    return Fail(error);
                }
            }

            if (hasCredits)
            {
                var error = targets.Count == 1
                    ? chapterApi.UpdateCreditsSingle(targets[0], creditsStart)
                    : chapterApi.UpdateCreditsSeason(targets, creditsStart);

                if (error != null)
                {
                    return Fail(error);
                }
            }

            if (Options.LockEpisode)
            {
                foreach (var target in targets)
                {
                    IntroMarkerOverrides.MarkLocked(
                        target.InternalId,
                        hasIntro ? (long?)introStart : null,
                        hasIntro ? (long?)introEnd : null,
                        hasCredits ? (long?)creditsStart : null);
                }
            }
            else if (IntroMarkerOverrides.Get(_episode.InternalId) != null)
            {
                // T15 §2.4（F4）：取消勾选「锁定本集」= 交回自动管理 —— 删除该集的覆盖表记录。
                // 修复前这里"什么都不做"：旧记录留着 → 列表照旧显示「手动锁定」，自愈还会按旧记录把值拽回。
                // 只作用于**当前集**；整季应用（ApplyToSeason）时其它集的锁定状态一律不动（整季只是写值）。
                // 注意：取消锁定**不删除** Chapters3 里的标记值（只交出"自动不得覆盖"的保护）。
                IntroMarkerOverrides.Remove(_episode.InternalId);
            }

            var detail = Describe(hasIntro, introStart, introEnd, hasCredits, creditsStart);
            var body = targets.Count > 1
                ? string.Format(CultureInfo.CurrentCulture, Resources.IntroMarker_Result_SeasonSaved, targets.Count, detail)
                : string.Format(CultureInfo.CurrentCulture, Resources.IntroMarker_Result_Saved, detail);

            // T15 §2.4.4：回执必须能分辨"锁定 / 解锁"，否则用户无法确认取消勾选是否真的生效。
            return _host.CreateMarkersRefreshedView(body + "；" + DescribeLockState(targets.Count));
        }

        /// <summary>T15 §2.4.4：保存回执里的锁定/解锁后缀（整季锁定时带上集数）。</summary>
        private string DescribeLockState(int targetCount)
        {
            if (!Options.LockEpisode)
            {
                return Resources.IntroMarker_Result_UnlockedSuffix;
            }

            return targetCount > 1
                ? string.Format(CultureInfo.CurrentCulture, Resources.IntroMarker_Result_SeasonLockedSuffix, targetCount)
                : Resources.IntroMarker_Result_LockedSuffix;
        }

        // ── 清除（写墓碑：禁止自动逻辑填回） ──

        private IPluginUIView Clear()
        {
            var chapterApi = Plugin.ChapterMarkerApi;
            if (chapterApi == null)
            {
                return Fail(Resources.IntroMarker_Error_EpisodeNotFound);
            }

            var targets = Targets();
            foreach (var target in targets)
            {
                chapterApi.ClearIntroMarkers(target);
                chapterApi.ClearCreditsMarker(target);
                IntroMarkerOverrides.MarkSuppressed(target.InternalId);
            }

            var notice = targets.Count > 1
                ? string.Format(CultureInfo.CurrentCulture, Resources.IntroMarker_Result_ClearSeason, targets.Count)
                : string.Format(CultureInfo.CurrentCulture, Resources.IntroMarker_Result_Cleared, _episode.Name);

            return _host.CreateMarkersRefreshedView(notice);
        }

        // ── 内部辅助 ──

        /// <summary>写入目标：总是包含当前集；勾选「应用到整季」时再并入同季未锁定的集。</summary>
        private List<Episode> Targets()
        {
            var targets = new List<Episode> { _episode };

            if (!Options.ApplyToSeason)
            {
                return targets;
            }

            foreach (var item in _seasonTargets)
            {
                if (targets.Any(t => t.InternalId == item.InternalId)) continue;
                targets.Add(item);
            }

            return targets;
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

                    Options.IntroStart = FormDataReader.ReadString(root, nameof(Options.IntroStart), Options.IntroStart);
                    Options.IntroEnd = FormDataReader.ReadString(root, nameof(Options.IntroEnd), Options.IntroEnd);
                    Options.CreditsStart = FormDataReader.ReadString(root, nameof(Options.CreditsStart), Options.CreditsStart);
                    Options.LockEpisode = FormDataReader.ReadBool(root, nameof(Options.LockEpisode), Options.LockEpisode);
                    Options.ApplyToSeason = FormDataReader.ReadBool(root, nameof(Options.ApplyToSeason), Options.ApplyToSeason);
                }
            }
            catch (JsonException)
            {
                // 提交内容不是 JSON：保持当前值。
            }
        }

        /// <summary>清除按钮的 CommandId 每次都要带上真实集 Id（ContentData 可能被框架重建）。</summary>
        private void EnsureClearButton()
        {
            Options.ClearButton = new ButtonItem(Resources.IntroMarker_Dialog_Clear)
            {
                CommandId = CommandClearPrefix + _episode.InternalId.ToString(CultureInfo.InvariantCulture),
            };
        }

        private IPluginUIView Fail(string message)
        {
            Options.MessageStatus = new StatusItem(Resources.IntroMarker_Dialog_StatusTitle, message, ItemStatus.Failed);
            Plugin.Instance?.Logger?.Info("[IntroMarker] 弹窗操作被拒（视图仍为 Dialog）：" + message);
            return this;
        }

        private string DescribeCurrent(long? introStart, long? introEnd, long? credits)
        {
            var parts = new List<string>
            {
                Label() + "　" + _episode.Name,
            };

            parts.Add(introStart.HasValue && introEnd.HasValue
                ? string.Format(CultureInfo.CurrentCulture, Resources.IntroMarker_Summary_Intro, MarkerTimeUtil.Format(introStart.Value), MarkerTimeUtil.Format(introEnd.Value))
                : Resources.IntroMarker_Summary_Unmarked);

            parts.Add(credits.HasValue
                ? string.Format(CultureInfo.CurrentCulture, Resources.IntroMarker_Summary_Credits, MarkerTimeUtil.Format(credits.Value))
                : Resources.IntroMarker_Summary_NoCredits);

            parts.Add(string.Format(CultureInfo.CurrentCulture, Resources.IntroMarker_Dialog_ApplySeason, _seasonEpisodeCount));

            return string.Join(" ｜ ", parts);
        }

        private static string Describe(bool hasIntro, long introStart, long introEnd, bool hasCredits, long creditsStart)
        {
            var parts = new List<string>();

            if (hasIntro)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, Resources.IntroMarker_Summary_Intro, MarkerTimeUtil.Format(introStart), MarkerTimeUtil.Format(introEnd)));
            }

            if (hasCredits)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, Resources.IntroMarker_Summary_Credits, MarkerTimeUtil.Format(creditsStart)));
            }

            return string.Join(" ｜ ", parts);
        }

        private string Label()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "S{0:00}E{1:00}",
                _episode.ParentIndexNumber ?? 0,
                _episode.IndexNumber ?? 0);
        }
    }
}
