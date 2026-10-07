using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ViewMate.Common;

namespace ViewMate.Pinyin
{
    /// <summary>
    /// 为中文媒体生成拼音首字母排序名，替换 Emby 默认的中文排序。
    /// 支持开关切换——关闭时从备份表还原原始 SortName，开启时重新生成。
    /// 无需 Harmony——直接 SQL UPDATE MediaItems.SortName。
    /// </summary>
    public class PinyinSortNameService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ConnectionManagerCache _connectionCache;

        private const string BackupTable = "PinyinSortNameBackup";
        // 与 PinyinSearchService 共用：TinyPinyinLoader 持有该 Lazy
        // （ExecutionAndPublication），把加载推迟到首次真正
        // 使用时，那时 TinyPinyin.dll 必定已被
        // Emby 的程序集扫描器加载。
        private static bool IsPinyinLoaded
        {
            get
            {
                try { return TinyPinyinLoader.GetPinyinFunc != null; }
                catch { return false; }
            }
        }

        private int _disposed;
        private bool IsDisposed => Interlocked.CompareExchange(ref _disposed, 0, 0) == 1;

        private const int BatchSize = 200;

        // ── 事件队列（与 FtsScanScheduler 同一套路） ──
        // ItemAdded/ItemUpdated 入队时不碰 SQL；由 30 秒的一次性定时器
        // 在一个写事务里排空队列，这样媒体库扫描的事件洪峰
        // 不再每个事件都开一个写连接（避免 SQLite 写锁争用）。
        private readonly ConcurrentQueue<Tuple<long, string>> _pendingEventQueue = new ConcurrentQueue<Tuple<long, string>>();
        private Timer _eventTimer;
        private int _eventTimerRunning;
        private const int EventBatchSize = 50;
        private const int EventTimerIntervalMs = 30000;

        public PinyinSortNameService(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _connectionCache = new ConnectionManagerCache(logger, "PinyinSortName");

            // 不要在这里调用 LoadPinyinOnce() —— 此时 TinyPinyin.dll 尚未
            // 被 Emby 扫描到。经 _getPinyinLazy 的延迟加载能正确处理
            // 时序，与 PinyinSearchService 的做法一致。
            EnsureBackupTable();

            _libraryManager.ItemAdded += OnItemChanged;
            _libraryManager.ItemUpdated += OnItemChanged;

            // 定时器以一次性模式启动；队列非空时由 EnsureEventTimer() 触发
            _eventTimer = new Timer(_ => ProcessQueuedEvents(), null, Timeout.Infinite, Timeout.Infinite);
        }

        // ── 公开的开关 API ──

        /// <summary>
        /// 启用：备份原始值 + 应用拼音首字母。
        /// 停用：还原原始值 + 清空备份。
        /// 可安全重复调用。
        /// </summary>
        public void SetEnabled(bool enable)
        {
            if (!IsPinyinLoaded)
            {
                _logger.Warn("[PinyinSortName] TinyPinyin not available, cannot toggle");
                return;
            }

            if (enable)
                Enable();
            else
                Disable();
        }

        private void Enable()
        {
            if (IsDisposed) return;
            _logger.Info("[PinyinSortName] Enabling...");
            EnsureBackupTable();
            BackfillAll();
        }

        private void Disable()
        {
            _logger.Info("[PinyinSortName] Disabling, restoring original sort names...");
            try
            {
                int total = RestoreAll();
                _logger.Info("[PinyinSortName] Restore complete: {0} items restored", total);
            }
            catch (Exception ex)
            {
                _logger.Error("[PinyinSortName] Restore failed", ex);
            }
        }

