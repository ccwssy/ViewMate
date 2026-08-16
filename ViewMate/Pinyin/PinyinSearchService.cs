using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using System;
using System.Threading;

#nullable enable
namespace ViewMate.Pinyin
{
    /// <summary>
    /// Facade for the pinyin search subsystem (phase-2 split). Keeps the public
    /// API surface used by Plugin.cs unchanged — constructor, ProcessAllPendingDeferred(),
    /// Dispose() — plus the public statics GeneratePinyin/IsCjkItem. Internal
    /// responsibilities are delegated:
    ///   - TinyPinyin loading + pinyin generation → TinyPinyinLoader
    ///   - FTS SQL + batched writes/transactions → FtsIndexWriter
    ///   - event queue, timers, scan paths → FtsScanScheduler
    /// Config gating and disposal coordination stay here.
    /// </summary>
    public class PinyinSearchService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly FtsScanScheduler _scheduler;

        // Thread-safe disposal flag
        private int _disposed;

        public PinyinSearchService(ILibraryManager libraryManager, ILogger logger)
        {
            _logger = logger;
            TinyPinyinLoader.SetStaticLogger(logger);
            _scheduler = new FtsScanScheduler(libraryManager, logger);
        }

        // ── Deferred background scan ──

        public void ProcessAllPendingDeferred()
        {
            if (!Plugin.Instance.Configuration.EnablePinyinSearch)
            {
                _logger.Info("[PinyinSearch] Disabled by config");
                return;
            }

            _scheduler.ProcessAllPendingDeferred();
        }

        // ── Pinyin generation (public static API, delegates to shared loader) ──

        public static (string spaced, string connected, string bigrams, string singleChars, string cjkBigrams) GeneratePinyin(string text)
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
