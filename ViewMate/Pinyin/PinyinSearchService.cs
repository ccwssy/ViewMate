using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Collections.Concurrent;
using System.Threading;
using System.IO;
using System.Threading.Tasks;
using ViewMate.Common;

#nullable disable
namespace ViewMate.Pinyin
{
    public class PinyinSearchService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private bool _disposed = false;

        // ── Event Queue (zero-SQL event handlers → timer-based batch processing) ──
        private readonly ConcurrentQueue<Tuple<long, string>> _pendingEventQueue = new ConcurrentQueue<Tuple<long, string>>();
        private Timer _eventTimer;
        private const int EventBatchSize = 50;
        private const int EventTimerIntervalMs = 30000; // 30s

        private static readonly Regex ChineseRegex = new Regex(@"[\\u4e00-\\u9fff]", RegexOptions.Compiled);

        // ── 词组级多音字校正表（外部 JSON，DLL 外维护）──
        // 路径：/config/plugins/pinyin-overrides.json
        // 修改此文件后重启 Emby 生效，无需重新编译 DLL
        private static Dictionary<string, string> _phraseOverrides;
        private static readonly object _phraseLock = new object();

        private static Dictionary<string, string> GetPhraseOverrides()
        {
            if (_phraseOverrides != null) return _phraseOverrides;
            lock (_phraseLock)
            {
                if (_phraseOverrides != null) return _phraseOverrides;

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
                        catch { }
                    }
                }

