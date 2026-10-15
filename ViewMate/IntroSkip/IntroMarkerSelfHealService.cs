using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using ViewMate.Common;

namespace ViewMate.IntroSkip
{
    /// <summary>
    /// 手动标记自愈服务（T12）。
    ///
    /// 背景：Emby 的缩略图/章节图片刷新流程会把**媒体文件探测出来的章节**
    /// （不含本项目的标记）交给 <c>IItemRepository.SaveChapters</c>，而该方法是
    /// **全量替换**语义（先 delete 再 insert）—— 任何一次"章节图片有变化"的
    /// 库刷新都会覆盖该集的 Chapters3，插件写入的 <c>#ECS</c> 手动标记随之消失。
    ///
    /// 本服务按覆盖表（viewmate-intro-overrides.json）周期校验：
    ///   · <c>locked</c> 的集：缺标记或值漂移 → 用覆盖表记录补回（片头/片尾）；
    ///   · <c>suppressed</c> 的集：又出现 <c>#ECS</c> → 再次删除（保持"已清除"语义）。
    /// 只在"确实不一致"时写库，正常轮零写入、零 Info 日志。
    ///
    /// T15 §2.6（F6）追加："现存超片长标记"清理 —— 每轮把**所有含 #ECS 行的集**纳入检查：
    ///   · 覆盖表 locked 集：只 Warn 不删（用户手动接管的数据绝不自动动，§2.6.2 红线）；
    ///   · 覆盖表 suppressed 集：又出现超片长 #ECS = 被写回 → 按墓碑逻辑清除（允许删）；
    ///   · 其余（自动态）集：删除值超出 <c>RunTimeTicks</c> 的单条 #ECS 标记（Info 一行）；
    ///   · <c>RunTimeTicks</c> 为 NULL/≤0 → 跳过该集（无法比较，绝不删）。
    /// 删除计数计入单轮写入上限（与覆盖表补写共用 <see cref="MaxEntriesPerRun"/>）。
    ///
    /// 日志降噪（T13）：无法写入的冲突（如记录值超出片长）每轮都会重现，
    /// 上一版会对**同一条**冲突每 10 分钟打一行同样的 Warn（一天约 144 条纯噪音）。
    /// 现在按「冲突键」（<c>集 ItemId | 标记类型 | 原因类别</c>）去重：
    /// 进程内首次遇到某键记 Warn（原文案不变），同键的后续轮次降 Debug（默认级别看不到）；
    /// 一轮完整遍历后，本轮未再出现的键从集合移除 —— 冲突消失（记录被清掉 / 值变合法 /
    /// 集被删除）后，同一集将来再次出现同一冲突会**重新 Warn 一次**。
    /// 集合只在进程内，不持久化（重启后允许再 Warn 一次）。
    /// 补写成功（<c>restored manual markers</c>）与墓碑恢复（<c>re-suppressed markers</c>）
    /// 是真实状态变化，**每次都记 Info**，不参与降噪。
    ///
    /// 隔离约束（§2.5，硬要求；T15 §2.6.4 起清理阶段按施工书要求做全库剧集枚举）：
    ///   · 独立轻量定时器 —— 不使用也不等待拼音链路的 FtsScanScheduler；
    ///   · 读写一律经 ChapterMarkerApi → IItemRepository，绝不新建 SQLite 连接；
    ///   · 覆盖表遍历上限 <see cref="MaxEntriesPerRun"/> 条；清理阶段枚举全库可播放剧集
    ///     （数量级 ≤ 千级，一轮内可承受），但**删除数**受同一个写入上限约束；
    ///   · Interlocked 防重入；整轮 try/catch，异常绝不冒泡到定时器之外；
    ///   · 启动路径上只注册定时器，不做任何同步 I/O。
    /// </summary>
    public sealed class IntroMarkerSelfHealService : IDisposable
    {
        /// <summary>
        /// 首轮延迟。取 150 秒：既避开 Emby 启动高峰与 60 秒处的启动回填，
        /// 也让触发点落在 5 分钟刻度的 +2:30 相位（2:30 / 12:30 / 22:30 …），
        /// 与拼音周期扫描的整点相位（0 / 5 / 10 …）永不落在同一分钟。
        /// </summary>
        internal static readonly TimeSpan FirstRunDelay = TimeSpan.FromSeconds(150);

        /// <summary>周期：10 分钟（拼音链路是 5 分钟整点，两者相位错开）。</summary>
        internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

