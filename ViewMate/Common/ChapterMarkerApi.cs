using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using ViewMate.IntroSkip;
using ViewMate.Properties;

namespace ViewMate.Common
{
    public class ChapterMarkerApi
    {
        private readonly ILogger _logger;
        private readonly IItemRepository _itemRepository;
        private readonly ILibraryManager _libraryManager;

        internal const string MarkerSuffix = "#ECS";

        /// <summary>社区数据写入的标记后缀（与手摆/本地值 #ECS 区分）。</summary>
        internal const string CommunitySuffix = "#VMDB";

        /// <summary>内嵌章节名映射写入的标记后缀。</summary>
        internal const string EmbeddedSuffix = "#VMCH";

        public ChapterMarkerApi(ILibraryManager libraryManager, IItemRepository itemRepository, ILogger logger)
        {
            _logger = logger;
            _itemRepository = itemRepository;
            _libraryManager = libraryManager;
        }

        public long? GetIntroStart(BaseItem item)
        {
            return GetChapters(item).FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart)
                ?.StartPositionTicks;
        }

        public long? GetIntroEnd(BaseItem item)
        {
            return GetChapters(item).FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd)
                ?.StartPositionTicks;
        }

        public long? GetCreditsStart(BaseItem item)
        {
            return GetChapters(item).FirstOrDefault(c => c.MarkerType == MarkerType.CreditsStart)
                ?.StartPositionTicks;
        }

        /// <summary>
        /// 一次性读出该集三个「本项目 #ECS」标记的值（只读，不写库）。
        /// 自愈服务每轮要按集比对三元组，这里刻意只读一次 Chapters，
        /// 且只认名称带 #ECS 后缀的标记 —— 别的来源/别的工具写的同类型
        /// 章节不算本项目的可补写对象。
        /// </summary>
        public (long? IntroStart, long? IntroEnd, long? CreditsStart) GetEcsMarkerValues(BaseItem item)
        {
            var chapters = GetChapters(item);
            return (
                FindOursMarker(chapters, MarkerType.IntroStart),
                FindOursMarker(chapters, MarkerType.IntroEnd),
                FindOursMarker(chapters, MarkerType.CreditsStart));
        }

        /// <summary>
        /// 该集是否已有社区精确值（#VMDB）。本地检测（播放跳转 / 漏集补打）
        /// 写入前若命中就不覆盖 —— 避免粗跳把社区精确值糊掉。锁定集仍由覆盖表闸门先行拦下。
        /// </summary>
        public bool HasCommunityMarkers(BaseItem item)
        {
            return GetChapters(item).Any(c =>
                c?.Name != null && c.Name.EndsWith(CommunitySuffix, StringComparison.Ordinal));
        }

        // ── 整季写入（自动检测路径，签名与语义保持不变） ──

        public void UpdateIntro(Episode item, long introStartTicks, long introEndTicks)
        {
            if (introStartTicks > introEndTicks) return;

            var episodes = FetchEpisodesInSeason(item);

            var written = 0;
            var skipped = 0;
            foreach (var episode in episodes)
            {
                // 手动接管过的集（locked/suppressed）不参与自动写入。
                if (IntroMarkerOverrides.IsProtected(episode.InternalId))
                {
                    skipped++;
                    continue;
                }

                // 已有社区值（#VMDB）→ 本地检测不覆盖（优先级：手动 > 社区 > 本地检测）。
                if (HasCommunityMarkers(episode))
                {
                    skipped++;
                    continue;
                }

                WriteIntro(episode, introStartTicks, introEndTicks);
                written++;
            }

            _logger.Info("[IntroSkip] Intro marker written for {0} - {1} ({2} episodes, {3} manual skipped)",
                item.FindSeriesName() ?? item.Name,
                item.FindSeasonName() ?? "",
                written,
                skipped);
        }

        public void UpdateCredits(Episode item, long creditsDurationTicks)
        {
            var episodes = FetchEpisodesInSeason(item);

            var written = 0;
            var skipped = 0;
            foreach (var episode in episodes)
            {
                if (!episode.RunTimeTicks.HasValue) continue;

                if (IntroMarkerOverrides.IsProtected(episode.InternalId))
                {
                    skipped++;
                    continue;
                }

                // 已有社区值（#VMDB）→ 本地检测不覆盖。
                if (HasCommunityMarkers(episode))
                {
                    skipped++;
                    continue;
                }

                var creditsStartTicks = episode.RunTimeTicks.Value - creditsDurationTicks;
                if (creditsStartTicks <= 0) continue;

                WriteCredits(episode, creditsStartTicks);
                written++;
            }

            _logger.Info("[IntroSkip] Credits marker written for {0} - {1} ({2} episodes, {3} manual skipped)",
                item.FindSeriesName() ?? item.Name,
                item.FindSeasonName() ?? "",
                written,
                skipped);
        }

        // ── 单集 / 指定集写入（标记管理界面） ──

        /// <summary>只写该集片头标记；返回 null 表示成功，否则为拒绝原因。</summary>
        public string UpdateIntroSingle(Episode item, long startTicks, long endTicks)
        {
            var error = ValidateIntro(item, startTicks, endTicks);
            if (error != null) return error;

            WriteIntro(item, startTicks, endTicks);
            _logger.Info("[IntroMarker] Intro set for {0} ({1} -> {2} ticks)", item.Name, startTicks, endTicks);
            return null;
        }

        /// <summary>
        /// 写指定的一组剧集片头标记；返回 null 表示成功。
        /// 调用方（用户主动发起的整季应用）传入的是同季全部可播放集，含手动锁定/抑制的集 ——
        /// 锁定只挡自动逻辑，见 UpdateIntro / UpdateCredits 与 IntroBackfillService 的避让。
        /// </summary>
        public string UpdateIntroSeason(IReadOnlyList<Episode> episodes, long startTicks, long endTicks)
        {
            if (episodes == null || episodes.Count == 0)
            {
                return Resources.IntroMarker_Error_NoTargets;
            }

            foreach (var episode in episodes)
            {
                var error = ValidateIntro(episode, startTicks, endTicks);
                if (error != null) return error;
            }

            foreach (var episode in episodes)
            {
                WriteIntro(episode, startTicks, endTicks);
            }

            _logger.Info("[IntroMarker] Intro set for {0} episodes ({1} -> {2} ticks)", episodes.Count, startTicks, endTicks);
            return null;
        }

        /// <summary>只写该集片尾标记；返回 null 表示成功，否则为拒绝原因。</summary>
        public string UpdateCreditsSingle(Episode item, long creditsStartTicks)
        {
            var error = ValidateCredits(item, creditsStartTicks);
            if (error != null) return error;

            WriteCredits(item, creditsStartTicks);
            _logger.Info("[IntroMarker] Credits set for {0} ({1} ticks)", item.Name, creditsStartTicks);
            return null;
        }

        /// <summary>写指定的一组剧集片尾标记；返回 null 表示成功。</summary>
        public string UpdateCreditsSeason(IReadOnlyList<Episode> episodes, long creditsStartTicks)
        {
            if (episodes == null || episodes.Count == 0)
            {
                return Resources.IntroMarker_Error_NoTargets;
            }

            foreach (var episode in episodes)
            {
                var error = ValidateCredits(episode, creditsStartTicks);
                if (error != null) return error;
            }

            foreach (var episode in episodes)
            {
                WriteCredits(episode, creditsStartTicks);
            }

            _logger.Info("[IntroMarker] Credits set for {0} episodes ({1} ticks)", episodes.Count, creditsStartTicks);
            return null;
        }

        public void ClearMarkers(BaseItem item)
        {
            var chapters = GetChapters(item);
            chapters.RemoveAll(c =>
                (c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd
                 || c.MarkerType == MarkerType.CreditsStart) && IsMarkerOurs(c));
            SaveChapters(item, chapters);
        }

        /// <summary>只清该集的片头标记（本项目 #ECS 归属）。</summary>
        public void ClearIntroMarkers(BaseItem item)
        {
            var chapters = GetChapters(item);
            chapters.RemoveAll(c =>
                (c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd) && IsMarkerOurs(c));
            SaveChapters(item, chapters);
        }

        /// <summary>只清该集的片尾标记（本项目 #ECS 归属）。</summary>
        public void ClearCreditsMarker(BaseItem item)
        {
            var chapters = GetChapters(item);
            chapters.RemoveAll(c => c.MarkerType == MarkerType.CreditsStart && IsMarkerOurs(c));
            SaveChapters(item, chapters);
        }

        /// <summary>
        /// 删除该集「本项目 #ECS」标记中**值超出片长**的条目 —— 三个类型
        /// （IntroStart / IntroEnd / CreditsStart）各自单独判断，返回被删除的条目；没有可删的
        /// 条目时是纯读操作、不写库。调用方（自愈）负责"锁定集只告警不删"与"片长未知则跳过"。
        /// </summary>
        public List<(MarkerType Type, long Ticks)> RemoveOutOfRangeEcsMarkers(BaseItem item, long runtimeTicks)
        {
            var removed = new List<(MarkerType Type, long Ticks)>();
            if (item == null || runtimeTicks <= 0) return removed;

            var chapters = GetChapters(item);
            var outOfRange = chapters
                .Where(c => IsMarkerOurs(c) && IsTrackedMarkerType(c.MarkerType) && c.StartPositionTicks > runtimeTicks)
                .ToList();

            if (outOfRange.Count == 0) return removed;

            foreach (var chapter in outOfRange)
            {
                removed.Add((chapter.MarkerType, chapter.StartPositionTicks));
                chapters.Remove(chapter);
            }

            SaveChapters(item, chapters);
            return removed;
        }

        private static bool IsTrackedMarkerType(MarkerType type) =>
            type == MarkerType.IntroStart || type == MarkerType.IntroEnd || type == MarkerType.CreditsStart;

        // ── 校验（越界一律拒绝，返回可直接展示的中文原因） ──

        internal string ValidateIntro(Episode item, long startTicks, long endTicks)
        {
            if (item == null) return Resources.IntroMarker_Error_EpisodeNotFound;
            if (startTicks < 0 || endTicks < 0) return Resources.IntroMarker_Error_Negative;
            if (startTicks > endTicks) return Resources.IntroMarker_Error_Order;
            if (item.RunTimeTicks.HasValue && endTicks > item.RunTimeTicks.Value)
            {
                return Resources.IntroMarker_Error_TooLong;
            }

            return null;
        }

        internal string ValidateCredits(Episode item, long creditsStartTicks)
        {
            if (item == null) return Resources.IntroMarker_Error_EpisodeNotFound;
            if (creditsStartTicks < 0) return Resources.IntroMarker_Error_Negative;
            if (item.RunTimeTicks.HasValue && creditsStartTicks > item.RunTimeTicks.Value)
            {
                return Resources.IntroMarker_Error_CreditsTooLong;
            }

            return null;
        }

        // ── 写入基元 ──

        private void WriteIntro(BaseItem item, long startTicks, long endTicks)
        {
            var chapters = GetChapters(item);
            chapters.RemoveAll(c =>
                (c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd) && IsMarkerOurs(c));

            chapters.Add(new ChapterInfo
            {
                Name = MarkerType.IntroStart + MarkerSuffix,
                MarkerType = MarkerType.IntroStart,
                StartPositionTicks = startTicks
            });
            chapters.Add(new ChapterInfo
            {
                Name = MarkerType.IntroEnd + MarkerSuffix,
                MarkerType = MarkerType.IntroEnd,
                StartPositionTicks = endTicks
            });

            chapters.Sort((a, b) => a.StartPositionTicks.CompareTo(b.StartPositionTicks));
            SaveChapters(item, chapters);
        }

        private void WriteCredits(BaseItem item, long creditsStartTicks)
        {
            var chapters = GetChapters(item);
            chapters.RemoveAll(c => c.MarkerType == MarkerType.CreditsStart && IsMarkerOurs(c));

            chapters.Add(new ChapterInfo
            {
                Name = MarkerType.CreditsStart + MarkerSuffix,
                MarkerType = MarkerType.CreditsStart,
                StartPositionTicks = creditsStartTicks
            });

            chapters.Sort((a, b) => a.StartPositionTicks.CompareTo(b.StartPositionTicks));
            SaveChapters(item, chapters);
        }

        // ── IItemRepository 包装层（处理各 Emby 版本间的 API 差异） ──

        private List<ChapterInfo> GetChapters(BaseItem item)
        {
            // IItemRepository.GetChapters() 的签名随 Emby 版本而异。
            // 按常用程度依次尝试各个重载。
            try
            {
                return _itemRepository.GetChapters(item).ToList();
            }
            catch (Exception ex)
            {
                _logger.Warn("[ChapterMarkerApi] GetChapters failed for {0}: {1}", item.Name, ex.Message);
                // 兜底：API 不匹配时返回空列表
                return new List<ChapterInfo>();
            }
        }

        private void SaveChapters(BaseItem item, List<ChapterInfo> chapters)
        {
            _itemRepository.SaveChapters(item.InternalId, chapters);
        }

        // ── 辅助方法 ──

        private static bool IsMarkerOurs(ChapterInfo c) =>
            c.Name != null && c.Name.EndsWith(MarkerSuffix);

        private static long? FindOursMarker(List<ChapterInfo> chapters, MarkerType type)
        {
            var chapter = chapters.FirstOrDefault(c => c.MarkerType == type && IsMarkerOurs(c));
            return chapter?.StartPositionTicks;
        }

        private List<Episode> FetchEpisodesInSeason(Episode item)
        {
            if (!item.IndexNumber.HasValue || item.Season == null)
                return new List<Episode> { item };

            var season = item.Season;
            var allEpisodes = season.GetEpisodes(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            }).Items.OfType<Episode>().OrderBy(e => e.IndexNumber ?? 0).ToList();

            // 把片头位置应用到同一季的所有剧集 —— 一次性批量补全。
            // 已带 #ECS 标记的剧集会被覆盖（自动修复）。
            // 手动锁定/抑制的集会在调用处被跳过（自动路径）；用户主动的整季应用不走这里。
            return allEpisodes.ToList();
        }
    }
}
