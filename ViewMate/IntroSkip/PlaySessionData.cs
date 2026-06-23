using MediaBrowser.Controller.Entities;
using System;

namespace ViewMate.IntroSkip
{
    public class PlaySessionData
    {
        public PlaySessionData(BaseItem item)
        {
            IntroStart = Plugin.ChapterMarkerApi.GetIntroStart(item);
            IntroEnd = Plugin.ChapterMarkerApi.GetIntroEnd(item);
            CreditsStart = Plugin.ChapterMarkerApi.GetCreditsStart(item);
        }

        public long? IntroStart { get; set; }
        public long? IntroEnd { get; set; }
        public long? CreditsStart { get; set; }

        public long PlaybackStartTicks { get; set; } = 0;
        public long PreviousPositionTicks { get; set; } = 0;
        public DateTime PreviousEventTime { get; set; } = DateTime.MinValue;

        public long? FirstJumpPositionTicks { get; set; }
        public long? LastJumpPositionTicks { get; set; }

        public long MaxIntroDurationTicks { get; set; } = TimeSpan.FromSeconds(150).Ticks;
        public long MaxCreditsDurationTicks { get; set; } = TimeSpan.FromSeconds(360).Ticks;
        public long MinOpeningPlotDurationTicks { get; set; } = TimeSpan.FromSeconds(30).Ticks;

        /// <summary>Last significant forward jump (≥10s) regardless of elapsed time.</summary>
        public long? LastBigJumpSourceTicks { get; set; }
        public long? LastBigJumpTargetTicks { get; set; }

        public DateTime? LastPauseEventTime { get; set; }
        public DateTime? LastPlaybackRateChangeEventTime { get; set; }

        /// <summary>
        /// When true: no auto-detection, but user pause-unpause near intro boundary
        /// still writes marker (manual teaching mode).
        /// </summary>
        public bool NoDetectionButReset { get; set; } = false;
    }
}
