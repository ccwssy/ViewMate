using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using ViewMate.Common;

namespace ViewMate.IntroSkip
{
    public class IntroBackfillService
    {
        private readonly ILogger _logger;
        private readonly ChapterMarkerApi _chapterMarkerApi;
        private readonly ConnectionManagerCache _connectionCache;

        // 本回填服务写入 Chapters3 的原始 MarkerType 取值。
        // 刻意不用 MediaBrowser.Model.Entities.MarkerType（其枚举数值
        // 顺序不同）—— 这些字面数据库取值必须保持不变。
        private enum BackfillMarkerType
        {
            IntroStart = 1,
            IntroEnd = 2,
            CreditsStart = 3,
        }

        // 剧中集在 MediaItems.Type 里的取值（Emby BaseItemKind.Episode）。
        private const long EpisodeType = 8;

        // 名称过滤：名字含此字符串的标记一律忽略
        // （例如 plot/章节式标记，它们并非片头/片尾）。
        private const string PlotFilter = "plot";

        public IntroBackfillService(ChapterMarkerApi chapterMarkerApi, ILogger logger)
        {
            _chapterMarkerApi = chapterMarkerApi;
            _logger = logger;
            _connectionCache = new ConnectionManagerCache(logger, "IntroBackfill");
        }

        // ── 回填逻辑 ──

