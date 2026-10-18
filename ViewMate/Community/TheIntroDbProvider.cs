using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace ViewMate.Community
{
    /// <summary>
    /// TheIntroDB 的「元数据下载器」占位 Provider（§B.2.1）。
    /// 存在意义：让 <c>ViewMate — TheIntroDB</c> 出现在每个媒体库的「元数据下载器」勾选列表里
    /// （§B.2.2 闸门 2 由维护者按库控制）。**它自身不发任何网络请求** ——
    /// <c>IRemoteMetadataProvider</c> 的回调只拿到 <c>MovieInfo</c>/<c>EpisodeInfo</c>（查找信息），
    /// 拿不到 BaseItem，无法实施「本地缺片段才查」与「锁定集豁免」两条闸门；
    /// 真正的查询与写回由同一条 ProviderManager 管线里的
    /// <see cref="CommunityMarkerProvider"/>（ICustomMetadataProvider，能拿到 item 与 libraryOptions）完成。
    /// 因此「未勾选 = 零出站」在两条路径上同时成立。
    /// </summary>
    internal sealed class TheIntroDbProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IRemoteMetadataProvider<Episode, EpisodeInfo>
    {
        internal const string ProviderName = "ViewMate — TheIntroDB";

        public string Name => ProviderName;

        public Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            return Task.FromResult(new MetadataResult<Movie> { HasMetadata = false });
        }

        public Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
        {
            return Task.FromResult(new MetadataResult<Episode> { HasMetadata = false });
        }

        public Task<System.Collections.Generic.IEnumerable<RemoteSearchResult>> GetSearchResults(
            MovieInfo searchInfo,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(System.Linq.Enumerable.Empty<RemoteSearchResult>());
        }

        public Task<System.Collections.Generic.IEnumerable<RemoteSearchResult>> GetSearchResults(
            EpisodeInfo searchInfo,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(System.Linq.Enumerable.Empty<RemoteSearchResult>());
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return Task.FromResult<HttpResponseInfo>(null);
        }
    }
}
