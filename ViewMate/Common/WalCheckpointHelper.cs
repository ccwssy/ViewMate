using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;

namespace ViewMate.Common
{
    /// <summary>
    /// Shared SQLite WAL checkpoint helpers. The three services that write to
    /// Emby's SQLite database (PinyinSearch, PinyinSortName, IntroBackfill) each
    /// reclaimed WAL space after bulk writes with an identical implementation;
    /// those are unified here. Logic preserved verbatim (TRUNCATE + PASSIVE).
    /// </summary>
    public static class WalCheckpointHelper
    {
        /// <summary>
        /// Full WAL checkpoint (TRUNCATE). Waits for active readers to drain,
        /// then truncates the WAL to reclaim disk space. Safe on background
        /// threads — blocks only briefly while readers finish their current query.
        /// Call after bulk write operations (initial catch-up, backfill).
        /// </summary>
        public static void TryTruncateCheckpoint(ConnectionManagerCache connectionCache, ILogger logger, string logPrefix, LogSeverity severity = LogSeverity.Debug)
        {
            try
            {
                using (var conn = connectionCache.OpenWriteConnection())
                {
                    if (conn == null) return;
                    conn.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                    logger.Log(severity, "[{0}] TRUNCATE checkpoint done", logPrefix);
                }
            }
            catch { /* checkpoint failures are benign — WAL will recover on next write */ }
        }

        /// <summary>
        /// Passive WAL checkpoint. Checkpoints pages that no active reader needs,
        /// without waiting. Call after moderate write operations (periodic scans).
        /// </summary>
        public static void TryPassiveCheckpoint(ConnectionManagerCache connectionCache)
        {
            try
            {
                using (var conn = connectionCache.OpenWriteConnection())
                {
                    if (conn == null) return;
                    conn.Execute("PRAGMA wal_checkpoint(PASSIVE)");
                }
            }
            catch { }
        }
    }
}
