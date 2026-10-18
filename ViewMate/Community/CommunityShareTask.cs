using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using ViewMate.Common;
using ViewMate.IntroSkip;

namespace ViewMate.Community
{
    /// <summary>
    /// 社区共享计划任务（§B.3）：Emby 计划任务面板名「共享片头片尾标志到社区」，
    /// 12 小时一趟（IntervalTrigger，起点 = 服务启动 + 1 小时，自然相位错开；
    /// 也可在面板手动触发）。
    ///
    /// 本版为 **dry-run（只打包请求体、绝不 POST）**：维护者先在面板点一次看全流程，
    /// 真实提交由后续版本启用。范围只含「本地检测产生的标记 + 非锁定集」，
    /// 绝不上传手动锁定值 / 内嵌章节映射值（#VMCH）。
    /// key 无效或任一闸门不满足 → 静默跳过（不写 Error、不中断 embyserver）。
    /// </summary>
    internal sealed class CommunityShareTask : IScheduledTask
    {
        /// <summary>dry-run 模式（本版固定 true：不发出任何 POST）。</summary>
        internal const bool DryRun = true;

        /// <summary>单轮最多打包的标记条数（防大库一次跑太久）。</summary>
        internal const int MaxItemsPerRun = 500;

        /// <summary>dry-run 产物文件名（插件数据目录，供自证与维护者查看）。</summary>
        internal const string DryRunFileName = "viewmate-community-share-dryrun.json";

        public string Name => "共享片头片尾标志到社区";

        public string Key => "ViewMateCommunityShare";

        public string Description =>
            "把本地检测产生的片头/片尾标记（不含手动锁定与内嵌章节映射值）提交到 TheIntroDB / IntroDB 社区库。"
            + "需要：总开关 + 共享子开关都开、对应库的 API key 已配置。当前为 dry-run（只打包请求体，不真正提交）。";

