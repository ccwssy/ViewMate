using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;
using System.Collections.Generic;
using ViewMate.Common;

namespace ViewMate.Pinyin
{
    /// <summary>
    /// FTS5 索引写入器（PinyinSearchService 的第二阶段拆分）：构建全部 FTS
    /// SQL（待处理/追赶/媒体项查询 + INSERT OR REPLACE），重新插入时保留
    /// 原有的 c1..c3 列，并把批量写入放在单个
    /// 延迟事务里执行（成功则提交，失败则回滚）。
    /// 文本转义在 Common/TextUtil 中。
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

        // ── 待处理项查询构建 ──
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

        // ── 追赶查询（不带 id 过滤） ──
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

        // ── 回填查询（按游标分页，写入拼音的行会带上首字母 token） ──
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

        // ── 全量重建索引查询 ──
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

        // ── 行级 SQL ──
        public static string ExistingColumnsQuery(long id) =>
            $"SELECT c1, c2, c3 FROM {FtsTableName}_content WHERE id = {id}";

        /// <summary>
        /// 某一行 `Name` 列（c0）的取值：原始名称，后接
        /// 六段拼音 token。在此处转义，使 INSERT 路径与
        /// 回填比较使用完全相同的字符串。
        /// </summary>
        public static string BuildFtsNameColumn(string name, string spaced, string connected, string bigrams, string singleChars, string cjkBigrams, string initials, string initialsSuffixes)
        {
            return $"{TextUtil.Escape(name)} {TextUtil.Escape(spaced)} {TextUtil.Escape(connected)} {TextUtil.Escape(bigrams)} {TextUtil.Escape(singleChars)} {TextUtil.Escape(cjkBigrams)} {TextUtil.Escape(initials)} {TextUtil.Escape(initialsSuffixes)}";
        }

        public static string BuildFtsInsertSql(long id, string name, string spaced, string connected, string bigrams, string singleChars, string cjkBigrams, string initials, string initialsSuffixes, string origTitle = "", string seriesName = "", string album = "")
        {
            string c0 = BuildFtsNameColumn(name, spaced, connected, bigrams, singleChars, cjkBigrams, initials, initialsSuffixes);
            string ot = TextUtil.Escape(origTitle);
            string sn = TextUtil.Escape(seriesName);
            string al = TextUtil.Escape(album);
            return $"INSERT OR REPLACE INTO {FtsTableName}(rowid,Name,OriginalTitle,SeriesName,Album) VALUES({id},'{c0}','{ot}','{sn}','{al}')";
        }

        // ── 单行写入 ──
        public void ExecuteInsert(IDatabaseConnection conn, long id, string name, string spaced, string connected, string bigrams, string singleChars, string cjkBigrams, string initials, string initialsSuffixes, string origTitle = "", string seriesName = "", string album = "")
        {
            conn.Execute(BuildFtsInsertSql(id, name, spaced, connected, bigrams, singleChars, cjkBigrams, initials, initialsSuffixes, origTitle, seriesName, album));
        }

        /// <summary>
        /// 读出某一行已有的 c1/c2/c3 列，使其在
        /// INSERT OR REPLACE 之后得以保留。失败时输出值保持为空，
        /// 并按 warnLogFormat 记录日志（调用方提供的消息，
        /// 以保留各调用点的措辞）。本方法从不抛异常。
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
        /// 在单个延迟事务里写入一批 (id, name) 行：
        /// 生成拼音，可选地保留已有的 c1..c3 列，
        /// 并逐行执行 INSERT OR REPLACE。返回已写入的行数。
        /// 单项异常在 itemLogFormat 非空时记录日志并吞掉；
        /// 为 null（全量重建索引路径）时则向上抛出，
        /// 整批回滚。事务失败时回滚，并返回
        /// 部分计数的结果，与原先循环的语义一致。
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
                        var (spaced, connected, bigrams, singleChars, cjkBigrams, initials, initialsSuffixes) = TinyPinyinLoader.GeneratePinyin(name);
                        if (string.IsNullOrEmpty(spaced)) continue;

                        string origTitle = "", seriesName = "", album = "";
                        if (readExistingColumns)
                            ReadExistingColumns(conn, id, readColumnsLogFormat, out origTitle, out seriesName, out album);

                        ExecuteInsert(conn, id, name, spaced, connected, bigrams, singleChars, cjkBigrams, initials, initialsSuffixes, origTitle, seriesName, album);
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
