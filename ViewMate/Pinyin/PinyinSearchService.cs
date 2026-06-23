using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TinyPinyin;

#nullable disable
namespace ViewMate.Pinyin
{
    public class PinyinSearchService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private bool _disposed = false;

        private static readonly Regex ChineseRegex = new Regex(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
        private const string FtsTableName = "fts_search9";

        public PinyinSearchService(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;

            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemUpdated;
        }

        public int ProcessAllPending()
        {
            if (!Plugin.Instance.Configuration.EnablePinyinSearch)
            {
                _logger.Info("[PinyinSearch] Disabled by config");
                return 0;
            }

            _logger.Info("[PinyinSearch] Scanning for pending items...");
            var connection = GetDbConnection();
            if (connection == null) return 0;

            int processed = 0;
            try
            {
                var query = $@"
                    SELECT c.id, mi.Name
                    FROM {FtsTableName}_content c
                    JOIN MediaItems mi ON c.id = mi.RowId
                    WHERE c.c0 NOT GLOB '*[a-zA-Z]*'
                      AND c.c0 GLOB '*[\u4e00-\u9fa5]*'
                      AND mi.Name NOT GLOB '*Season*'
                      AND mi.Name NOT GLOB '*Episode*'
                      AND mi.Name NOT GLOB '*Media Folder*'
                    LIMIT 5000";

                var rows = new List<Tuple<long, string>>();
                using (var stmt = connection.PrepareStatement(query))
                {
                    while (stmt.MoveNext())
                        rows.Add(Tuple.Create(stmt.Current.GetInt64(0), stmt.Current.GetString(1)));
                }

                if (rows.Count == 0)
                {
                    _logger.Info("[PinyinSearch] No pending items");
                    return 0;
                }

                _logger.Info("[PinyinSearch] Found {0} items", rows.Count);
                connection.BeginTransaction(TransactionMode.Immediate);
                try
                {
                    foreach (var row in rows)
                    {
                        try
                        {
                            long id = row.Item1;
                            string name = row.Item2;
                            var (spaced, connected) = GeneratePinyin(name);
                            if (string.IsNullOrEmpty(spaced)) continue;

                            string esc = name.Replace("'", "''");
                            string s = spaced.Replace("'", "''");
                            string c = connected.Replace("'", "''");
                            connection.Execute(
                                $"UPDATE {FtsTableName}_content SET c0 = '{esc} {s} {c}' WHERE id = {id}");
                            connection.Execute(
                                $"UPDATE MediaItems SET Name = '{esc} {s} {c}' WHERE RowId = {id}");
                            processed++;
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn("[PinyinSearch] Item {0}: {1}", row.Item1, ex.Message);
                        }
                    }

                    connection.CommitTransaction();
                    connection.Execute($"INSERT INTO {FtsTableName}({FtsTableName}) VALUES('rebuild')");
                    _logger.Info("[PinyinSearch] Complete: {0} items injected, FTS rebuilt", processed);
                }
                catch (Exception ex)
                {
                    connection.RollbackTransaction();
                    _logger.Error("[PinyinSearch] Batch failed, rolled back", ex);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[PinyinSearch] Scan failed", ex);
            }

            return processed;
        }

        public bool ProcessItem(BaseItem item)
        {
            if (!Plugin.Instance.Configuration.EnablePinyinSearch) return false;
            if (!IsCjkItem(item)) return false;

            var name = item.Name;
            if (string.IsNullOrEmpty(name)) return false;

            var (spaced, connected) = GeneratePinyin(name);
            if (string.IsNullOrEmpty(spaced)) return false;

            var connection = GetDbConnection();
            if (connection == null) return false;

            try
            {
                string esc = name.Replace("'", "''");
                string s = spaced.Replace("'", "''");
                string c = connected.Replace("'", "''");
                connection.Execute(
                    $"UPDATE {FtsTableName}_content SET c0 = '{esc} {s} {c}' WHERE id = {item.Id}");
                connection.Execute(
                    $"UPDATE MediaItems SET Name = '{esc} {s} {c}' WHERE RowId = {item.Id}");
                connection.Execute($"INSERT INTO {FtsTableName}({FtsTableName}) VALUES('rebuild')");
                _logger.Info("[PinyinSearch] Injected '{0}'", name);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn("[PinyinSearch] '{0}': {1}", name, ex.Message);
                return false;
            }
        }

        public static (string spaced, string connected) GeneratePinyin(string text)
        {
            if (string.IsNullOrEmpty(text)) return (null, null);

            var sbSpaced = new StringBuilder();
            var sbConnected = new StringBuilder();
            bool hasChinese = false;

            foreach (char ch in text)
            {
                if (ch >= 0x4e00 && ch <= 0x9fff)
                {
                    try
                    {
                        var p = PinyinHelper.GetPinyin(ch);
                        if (!string.IsNullOrEmpty(p))
                        {
                            sbSpaced.Append(p);
                            sbSpaced.Append(' ');
                            sbConnected.Append(p);
                            hasChinese = true;
                        }
                    }
                    catch { }
                }
            }

            if (!hasChinese) return (null, null);
            return (sbSpaced.ToString().TrimEnd(), sbConnected.ToString());
        }

        public static bool IsCjkItem(BaseItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Name)) return false;
            if (item.IsDisplayedAsFolder && !(item is Series)) return false;
            return ChineseRegex.IsMatch(item.Name);
        }

        private IDatabaseConnection GetDbConnection()
        {
            try
            {
                var itemRepo = Plugin.Instance.ApplicationHost.Resolve<IItemRepository>();
                if (itemRepo == null) return null;
                var repoType = itemRepo.GetType();
                var connField = repoType.GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance);
                if (connField == null)
                    connField = repoType.GetField("Connection", BindingFlags.NonPublic | BindingFlags.Instance);
                return connField?.GetValue(itemRepo) as IDatabaseConnection;
            }
            catch (Exception ex)
            {
                _logger.Error("[PinyinSearch] GetDbConnection failed", ex);
                return null;
            }
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
            if (_libraryManager != null)
            {
                _libraryManager.ItemAdded -= OnItemAdded;
                _libraryManager.ItemUpdated -= OnItemUpdated;
            }
        }
    }
}
