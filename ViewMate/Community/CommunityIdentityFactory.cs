using System;
using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace ViewMate.Community
{
    /// <summary>
    /// BaseItem → 社区库定位参数。
    /// id 读取一律走 Emby 的 <c>GetProviderId</c> 扩展方法（ProviderIdDictionary 大小写不敏感），
    /// 不手解析字符串；剧集用所属剧（Series）的 id，片长取集自身的 RunTimeTicks。
    /// </summary>
    internal static class CommunityIdentityFactory
    {
        internal static CommunityIdentity ForEpisode(Episode episode, string source)
        {
            if (episode == null)
            {
                return null;
            }

            var series = episode.Series;
            var season = episode.ParentIndexNumber;
            var number = episode.IndexNumber;

            var identity = new CommunityIdentity
            {
                Source = source,
                IsMovie = false,
                Season = season,
                Episode = number,
                RuntimeTicks = episode.RunTimeTicks ?? 0,
                Title = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} S{1:00}E{2:00}",
                    series?.Name ?? episode.SeriesName ?? "<unknown>",
                    season ?? 0,
                    number ?? 0),
            };

            ApplyIds(identity, series, episode);

            // v4 的集序与钉集参数：必须在 ApplyIds 之后（剧 tvdb id 用来判定「集 id 是否与剧相同」），
            // 且在 BuildCacheKey 之前（两者都要进缓存键）。
            identity.DisplayOrder = GetDisplayOrder(series);
            var episodeTvdbId = ParseId(episode.GetProviderId(MetadataProviders.Tvdb));
            if (episodeTvdbId.HasValue && (!identity.TvdbId.HasValue || identity.TvdbId.Value != episodeTvdbId.Value))
            {
                identity.TvdbEpisodeId = episodeTvdbId;
            }

            identity.CacheKey = BuildCacheKey(identity, "tv");
            return identity;
        }

        internal static CommunityIdentity ForMovie(Movie movie, string source)
        {
            if (movie == null)
            {
                return null;
            }

            var identity = new CommunityIdentity
            {
                Source = source,
                IsMovie = true,
                RuntimeTicks = movie.RunTimeTicks ?? 0,
                Title = movie.Name ?? "<unknown>",
            };

            ApplyIds(identity, movie, movie);
            identity.CacheKey = BuildCacheKey(identity, "movie");
            return identity;
        }

        private static void ApplyIds(CommunityIdentity identity, BaseItem primary, BaseItem fallback)
        {
            identity.TmdbId = ParseId(primary?.GetProviderId(MetadataProviders.Tmdb))
                              ?? ParseId(fallback?.GetProviderId(MetadataProviders.Tmdb));
            identity.TvdbId = ParseId(primary?.GetProviderId(MetadataProviders.Tvdb))
                              ?? ParseId(fallback?.GetProviderId(MetadataProviders.Tvdb));

            var imdb = primary?.GetProviderId(MetadataProviders.Imdb);
            if (string.IsNullOrWhiteSpace(imdb))
            {
                imdb = fallback?.GetProviderId(MetadataProviders.Imdb);
            }

            identity.ImdbId = string.IsNullOrWhiteSpace(imdb) ? null : imdb.Trim();
        }

        private static int? ParseId(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id
                : (int?)null;
        }

        /// <summary>
        /// 集序 → v4 <c>mapping</c> 的取值。只认 Dvd / Absolute 两种，值写死；
        /// 不做别名猜测（v4 对未知拼写返回 400），其它（含 Aired / default）一律 null（= 不传 mapping）。
        /// </summary>
        private static string GetDisplayOrder(Series series)
        {
            if (series == null)
            {
                return null;
            }

            switch (series.DisplayOrder)
            {
                case SeriesDisplayOrder.Dvd:
                    return "dvd";
                case SeriesDisplayOrder.Absolute:
                    return "absolute";
                default:
                    return null;
            }
        }

        /// <summary>
        /// 缓存键：{site}_{id 类型}:{值}_{movie|tv}[_s{季}_e{集}][_o{集序}][_ep{集 tvdb id}]。
        /// id 段优先序与请求一致：tvdb → tmdb → imdb（v4 以 TVDB 为身份）。
        /// 剧集额外带集序与钉集 id —— 同一集的不同发行版/不同编号必须落在不同键上。
        /// </summary>
        internal static string BuildCacheKey(CommunityIdentity identity, string type)
        {
            string idPart;
            if (identity.TvdbId.HasValue)
            {
                idPart = "tvdb:" + identity.TvdbId.Value.ToString(CultureInfo.InvariantCulture);
            }
            else if (identity.TmdbId.HasValue)
            {
                idPart = "tmdb:" + identity.TmdbId.Value.ToString(CultureInfo.InvariantCulture);
            }
            else if (!string.IsNullOrWhiteSpace(identity.ImdbId))
            {
                idPart = "imdb:" + identity.ImdbId;
            }
            else
            {
                idPart = "none";
            }

            var key = identity.Source + "_" + idPart + "_" + type;
            if (!identity.IsMovie && identity.Season.HasValue && identity.Episode.HasValue)
            {
                key += "_s" + identity.Season.Value.ToString(CultureInfo.InvariantCulture)
                       + "_e" + identity.Episode.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (!identity.IsMovie)
            {
                if (!string.IsNullOrEmpty(identity.DisplayOrder))
                {
                    key += "_o" + identity.DisplayOrder;
                }

                if (identity.TvdbEpisodeId.HasValue)
                {
                    key += "_ep" + identity.TvdbEpisodeId.Value.ToString(CultureInfo.InvariantCulture);
                }
            }

            return key;
        }

        /// <summary>
        /// 闸门 3：本地缺哪个片段（缺 IntroStart 或缺 CreditsStart 之一才算缺；
        /// 有标记但值不完整时按缺处理）。
        /// 只认**本地检测值**（#ECS）：社区值（#VMDB）与内嵌章节映射值（#VMCH）不算
        /// 「本地已有」—— 否则同一次刷新里先写入的 #VMCH 会把社区查询永久挡掉，
        /// 「社区值 &gt; 本地检测值」的优先级也就无从体现。
        /// </summary>
        internal static (bool NeedIntro, bool NeedCredits) GetMissingSegments(BaseItem item)
        {
            var api = Plugin.ChapterMarkerApi;
            if (api == null || item == null)
            {
                return (false, false);
            }

            var values = api.GetEcsMarkerValues(item);
            var needIntro = !values.IntroStart.HasValue || !values.IntroEnd.HasValue;
            var needCredits = !values.CreditsStart.HasValue;
            return (needIntro, needCredits);
        }
    }
}
