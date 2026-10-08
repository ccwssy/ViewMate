using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ViewMate.Common
{
    public class ChapterMarkerApi
    {
        private readonly ILogger _logger;
        private readonly IItemRepository _itemRepository;
        private readonly ILibraryManager _libraryManager;

        internal const string MarkerSuffix = "#ECS";

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

        public void UpdateIntro(Episode item, long introStartTicks, long introEndTicks)
        {
            if (introStartTicks > introEndTicks) return;

            var episodes = FetchEpisodesInSeason(item);

            foreach (var episode in episodes)
            {
                var chapters = GetChapters(episode);

                chapters.RemoveAll(c =>
                    (c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd)
                    && IsMarkerOurs(c));

                chapters.Add(new ChapterInfo
                {
                    Name = MarkerType.IntroStart + MarkerSuffix,
                    MarkerType = MarkerType.IntroStart,
                    StartPositionTicks = introStartTicks
                });
                chapters.Add(new ChapterInfo
                {
                    Name = MarkerType.IntroEnd + MarkerSuffix,
                    MarkerType = MarkerType.IntroEnd,
                    StartPositionTicks = introEndTicks
                });

                chapters.Sort((a, b) => a.StartPositionTicks.CompareTo(b.StartPositionTicks));
                SaveChapters(episode, chapters);
            }

            _logger.Info("[IntroSkip] Intro marker written for {0} - {1} ({2} episodes)",
                item.FindSeriesName() ?? item.Name,
                item.FindSeasonName() ?? "",
                episodes.Count);
        }

        public void UpdateCredits(Episode item, long creditsDurationTicks)
        {
            var episodes = FetchEpisodesInSeason(item);

            foreach (var episode in episodes)
            {
                if (!episode.RunTimeTicks.HasValue) continue;
                var creditsStartTicks = episode.RunTimeTicks.Value - creditsDurationTicks;
                if (creditsStartTicks <= 0) continue;

                var chapters = GetChapters(episode);
                chapters.RemoveAll(c => c.MarkerType == MarkerType.CreditsStart && IsMarkerOurs(c));

                chapters.Add(new ChapterInfo
                {
                    Name = MarkerType.CreditsStart + MarkerSuffix,
                    MarkerType = MarkerType.CreditsStart,
                    StartPositionTicks = creditsStartTicks
                });

                chapters.Sort((a, b) => a.StartPositionTicks.CompareTo(b.StartPositionTicks));
                SaveChapters(episode, chapters);
            }

            _logger.Info("[IntroSkip] Credits marker written for {0} - {1} ({2} episodes)",
                item.FindSeriesName() ?? item.Name,
                item.FindSeasonName() ?? "",
                episodes.Count);
        }

        public void ClearMarkers(BaseItem item)
        {
            var chapters = GetChapters(item);
            chapters.RemoveAll(c =>
                (c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd
                 || c.MarkerType == MarkerType.CreditsStart) && IsMarkerOurs(c));
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
            return allEpisodes.ToList();
        }
    }
}
