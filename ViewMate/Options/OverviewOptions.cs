using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;

namespace ViewMate.Options
{
    /// <summary>
    /// 「观影助手」总览 tab（主页面）的内容模型：只有插件简介与三个功能
    /// 模块（拼音搜索 / 片头尾跳过 / 关于）的只读状态与入口提示，不含任何
    /// 可编辑字段。状态在页面构建时从 PluginConfiguration（XML）读取，
    /// 本模型不参与保存流程。
    /// </summary>
    public class OverviewOptions : EditableOptionsBase
    {
        public override string EditorTitle => "观影助手";

        public override string EditorDescription =>
            "观影助手为 Emby 提供中文媒体增强：拼音与中文子串搜索，以及片头片尾跳过、漏集补打。";

        public GenericItemList ModuleStatusList { get; set; } = new GenericItemList();

        public void Initialize()
        {
            var config = Plugin.Instance?.Configuration as PluginConfiguration ?? new PluginConfiguration();

            ModuleStatusList.Clear();

            // 只读状态 + 入口提示；这里不复制「关于」tab 的版本号、链接与许可内容，
            // 避免同一份信息两处维护。
            ModuleStatusList.Add(new GenericListItem
            {
                PrimaryText = config.EnablePinyinSearch ? "拼音搜索：已启用" : "拼音搜索：已禁用",
                SecondaryText = "配置入口：上方「拼音搜索」标签页",
                Icon = IconNames.search,
                IconMode = ItemListIconMode.SmallRegular,
            });

            ModuleStatusList.Add(new GenericListItem
            {
                PrimaryText = config.EnableIntroSkip ? "片头尾跳过：已启用" : "片头尾跳过：已禁用",
                SecondaryText = "配置入口：上方「片头尾跳过」标签页",
                Icon = IconNames.skip_next,
                IconMode = ItemListIconMode.SmallRegular,
            });

            ModuleStatusList.Add(new GenericListItem
            {
                PrimaryText = config.EnableVersionCheck ? "关于：版本检查已启用" : "关于：版本检查已禁用",
                SecondaryText = "配置入口：上方「关于」标签页",
                Icon = IconNames.info,
                IconMode = ItemListIconMode.SmallRegular,
            });
        }
    }
}
