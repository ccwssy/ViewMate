using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;
using System.Reflection;
using System.Threading;

namespace ViewMate.Common
{
    /// <summary>
    /// Caches the Emby internal ConnectionManager and CreateConnection method
    /// via reflection, so each call creates a new IDatabaseConnection on demand.
    /// Duplicated in IntroBackfillService and PinyinSearchService — extracted here.
    /// </summary>
    public class ConnectionManagerCache
    {
        private object _connectionManager;
        private MethodInfo _createConnectionMethod;
        private readonly object _lock = new object();
        private readonly ILogger _logger;
        private readonly string _logPrefix;

        public ConnectionManagerCache(ILogger logger, string logPrefix)
        {
            _logger = logger;
            _logPrefix = logPrefix;
        }

        public IDatabaseConnection OpenReadConnection() => OpenConnection(true);
        public IDatabaseConnection OpenWriteConnection() => OpenConnection(false);

        private IDatabaseConnection OpenConnection(bool readOnly)
        {
            if (!TryEnsureConnectionManager())
                return null;

            try
            {
                return (IDatabaseConnection)_createConnectionMethod.Invoke(
                    _connectionManager, new object[] { readOnly, CancellationToken.None });
            }
            catch (Exception ex)
            {
                _logger.Error("[{0}] OpenConnection({1}) failed: {2}", _logPrefix, readOnly, ex.Message);
                return null;
            }
        }

        private bool TryEnsureConnectionManager()
        {
            if (_connectionManager != null && _createConnectionMethod != null)
                return true;

            lock (_lock)
            {
                if (_connectionManager != null && _createConnectionMethod != null)
                    return true;

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        var itemRepo = Plugin.Instance.ApplicationHost.Resolve<IItemRepository>();
                        var type = itemRepo.GetType();
                        while (type != null)
                        {
                            foreach (var f in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
                            {
                                var val = f.GetValue(itemRepo);
                                if (val == null) continue;
                                var fn = f.Name;
                                if (fn.Contains("ConnectionManager") || fn == "_connectionManager" || fn == "ConnectionManager")
                                {
                                    _connectionManager = val;
                                    _createConnectionMethod = val.GetType().GetMethod(
                                        "CreateConnection", new[] { typeof(bool), typeof(CancellationToken) });
                                    _logger.Info("[{0}] Cached ConnectionManager ({1})", _logPrefix, fn);
                                    return true;
                                }
                            }
                            type = type.BaseType;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("[{0}] Attempt {1} to find ConnectionManager failed: {2}", _logPrefix, attempt + 1, ex.Message);
                    }

                    if (attempt < 2)
                        Thread.Sleep(500);
                }

                _logger.Error("[{0}] Could not find ConnectionManager after retries", _logPrefix);
                return false;
            }
        }
    }
}
