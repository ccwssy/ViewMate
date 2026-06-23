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

namespace ViewMate.IntroSkip
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
        public long MinOpeningPlotDurationTicks { get; set; } = TimeSpan.FromSeconds(30).Ticks;
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
            // Include Pause for mobile tap-to-seek (mobile Emby Web sends Pause, not TimeUpdate)
            // Always track jumps regardless of existing markers, enabling auto-healing
            if ((e.EventName == ProgressEvent.TimeUpdate || e.EventName == ProgressEvent.Unpause || e.EventName == ProgressEvent.Pause)
                && !data.NoDetectionButReset)
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

            // ── track manual forward jumps (≥20s to avoid normal ~10s progress) ──
            var timeElapsed = (now - data.PreviousEventTime).TotalSeconds;
            var posDelta = TimeSpan.FromTicks(currentTicks - data.PreviousPositionTicks).TotalSeconds;
            if (posDelta >= 20 && !data.NoDetectionButReset)
            {
                data.LastBigJumpSourceTicks = data.PreviousPositionTicks;
                data.LastBigJumpTargetTicks = currentTicks;
                _logger.Info("[IntroSkip] Big jump tracked: {0:F0}s → {1:F0}s (elapsed={2:F1}s)",
                    TimeSpan.FromTicks(data.PreviousPositionTicks).TotalSeconds,
                    TimeSpan.FromTicks(currentTicks).TotalSeconds,
                    timeElapsed);
            }

            data.PreviousPositionTicks = currentTicks;
            data.PreviousEventTime = now;
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            if (!(e.Item is Episode episode) || !e.PlaybackPositionTicks.HasValue)
            {
                _logger.Info("[IntroSkip] OnPlaybackStopped skipped: type={0} pos={1} session={2}",
                    e.Item?.GetType().Name, e.PlaybackPositionTicks, e.PlaySessionId);
                return;
            }

            if (!_sessions.TryRemove(e.PlaySessionId, out var data))
            {
                _logger.Info("[IntroSkip] OnPlaybackStopped session {0} not found (sessions count={1})",
                    e.PlaySessionId, _sessions.Count);
                return;
            }

            var currentTicks = e.PlaybackPositionTicks.Value;
            var prevTicks = data.PreviousPositionTicks;
            var jumpForward = TimeSpan.FromTicks(currentTicks - prevTicks).TotalSeconds;
            var curSec = TimeSpan.FromTicks(currentTicks).TotalSeconds;
            var prevSec = TimeSpan.FromTicks(prevTicks).TotalSeconds;
            var maxIntroSec = TimeSpan.FromTicks(MaxIntroDurationTicks).TotalSeconds;
            var minOpeningSec = TimeSpan.FromTicks(MinOpeningPlotDurationTicks).TotalSeconds;

            _logger.Info("[IntroSkip] OnPlaybackStopped: pos={0:F0}s prev={1:F0}s jump={2:F0}s maxIntro={3:F0}s minOpen={4:F0}s",
                curSec, prevSec, jumpForward, maxIntroSec, minOpeningSec);

            // Detect intro from tracked big jump (covers mobile seek-then-resume scenarios)
            // Always run even with existing markers → enables auto-healing of wrong markers
            if (!data.NoDetectionButReset
                && data.LastBigJumpSourceTicks.HasValue && data.LastBigJumpTargetTicks.HasValue)
            {
                var jumpSrc = data.LastBigJumpSourceTicks.Value;
                var jumpTgt = data.LastBigJumpTargetTicks.Value;
                var jumpSrcSec = TimeSpan.FromTicks(jumpSrc).TotalSeconds;
                var jumpTgtSec = TimeSpan.FromTicks(jumpTgt).TotalSeconds;
                if (jumpSrcSec <= maxIntroSec)
                {
                    Plugin.ChapterMarkerApi.UpdateIntro(episode, jumpSrc, jumpTgt);
                    _logger.Info("[IntroSkip] Intro detected (from tracked jump): {0:F0}s → {1:F0}s (src={2:F0}s)",
                        jumpSrcSec, jumpTgtSec, jumpSrcSec);
                }
                else
                {
                    _logger.Info("[IntroSkip] Tracked jump ignored: src={0:F0}s exceeds maxIntro={1:F0}s",
                        jumpSrcSec, maxIntroSec);
                }
            }
            else if (!data.IntroEnd.HasValue)
            {
                _logger.Info("[IntroSkip] OnPlaybackStopped: no tracked jump available");
            }

            // Detect credits from stop position (requires RunTimeTicks)
            if (episode.RunTimeTicks.HasValue && !data.CreditsStart.HasValue && !data.NoDetectionButReset)
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
            var currentSeconds = TimeSpan.FromTicks(currentTicks).TotalSeconds;
            var previousSeconds = TimeSpan.FromTicks(data.PreviousPositionTicks).TotalSeconds;
            var elapsedSeconds = (now - data.PreviousEventTime).TotalSeconds;

            // Seek detection: position jumped forward ≥10 seconds in ≤3 real seconds
            // (3s threshold for mobile tap-to-seek; Web clients report every ~10s)
            var jumpForward = currentSeconds - previousSeconds;
            var isSeek = jumpForward >= 10 && elapsedSeconds >= 0.1 && elapsedSeconds <= 3.0;

            if (!isSeek)
            {
                // Reset jump tracking if user pauses for too long (not a seek)
                if (elapsedSeconds > 10)
                {
                    data.FirstJumpPositionTicks = null;
                    data.LastJumpPositionTicks = null;
                }
                return;
            }

            // Track the first and last seek positions
            if (!data.FirstJumpPositionTicks.HasValue)
            {
                // New jump sequence — keep the original source position
                data.FirstJumpPositionTicks = data.PreviousPositionTicks;
            }
            data.LastJumpPositionTicks = currentTicks;

            _logger.Debug("[IntroSkip] Seek detected: {0} → {1} (jump={2}s elapsed={3:F1}s)",
                new TimeSpan(data.PreviousPositionTicks).ToString(@"hh\:mm\:ss"),
                new TimeSpan(currentTicks).ToString(@"hh\:mm\:ss"),
                jumpForward, elapsedSeconds);

            // Analyse: if end of jump within MaxIntro → it's the intro
            if (data.FirstJumpPositionTicks.HasValue && data.LastJumpPositionTicks.HasValue)
            {
                var introStart = data.FirstJumpPositionTicks.Value;
                var introEnd = data.LastJumpPositionTicks.Value;
                var introDurationSeconds = TimeSpan.FromTicks(introEnd - introStart).TotalSeconds;

                if (introDurationSeconds > 5
                    && TimeSpan.FromTicks(introStart).TotalSeconds <= TimeSpan.FromTicks(MaxIntroDurationTicks).TotalSeconds)
                {
                    Plugin.ChapterMarkerApi.UpdateIntro(episode, introStart, introEnd);
                    data.IntroStart = Plugin.ChapterMarkerApi.GetIntroStart(episode);
                    data.IntroEnd = Plugin.ChapterMarkerApi.GetIntroEnd(episode);
                    _logger.Info("[IntroSkip] Intro detected: {0} – {1} (dur={2:F0}s)",
                        new TimeSpan(introStart).ToString(@"hh\:mm\:ss\.fff"),
                        new TimeSpan(introEnd).ToString(@"hh\:mm\:ss\.fff"),
                        introDurationSeconds);
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
