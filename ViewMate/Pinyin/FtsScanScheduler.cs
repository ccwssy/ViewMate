using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ViewMate.Common;

#nullable enable
namespace ViewMate.Pinyin
{
    /// <summary>
    /// Scan scheduler (phase-2 split of PinyinSearchService): owns the event
    /// queue (OnItemAdded/OnItemUpdated + ProcessQueuedEvents), the dual timers
    /// (event batch timer + 5-minute periodic timer), and the four scan paths —
    /// incremental (ProcessAllPendingBatched), full reindex, catch-up and
    /// periodic. FTS writes are delegated to FtsIndexWriter, pinyin generation to
    /// TinyPinyinLoader; WAL checkpoint timing (TRUNCATE after bulk write,
    /// PASSIVE after periodic scan) is preserved here.
    /// </summary>
    public class FtsScanScheduler : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ConnectionManagerCache _connectionCache;
        private readonly FtsIndexWriter _writer;

        // Thread-safe disposal flag
        private int _disposed;
        private bool IsDisposed => Interlocked.CompareExchange(ref _disposed, 0, 0) == 1;

        // ── Event Queue ──
        private readonly ConcurrentQueue<Tuple<long, string>> _pendingEventQueue = new ConcurrentQueue<Tuple<long, string>>();
        private Timer? _eventTimer;
        private int _eventTimerRunning;
        private const int EventBatchSize = 50;
        private const int EventTimerIntervalMs = 30000;
        private const int PeriodicScanIntervalMs = 300000; // 5 minutes

        // ── Periodic background scan ──
        private Timer? _periodicTimer;

        // ── Batch state ──
        private long _lastScanId;

        public FtsScanScheduler(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _connectionCache = new ConnectionManagerCache(logger, "PinyinSearch");
            _writer = new FtsIndexWriter(logger);

            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemUpdated;

            // Timer starts in one-shot mode; EnsureEventTimer() fires it when queue is non-empty
            _eventTimer = new Timer(_ => ProcessQueuedEvents(), null, Timeout.Infinite, Timeout.Infinite);
        }

        // ── Deferred background scan ──

        public void ProcessAllPendingDeferred()
        {
            // All FTS operations run on a background thread to avoid blocking
            // Plugin.Run() and delaying Emby HTTP server startup.
            // Previously a sync retry loop blocked Plugin.Run() for up to 120s,
            // causing the home page to hang — a recurring bug across 5 releases.
            // Background thread uses Thread.Sleep (not Task.Delay) to avoid
            // deadlock on Emby 4.9.5.0's single-threaded sync context, and
            // checks IsDisposed at every iteration for clean shutdown.
            var scanThread = new Thread(() =>
            {
                _logger.Info("[PinyinSearch] Background thread started, waiting 60s...");
                for (int i = 0; i < 60 && !IsDisposed; i++)
                    Thread.Sleep(1000);

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

                    // Recover WAL space after initial scan: full checkpoint waits for
                    // active readers to drain, then truncates the WAL to prevent bloat.
                    WalCheckpointHelper.TryTruncateCheckpoint(_connectionCache, _logger, "PinyinSearch");

                    // Start periodic timer for catch-up scans after initial scan completes.
                    // This catches items added by library scans that don't fire ItemAdded/Updated events.
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
            // Phase 0: FTS empty → MediaItems full scan
            long ftsTotal = GetFtsTotalCount();
            if (ftsTotal == 0)
            {
                _logger.Info("[PinyinSearch] fts_search9 is empty, scanning MediaItems directly...");
                return ProcessFullReindex();
            }

            // Phase 0.5: Missing MediaItems → full reindex
            long missingCount = GetMissingMediaItemsCount();
            if (missingCount > 0)
            {
                _logger.Info("[PinyinSearch] {0} MediaItems without FTS entry, full reindex needed", missingCount);
                return ProcessFullReindex();
            }

            if (!TryGetPendingCount(out long totalPending) || totalPending == 0)
            {
                _logger.Info("[PinyinSearch] No pending items, checking for catch-up items...");
                // Run catch-up scan only when incremental scan finds 0 items
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

        // ── Periodic background scan callback ──
        // Runs catch-up scan first (catches ALL stale entries regardless of ID),
        // then incremental scan for any new items added since the last run.
        private void ProcessPeriodicScan()
        {
            if (IsDisposed) return;

            try
            {
                int total = 0;

                // Phase 1: catch-up scan — processes all stale entries (any ID)
                if (TryGetCatchUpCount(out long catchUpCount) && catchUpCount > 0)
                {
                    _logger.Info("[PinyinSearch] Periodic catch-up: {0} stale entries", catchUpCount);
                    total += ProcessCatchUpBatched();
                }

                // Phase 2: incremental scan — new items since last scan
                int incremental = ProcessAllPendingBatched();
                total += incremental;

                if (total > 0)
                {
                    _logger.Info("[PinyinSearch] Periodic scan: {0} items processed", total);
                    // Passive checkpoint after write-heavy periodic scan — clears
                    // what it can without blocking concurrent readers.
                    WalCheckpointHelper.TryPassiveCheckpoint(_connectionCache);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[PinyinSearch] Periodic scan failed", ex);
            }
        }

        // ── Empty-FTS fallback ──
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

        // ── Catch-up scan (no id filter) ──
        // Thread-safe tracking of already-processed catch-up items to avoid re-processing
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

            // Clear processed-IDs tracker so stale items get retried next cycle.
            // Items successfully processed will have pinyin in c0 and won't
            // match the catch-up query again; items that legitimately have no
            // pinyin will be re-skipped harmlessly.
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

                    // Filter out already-processed items
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

                                // Track processed to avoid re-processing on next cycle
                                var (spaced, connected, bigrams, singleChars, cjkBigrams) = TinyPinyinLoader.GeneratePinyin(name);
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

                                _writer.ExecuteInsert(conn, id, name, spaced, connected, bigrams, singleChars, cjkBigrams, origTitle, seriesName, album);
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

        // ── Zero-SQL Event Handlers ──
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

                            var (spaced, connected, bigrams, singleChars, cjkBigrams) = TinyPinyinLoader.GeneratePinyin(name);
                            if (string.IsNullOrEmpty(spaced)) continue;

                            _writer.ExecuteInsert(connection, id, name, spaced, connected, bigrams, singleChars, cjkBigrams, origTitle, seriesName, album);
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

            if (!_pendingEventQueue.IsEmpty)
                EnsureEventTimer();
            else
                Interlocked.Exchange(ref _eventTimerRunning, 0);
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
