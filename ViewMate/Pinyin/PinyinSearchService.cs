using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ViewMate.Common;

#nullable enable
namespace ViewMate.Pinyin
{
    public class PinyinSearchService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;

        // Thread-safe disposal flag
        private int _disposed;
        private bool IsDisposed => Interlocked.CompareExchange(ref _disposed, 0, 0) == 1;

        // Static logger for Lazy initializers
        private static ILogger _staticLogger = null!;

        // ── Event Queue ──
        private readonly ConcurrentQueue<Tuple<long, string>> _pendingEventQueue = new ConcurrentQueue<Tuple<long, string>>();
        private Timer? _eventTimer;
        private int _eventTimerRunning;
        private const int EventBatchSize = 50;
        private const int EventTimerIntervalMs = 30000;

        private static readonly Regex ChineseRegex = new Regex(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

        // ── ConnectionManager cache ──
        private readonly ConnectionManagerCache _connectionCache;

        // ── Static caches (Lazy<T>, thread-safe) ──
        private static readonly Lazy<Dictionary<string, string>> _phraseOverridesLazy =
            new Lazy<Dictionary<string, string>>(LoadPhraseOverrides, LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Lazy<Func<char, string>> _getPinyinLazy =
            new Lazy<Func<char, string>>(LoadPinyinFunc, LazyThreadSafetyMode.ExecutionAndPublication);

        private const string FtsTableName = "fts_search9";

        // ── Batch processing constants ──
        private const int BatchSize = 200;
        private const int MaxPendingTotal = 100000;

        // ── Batch state ──
        private long _lastScanId;
        private DateTime _lastOrphanCleanup = DateTime.MinValue;

        public PinyinSearchService(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _staticLogger = logger;
            _connectionCache = new ConnectionManagerCache(logger, "PinyinSearch");

            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemUpdated;

            // Timer starts in one-shot mode; EnsureEventTimer() fires it when queue is non-empty
            _eventTimer = new Timer(_ => ProcessQueuedEvents(), null, Timeout.Infinite, Timeout.Infinite);
        }

        // ── Pending-Item Query Builder ──
        private static string PendingQuery(long lastId) => $@"
            SELECT c.id, mi.Name
            FROM {FtsTableName}_content c
            JOIN MediaItems mi ON c.id = mi.RowId
            WHERE c.c0 NOT GLOB '*[a-zA-Z]*'
              AND c.c0 GLOB '*[一-龥]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'
              AND c.id > {lastId}
            ORDER BY c.id";

        private static string PendingCountQuery(long lastId) => $@"
            SELECT COUNT(*)
            FROM {FtsTableName}_content c
            JOIN MediaItems mi ON c.id = mi.RowId
            WHERE c.c0 NOT GLOB '*[a-zA-Z]*'
              AND c.c0 GLOB '*[一-龥]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'
              AND c.id > {lastId}";

        // ── Catch-up Query (no id filter) ──
        private static string CatchUpQuery() => $@"
            SELECT c.id, mi.Name
            FROM {FtsTableName}_content c
            JOIN MediaItems mi ON c.id = mi.RowId
            WHERE c.c0 NOT GLOB '*[a-zA-Z]*'
              AND c.c0 GLOB '*[一-龥]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'
            ORDER BY c.id";

        private static string CatchUpCountQuery() => $@"
            SELECT COUNT(*)
            FROM {FtsTableName}_content c
            JOIN MediaItems mi ON c.id = mi.RowId
            WHERE c.c0 NOT GLOB '*[a-zA-Z]*'
              AND c.c0 GLOB '*[一-龥]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'";


        // ── Deferred background scan ──

        public void ProcessAllPendingDeferred()
        {
            if (!Plugin.Instance.Configuration.EnablePinyinSearch)
            {
                _logger.Info("[PinyinSearch] Disabled by config");
                return;
            }

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
                }
            });
            scanThread.IsBackground = true;
            scanThread.Start();
        }

        public int ProcessAllPending() => ProcessAllPendingBatched();

        private bool TryGetPendingCount(out long count)
        {
            count = 0;
            try
            {
                using (var conn = _connectionCache.OpenReadConnection())
                {
                    if (conn == null) return false;

                    using (var stmt = conn.PrepareStatement(PendingCountQuery(Volatile.Read(ref _lastScanId))))
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

                    using (var stmt = conn.PrepareStatement(CatchUpCountQuery()))
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

            long actualTotal = Math.Min(totalPending, MaxPendingTotal);
            _logger.Info("[PinyinSearch] {0} pending items, processing in batches of {1}...",
                actualTotal, BatchSize);

            int totalProcessed = 0;
            long offset = 0;

            while (offset < actualTotal && !IsDisposed)
            {
                int batch = ProcessBatch(offset, BatchSize);
                totalProcessed += batch;
                offset += BatchSize;

                if (offset < actualTotal)
                    Thread.Sleep(500);
            }

            if (!IsDisposed)
                _logger.Debug("[PinyinSearch] Batch scan complete, skipping redundant FTS rebuild");

            UpdateLastScanId();
            return totalProcessed;
        }

        // ── Empty-FTS fallback ──
        private long GetFtsTotalCount()
        {
            try
            {
                using (var conn = _connectionCache.OpenReadConnection())
                {
                    if (conn == null) return -1;
                    using (var stmt = conn.PrepareStatement($"SELECT COUNT(*) FROM {FtsTableName}"))
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
                    using (var stmt = conn.PrepareStatement($@"
                        SELECT COUNT(*)
                        FROM MediaItems mi
                        LEFT JOIN {FtsTableName}_content c ON mi.RowId = c.id
                        WHERE c.id IS NULL
                          AND mi.Name GLOB '*[一-龥]*'
                          AND mi.Name NOT GLOB '*Season*'
                          AND mi.Name NOT GLOB '*Episode*'
                          AND mi.Name NOT GLOB '*Media Folder*'"))
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

        private static string MediaItemsCountQuery() => @"
            SELECT COUNT(*)
            FROM MediaItems mi
            WHERE mi.Name GLOB '*[一-龥]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'";

        private static string MediaItemsBatchQuery(long offset, int limit) => $@"
            SELECT mi.RowId, mi.Name
            FROM MediaItems mi
            WHERE mi.Name GLOB '*[一-龥]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'
            ORDER BY mi.RowId
            LIMIT {limit} OFFSET {offset}";

        private int ProcessFullReindex()
        {
            long totalItems = 0;
            try
            {
                using (var conn = _connectionCache.OpenReadConnection())
                {
                    if (conn == null) return 0;
                    using (var stmt = conn.PrepareStatement(MediaItemsCountQuery()))
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
                int batch = ProcessMediaItemsBatch(offset, BatchSize);
                totalProcessed += batch;
                offset += BatchSize;

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
                    using (var stmt = conn.PrepareStatement(MediaItemsBatchQuery(offset, limit)))
                    {
                        while (stmt.MoveNext())
                            rows.Add(Tuple.Create(stmt.Current.GetInt64(0), stmt.Current.GetString(1)));
                    }

                    if (rows.Count == 0) return 0;

                    conn.BeginTransaction(TransactionMode.Deferred);
                    int processed = 0;
                    try
                    {
                        foreach (var row in rows)
                        {
                            if (IsDisposed) break;

                            long id = row.Item1;
                            string name = row.Item2;
                            var (spaced, connected, bigrams, singleChars, cjkBigrams) = GeneratePinyin(name);
                            if (string.IsNullOrEmpty(spaced)) continue;

                            conn.Execute(
                                BuildFtsInsertSql(id, name, spaced, connected, bigrams, singleChars, cjkBigrams));
                            processed++;
                        }

                        conn.CommitTransaction();
                        _logger.Debug("[PinyinSearch] MediaItems batch offset={0}: {1} items", offset, processed);
                    }
                    catch (Exception ex)
                    {
                        conn.RollbackTransaction();
                        _logger.Error("[PinyinSearch] MediaItems batch offset={0} failed: {1}", offset, ex.Message);
                    }

                    return processed;
                }
                catch (Exception ex)
                {
                    _logger.Error("[PinyinSearch] MediaItems batch query failed at offset={0}: {1}", offset, ex.Message);
                    return 0;
                }
            }
        }

        // ── SQL helpers ──
        private static string Escape(string s) => s?.Replace("'", "''") ?? "";

        private string BuildFtsInsertSql(long id, string name, string spaced, string connected, string bigrams, string singleChars, string cjkBigrams, string origTitle = "", string seriesName = "", string album = "")
        {
            string esc = Escape(name);
            string s = Escape(spaced);
            string c = Escape(connected);
            string b = Escape(bigrams);
            string sc = Escape(singleChars);
            string cb = Escape(cjkBigrams);
            string ot = Escape(origTitle);
            string sn = Escape(seriesName);
            string al = Escape(album);
            return $"INSERT OR REPLACE INTO {FtsTableName}(rowid,Name,OriginalTitle,SeriesName,Album) VALUES({id},'{esc} {s} {c} {b} {sc} {cb}','{ot}','{sn}','{al}')";
        }

        private void UpdateLastScanId()
        {
            try
            {
                using (var conn = _connectionCache.OpenReadConnection())
                {
                    if (conn == null) return;
                    using (var stmt = conn.PrepareStatement($"SELECT MAX(id) FROM {FtsTableName}_content"))
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

        private void CleanOrphanedFtsEntries()
        {
            try
            {
                using (var conn = _connectionCache.OpenWriteConnection())
                {
                    if (conn == null) return;
                    conn.Execute(
                        $"DELETE FROM {FtsTableName} WHERE rowid NOT IN (SELECT RowId FROM MediaItems)");
                    _logger.Debug("[PinyinSearch] Orphan cleanup done");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("[PinyinSearch] Orphan cleanup failed: {0}", ex.Message);
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
                    var query = PendingQuery(Volatile.Read(ref _lastScanId)) + $" LIMIT {limit} OFFSET {offset}";
                    using (var stmt = conn.PrepareStatement(query))
                    {
                        while (stmt.MoveNext())
                            rows.Add(Tuple.Create(stmt.Current.GetInt64(0), stmt.Current.GetString(1)));
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
                                var (spaced, connected, bigrams, singleChars, cjkBigrams) = GeneratePinyin(name);
                                if (string.IsNullOrEmpty(spaced)) continue;

                                string origTitle = "", seriesName = "", album = "";
                                try
                                {
                                    using (var r = conn.PrepareStatement(
                                        $"SELECT c1, c2, c3 FROM {FtsTableName}_content WHERE id = {id}"))
                                    {
                                        if (r.MoveNext())
                                        {
                                            origTitle = r.Current.GetString(0) ?? "";
                                            seriesName = r.Current.GetString(1) ?? "";
                                            album = r.Current.GetString(2) ?? "";
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.Warn("[PinyinSearch] Batch read existing columns for id {0}: {1}", id, ex.Message);
                                }

                                conn.Execute(
                                    BuildFtsInsertSql(id, name, spaced, connected, bigrams, singleChars, cjkBigrams, origTitle, seriesName, album));
                                processed++;
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn("[PinyinSearch] Item {0}: {1}", row.Item1, ex.Message);
                            }
                        }

                        conn.CommitTransaction();
                        _logger.Debug("[PinyinSearch] Batch offset={0}: {1} items", offset, processed);
                    }
                    catch (Exception ex)
                    {
                        conn.RollbackTransaction();
                        _logger.Error("[PinyinSearch] Batch offset={0} failed, rolled back: {1}", offset, ex.Message);
                    }

                    return processed;
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
        private readonly HashSet<int> _processedCatchUpIds = new HashSet<int>();
        private readonly object _processedCatchUpLock = new object();

        private int ProcessCatchUpBatched()
        {
            long offset = 0;
            int totalProcessed = 0;
            _logger.Info("[PinyinSearch] Starting catch-up scan in batches of {0}...", BatchSize);

            while (!IsDisposed)
            {
                int batch = ProcessCatchUpBatch((int)offset);
                if (batch == 0) break;
                totalProcessed += batch;
                offset += BatchSize;

                if (!IsDisposed)
                    Thread.Sleep(500);
            }

            _logger.Info("[PinyinSearch] Catch-up scan complete: {0} items processed", totalProcessed);
            UpdateLastScanId();
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
                    var query = CatchUpQuery() + $" LIMIT {BatchSize} OFFSET {offset}";
                    using (var stmt = conn.PrepareStatement(query))
                    {
                        while (stmt.MoveNext())
                            rows.Add(Tuple.Create(stmt.Current.GetInt64(0), stmt.Current.GetString(1)));
                    }

                    if (rows.Count == 0) return 0;

                    // Filter out already-processed items
                    lock (_processedCatchUpLock)
                    {
                        rows.RemoveAll(r => _processedCatchUpIds.Contains((int)r.Item1));
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
                                bool alreadyProcessed;
                                lock (_processedCatchUpLock)
                                {
                                    alreadyProcessed = _processedCatchUpIds.Contains((int)id);
                                    if (!alreadyProcessed)
                                        _processedCatchUpIds.Add((int)id);
                                }

                                if (alreadyProcessed) continue;

                                var (spaced, connected, bigrams, singleChars, cjkBigrams) = GeneratePinyin(name);
                                if (string.IsNullOrEmpty(spaced)) continue;

                                string origTitle = "", seriesName = "", album = "";
                                try
                                {
                                    using (var r = conn.PrepareStatement(
                                        $@"SELECT c1, c2, c3 FROM {FtsTableName}_content WHERE id = {id}"))
                                    {
                                        if (r.MoveNext())
                                        {
                                            origTitle = r.Current.GetString(0) ?? "";
                                            seriesName = r.Current.GetString(1) ?? "";
                                            album = r.Current.GetString(2) ?? "";
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.Warn("[PinyinSearch] Catch-up batch read existing columns for id {0}: {1}", id, ex.Message);
                                }

                                conn.Execute(
                                    BuildFtsInsertSql(id, name, spaced, connected, bigrams, singleChars, cjkBigrams, origTitle, seriesName, album));
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

        public bool ProcessItem(BaseItem item)
        {
            if (!Plugin.Instance.Configuration.EnablePinyinSearch) return false;
            if (!IsCjkItem(item)) return false;

            var name = item.Name;
            if (string.IsNullOrEmpty(name)) return false;

            var (spaced, connected, bigrams, singleChars, cjkBigrams) = GeneratePinyin(name);
            if (string.IsNullOrEmpty(spaced)) return false;

            using (var connection = _connectionCache.OpenWriteConnection())
            {
                if (connection == null) return false;

                try
                {
                    string origTitle = "", seriesName = "", album = "";
                    try
                    {
                        using (var stmt = connection.PrepareStatement(
                            $"SELECT c1, c2, c3 FROM {FtsTableName}_content WHERE id = {item.InternalId}"))
                        {
                            if (stmt.MoveNext())
                            {
                                origTitle = (stmt.Current.GetString(0) ?? "");
                                seriesName = (stmt.Current.GetString(1) ?? "");
                                album = (stmt.Current.GetString(2) ?? "");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("[PinyinSearch] Read existing columns for item {0}: {1}", item.InternalId, ex.Message);
                    }

                    connection.BeginTransaction(TransactionMode.Deferred);
                    try
                    {
                        var id = item.InternalId;
                        var sql = BuildFtsInsertSql(id, name, spaced, connected, bigrams, singleChars, cjkBigrams, origTitle, seriesName, album);
                        _logger.Debug("[PinyinSearch] DEBUG SQL for {0} (len={1}): {2}", id, sql.Length, sql.Substring(0, Math.Min(sql.Length, 200)));
                        connection.Execute(sql);
                        connection.CommitTransaction();
                    }
                    catch
                    {
                        connection.RollbackTransaction();
                        throw;
                    }

                    _logger.Info("[PinyinSearch] Injected '{0}'", name);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Warn("[PinyinSearch] '{0}': {1} [Type={2} HResult={3}]",
                        name, ex.Message, ex.GetType().FullName, ex.HResult);
                    if (ex.InnerException != null)
                        _logger.Warn("[PinyinSearch] Inner: {0} [Type={1}]",
                            ex.InnerException.Message, ex.InnerException.GetType().FullName);
                    var st = ex.StackTrace;
                    if (st != null)
                    {
                        var lines = st.Split('\n');
                        var top = lines.Length > 4
                            ? string.Join(" | ", lines[0], lines[1], lines[2], lines[3])
                            : string.Join(" | ", lines);
                        _logger.Warn("[PinyinSearch] Stack: {0}", top.Trim());
                    }
                    return false;
                }
            }
        }

        public static (string spaced, string connected, string bigrams, string singleChars, string cjkBigrams) GeneratePinyin(string text)
        {
            if (string.IsNullOrEmpty(text)) return (null!, null!, null!, null!, null!);

            var sbSpaced = new StringBuilder();
            var sbConnected = new StringBuilder();
            var syllables = new List<string>();
            var cjkChars = new List<char>();
            bool hasChinese = false;

            for (int i = 0; i < text.Length; )
            {
                char ch = text[i];
                if (ch >= 0x4e00 && ch <= 0x9fff)
                {
                    string? matchedPhrase = null;
                    int matchLen = 0;
                    var overrides = _phraseOverridesLazy.Value;
                    foreach (var kvp in overrides)
                    {
                        if (i + kvp.Key.Length <= text.Length &&
                            text.Substring(i, kvp.Key.Length) == kvp.Key &&
                            kvp.Key.Length > matchLen)
                        {
                            matchedPhrase = kvp.Key;
                            matchLen = kvp.Key.Length;
                        }
                    }

                    if (matchedPhrase != null)
                    {
                        var overrideSegs = overrides[matchedPhrase].Split(' ');
                        foreach (var seg in overrideSegs)
                        {
                            sbSpaced.Append(seg);
                            sbSpaced.Append(' ');
                            sbConnected.Append(seg);
                            syllables.Add(seg);
                        }
                        foreach (char pc in matchedPhrase)
                            cjkChars.Add(pc);
                        i += matchLen;
                        hasChinese = true;
                        continue;
                    }

                    try
                    {
                        var p = _getPinyinLazy.Value(ch);
                        if (!string.IsNullOrEmpty(p))
                        {
                            sbSpaced.Append(p);
                            sbSpaced.Append(' ');
                            sbConnected.Append(p);
                            syllables.Add(p);
                            cjkChars.Add(ch);
                            hasChinese = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _staticLogger?.Warn("[PinyinSearch] TinyPinyin failed for char '{0}': {1}", ch, ex.Message);
                    }
                }
                i++;
            }

            if (!hasChinese) return (null!, null!, null!, null!, null!);

            var sbBigram = new StringBuilder();
            for (int i = 0; i + 1 < syllables.Count; i++)
            {
                sbBigram.Append(syllables[i]);
                sbBigram.Append(syllables[i + 1]);
                sbBigram.Append(' ');
            }

            var sbSingle = new StringBuilder();
            foreach (var ch in cjkChars)
                sbSingle.Append(ch).Append(' ');

            var sbCjkBigram = new StringBuilder();
            for (int i = 0; i + 1 < cjkChars.Count; i++)
                sbCjkBigram.Append(cjkChars[i]).Append(cjkChars[i + 1]).Append(' ');

            return (sbSpaced.ToString().TrimEnd(), sbConnected.ToString(),
                    sbBigram.ToString().TrimEnd(),
                    sbSingle.ToString().TrimEnd(), sbCjkBigram.ToString().TrimEnd());
        }

        public static bool IsCjkItem(BaseItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Name)) return false;
            if (item.IsDisplayedAsFolder && !(item is Series)) return false;
            return ChineseRegex.IsMatch(item.Name);
        }

        // ── Zero-SQL Event Handlers ──
        private void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item != null && !IsDisposed && IsCjkItem(e.Item))
            {
                _pendingEventQueue.Enqueue(Tuple.Create(e.Item.InternalId, e.Item.Name));
                EnsureEventTimer();
            }
        }

        private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item != null && !IsDisposed && IsCjkItem(e.Item))
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

                            string origTitle = "", seriesName = "", album = "";
                            try
                            {
                                using (var stmt = connection.PrepareStatement(
                                    $"SELECT c1, c2, c3 FROM {FtsTableName}_content WHERE id = {id}"))
                                {
                                    if (stmt.MoveNext())
                                    {
                                        origTitle = stmt.Current.GetString(0) ?? "";
                                        seriesName = stmt.Current.GetString(1) ?? "";
                                        album = stmt.Current.GetString(2) ?? "";
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn("[PinyinSearch] Queue batch read columns for id {0}: {1}", id, ex.Message);
                            }

                            var (spaced, connected, bigrams, singleChars, cjkBigrams) = GeneratePinyin(name);
                            if (string.IsNullOrEmpty(spaced)) continue;

                            connection.Execute(
                                BuildFtsInsertSql(id, name, spaced, connected, bigrams, singleChars, cjkBigrams, origTitle, seriesName, album));
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

            if (_libraryManager != null)
            {
                _libraryManager.ItemAdded -= OnItemAdded;
                _libraryManager.ItemUpdated -= OnItemUpdated;
            }
        }

        // ── Static Lazy initializers ──
        private static Dictionary<string, string> LoadPhraseOverrides()
        {
            var dict = new Dictionary<string, string>();
            string[] probePaths =
            {
                "/config/plugins/pinyin-overrides.json",
                "plugins/pinyin-overrides.json",
                "../plugins/pinyin-overrides.json",
            };

            foreach (var path in probePaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        var text = File.ReadAllText(path);
                        var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(text);
                        if (parsed != null)
                        {
                            dict = parsed;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _staticLogger?.Warn("[PinyinSearch] Failed to load pinyin overrides from {0}: {1}", path, ex.Message);
                    }
                }
            }
            return dict;
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

            Assembly? asm = null;
            foreach (var path in probePaths)
            {
                if (File.Exists(path))
                {
                    asm = Assembly.Load(File.ReadAllBytes(path));
                    break;
                }
            }

            if (asm == null)
                throw new FileNotFoundException("TinyPinyin.dll not found in any probe path");

            var helperType = asm.GetType("TinyPinyin.PinyinHelper");
            if (helperType == null)
                throw new FileNotFoundException("TinyPinyin.PinyinHelper type not found");

            var method = helperType.GetMethod("GetPinyin", new[] { typeof(char) });
            if (method == null)
                throw new MissingMethodException("GetPinyin method not found");

            return (Func<char, string>)Delegate.CreateDelegate(
                typeof(Func<char, string>), null, method);
        }
    }
}
