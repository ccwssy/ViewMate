using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ViewMate.Common;

#nullable enable
namespace ViewMate.Pinyin
{
    /// <summary>
    /// 扫描调度器（PinyinSearchService 的第二阶段拆分）：持有事件
    /// 队列（OnItemAdded/OnItemUpdated + ProcessQueuedEvents）、两个定时器
    /// （事件批量定时器 + 5 分钟周期定时器），以及四条扫描路径 ——
    /// 增量（ProcessAllPendingBatched）、全量重建索引、追赶与
    /// 周期扫描。FTS 写入委托给 FtsIndexWriter，拼音生成委托给
    /// TinyPinyinLoader；WAL checkpoint 的时机（批量写入后用 TRUNCATE，
    /// 周期扫描后用 PASSIVE）在此保持原样。
    /// </summary>
    public class FtsScanScheduler : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ConnectionManagerCache _connectionCache;
        private readonly FtsIndexWriter _writer;

        // 线程安全的销毁标志
        private int _disposed;
        private bool IsDisposed => Interlocked.CompareExchange(ref _disposed, 0, 0) == 1;

        // ── 事件队列 ──
        private readonly ConcurrentQueue<Tuple<long, string>> _pendingEventQueue = new ConcurrentQueue<Tuple<long, string>>();
        private Timer? _eventTimer;
        private int _eventTimerRunning;
        private const int EventBatchSize = 50;
        private const int EventTimerIntervalMs = 30000;
        private const int PeriodicScanIntervalMs = 300000; // 5 分钟

        // ── 周期性的后台扫描 ──
        private Timer? _periodicTimer;

        // ── 批量状态 ──
        private long _lastScanId;

        // ── 首字母回填状态（按游标分页，搭周期扫描的车） ──
        private long _backfillCursor;
        private const int BackfillLimit = 1000;
        private const int BackfillBatchSize = 200;

        // 游标持久化：探测路径式，与 TinyPinyinLoader 的
        // pinyin-overrides.json 同一先例（第一个存在的目录胜出）。
        private const string BackfillCursorFileName = "pinyin-backfill-cursor.txt";
        private static readonly string[] BackfillCursorProbeDirs = { "/config/plugins/", "plugins/", "../plugins/" };
        private int _cursorPersistDirWarned;

        public FtsScanScheduler(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _connectionCache = new ConnectionManagerCache(logger, "PinyinSearch");
            _writer = new FtsIndexWriter(logger);

            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemUpdated;

            // 定时器以一次性模式启动；队列非空时由 EnsureEventTimer() 触发
            _eventTimer = new Timer(_ => ProcessQueuedEvents(), null, Timeout.Infinite, Timeout.Infinite);

            // 在第一轮回填运行之前先恢复回填游标。
            LoadBackfillCursor();
        }

        // ── 延后的后台扫描 ──

        public void ProcessAllPendingDeferred()
        {
            // 所有 FTS 操作都在后台线程上执行，以免阻塞
            // Plugin.Run() 并拖慢 Emby HTTP 服务的启动。
            // 此前用同步重试循环会阻塞 Plugin.Run() 长达 120 秒，
            // 导致首页卡死 —— 这个 bug 在 5 个版本里反复出现。
            // 后台线程用 Thread.Sleep（而非 Task.Delay），以避开
            // Emby 4.9.5.0 单线程同步上下文上的死锁，并
            // 在每轮迭代时检查 IsDisposed，以便干净退出。
            var scanThread = new Thread(() =>
            {
                _logger.Info("[PinyinSearch] Background thread started, waiting 60s...");
                for (int i = 0; i < 60 && !IsDisposed; i++)
                    Thread.Sleep(1000);

                _logger.Info("[PinyinSearch] Wait loop exited, IsDisposed={0}", IsDisposed);

                if (!IsDisposed)
                {
                    try
                    {
                        int total = ProcessAllPendingBatched();
                        if (total > 0)
                            _logger.Info("[PinyinSearch] Catch-up scan: {0} items processed", total);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("[PinyinSearch] Catch-up scan failed", ex);
                    }

                    // 首次扫描后回收 WAL 空间：完整 checkpoint 会等待
                    // 活跃读者退出，然后截断 WAL 以防其膨胀。
                    WalCheckpointHelper.TryTruncateCheckpoint(_connectionCache, _logger, "PinyinSearch");

                    // 首次扫描完成后启动周期定时器做追赶扫描。
                    // 用于兜住媒体库扫描新增、却没有触发 ItemAdded/Updated 事件的条目。
                    if (!IsDisposed)
                    {
                        _logger.Info("[PinyinSearch] Starting periodic scan every 5 minutes...");
                        _periodicTimer = new Timer(
                            _ => ProcessPeriodicScan(),
                            null,
                            PeriodicScanIntervalMs,
                            PeriodicScanIntervalMs);
                    }
                }
            });
            scanThread.IsBackground = true;
            scanThread.Start();
        }

