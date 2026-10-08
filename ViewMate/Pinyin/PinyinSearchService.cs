using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using System;
using System.Threading;

#nullable enable
namespace ViewMate.Pinyin
{
    /// <summary>
    /// 拼音搜索子系统的门面（第二阶段拆分）。保持 Plugin.cs 使用的
    /// 公开 API 面不变 —— 构造函数、ProcessAllPendingDeferred()、
    /// Dispose() —— 另外还有公开静态方法 GeneratePinyin/IsCjkItem。内部
    /// 职责分别委托给：
    ///   - TinyPinyin 加载 + 拼音生成 → TinyPinyinLoader
    ///   - FTS SQL + 批量写入/事务 → FtsIndexWriter
    ///   - 事件队列、定时器、扫描流程 → FtsScanScheduler
    /// 配置开关判断与销毁协调仍留在本类。
    /// </summary>
    public class PinyinSearchService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly FtsScanScheduler _scheduler;

        // 线程安全的销毁标志
        private int _disposed;

        public PinyinSearchService(ILibraryManager libraryManager, ILogger logger)
        {
            _logger = logger;
            TinyPinyinLoader.SetStaticLogger(logger);
            _scheduler = new FtsScanScheduler(libraryManager, logger);
        }

        // ── 延后的后台扫描 ──

        public void ProcessAllPendingDeferred()
        {
            if (!Plugin.Instance.Configuration.EnablePinyinSearch)
            {
                _logger.Info("[PinyinSearch] Disabled by config");
                return;
            }

            _scheduler.ProcessAllPendingDeferred();
        }

        // ── 拼音生成（公开静态 API，委托给共用加载器） ──

        public static (string spaced, string connected, string bigrams, string singleChars, string cjkBigrams, string initials, string initialsSuffixes) GeneratePinyin(string text)
            => TinyPinyinLoader.GeneratePinyin(text);

        public static bool IsCjkItem(BaseItem item)
            => TinyPinyinLoader.IsCjkItem(item);

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _scheduler.Dispose();
        }
    }
}