        /// <summary>
        /// 对所有现有中文名条目做全量回填。
        /// 在启动时调用一次，传 delayMs > 0 可推迟到 Emby 首次扫描之后。
        /// </summary>
        public void BackfillAll(int startupDelayMs = 0)
        {
            if (IsDisposed) return;

            Task.Run(async () =>
            {
                if (startupDelayMs > 0)
                {
                    _logger.Info("[PinyinSortName] Delaying backfill {0}ms for Emby startup...", startupDelayMs);
                    await Task.Delay(startupDelayMs);
                }

                // Lazy：把 TinyPinyin 的加载推迟到 Emby 启动之后，
                // 那时 Emby 的程序集扫描器已加载 TinyPinyin.dll。
                Func<char, string> getPinyin;
                try { getPinyin = TinyPinyinLoader.GetPinyinFunc; }
                catch
                {
                    _logger.Warn("[PinyinSortName] TinyPinyin not available, skipping backfill");
                    return;
                }

                if (IsDisposed) return;

                try
                {
                    _logger.Info("[PinyinSortName] Starting backfill...");
                    int total = ProcessBackfill();
                    if (total > 0)
                        _logger.Info("[PinyinSortName] Backfill complete: {0} items updated", total);
                    else
                        _logger.Info("[PinyinSortName] Backfill: no items needed updating");

                    // 回填后回收 WAL 空间 —— TRUNCATE 会等待读者
                    // 退出，然后完全重置 WAL 以防其膨胀。
                    WalCheckpointHelper.TryTruncateCheckpoint(_connectionCache, _logger, "PinyinSortName");
                }
                catch (Exception ex)
                {
                    _logger.Error("[PinyinSortName] Backfill failed", ex);
                }
            });
        }

        // ── 备份表 ──

