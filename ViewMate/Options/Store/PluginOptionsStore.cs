using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.PropertyDiff;
using MediaBrowser.Common;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using ViewMate.Mod;
using ViewMate.Options.UIBaseClasses.Store;
using ViewMate.Properties;
using SQLitePCL.pretty;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using static ViewMate.Common.CommonUtility;
using static ViewMate.Options.Utility;

namespace ViewMate.Options.Store
{
    public class PluginOptionsStore : SimpleFileStore<PluginOptions>
    {
        private readonly ILogger _logger;

        private bool _currentSuppressOnOptionsSaved;

        public PluginOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
            : base(applicationHost, logger, pluginFullName)
        {
            _logger = logger;

            FileSaved += OnFileSaved;
            FileSaving += OnFileSaving;
        }

        public PluginOptions PluginOptions => GetOptions();

        public void SavePluginOptionsSuppress()
        {
            _currentSuppressOnOptionsSaved = true;
            SetOptions(PluginOptions);
        }

        private void OnFileSaving(object sender, FileSavingEventArgs e)
        {
            if (e.Options is PluginOptions options)
            {
                var suppress = _currentSuppressOnOptionsSaved;

                options.NetworkOptions.ProxyServerUrl =
                    !string.IsNullOrWhiteSpace(options.NetworkOptions.ProxyServerUrl)
                        ? options.NetworkOptions.ProxyServerUrl.Trim().TrimEnd('/')
                        : options.NetworkOptions.ProxyServerUrl?.Trim();

                if (!suppress)
                {
                    if (options.NetworkOptions.EnableProxyServer &&
                        !string.IsNullOrWhiteSpace(options.NetworkOptions.ProxyServerUrl))
                    {
                        if (TryParseProxyUrl(options.NetworkOptions.ProxyServerUrl, out var schema, out var host, out var port,
                                out var username, out var password) &&
                            CheckProxyReachability(schema, host, port, username, password) is (true, var httpPing))
                        {
                            options.NetworkOptions.ProxyServerStatus.Status = ItemStatus.Succeeded;
                            options.NetworkOptions.ProxyServerStatus.Caption = Resources.ProxyServer_Available;
                            options.NetworkOptions.ProxyServerStatus.StatusText = $"{httpPing} ms";
                        }
                        else
                        {
                            options.NetworkOptions.ProxyServerStatus.Status = ItemStatus.Unavailable;
                            options.NetworkOptions.ProxyServerStatus.Caption = Resources.ProxyServer_Unavailable;
                            options.NetworkOptions.ProxyServerStatus.StatusText = "N/A";
                        }

                        options.NetworkOptions.ShowProxyServerStatus = true;
                    }
                    else
                    {
                        options.NetworkOptions.ProxyServerStatus.StatusText = string.Empty;
                        options.NetworkOptions.ShowProxyServerStatus = false;
                    }
                }

                var changes = PropertyChangeDetector.DetectObjectPropertyChanges(PluginOptions, options);
                var changedProperties = new HashSet<string>(changes.Select(c => c.PropertyName));

                if (PatchManager.GetMod<EnhanceChineseSearch>() != null)
                {
                    var isSimpleTokenizer = string.Equals(EnhanceChineseSearch.CurrentTokenizerName, "simple",
                        StringComparison.Ordinal);
                    options.ModOptions.EnhanceChineseSearchRestore =
                        !options.ModOptions.EnhanceChineseSearch && isSimpleTokenizer;

                    if (changedProperties.Contains(nameof(PluginOptions.ModOptions.EnhanceChineseSearch)) &&
                        ((!options.ModOptions.EnhanceChineseSearch && isSimpleTokenizer) ||
                         (options.ModOptions.EnhanceChineseSearch && !isSimpleTokenizer)))
                    {
                        NotifyPendingRestart();
                    }
                }

                if (changedProperties.Contains(nameof(PluginOptions.ModOptions.SearchScope)))
                {
                    UpdateSearchScope(options.ModOptions.SearchScope);
                }

                if (changedProperties.Contains(nameof(PluginOptions.NetworkOptions.EnableProxyServer)))
                {
                    if (options.NetworkOptions.EnableProxyServer)
                    {
                        PatchManager.GetMod<EnableProxyServer>().Patch();
                    }
                    else
                    {
                        PatchManager.GetMod<EnableProxyServer>().Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(PluginOptions.NetworkOptions.ProxyServerUrl)) ||
                    changedProperties.Contains(nameof(PluginOptions.NetworkOptions.EnableProxyServer)))
                {
                    if (options.NetworkOptions.EnableProxyServer &&
                        options.NetworkOptions.ProxyServerStatus.Status == ItemStatus.Succeeded)
                    {
                        NotifyPendingRestart();
                    }
                    else if (!options.NetworkOptions.EnableProxyServer)
                    {
                        NotifyPendingRestart();
                    }
                }
            }
        }

