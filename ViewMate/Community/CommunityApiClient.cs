using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;

namespace ViewMate.Community
{
    /// <summary>
    /// 社区片头片尾查询客户端。只走 <see cref="IHttpClient"/>（Emby 的连接池），
    /// 10 秒超时、ThrowOnErrorResponse=false、整段 try/catch —— 失败一律返回未命中结果，
    /// 绝不上抛到 Emby 的元数据刷新管线。
    /// 出站域名写死：api.theintrodb.org / api.introdb.app。
    /// TheIntroDB 走 v4：v2/v3 已冻结（只剩 GET /media 且不再扩展），v3 于 2027-01-18 00:00:00 GMT 关停。
    /// </summary>
    internal static class CommunityApiClient
    {
        internal const string TheIntroDbBase = "https://api.theintrodb.org/v4";
        internal const string IntroDbBase = "https://api.introdb.app";

        private const int TimeoutMs = 10000;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        // 1 ms = 10000 ticks。
        private const long TicksPerMs = 10000L;

        // ── TheIntroDB 回应模型 ──
        private class TheIntroDbSegment
        {
            public long? start_ms { get; set; }

            public long? end_ms { get; set; }
        }

        private class TheIntroDbMedia
        {
            public List<TheIntroDbSegment> intro { get; set; }

            public List<TheIntroDbSegment> credits { get; set; }
        }

        // ── IntroDB 回应模型（单对象、非数组）──
        private class IntroDbSegment
        {
            public long? start_ms { get; set; }

            public long? end_ms { get; set; }
        }

        private class IntroDbSegments
        {
            public IntroDbSegment intro { get; set; }

            public IntroDbSegment recap { get; set; }

            public IntroDbSegment outro { get; set; }

            public IntroDbSegment post_credits { get; set; }
        }

        /// <summary>
        /// 带缓存查询：命中 6h 缓存（含负缓存）直接返回，不产生任何 HTTP 请求。
        /// </summary>
        internal static async Task<CommunityLookupResult> QueryAsync(
            CommunityIdentity identity,
            string apiKey,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (identity == null || string.IsNullOrEmpty(identity.CacheKey))
            {
                return NotFound(identity, "no identity");
            }

            var cached = CommunityDiskCache.Get(identity.CacheKey, logger);
            if (cached != null)
            {
                logger?.Debug("[Community] cache hit {0}", identity.CacheKey);
                return cached;
            }

            var result = string.Equals(identity.Source, CommunitySources.IntroDb, StringComparison.Ordinal)
                ? await QueryIntroDbAsync(identity, logger, cancellationToken).ConfigureAwait(false)
                : await QueryTheIntroDbAsync(identity, apiKey, logger, cancellationToken).ConfigureAwait(false);

            CommunityDiskCache.Set(identity.CacheKey, result, logger);
            return result;
        }

        /// <summary>
        /// TheIntroDB：GET {base}/media，id 优先序 tvdb &gt; tmdb &gt; imdb（v4 以 TVDB 为身份）。
        /// query 段全部由 <see cref="BuildTheIntroDbQuery"/> 构造（纯函数），此处只负责发请求与解析。
        /// </summary>
        private static async Task<CommunityLookupResult> QueryTheIntroDbAsync(
            CommunityIdentity identity,
            string apiKey,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var query = BuildTheIntroDbQuery(identity);
            if (query == null)
            {
                return NotFound(identity, DescribeQueryMiss(identity));
            }

            var url = TheIntroDbBase + "/media" + query;
            var headers = string.IsNullOrWhiteSpace(apiKey)
                ? null
                : new Dictionary<string, string> { ["Authorization"] = "Bearer " + apiKey.Trim() };

            var body = await GetStringAsync(url, headers, CommunitySources.TheIntroDb, identity, logger, cancellationToken)
                .ConfigureAwait(false);
            if (body == null)
            {
                return NotFound(identity, "request failed");
            }

            TheIntroDbMedia media;
            try
            {
                // v4 /media 除片头片尾数据外还有 versions[] / episodes[] 两种形态
                // （官方 parseMediaResponse 先判 versions 再判 episodes），这里只识别、不消费。
                using (var document = JsonDocument.Parse(body))
                {
                    var root = document.RootElement;
                    if (HasArrayPayload(root, "versions") || HasArrayPayload(root, "episodes"))
                    {
                        logger?.Info(
                            "[Community] TheIntroDB 回应为 versions/episodes 形态（非片头片尾数据）：{0}",
                            identity.Title);
                        return NotFound(identity, "unexpected /media shape");
                    }
                }

                media = JsonSerializer.Deserialize<TheIntroDbMedia>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                logger?.Info("[Community] TheIntroDB 回应无法解析：{0}（{1}）", ex.Message, identity.Title);
                return NotFound(identity, "bad json");
            }

            var result = BaseResult(identity);
            result.IntroStartTicks = SelectIntroStart(media?.intro);
            var introSegment = SelectIntro(media?.intro);
            if (introSegment != null)
            {
                result.IntroStartTicks = introSegment.Item1;
                result.IntroEndTicks = introSegment.Item2;
            }

            var creditsStart = SelectCreditsStart(media?.credits, identity.RuntimeTicks);
            if (creditsStart.HasValue)
            {
                result.CreditsStartTicks = creditsStart.Value;
            }

            result.Found = result.IntroStartTicks.HasValue || result.CreditsStartTicks.HasValue;
            result.Reason = result.Found ? null : "no usable segment";
            if (!result.Found)
            {
                logger?.Debug("[Community] TheIntroDB 无可用片段：{0}", identity.Title);
            }

            return result;
        }