        public string Category => "观影助手";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(12).Ticks,
                },
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var logger = Plugin.Instance?.Logger;
            try
            {
                // 全闸门：总开关 + 共享子开关 + 两把 key 都齐 —— 任一不满足静默跳过。
                if (!CommunityOptions.ShareEnabled)
                {
                    logger?.Debug("[Community] 共享任务跳过：共享开关未开");
                    return;
                }

                var theIntroDbKey = CommunityOptions.TheIntroDbKey;
                var introDbKey = CommunityOptions.IntroDbKey;
                if (string.IsNullOrWhiteSpace(theIntroDbKey) && string.IsNullOrWhiteSpace(introDbKey))
                {
                    logger?.Debug("[Community] 共享任务跳过：两把 key 都未配置");
                    return;
                }

                var bundle = BuildBundle(logger, out var skipped);
                progress?.Report(50);

                var path = WriteDryRunFile(bundle, logger);

                logger?.Info(
                    "[Community] 共享任务（dry-run）完成：打包 {0} 条，跳过 {1} 条，产物 {2}",
                    bundle.Count,
                    skipped,
                    path);
                await Task.CompletedTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                logger?.Info("[Community] 共享任务被取消");
            }
            catch (Exception ex)
            {
                // 失败静默：只记一条 Info/Debug，不抛给调度器。
                logger?.Info("[Community] 共享任务异常（已忽略）：{0}", ex.Message);
            }
        }

        private class ShareBundle
        {
            public string GeneratedAt { get; set; }

            public string Mode { get; set; }

            public List<ShareItem> Items { get; set; } = new List<ShareItem>();
        }

        private class ShareItem
        {
            public string Target { get; set; }

            public long ItemId { get; set; }

            /// <summary>TheIntroDB 提交体（字段名照 MIK Services/TheIntroDbService.cs 的 SubmissionRequest）。</summary>
            public Dictionary<string, object> TheIntroDbBody { get; set; }

            /// <summary>IntroDB 提交体（字段名照 MIK Services/IntroDbService.cs 的 SubmissionRequest）。</summary>
            public Dictionary<string, object> IntroDbBody { get; set; }
        }

        private static List<ShareItem> BuildBundle(ILogger logger, out int skipped)
        {
            var items = new List<ShareItem>();
            skipped = 0;

            var plugin = Plugin.Instance;
            if (plugin?.LibraryManager == null)
            {
                return items;
            }

            List<BaseItem> candidates;
            try
            {
                candidates = plugin.LibraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode), nameof(Movie) },
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video },
                    Limit = MaxItemsPerRun,
                }).ToList();
            }
            catch (Exception ex)
            {
                logger?.Info("[Community] 共享任务枚举条目失败：{0}", ex.Message);
                return items;
            }

            foreach (var item in candidates)
            {
                try
                {
                    // 只上传本地检测产生的非锁定集：覆盖表命中（locked/suppressed）一律跳过。
                    if (IntroMarkerOverrides.IsProtected(item.InternalId))
                    {
                        skipped++;
                        continue;
                    }

                    var api = Plugin.ChapterMarkerApi;
                    var introStart = api?.GetIntroStart(item);
                    var introEnd = api?.GetIntroEnd(item);
                    var credits = api?.GetCreditsStart(item);

                    if (!introStart.HasValue && !credits.HasValue)
                    {
                        continue;
                    }

                    var source = item is Episode ? CommunitySources.TheIntroDb : CommunitySources.TheIntroDb;
                    var identity = item is Episode episode
                        ? CommunityIdentityFactory.ForEpisode(episode, source)
                        : CommunityIdentityFactory.ForMovie(item as Movie, source);

                    if (identity == null)
                    {
                        skipped++;
                        continue;
                    }

                    var entry = new ShareItem
                    {
                        Target = identity.CacheKey,
                        ItemId = item.InternalId,
                    };

                    if (identity.TmdbId.HasValue || !string.IsNullOrWhiteSpace(identity.ImdbId))
                    {
                        entry.TheIntroDbBody = BuildTheIntroDbBody(identity, introStart, introEnd, credits);
                        entry.IntroDbBody = BuildIntroDbBody(identity, introStart, introEnd, credits);
                    }
                    else
                    {
                        // 没有 TMDB/IMDB 定位 → 两个库都提交不了。
                        skipped++;
                        continue;
                    }

                    if (entry.TheIntroDbBody == null && entry.IntroDbBody == null)
                    {
                        skipped++;
                        continue;
                    }

                    items.Add(entry);
                }
                catch (Exception ex)
                {
                    skipped++;
                    logger?.Debug("[Community] 共享任务跳过一条（{0}）：{1}", item?.Name, ex.Message);
                }
            }

            return items;
        }

        private static Dictionary<string, object> BuildTheIntroDbBody(
            CommunityIdentity identity,
            long? introStart,
            long? introEnd,
            long? credits)
        {
            if (!identity.TmdbId.HasValue)
            {
                return null;
            }

            var body = new Dictionary<string, object>
            {
                ["tmdb_id"] = identity.TmdbId.Value,
                ["type"] = identity.IsMovie ? "movie" : "tv",
            };

            if (!identity.IsMovie)
            {
                if (!identity.Season.HasValue || !identity.Episode.HasValue)
                {
                    return null;
                }

                body["season"] = identity.Season.Value;
                body["episode"] = identity.Episode.Value;
            }

            if (!string.IsNullOrWhiteSpace(identity.ImdbId))
            {
                body["imdb_id"] = identity.ImdbId;
            }

            if (identity.TvdbId.HasValue)
            {
                body["tvdb_id"] = identity.TvdbId.Value;
            }

            var durationMs = identity.RuntimeTicks / 10000L;
            if (durationMs >= 300000L && durationMs <= 21600000L)
            {
                body["video_duration_ms"] = durationMs;
            }

            // intro 与 credits 是两次独立提交；dry-run 里各给一个 segment 视图。
            if (introStart.HasValue)
            {
                return new Dictionary<string, object>(body)
                {
                    ["segment"] = "intro",
                    ["start_ms"] = introStart.Value / 10000L,
                    ["end_ms"] = (introEnd ?? introStart.Value) / 10000L,
                };
            }

            if (credits.HasValue)
            {
                return new Dictionary<string, object>(body)
                {
                    ["segment"] = "credits",
                    ["start_ms"] = credits.Value / 10000L,
                };
            }

            return null;
        }

        private static Dictionary<string, object> BuildIntroDbBody(
            CommunityIdentity identity,
            long? introStart,
            long? introEnd,
            long? credits)
        {
            if (identity.IsMovie || string.IsNullOrWhiteSpace(identity.ImdbId)
                || !identity.Season.HasValue || !identity.Episode.HasValue)
            {
                return null;
            }

            var body = new Dictionary<string, object>
            {
                ["segment_type"] = introStart.HasValue ? "intro" : "outro",
                ["imdb_id"] = identity.ImdbId,
                ["season"] = identity.Season.Value,
                ["episode"] = identity.Episode.Value,
            };

            if (identity.TvdbId.HasValue)
            {
                body["tvdb_id"] = identity.TvdbId.Value;
            }

            if (identity.TmdbId.HasValue)
            {
                body["tmdb_id"] = identity.TmdbId.Value;
            }

            if (introStart.HasValue)
            {
                body["start_sec"] = Math.Round(introStart.Value / 10000000.0, 3);
                body["end_sec"] = Math.Round((introEnd ?? introStart.Value) / 10000000.0, 3);
            }
            else
            {
                // outro：end_sec = 片长（MIK 口径）。
                body["start_sec"] = Math.Round(credits.Value / 10000000.0, 3);
                body["end_sec"] = Math.Round(identity.RuntimeTicks / 10000000.0, 3);
            }

            return body;
        }

        private static string WriteDryRunFile(List<ShareItem> items, ILogger logger)
        {
            var bundle = new ShareBundle
            {
                GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Mode = DryRun ? "dry-run" : "live",
                Items = items,
            };

            var path = ResolveDryRunPath();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };

            try
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(bundle, options), new UTF8Encoding(false));
                File.Move(tmp, path, true);
            }
            catch (Exception ex)
            {
                logger?.Info("[Community] 写入 dry-run 产物失败：{0}", ex.Message);
            }

            return path;
        }

        private static string ResolveDryRunPath()
        {
            var candidates = new[]
            {
                "/config/plugins/" + DryRunFileName,
                "plugins/" + DryRunFileName,
                "../plugins/" + DryRunFileName,
            };

            foreach (var path in candidates)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    return path;
                }
            }

            return candidates[0];
        }
    }
}