        public int BackfillMissing()
        {
            if (!Plugin.Instance.Configuration.EnableIntroBackfill)
            {
                _logger.Info("[IntroBackfill] Disabled by config");
                return 0;
            }

            _logger.Info("[IntroBackfill] Scanning...");

            // 阶段 1：读 —— 找出已有标记的剧集系列
            var seriesIds = new List<long>();
            using (var conn = _connectionCache.OpenReadConnection())
            {
                if (conn == null) return 0;

                try
                {
                    using (var stmt = conn.PrepareStatement(
                        $@"SELECT DISTINCT m.SeriesId FROM MediaItems m
                          JOIN Chapters3 c ON c.ItemId = m.Id
                          WHERE c.Name LIKE '%{ChapterMarkerApi.MarkerSuffix}%' AND c.Name NOT LIKE '%{PlotFilter}%'"))
                    {
                        while (stmt.MoveNext())
                            seriesIds.Add(stmt.Current.GetInt64(0));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("[IntroBackfill] Series scan failed", ex);
                    return 0;
                }
            }

            _logger.Info("[IntroBackfill] {0} series with existing markers", seriesIds.Count);

            int totalFixed = 0;

            foreach (var sid in seriesIds)
            {
                // 阶段 2：读 —— 取出该系列的各集
                var episodes = new List<Tuple<long, string, int?, int?>>();
                using (var conn = _connectionCache.OpenReadConnection())
                {
                    if (conn == null) continue;

                    try
                    {
                        var epQuery = $@"SELECT Id, Name, IndexNumber, ParentIndexNumber FROM MediaItems
                                         WHERE SeriesId = {sid} AND Type = {EpisodeType} ORDER BY IndexNumber";
                        using (var stmt = conn.PrepareStatement(epQuery))
                        {
                            while (stmt.MoveNext())
                            {
                                episodes.Add(Tuple.Create(
                                    stmt.Current.GetInt64(0),
                                    stmt.Current.GetString(1),
                                    stmt.Current.IsDBNull(2) ? (int?)null : (int?)stmt.Current.GetInt64(2),
                                    stmt.Current.IsDBNull(3) ? (int?)null : (int?)stmt.Current.GetInt64(3)));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("[IntroBackfill] Episode scan failed for series {0}: {1}", sid, ex.Message);
                        continue;
                    }
                }

                if (episodes.Count == 0) continue;

                // 按季分组
                var seasons = new Dictionary<int, List<Tuple<long, string, int?>>>();
                foreach (var ep in episodes)
                {
                    int seasonIdx = ep.Item4 ?? 1;
                    if (!seasons.ContainsKey(seasonIdx))
                        seasons[seasonIdx] = new List<Tuple<long, string, int?>>();
                    seasons[seasonIdx].Add(Tuple.Create(ep.Item1, ep.Item2, ep.Item3));
                }

                foreach (var kv in seasons)
                {
                    var eps = kv.Value;

                    // 阶段 3：读 —— 找到带标记的参考剧集
                    long refId = 0;
                    long refStart = 0;
                    long refEnd = 0;
                    long refCreditsStart = 0;
                    bool foundRef = false;
                    bool refHasCredits = false;

                    foreach (var ep in eps)
                    {
                        using (var conn = _connectionCache.OpenReadConnection())
                        {
                            if (conn == null) break;

                            try
                            {
                                var markerQuery = $@"SELECT StartPositionTicks, Name FROM Chapters3
                                                   WHERE ItemId = {ep.Item1} AND Name LIKE '%{ChapterMarkerApi.MarkerSuffix}%'
                                                   ORDER BY StartPositionTicks";
                                var markers = new List<Tuple<long, string>>();
                                using (var stmt = conn.PrepareStatement(markerQuery))
                                {
                                    while (stmt.MoveNext())
                                        markers.Add(Tuple.Create(stmt.Current.GetInt64(0), stmt.Current.GetString(1)));
                                }

                                if (markers.Count >= 2)
                                {
                                    refId = ep.Item1;
                                    refStart = markers[0].Item1;
                                    refEnd = markers[1].Item1;
                                    foundRef = true;
                                    // 检查是否存在 CreditsStart 标记（若有，则为第 3 个标记）
                                    refHasCredits = markers.Count >= 3
                                        && markers[2].Item2.StartsWith("CreditsStart");
                                    if (refHasCredits)
                                        refCreditsStart = markers[2].Item1;
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn("[IntroBackfill] Marker scan failed for episode {0}: {1}", ep.Item1, ex.Message);
                            }
                        }
                    }

                    if (!foundRef) continue;

                    // 阶段 4：写 —— 为该季补齐缺失的标记
                    foreach (var ep in eps)
                    {
                        using (var conn = _connectionCache.OpenWriteConnection())
                        {
                            if (conn == null) continue;

                            try
                            {
                                // 检查已有 ECS 标记数量
                                var countQuery = $@"SELECT COUNT(*) FROM Chapters3
                                                  WHERE ItemId = {ep.Item1} AND Name LIKE '%{ChapterMarkerApi.MarkerSuffix}%'
                                                  AND Name NOT LIKE '%{PlotFilter}%'";
                                int has;
                                using (var stmt = conn.PrepareStatement(countQuery))
                                {
                                    stmt.MoveNext();
                                    has = (int)stmt.Current.GetInt64(0);
                                }

                                if (has >= 2)
                                {
                                    // 该集已有片头标记。检查是否只缺 CreditsStart。
                                    if (!refHasCredits || has >= 3)
                                        continue;

                                    // 只缺 CreditsStart —— 直接补上，跳过片头回填
                                    int maxIdxCredits;
                                    using (var stmt = conn.PrepareStatement(
                                        $"SELECT MAX(ChapterIndex) FROM Chapters3 WHERE ItemId = {ep.Item1}"))
                                    {
                                        stmt.MoveNext();
                                        maxIdxCredits = stmt.Current.IsDBNull(0) ? 0 : (int)stmt.Current.GetInt64(0);
                                    }
                                    conn.Execute(
                                        $"INSERT INTO Chapters3 (ItemId, ChapterIndex, StartPositionTicks, Name, MarkerType) " +
                                        $"VALUES ({ep.Item1}, {maxIdxCredits + 1}, {refCreditsStart}, 'CreditsStart{ChapterMarkerApi.MarkerSuffix}', {(int)BackfillMarkerType.CreditsStart})");
                                    totalFixed++;
                                    _logger.Info("[IntroBackfill] Credits-only backfill: Series={0} E{1} ({2})", sid, ep.Item3 ?? 0, ep.Item2);
                                    continue;
                                }

                                // 取得最大 ChapterIndex
                                int maxIdx;
                                using (var stmt = conn.PrepareStatement(
                                    $"SELECT MAX(ChapterIndex) FROM Chapters3 WHERE ItemId = {ep.Item1}"))
                                {
                                    stmt.MoveNext();
                                    maxIdx = stmt.Current.IsDBNull(0) ? 0 : (int)stmt.Current.GetInt64(0);
                                }

                                // 原子地删除旧 ECS 标记并重新插入。
                                // 若在 DELETE 与 INSERT 之间崩溃，会彻底丢失该集
                                // 的所有标记（v1.2.16.23 修复）。
                                conn.BeginTransaction(TransactionMode.Deferred);
                                try
                                {
                                    conn.Execute(
                                        $"DELETE FROM Chapters3 WHERE ItemId = {ep.Item1} AND Name LIKE '%{ChapterMarkerApi.MarkerSuffix}%'");

                                    conn.Execute(
                                        $"INSERT INTO Chapters3 (ItemId, ChapterIndex, StartPositionTicks, Name, MarkerType) " +
                                        $"VALUES ({ep.Item1}, {maxIdx + 1}, {refStart}, 'IntroStart{ChapterMarkerApi.MarkerSuffix}', {(int)BackfillMarkerType.IntroStart})");

                                    conn.Execute(
                                        $"INSERT INTO Chapters3 (ItemId, ChapterIndex, StartPositionTicks, Name, MarkerType) " +
                                        $"VALUES ({ep.Item1}, {maxIdx + 2}, {refEnd}, 'IntroEnd{ChapterMarkerApi.MarkerSuffix}', {(int)BackfillMarkerType.IntroEnd})");

                                    // 若参考剧集带 CreditsStart，也一并回填
                                    if (refHasCredits)
                                    {
                                        conn.Execute(
                                            $"INSERT INTO Chapters3 (ItemId, ChapterIndex, StartPositionTicks, Name, MarkerType) " +
                                            $"VALUES ({ep.Item1}, {maxIdx + 3}, {refCreditsStart}, 'CreditsStart{ChapterMarkerApi.MarkerSuffix}', {(int)BackfillMarkerType.CreditsStart})");
                                    }

                                    conn.CommitTransaction();
                                }
                                catch
                                {
                                    try { conn.RollbackTransaction(); }
                                    catch { }
                                    throw;
                                }

                                totalFixed++;
                                _logger.Info("[IntroBackfill] Fixed: Series={0} E{1} ({2})", sid, ep.Item3 ?? 0, ep.Item2);
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn("[IntroBackfill] Failed to fix episode {0} (series {1}): {2}", ep.Item1, sid, ex.Message);
                            }
                        }
                    }
                }
            }

            _logger.Info("[IntroBackfill] Complete: {0} episodes fixed", totalFixed);
            if (totalFixed > 0)
                WalCheckpointHelper.TryTruncateCheckpoint(_connectionCache, _logger, "IntroBackfill", LogSeverity.Info);
            return totalFixed;
        }
    }
}
