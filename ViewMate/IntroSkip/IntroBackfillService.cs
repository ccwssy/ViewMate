using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;
using System.Collections.Generic;
using System.Reflection;
using ViewMate.Common;

namespace ViewMate.IntroSkip
{
    public class IntroBackfillService
    {
        private readonly ILogger _logger;
        private readonly ChapterMarkerApi _chapterMarkerApi;

        public IntroBackfillService(ChapterMarkerApi chapterMarkerApi, ILogger logger)
        {
            _chapterMarkerApi = chapterMarkerApi;
            _logger = logger;
        }

        public int BackfillMissing()
        {
            if (!Plugin.Instance.Configuration.EnableIntroBackfill)
            {
                _logger.Info("[IntroBackfill] Disabled by config");
                return 0;
            }

            _logger.Info("[IntroBackfill] Scanning...");
            var connection = GetDbConnection();
            if (connection == null) return 0;

            int totalFixed = 0;
            try
            {
                // Find all series that have at least one episode with #ECS marker
                var seriesQuery = @"
                    SELECT DISTINCT m.SeriesId FROM MediaItems m
                    JOIN Chapters3 c ON c.ItemId = m.Id
                    WHERE c.Name LIKE '%#ECS%' AND c.Name NOT LIKE '%plot%'";

                var seriesIds = new List<long>();
                using (var stmt = connection.PrepareStatement(seriesQuery))
                {
                    while (stmt.MoveNext())
                        seriesIds.Add(stmt.Current.GetInt64(0));
                }

                _logger.Info("[IntroBackfill] {0} series with existing markers", seriesIds.Count);

                foreach (var sid in seriesIds)
                {
                    // Get all episodes, grouped by season
                    var epQuery = $@"
                        SELECT Id, Name, IndexNumber, ParentIndexNumber FROM MediaItems
                        WHERE SeriesId = {sid} AND Type = 8 ORDER BY IndexNumber";

                    var episodes = new List<Tuple<long, string, int?, int?>>();
                    using (var stmt = connection.PrepareStatement(epQuery))
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

                        // Find reference episode with markers
                        long refId = 0;
                        long refStart = 0;
                        long refEnd = 0;
                        bool foundRef = false;

                        foreach (var ep in eps)
                        {
                            var markerQuery = $@"
                                SELECT StartPositionTicks, Name FROM Chapters3
                                WHERE ItemId = {ep.Item1} AND Name LIKE '%#ECS%'
                                ORDER BY StartPositionTicks";

                            var markers = new List<Tuple<long, string>>();
                            using (var stmt = connection.PrepareStatement(markerQuery))
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
                                break;
                            }
                        }

                        if (!foundRef) continue;

                        // Fill missing episodes
                        foreach (var ep in eps)
                        {
                            var countQuery = $@"
                                SELECT COUNT(*) FROM Chapters3
                                WHERE ItemId = {ep.Item1} AND Name LIKE '%#ECS%'
                                AND Name NOT LIKE '%plot%'";

                            int has;
                            using (var stmt = connection.PrepareStatement(countQuery))
                            {
                                stmt.MoveNext();
                                has = (int)stmt.Current.GetInt64(0);
                            }

                            if (has >= 2) continue;

                            // Get max ChapterIndex
                            int maxIdx;
                            using (var stmt = connection.PrepareStatement(
                                $"SELECT MAX(ChapterIndex) FROM Chapters3 WHERE ItemId = {ep.Item1}"))
                            {
                                stmt.MoveNext();
                                maxIdx = stmt.Current.IsDBNull(0) ? 0 : (int)stmt.Current.GetInt64(0);
                            }

                            connection.Execute(
                                $"DELETE FROM Chapters3 WHERE ItemId = {ep.Item1} AND Name LIKE '%#ECS%'");

                            connection.Execute(
                                $"INSERT INTO Chapters3 (ItemId, ChapterIndex, StartPositionTicks, Name, MarkerType) " +
                                $"VALUES ({ep.Item1}, {maxIdx + 1}, {refStart}, 'IntroStart#ECS', 1)");

                            connection.Execute(
                                $"INSERT INTO Chapters3 (ItemId, ChapterIndex, StartPositionTicks, Name, MarkerType) " +
                                $"VALUES ({ep.Item1}, {maxIdx + 2}, {refEnd}, 'IntroEnd#ECS', 2)");

                            totalFixed++;
                            _logger.Info("[IntroBackfill] Fixed: Series={0} E{1} ({2})", sid, ep.Item3 ?? 0, ep.Item2);
                        }
                    }
                }

                _logger.Info("[IntroBackfill] Complete: {0} episodes fixed", totalFixed);
            }
            catch (Exception ex)
            {
                _logger.Error("[IntroBackfill] Scan failed", ex);
            }

            return totalFixed;
        }

        private IDatabaseConnection GetDbConnection()
        {
            try
            {
                var itemRepo = Plugin.Instance.ApplicationHost.Resolve<IItemRepository>();
                if (itemRepo == null) return null;
                var repoType = itemRepo.GetType();
                var connField = repoType.GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance);
                if (connField == null)
                    connField = repoType.GetField("Connection", BindingFlags.NonPublic | BindingFlags.Instance);
                return connField?.GetValue(itemRepo) as IDatabaseConnection;
            }
            catch (Exception ex)
            {
                _logger.Error("[IntroBackfill] GetDbConnection failed", ex);
                return null;
            }
        }
    }
}