        private void EnsureBackupTable()
        {
            try
            {
                using (var conn = _connectionCache.OpenWriteConnection())
                {
                    if (conn == null) return;
                    conn.Execute($@"
                        CREATE TABLE IF NOT EXISTS {BackupTable} (
                            ItemId INTEGER PRIMARY KEY,
                            OriginalSortName TEXT
                        )");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("[PinyinSortName] Backup table init: {0}", ex.Message);
            }
        }

        /// <summary>
        /// 在被覆盖之前，把原始 SortName 存入备份表。
        /// </summary>
        private void SaveOriginalSortName(IDatabaseConnection conn, long itemId, string originalSortName)
        {
            try
            {
                string escaped = TextUtil.Escape(originalSortName);
                if (originalSortName == null)
                    conn.Execute($"INSERT OR IGNORE INTO {BackupTable}(ItemId,OriginalSortName) VALUES({itemId},NULL)");
                else
                    conn.Execute($"INSERT OR IGNORE INTO {BackupTable}(ItemId,OriginalSortName) VALUES({itemId},'{escaped}')");
            }
            catch (Exception ex)
            {
                _logger.Warn("[PinyinSortName] Backup save failed for {0}: {1}", itemId, ex.Message);
            }
        }

        /// <summary>
        /// 还原所有已备份的 SortName，并清空备份表。
        /// </summary>
        private int RestoreAll()
        {
            using (var conn = _connectionCache.OpenWriteConnection())
            {
                if (conn == null) return 0;

                // 统计备份条目数
                long total;
                using (var stmt = conn.PrepareStatement($"SELECT COUNT(*) FROM {BackupTable}"))
                {
                    if (!stmt.MoveNext()) return 0;
                    total = stmt.Current.GetInt64(0);
                }

                if (total == 0)
                {
                    _logger.Info("[PinyinSortName] No backup entries found — clearing SortName to let Emby recalculate from Name");
                    return ClearAllChineseSortNames(conn);
                }

                _logger.Info("[PinyinSortName] Restoring {0} items...", total);

                conn.BeginTransaction(TransactionMode.Deferred);
                int restored = 0;
                try
                {
                    // 取出全部备份条目
                    var backupEntries = new List<Tuple<long, string>>();
                    using (var stmt = conn.PrepareStatement($"SELECT ItemId, OriginalSortName FROM {BackupTable}"))
                    {
                        while (stmt.MoveNext())
                        {
                            long id = stmt.Current.GetInt64(0);
                            string orig = stmt.Current.IsDBNull(1) ? null : stmt.Current.GetString(1);
                            backupEntries.Add(Tuple.Create(id, orig));
                        }
                    }

                    foreach (var entry in backupEntries)
                    {
                        if (IsDisposed) break;

                        if (entry.Item2 == null)
                            conn.Execute($"UPDATE MediaItems SET SortName = NULL WHERE RowId = {entry.Item1}");
                        else
                            conn.Execute($"UPDATE MediaItems SET SortName = '{TextUtil.Escape(entry.Item2)}' WHERE RowId = {entry.Item1}");
                        restored++;
                    }

                    // 清空备份表
                    conn.Execute($"DELETE FROM {BackupTable}");
                    conn.CommitTransaction();
                }
                catch (Exception ex)
                {
                    conn.RollbackTransaction();
                    _logger.Error("[PinyinSortName] Restore transaction failed", ex);
                    return 0;
                }

                return restored;
            }
        }

        // ── 回填处理 ──

        private int ProcessBackfill()
        {
            using (var conn = _connectionCache.OpenWriteConnection())
            {
                if (conn == null) return 0;

                // 统计符合条件且尚未进备份表的条目（即还没处理过的）
                long total;
                try
                {
                    using (var stmt = conn.PrepareStatement($@"
                        SELECT COUNT(*) FROM MediaItems mi
                        WHERE mi.Name GLOB '*[一-龥]*'
                          AND NOT EXISTS (SELECT 1 FROM {BackupTable} b WHERE b.ItemId = mi.RowId)"))
                    {
                        if (stmt.MoveNext())
                            total = stmt.Current.GetInt64(0);
                        else
                            return 0;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn("[PinyinSortName] Count query failed: {0}", ex.Message);
                    return 0;
                }

                if (total == 0)
                {
                    // 所有条目都已处理过 —— 只更新发生变更的那些
                    _logger.Info("[PinyinSortName] All items already backed up, re-applying pending updates...");
                    return 0;
                }

                _logger.Info("[PinyinSortName] {0} new Chinese-named items to process", total);

                int processed = 0;
                long offset = 0;

                while (offset < total && !IsDisposed)
                {
                    int batch = ProcessBatch(conn, offset, BatchSize);
                    if (batch == 0) break;
                    processed += batch;
                    offset += BatchSize;

                    if (!IsDisposed && offset < total)
                        Thread.Sleep(300);
                }

                return processed;
            }
        }

        /// <summary>
        /// 若不存在备份，则把 SortName 置为 Name（Emby 对中文的默认行为）。
        /// </summary>
        private int ClearAllChineseSortNames(IDatabaseConnection conn)
        {
            try
            {
                long total;
                using (var stmt = conn.PrepareStatement(
                    "SELECT COUNT(*) FROM MediaItems WHERE Name GLOB '*[一-龥]*'"))
                {
                    if (!stmt.MoveNext()) return 0;
                    total = stmt.Current.GetInt64(0);
                }

                if (total == 0) return 0;

                long limit = total;
                int cleared = 0;
                long offset = 0;

                while (offset < limit && !IsDisposed)
                {
                    conn.Execute(
                        $"UPDATE MediaItems SET SortName = SUBSTR(Name, 1, 50) WHERE RowId IN (SELECT RowId FROM MediaItems WHERE Name GLOB '*[一-龥]*' AND SortName IS NOT NULL ORDER BY RowId LIMIT {BatchSize} OFFSET {offset})");
                    cleared += BatchSize;
                    offset += BatchSize;

                    if (!IsDisposed && offset < limit)
                        Thread.Sleep(300);
                }

                return cleared;
            }
            catch (Exception ex)
            {
                _logger.Warn("[PinyinSortName] Clear failed: {0}", ex.Message);
                return 0;
            }
        }

        private int ProcessBatch(IDatabaseConnection conn, long offset, int limit)
        {
            try
            {
                var rows = new List<Tuple<long, string, string>>();

                using (var stmt = conn.PrepareStatement($@"
                    SELECT mi.RowId, mi.Name, mi.SortName
                    FROM MediaItems mi
                    WHERE mi.Name GLOB '*[一-龥]*'
                      AND NOT EXISTS (SELECT 1 FROM {BackupTable} b WHERE b.ItemId = mi.RowId)
                    ORDER BY mi.RowId
                    LIMIT {limit} OFFSET {offset}"))
                {
                    while (stmt.MoveNext())
                    {
                        long id = stmt.Current.GetInt64(0);
                        string name = stmt.Current.GetString(1);
                        string sortName = stmt.Current.IsDBNull(2) ? null : stmt.Current.GetString(2);
                        rows.Add(Tuple.Create(id, name, sortName));
                    }
                }

                if (rows.Count == 0) return 0;

                conn.BeginTransaction(TransactionMode.Deferred);
                int count = 0;
                try
                {
                    foreach (var row in rows)
                    {
                        if (IsDisposed) break;

                        string desired = BuildPinyinSortName(row.Item2);
                        if (desired == null) continue;

                        // 先把原始 SortName 存入备份
                        SaveOriginalSortName(conn, row.Item1, row.Item3);

                        // 应用拼音排序名
                        conn.Execute(
                            $"UPDATE MediaItems SET SortName = '{TextUtil.Escape(desired)}' WHERE RowId = {row.Item1}");
                        count++;
                    }
                    conn.CommitTransaction();

                    if (count > 0)
                        _logger.Debug("[PinyinSortName] Batch offset={0}: {1} items updated", offset, count);
                }
                catch (Exception ex)
                {
                    conn.RollbackTransaction();
                    _logger.Warn("[PinyinSortName] Batch offset={0} failed: {1}", offset, ex.Message);
                }

                return count;
            }
            catch (Exception ex)
            {
                _logger.Warn("[PinyinSortName] Batch query failed at offset={0}: {1}", offset, ex.Message);
                return 0;
            }
        }

        // ── 事件处理器（不碰 SQL，经定时器批量处理 —— 与 FtsScanScheduler 同一套路） ──

        private void OnItemChanged(object sender, ItemChangeEventArgs e)
        {
            if (IsDisposed || !IsPinyinLoaded) return;
            if (e.Item == null || string.IsNullOrEmpty(e.Item.Name)) return;
            if (!IsEligibleItem(e.Item)) return;
            if (!TextUtil.ChineseRegex.IsMatch(e.Item.Name)) return;

            string desired = BuildPinyinSortName(e.Item.Name);
            if (desired == null) return;

            // 只入队；所有 SQL 都在 ProcessQueuedEvents 的同一个写事务
            // 里执行。队列溢出或已销毁时条目会被丢弃。
            _pendingEventQueue.Enqueue(Tuple.Create(e.Item.InternalId, desired));
            EnsureEventTimer();
        }

        private void ProcessQueuedEvents()
        {
            if (IsDisposed) return;

            var items = new List<Tuple<long, string>>(EventBatchSize);
            while (items.Count < EventBatchSize && _pendingEventQueue.TryDequeue(out var entry))
                items.Add(entry);

            if (items.Count == 0)
            {
                Interlocked.Exchange(ref _eventTimerRunning, 0);
                return;
            }

            int updated = 0;
            using (var conn = _connectionCache.OpenWriteConnection())
            {
                if (conn == null)
                {
                    foreach (var entry in items)
                        _pendingEventQueue.Enqueue(entry);
                    Interlocked.Exchange(ref _eventTimerRunning, 0);
                    return;
                }

                conn.BeginTransaction(TransactionMode.Deferred);
                try
                {
                    foreach (var entry in items)
                    {
                        long id = entry.Item1;
                        string desired = entry.Item2;
                        try
                        {
                            if (IsDisposed) break;

                            // 读出当前的 SortName
                            string current = null;
                            using (var stmt = conn.PrepareStatement(
                                $"SELECT SortName FROM MediaItems WHERE RowId = {id}"))
                            {
                                if (stmt.MoveNext() && !stmt.Current.IsDBNull(0))
                                    current = stmt.Current.GetString(0);
                            }

                            if (string.Equals(current, desired, StringComparison.Ordinal))
                                continue;

                            // 若尚未备份，则备份原始值
                            SaveOriginalSortName(conn, id, current);

                            conn.Execute(
                                $"UPDATE MediaItems SET SortName = '{TextUtil.Escape(desired)}' WHERE RowId = {id}");
                            updated++;
                            _logger.Debug("[PinyinSortName] Updated sort: RowId={0} -> '{1}'", id, desired);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn("[PinyinSortName] Queue batch item {0}: {1}", id, ex.Message);
                        }
                    }
                    conn.CommitTransaction();
                    _logger.Debug("[PinyinSortName] Queue batch: {0} items processed", items.Count);
                }
                catch (Exception ex)
                {
                    conn.RollbackTransaction();
                    _logger.Error("[PinyinSortName] Queue batch transaction failed, rolled back: {0}", ex.Message);
                    foreach (var entry in items)
                        _pendingEventQueue.Enqueue(entry);
                }
            }

            // 批量写入后回收 WAL 空间 —— TRUNCATE 会等待读者
            // 退出，然后完全重置 WAL 以防其膨胀。
            if (updated > 0)
                WalCheckpointHelper.TryTruncateCheckpoint(_connectionCache, _logger, "PinyinSortName");

            // 先释放重挂守卫；若本批处理期间又有条目进来，
            // 则重新挂上（避免过期守卫把队列卡死）。
            Interlocked.Exchange(ref _eventTimerRunning, 0);
            if (!_pendingEventQueue.IsEmpty)
                EnsureEventTimer();
        }

        private void EnsureEventTimer(int delayMs = EventTimerIntervalMs)
        {
            if (Interlocked.CompareExchange(ref _eventTimerRunning, 1, 0) == 0)
                _eventTimer?.Change(delayMs, Timeout.Infinite);
        }

        // ── 资格判定 ──

        private static bool IsEligibleItem(BaseItem item)
        {
            if (item == null || !item.SupportsUserData || !item.EnableAlphaNumericSorting)
                return false;

            if (item is IHasSeries) return false;
            if (item.IsFieldLocked(MetadataFields.SortName)) return false;

            return item is Video || item is Audio || item is IItemByName || item is Folder;
        }

        // ── 拼音排序名生成 ──

        public static string BuildPinyinSortName(string source)
        {
            if (string.IsNullOrEmpty(source) || !TextUtil.ChineseRegex.IsMatch(source))
                return null;

            if (!IsPinyinLoaded) return null;

            var sb = new StringBuilder(source.Length);
            bool hasChinese = false;

            for (int i = 0; i < source.Length; i++)
            {
                char ch = source[i];
                if (ch >= 0x4e00 && ch <= 0x9fff)
                {
                    try
                    {
                        var pinyin = TinyPinyinLoader.GetPinyinFunc(ch);
                        if (!string.IsNullOrEmpty(pinyin) && pinyin.Length > 0)
                        {
                            sb.Append(char.ToUpperInvariant(pinyin[0]));
                            hasChinese = true;
                        }
                    }
                    catch
                    {
                        // 跳过 TinyPinyin 处理不了的字符
                    }
                }
                else if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                }
            }

            return hasChinese ? sb.ToString() : null;
        }

        // ── 辅助方法 ──

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

            _eventTimer?.Dispose();
            _eventTimer = null;

            _libraryManager.ItemAdded -= OnItemChanged;
            _libraryManager.ItemUpdated -= OnItemChanged;
        }
    }
}
