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
    /// IntroDB 的「元数据下载器」占位 Provider（仅剧集）。
    /// 同 <see cref="TheIntroDbProvider"/>：只负责让 <c>ViewMate — IntroDB</c> 出现在库设置里，
    /// 自身零网络请求；实际查询/写回在同管线的 <see cref="CommunityMarkerProvider"/>。
    /// </summary>
    internal sealed class IntroDbProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>
    {
        internal const string ProviderName = "ViewMate — IntroDB";

        public string Name => ProviderName;

        public Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
        {
            return Task.FromResult(new MetadataResult<Episode> { HasMetadata = false });
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
