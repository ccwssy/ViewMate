using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;
using System.Collections.Generic;
using System.Reflection;
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
        // Lazy: deferred until first actual use, by which time TinyPinyin.dll
        // is guaranteed to be loaded by Emby's assembly scanner.
        private static readonly Lazy<Func<char, string>> _getPinyinLazy =
            new Lazy<Func<char, string>>(LoadPinyinFunc, LazyThreadSafetyMode.ExecutionAndPublication);
        private static bool IsPinyinLoaded
        {
            get
            {
                try { return _getPinyinLazy.Value != null; }
                catch { return false; }
            }
        }

        private int _disposed;
        private bool IsDisposed => Interlocked.CompareExchange(ref _disposed, 0, 0) == 1;

        private const int BatchSize = 200;

        public PinyinSortNameService(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _connectionCache = new ConnectionManagerCache(logger, "PinyinSortName");

            // Do NOT call LoadPinyinOnce() here — TinyPinyin.dll hasn't been
            // scanned by Emby yet. Deferred loading via _getPinyinLazy handles
            // the timing correctly, matching PinyinSearchService's approach.
            EnsureBackupTable();

            _libraryManager.ItemAdded += OnItemChanged;
            _libraryManager.ItemUpdated += OnItemChanged;
        }

        private static Func<char, string> LoadPinyinFunc()
        {
            string[] probePaths =
            {
                "/config/plugins/TinyPinyin.dll",
                "/system/TinyPinyin.dll",
                "plugins/TinyPinyin.dll",
                "../plugins/TinyPinyin.dll",
            };

            Assembly asm = null;
            foreach (var path in probePaths)
            {
                if (System.IO.File.Exists(path))
                {
                    asm = Assembly.Load(System.IO.File.ReadAllBytes(path));
                    break;
                }
            }

            if (asm == null)
                throw new System.IO.FileNotFoundException("TinyPinyin.dll not found in any probe path");

            var helperType = asm.GetType("TinyPinyin.PinyinHelper");
            if (helperType == null)
                throw new System.IO.FileNotFoundException("TinyPinyin.PinyinHelper type not found");

            var method = helperType.GetMethod("GetPinyin", new[] { typeof(char) });
            if (method == null)
                throw new MissingMethodException("GetPinyin method not found");

            return (Func<char, string>)Delegate.CreateDelegate(
                typeof(Func<char, string>), null, method);
        }

        // ── Public toggle API ──

        /// <summary>
        /// Enable: backup originals + apply pinyin initials.
        /// Disable: restore originals + clear backup.
        /// Safe to call multiple times.
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
        /// Full backfill of all existing Chinese-named items.
        /// Call once on startup with delayMs > 0 to defer after Emby initial scan.
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

                // Lazy: defer TinyPinyin loading until after Emby startup,
                // by which time Emby's assembly scanner has loaded TinyPinyin.dll.
                Func<char, string> getPinyin;
                try { getPinyin = _getPinyinLazy.Value; }
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

                    // Recover WAL space after backfill — TRUNCATE waits for readers
                    // to drain, then fully resets the WAL to prevent bloat.
                    WalCheckpointHelper.TryTruncateCheckpoint(_connectionCache, _logger, "PinyinSortName");
                }
                catch (Exception ex)
                {
                    _logger.Error("[PinyinSortName] Backfill failed", ex);
                }
            });
        }

        // ── Backup table ──

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
        /// Save original SortName to backup table before it gets overwritten.
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
        /// Restore all backed-up SortNames and clear the backup table.
        /// </summary>
        private int RestoreAll()
        {
            using (var conn = _connectionCache.OpenWriteConnection())
            {
                if (conn == null) return 0;

                // Count backup entries
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
                    // Fetch all backup entries
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

                    // Clear backup table
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

        // ── Backfill processing ──

        private int ProcessBackfill()
        {
            using (var conn = _connectionCache.OpenWriteConnection())
            {
                if (conn == null) return 0;

                // Count eligible items NOT already in backup (not yet processed)
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
                    // All items already processed — just update any that got changed
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
        /// When no backup exists, set SortName to Name (Emby default for Chinese).
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

                        // Save original SortName to backup first
                        SaveOriginalSortName(conn, row.Item1, row.Item3);

                        // Apply pinyin sort name
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

        // ── Event handler ──

        private void OnItemChanged(object sender, ItemChangeEventArgs e)
        {
            if (IsDisposed || !IsPinyinLoaded) return;
            if (e.Item == null || string.IsNullOrEmpty(e.Item.Name)) return;
            if (!IsEligibleItem(e.Item)) return;
            if (!TextUtil.ChineseRegex.IsMatch(e.Item.Name)) return;

            string desired = BuildPinyinSortName(e.Item.Name);
            if (desired == null) return;

            Task.Run(() =>
            {
                try
                {
                    using (var conn = _connectionCache.OpenWriteConnection())
                    {
                        if (conn == null) return;

                        // Read current SortName
                        string current = null;
                        using (var stmt = conn.PrepareStatement(
                            $"SELECT SortName FROM MediaItems WHERE RowId = {e.Item.InternalId}"))
                        {
                            if (stmt.MoveNext() && !stmt.Current.IsDBNull(0))
                                current = stmt.Current.GetString(0);
                        }

                        if (string.Equals(current, desired, StringComparison.Ordinal))
                            return;

                        // Backup original if not already backed up
                        SaveOriginalSortName(conn, e.Item.InternalId, current);

                        conn.Execute(
                            $"UPDATE MediaItems SET SortName = '{TextUtil.Escape(desired)}' WHERE RowId = {e.Item.InternalId}");
                        _logger.Debug("[PinyinSortName] Updated sort: '{0}' -> '{1}'", e.Item.Name, desired);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn("[PinyinSortName] Event update failed for '{0}': {1}", e.Item.Name, ex.Message);
                }
            });
        }

        // ── Eligibility ──

        private static bool IsEligibleItem(BaseItem item)
        {
            if (item == null || !item.SupportsUserData || !item.EnableAlphaNumericSorting)
                return false;

            if (item is IHasSeries) return false;
            if (item.IsFieldLocked(MetadataFields.SortName)) return false;

            return item is Video || item is Audio || item is IItemByName || item is Folder;
        }

        // ── Pinyin sort name generation ──

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
                        var pinyin = _getPinyinLazy.Value(ch);
                        if (!string.IsNullOrEmpty(pinyin) && pinyin.Length > 0)
                        {
                            sb.Append(char.ToUpperInvariant(pinyin[0]));
                            hasChinese = true;
                        }
                    }
                    catch
                    {
                        // Skip chars that TinyPinyin can't handle
                    }
                }
                else if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                }
            }

            return hasChinese ? sb.ToString() : null;
        }

        // ── Helpers ──

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
            _libraryManager.ItemAdded -= OnItemChanged;
            _libraryManager.ItemUpdated -= OnItemChanged;
        }
    }
}