        /// <summary>
        /// 单轮上限：覆盖表最多遍历这么多条，**写库（含 §2.6 的删除）总数也不超过它**
        /// （正常情况远小于此值）。
        /// </summary>
        internal const int MaxEntriesPerRun = 50;

        private readonly ChapterMarkerApi _chapterMarkerApi;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        private Timer _timer;

        /// <summary>0 = 空闲，1 = 本轮进行中（Interlocked 防重入，不排队堆积）。</summary>
        private int _running;

        private int _disposed;

        /// <summary>
        /// 「覆盖表条目指向的集已不存在」在同一进程内只 Warn 一次，
        /// 避免每 10 分钟刷一行同样的日志。
        /// </summary>
        private readonly HashSet<long> _warnedMissingEntries = new HashSet<long>();
        private readonly object _warnedMissingLock = new object();

        /// <summary>
        /// T13：已 Warn 过的「冲突键」（<c>集 ItemId | 标记类型 | 原因类别</c>），进程内、不持久化。
        /// 首次遇到某键 → Warn；同键再次遇到 → Debug（默认级别看不到）。
        /// 一轮**完整**遍历后，本轮未再出现的键会被移除（冲突消失即重置，
        /// 将来同一集再现同一冲突会重新 Warn 一次）。
        /// </summary>
        private readonly HashSet<string> _warnedConflictKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _warnedConflictLock = new object();

        public IntroMarkerSelfHealService(ChapterMarkerApi chapterMarkerApi, ILibraryManager libraryManager, ILogger logger)
        {
            _chapterMarkerApi = chapterMarkerApi;
            _libraryManager = libraryManager;
            _logger = logger;
        }

        private bool IsDisposed => Interlocked.CompareExchange(ref _disposed, 0, 0) == 1;

        /// <summary>
        /// 启动独立定时器：启动 +150 秒首轮，其后每 10 分钟一轮。
        /// 这里只注册定时器，不做任何同步 I/O（启动路径零阻塞）。
        /// </summary>
        public void Start()
        {
            if (IsDisposed) return;
            if (_timer != null) return;

            _timer = new Timer(_ => OnTick(), null, FirstRunDelay, Interval);
            Info("Self-heal scheduled: first run in {0}s, then every {1} min (offset from pinyin 5-min ticks)",
                (long)FirstRunDelay.TotalSeconds, (long)Interval.TotalMinutes);
        }

        // ── 定时器回调：唯一的外抛防线 ──

        private void OnTick()
        {
            if (IsDisposed) return;

            // 自愈自身不可重入：上一轮未结束则直接跳过（记 Debug，不排队堆积）。
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                Debug("self-heal: previous round still running, skip this tick");
                return;
            }

