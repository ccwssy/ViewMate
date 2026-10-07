using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;
using System.Collections.Generic;
using ViewMate.Common;

namespace ViewMate.Pinyin
{
    /// <summary>
    /// FTS5 index writer (phase-2 split of PinyinSearchService): builds all FTS
    /// SQL (pending/catch-up/media-items queries + INSERT OR REPLACE), preserves
    /// existing c1..c3 columns on re-insert, and runs batched writes inside a
    /// single deferred transaction (commit on success, rollback on failure).
    /// Text escaping lives in Common/TextUtil.
    /// </summary>
    public class FtsIndexWriter
    {
        private readonly ILogger _logger;

        public const string FtsTableName = "fts_search9";
        public const int BatchSize = 200;
        public const int MaxPendingTotal = 100000;

        public FtsIndexWriter(ILogger logger)
        {
            _logger = logger;
        }

        // ── Pending-Item Query Builder ──
        public static string PendingQuery(long lastId) => $@"
            SELECT c.id, mi.Name
            FROM {FtsTableName}_content c
            JOIN MediaItems mi ON c.id = mi.RowId
            WHERE c.c0 = mi.Name
              AND mi.Name GLOB '*[一-龥]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'
              AND c.id > {lastId}
            ORDER BY c.id";

        public static string PendingCountQuery(long lastId) => $@"
            SELECT COUNT(*)
            FROM {FtsTableName}_content c
            JOIN MediaItems mi ON c.id = mi.RowId
            WHERE c.c0 = mi.Name
              AND mi.Name GLOB '*[一-龥]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'
              AND c.id > {lastId}";

        // ── Catch-up Query (no id filter) ──
        public static string CatchUpQuery() => $@"
            SELECT c.id, mi.Name
            FROM {FtsTableName}_content c
            JOIN MediaItems mi ON c.id = mi.RowId
            WHERE c.c0 = mi.Name
              AND mi.Name GLOB '*[一-龥]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'
            ORDER BY c.id";

        public static string CatchUpCountQuery() => $@"
            SELECT COUNT(*)
            FROM {FtsTableName}_content c
            JOIN MediaItems mi ON c.id = mi.RowId
            WHERE c.c0 = mi.Name
              AND mi.Name GLOB '*[一-龥]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'";

        // ── Backfill query (cursor-paged, newest-pinyin rows get the initials token) ──
        public static string BackfillQuery(long cursor, int limit) => $@"
            SELECT c.id, mi.Name, c.c0
            FROM {FtsTableName}_content c
            JOIN MediaItems mi ON c.id = mi.RowId
            WHERE c.id > {cursor}
              AND mi.Name GLOB '*[一-龥]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'
            ORDER BY c.id
            LIMIT {limit}";

        // ── Full reindex queries ──
        public static string FtsTotalCountQuery() => $"SELECT COUNT(*) FROM {FtsTableName}";

        public static string MissingMediaItemsCountQuery() => $@"
            SELECT COUNT(*)
            FROM MediaItems mi
            LEFT JOIN {FtsTableName}_content c ON mi.RowId = c.id
            WHERE c.id IS NULL
              AND mi.Name GLOB '*[一-龥]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'";

        public static string MediaItemsCountQuery() => @"
            SELECT COUNT(*)
            FROM MediaItems mi
            WHERE mi.Name GLOB '*[一-龥]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'";

        public static string MediaItemsBatchQuery(long offset, int limit) => $@"
            SELECT mi.RowId, mi.Name
            FROM MediaItems mi
            WHERE mi.Name GLOB '*[一-龥]*'
              AND mi.Name NOT GLOB '*Season*'
              AND mi.Name NOT GLOB '*Episode*'
              AND mi.Name NOT GLOB '*Media Folder*'
            ORDER BY mi.RowId
            LIMIT {limit} OFFSET {offset}";

        // ── Row-level SQL ──
        public static string ExistingColumnsQuery(long id) =>
            $"SELECT c1, c2, c3 FROM {FtsTableName}_content WHERE id = {id}";

        /// <summary>
        /// The `Name` column value (c0) for one row: original name followed by the
        /// six pinyin token sections. Escaped here so both the INSERT path and the
        /// backfill comparison use the exact same string.
        /// </summary>
        public static string BuildFtsNameColumn(string name, string spaced, string connected, string bigrams, string singleChars, string cjkBigrams, string initials, string initialsBigrams)
        {
            return $"{TextUtil.Escape(name)} {TextUtil.Escape(spaced)} {TextUtil.Escape(connected)} {TextUtil.Escape(bigrams)} {TextUtil.Escape(singleChars)} {TextUtil.Escape(cjkBigrams)} {TextUtil.Escape(initials)} {TextUtil.Escape(initialsBigrams)}";
        }

