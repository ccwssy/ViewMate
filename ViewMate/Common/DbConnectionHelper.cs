using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;
using System;
using System.Reflection;
using System.Threading;

namespace ViewMate.Common
{
    internal static class DbConnectionHelper
    {
        /// <summary>
        /// Get the SQLite IDatabaseConnection from IItemRepository.
        /// Emby uses various internal field names and wrapper types (ManagedConnection)
        /// that differ between versions. This method walks the full type hierarchy
        /// and handles both direct IDatabaseConnection fields and wrapped ones.
        /// </summary>
        public static IDatabaseConnection GetConnection(IItemRepository itemRepo, ILogger logger, string logPrefix)
        {
            if (itemRepo == null)
            {
                logger.Error("[{0}] itemRepo is null", logPrefix);
                return null;
            }

            try
            {
                // Walk the full type hierarchy (including base classes)
                var type = itemRepo.GetType();
                while (type != null)
                {
                    var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    foreach (var f in fields)
                    {
                        var val = f.GetValue(itemRepo);
                        if (val == null) continue;

                        // Direct IDatabaseConnection
                        if (val is IDatabaseConnection conn)
                            return conn;

                        // Wrapped — check properties and fields of the wrapper object
                        var valType = val.GetType();
                        var name = valType.Name;

                        // Known wrapper: ManagedConnection (Emby's internal wrapper)
                        // Check its public/internal fields and properties
                        var innerFields = valType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                        foreach (var innerF in innerFields)
                        {
                            if (innerF.GetValue(val) is IDatabaseConnection innerConn)
                            {
                                logger.Info("[{0}] Found IDatabaseConnection inside {1}.{2}", logPrefix, name, innerF.Name);
                                return innerConn;
                            }
                        }

                        var innerProps = valType.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                        foreach (var p in innerProps)
                        {
                            try
                            {
                                if (p.GetValue(val) is IDatabaseConnection propConn)
                                {
                                    logger.Info("[{0}] Found IDatabaseConnection via {1}.{2}", logPrefix, name, p.Name);
                                    return propConn;
                                }
                            }
                            catch
                            {
                                // Some properties may throw on get
                            }
                        }
                    }

                    type = type.BaseType;
                }

                // Last resort: scan ALL fields on the top-level type for anything assignable to IDatabaseConnection
                // (catches edge cases where the field type is an interface type itself)
                type = itemRepo.GetType();
                while (type != null)
                {
                    var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    foreach (var f in fields)
                    {
                        if (typeof(IDatabaseConnection).IsAssignableFrom(f.FieldType))
                        {
                            var val = f.GetValue(itemRepo);
                            if (val != null)
                            {
                                logger.Info("[{0}] Found assignable field {1} of type {2}", logPrefix, f.Name, f.FieldType.Name);
                                return (IDatabaseConnection)val;
                            }
                        }
                        // Also check for ISqliteDatabase (another Emby internal wrapper)
                        if (f.FieldType.Name == "ISqliteDatabase" || f.FieldType.Name == "SqliteDatabase")
                        {
                            var val = f.GetValue(itemRepo);
                            if (val != null)
                            {
                                // Try to get connection from SQLiteDatabase
                                var connProp = val.GetType().GetProperty("Connection",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (connProp?.GetValue(val) is IDatabaseConnection dbConn)
                                {
                                    logger.Info("[{0}] Found ISqliteDatabase.{1} via {2}", logPrefix, connProp.Name, f.Name);
                                    return dbConn;
                                }
                            }
                        }
                    }
                    type = type.BaseType;
                }

                logger.Error("[{0}] No connection field found on {1}", logPrefix, itemRepo.GetType().FullName);

                // Fallback: try ConnectionManager (PooledDatabaseConnectionManager)
                // Found via type: BaseSqliteRepository.<ConnectionManager>k__BackingField
                logger.Info("[{0}] Trying ConnectionManager fallback...", logPrefix);
                type = itemRepo.GetType();
                while (type != null)
                {
                    foreach (var f in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
                    {
                        var val = f.GetValue(itemRepo);
                        if (val == null) continue;
                        var fn = f.Name;
                        // Match both <ConnectionManager>k__BackingField and _connectionManager
                        if (fn.Contains("ConnectionManager") || fn == "_connectionManager" || fn == "ConnectionManager")
                        {
                            var mgrType = val.GetType();
                            logger.Info("[{0}] Found ConnectionManager: {1} ({2})", logPrefix, fn, mgrType.Name);
                            var mgrConn = ExtractConnectionFromManager(val, logger, logPrefix);
                            if (mgrConn != null) return mgrConn;
                        }

                        // Also check for _connection or Connection fields that might be ManagedConnection
                        // and whose value's type name contains "Connection"
                        var valTypeName = val.GetType().Name;
                        if (valTypeName.Contains("ManagedConnection") || valTypeName.Contains("SqliteConnection") || valTypeName == "DatabaseConnection")
                        {
                            logger.Info("[{0}] Found potential connection field: {1} ({2})", logPrefix, fn, valTypeName);
                            var unwrapped = UnwrapConnection(val, logger, logPrefix);
                            if (unwrapped != null) return unwrapped;
                        }
                    }
                    type = type.BaseType;
                }

                logger.Error("[{0}] All fallbacks failed on {1}", logPrefix, itemRepo.GetType().FullName);
            }
            catch (Exception ex)
            {
                logger.Error("[{0}] GetDbConnection failed: {1}", logPrefix, ex.Message);
                logger.Debug("[{0}] {1}", logPrefix, ex.StackTrace);
            }

            return null;
        }

        private static IDatabaseConnection ExtractConnectionFromManager(object mgr, ILogger logger, string logPrefix)
        {
            var mgrType = mgr.GetType();

            // Emby 4.8: PooledDatabaseConnectionManager.CreateConnection(isReadOnly, cancellationToken)
            // Try exact match first (returns IDatabaseConnection directly)
            var createConnMethod = mgrType.GetMethod("CreateConnection", new[] { typeof(bool), typeof(CancellationToken) });
            if (createConnMethod != null)
            {
                try
                {
                    var result = createConnMethod.Invoke(mgr, new object[] { false, CancellationToken.None });
                    if (result is IDatabaseConnection conn)
                    {
                        logger.Info("[{0}] Got connection via CreateConnection(false, default)", logPrefix);
                        return conn;
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn("[{0}] CreateConnection failed: {1}", logPrefix, ex.Message);
                }
            }

            // Method: GetConnection() or OpenConnection()
            foreach (var name in new[] { "GetConnection", "OpenConnection", "Get", "Open" })
            {
                var method = mgrType.GetMethod(name, Type.EmptyTypes);
                if (method != null)
                {
                    try
                    {
                        var result = method.Invoke(mgr, null);
                        if (result is IDatabaseConnection conn)
                        {
                            logger.Info("[{0}] Got connection via {1}()", logPrefix, name);
                            return conn;
                        }
                        // Try unwrapping the result
                        if (result != null)
                        {
                            var unwrapped = UnwrapConnection(result, logger, logPrefix);
                            if (unwrapped != null) return unwrapped;
                        }
                    }
                    catch { }
                }
            }

            // Property: Connection (might be on a base type or interface)
            foreach (var prop in new[] { "Connection", "CurrentConnection", "ActiveConnection", "DbConnection" })
            {
                var p = mgrType.GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null)
                {
                    try
                    {
                        var val = p.GetValue(mgr);
                        if (val is IDatabaseConnection conn)
                        {
                            logger.Info("[{0}] Got connection via {1}.{2}", logPrefix, mgrType.Name, prop);
                            return conn;
                        }
                        if (val != null)
                        {
                            var unwrapped = UnwrapConnection(val, logger, logPrefix);
                            if (unwrapped != null) return unwrapped;
                        }
                    }
                    catch { }
                }
            }

            // Field: _connection or connection
            foreach (var fieldName in new[] { "_connection", "connection", "_db", "db" })
            {
                var field = mgrType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    var val = field.GetValue(mgr);
                    if (val is IDatabaseConnection conn)
                        return conn;
                    if (val != null)
                    {
                        var unwrapped = UnwrapConnection(val, logger, logPrefix);
                        if (unwrapped != null) return unwrapped;
                    }
                }
            }

            logger.Warn("[{0}] Could not extract connection from {1}", logPrefix, mgrType.FullName);
            return null;
        }

        private static IDatabaseConnection UnwrapConnection(object wrapper, ILogger logger, string logPrefix)
        {
            var wType = wrapper.GetType();

            // Check all fields for IDatabaseConnection
            var fields = wType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            foreach (var f in fields)
            {
                var val = f.GetValue(wrapper);
                if (val is IDatabaseConnection conn)
                {
                    logger.Info("[{0}] Unwrapped via field {1}.{2}", logPrefix, wType.Name, f.Name);
                    return conn;
                }
            }

            // Check all properties for IDatabaseConnection
            var props = wType.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            foreach (var p in props)
            {
                try
                {
                    var val = p.GetValue(wrapper);
                    if (val is IDatabaseConnection conn)
                    {
                        logger.Info("[{0}] Unwrapped via property {1}.{2}", logPrefix, wType.Name, p.Name);
                        return conn;
                    }
                }
                catch { }
            }

            // Check all methods returning IDatabaseConnection
            var methods = wType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var m in methods)
            {
                if (m.ReturnType == typeof(IDatabaseConnection) && m.GetParameters().Length == 0)
                {
                    try
                    {
                        var val = m.Invoke(wrapper, null);
                        if (val is IDatabaseConnection conn)
                        {
                            logger.Info("[{0}] Unwrapped via method {1}.{2}()", logPrefix, wType.Name, m.Name);
                            return conn;
                        }
                    }
                    catch { }
                }
            }

            return null;
        }
    }
}
