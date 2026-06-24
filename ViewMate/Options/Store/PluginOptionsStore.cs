using Emby.Web.GenericEdit.PropertyDiff;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using ViewMate.Options.UIBaseClasses.Store;
using System;
using System.Collections.Generic;
using System.Linq;

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
        }

        private void OnFileSaved(object sender, FileSavedEventArgs e)
        {
            if (e.Options is PluginOptions options)
            {
                var suppress = _currentSuppressOnOptionsSaved;
                if (suppress) _currentSuppressOnOptionsSaved = false;
            }
        }
    }
}
