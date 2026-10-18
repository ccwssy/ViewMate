using MediaBrowser.Model.Plugins.UI.Views;

namespace ViewMate.Options.View
{
    /// <summary>
    /// 「单集标记编辑弹窗」的宿主抽象。
    /// 现役 <see cref="IntroMarkerDialogView"/> 保存/清除成功后要回到「列表」，
    /// 而列表现在有两个容器：页面（旧路径）与弹窗 1（新路径）。
    /// 通过本接口把「回到哪里」从具体类型里解耦 —— 弹窗内部的命令链、
    /// 校验、回执文案、锁定/整季语义一字未改。
    /// </summary>
    internal interface IMarkerHostView
    {
        /// <summary>宿主视图实例本身（取消/关闭时返回它）。</summary>
        IPluginUIView HostView { get; }

        /// <summary>操作成功后重建的宿主视图（列表刷新 + 状态提示）；notice 为 null 时只做刷新。</summary>
        IPluginUIView CreateMarkersRefreshedView(string notice);
    }
}
