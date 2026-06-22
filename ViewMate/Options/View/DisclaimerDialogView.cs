using MediaBrowser.Model.Plugins;
using ViewMate.Options.UIBaseClasses.Views;
using ViewMate.Properties;
using System.Threading.Tasks;

namespace ViewMate.Options.View
{
    internal class DisclaimerDialogView : PluginDialogView
    {
        public DisclaimerDialogView(PluginInfo pluginInfo) : base(pluginInfo.Id)
        {
            ContentData = new DisclaimerDialog();
            AllowCancel = false;
            OKButtonCaption = Resources.OKButtonCaption;
        }

        public override string Caption => Resources.Disclaimer;

        public DisclaimerDialog DisclaimerDialog => ContentData as DisclaimerDialog;

        public override Task OnOkCommand(string providerId, string commandId, string data)
        {
            return Task.CompletedTask;
        }
    }
}