        private void OnFileSaved(object sender, FileSavedEventArgs e)
        {
            if (e.Options is PluginOptions options)
            {
                var suppress = _currentSuppressOnOptionsSaved;

                if (!suppress)
                {
                    _logger.Info("EnhanceChineseSearch is set to {0}", options.ModOptions.EnhanceChineseSearch);

                    // If Chinese search just got enabled and tokenizer not yet loaded, force init now
                    if (options.ModOptions.EnhanceChineseSearch)
                    {
                        var mod = PatchManager.GetMod<EnhanceChineseSearch>();
                        if (mod != null && string.Equals(EnhanceChineseSearch.CurrentTokenizerName, "unknown", StringComparison.OrdinalIgnoreCase))
                        {
                            ForceInitializeTokenizer();
                        }
                    }

                    var searchScope = string.Join(", ",
                        options.ModOptions.SearchScope
                            ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s =>
                                Enum.TryParse(s.Trim(), true, out ModOptions.SearchItemType type)
                                    ? type.GetDescription()
                                    : null)
                            .Where(d => d != null) ?? Enumerable.Empty<string>());
                    _logger.Info("EnhanceChineseSearch - SearchScope is set to {0}",
                        string.IsNullOrEmpty(searchScope) ? "ALL" : searchScope);
                    _logger.Info("ExcludeOriginalTitleFromSearch is set to {0}", options.ModOptions.ExcludeOriginalTitleFromSearch);

                    _logger.Info("EnableProxyServer is set to {0}", options.NetworkOptions.EnableProxyServer);
                    _logger.Info("ProxyServerUrl is set to {0}",
                        !string.IsNullOrEmpty(options.NetworkOptions.ProxyServerUrl)
                            ? options.NetworkOptions.ProxyServerUrl
                            : "EMPTY");
                }

                if (suppress) _currentSuppressOnOptionsSaved = false;
            }
        }

        private void ForceInitializeTokenizer()
        {
            try
            {
                _logger.Info("ForceInitializeTokenizer - Starting");

                // Get the existing SqliteItemRepository instance from the item repository
                var asm = Assembly.Load("Emby.Server.Implementations");
                var repoType = asm.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
                if (repoType == null)
                {
                    _logger.Warn("ForceInitializeTokenizer - Could not find SqliteItemRepository type");
                    return;
                }

                // Find the connection field on the concrete SqliteItemRepository
                FieldInfo connField = null;
                foreach (var f in new[] { "_connection", "connection", "_db", "db" })
                {
                    connField = repoType.GetField(f, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.Instance);
                    if (connField != null && typeof(IDatabaseConnection).IsAssignableFrom(connField.FieldType))
                    {
                        _logger.Info("ForceInitializeTokenizer - Found connection field: " + f);
                        break;
                    }
                }

                if (connField == null)
                {
                    _logger.Warn("ForceInitializeTokenizer - Could not find connection field");
                    return;
                }

                // Get the instance - it's registered in the DI container
                var itemRepo = Plugin.Instance.ApplicationHost.Resolve<IItemRepository>();
                if (itemRepo == null)
                {
                    _logger.Warn("ForceInitializeTokenizer - Could not resolve IItemRepository");
                    return;
                }

                // Get the connection value
                var connection = connField.GetValue(itemRepo) as IDatabaseConnection;
                if (connection == null)
                {
                    _logger.Warn("ForceInitializeTokenizer - Connection is null");
                    return;
                }

                _logger.Info("ForceInitializeTokenizer - Got connection, calling ForceInitializeOnConnection");
                var result = EnhanceChineseSearch.ForceInitializeOnConnection(connection);
                _logger.Info("ForceInitializeTokenizer - Result: {0}", result);
            }
            catch (Exception ex)
            {
                _logger.Warn("ForceInitializeTokenizer failed: " + ex.Message);
                if (Plugin.Instance.DebugMode)
                    _logger.Debug(ex.StackTrace);
            }
        }
    }
}