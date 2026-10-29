using System;

namespace ViewMate.Community
{
    /// <summary>社区库来源标识（缓存键前缀 / 日志 / 状态行使用）。</summary>
    internal static class CommunitySources
    {
        internal const string TheIntroDb = "theintrodb";
        internal const string IntroDb = "introdb";
    }

    /// <summary>
    /// 一次社区查询的结果，同时是磁盘缓存条目。
    /// 时间字段一律 Ticks（1 tick = 100ns），与 Chapters3.StartPositionTicks 同单位；
    /// 社区接口的毫秒值统一乘 10000 后存入。
    /// </summary>
    internal class CommunityLookupResult
    {
        /// <summary>是否取到至少一个可用片段。</summary>
        public bool Found { get; set; }

        /// <summary>被社区限流（429）：下次仍可重试，不算失败。</summary>
        public bool RateLimited { get; set; }

        /// <summary>未命中/失败原因（写日志用，不面向用户）。</summary>
        public string Reason { get; set; }

        public string Source { get; set; }

        public string CacheKey { get; set; }

        public long? IntroStartTicks { get; set; }

        public long? IntroEndTicks { get; set; }

        public long? CreditsStartTicks { get; set; }

        // ── 提交侧定位信息：未命中时也会带上，便于手动触发共享 ──
        public int? TmdbId { get; set; }

        public int? TvdbId { get; set; }

        public string ImdbId { get; set; }

        public bool IsMovie { get; set; }

        public int? Season { get; set; }

        public int? Episode { get; set; }

        public long RuntimeTicks { get; set; }

        /// <summary>查询时刻（本地时间字符串，缓存新鲜度判断用 mtime，这里仅供日志/报告）。</summary>
        public string At { get; set; }
    }

    /// <summary>item → 社区库定位参数。</summary>
    internal class CommunityIdentity
    {
        public string Source { get; set; }

        public bool IsMovie { get; set; }

        public int? TmdbId { get; set; }

        public int? TvdbId { get; set; }

        public string ImdbId { get; set; }

        public int? Season { get; set; }

        public int? Episode { get; set; }

        /// <summary>
        /// 集序（v4 的 <c>mapping</c> 参数）：只取 <c>"dvd"</c> / <c>"absolute"</c> 两种；
        /// null = default 序（aired），此时不传 mapping。
        /// </summary>
        public string DisplayOrder { get; set; }

        /// <summary>
        /// 集自身在 TVDB 的 id（v4 的 <c>tvdb_episode_id</c> 参数，用于钉住具体集）；
        /// 与所属剧的 tvdb id 相同或缺失时为 null。
        /// </summary>
        public int? TvdbEpisodeId { get; set; }

        public long RuntimeTicks { get; set; }

        /// <summary>日志用的人类可读标识（剧名 SxxExx）。</summary>
        public string Title { get; set; }

        /// <summary>缓存键：{site}_{id 类型}:{值}_{movie|tv}_s{季}_e{集}。</summary>
        public string CacheKey { get; set; }

        public CommunityIdentity Clone()
        {
            return (CommunityIdentity)MemberwiseClone();
        }
    }
}
