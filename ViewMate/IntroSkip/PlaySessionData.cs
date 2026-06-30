using MediaBrowser.Controller.Entities;
using System;

namespace ViewMate.IntroSkip
{
    /// <summary>
    /// Mutable per-session playback tracking data.
    /// Fields are mutated in-place by PlaySessionMonitor during playback.
    /// </summary>
    public class PlaySessionData
    {
        public PlaySessionData(BaseItem item)
        {
            IntroStart = Plugin.ChapterMarkerApi.GetIntroStart(item);
            IntroEnd = Plugin.ChapterMarkerApi.GetIntroEnd(item);
            CreditsStart = Plugin.ChapterMarkerApi.GetCreditsStart(item);
        }

        // ── existing marker positions (read once at session start) ──
        public long? IntroStart { get; set; }
        public long? IntroEnd { get; set; }
        public long? CreditsStart { get; set; }

        // ── playback tracking ──
        public long PlaybackStartTicks { get; set; } = 0;
        public long PreviousPositionTicks { get; set; } = 0;
        public DateTime PreviousEventTime { get; set; } = DateTime.MinValue;

        // ── cumulative seek tracking ──
        public long? FirstJumpPositionTicks { get; set; }
        public long? FirstJumpTargetTicks { get; set; }
        public long? LastJumpPositionTicks { get; set; }

        // ── config snapshot (copied from PlaySessionMonitor at session start) ──
        public long MaxIntroDurationTicks { get; set; } = TimeSpan.FromSeconds(150).Ticks;
        public long MaxCreditsDurationTicks { get; set; } = TimeSpan.FromSeconds(180).Ticks;

        // ── big-jump tracking (≥20s forward jumps, used by OnPlaybackStopped) ──
        public long? LastBigJumpSourceTicks { get; set; }
        public long? LastBigJumpTargetTicks { get; set; }

        // ── event timestamps ──
        public DateTime? LastPauseEventTime { get; set; }
        public DateTime? LastPlaybackRateChangeEventTime { get; set; }

        /// <summary>
        /// When true: no auto-detection, but user pause-unpause near intro boundary
        /// still writes marker (manual teaching mode).
        /// </summary>
        public bool NoDetectionButReset { get; set; } = false;
    }
}
