using MediaBrowser.Controller.Entities;
using System;

namespace ViewMate.IntroSkip
{
    /// <summary>
    /// 各会话可变的播放跟踪数据。
    /// 播放期间由 PlaySessionMonitor 就地修改这些字段。
    /// </summary>
    public class PlaySessionData
    {
        public PlaySessionData(BaseItem item)
        {
            IntroStart = Plugin.ChapterMarkerApi.GetIntroStart(item);
            IntroEnd = Plugin.ChapterMarkerApi.GetIntroEnd(item);
            CreditsStart = Plugin.ChapterMarkerApi.GetCreditsStart(item);
        }

        // ── 已存在的标记位置（会话开始时读取一次） ──
        public long? IntroStart { get; set; }
        public long? IntroEnd { get; set; }
        public long? CreditsStart { get; set; }

        // ── 播放跟踪 ──
        public long PlaybackStartTicks { get; set; } = 0;
        public long PreviousPositionTicks { get; set; } = 0;
        public DateTime PreviousEventTime { get; set; } = DateTime.MinValue;

        // ── 累计跳转跟踪 ──
        public long? FirstJumpPositionTicks { get; set; }
        public long? FirstJumpTargetTicks { get; set; }
        public long? LastJumpPositionTicks { get; set; }

        // ── 大跨度跳转跟踪（≥20 秒的前跳，供 OnPlaybackStopped 使用） ──
        public long? LastBigJumpSourceTicks { get; set; }
        public long? LastBigJumpTargetTicks { get; set; }

        // ── 事件时间戳 ──
        public DateTime? LastPauseEventTime { get; set; }
    }
}