                _phraseOverrides = dict;
                return _phraseOverrides;
            }
        }

        // ── TinyPinyin reflection cache ──
        private static Func<char, string> _getPinyin;
        private static readonly object _pinyinLock = new object();

        private static Func<char, string> GetPinyinFunc()
        {
            if (_getPinyin != null) return _getPinyin;
            lock (_pinyinLock)
            {
                if (_getPinyin != null) return _getPinyin;

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
                    if (File.Exists(path))
                    {
                        asm = Assembly.Load(File.ReadAllBytes(path));
                        break;
                    }
                }

                if (asm == null)
                    throw new FileNotFoundException("TinyPinyin.dll not found in any probe path");

                var helperType = asm.GetType("TinyPinyin.PinyinHelper");
                var method = helperType.GetMethod("GetPinyin", new[] { typeof(char) });
                _getPinyin = (Func<char, string>)Delegate.CreateDelegate(
                    typeof(Func<char, string>), null, method);
                return _getPinyin;
            }
        }
        private const string FtsTableName = "fts_search9";
        private const int RebuildDebounceMs = 10000;

        // ── Batch processing constants ──
        private const int BatchSize = 200;
        private const int MaxPendingTotal = 100000;

        // ── debounced rebuild ──
        private Timer _rebuildTimer;
        private readonly object _rebuildLock = new object();

        private void ScheduleRebuild()
        {
            lock (_rebuildLock)
            {
                if (_rebuildTimer == null)
                {
                    _rebuildTimer = new Timer(_ =>
                    {
                        DoRebuild();
                    }, null, RebuildDebounceMs, Timeout.Infinite);
                }
                else
                {
                    _rebuildTimer.Change(RebuildDebounceMs, Timeout.Infinite);
                }
            }
        }

        private void DoRebuild()
        {
            lock (_rebuildLock)
            {
                try
                {
                    var connection = GetDbConnection();
                    if (connection != null)
                    {
                        connection.Execute($"INSERT INTO {FtsTableName}({FtsTableName}) VALUES('rebuild')");
                        _logger.Debug("[PinyinSearch] Deferred FTS rebuild done");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn("[PinyinSearch] Deferred FTS rebuild failed", ex);
                }
                finally
                {
                    _rebuildTimer?.Dispose();
                    _rebuildTimer = null;
                }
            }
        }

        public PinyinSearchService(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;

            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemUpdated;

            // Timer-based batch processor: fires every 30s when queue is non-empty
            _eventTimer = new Timer(_ => ProcessQueuedEvents(), null,
                EventTimerIntervalMs, EventTimerIntervalMs);
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

        private long _lastScanId = 0;

        // ── Deferred background scan (non-blocking for plugin startup) ──

        /// <summary>
        /// Fire-and-forget background scan.  Emby's HTTP server starts immediately
        /// while we process items in small batches, releasing the SQLite lock between
        /// each batch.  Prevents "卡死" on slow ARM hardware.
        /// </summary>
        public void ProcessAllPendingDeferred()
        {
            if (!Plugin.Instance.Configuration.EnablePinyinSearch)
            {
                _logger.Info("[PinyinSearch] Disabled by config");
                return;
            }

            _logger.Info("[PinyinSearch] Deferring initial scan to background thread...");
            Task.Run(async () =>
            {
                // Wait 60s before first scan — Emby's startup library scan needs
                // uncontended SQLite access.  Immediate scan on startup competes
                // for write locks and freezes homepage / search.
                for (int i = 0; i < 60 && !_disposed; i++)
                    await Task.Delay(1000);

                while (!_disposed)
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

                    // 5-minute interval between scans — frequent enough for
                    // missed items, sparse enough to avoid SQLite contention
                    // with Emby's HTTP read queries / library scans.
                    for (int i = 0; i < 300 && !_disposed; i++)
                        await Task.Delay(1000);
                }
            });
        }

        // ── Batched processing (kept public for backward compat / manual triggers) ──

        public int ProcessAllPending()
        {
            // Calling this synchronously is still discouraged on slow hardware.
            // Delegate to the batched implementation.
            return ProcessAllPendingBatched();
        }

        private bool TryGetPendingCount(out long count)
        {
            count = 0;
            try
            {
                var conn = GetDbConnection();
                if (conn == null) return false;

                using (var stmt = conn.PrepareStatement(PendingCountQuery(_lastScanId)))
                {
                    stmt.MoveNext();
                    count = stmt.Current.GetInt64(0);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private int ProcessAllPendingBatched()
        {
            // Phase 0: If FTS table is completely empty, fall back to MediaItems
            // direct scan (e.g. after DELETE FROM fts_search9 for testing or
            // plugin first-install). PendingQuery() on an empty FTS returns 0 rows
            // and silently does nothing — without this fallback the user would see
            // "No pending items" forever and no pinyin would ever be generated.
            // v1.2.14.2
            long ftsTotal = GetFtsTotalCount();
            if (ftsTotal == 0)
            {
                _logger.Info("[PinyinSearch] fts_search9 is empty, scanning MediaItems directly...");
                return ProcessFullReindex();
            }

            // Phase 0.5: If FTS has entries but MediaItems contain items with
            // Chinese names that don't have any FTS entry yet, do a full reindex.
            // This handles the case where MaxPendingTotal capped the initial
            // reindex and some items were skipped (> 5000 but < 100000).
            // v1.2.14.3
            long missingCount = GetMissingMediaItemsCount();
            if (missingCount > 0)
            {
                _logger.Info("[PinyinSearch] {0} MediaItems without FTS entry, full reindex needed", missingCount);
                return ProcessFullReindex();
            }

            if (!TryGetPendingCount(out long totalPending) || totalPending == 0)
            {
                _logger.Info("[PinyinSearch] No pending items");
                return 0;
            }

            long actualTotal = Math.Min(totalPending, MaxPendingTotal);
            _logger.Info("[PinyinSearch] {0} pending items, processing in batches of {1}...",
                actualTotal, BatchSize);

            int totalProcessed = 0;
            long offset = 0;

            while (offset < actualTotal)
            {
                int batch = ProcessBatch(offset, BatchSize);
                totalProcessed += batch;
                offset += BatchSize;

                // Yield between batches so Emby's HTTP readers can acquire
                // the SQLite read lock — prevents homepage freeze during
                // library scan / startup background processing.
                if (offset < actualTotal)
                    Thread.Sleep(500);

                if (_disposed) break;
            }

            // Single FTS rebuild after all batches — removed because INSERT OR REPLACE
            // already auto-updates the FTS index incrementally. The explicit rebuild
            // + WAL checkpoint held a write lock that blocked Emby's homepage search
            // queries, causing 卡死. (v1.2.13.3)
            if (!_disposed)
            {
                _logger.Debug("[PinyinSearch] Batch scan complete, skipping redundant FTS rebuild");
            }

            UpdateLastScanId();

            return totalProcessed;
        }

        // ── Empty-FTS fallback: full reindex from MediaItems ──
        // See Phase 0 in ProcessAllPendingBatched() above. v1.2.14.2

        private long GetFtsTotalCount()
        {
            try
            {
                using (var conn = GetDbConnection())
                using (var stmt = conn.PrepareStatement($"SELECT COUNT(*) FROM {FtsTableName}"))
                {
                    if (stmt.MoveNext())
                        return stmt.Current.GetInt64(0);
                }
            }
            catch { }
            return -1;
        }

        private long GetMissingMediaItemsCount()
        {
            try
            {
                using (var conn = GetDbConnection())
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
            catch { }
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
            // Count total Chinese-named items
            long totalItems = 0;
            try
            {
                using (var conn = GetDbConnection())
                using (var stmt = conn.PrepareStatement(MediaItemsCountQuery()))
                {
                    if (stmt.MoveNext()) totalItems = stmt.Current.GetInt64(0);
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

            long limit = totalItems;  // no cap — process ALL items
            _logger.Info("[PinyinSearch] Full reindex: {0} items from MediaItems", limit);

            int totalProcessed = 0;
            long offset = 0;

            while (offset < limit)
            {
                int batch = ProcessMediaItemsBatch(offset, BatchSize);
                totalProcessed += batch;
                offset += BatchSize;

                if (offset < limit)
                    Thread.Sleep(500);

                if (_disposed) break;
            }

            // Update _lastScanId so subsequent 5min scans are incremental
            UpdateLastScanId();

            return totalProcessed;
        }

        private int ProcessMediaItemsBatch(long offset, int limit)
        {
            using (var conn = GetDbConnection())
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
                            if (_disposed) break;

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
                using (var conn = GetDbConnection())
                using (var stmt = conn.PrepareStatement($"SELECT MAX(id) FROM {FtsTableName}_content"))
                {
                    if (stmt.MoveNext() && !stmt.Current.IsDBNull(0))
                        _lastScanId = stmt.Current.GetInt64(0);
                }
            }
            catch { }
        }

        /// <summary>
        /// Process one batch of <paramref name="limit"/> items starting at
        /// <paramref name="offset"/>.  Opens a fresh connection, holds a
        /// short-lived deferred transaction, then releases — allowing Emby's
        /// HTTP layer to access the database between batches.
        /// </summary>
        private int ProcessBatch(long offset, int limit)
        {
            using (var conn = GetDbConnection())
            {
                if (conn == null) return 0;

                try
                {
                    // Fetch batch rows
                    var rows = new List<Tuple<long, string>>();
                    var query = PendingQuery(_lastScanId) + $" LIMIT {limit} OFFSET {offset}";
                    using (var stmt = conn.PrepareStatement(query))
                    {
                        while (stmt.MoveNext())
                            rows.Add(Tuple.Create(stmt.Current.GetInt64(0), stmt.Current.GetString(1)));
                    }

                    if (rows.Count == 0) return 0;

                    // Process in a short transaction
                    conn.BeginTransaction(TransactionMode.Deferred);
                    int processed = 0;
                    try
                    {
                        foreach (var row in rows)
                        {
                            try
                            {
                                if (_disposed) break;

                                long id = row.Item1;
                                string name = row.Item2;
                                var (spaced, connected, bigrams, singleChars, cjkBigrams) = GeneratePinyin(name);
                                if (string.IsNullOrEmpty(spaced)) continue;

                                // Read existing OriginalTitle/SeriesName/Album to preserve them
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
                                catch { }

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

        public bool ProcessItem(BaseItem item)
        {
            if (!Plugin.Instance.Configuration.EnablePinyinSearch) return false;
            if (!IsCjkItem(item)) return false;

            var name = item.Name;
            if (string.IsNullOrEmpty(name)) return false;

            var (spaced, connected, bigrams, singleChars, cjkBigrams) = GeneratePinyin(name);
            if (string.IsNullOrEmpty(spaced)) return false;

            var connection = GetDbConnection();
            if (connection == null) return false;

            try
            {
                // Read existing columns via simple SELECT (no subqueries in VALUES)
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
                catch { }

                connection.BeginTransaction(TransactionMode.Deferred);
                try
                {
                    var id = item.InternalId;
                    var sql = BuildFtsInsertSql(id, name, spaced, connected, bigrams, singleChars, cjkBigrams, origTitle, seriesName, album);
                    _logger.Info("[PinyinSearch] DEBUG SQL for {0} (len={1}): {2}", id, sql.Length, sql.Substring(0, Math.Min(sql.Length, 200)));
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

        public static (string spaced, string connected, string bigrams, string singleChars, string cjkBigrams) GeneratePinyin(string text)
        {
            if (string.IsNullOrEmpty(text)) return (null, null, null, null, null);

            var sbSpaced = new StringBuilder();
            var sbConnected = new StringBuilder();
            var syllables = new List<string>();
            var cjkChars = new List<char>();
            bool hasChinese = false;

            // Index-based iteration to support phrase skipping
            for (int i = 0; i < text.Length; )
            {
                char ch = text[i];
                if (ch >= 0x4e00 && ch <= 0x9fff)
                {
                    // Check if a known phrase starts at this position (longest match wins)
                    string matchedPhrase = null;
                    int matchLen = 0;
                    var overrides = GetPhraseOverrides();
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
                        // Use override pinyin for the entire phrase
                        var overrideSegs = overrides[matchedPhrase].Split(' ');
                        foreach (var seg in overrideSegs)
                        {
                            sbSpaced.Append(seg);
                            sbSpaced.Append(' ');
                            sbConnected.Append(seg);
                            syllables.Add(seg);
                        }
                        foreach (char pc in matchedPhrase)
                        {
                            cjkChars.Add(pc);
                        }
                        i += matchLen;
                        hasChinese = true;
                        continue;
                    }

                    // Fall back to per-character TinyPinyin
                    try
                    {
                        var p = GetPinyinFunc()(ch);
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
                    catch { }
                }
                i++;
            }

            if (!hasChinese) return (null, null, null, null, null);

            // Pinyin syllable bigrams (adjacent pairs)
            var sbBigram = new StringBuilder();
            for (int i = 0; i + 1 < syllables.Count; i++)
            {
                sbBigram.Append(syllables[i]);
                sbBigram.Append(syllables[i + 1]);
                sbBigram.Append(' ');
            }

            // Single CJK characters
            var sbSingle = new StringBuilder();
            foreach (var ch in cjkChars)
            {
                sbSingle.Append(ch);
                sbSingle.Append(' ');
            }

            // CJK 2-char bigrams (sliding window)
            var sbCjkBigram = new StringBuilder();
            for (int i = 0; i + 1 < cjkChars.Count; i++)
            {
                sbCjkBigram.Append(cjkChars[i]);
                sbCjkBigram.Append(cjkChars[i + 1]);
                sbCjkBigram.Append(' ');
            }

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

        private IDatabaseConnection GetDbConnection()
        {
            var itemRepo = Plugin.Instance.ApplicationHost.Resolve<IItemRepository>();
            return DbConnectionHelper.GetConnection(itemRepo, _logger, "PinyinSearch");
        }

        // ── Zero-SQL Event Handlers (queue only, no SQLite access) ──
        // Items are batched and processed by the timer, preventing threadpool
        // starvation and SQLite write-lock contention during library scans.

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            if (e.Item != null && !_disposed && IsCjkItem(e.Item))
                _pendingEventQueue.Enqueue(Tuple.Create(e.Item.InternalId, e.Item.Name));
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            if (e.Item != null && !_disposed && IsCjkItem(e.Item))
                _pendingEventQueue.Enqueue(Tuple.Create(e.Item.InternalId, e.Item.Name));
        }

        /// <summary>
        /// Dequeue up to EventBatchSize items and process them in batch.
        /// Called by the timer; also safe to call manually after a scan.
        /// </summary>
        private void ProcessQueuedEvents(int maxItems = -1)
        {
            if (_disposed) return;

            int limit = maxItems > 0 ? maxItems : EventBatchSize;
            var items = new List<Tuple<long, string>>(limit);
            while (items.Count < limit && _pendingEventQueue.TryDequeue(out var entry))
                items.Add(entry);

            if (items.Count == 0) return;

            using (var connection = GetDbConnection())
            {
                connection.BeginTransaction(TransactionMode.Deferred);
                try
                {
                    foreach (var entry in items)
                    {
                        long id = entry.Item1;
                        string name = entry.Item2;
                        try
                        {
                            if (_disposed) break;

                            // Read existing columns to preserve them
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
                            catch { }

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
                catch
                {
                    connection.RollbackTransaction();
                    throw;
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _eventTimer?.Dispose();
            lock (_rebuildLock)
            {
                _rebuildTimer?.Dispose();
                _rebuildTimer = null;
            }
            if (_libraryManager != null)
            {
                _libraryManager.ItemAdded -= OnItemAdded;
                _libraryManager.ItemUpdated -= OnItemUpdated;
            }
        }
    }
}