            try
            {
                RunOnce();
            }
            catch (Exception ex)
            {
                // 绝不外抛：embyserver 的运行不能受本服务影响。
                Warn("self-heal round failed: {0}", ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        /// <summary>
        /// 跑一轮校验。返回处理（遍历）的条目数，仅供自证/排障使用。
        /// 每条独立 try/catch：单条失败只 Warn 并继续。
        /// </summary>
        internal int RunOnce()
        {
            // 每轮重新读覆盖表：外部改动（含手工编辑）立即生效。
            // 文件损坏时整轮跳过（只 Warn）——不做任何修补，也不清空共享缓存
            // （缓存保留上次成功加载的内容，设置页与保存路径不受影响）。
            if (!IntroMarkerOverrides.TryRefresh())
            {
                Warn("self-heal: overrides file unreadable, skipping this round (previous cache kept)");
                return 0;
            }

            var entries = IntroMarkerOverrides.Snapshot();
            var processed = 0;

            // T15 §2.6.4：本轮写库次数（覆盖表补写 + 超片长删除），与 MaxEntriesPerRun 共用上限。
            var writes = 0;

            // T13：本轮观察到的冲突键。只有当本轮把覆盖表**全部**条目都走完
            // （没被上限截断、没被释放中断、没有单条异常）时，才用它来重置降噪集合 ——
            // 否则"没看到"可能只是"没轮到"，误重置会让下轮又 Warn 一次。
            var seenConflicts = new HashSet<string>(StringComparer.Ordinal);
            var roundComplete = true;

            foreach (var kv in entries)
            {
                if (IsDisposed)
                {
                    roundComplete = false;
                    break;
                }

                if (processed >= MaxEntriesPerRun)
                {
                    Warn("self-heal: run capped at {0} entries, rest deferred to next round", MaxEntriesPerRun);
                    roundComplete = false;
                    break;
                }

                processed++;
                try
                {
                    writes += HealEntry(kv.Key, kv.Value, seenConflicts);
                }
                catch (Exception ex)
                {
                    // 该条没走完，冲突状态未知：本轮不做重置（保守，只推迟一轮）。
                    roundComplete = false;
                    Warn("self-heal: item {0} failed: {1}", kv.Key, ex.Message);
                }
            }

            // T15 §2.6（F6）：超片长 #ECS 清理。放在覆盖表遍历之后：
            // 锁定/墓碑集刚被上面处理过，这里的判定不会与补写打架。
            CleanOutOfRangeMarkers(seenConflicts, Math.Max(0, MaxEntriesPerRun - writes));

            if (roundComplete)
            {
                ResetVanishedConflicts(seenConflicts);
            }

            return processed;
        }

        // ── 单条处理 ──

        /// <summary>处理一条覆盖表记录，返回本轮实际写库次数（T15 §2.6.4 的写入额度用）。</summary>
        private int HealEntry(long itemId, IntroMarkerOverrides.EpisodeOverride entry, HashSet<string> seenConflicts)
        {
            if (entry == null) return 0;

            var item = ResolveEpisode(itemId);
            if (item == null)
            {
                WarnMissingItemOnce(itemId);
                return 0;
            }

            // 覆盖表写入侧保证 locked / suppressed 互斥；两者同时为真时以
            // "已清除"（suppressed）优先，与 IntroMarkerAdminService 的状态判定同序。
            if (entry.Suppressed)
            {
                return ReSuppress(item);
            }

            if (entry.Locked)
            {
                return RestoreLocked(item, entry, seenConflicts);
            }

            // T13：同一条配置不良的记录也只 Warn 一次（该键同样参与"消失即重置"）。
            WarnConflictOnce(seenConflicts, ConflictKey(item.InternalId, "Entry", "NotLockedNorSuppressed"),
                "self-heal: entry {0} has neither locked nor suppressed, skipped", itemId);
            return 0;
        }

        /// <summary>locked：按覆盖表补回缺失/漂移的片头与片尾标记（无缺失时不写库、不记日志）。</summary>
        private int RestoreLocked(Episode item, IntroMarkerOverrides.EpisodeOverride entry, HashSet<string> seenConflicts)
        {
            // 记录值与当前片长冲突 → 整条优雅跳过：不写库、不反复重试、
            // 更不改写覆盖表记录（那条记录归用户处置）。
            var conflict = DescribeRuntimeConflict(item, entry);
            if (conflict.HasValue)
            {
                var c = conflict.Value;
                // T13：同一条冲突只 Warn 一次（原文案含集名与两个片长数值，全部保留）。
                WarnConflictOnce(seenConflicts, ConflictKey(item.InternalId, c.MarkerKind, c.ReasonKind),
                    "self-heal: skipped {0} - {1}; 按当前片长无法写入，覆盖表记录保留且不再重试", item.Name, c.Description);
                return 0;
            }

            var before = _chapterMarkerApi.GetEcsMarkerValues(item);
            var restored = false;

            // 片头：覆盖表成对记录时才校验（单边记录无值可补，交给用户处置）。
            if (entry.StartTicks.HasValue && entry.EndTicks.HasValue
                && (before.IntroStart != entry.StartTicks.Value || before.IntroEnd != entry.EndTicks.Value))
            {
                var error = _chapterMarkerApi.UpdateIntroSingle(item, entry.StartTicks.Value, entry.EndTicks.Value);
                if (error != null)
                {
                    // T13：写入被拒（校验不通过）同样是确定性、每轮重现的冲突 → 只 Warn 一次。
                    WarnConflictOnce(seenConflicts, ConflictKey(item.InternalId, "Intro", "WriteRejected"),
                        "self-heal: failed to restore intro for {0}: {1}", item.Name, error);
                }
                else
                {
                    restored = true;
                }
            }

            // 片尾：覆盖表有记录且与库中不一致时补写。
            if (entry.CreditsTicks.HasValue && before.CreditsStart != entry.CreditsTicks.Value)
            {
                var error = _chapterMarkerApi.UpdateCreditsSingle(item, entry.CreditsTicks.Value);
                if (error != null)
                {
                    WarnConflictOnce(seenConflicts, ConflictKey(item.InternalId, "Credits", "WriteRejected"),
                        "self-heal: failed to restore credits for {0}: {1}", item.Name, error);
                }
                else
                {
                    restored = true;
                }
            }

            if (!restored)
            {
                // 无缺失：不写日志、不写库（幂等的关键）。
                return 0;
            }

            // 补写成功：日志反映落盘后的真实值（一行含集名与值）。
            var after = _chapterMarkerApi.GetEcsMarkerValues(item);
            Info("self-heal: restored manual markers for {0} (introStart={1} introEnd={2} credits={3})",
                item.Name, FormatTicks(after.IntroStart), FormatTicks(after.IntroEnd), FormatTicks(after.CreditsStart));
            return 1;
        }

        /// <summary>suppressed：库里又出现本项目标记时再次删除（无标记时是纯读操作）。</summary>
        private int ReSuppress(Episode item)
        {
            var markers = _chapterMarkerApi.GetEcsMarkerValues(item);
            if (!markers.IntroStart.HasValue && !markers.IntroEnd.HasValue && !markers.CreditsStart.HasValue)
            {
                // 已清除：零写入、零日志。
                return 0;
            }

            _chapterMarkerApi.ClearMarkers(item);
            Info("self-heal: re-suppressed markers for {0}", item.Name);
            return 1;
        }

        // ── T15 §2.6（F6）：现存超片长标记清理 ──

        /// <summary>
        /// 遍历库中所有可播放剧集清理「现存超片长」标记（只有含本项目 #ECS 的集才真正进入判定）：
        ///   · locked 集 → 只 Warn 不删（红线）；suppressed 集 → 按墓碑逻辑清除（被写回才可能走到）；
        ///   · 自动态集 → 删除超片长的单条 #ECS 标记（Info 一行）；
        ///   · <c>RunTimeTicks</c> 为 NULL/≤0 → 跳过（绝不删）。
        /// 返回本阶段写库次数（每集删除计 1 次），由调用方计入单轮写入上限。
        /// 单集失败只 Warn 并继续，绝不让清理阶段把整轮打挂。
        /// </summary>
        private int CleanOutOfRangeMarkers(HashSet<string> seenConflicts, int budget)
        {
            if (budget <= 0)
            {
                // 本轮写入额度已被覆盖表补写用完：清理推迟到下一轮（不静默 —— 记 Warn 便于排障）。
                Warn("self-heal: out-of-range cleanup deferred (write budget for this round already used)");
                return 0;
            }

            var writes = 0;

            foreach (var episode in AllEpisodes())
            {
                if (IsDisposed) break;

                if (writes >= budget)
                {
                    Warn("self-heal: out-of-range cleanup capped at {0} writes, rest deferred to next round", budget);
                    break;
                }

                try
                {
                    writes += CleanEpisodeMarkers(episode, seenConflicts);
                }
                catch (Exception ex)
                {
                    Warn("self-heal: out-of-range cleanup failed for {0}: {1}", episode?.Name, ex.Message);
                }
            }

            return writes;
        }

        /// <summary>单个剧集的超片长清理；返回写库次数（0 或 1）。</summary>
        private int CleanEpisodeMarkers(Episode item, HashSet<string> seenConflicts)
        {
            if (item == null) return 0;

            var markers = _chapterMarkerApi.GetEcsMarkerValues(item);
            if (!markers.IntroStart.HasValue && !markers.IntroEnd.HasValue && !markers.CreditsStart.HasValue)
            {
                // 没有本项目的标记：零写入、零日志。
                return 0;
            }

            // §2.6.3：片长未知（NULL / ≤0）→ 无法比较，整集跳过，绝不删。
            if (!item.RunTimeTicks.HasValue || item.RunTimeTicks.Value <= 0)
            {
                Debug("self-heal: skip out-of-range check for {0} (runtime unknown)", item.Name);
                return 0;
            }

            var runtime = item.RunTimeTicks.Value;
            var outOfRange = DescribeOutOfRangeMarkers(markers, runtime);
            if (outOfRange.Count == 0) return 0;

            var entry = IntroMarkerOverrides.Get(item.InternalId);

            if (entry != null && entry.Locked)
            {
                // §2.6.2 用户数据保护：locked 集只 Warn 不删、不写库、不重试（T13 降噪键语义）。
                foreach (var marker in outOfRange)
                {
                    WarnConflictOnce(seenConflicts, ConflictKey(item.InternalId, marker.MarkerKind, "OutOfRange"),
                        "self-heal: kept out-of-range {0} for {1} - {2}; locked 集只告警不删除（覆盖表记录与库中标记均保留）",
                        marker.MarkerKind, item.Name, marker.Description);
                }

                return 0;
            }

            if (entry != null && entry.Suppressed)
            {
                // §2.6.2：墓碑集又出现超片长 #ECS（被写回）→ 按既有墓碑逻辑整集清除（允许删）。
                _chapterMarkerApi.ClearMarkers(item);
                Info("self-heal: re-suppressed markers for {0} (out-of-range marker written back)", item.Name);
                return 1;
            }

            // 自动态集：删除值超出片长的单条 #ECS 标记（三类型各自判断）。
            var removed = _chapterMarkerApi.RemoveOutOfRangeEcsMarkers(item, runtime);
            if (removed.Count == 0) return 0;

            foreach (var marker in removed)
            {
                Info("self-heal: removed out-of-range {0} for {1}（{2:0}s > 片长 {3:0}s）",
                    marker.Type, item.Name, TicksToSeconds(marker.Ticks), TicksToSeconds(runtime));
            }

            return 1;
        }

        /// <summary>
        /// 列出超片长的标记（三类型各自判断，含可直接落日志的中文描述）。
        /// 判据用 <c>StartPositionTicks</c>：三个标记类型在本项目里都是"位置"语义
        /// （IntroEnd 也是 StartPositionTicks，见 ChapterMarkerApi.WriteIntro）。
        /// </summary>
        private static List<(string MarkerKind, string Description)> DescribeOutOfRangeMarkers(
            (long? IntroStart, long? IntroEnd, long? CreditsStart) markers, long runtime)
        {
            var list = new List<(string MarkerKind, string Description)>();

            if (markers.IntroStart.HasValue && markers.IntroStart.Value > runtime)
            {
                list.Add(("IntroStart", Describe("片头开始", markers.IntroStart.Value, runtime)));
            }

            if (markers.IntroEnd.HasValue && markers.IntroEnd.Value > runtime)
            {
                list.Add(("IntroEnd", Describe("片头结束", markers.IntroEnd.Value, runtime)));
            }

            if (markers.CreditsStart.HasValue && markers.CreditsStart.Value > runtime)
            {
                list.Add(("CreditsStart", Describe("片尾开始", markers.CreditsStart.Value, runtime)));
            }

            return list;
        }

        private static string Describe(string label, long ticks, long runtime)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} {1} ticks（{2:0}s）超出片长 {3} ticks（{4:0}s）",
                label, ticks, TicksToSeconds(ticks), runtime, TicksToSeconds(runtime));
        }

