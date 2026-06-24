using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.LocalizationAttributes;
using ViewMate.Properties;
using System.ComponentModel;
using System.Reflection;

namespace ViewMate.Options
{
    public class AboutOptions : EditableOptionsBase
    {
        [DisplayNameL("AboutOptions_EditorTitle_About", typeof(Resources))]
        public override string EditorTitle => Resources.AboutOptions_EditorTitle_About;

        public GenericItemList VersionInfoList { get; set; } = new GenericItemList();

        [Browsable(false)]
        public string DefaultUICulture { get; set; } = "zh-CN";

        [Browsable(false)]
        public bool DebugMode { get; set; } = true;

        [Browsable(false)]
        public string GitHubToken { get; set; } = string.Empty;

        [Browsable(false)]
        public string GitHubProxy { get; set; } = string.Empty;
        
        private static string GetVersionHash()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            var fullVersion = assembly.GetName().Version?.ToString();

            if (informationalVersion != null)
            {
                var parts = informationalVersion.Split('+');
                if (parts.Length > 1)
                    return $"{fullVersion}+{parts[1].Substring(0, 7)}";
                return fullVersion ?? informationalVersion;
            }

            return fullVersion ?? "unknown";
        }

        public void Initialize()
        {
            VersionInfoList.Clear();

            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = "观影助手",
                    Icon = IconNames.title,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/ccwssy/ViewMate",
                });

            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = GetVersionHash(),
                    Icon = IconNames.info,
                    IconMode = ItemListIconMode.SmallRegular
                });
            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = "GitHub 仓库",
                    Icon = IconNames.code,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/ccwssy/ViewMate",
                });

            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = "上游: StrmAssistant_less",
                    Icon = IconNames.code,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/xinjiawei/StrmAssistant_less",
                });
            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = "上游: StrmAssistant (原始版)",
                    Icon = IconNames.code,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/sjtuross/StrmAssistant",
                });
        }
    }
}
