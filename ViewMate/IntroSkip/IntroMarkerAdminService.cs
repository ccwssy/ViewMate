using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using ViewMate.Properties;

namespace ViewMate.IntroSkip
{
    /// <summary>
    /// 剧集标记管理页的只读查询服务：媒体库 → 剧集 → 季 → 各集状态。
    /// 任何一层查询失败都只记 Warn 并返回空集合 —— 设置页不能因为查询异常而崩。
    /// </summary>
    public class IntroMarkerAdminService
    {
        /// <summary>片头时长达到该值即判为异常（集中定义，便于后续调整）。</summary>
        internal const long LongIntroThresholdTicks = 5L * 60 * 10000000L;

        /// <summary>"只看最近播放"的时间窗口（天）。</summary>
        internal const int RecentlyPlayedWindowDays = 30;

        private static readonly MarkerType[] TrackedMarkerTypes =
        {
            MarkerType.IntroStart,
            MarkerType.IntroEnd,
            MarkerType.CreditsStart,
        };

        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IUserDataManager _userDataManager;
        private readonly IUserManager _userManager;
        private readonly ILogger _logger;

        public IntroMarkerAdminService(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IUserDataManager userDataManager,
            IUserManager userManager,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _userDataManager = userDataManager;
            _userManager = userManager;
            _logger = logger;
        }

        public sealed class LibraryRow
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public int SeriesCount { get; set; }
            public int EpisodeCount { get; set; }
        }

