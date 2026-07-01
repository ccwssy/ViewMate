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

        public IntroBackfillService(ChapterMarkerApi chapterMarkerApi, ILogger logger)
        {
            _chapterMarkerApi = chapterMarkerApi;
            _logger = logger;
            _connectionCache = new ConnectionManagerCache(logger, "IntroBackfill");
        }

        // ── Backfill logic ──

        public int BackfillMissing()
        {
            if (!Plugin.Instance.Configuration.EnableIntroBackfill)
            {
                _logger.Info("[IntroBackfill] Disabled by config");
                return 0;
            }

            _logger.Info("[IntroBackfill] Scanning...");

            // Phase 1: read — discover series with existing markers
            var seriesIds = new List<long>();
            using (var conn = _connectionCache.OpenReadConnection())
            {
                if (conn == null) return 0;

                try
                {
                    using (var stmt = conn.PrepareStatement(
                        @"SELECT DISTINCT m.SeriesId FROM MediaItems m
                          JOIN Chapters3 c ON c.ItemId = m.Id
                          WHERE c.Name LIKE '%#ECS%' AND c.Name NOT LIKE '%plot%'"))
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
                // Phase 2: read — get episodes for this series
                var episodes = new List<Tuple<long, string, int?, int?>>();
                using (var conn = _connectionCache.OpenReadConnection())
                {
                    if (conn == null) continue;

                    try
                    {
                        var epQuery = $@"SELECT Id, Name, IndexNumber, ParentIndexNumber FROM MediaItems
                                         WHERE SeriesId = {sid} AND Type = 8 ORDER BY IndexNumber";
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

                // Group by season
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

                    // Phase 3: read — find reference episode with markers
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
                                                   WHERE ItemId = {ep.Item1} AND Name LIKE '%#ECS%'
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
                                    // Check for CreditsStart marker (3rd marker, if exists)
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

                    // Phase 4: write — backfill missing markers in the season
                    foreach (var ep in eps)
                    {
                        using (var conn = _connectionCache.OpenWriteConnection())
                        {
                            if (conn == null) continue;

                            try
                            {
                                // Check existing ECS marker count
                                var countQuery = $@"SELECT COUNT(*) FROM Chapters3
                                                  WHERE ItemId = {ep.Item1} AND Name LIKE '%#ECS%'
                                                  AND Name NOT LIKE '%plot%'";
                                int has;
                                using (var stmt = conn.PrepareStatement(countQuery))
                                {
                                    stmt.MoveNext();
                                    has = (int)stmt.Current.GetInt64(0);
                                }

                                if (has >= 2)
                                {
                                    // Episode already has Intro markers. Check if only CreditsStart is missing.
                                    if (!refHasCredits || has >= 3)
                                        continue;

                                    // Only missing CreditsStart — just add it, skip intro backfill
                                    int maxIdxCredits;
                                    using (var stmt = conn.PrepareStatement(
                                        $"SELECT MAX(ChapterIndex) FROM Chapters3 WHERE ItemId = {ep.Item1}"))
                                    {
                                        stmt.MoveNext();
                                        maxIdxCredits = stmt.Current.IsDBNull(0) ? 0 : (int)stmt.Current.GetInt64(0);
                                    }
                                    conn.Execute(
                                        $"INSERT INTO Chapters3 (ItemId, ChapterIndex, StartPositionTicks, Name, MarkerType) " +
                                        $"VALUES ({ep.Item1}, {maxIdxCredits + 1}, {refCreditsStart}, 'CreditsStart#ECS', 3)");
                                    totalFixed++;
                                    _logger.Info("[IntroBackfill] Credits-only backfill: Series={0} E{1} ({2})", sid, ep.Item3 ?? 0, ep.Item2);
                                    continue;
                                }

                                // Get max ChapterIndex
                                int maxIdx;
                                using (var stmt = conn.PrepareStatement(
                                    $"SELECT MAX(ChapterIndex) FROM Chapters3 WHERE ItemId = {ep.Item1}"))
                                {
                                    stmt.MoveNext();
                                    maxIdx = stmt.Current.IsDBNull(0) ? 0 : (int)stmt.Current.GetInt64(0);
                                }

                                // Delete old ECS markers
                                conn.Execute(
                                    $"DELETE FROM Chapters3 WHERE ItemId = {ep.Item1} AND Name LIKE '%#ECS%'");

                                conn.Execute(
                                    $"INSERT INTO Chapters3 (ItemId, ChapterIndex, StartPositionTicks, Name, MarkerType) " +
                                    $"VALUES ({ep.Item1}, {maxIdx + 1}, {refStart}, 'IntroStart#ECS', 1)");

                                conn.Execute(
                                    $"INSERT INTO Chapters3 (ItemId, ChapterIndex, StartPositionTicks, Name, MarkerType) " +
                                    $"VALUES ({ep.Item1}, {maxIdx + 2}, {refEnd}, 'IntroEnd#ECS', 2)");

                                // Also backfill CreditsStart if the reference episode has one
                                if (refHasCredits)
                                {
                                    conn.Execute(
                                        $"INSERT INTO Chapters3 (ItemId, ChapterIndex, StartPositionTicks, Name, MarkerType) " +
                                        $"VALUES ({ep.Item1}, {maxIdx + 3}, {refCreditsStart}, 'CreditsStart#ECS', 3)");
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
            return totalFixed;
        }
    }
}
