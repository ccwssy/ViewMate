using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;

namespace ViewMate.Common
{
    /// <summary>
    /// 共用的 SQLite WAL checkpoint 辅助方法。三个写入 Emby SQLite 数据库的服务
    /// （PinyinSearch、PinyinSortName、IntroBackfill）各自用一份完全相同的实现
    /// 在批量写入后回收 WAL 空间，现统一到此。
    /// 逻辑逐字保留（TRUNCATE + PASSIVE）。
    /// </summary>
    public static class WalCheckpointHelper
    {
        /// <summary>
        /// 完整 WAL checkpoint（TRUNCATE）。等待活跃读者退出，
        /// 然后截断 WAL 以回收磁盘空间。后台线程上使用是安全的 ——
        /// 仅在读者完成当前查询期间短暂阻塞。
        /// 在批量写操作（首次追赶、回填）之后调用。
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
            catch { /* checkpoint 失败无害 —— WAL 会在下次写入时自行恢复 */ }
        }

        /// <summary>
        /// 被动 WAL checkpoint。只对当前无活跃读者需要的页做检查点，不等待。
        /// 在中等规模写操作（周期性扫描）之后调用。
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
