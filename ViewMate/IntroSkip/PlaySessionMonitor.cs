using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using System;
using System.Collections.Concurrent;

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

        private readonly ConcurrentDictionary<string, PlaySessionData> _sessions
            = new ConcurrentDictionary<string, PlaySessionData>();

        private readonly object _configLock = new object();
        private bool _disposed;

        // ── config overrides (thread-safe via _configLock) ──

        private long _maxIntroDurationTicks = TimeSpan.FromSeconds(150).Ticks;
        private long _maxCreditsDurationTicks = TimeSpan.FromSeconds(180).Ticks;
        private bool _allLibrariesEnabled = true;
        private string _clientFilter = "";

        public long MaxIntroDurationTicks
        {
            get { lock (_configLock) return _maxIntroDurationTicks; }
            set { lock (_configLock) _maxIntroDurationTicks = value; }
        }

        public long MaxCreditsDurationTicks
        {
            get { lock (_configLock) return _maxCreditsDurationTicks; }
            set { lock (_configLock) _maxCreditsDurationTicks = value; }
        }

        /// <summary>When true, all TV libraries are in scope.</summary>
        public bool AllLibrariesEnabled
        {
            get { lock (_configLock) return _allLibrariesEnabled; }
            set { lock (_configLock) _allLibrariesEnabled = value; }
        }

        /// <summary>Substring match on client name — empty means all clients.</summary>
        public string ClientFilter
        {
            get { lock (_configLock) return _clientFilter ?? ""; }
            set { lock (_configLock) _clientFilter = value ?? ""; }
        }

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
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                _sessionManager.PlaybackStart -= OnPlaybackStart;
                _sessionManager.PlaybackProgress -= OnPlaybackProgress;
                _sessionManager.PlaybackStopped -= OnPlaybackStopped;
                _sessions.Clear();
            }
            _disposed = true;
            _logger.Info("[IntroSkip] PlaySessionMonitor stopped");
        }

        // ── event handlers ──

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            if (!(e.Item is Episode episode) || !e.PlaybackPositionTicks.HasValue)
                return;

            if (!IsClientInScope(e.ClientName))
            {
                _logger.Debug($"[IntroSkip] Client {e.ClientName} not in scope, skipping");
                return;
            }

            _sessions.TryRemove(e.PlaySessionId, out _);

            long maxIntro, maxCredits;
            lock (_configLock)
            {
                maxIntro = _maxIntroDurationTicks;
                maxCredits = _maxCreditsDurationTicks;
            }

            var data = new PlaySessionData(episode)
            {
                PlaybackStartTicks = e.PlaybackPositionTicks.Value,
                PreviousPositionTicks = e.PlaybackPositionTicks.Value,
                PreviousEventTime = DateTime.UtcNow,
                MaxIntroDurationTicks = maxIntro,
                MaxCreditsDurationTicks = maxCredits,
            };
            _sessions[e.PlaySessionId] = data;

            _logger.Info($"[IntroSkip] Playback started: {episode.Name} pos={new TimeSpan(data.PlaybackStartTicks).ToString(@"hh\:mm\:ss\.fff")} client={e.ClientName}");
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
            long maxCredits;
            long maxIntro;
            lock (_configLock)
            {
                maxCredits = _maxCreditsDurationTicks;
                maxIntro = _maxIntroDurationTicks;
            }

            if (e.EventName == ProgressEvent.Unpause && data.LastPauseEventTime.HasValue && episode.RunTimeTicks.HasValue)
            {
                var pauseDuration = (now - data.LastPauseEventTime.Value).TotalMilliseconds;
                if (pauseDuration > 500 && pauseDuration < 5000)
                {
                    // User paused near end → likely credits boundary
                    var nearEnd = episode.RunTimeTicks.Value - maxCredits;
                    if (!data.CreditsStart.HasValue && currentTicks > nearEnd)
                    {
                        var creditsDuration = episode.RunTimeTicks.Value - currentTicks;
                        if (creditsDuration > 0 && creditsDuration <= maxCredits)
                        {
                            Plugin.ChapterMarkerApi.UpdateCredits(episode, creditsDuration);
                            data.CreditsStart = Plugin.ChapterMarkerApi.GetCreditsStart(episode);
                        }
                    }

                    // User paused near beginning → teach intro boundary (NoDetectionButReset mode)
                    if (data.NoDetectionButReset && !data.IntroStart.HasValue && currentTicks < maxIntro)
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
                _logger.Info($"[IntroSkip] Big jump tracked: {TimeSpan.FromTicks(data.PreviousPositionTicks).TotalSeconds:F0}s → {TimeSpan.FromTicks(currentTicks).TotalSeconds:F0}s (elapsed={timeElapsed:F1}s)");
            }

            data.PreviousPositionTicks = currentTicks;
            data.PreviousEventTime = now;
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            if (!(e.Item is Episode episode) || !e.PlaybackPositionTicks.HasValue)
            {
                _logger.Info($"[IntroSkip] OnPlaybackStopped skipped: type={e.Item?.GetType().Name} pos={e.PlaybackPositionTicks} session={e.PlaySessionId}");
                return;
            }

            if (!_sessions.TryRemove(e.PlaySessionId, out var data))
            {
                _logger.Info($"[IntroSkip] OnPlaybackStopped session {e.PlaySessionId} not found (sessions count={_sessions.Count})");
                return;
            }

            var currentTicks = e.PlaybackPositionTicks.Value;
            var prevTicks = data.PreviousPositionTicks;
            var jumpForward = TimeSpan.FromTicks(currentTicks - prevTicks).TotalSeconds;
            var curSec = TimeSpan.FromTicks(currentTicks).TotalSeconds;
            var prevSec = TimeSpan.FromTicks(prevTicks).TotalSeconds;

            long maxIntro;
            long maxCredits;
            lock (_configLock)
            {
                maxIntro = _maxIntroDurationTicks;
                maxCredits = _maxCreditsDurationTicks;
            }
            var maxIntroSec = TimeSpan.FromTicks(maxIntro).TotalSeconds;

            _logger.Info($"[IntroSkip] OnPlaybackStopped: pos={curSec:F0}s prev={prevSec:F0}s jump={jumpForward:F0}s maxIntro={maxIntroSec:F0}s");

            // Detect intro from seek tracking (DetectJump tracks cumulative multi-tap fast-forward)
            // FirstJumpPositionTicks = first seek source (never overwritten after first seek)
            // FirstJumpTargetTicks = first seek target (never overwritten — where user actually started watching)
            // LastJumpPositionTicks = last seek target (updates on each seek in the sequence)
            // LastBigJumpSourceTicks / LastBigJumpTargetTicks are fallbacks for older single-jump scenario
            if (!data.NoDetectionButReset)
            {
                long? jumpSrc = data.FirstJumpPositionTicks ?? data.LastBigJumpSourceTicks;
                long? jumpTgt = data.FirstJumpTargetTicks ?? data.LastJumpPositionTicks ?? data.LastBigJumpTargetTicks;

                if (jumpSrc.HasValue && jumpTgt.HasValue)
                {
                    // Yamby (and most mobile clients) report progress infrequently (~20s intervals).
                    // The detected jump source is often the LAST REPORTED position, not the actual
                    // pre-jump position. If the user started from 0s (PlaybackStartTicks=0) and the
                    // jump source is within maxIntro, the intro genuinely starts at 0, not at some
                    // intermediate position Yamby finally reported.
                    if (data.PlaybackStartTicks == 0 && jumpSrc.Value > 0
                        && jumpSrc.Value <= maxIntro)
                    {
                        jumpSrc = 0;
                    }

                    // Determine intro end: use FirstJumpTargetTicks when client reports timely
                    // (unreported gap ≤10s), fall back to skipDistance when Yamby combines events.
                    // Hills reports frequently (~5s gap) → FirstJumpTargetTicks=45s ✅
                    // Yamby reports rarely (~21s gap) → skipDistance=39s≈40s ✅
                    if (data.PlaybackStartTicks == 0
                        && data.FirstJumpPositionTicks.HasValue && data.LastJumpPositionTicks.HasValue)
                    {
                        var unreportedGap = data.FirstJumpPositionTicks.Value - data.PlaybackStartTicks;
                        var skipDistance = data.LastJumpPositionTicks.Value - data.FirstJumpPositionTicks.Value;
                        if (skipDistance > 0 && skipDistance <= maxIntro)
                        {
                            var reliableTarget = data.FirstJumpTargetTicks ?? (skipDistance);
                            jumpTgt = unreportedGap > TimeSpan.FromSeconds(10).Ticks
                                ? skipDistance    // Yamby: use skip distance from 0
                                : reliableTarget; // Hills: use first FF target
                        }
                    }

                    // User may overshoot first FF and correct backward (FF to 60s, scrub back to 40s, FF again).
                    // In that case LastJumpPositionTicks is closer to the actual watching start.
                    if (data.FirstJumpTargetTicks.HasValue && data.LastJumpPositionTicks.HasValue
                        && data.LastJumpPositionTicks.Value < data.FirstJumpTargetTicks.Value)
                    {
                        jumpTgt = data.LastJumpPositionTicks.Value;
                    }

                    var jumpSrcSec = TimeSpan.FromTicks(jumpSrc.Value).TotalSeconds;
                    var jumpTgtSec = TimeSpan.FromTicks(jumpTgt.Value).TotalSeconds;
                    if (jumpSrcSec <= maxIntroSec)
                    {
                        Plugin.ChapterMarkerApi.UpdateIntro(episode, jumpSrc.Value, jumpTgt.Value);
                        _logger.Info($"[IntroSkip] Intro detected: {jumpSrcSec:F0}s → {jumpTgtSec:F0}s (src={jumpSrcSec:F0}s)");
                    }
                    else
                    {
                        _logger.Info($"[IntroSkip] Tracked jump ignored: src={jumpSrcSec:F0}s exceeds maxIntro={maxIntroSec:F0}s");
                    }
                }
            }
            if (!data.IntroEnd.HasValue && !data.FirstJumpPositionTicks.HasValue && !data.LastBigJumpSourceTicks.HasValue)
            {
                _logger.Debug("[IntroSkip] OnPlaybackStopped: no tracked jump available");
            }

            // Detect credits from stop position (requires RunTimeTicks)
            if (episode.RunTimeTicks.HasValue && !data.CreditsStart.HasValue && !data.NoDetectionButReset)
            {
                var nearEnd = episode.RunTimeTicks.Value - maxCredits;
                if (e.PlaybackPositionTicks.Value > nearEnd)
                {
                    var creditsDuration = episode.RunTimeTicks.Value - e.PlaybackPositionTicks.Value;
                    if (creditsDuration > 0 && creditsDuration <= maxCredits)
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
                // Reset last-jump tracking only (not FirstJumpPositionTicks)
                // FirstJumpPositionTicks marks the origin of the very first seek sequence
                // in this session and must survive non-seek events (e.g. Yamby sends Pause
                // with a large position delta as a single event, which is not a real seek).
                // Clearing it here would lose the true intro origin on the next FF event.
                if (elapsedSeconds > 10)
                    data.LastJumpPositionTicks = null;
                return;
            }

            // Track the first and last seek positions
            if (!data.FirstJumpPositionTicks.HasValue)
            {
                // New jump sequence — keep the original source position and first target
                data.FirstJumpPositionTicks = data.PreviousPositionTicks;
                data.FirstJumpTargetTicks = currentTicks;
            }
            data.LastJumpPositionTicks = currentTicks;

            _logger.Debug($"[IntroSkip] Seek detected: {new TimeSpan(data.PreviousPositionTicks).ToString(@"hh\:mm\:ss")} → {new TimeSpan(currentTicks).ToString(@"hh\:mm\:ss")} (jump={jumpForward}s elapsed={elapsedSeconds:F1}s)");

            // Analyse: if end of jump within MaxIntro → it's the intro
            if (data.FirstJumpPositionTicks.HasValue && data.LastJumpPositionTicks.HasValue)
            {
                var introStart = data.FirstJumpPositionTicks.Value;
                var introEnd = data.LastJumpPositionTicks.Value;
                var introDurationSeconds = TimeSpan.FromTicks(introEnd - introStart).TotalSeconds;
                long maxIntro;
                lock (_configLock) { maxIntro = _maxIntroDurationTicks; }
                var maxIntroSec = TimeSpan.FromTicks(maxIntro).TotalSeconds;

                if (introDurationSeconds > 5
                    && TimeSpan.FromTicks(introStart).TotalSeconds <= maxIntroSec)
                {
                    Plugin.ChapterMarkerApi.UpdateIntro(episode, introStart, introEnd);
                    data.IntroStart = Plugin.ChapterMarkerApi.GetIntroStart(episode);
                    data.IntroEnd = Plugin.ChapterMarkerApi.GetIntroEnd(episode);
                    _logger.Info($"[IntroSkip] Intro detected: {new TimeSpan(introStart).ToString(@"hh\:mm\:ss\.fff")} – {new TimeSpan(introEnd).ToString(@"hh\:mm\:ss\.fff")} (dur={introDurationSeconds:F0}s)");
                }
            }
        }

        // ── scope helpers ──

        private bool IsClientInScope(string clientName)
        {
            string filter;
            lock (_configLock) { filter = _clientFilter ?? ""; }
            if (string.IsNullOrEmpty(filter)) return true;
            return clientName != null && clientName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public bool IsLibraryInScope(BaseItem item)
        {
            bool allEnabled;
            lock (_configLock) { allEnabled = _allLibrariesEnabled; }
            if (allEnabled) return item is Episode;
            return item is Episode;
        }
    }
}