        /// <summary>库中全部可播放剧集（Video + 有路径），供 §2.6 清理逐集比对。</summary>
        private List<Episode> AllEpisodes()
        {
            try
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    Recursive = true,
                    MediaTypes = new[] { MediaType.Video },
                    HasPath = true,
                };

                return _libraryManager?.GetItemList(query)?.OfType<Episode>().ToList() ?? new List<Episode>();
            }
            catch (Exception ex)
            {
                Warn("self-heal: enumerate episodes failed: {0}", ex.Message);
                return new List<Episode>();
            }
        }

        // ── 辅助 ──

        private Episode ResolveEpisode(long itemId)
        {
            try
            {
                return _libraryManager?.GetItemById(itemId) as Episode;
            }
            catch (Exception ex)
            {
                Warn("self-heal: resolve item {0} failed: {1}", itemId, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 记录值超出片长时返回冲突描述；不冲突返回 null。
        /// <c>MarkerKind</c> = 标记类型（Intro / Credits），<c>ReasonKind</c> = 原因类别，
        /// <c>Description</c> = 可直接落日志的原文案（含两个片长数值），三者共同构成 T13 的冲突键。
        /// </summary>
        private static (string MarkerKind, string ReasonKind, string Description)? DescribeRuntimeConflict(
            Episode item, IntroMarkerOverrides.EpisodeOverride entry)
        {
            if (!item.RunTimeTicks.HasValue) return null;
            var runtime = item.RunTimeTicks.Value;

            if (entry.EndTicks.HasValue && entry.EndTicks.Value > runtime)
            {
                return ("Intro", "RuntimeExceeded", string.Format(CultureInfo.InvariantCulture,
                    "片头结束 {0} ticks（{1:0}s）超出片长 {2} ticks（{3:0}s）",
                    entry.EndTicks.Value, TicksToSeconds(entry.EndTicks.Value), runtime, TicksToSeconds(runtime)));
            }

            if (entry.StartTicks.HasValue && entry.StartTicks.Value > runtime)
            {
                return ("Intro", "RuntimeExceeded", string.Format(CultureInfo.InvariantCulture,
                    "片头开始 {0} ticks（{1:0}s）超出片长 {2} ticks（{3:0}s）",
                    entry.StartTicks.Value, TicksToSeconds(entry.StartTicks.Value), runtime, TicksToSeconds(runtime)));
            }

            if (entry.CreditsTicks.HasValue && entry.CreditsTicks.Value > runtime)
            {
                return ("Credits", "RuntimeExceeded", string.Format(CultureInfo.InvariantCulture,
                    "片尾开始 {0} ticks（{1:0}s）超出片长 {2} ticks（{3:0}s）",
                    entry.CreditsTicks.Value, TicksToSeconds(entry.CreditsTicks.Value), runtime, TicksToSeconds(runtime)));
            }

            return null;
        }

        /// <summary>T13 冲突键：<c>集 ItemId | 标记类型（Intro/Credits/Entry）| 原因类别</c>。</summary>
        private static string ConflictKey(long itemId, string markerKind, string reasonKind) =>
            string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}", itemId, markerKind, reasonKind);

        /// <summary>
        /// T13：同一条冲突只 Warn 一次 —— 首次遇到该键记 Warn（<paramref name="format"/> 原文案不变），
        /// 同一进程内再次遇到记 Debug（默认日志级别看不到，带「已抑制重复」标记便于排障）。
        /// <paramref name="seenConflicts"/> 记录本轮出现过的键，供轮末重置使用。
        /// </summary>
        private void WarnConflictOnce(HashSet<string> seenConflicts, string key, string format, params object[] args)
        {
            seenConflicts?.Add(key);

            bool firstTime;
            lock (_warnedConflictLock)
            {
                firstTime = _warnedConflictKeys.Add(key);
            }

            if (firstTime)
            {
                Warn(format, args);
                return;
            }

            try
            {
                Debug("self-heal: [已抑制重复] {0} | " + string.Format(CultureInfo.InvariantCulture, format, args), key);
            }
            catch (Exception)
            {
                Debug("self-heal: [已抑制重复] {0}", key);
            }
        }

        /// <summary>
        /// T13：一轮完整遍历后，把本轮未再出现的冲突键从集合移除 ——
        /// 冲突消失（记录被清掉、值变合法、集被删除）即重置，
        /// 将来同一集再次出现同一冲突会重新 Warn 一次。
        /// </summary>
        private void ResetVanishedConflicts(HashSet<string> seenConflicts)
        {
            lock (_warnedConflictLock)
            {
                _warnedConflictKeys.RemoveWhere(key => !seenConflicts.Contains(key));
            }
        }

        private static double TicksToSeconds(long ticks) => ticks / 10000000.0;

        private static string FormatTicks(long? ticks) =>
            ticks.HasValue ? ticks.Value.ToString(CultureInfo.InvariantCulture) : "-";

        private void WarnMissingItemOnce(long itemId)
        {
            lock (_warnedMissingLock)
            {
                if (!_warnedMissingEntries.Add(itemId)) return;
            }

            Warn("self-heal: entry {0} points to a missing media item, skipped (warned once per process)", itemId);
        }

        // ── 日志（全部吞掉日志自身的异常，绝不影响主流程） ──

        private void Info(string format, params object[] args)
        {
            try { _logger?.Info("[IntroMarker] " + format, args); } catch (Exception) { }
        }

        private void Warn(string format, params object[] args)
        {
            try { _logger?.Warn("[IntroMarker] " + format, args); } catch (Exception) { }
        }

        private void Debug(string format, params object[] args)
        {
            try { _logger?.Debug("[IntroMarker] " + format, args); } catch (Exception) { }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

            try
            {
                _timer?.Dispose();
            }
            catch (Exception)
            {
                // 释放失败不影响宿主。
            }

            _timer = null;
        }
    }
}
