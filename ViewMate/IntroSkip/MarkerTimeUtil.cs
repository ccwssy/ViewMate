using System;

namespace ViewMate.IntroSkip
{
    /// <summary>
    /// 片头/片尾时间文本与 ticks 的互转（标记管理界面与校验共用）。
    /// </summary>
    internal static class MarkerTimeUtil
    {
        private const long TicksPerSecond = 10000000L;

        /// <summary>ticks → "hh:mm:ss"；不足 1 小时输出 "mm:ss"。</summary>
        internal static string Format(long ticks)
        {
            var totalSeconds = ticks / TicksPerSecond;
            if (totalSeconds < 0)
            {
                totalSeconds = 0;
            }

            var hours = totalSeconds / 3600;
            var minutes = (totalSeconds % 3600) / 60;
            var seconds = totalSeconds % 60;
            return hours > 0
                ? string.Format("{0:00}:{1:00}:{2:00}", hours, minutes, seconds)
                : string.Format("{0:00}:{1:00}", minutes, seconds);
        }

        /// <summary>
        /// ticks → 定长 "hh:mm:ss"（弹窗输入框专用；摘要等展示仍用短格式 <see cref="Format"/>）。
        /// 长度恒为 8，与输入框 MaxLength 一致。
        /// </summary>
        internal static string FormatHms(long ticks)
        {
            var totalSeconds = ticks / TicksPerSecond;
            if (totalSeconds < 0)
            {
                totalSeconds = 0;
            }

            var hours = totalSeconds / 3600;
            var minutes = (totalSeconds % 3600) / 60;
            var seconds = totalSeconds % 60;
            return string.Format("{0:00}:{1:00}:{2:00}", hours, minutes, seconds);
        }

        /// <summary>
        /// 解析 "mm:ss" / "hh:mm:ss" / 纯秒数（分钟数允许 &gt; 59，容错优先）。
        /// 空串、含非数字字符、超过 3 段、数值溢出都返回 false。
        /// </summary>
        internal static bool TryParse(string text, out long ticks)
        {
            ticks = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var parts = text.Trim().Split(':');
            if (parts.Length == 0 || parts.Length > 3)
            {
                return false;
            }

            long total = 0;
            foreach (var part in parts)
            {
                if (part.Length == 0)
                {
                    return false;
                }

                foreach (var ch in part)
                {
                    if (ch < '0' || ch > '9')
                    {
                        return false;
                    }
                }

                if (!long.TryParse(part, out var value) || value > 100000L)
                {
                    return false;
                }

                total = total * 60 + value;
            }

            ticks = total * TicksPerSecond;
            return true;
        }
    }
}