        // ── v4 查询串构造（纯函数，便于离线断言）──

        /// <summary>
        /// 构造 v4 <c>GET /media</c> 的 query string（含前导 '?'）；无法构造时返回 null。
        /// 段顺序固定：身份 → season → episode → mapping → tvdb_episode_id → duration_ms。
        /// 身份三选一按序：tvdb_id → tmdb_id → imdb_id（v4 以 TVDB 为身份）。
        /// 电影只出身份段：duration_ms 在 v4 仅单集 TV 有效，movie 带 season/episode/mapping 也会 400。
        /// 剧集非 default 集序必须用 tvdb 寻址（tmdb_id 请求一律按 TMDB 编号解释，配别的集序无意义）。
        /// </summary>
        internal static string BuildTheIntroDbQuery(CommunityIdentity identity)
        {
            if (identity == null)
            {
                return null;
            }

            var parts = new List<string>();
            if (identity.TvdbId.HasValue)
            {
                parts.Add("tvdb_id=" + identity.TvdbId.Value.ToString(CultureInfo.InvariantCulture));
            }
            else if (identity.TmdbId.HasValue)
            {
                parts.Add("tmdb_id=" + identity.TmdbId.Value.ToString(CultureInfo.InvariantCulture));
            }
            else if (!string.IsNullOrWhiteSpace(identity.ImdbId))
            {
                parts.Add("imdb_id=" + Uri.EscapeDataString(identity.ImdbId));
            }
            else
            {
                return null;
            }

            if (identity.IsMovie)
            {
                return "?" + string.Join("&", parts);
            }

            if (!identity.Season.HasValue || !identity.Episode.HasValue)
            {
                return null;
            }

            var hasTvdb = identity.TvdbId.HasValue;
            var hasDisplayOrder = !string.IsNullOrEmpty(identity.DisplayOrder);
            if (hasDisplayOrder && !hasTvdb)
            {
                return null;
            }

            parts.Add("season=" + identity.Season.Value.ToString(CultureInfo.InvariantCulture));
            parts.Add("episode=" + identity.Episode.Value.ToString(CultureInfo.InvariantCulture));

            if (hasTvdb && hasDisplayOrder)
            {
                parts.Add("mapping=" + Uri.EscapeDataString(identity.DisplayOrder));
            }

            if (hasTvdb && identity.TvdbEpisodeId.HasValue && identity.TvdbEpisodeId.Value > 0)
            {
                parts.Add("tvdb_episode_id=" + identity.TvdbEpisodeId.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (identity.RuntimeTicks > 0)
            {
                parts.Add("duration_ms=" + (identity.RuntimeTicks / TicksPerMs).ToString(CultureInfo.InvariantCulture));
            }

            return "?" + string.Join("&", parts);
        }

        /// <summary><see cref="BuildTheIntroDbQuery"/> 返回 null 时的未命中原因（日志用）。</summary>
        private static string DescribeQueryMiss(CommunityIdentity identity)
        {
            if (identity == null)
            {
                return "no identity";
            }

            if (!identity.TvdbId.HasValue && !identity.TmdbId.HasValue && string.IsNullOrWhiteSpace(identity.ImdbId))
            {
                return "no tvdb/tmdb/imdb id";
            }

            if (!identity.IsMovie && (!identity.Season.HasValue || !identity.Episode.HasValue))
            {
                return "tv requires season+episode";
            }

            if (!identity.IsMovie && !string.IsNullOrEmpty(identity.DisplayOrder) && !identity.TvdbId.HasValue)
            {
                return "non-default episode order requires tvdb id";
            }

            return "cannot build query";
        }

        /// <summary>IntroDB：GET {base}/segments，imdb_id 硬门（国产剧无 IMDB 直接跳过）。</summary>
        private static async Task<CommunityLookupResult> QueryIntroDbAsync(
            CommunityIdentity identity,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(identity.ImdbId) || !identity.Season.HasValue || !identity.Episode.HasValue)
            {
                return NotFound(identity, "introdb requires imdb_id+season+episode");
            }

            var url = IntroDbBase + "/segments?imdb_id=" + Uri.EscapeDataString(identity.ImdbId)
                      + "&season=" + identity.Season.Value.ToString(CultureInfo.InvariantCulture)
                      + "&episode=" + identity.Episode.Value.ToString(CultureInfo.InvariantCulture);

            var body = await GetStringAsync(url, null, CommunitySources.IntroDb, identity, logger, cancellationToken)
                .ConfigureAwait(false);
            if (body == null)
            {
                return NotFound(identity, "request failed");
            }

            IntroDbSegments segments;
            try
            {
                segments = JsonSerializer.Deserialize<IntroDbSegments>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                logger?.Info("[Community] IntroDB 回应无法解析：{0}（{1}）", ex.Message, identity.Title);
                return NotFound(identity, "bad json");
            }

            var result = BaseResult(identity);
            var intro = Normalize(segments?.intro, identity.RuntimeTicks, requireEnd: true);
            if (intro != null)
            {
                result.IntroStartTicks = intro.Item1;
                result.IntroEndTicks = intro.Item2;
            }

            var outro = Normalize(segments?.outro, identity.RuntimeTicks, requireEnd: false);
            if (outro != null)
            {
                result.CreditsStartTicks = outro.Item1;
            }

            result.Found = result.IntroStartTicks.HasValue || result.CreditsStartTicks.HasValue;
            result.Reason = result.Found ? null : "no usable segment";
            return result;
        }

        // ── 解析与校验（同口径：start ≥ 0、end > start、credits start < 片长）──

        private static long? SelectIntroStart(List<TheIntroDbSegment> intro)
        {
            var pair = SelectIntro(intro);
            return pair?.Item1;
        }

        private static Tuple<long, long> SelectIntro(List<TheIntroDbSegment> intro)
        {
            if (intro == null)
            {
                return null;
            }

            foreach (var segment in intro)
            {
                if (segment?.end_ms == null || segment.end_ms.Value <= 0)
                {
                    continue;
                }

                var startMs = segment.start_ms ?? 0;
                if (startMs < 0 || segment.end_ms.Value <= startMs)
                {
                    continue;
                }

                return Tuple.Create(startMs * TicksPerMs, segment.end_ms.Value * TicksPerMs);
            }

            return null;
        }

        private static long? SelectCreditsStart(List<TheIntroDbSegment> credits, long runtimeTicks)
        {
            if (credits == null)
            {
                return null;
            }

            foreach (var segment in credits)
            {
                if (segment?.start_ms == null || segment.start_ms.Value <= 0)
                {
                    continue;
                }

                var ticks = segment.start_ms.Value * TicksPerMs;
                if (runtimeTicks > 0 && ticks >= runtimeTicks)
                {
                    continue;
                }

                return ticks;
            }

            return null;
        }

        /// <summary>根对象是否含指定名字的数组型属性（v4 /media 的 versions[] / episodes[] 形态判别）。</summary>
        private static bool HasArrayPayload(JsonElement root, string name)
        {
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.Array;
        }

        private static Tuple<long, long> Normalize(IntroDbSegment segment, long runtimeTicks, bool requireEnd)
        {
            if (segment?.start_ms == null || segment.start_ms.Value < 0)
            {
                return null;
            }

            var startTicks = segment.start_ms.Value * TicksPerMs;
            var endTicks = (segment.end_ms ?? segment.start_ms.Value) * TicksPerMs;

            if (requireEnd && (segment.end_ms == null || endTicks <= startTicks))
            {
                return null;
            }

            if (runtimeTicks > 0 && startTicks >= runtimeTicks)
            {
                return null;
            }

            return Tuple.Create(startTicks, endTicks);
        }

        // ── HTTP ──

        private static async Task<string> GetStringAsync(
            string url,
            Dictionary<string, string> headers,
            string source,
            CommunityIdentity identity,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var httpClient = Plugin.SharedHttpClient;
            if (httpClient == null)
            {
                logger?.Info("[Community] IHttpClient 不可用，跳过 {0} 查询", source);
                return null;
            }

            var started = DateTime.UtcNow;
            try
            {
                var options = new HttpRequestOptions
                {
                    Url = url,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = "ViewMate",
                    EnableDefaultUserAgent = false,
                    TimeoutMs = TimeoutMs,
                    CacheMode = CacheMode.None,
                    ThrowOnErrorResponse = false,
                };

                if (headers != null)
                {
                    foreach (var pair in headers)
                    {
                        options.RequestHeaders[pair.Key] = pair.Value;
                    }
                }

                using (var response = await httpClient.SendAsync(options, "GET").ConfigureAwait(false))
                {
                    var status = (int)response.StatusCode;
                    string body = null;
                    if (response.Content != null)
                    {
                        using (var reader = new StreamReader(response.Content))
                        {
                            body = await reader.ReadToEndAsync().ConfigureAwait(false);
                        }
                    }

                    var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                    if (status == 404)
                    {
                        logger?.Debug("[Community] {0} 404 未命中：{1}（{2}ms）", source, identity?.Title, elapsed);
                        return null;
                    }

                    if (status == 429)
                    {
                        logger?.Info(
                            "[Community] {0} 达到额度限制（429{1}）：{2}",
                            source,
                            DescribeLimitCode(body),
                            identity?.Title);
                        return null;
                    }

                    if (status < 200 || status >= 300)
                    {
                        logger?.Info("[Community] {0} 查询失败 status={1}：{2}", source, status, identity?.Title);
                        return null;
                    }

                    logger?.Debug("[Community] {0} 查询成功：{1}（{2}ms）", source, identity?.Title, elapsed);
                    return body;
                }
            }
            catch (OperationCanceledException)
            {
                logger?.Debug("[Community] {0} 查询取消/超时：{1}", source, identity?.Title);
                return null;
            }
            catch (Exception ex)
            {
                logger?.Info("[Community] {0} 查询异常（已忽略）：{1} — {2}", source, identity?.Title, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 429 回应体的 <c>code</c> 字段取值（提不到返回空串）；命中时前缀「，」，直接拼进日志模板。
        /// 本方法不得抛异常 —— 回应体可能不是 JSON。
        /// </summary>
        private static string DescribeLimitCode(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            try
            {
                using (var document = JsonDocument.Parse(body))
                {
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("code", out var code)
                        && code.ValueKind == JsonValueKind.String)
                    {
                        var text = code.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return "，" + text.Trim();
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 非 JSON 回应体：不记错误码，不影响主流程。
            }

            return string.Empty;
        }

        // ── 结果构造 ──

        private static CommunityLookupResult BaseResult(CommunityIdentity identity)
        {
            return new CommunityLookupResult
            {
                Source = identity.Source,
                CacheKey = identity.CacheKey,
                TmdbId = identity.TmdbId,
                TvdbId = identity.TvdbId,
                ImdbId = identity.ImdbId,
                IsMovie = identity.IsMovie,
                Season = identity.Season,
                Episode = identity.Episode,
                RuntimeTicks = identity.RuntimeTicks,
                At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
        }

        private static CommunityLookupResult NotFound(CommunityIdentity identity, string reason)
        {
            var result = identity != null
                ? BaseResult(identity)
                : new CommunityLookupResult { At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            result.Found = false;
            result.Reason = reason;
            return result;
        }
    }
}
