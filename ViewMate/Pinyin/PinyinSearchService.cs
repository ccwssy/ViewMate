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
        private const int MaxPendingTotal = 5000;

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
        }

        // ── Pending-Item Query Builder ──

        private static string PendingQuery() => $@"
            SELECT c.id, mi.Name
            FROM {FtsTableName}_content c
            JOIN MediaItems mi ON c.id = mi.RowId
            WHERE c.c0 NOT GLOB '*[a-zA-Z]*'
              AND c.c0 GLOB '*[\\u4e00-\\u9fa5]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'";

        private static string PendingCountQuery() => $@"
            SELECT COUNT(*)
            FROM {FtsTableName}_content c
            JOIN MediaItems mi ON c.id = mi.RowId
            WHERE c.c0 NOT GLOB '*[a-zA-Z]*'
              AND c.c0 GLOB '*[\\u4e00-\\u9fa5]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'";

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
            Task.Run(() =>
            {
                try
                {
                    int total = ProcessAllPendingBatched();
                    _logger.Info("[PinyinSearch] Background scan complete: {0} items processed", total);
                }
                catch (Exception ex)
                {
                    _logger.Error("[PinyinSearch] Background scan failed", ex);
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

                using (var stmt = conn.PrepareStatement(PendingCountQuery()))
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

                if (_disposed) break;
            }

            // Single FTS rebuild after all batches
            if (!_disposed)
            {
                try
                {
                    var conn = GetDbConnection();
                    if (conn != null)
                    {
                        conn.Execute($"INSERT INTO {FtsTableName}({FtsTableName}) VALUES('rebuild')");
                        _logger.Info("[PinyinSearch] Final FTS rebuild done");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn("[PinyinSearch] Final FTS rebuild failed: {0}", ex.Message);
                }
            }

            return totalProcessed;
        }

        /// <summary>
        /// Process one batch of <paramref name="limit"/> items starting at
        /// <paramref name="offset"/>.  Opens a fresh connection, holds a
        /// short-lived deferred transaction, then releases — allowing Emby's
        /// HTTP layer to access the database between batches.
        /// </summary>
        private int ProcessBatch(long offset, int limit)
        {
            var conn = GetDbConnection();
            if (conn == null) return 0;

            try
            {
                // Fetch batch rows
                var rows = new List<Tuple<long, string>>();
                var query = PendingQuery() + $" LIMIT {limit} OFFSET {offset}";
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

                            string esc = name.Replace("'", "''");
                            string s = spaced.Replace("'", "''");
                            string c = connected.Replace("'", "''");
                            string b = bigrams.Replace("'", "''");
                            string sc = singleChars.Replace("'", "''");
                            string cb = cjkBigrams.Replace("'", "''");
                            conn.Execute(
                                $"UPDATE {FtsTableName}_content SET c0 = '{esc} {s} {c} {b} {sc} {cb}' WHERE id = {id}");
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
                string esc = name.Replace("'", "''");
                string s = spaced.Replace("'", "''");
                string c = connected.Replace("'", "''");
                string b = bigrams.Replace("'", "''");
                string sc = singleChars.Replace("'", "''");
                string cb = cjkBigrams.Replace("'", "''");
                connection.Execute(
                    $"UPDATE {FtsTableName}_content SET c0 = '{esc} {s} {c} {b} {sc} {cb}' WHERE id = {item.Id}");
                ScheduleRebuild();
                _logger.Info("[PinyinSearch] Injected '{0}'", name);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn("[PinyinSearch] '{0}': {1}", name, ex.Message);
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

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            if (e.Item != null && !_disposed) ProcessItem(e.Item);
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            if (e.Item != null && !_disposed) ProcessItem(e.Item);
        }

        public void Dispose()
        {
            _disposed = true;
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