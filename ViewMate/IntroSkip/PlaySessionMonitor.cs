using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using System;
using System.Collections.Concurrent;
using System.Threading;
using ViewMate.Common;

namespace ViewMate.IntroSkip
{
    /// <summary>
    /// 监视用户的播放行为，通过捕捉手动 seek 跳跃
    /// 来识别片头/片尾边界。一旦识别出规律，
    /// 就经 ChapterMarkerApi 写入章节标记。
    ///
    /// 检测模式：自动检测（默认）—— 观察前跳（seek-forward）行为。
    /// </summary>
    public class PlaySessionMonitor : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ISessionManager _sessionManager;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<string, PlaySessionData> _sessions
            = new ConcurrentDictionary<string, PlaySessionData>();

        private readonly object _configLock = new object();
        private int _disposed;
        private bool IsDisposed => Interlocked.CompareExchange(ref _disposed, 0, 0) == 1;

        // ── 配置覆盖值（经 _configLock 保证线程安全） ──

        private long _maxIntroDurationTicks = TimeSpan.FromSeconds(IntroSkipDefaults.MaxIntroDurationSeconds).Ticks;
        private long _maxCreditsDurationTicks = TimeSpan.FromSeconds(IntroSkipDefaults.MaxCreditsDurationSeconds).Ticks;
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
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
            if (disposing)
            {
                _sessionManager.PlaybackStart -= OnPlaybackStart;
                _sessionManager.PlaybackProgress -= OnPlaybackProgress;
                _sessionManager.PlaybackStopped -= OnPlaybackStopped;
                _sessions.Clear();
            }
            _logger.Info("[IntroSkip] PlaySessionMonitor stopped");
        }

        // ── 事件处理器 ──

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

            var data = new PlaySessionData(episode)
            {
                PlaybackStartTicks = e.PlaybackPositionTicks.Value,
                PreviousPositionTicks = e.PlaybackPositionTicks.Value,
                PreviousEventTime = DateTime.UtcNow,
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

            // ── 检测 seek 跳跃（手动向前跳过） ──
            // 把 Pause 也算进来，以支持移动端点按跳转（移动端 Emby Web 发的是 Pause 而非 TimeUpdate）
            // 无论是否已有标记都持续跟踪跳跃，以支持自动修复
            if (e.EventName == ProgressEvent.TimeUpdate || e.EventName == ProgressEvent.Unpause || e.EventName == ProgressEvent.Pause)
            {
                DetectJump(episode, e.Session, data, currentTicks, now);
            }

            // ── 由手动暂停-继续推断片尾（用户示教） ──
            long maxCredits;
            lock (_configLock)
            {
                maxCredits = _maxCreditsDurationTicks;
            }

            if (e.EventName == ProgressEvent.Unpause && data.LastPauseEventTime.HasValue && episode.RunTimeTicks.HasValue)
            {
                var pauseDuration = (now - data.LastPauseEventTime.Value).TotalMilliseconds;
                if (pauseDuration > 500 && pauseDuration < 5000)
                {
                    // 用户在临近结尾处暂停 → 很可能是片尾边界
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
                }
            }

            // ── 记录暂停/倍速变化的时间戳 ──
            if (e.EventName == ProgressEvent.Pause)
                data.LastPauseEventTime = now;

            // ── 跟踪手动前跳（≥20 秒，以排除正常约 10 秒的进度上报） ──
            var timeElapsed = (now - data.PreviousEventTime).TotalSeconds;
            var posDelta = TimeSpan.FromTicks(currentTicks - data.PreviousPositionTicks).TotalSeconds;
            if (posDelta >= 20)
            {
                data.LastBigJumpSourceTicks = data.PreviousPositionTicks;
                data.LastBigJumpTargetTicks = currentTicks;
                _logger.Info($"[IntroSkip] Big jump tracked: {TimeSpan.FromTicks(data.PreviousPositionTicks).TotalSeconds:F0}s → {TimeSpan.FromTicks(currentTicks).TotalSeconds:F0}s (elapsed={timeElapsed:F1}s)");

                // ── 由临近结尾的大跨度跳跃推断片尾 ──
                if (!data.CreditsStart.HasValue && episode.RunTimeTicks.HasValue)
                {
                    long maxCreditsBigJump;
                    lock (_configLock) { maxCreditsBigJump = _maxCreditsDurationTicks; }
                    var nearEndFromPrev = episode.RunTimeTicks.Value - data.PreviousPositionTicks;
                    if (nearEndFromPrev > 0 && nearEndFromPrev <= maxCreditsBigJump)
                    {
                        Plugin.ChapterMarkerApi.UpdateCredits(episode, nearEndFromPrev);
                        data.CreditsStart = Plugin.ChapterMarkerApi.GetCreditsStart(episode);
                        _logger.Info($"[IntroSkip] Credits detected from big jump: src={TimeSpan.FromTicks(data.PreviousPositionTicks).TotalSeconds:F0}s (creditsDur={TimeSpan.FromTicks(nearEndFromPrev).TotalSeconds:F0}s)");
                    }
                }
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

            // 由 seek 跟踪推断片头（DetectJump 会累积多次点按的快进）
            // FirstJumpPositionTicks = 首次 seek 的起点（首次 seek 之后不再被覆盖）
            // FirstJumpTargetTicks = 首次 seek 的落点（不再被覆盖 —— 用户实际开始观看的位置）
            // LastJumpPositionTicks = 末次 seek 的落点（序列中每次 seek 都会更新）
            // LastBigJumpSourceTicks / LastBigJumpTargetTicks 是旧的单次跳跃场景的兜底
            long? jumpSrc = data.FirstJumpPositionTicks ?? data.LastBigJumpSourceTicks;
            long? jumpTgt = data.FirstJumpTargetTicks ?? data.LastJumpPositionTicks ?? data.LastBigJumpTargetTicks;

            if (jumpSrc.HasValue && jumpTgt.HasValue)
            {
                // Yamby（以及多数移动端）上报进度的间隔很长（约 20 秒）。
                // 检测到的跳跃起点往往是「最后一次上报的位置」，而不是真正的
                // 跳跃前位置。如果用户从 0 秒开始（PlaybackStartTicks=0），且跳跃起点
                // 落在 maxIntro 之内，那么片头确实从 0 秒开始，而不是 Yamby 最终
                // 上报的那个中间位置。
                if (data.PlaybackStartTicks == 0 && jumpSrc.Value > 0
                    && jumpSrc.Value <= maxIntro)
                {
                    jumpSrc = 0;
                }

                // 确定片头终点：客户端上报及时时用 FirstJumpTargetTicks
                // （未上报缺口 ≤10 秒）；Yamby 会把事件合并，此时退回用 skipDistance。
                // Hills 上报频繁（缺口约 5 秒）→ FirstJumpTargetTicks=45s ✅
                // Yamby 上报稀疏（缺口约 21 秒）→ skipDistance=39s≈40s ✅
                if (data.PlaybackStartTicks == 0
                    && data.FirstJumpPositionTicks.HasValue && data.LastJumpPositionTicks.HasValue)
                {
                    var unreportedGap = data.FirstJumpPositionTicks.Value - data.PlaybackStartTicks;
                    var skipDistance = data.LastJumpPositionTicks.Value - data.FirstJumpPositionTicks.Value;
                    if (skipDistance > 0 && skipDistance <= maxIntro)
                    {
                        var reliableTarget = data.FirstJumpTargetTicks ?? (skipDistance);
                        jumpTgt = unreportedGap > TimeSpan.FromSeconds(10).Ticks
                            ? skipDistance    // Yamby：使用从 0 起算的跳跃距离
                            : reliableTarget; // Hills：使用首次快进的落点
                    }
                }

                // 用户可能第一次快进过头再往回拖（快进到 60 秒，拖回 40 秒，再快进）。
                // 这种情况下 LastJumpPositionTicks 更接近实际开始观看的位置。
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
            if (!data.IntroEnd.HasValue && !data.FirstJumpPositionTicks.HasValue && !data.LastBigJumpSourceTicks.HasValue)
            {
                _logger.Debug("[IntroSkip] OnPlaybackStopped: no tracked jump available");
            }

            // 由停止位置推断片尾（需要 RunTimeTicks）
            if (episode.RunTimeTicks.HasValue && !data.CreditsStart.HasValue)
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

        // ── 跳跃检测核心 ──

        private void DetectJump(Episode episode, SessionInfo session, PlaySessionData data,
            long currentTicks, DateTime now)
        {
            var currentSeconds = TimeSpan.FromTicks(currentTicks).TotalSeconds;
            var previousSeconds = TimeSpan.FromTicks(data.PreviousPositionTicks).TotalSeconds;
            var elapsedSeconds = (now - data.PreviousEventTime).TotalSeconds;

            // seek 判定：位置在 ≤3 秒真实时间内前跳 ≥10 秒
            // （3 秒阈值针对移动端点按跳转；Web 客户端约每 10 秒上报一次）
            var jumpForward = currentSeconds - previousSeconds;
            var isSeek = jumpForward >= 10 && elapsedSeconds >= 0.1 && elapsedSeconds <= 3.0;

            if (!isSeek)
            {
                // 只重置「末次跳跃」跟踪（不动 FirstJumpPositionTicks）
                // FirstJumpPositionTicks 标记本会话中第一个 seek 序列的原点，
                // 它必须能在非 seek 事件中存活（例如 Yamby 会把一次大位移的
                // Pause 作为单个事件发来，而那不是真正的 seek）。
                // 在这里清掉它，下一次快进事件就会丢失真正的片头起点。
                if (elapsedSeconds > 10)
                    data.LastJumpPositionTicks = null;
                return;
            }

            // 跟踪首次与末次 seek 位置
            if (!data.FirstJumpPositionTicks.HasValue)
            {
                // 新的跳跃序列 —— 保留最初的起点与首个落点
                data.FirstJumpPositionTicks = data.PreviousPositionTicks;
                data.FirstJumpTargetTicks = currentTicks;
            }
            data.LastJumpPositionTicks = currentTicks;

            _logger.Debug($"[IntroSkip] Seek detected: {new TimeSpan(data.PreviousPositionTicks).ToString(@"hh\:mm\:ss")} → {new TimeSpan(currentTicks).ToString(@"hh\:mm\:ss")} (jump={jumpForward}s elapsed={elapsedSeconds:F1}s)");

            // 分析：若跳跃终点落在 MaxIntro 之内 → 判定为片头
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
            // ── 由 seek 推断片尾 ──
            if (!data.CreditsStart.HasValue && episode.RunTimeTicks.HasValue)
            {
                long maxCredits;
                lock (_configLock) { maxCredits = _maxCreditsDurationTicks; }
                var nearEndFromSrc = episode.RunTimeTicks.Value - data.PreviousPositionTicks;
                if (nearEndFromSrc > 0 && nearEndFromSrc <= maxCredits)
                {
                    Plugin.ChapterMarkerApi.UpdateCredits(episode, nearEndFromSrc);
                    data.CreditsStart = Plugin.ChapterMarkerApi.GetCreditsStart(episode);
                    _logger.Info($"[IntroSkip] Credits detected from seek: src={TimeSpan.FromTicks(data.PreviousPositionTicks).TotalSeconds:F0}s (creditsDur={TimeSpan.FromTicks(nearEndFromSrc).TotalSeconds:F0}s)");
                }
            }
        }

        // ── 作用域辅助方法 ──

        private bool IsClientInScope(string clientName)
        {
            string filter;
            lock (_configLock) { filter = _clientFilter ?? ""; }
            if (string.IsNullOrEmpty(filter)) return true;
            return clientName != null && clientName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