        public static string BuildFtsInsertSql(long id, string name, string spaced, string connected, string bigrams, string singleChars, string cjkBigrams, string initials, string initialsBigrams, string origTitle = "", string seriesName = "", string album = "")
        {
            string c0 = BuildFtsNameColumn(name, spaced, connected, bigrams, singleChars, cjkBigrams, initials, initialsBigrams);
            string ot = TextUtil.Escape(origTitle);
            string sn = TextUtil.Escape(seriesName);
            string al = TextUtil.Escape(album);
            return $"INSERT OR REPLACE INTO {FtsTableName}(rowid,Name,OriginalTitle,SeriesName,Album) VALUES({id},'{c0}','{ot}','{sn}','{al}')";
        }

        // ── Single-row write ──
        public void ExecuteInsert(IDatabaseConnection conn, long id, string name, string spaced, string connected, string bigrams, string singleChars, string cjkBigrams, string initials, string initialsBigrams, string origTitle = "", string seriesName = "", string album = "")
        {
            conn.Execute(BuildFtsInsertSql(id, name, spaced, connected, bigrams, singleChars, cjkBigrams, initials, initialsBigrams, origTitle, seriesName, album));
        }

        /// <summary>
        /// Reads the existing c1/c2/c3 columns of a row so they survive
        /// INSERT OR REPLACE. On failure the out values stay empty and the
        /// failure is logged with warnLogFormat (caller-supplied message,
        /// preserving per-site wording). Never throws.
        /// </summary>
        public void ReadExistingColumns(IDatabaseConnection conn, long id, string warnLogFormat,
            out string origTitle, out string seriesName, out string album)
        {
            origTitle = "";
            seriesName = "";
            album = "";
            try
            {
                using (var r = conn.PrepareStatement(ExistingColumnsQuery(id)))
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
                _logger.Warn(warnLogFormat, id, ex.Message);
            }
        }

        /// <summary>
        /// Writes a batch of (id, name) rows inside one deferred transaction:
        /// generates pinyin, optionally preserves existing c1..c3 columns, and
        /// executes INSERT OR REPLACE per row. Returns the number of rows written.
        /// Per-item exceptions are logged with itemLogFormat and swallowed when it
        /// is non-null; when null (full-reindex path) they propagate and the whole
        /// batch rolls back. Transaction failure rolls back and returns the
        /// partially-counted value, matching the original loop semantics.
        /// </summary>
        public int WriteBatch(IDatabaseConnection conn,
            IReadOnlyList<Tuple<long, string>> rows,
            bool readExistingColumns,
            string readColumnsLogFormat,
            string itemLogFormat,
            string debugLogFormat,
            string errorLogFormat,
            long logOffset,
            Func<bool> shouldContinue)
        {
            conn.BeginTransaction(TransactionMode.Deferred);
            int processed = 0;
            try
            {
                foreach (var row in rows)
                {
                    try
                    {
                        if (!shouldContinue()) break;

                        long id = row.Item1;
                        string name = row.Item2;
                        var (spaced, connected, bigrams, singleChars, cjkBigrams, initials, initialsBigrams) = TinyPinyinLoader.GeneratePinyin(name);
                        if (string.IsNullOrEmpty(spaced)) continue;

                        string origTitle = "", seriesName = "", album = "";
                        if (readExistingColumns)
                            ReadExistingColumns(conn, id, readColumnsLogFormat, out origTitle, out seriesName, out album);

                        ExecuteInsert(conn, id, name, spaced, connected, bigrams, singleChars, cjkBigrams, initials, initialsBigrams, origTitle, seriesName, album);
                        processed++;
                    }
                    catch (Exception ex) when (itemLogFormat != null)
                    {
                        _logger.Warn(itemLogFormat, row.Item1, ex.Message);
                    }
                }

                conn.CommitTransaction();
                _logger.Debug(debugLogFormat, logOffset, processed);
            }
            catch (Exception ex)
            {
                conn.RollbackTransaction();
                _logger.Error(errorLogFormat, logOffset, ex.Message);
            }

            return processed;
        }
    }
}
