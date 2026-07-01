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
        public PluginOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
            : base(applicationHost, logger, pluginFullName)
        {
        }

        public PluginOptions PluginOptions => GetOptions();
    }
}
