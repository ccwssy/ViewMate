using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace ViewMate.Community
{
    /// <summary>
    /// 内嵌章节名映射：把媒体文件内嵌章节里名为 intro / credits 之类的章节
    /// 映射成片头/片尾标记，Name 后缀 <c>#VMCH</c>（与社区值的 <c>#VMDB</c>、手动/本地值的
    /// <c>#ECS</c> 各自区分）。词表参考 honue/MediaInfoKeeper 的 EmbeddedChapterMarkerMap，
    /// 并按要求做「英文大小写不敏感 + 中文 + 日语」一体化识别
    /// （MIK 原表另含德/韩，一并保留，纯增量不影响匹配语义）。
    /// </summary>
    internal static class EmbeddedChapterMap
    {
        internal const string Suffix = "#VMCH";

        internal static readonly HashSet<string> IntroNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "intro", "opening", "op", "opening credits",
            "片头", "片头曲",
            "オープニング",
            "vorspann",
            "오프닝",
        };

        internal static readonly HashSet<string> CreditsNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "credits", "end credits", "outro", "ending", "ed",
            "片尾", "片尾曲", "演职员表",
            "エンディング", "スタッフロール",
            "abspann",
            "엔딩", "크레딧",
        };

        internal enum Kind
        {
            None,
            Intro,
            Credits,
        }

        /// <summary>章节名 → 类型（trim + 大小写不敏感；空名不算）。</summary>
        internal static Kind Classify(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Kind.None;
            }

            var trimmed = name.Trim();
            if (IntroNames.Contains(trimmed))
            {
                return Kind.Intro;
            }

            return CreditsNames.Contains(trimmed) ? Kind.Credits : Kind.None;
        }

        /// <summary>
        /// 从 Emby 探测出的章节列表里挑出可映射的条目，返回待追加的标记行。
        /// IntroEnd 取「同集内下一个章节的起点」（MIK 的次级策略）；没有下一章时退回
        /// start + 默认片头时长（不超过片长），保证片头成对、写入前校验能过。
        /// </summary>
        internal static List<ChapterInfo> BuildMarkers(
            IReadOnlyList<ChapterInfo> source,
            long runtimeTicks,
            int defaultIntroSeconds)
        {
            var markers = new List<ChapterInfo>();
            if (source == null || source.Count == 0)
            {
                return markers;
            }

            var ordered = source
                .Where(c => c != null && c.StartPositionTicks >= 0)
                .OrderBy(c => c.StartPositionTicks)
                .ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                var kind = Classify(ordered[i].Name);
                if (kind == Kind.None)
                {
                    continue;
                }

                var start = ordered[i].StartPositionTicks;

                if (kind == Kind.Intro)
                {
                    var next = i + 1 < ordered.Count ? ordered[i + 1].StartPositionTicks : -1;
                    var end = next > start
                        ? next
                        : start + defaultIntroSeconds * 10000000L;

                    if (runtimeTicks > 0 && end > runtimeTicks)
                    {
                        end = runtimeTicks;
                    }

                    if (end <= start)
                    {
                        continue;
                    }

                    markers.Add(new ChapterInfo
                    {
                        Name = MarkerType.IntroStart + Suffix,
                        MarkerType = MarkerType.IntroStart,
                        StartPositionTicks = start,
                    });
                    markers.Add(new ChapterInfo
                    {
                        Name = MarkerType.IntroEnd + Suffix,
                        MarkerType = MarkerType.IntroEnd,
                        StartPositionTicks = end,
                    });
                }
                else
                {
                    if (runtimeTicks > 0 && start >= runtimeTicks)
                    {
                        continue;
                    }

                    markers.Add(new ChapterInfo
                    {
                        Name = MarkerType.CreditsStart + Suffix,
                        MarkerType = MarkerType.CreditsStart,
                        StartPositionTicks = start,
                    });
                }
            }

            return markers;
        }

        /// <summary>报告/日志用：当前生效的两张词表。</summary>
        internal static string DescribeVocabulary()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "intro=[{0}] credits=[{1}]",
                string.Join(" / ", IntroNames),
                string.Join(" / ", CreditsNames));
        }
    }
}
