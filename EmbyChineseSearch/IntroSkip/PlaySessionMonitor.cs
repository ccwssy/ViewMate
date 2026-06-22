using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace EmbyChineseSearch.IntroSkip
{
    /// <summary>
    /// Monitors user playback behaviour to detect intro/credits boundaries
    /// by watching for manual seek jumps. When a pattern is recognised,
    /// writes Chapter markers via ChapterMarkerApi.
    ///
    /// Two detection modes:
    ///   1. Auto-detect (default) — watches seek-forward behaviour
    ///   2. Manual-teach (NoDetectionButReset) — user pause-unpause at boundary
    /// </summary>
    public class PlaySessionMonitor : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ISessionManager _sessionManager;
        private readonly ILogger _logger;

        private readonly TimeSpan _updateInterval = TimeSpan.FromSeconds(10);
        private readonly ConcurrentDictionary<string, PlaySessionData> _sessions
            = new ConcurrentDictionary<string, PlaySessionData>();

        // ── config overrides (could be moved to PluginOptions) ──
        public long MaxIntroDurationTicks { get; set; } = TimeSpan.FromSeconds(150).Ticks;
        public long MinOpeningPlotDurationTicks { get; set; } = TimeSpan.FromSeconds(60).Ticks;
        public long MaxCreditsDurationTicks { get; set; } = TimeSpan.FromSeconds(360).Ticks;

        /// <summary>When true, all TV libraries are in scope.</summary>
        public bool AllLibrariesEnabled { get; set; } = true;

        /// <summary>Substring match on client name — empty means all clients.</summary>
        public string ClientFilter { get; set; } = "";

        public PlaySessionMonitor(ILibraryManager libraryManager, ISessionManager sessionManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _sessionManager = sessionManager;
            _logger = logger;
        }

        public void Start()
        {
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _logger.Info("[IntroSkip] PlaySessionMonitor started");
        }

        public void Dispose()
        {
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _sessions.Clear();
            _logger.Info("[IntroSkip] PlaySessionMonitor stopped");
        }

        // ── event handlers ──

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            if (!(e.Item is Episode episode) || !e.PlaybackPositionTicks.HasValue)
                return;

            if (!IsClientInScope(e.ClientName))
            {
                _logger.Debug("[IntroSkip] Client {0} not in scope, skipping", e.ClientName);
                return;
            }

            _sessions.TryRemove(e.PlaySessionId, out _);

            var data = new PlaySessionData(episode)
            {
                PlaybackStartTicks = e.PlaybackPositionTicks.Value,
                PreviousPositionTicks = e.PlaybackPositionTicks.Value,
                PreviousEventTime = DateTime.UtcNow,
                MaxIntroDurationTicks = MaxIntroDurationTicks,
                MaxCreditsDurationTicks = MaxCreditsDurationTicks,
                MinOpeningPlotDurationTicks = MinOpeningPlotDurationTicks,
            };
            _sessions[e.PlaySessionId] = data;

            _logger.Info("[IntroSkip] Playback started: {0} pos={1} client={2}",
                episode.Name, new TimeSpan(data.PlaybackStartTicks).ToString(@"hh\:mm\:ss\.fff"), e.ClientName);
        }

        private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            if (!(e.Item is Episode) || !e.PlaybackPositionTicks.HasValue || e.PlaybackPositionTicks.Value == 0)
                return;

            if (!_sessions.TryGetValue(e.PlaySessionId, out var data))
                return;

            var currentTicks = e.PlaybackPositionTicks.Value;
            var now = DateTime.UtcNow;
            var episode = (Episode)e.Item;

            // ── detect seek-jump (manual skip forward) ──
            if (e.EventName == ProgressEvent.TimeUpdate && !data.IntroEnd.HasValue && !data.NoDetectionButReset)
            {
                DetectJump(episode, e.Session, data, currentTicks, now);
            }

            // ── detect manual pause-unpause → credits (user teaching) ──
            if (e.EventName == ProgressEvent.Unpause && data.LastPauseEventTime.HasValue && episode.RunTimeTicks.HasValue)
            {
                var pauseDuration = (now - data.LastPauseEventTime.Value).TotalMilliseconds;
                if (pauseDuration > 500 && pauseDuration < 5000)
                {
                    // User paused near end → likely credits boundary
                    var nearEnd = episode.RunTimeTicks.Value - MaxCreditsDurationTicks;
                    if (!data.CreditsStart.HasValue && currentTicks > nearEnd)
                    {
                        var creditsDuration = episode.RunTimeTicks.Value - currentTicks;
                        if (creditsDuration > 0 && creditsDuration <= MaxCreditsDurationTicks)
                        {
                            Plugin.ChapterMarkerApi.UpdateCredits(episode, creditsDuration);
                            data.CreditsStart = Plugin.ChapterMarkerApi.GetCreditsStart(episode);
                        }
                    }

                    // User paused near beginning → teach intro boundary (NoDetectionButReset mode)
                    if (data.NoDetectionButReset && !data.IntroStart.HasValue && currentTicks < MaxIntroDurationTicks)
                    {
                        Plugin.ChapterMarkerApi.UpdateIntro(episode, 0, currentTicks);
                        data.IntroStart = Plugin.ChapterMarkerApi.GetIntroStart(episode);
                        data.IntroEnd = Plugin.ChapterMarkerApi.GetIntroEnd(episode);
                    }
                }
            }

            // ── track pause / rate-change timestamps ──
            if (e.EventName == ProgressEvent.Pause)
                data.LastPauseEventTime = now;
            if (e.EventName == ProgressEvent.PlaybackRateChange)
                data.LastPlaybackRateChangeEventTime = now;

            data.PreviousPositionTicks = currentTicks;
            data.PreviousEventTime = now;
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            if (!(e.Item is Episode episode) || !e.PlaybackPositionTicks.HasValue || !episode.RunTimeTicks.HasValue)
                return;

            if (!_sessions.TryRemove(e.PlaySessionId, out var data))
                return;

            // Detect credits from stop position (user stopped near end → credits already passed)
            if (!data.CreditsStart.HasValue && !data.NoDetectionButReset)
            {
                var nearEnd = episode.RunTimeTicks.Value - MaxCreditsDurationTicks;
                if (e.PlaybackPositionTicks.Value > nearEnd)
                {
                    var creditsDuration = episode.RunTimeTicks.Value - e.PlaybackPositionTicks.Value;
                    if (creditsDuration > 0 && creditsDuration <= MaxCreditsDurationTicks)
                    {
                        Plugin.ChapterMarkerApi.UpdateCredits(episode, creditsDuration);
                    }
                }
            }
        }

        // ── jump detection core ──

        private void DetectJump(Episode episode, SessionInfo session, PlaySessionData data,
            long currentTicks, DateTime now)
        {
            var elapsed = (now - data.PreviousEventTime).TotalSeconds;
            var moved = TimeSpan.FromTicks(currentTicks - data.PreviousPositionTicks).TotalSeconds;

            // Must be a forward jump (not normal playback)
            if (moved <= 0) return;

            // Calculate playback-speed-adjusted expected movement
            var normalPlayRate = Math.Abs(data.PreviousPositionTicks - data.PlaybackStartTicks)
                / Math.Max(1, (now - data.PreviousEventTime.AddSeconds(-1)).TotalSeconds);

            // A jump is when movement exceeds 3x normal playback rate with short elapsed time
            if (elapsed > 0 && moved / Math.Max(1, elapsed) > normalPlayRate * 3 && moved > 5)
            {
                if (!data.FirstJumpPositionTicks.HasValue)
                {
                    data.FirstJumpPositionTicks = data.PreviousPositionTicks;
                }
                data.LastJumpPositionTicks = currentTicks;
            }

            // Analyse jump pair to determine intro boundaries
            if (data.FirstJumpPositionTicks.HasValue && data.LastJumpPositionTicks.HasValue)
            {
                var introStart = data.FirstJumpPositionTicks.Value;
                var introEnd = data.LastJumpPositionTicks.Value;
                var introDuration = TimeSpan.FromTicks(introEnd - introStart).TotalSeconds;

                // Validate: intro should be within limits and near start of episode
                if (introDuration > 0
                    && introEnd <= MaxIntroDurationTicks
                    && introStart >= MinOpeningPlotDurationTicks)
                {
                    Plugin.ChapterMarkerApi.UpdateIntro(episode, introStart, introEnd);
                    data.IntroStart = Plugin.ChapterMarkerApi.GetIntroStart(episode);
                    data.IntroEnd = Plugin.ChapterMarkerApi.GetIntroEnd(episode);
                    _logger.Info("[IntroSkip] Intro detected: {0} – {1} (dur={2}s)",
                        new TimeSpan(introStart).ToString(@"hh\:mm\:ss\.fff"),
                        new TimeSpan(introEnd).ToString(@"hh\:mm\:ss\.fff"),
                        introDuration);

                    // Reset jump tracking for potential credits detection later
                    data.FirstJumpPositionTicks = null;
                    data.LastJumpPositionTicks = null;
                }
            }
        }

        // ── scope helpers ──

        private bool IsClientInScope(string clientName)
        {
            if (string.IsNullOrEmpty(ClientFilter)) return true;
            return clientName != null && clientName.Contains(ClientFilter, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsLibraryInScope(BaseItem item)
        {
            if (AllLibrariesEnabled) return item is Episode;
            // Future: library path filtering
            return item is Episode;
        }
    }
}
