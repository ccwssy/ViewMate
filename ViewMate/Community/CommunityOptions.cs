using System;

namespace ViewMate.Community
{
    /// <summary>
    /// 社区数据三开关与两把 key 的统一读取口。
    /// 值以 PluginConfiguration（XML，常驻内存）为准 —— 与 IntroSkip / Pinyin 的运行开关同一套模式，
    /// 这样元数据 provider 在任意线程都能廉价读到最新值，不必反复读 JSON 文件。
    /// </summary>
    internal static class CommunityOptions
    {
        internal static PluginConfiguration Config => Plugin.Instance?.Configuration as PluginConfiguration;

        /// <summary>总开关：关 = 查询与共享全部静默。</summary>
        internal static bool DataEnabled => Config?.EnableCommunityData == true;

        /// <summary>「自动补打」（获取侧）：总开关 + 本开关都开才会查询。</summary>
        internal static bool FetchEnabled => DataEnabled && Config?.EnableCommunityFetch == true;

        /// <summary>「共享本机检测结果」（提交侧）。</summary>
        internal static bool ShareEnabled => DataEnabled && Config?.EnableCommunityShare == true;

        /// <summary>TheIntroDB key（可为空：无 key 低额度）。</summary>
        internal static string TheIntroDbKey => (Config?.TheIntroDbApiKey ?? string.Empty).Trim();

        /// <summary>IntroDB key（可为空：查询免 key，仅提交需要）。</summary>
        internal static string IntroDbKey => (Config?.IntroDbApiKey ?? string.Empty).Trim();

        /// <summary>状态行摘要用的 key 展示（绝不回显明文）。</summary>
        internal static string DescribeKey(string key) => string.IsNullOrWhiteSpace(key) ? "未配置" : "已配置";
    }
}