        private bool TryGetPendingCount(out long count)
        {
            count = 0;
            try
            {
                using (var conn = _connectionCache.OpenReadConnection())
                {
                    if (conn == null) return false;

                    using (var stmt = conn.PrepareStatement(FtsIndexWriter.PendingCountQuery(Volatile.Read(ref _lastScanId))))
                    {
                        if (stmt.MoveNext())
                            count = stmt.Current.GetInt64(0);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("[PinyinSearch] Failed to get pending count: {0}", ex.Message);
                return false;
            }
        }

        private bool TryGetCatchUpCount(out long count)
        {
            count = 0;
            try
            {
                using (var conn = _connectionCache.OpenReadConnection())
                {
                    if (conn == null) return false;

                    using (var stmt = conn.PrepareStatement(FtsIndexWriter.CatchUpCountQuery()))
                    {
                        if (stmt.MoveNext())
                            count = stmt.Current.GetInt64(0);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("[PinyinSearch] Failed to get catch-up count: {0}", ex.Message);
                return false;
            }
        }

        private int ProcessAllPendingBatched()
        {
            // 阶段 0：FTS 为空 → 全表扫描 MediaItems
            long ftsTotal = GetFtsTotalCount();
            if (ftsTotal == 0)
            {
                _logger.Info("[PinyinSearch] fts_search9 is empty, scanning MediaItems directly...");
                return ProcessFullReindex();
            }

            // 阶段 0.5：存在缺失的 MediaItems → 全量重建索引
            long missingCount = GetMissingMediaItemsCount();
            if (missingCount > 0)
            {
                _logger.Info("[PinyinSearch] {0} MediaItems without FTS entry, full reindex needed", missingCount);
                return ProcessFullReindex();
            }

            if (!TryGetPendingCount(out long totalPending) || totalPending == 0)
            {
                _logger.Info("[PinyinSearch] No pending items, checking for catch-up items...");
                // 仅当增量扫描发现 0 个条目时才做追赶扫描
                if (TryGetCatchUpCount(out long catchUpTotal) && catchUpTotal > 0)
                {
                    _logger.Info("[PinyinSearch] Found {0} catch-up items (missing from incremental scan)", catchUpTotal);
                    return ProcessCatchUpBatched();
                }
                _logger.Info("[PinyinSearch] No pending items");
                return 0;
            }

            long actualTotal = Math.Min(totalPending, FtsIndexWriter.MaxPendingTotal);
            _logger.Info("[PinyinSearch] {0} pending items, processing in batches of {1}...",
                actualTotal, FtsIndexWriter.BatchSize);

            int totalProcessed = 0;
            long offset = 0;

            while (offset < actualTotal && !IsDisposed)
            {
                int batch = ProcessBatch(offset, FtsIndexWriter.BatchSize);
                totalProcessed += batch;
                offset += FtsIndexWriter.BatchSize;

                if (offset < actualTotal)
                    Thread.Sleep(500);
            }

            if (!IsDisposed)
                _logger.Debug("[PinyinSearch] Batch scan complete, skipping redundant FTS rebuild");

            UpdateLastScanId();
            return totalProcessed;
        }

        // ── 周期后台扫描回调 ──
        // 先跑追赶扫描（不论 ID 一律兜住所有过期条目），
        // 再跑增量扫描，处理上次运行之后新增的条目。
        private void ProcessPeriodicScan()
        {
            if (IsDisposed) return;

            try
            {
                int total = 0;

                // 阶段 1：追赶扫描 —— 处理所有过期条目（不限 ID）
                if (TryGetCatchUpCount(out long catchUpCount) && catchUpCount > 0)
                {
                    _logger.Info("[PinyinSearch] Periodic catch-up: {0} stale entries", catchUpCount);
                    total += ProcessCatchUpBatched();
                }

                // 阶段 2：增量扫描 —— 上次扫描之后的新条目
                int incremental = ProcessAllPendingBatched();
                total += incremental;

                // 阶段 3：回填 —— 旧版本写入的行缺少
                // 首字母 token（或已漂移）；按游标把它们重写一遍。
                total += ProcessBackfill();

                if (total > 0)
                {
                    _logger.Info("[PinyinSearch] Periodic scan: {0} items processed", total);
                    // 写操作密集的周期扫描之后做被动 checkpoint —— 在不阻塞
                    // 并发读者的前提下尽量清理。
                    WalCheckpointHelper.TryPassiveCheckpoint(_connectionCache);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[PinyinSearch] Periodic scan failed", ex);
            }
        }

        // ── 首字母回填（按游标分页，搭周期扫描的车） ──
        // 按 id 以 1000 行一页遍历 fts_search9_content，重写所有
        // c0 与新构建的完整值不一致的行（缺首字母、
        // 首拼音漂移、被 Emby 触发器覆盖 —— 任何情况）。比较刻意
        // 放在 C# 侧做整串比较：像 c0 NOT GLOB '*[a-z]*'
        // 这样的 SQL 判据会漏掉含小写字母的原始名称。
        private int ProcessBackfill()
        {
            long cursor = Volatile.Read(ref _backfillCursor);
            var rows = new List<BackfillRow>();

            try
            {
                using (var conn = _connectionCache.OpenReadConnection())
                {
                    if (conn == null) return 0;

                    using (var stmt = conn.PrepareStatement(FtsIndexWriter.BackfillQuery(cursor, BackfillLimit)))
                    {
                        while (stmt.MoveNext())
                        {
                            long id = stmt.Current.GetInt64(0);
                            string name = stmt.Current.GetString(1);
                            string currentC0 = stmt.Current.GetString(2);
                            var (spaced, connected, bigrams, singleChars, cjkBigrams, initials, initialsBigrams) =
                                TinyPinyinLoader.GeneratePinyin(name);

                            // TinyPinyin 不可用 → 放弃整轮，
                            // 游标原地不动；绝不在没有拼音的情况下重写任何行。
                            if (string.IsNullOrEmpty(spaced))
                            {
                                _logger.Warn("[PinyinSearch] Backfill abandoned: no pinyin for id {0}", id);
                                return 0;
                            }

                            rows.Add(new BackfillRow
                            {
                                Id = id,
                                Name = name,
                                Spaced = spaced,
                                Connected = connected,
                                Bigrams = bigrams,
                                SingleChars = singleChars,
                                CjkBigrams = cjkBigrams,
                                Initials = initials,
                                InitialsBigrams = initialsBigrams,
                                ExpectedC0 = FtsIndexWriter.BuildFtsNameColumn(name, spaced, connected, bigrams, singleChars, cjkBigrams, initials, initialsBigrams),
                                CurrentC0 = currentC0,
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("[PinyinSearch] Backfill query failed: {0}", ex.Message);
                return 0;
            }

            if (rows.Count == 0) return 0;

            int updated = 0;
            for (int offset = 0; offset < rows.Count; offset += BackfillBatchSize)
            {
                if (IsDisposed) return updated;

                int count = Math.Min(BackfillBatchSize, rows.Count - offset);
                long maxId = 0;

                using (var conn = _connectionCache.OpenWriteConnection())
                {
                    if (conn == null) return updated;
                    try
                    {
                        conn.BeginTransaction(TransactionMode.Deferred);
                        for (int i = 0; i < count; i++)
                        {
                            var row = rows[offset + i];
                            if (row.Id > maxId) maxId = row.Id;
                            if (row.ExpectedC0 == row.CurrentC0) continue;

                            _writer.ReadExistingColumns(conn, row.Id,
                                "[PinyinSearch] Backfill read existing columns for id {0}: {1}",
                                out string origTitle, out string seriesName, out string album);
                            _writer.ExecuteInsert(conn, row.Id, row.Name, row.Spaced, row.Connected,
                                row.Bigrams, row.SingleChars, row.CjkBigrams, row.Initials, row.InitialsBigrams,
                                origTitle, seriesName, album);
                            updated++;
                        }
                        conn.CommitTransaction();
                    }
                    catch (Exception ex)
                    {
                        conn.RollbackTransaction();
                        _logger.Error("[PinyinSearch] Backfill batch offset={0} failed, rolled back: {1}", offset, ex.Message);
                        return updated;
                    }
                }

                // 仅在批次提交之后才前进游标并持久化，这样重启后
                // 从这里继续，而不是从 id 0 重新遍历整张表。
                Volatile.Write(ref _backfillCursor, maxId);
                PersistBackfillCursor(maxId);

                if (offset + BackfillBatchSize < rows.Count)
                    Thread.Sleep(500);
            }

            if (updated > 0)
                _logger.Info("[PinyinSearch] Backfill: {0} rows rewritten, cursor={1}",
                    updated, Volatile.Read(ref _backfillCursor));

            return updated;
        }

        // ── 回填游标持久化 ──
        // 游标只存内存时，每次 Emby 重启都会从 id 0 重新遍历 FTS 表：
        // 在重启间隔短于完整回填耗时（约 85 分钟）的服务器上，
        // 回填永远跑不完。格式：单个 long 值，UTF-8 无 BOM，末尾带换行。
        private void LoadBackfillCursor()
        {
            foreach (var dir in BackfillCursorProbeDirs)
            {
                string path = Path.Combine(dir, BackfillCursorFileName);
                try
                {
                    if (!File.Exists(path)) continue;

                    if (long.TryParse(File.ReadAllText(path).Trim(), out long value))
                    {
                        Volatile.Write(ref _backfillCursor, value);
                        _logger.Info("[PinyinSearch] Backfill cursor restored from disk: {0}", value);
                    }
                    else
                    {
                        _logger.Warn("[PinyinSearch] Backfill cursor file unparsable, keeping cursor at 0: {0}", path);
                    }

                    return; // 找到文件 —— 这里就是权威位置。
                }
                catch (Exception ex)
                {
                    _logger.Warn("[PinyinSearch] Backfill cursor restore failed ({0}): {1}", path, ex.Message);
                    return;
                }
            }
        }

        private void PersistBackfillCursor(long cursor)
        {
            foreach (var dir in BackfillCursorProbeDirs)
            {
                if (!Directory.Exists(dir)) continue;

                string path = Path.Combine(dir, BackfillCursorFileName);
                try
                {
                    string tmpPath = path + ".tmp";
                    File.WriteAllText(tmpPath, cursor.ToString(CultureInfo.InvariantCulture) + "\n", new UTF8Encoding(false));
                    File.Move(tmpPath, path, true);
                }
                catch (Exception ex)
                {
                    _logger.Warn("[PinyinSearch] Backfill cursor persist failed ({0}): {1}", path, ex.Message);
                }

                return;
            }

            if (Interlocked.Exchange(ref _cursorPersistDirWarned, 1) == 0)
                _logger.Warn("[PinyinSearch] Backfill cursor not persisted: none of the probe directories exist");
        }

        private sealed class BackfillRow
        {
            public long Id;
            public string Name = "";
            public string Spaced = "";
            public string Connected = "";
            public string Bigrams = "";
            public string SingleChars = "";
            public string CjkBigrams = "";
            public string Initials = "";
            public string InitialsBigrams = "";
            public string ExpectedC0 = "";
            public string CurrentC0 = "";
        }

        // ── FTS 为空的兜底 ──
        private long GetFtsTotalCount()
        {
            try
            {
                using (var conn = _connectionCache.OpenReadConnection())
                {
                    if (conn == null) return -1;
                    using (var stmt = conn.PrepareStatement(FtsIndexWriter.FtsTotalCountQuery()))
                    {
                        if (stmt.MoveNext())
                            return stmt.Current.GetInt64(0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("[PinyinSearch] Failed to get FTS total count: {0}", ex.Message);
            }
            return -1;
        }

        private long GetMissingMediaItemsCount()
        {
            try
            {
                using (var conn = _connectionCache.OpenReadConnection())
                {
                    if (conn == null) return 0;
                    using (var stmt = conn.PrepareStatement(FtsIndexWriter.MissingMediaItemsCountQuery()))
                    {
                        if (stmt.MoveNext())
                            return stmt.Current.GetInt64(0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("[PinyinSearch] Failed to get missing media items count: {0}", ex.Message);
            }
            return 0;
        }

        private int ProcessFullReindex()
        {
            long totalItems = 0;
            try
            {
                using (var conn = _connectionCache.OpenReadConnection())
                {
                    if (conn == null) return 0;
                    using (var stmt = conn.PrepareStatement(FtsIndexWriter.MediaItemsCountQuery()))
                    {
                        if (stmt.MoveNext()) totalItems = stmt.Current.GetInt64(0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[PinyinSearch] Full reindex count failed: {0}", ex.Message);
                return 0;
            }

            if (totalItems == 0)
            {
                _logger.Info("[PinyinSearch] No Chinese-named items in MediaItems");
                return 0;
            }

            long limit = totalItems;
            _logger.Info("[PinyinSearch] Full reindex: {0} items from MediaItems", limit);

            int totalProcessed = 0;
            long offset = 0;

            while (offset < limit && !IsDisposed)
            {
                int batch = ProcessMediaItemsBatch(offset, FtsIndexWriter.BatchSize);
                totalProcessed += batch;
                offset += FtsIndexWriter.BatchSize;

                if (offset < limit)
                    Thread.Sleep(500);
            }

            UpdateLastScanId();
            return totalProcessed;
        }

        private int ProcessMediaItemsBatch(long offset, int limit)
        {
            using (var conn = _connectionCache.OpenWriteConnection())
            {
                if (conn == null) return 0;

                try
                {
                    var rows = new List<Tuple<long, string>>();
                    using (var stmt = conn.PrepareStatement(FtsIndexWriter.MediaItemsBatchQuery(offset, limit)))
                    {
                        while (stmt.MoveNext())
                            rows.Add(Tuple.Create(stmt.Current.GetInt64(0), stmt.Current.GetString(1)));
                    }

                    if (rows.Count == 0) return 0;

                    return _writer.WriteBatch(conn, rows,
                        readExistingColumns: false,
                        readColumnsLogFormat: "",
                        itemLogFormat: null,
                        debugLogFormat: "[PinyinSearch] MediaItems batch offset={0}: {1} items",
                        errorLogFormat: "[PinyinSearch] MediaItems batch offset={0} failed: {1}",
                        logOffset: offset,
                        shouldContinue: () => !IsDisposed);
                }
                catch (Exception ex)
                {
                    _logger.Error("[PinyinSearch] MediaItems batch query failed at offset={0}: {1}", offset, ex.Message);
                    return 0;
                }
            }
        }

        private void UpdateLastScanId()
        {
            try
            {
                using (var conn = _connectionCache.OpenReadConnection())
                {
                    if (conn == null) return;
                    using (var stmt = conn.PrepareStatement($"SELECT MAX(id) FROM {FtsIndexWriter.FtsTableName}_content"))
                    {
                        if (stmt.MoveNext() && !stmt.Current.IsDBNull(0))
                            Volatile.Write(ref _lastScanId, stmt.Current.GetInt64(0));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("[PinyinSearch] Failed to update last scan ID: {0}", ex.Message);
            }
        }

        private int ProcessBatch(long offset, int limit)
        {
            using (var conn = _connectionCache.OpenWriteConnection())
            {
                if (conn == null) return 0;

                try
                {
                    var rows = new List<Tuple<long, string>>();
                    var query = FtsIndexWriter.PendingQuery(Volatile.Read(ref _lastScanId)) + $" LIMIT {limit} OFFSET {offset}";
                    using (var stmt = conn.PrepareStatement(query))
                    {
                        while (stmt.MoveNext())
                            rows.Add(Tuple.Create(stmt.Current.GetInt64(0), stmt.Current.GetString(1)));
                    }

                    if (rows.Count == 0) return 0;

                    return _writer.WriteBatch(conn, rows,
                        readExistingColumns: true,
                        readColumnsLogFormat: "[PinyinSearch] Batch read existing columns for id {0}: {1}",
                        itemLogFormat: "[PinyinSearch] Item {0}: {1}",
                        debugLogFormat: "[PinyinSearch] Batch offset={0}: {1} items",
                        errorLogFormat: "[PinyinSearch] Batch offset={0} failed, rolled back: {1}",
                        logOffset: offset,
                        shouldContinue: () => !IsDisposed);
                }
                catch (Exception ex)
                {
                    _logger.Error("[PinyinSearch] Batch query failed at offset={0}: {1}", offset, ex.Message);
                    return 0;
                }
            }
        }

        // ── 追赶扫描（不带 id 过滤） ──
        // 线程安全地跟踪已处理的追赶条目，避免重复处理
        private readonly HashSet<long> _processedCatchUpIds = new HashSet<long>();
        private readonly object _processedCatchUpLock = new object();

        private int ProcessCatchUpBatched()
        {
            long offset = 0;
            int totalProcessed = 0;
            _logger.Info("[PinyinSearch] Starting catch-up scan in batches of {0}...", FtsIndexWriter.BatchSize);

            while (!IsDisposed)
            {
                int batch = ProcessCatchUpBatch((int)offset);
                if (batch == 0) break;
                totalProcessed += batch;
                offset += FtsIndexWriter.BatchSize;

                if (!IsDisposed)
                    Thread.Sleep(500);
            }

            _logger.Info("[PinyinSearch] Catch-up scan complete: {0} items processed", totalProcessed);
            UpdateLastScanId();

            // 清空已处理 ID 跟踪表，让过期条目在下一轮重试。
            // 处理成功的条目 c0 里已有拼音，不会再
            // 命中追赶查询；而那些确实没有拼音的条目
            // 会被无害地再次跳过。
            lock (_processedCatchUpLock)
                _processedCatchUpIds.Clear();

            return totalProcessed;
        }

        private int ProcessCatchUpBatch(int offset)
        {
            using (var conn = _connectionCache.OpenWriteConnection())
            {
                if (conn == null) return 0;

                try
                {
                    var rows = new List<Tuple<long, string>>();
                    var query = FtsIndexWriter.CatchUpQuery() + $" LIMIT {FtsIndexWriter.BatchSize} OFFSET {offset}";
                    using (var stmt = conn.PrepareStatement(query))
                    {
                        while (stmt.MoveNext())
                            rows.Add(Tuple.Create(stmt.Current.GetInt64(0), stmt.Current.GetString(1)));
                    }

                    if (rows.Count == 0) return 0;

                    // 过滤掉已处理的条目
                    lock (_processedCatchUpLock)
                    {
                        rows.RemoveAll(r => _processedCatchUpIds.Contains(r.Item1));
                    }

                    if (rows.Count == 0) return 0;

                    conn.BeginTransaction(TransactionMode.Deferred);
                    int processed = 0;
                    try
                    {
                        foreach (var row in rows)
                        {
                            try
                            {
                                if (IsDisposed) break;

                                long id = row.Item1;
                                string name = row.Item2;

                                // 记入已处理，避免下一轮重复处理
                                var (spaced, connected, bigrams, singleChars, cjkBigrams, initials, initialsBigrams) = TinyPinyinLoader.GeneratePinyin(name);
                                if (string.IsNullOrEmpty(spaced))
                                {
                                    lock (_processedCatchUpLock)
                                        _processedCatchUpIds.Add(id);
                                    continue;
                                }

                                bool alreadyProcessed;
                                lock (_processedCatchUpLock)
                                    alreadyProcessed = _processedCatchUpIds.Contains(id);
                                if (alreadyProcessed)
                                {
                                    _logger.Debug("[PinyinSearch] CatchUp skip already processed: id={0}", id);
                                    continue;
                                }

                                lock (_processedCatchUpLock)
                                    _processedCatchUpIds.Add(id);

                                _writer.ReadExistingColumns(conn, id,
                                    "[PinyinSearch] Catch-up batch read existing columns for id {0}: {1}",
                                    out string origTitle, out string seriesName, out string album);

                                _writer.ExecuteInsert(conn, id, name, spaced, connected, bigrams, singleChars, cjkBigrams, initials, initialsBigrams, origTitle, seriesName, album);
                                processed++;
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn("[PinyinSearch] Catch-up item {0}: {1}", row.Item1, ex.Message);
                            }
                        }

                        conn.CommitTransaction();
                        _logger.Debug("[PinyinSearch] Catch-up batch offset={0}: {1} items", offset, processed);
                    }
                    catch (Exception ex)
                    {
                        conn.RollbackTransaction();
                        _logger.Error("[PinyinSearch] Catch-up batch offset={0} failed, rolled back: {1}", offset, ex.Message);
                    }

                    return processed;
                }
                catch (Exception ex)
                {
                    _logger.Error("[PinyinSearch] Catch-up batch query failed at offset={0}: {1}", offset, ex.Message);
                    return 0;
                }
            }
        }

        // ── 不碰 SQL 的事件处理器 ──
        private void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item != null && !IsDisposed && TinyPinyinLoader.IsCjkItem(e.Item))
            {
                _pendingEventQueue.Enqueue(Tuple.Create(e.Item.InternalId, e.Item.Name));
                EnsureEventTimer();
            }
        }

        private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item != null && !IsDisposed && TinyPinyinLoader.IsCjkItem(e.Item))
            {
                _pendingEventQueue.Enqueue(Tuple.Create(e.Item.InternalId, e.Item.Name));
                EnsureEventTimer();
            }
        }

        private void ProcessQueuedEvents(int maxItems = -1)
        {
            if (IsDisposed) return;

            int limit = maxItems > 0 ? maxItems : EventBatchSize;
            var items = new List<Tuple<long, string>>(limit);
            while (items.Count < limit && _pendingEventQueue.TryDequeue(out var entry))
                items.Add(entry);

            if (items.Count == 0)
            {
                Interlocked.Exchange(ref _eventTimerRunning, 0);
                return;
            }

            using (var connection = _connectionCache.OpenWriteConnection())
            {
                if (connection == null)
                {
                    foreach (var entry in items)
                        _pendingEventQueue.Enqueue(entry);
                    Interlocked.Exchange(ref _eventTimerRunning, 0);
                    return;
                }

                connection.BeginTransaction(TransactionMode.Deferred);
                try
                {
                    foreach (var entry in items)
                    {
                        long id = entry.Item1;
                        string name = entry.Item2;
                        try
                        {
                            if (IsDisposed) break;

                            _writer.ReadExistingColumns(connection, id,
                                "[PinyinSearch] Queue batch read columns for id {0}: {1}",
                                out string origTitle, out string seriesName, out string album);

                            var (spaced, connected, bigrams, singleChars, cjkBigrams, initials, initialsBigrams) = TinyPinyinLoader.GeneratePinyin(name);
                            if (string.IsNullOrEmpty(spaced)) continue;

                            _writer.ExecuteInsert(connection, id, name, spaced, connected, bigrams, singleChars, cjkBigrams, initials, initialsBigrams, origTitle, seriesName, album);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn("[PinyinSearch] Queue batch item {0}: {1}", id, ex.Message);
                        }
                    }
                    connection.CommitTransaction();
                    _logger.Debug("[PinyinSearch] Queue batch: {0} items processed", items.Count);
                }
                catch (Exception ex)
                {
                    connection.RollbackTransaction();
                    _logger.Error("[PinyinSearch] Queue batch transaction failed, rolled back: {0}", ex.Message);
                    foreach (var entry in items)
                        _pendingEventQueue.Enqueue(entry);
                }
            }

            // 先释放重挂守卫；若本批处理期间又有条目进来，
            // 则重新挂上（避免过期守卫把
            // 队列卡死 —— 与 PinyinSortNameService 同一套路）。
            Interlocked.Exchange(ref _eventTimerRunning, 0);
            if (!_pendingEventQueue.IsEmpty)
                EnsureEventTimer();
        }

        private void EnsureEventTimer(int delayMs = EventTimerIntervalMs)
        {
            if (Interlocked.CompareExchange(ref _eventTimerRunning, 1, 0) == 0)
                _eventTimer?.Change(delayMs, Timeout.Infinite);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _logger.Info("[PinyinSearch] Scheduler Dispose called");

            _eventTimer?.Dispose();
            _eventTimer = null;

            _periodicTimer?.Dispose();
            _periodicTimer = null;

            if (_libraryManager != null)
            {
                _libraryManager.ItemAdded -= OnItemAdded;
                _libraryManager.ItemUpdated -= OnItemUpdated;
            }
        }
    }
}