        public sealed class SeriesRow
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public int EpisodeCount { get; set; }
            public int MarkedCount { get; set; }
            public bool HasAnomaly { get; set; }
            public DateTimeOffset? LastPlayedDate { get; set; }
        }

        public sealed class SeasonRow
        {
            public int SeasonIndex { get; set; }
            public string Name { get; set; }
            public int EpisodeCount { get; set; }
        }

        public sealed class EpisodeRow
        {
            public string Id { get; set; }
            public string Label { get; set; }
            public string Title { get; set; }
            public long? IntroStartTicks { get; set; }
            public long? IntroEndTicks { get; set; }
            public long? CreditsStartTicks { get; set; }

            /// <summary>Unmarked / Auto / ManualLocked / Suppressed / LongIntro / NoCredits。</summary>
            public string StateKind { get; set; }

            public string StateText { get; set; }
        }

        // ── 1 媒体库 ──

        /// <summary>只返回含剧集的媒体库（库里没有任何 Episode 的库不列）。</summary>
        public List<LibraryRow> GetLibraries()
        {
            var rows = new List<LibraryRow>();

            try
            {
                foreach (var folder in _libraryManager.GetVirtualFolders())
                {
                    try
                    {
                        var root = ResolveFolder(folder);
                        if (root == null) continue;

                        var episodes = QueryItems(root, "Episode");
                        if (episodes.Count == 0) continue;

                        rows.Add(new LibraryRow
                        {
                            Id = folder.ItemId,
                            Name = folder.Name,
                            SeriesCount = QueryItems(root, "Series").Count,
                            EpisodeCount = episodes.Count,
                        });
                    }
                    catch (Exception ex)
                    {
                        Warn("库 {0} 统计失败：{1}", folder?.Name, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Warn("媒体库枚举失败：{0}", ex.Message);
            }

            return rows;
        }

        // ── 2 剧集 ──

        /// <summary>筛选后的剧集清单（含集数、已标记集数、异常标记、最近播放时间）。</summary>
        public List<SeriesRow> GetSeries(string libraryId, string nameFilter, bool onlyAnomalous, bool onlyRecentlyPlayed)
        {
            var rows = new List<SeriesRow>();

            try
            {
                var roots = ResolveLibraryRoots(libraryId);
                if (roots.Count == 0) return rows;

                var series = new List<BaseItem>();
                var episodes = new List<Episode>();
                foreach (var root in roots)
                {
                    series.AddRange(QueryItems(root, "Series"));
                    episodes.AddRange(QueryItems(root, "Episode").OfType<Episode>());
                }

                var episodesBySeries = new Dictionary<long, List<Episode>>();
                foreach (var group in episodes.GroupBy(e => e.SeriesId))
                {
                    episodesBySeries[group.Key] = group.ToList();
                }

                var user = FirstUser();
                var filter = (nameFilter ?? string.Empty).Trim();

                foreach (var item in series.OrderBy(s => s.SortName ?? s.Name, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (filter.Length > 0
                            && (item.Name ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        var own = episodesBySeries.TryGetValue(item.InternalId, out var list)
                            ? list
                            : new List<Episode>();

                        var stats = Summarize(own, user);

                        if (onlyAnomalous && !stats.HasAnomaly) continue;
                        if (onlyRecentlyPlayed && !IsRecentlyPlayed(stats.LastPlayedDate)) continue;

                        rows.Add(new SeriesRow
                        {
                            Id = item.InternalId.ToString(CultureInfo.InvariantCulture),
                            Name = item.Name,
                            EpisodeCount = stats.EpisodeCount,
                            MarkedCount = stats.MarkedCount,
                            HasAnomaly = stats.HasAnomaly,
                            LastPlayedDate = stats.LastPlayedDate,
                        });
                    }
                    catch (Exception ex)
                    {
                        Warn("剧集 {0} 统计失败：{1}", item?.Name, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Warn("剧集查询失败：{0}", ex.Message);
            }

            return rows;
        }

        // ── 3 季 ──

        public List<SeasonRow> GetSeasons(string seriesId)
        {
            var rows = new List<SeasonRow>();

            try
            {
                var series = ResolveSeries(seriesId);
                if (series == null) return rows;

                var episodes = QueryItems(series, "Episode").OfType<Episode>();
                foreach (var group in episodes.GroupBy(e => e.ParentIndexNumber ?? 0).OrderBy(g => g.Key))
                {
                    rows.Add(new SeasonRow
                    {
                        SeasonIndex = group.Key,
                        Name = string.Format(Resources.IntroMarker_Season_Name, group.Key),
                        EpisodeCount = group.Count(),
                    });
                }
            }
            catch (Exception ex)
            {
                Warn("季查询失败：{0}", ex.Message);
            }

            return rows;
        }

        // ── 4 整季各集 ──

        public List<EpisodeRow> GetEpisodeRows(string seriesId, int seasonIndex)
        {
            var rows = new List<EpisodeRow>();

            try
            {
                var series = ResolveSeries(seriesId);
                if (series == null) return rows;

                var episodes = QueryItems(series, "Episode").OfType<Episode>()
                    .Where(e => (e.ParentIndexNumber ?? 0) == seasonIndex)
                    .OrderBy(e => e.IndexNumber ?? 0)
                    .ToList();

                foreach (var episode in episodes)
                {
                    try
                    {
                        var markers = GetMarkers(episode);
                        var introStart = FindMarker(markers, MarkerType.IntroStart);
                        var introEnd = FindMarker(markers, MarkerType.IntroEnd);
                        var credits = FindMarker(markers, MarkerType.CreditsStart);
                        var stateKind = ResolveStateKind(episode, introStart, introEnd, credits);

                        rows.Add(new EpisodeRow
                        {
                            Id = episode.InternalId.ToString(CultureInfo.InvariantCulture),
                            Label = string.Format(
                                CultureInfo.InvariantCulture,
                                "S{0:00}E{1:00}",
                                episode.ParentIndexNumber ?? 0,
                                episode.IndexNumber ?? 0),
                            Title = episode.Name,
                            IntroStartTicks = introStart,
                            IntroEndTicks = introEnd,
                            CreditsStartTicks = credits,
                            StateKind = stateKind,
                            StateText = ResolveStateText(stateKind),
                        });
                    }
                    catch (Exception ex)
                    {
                        Warn("剧集 {0} 查询失败：{1}", episode?.Name, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Warn("整季列表查询失败：{0}", ex.Message);
            }

            return rows;
        }

        // ── 内部实现 ──

        private sealed class SeriesStats
        {
            public int EpisodeCount { get; set; }
            public int MarkedCount { get; set; }
            public bool HasAnomaly { get; set; }
            public DateTimeOffset? LastPlayedDate { get; set; }
        }

        private SeriesStats Summarize(List<Episode> episodes, User user)
        {
            var stats = new SeriesStats { EpisodeCount = episodes.Count };

            foreach (var episode in episodes)
            {
                var markers = GetMarkers(episode);
                if (markers.Count > 0)
                {
                    stats.MarkedCount++;
                }

                // T15 §2.3（F3）：剧集级 ⚠ 与单集状态**同一口径** —— 直接复用 ResolveStateKind
                // 的分类结果，不再用"只看标记三元组"的另一套判定（那套会把手动锁定/墓碑集也算成异常，
                // 与单集行显示的状态自相矛盾）。阈值 LongIntroThresholdTicks 不参与这里，仍在 ResolveStateKind 内。
                var stateKind = ResolveStateKind(
                    episode,
                    FindMarker(markers, MarkerType.IntroStart),
                    FindMarker(markers, MarkerType.IntroEnd),
                    FindMarker(markers, MarkerType.CreditsStart));

                if (IsAnomalousState(stateKind))
                {
                    stats.HasAnomaly = true;
                }

                if (user == null) continue;

                try
                {
                    var data = _userDataManager.GetUserData(user, episode);
                    var played = data?.LastPlayedDate;
                    if (played.HasValue && (!stats.LastPlayedDate.HasValue || played.Value > stats.LastPlayedDate.Value))
                    {
                        stats.LastPlayedDate = played.Value;
                    }
                }
                catch (Exception ex)
                {
                    Warn("用户数据读取失败（{0}）：{1}", episode.Name, ex.Message);
                }
            }

            return stats;
        }

        /// <summary>
        /// T15 §2.3：剧集级异常判定 = 该剧下存在 <c>Unmarked / LongIntro / NoCredits</c> 的集。
        /// <c>ManualLocked</c> 与 <c>Suppressed</c>（用户手动接管过的集）一律豁免 —— 不再使剧集亮 ⚠；
        /// 单集行内仍照常显示各自状态标签、异常橙条仍按原逻辑（整体状态含 Warning）。
        /// 「只看标记异常」筛选复用同一结果，故与 ⚠ 口径天然一致。
        /// </summary>
        private static bool IsAnomalousState(string stateKind)
        {
            return stateKind == "Unmarked" || stateKind == "LongIntro" || stateKind == "NoCredits";
        }

        private static string ResolveStateKind(Episode episode, long? introStart, long? introEnd, long? credits)
        {
            var entry = IntroMarkerOverrides.Get(episode.InternalId);
            if (entry != null && entry.Suppressed) return "Suppressed";
            if (entry != null && entry.Locked) return "ManualLocked";
            if (!introStart.HasValue || !introEnd.HasValue) return "Unmarked";
            if (introEnd.Value - introStart.Value >= LongIntroThresholdTicks) return "LongIntro";
            if (!credits.HasValue) return "NoCredits";
            return "Auto";
        }

        private static string ResolveStateText(string stateKind)
        {
            switch (stateKind)
            {
                case "Suppressed":
                    return Resources.IntroMarker_State_Suppressed;
                case "ManualLocked":
                    return Resources.IntroMarker_State_Locked;
                case "LongIntro":
                    return Resources.IntroMarker_State_LongIntro;
                case "NoCredits":
                    return Resources.IntroMarker_State_NoCredits;
                case "Auto":
                    return Resources.IntroMarker_State_Auto;
                default:
                    return Resources.IntroMarker_State_Unmarked;
            }
        }

        private static long? FindMarker(List<ChapterInfo> markers, MarkerType type)
        {
            var chapter = markers.FirstOrDefault(c => c.MarkerType == type);
            return chapter?.StartPositionTicks;
        }

        private List<ChapterInfo> GetMarkers(Episode episode)
        {
            try
            {
                return _itemRepository.GetChapters(episode.InternalId, TrackedMarkerTypes, CancellationToken.None)?.ToList()
                       ?? new List<ChapterInfo>();
            }
            catch (Exception ex)
            {
                Warn("章节读取失败（{0}）：{1}", episode.Name, ex.Message);
                return new List<ChapterInfo>();
            }
        }

        private List<BaseItem> QueryItems(BaseItem parent, string itemType)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { itemType },
                Recursive = true,
                Parent = parent,
            };

            if (itemType == "Episode")
            {
                query.MediaTypes = new[] { MediaType.Video };
                query.HasPath = true;
            }

            return _libraryManager.GetItemList(query)?.ToList() ?? new List<BaseItem>();
        }

        private BaseItem ResolveFolder(VirtualFolderInfo folder)
        {
            if (folder == null || string.IsNullOrEmpty(folder.ItemId)) return null;

            // Emby 的 VirtualFolderInfo.ItemId 可能是 GUID 字符串，也可能是内部数字 Id。
            if (Guid.TryParse(folder.ItemId, out var guidId))
            {
                return _libraryManager.GetItemById(guidId);
            }

            if (long.TryParse(folder.ItemId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var internalId))
            {
                return _libraryManager.GetItemById(internalId);
            }

            return null;
        }

        private Series ResolveSeries(string seriesId)
        {
            if (string.IsNullOrEmpty(seriesId) || !long.TryParse(seriesId, out var id)) return null;
            return _libraryManager.GetItemById(id) as Series;
        }

        private List<BaseItem> ResolveLibraryRoots(string libraryId)
        {
            var roots = new List<BaseItem>();

            foreach (var folder in _libraryManager.GetVirtualFolders())
            {
                // 空 或 "all" = 全部剧集库。
                if (!string.IsNullOrEmpty(libraryId)
                    && libraryId != "all"
                    && !string.Equals(folder.ItemId, libraryId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var root = ResolveFolder(folder);
                if (root != null)
                {
                    roots.Add(root);
                }
            }

            return roots;
        }

        private User FirstUser()
        {
            try
            {
                return _userManager?.GetUserList(new UserQuery { Limit = 1 })?.FirstOrDefault();
            }
            catch (Exception ex)
            {
                Warn("用户枚举失败：{0}", ex.Message);
                return null;
            }
        }

        private static bool IsRecentlyPlayed(DateTimeOffset? lastPlayed)
        {
            return lastPlayed.HasValue
                   && lastPlayed.Value >= DateTimeOffset.Now.AddDays(-RecentlyPlayedWindowDays);
        }

        private void Warn(string message, params object[] args)
        {
            try
            {
                _logger?.Warn("[IntroMarkerAdmin] " + message, args);
            }
            catch (Exception)
            {
                // 日志失败不影响查询。
            }
        }
    }
}
