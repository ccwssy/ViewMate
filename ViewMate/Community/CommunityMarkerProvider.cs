using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using ViewMate.Common;
using ViewMate.IntroSkip;

namespace ViewMate.Community
{
    /// <summary>
    /// 社区数据写入器（§B.2.5 + §B.4）：同一条 Emby 元数据刷新管线里的**后置** provider
    /// （<c>ICustomMetadataProvider</c>，在 ffprobe 之后、SaveItem/SaveChapters 之前被调用，
    /// 拿得到 BaseItem 与 LibraryOptions），负责：
    ///   1) 内嵌章节名映射 → 追加 <c>#VMCH</c> 标记（§B.4，纯 C# 接口，不用 Harmony）；
    ///   2) 社区查询 → 追加 <c>#VMDB</c> 标记（§B.2，三闸门 + 锁定集豁免）；
    ///   3) 把该集**已有的插件标记行**（#ECS / #VMDB / #VMCH）并回即将交给 SaveChapters 的列表，
    ///      避免 Emby 的「先 DELETE 再全量 INSERT」把插件标记抹掉（T12 自愈要 10 分钟才补回）。
    /// 只有真的要新增标记时才把 Chapters 交回去并返回 MetadataImport ——
    /// 没有任何可写内容时保持原样（返回 None），绝不额外触发 SaveChapters。
    /// </summary>
    internal sealed class CommunityMarkerProvider :
        ICustomMetadataProvider<Movie>,
        ICustomMetadataProvider<Episode>,
        IHasItemChangeWithItemResultMonitor
    {
        internal const string ProviderName = "ViewMate — 社区标记";

        /// <summary>单集单轮最多追加的标记行（防御性上限）。</summary>
        private const int MaxMarkersPerItem = 8;

        private static ILogger Logger => Plugin.Instance?.Logger;

        public string Name => ProviderName;

        public bool HasChanged(
            BaseMetadataResult itemResult,
            LibraryOptions libraryOptions,
            MetadataRefreshOptions options,
            IDirectoryService directoryService)
        {
            // 总开关关 → 完全不参与（省掉每次刷新的空转）。
            return CommunityOptions.DataEnabled;
        }

        public Task<ItemUpdateType> FetchAsync(
            MetadataResult<Movie> itemResult,
            MetadataRefreshOptions options,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken)
        {
            return ApplyAsync(itemResult, libraryOptions, cancellationToken);
        }

        public Task<ItemUpdateType> FetchAsync(
            MetadataResult<Episode> itemResult,
            MetadataRefreshOptions options,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken)
        {
            return ApplyAsync(itemResult, libraryOptions, cancellationToken);
        }

        private static async Task<ItemUpdateType> ApplyAsync<TItem>(
            MetadataResult<TItem> itemResult,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken)
            where TItem : BaseItem
        {
            try
            {
                var item = itemResult?.Item;
                if (item == null || item.InternalId <= 0 || !CommunityOptions.DataEnabled)
                {
                    return ItemUpdateType.None;
                }

                // 锁定/抑制的集永久豁免（§B.2.2 / §B.4）：既不查社区，也不写 #VMCH。
                var protectedItem = IntroMarkerOverrides.IsProtected(item.InternalId);

                var chapters = itemResult.Chapters ?? new List<ChapterInfo>();
                var changed = false;

                if (!protectedItem)
                {
                    var embedded = EmbeddedChapterMap.BuildMarkers(
                        chapters,
                        item.RunTimeTicks ?? 0,
                        IntroSkipDefaults.MaxIntroDurationSeconds);

                    var appended = 0;
                    foreach (var marker in embedded)
                    {
                        if (appended >= MaxMarkersPerItem)
                        {
                            break;
                        }

                        // 同名同类型的 #VMCH 已存在则跳过（幂等）。
                        if (chapters.Any(c => c.MarkerType == marker.MarkerType
                                              && string.Equals(c.Name, marker.Name, StringComparison.Ordinal)))
                        {
                            continue;
                        }

                        chapters.Add(marker);
                        appended++;
                    }

                    if (appended > 0)
                    {
                        changed = true;
                        Logger?.Info(
                            "[Community] 内嵌章节映射写入 {0} 行（#VMCH）：{1}",
                            appended,
                            item.Name);
                    }
                }

                if (await ApplyCommunityAsync(item, chapters, libraryOptions, cancellationToken).ConfigureAwait(false))
                {
                    changed = true;
                }

                // 已有插件标记行并回：只要 Emby 这次会走 SaveChapters（result.Chapters 非 null），
                // 就把该集现有的 #ECS / #VMDB / #VMCH 一起交回去 —— SaveChapters 是
                // 「DELETE 该 ItemId 全部行 + 全量 INSERT」，不并回就会把插件（含用户手摆）标记抹掉。
                if (itemResult.Chapters != null)
                {
                    if (MergeExistingPluginMarkers(item, chapters))
                    {
                        changed = true;
                    }
                }

                if (!changed)
                {
                    return ItemUpdateType.None;
                }

                chapters.Sort((a, b) => a.StartPositionTicks.CompareTo(b.StartPositionTicks));
                itemResult.Chapters = chapters;
                return ItemUpdateType.MetadataImport;
            }
            catch (Exception ex)
            {
                // Provider 异常绝不上抛：刷新流程必须走完。
                Logger?.Debug("[Community] provider 跳过（{0}）：{1}", typeof(TItem).Name, ex.Message);
                return ItemUpdateType.None;
            }
        }

        /// <summary>
        /// §B.2 查询侧：三闸门（Options 总开关+获取 / 库勾选 / 本地缺片段）+ 锁定集豁免。
        /// 返回是否真的写入了 #VMDB 行。
        /// </summary>
        private static async Task<bool> ApplyCommunityAsync(
            BaseItem item,
            List<ChapterInfo> chapters,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken)
        {
            if (!CommunityOptions.FetchEnabled)
            {
                return false;
            }

            if (IntroMarkerOverrides.IsProtected(item.InternalId))
            {
                return false;
            }

            var missing = CommunityIdentityFactory.GetMissingSegments(item);
            if (!missing.NeedIntro && !missing.NeedCredits)
            {
                return false;
            }

            // 闸门 2：库的「元数据下载器」勾选。两个源都没勾选 → 记一条 Info 便于诊断后直接返回。
            var enabledSources = new List<string>();
            foreach (var candidate in new[] { CommunitySources.TheIntroDb, CommunitySources.IntroDb })
            {
                if (IsFetcherEnabled(item, libraryOptions, candidate))
                {
                    enabledSources.Add(candidate);
                }
            }

            if (enabledSources.Count == 0)
            {
                var typeName = item.GetType().Name;
                var typeOptions = libraryOptions?.GetTypeOptions(typeName);
                Logger?.Info(
                    "[Community] 跳过社区查询（库的「元数据下载器」未勾选 ViewMate Provider）：{0}；诊断：typeName={1}，TypeOptions={2}，该类型 fetchers=[{3}]，全部 TypeOptions=[{4}]",
                    item.Name,
                    typeName,
                    libraryOptions?.TypeOptions?.Length ?? -1,
                    typeOptions == null ? "<null>" : string.Join(",", typeOptions.MetadataFetchers ?? new string[0]),
                    libraryOptions?.TypeOptions == null ? "<null>" : string.Join(" | ", libraryOptions.TypeOptions.Select(t => t?.Type ?? "<null>")));
                return false;
            }

            var written = false;
            foreach (var source in enabledSources)
            {

                var identity = item is Episode episode
                    ? CommunityIdentityFactory.ForEpisode(episode, source)
                    : CommunityIdentityFactory.ForMovie(item as Movie, source);

                if (identity == null)
                {
                    continue;
                }

                var apiKey = string.Equals(source, CommunitySources.IntroDb, StringComparison.Ordinal)
                    ? CommunityOptions.IntroDbKey
                    : CommunityOptions.TheIntroDbKey;

                var result = await CommunityApiClient.QueryAsync(identity, apiKey, Logger, cancellationToken)
                    .ConfigureAwait(false);

                if (result == null || !result.Found)
                {
                    continue;
                }

                // 写前逐段校验（§B.2.5 同 T15 口径）：start ≥ 0、intro 成对且 end > start、
                // credits start < 片长；任一不满足 → 该段丢弃、不写、Info 一条日志。
                if (missing.NeedIntro && result.IntroStartTicks.HasValue && result.IntroEndTicks.HasValue)
                {
                    var start = result.IntroStartTicks.Value;
                    var end = result.IntroEndTicks.Value;
                    if (start >= 0 && end > start)
                    {
                        RemoveOurs(chapters, MarkerType.IntroStart, ChapterMarkerApi.MarkerSuffix);
                        RemoveOurs(chapters, MarkerType.IntroEnd, ChapterMarkerApi.MarkerSuffix);
                        RemoveOurs(chapters, MarkerType.IntroStart, EmbeddedChapterMap.Suffix);
                        RemoveOurs(chapters, MarkerType.IntroEnd, EmbeddedChapterMap.Suffix);
                        chapters.Add(new ChapterInfo
                        {
                            Name = MarkerType.IntroStart + CommunityMarkerSuffix,
                            MarkerType = MarkerType.IntroStart,
                            StartPositionTicks = start,
                        });
                        chapters.Add(new ChapterInfo
                        {
                            Name = MarkerType.IntroEnd + CommunityMarkerSuffix,
                            MarkerType = MarkerType.IntroEnd,
                            StartPositionTicks = end,
                        });
                        written = true;
                        Logger?.Info("[Community] 写入社区片头（#VMDB，{0}）：{1}", source, identity.Title);
                    }
                    else
                    {
                        Logger?.Info("[Community] 丢弃越界片头段（{0}）：{1}", source, identity.Title);
                    }
                }

                if (missing.NeedCredits && result.CreditsStartTicks.HasValue)
                {
                    var credits = result.CreditsStartTicks.Value;
                    var runtime = item.RunTimeTicks ?? 0;
                    if (credits > 0 && (runtime <= 0 || credits < runtime))
                    {
                        RemoveOurs(chapters, MarkerType.CreditsStart, ChapterMarkerApi.MarkerSuffix);
                        RemoveOurs(chapters, MarkerType.CreditsStart, EmbeddedChapterMap.Suffix);
                        chapters.Add(new ChapterInfo
                        {
                            Name = MarkerType.CreditsStart + CommunityMarkerSuffix,
                            MarkerType = MarkerType.CreditsStart,
                            StartPositionTicks = credits,
                        });
                        written = true;
                        Logger?.Info("[Community] 写入社区片尾（#VMDB，{0}）：{1}", source, identity.Title);
                    }
                    else
                    {
                        Logger?.Info("[Community] 丢弃越界片尾段（{0}）：{1}", source, identity.Title);
                    }
                }

                // 一侧拿到就不再问下一家（避免两库值互相覆盖）。
                if (written)
                {
                    break;
                }
            }

            return written;
        }

        internal const string CommunityMarkerSuffix = "#VMDB";

        internal static readonly string[] PluginSuffixes =
        {
            ChapterMarkerApi.MarkerSuffix,
            CommunityMarkerSuffix,
            EmbeddedChapterMap.Suffix,
        };

        /// <summary>库选项的「元数据下载器」闸门：与 Emby 自己的 IsMetadataFetcherEnabled 同源判断。</summary>
        private static bool IsFetcherEnabled(BaseItem item, LibraryOptions libraryOptions, string source)
        {
            var providerName = string.Equals(source, CommunitySources.IntroDb, StringComparison.Ordinal)
                ? IntroDbProvider.ProviderName
                : TheIntroDbProvider.ProviderName;

            try
            {
                return item.IsMetadataFetcherEnabled(libraryOptions, providerName);
            }
            catch (Exception ex)
            {
                // 版本差异兜底：保守视为未勾选（宁可不查），但把原因写出来便于诊断。
                Logger?.Info("[Community] 库选项判断失败（按未勾选处理）：{0} — {1}", providerName, ex.Message);
                return false;
            }
        }

        /// <summary>移除同类型、指定后缀的插件标记行。</summary>
        private static void RemoveOurs(List<ChapterInfo> chapters, MarkerType type, string suffix)
        {
            chapters.RemoveAll(c => c != null
                                    && c.MarkerType == type
                                    && c.Name != null
                                    && c.Name.EndsWith(suffix, StringComparison.Ordinal));
        }

        /// <summary>
        /// 把该集现有的插件标记行（#ECS / #VMDB / #VMCH）并回待写列表：
        /// Emby 的 SaveChapters 是「DELETE 该 ItemId 全部行 + 全量 INSERT」，
        /// 不并回就会把插件（含用户手摆）标记抹掉。
        /// </summary>
        private static bool MergeExistingPluginMarkers(BaseItem item, List<ChapterInfo> chapters)
        {
            var added = false;
            try
            {
                var existing = Plugin.Instance?.ItemRepository?.GetChapters(item);
                if (existing == null)
                {
                    return false;
                }

                foreach (var row in existing)
                {
                    if (row?.Name == null)
                    {
                        continue;
                    }

                    if (!PluginSuffixes.Any(s => row.Name.EndsWith(s, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    // 同一 MarkerType 只留一条：按来源优先级取舍
                    // （手动锁定 > 社区值 #VMDB(2) > 内嵌章节映射 #VMCH(1) > 本地检测值 #ECS(0)）。
                    // 手动锁定的集在前面已整体豁免，走到这里的 #ECS 都是自动检测值。
                    var clash = chapters.FirstOrDefault(c => c.MarkerType == row.MarkerType);
                    if (clash != null)
                    {
                        if (MarkerPriority(row.Name) <= MarkerPriority(clash.Name))
                        {
                            continue;
                        }

                        chapters.Remove(clash);
                    }

                    chapters.Add(new ChapterInfo
                    {
                        Name = row.Name,
                        MarkerType = row.MarkerType,
                        StartPositionTicks = row.StartPositionTicks,
                        ImagePath = row.ImagePath,
                        ImageDateModified = row.ImageDateModified,
                    });
                    added = true;
                }
            }
            catch (Exception ex)
            {
                Logger?.Debug("[Community] 并回既有插件标记失败（忽略）：{0}", ex.Message);
            }

            return added;
        }

        /// <summary>标记来源优先级：社区值 &gt; 内嵌章节映射 &gt; 本地检测值。</summary>
        private static int MarkerPriority(string name)
        {
            if (name == null)
            {
                return -1;
            }

            if (name.EndsWith(CommunityMarkerSuffix, StringComparison.Ordinal))
            {
                return 2;
            }

            return name.EndsWith(EmbeddedChapterMap.Suffix, StringComparison.Ordinal) ? 1 : 0;
        }

    }
}
