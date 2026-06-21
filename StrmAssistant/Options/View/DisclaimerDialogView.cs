using MediaBrowser.Model.Plugins;
using StrmAssistant.Options.UIBaseClasses.Views;
using StrmAssistant.Properties;
using System.Threading.Tasks;

namespace StrmAssistant.Options.View
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
