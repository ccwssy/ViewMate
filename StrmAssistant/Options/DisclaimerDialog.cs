using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;

namespace StrmAssistant.Options
{
    public class DisclaimerDialog : EditableOptionsBase
    {
        public DisclaimerDialog()
        {
            UsageNoticeList.AddRange(new[]
            {
                new GenericListItem
                {
                    PrimaryText = "合法使用",
                    SecondaryText = "本项目仅适用于合法安装和使用 Emby 软件的用户。\n使用本项目时，用户需自行确保遵守 Emby 软件的服务条款和使用许可协议。",
                    Icon = IconNames.label_important,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded
                },
                new GenericListItem
                {
                    PrimaryText = "非商业用途",
                    SecondaryText = "本项目完全免费，仅限个人学习、研究和非商业用途。\n严禁将本项目或其衍生版本用于任何商业用途。",
                    Icon = IconNames.label_important,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded
                },
                new GenericListItem
                {
                    PrimaryText = "不包含 Emby 专有组件",
                    SecondaryText = "本项目未包含 Emby 软件的任何专有组件（例如：DLL 文件、代码、图标或其他版权资源）。\n使用本项目不会直接修改或分发 Emby 软件本身。",
                    Icon = IconNames.label_important,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded
                },
                new GenericListItem
                {
                    PrimaryText = "功能限制",
                    SecondaryText = "本项目不会绕过 Emby 的授权机制、数字版权保护 (DRM)，或以任何方式解锁其付费功能。\n本项目仅在运行时动态注入代码，且不会篡改 Emby 软件的核心功能。",
                    Icon = IconNames.label_important,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded
                },
                new GenericListItem
                {
                    PrimaryText = "用户责任",
                    SecondaryText = "用户在使用本项目时，需自行承担遵守相关法律法规的责任。\n如果用户使用本项目违反了 Emby 的服务条款或相关法律法规，本项目开发者概不负责。",
                    Icon = IconNames.label_important,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded
                }
            });
            DisclaimerList.AddRange(new[]
            {
                new GenericListItem
                {
                    SecondaryText = "本项目开发者不对因使用本项目而可能导致的任何直接或间接后果，\n包括但不限于数据丢失、软件故障或法律纠纷负责。",
                    ShowSecondaryFirst = true,
                    Icon = IconNames.label_important,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded
                },
                new GenericListItem
                {
                    SecondaryText = "如果认为本项目可能侵犯相关方的合法权益，请与开发者取得联系。",
                    ShowSecondaryFirst = true,
                    Icon = IconNames.label_important,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded
                }
            });
        }

        public override string EditorTitle => "声明";

        public LabelItem Statement { get; set; } = new LabelItem(
            "本项目为开源项目，与 Emby LLC 没有任何关联，也未获得 Emby LLC 的授权或认可。本项目的目的是为合法购买并安装了 Emby 软件的用户提供额外的功能增强和使用便利。");

        public CaptionItem UsageNoticeCaption { get; set; } = new CaptionItem("使用须知");

        public GenericItemList UsageNoticeList { get; set; } = new GenericItemList();

        public CaptionItem DisclaimerCaption { get; set; } = new CaptionItem("免责声明");

        public GenericItemList DisclaimerList { get; set; } = new GenericItemList();
    }
}
