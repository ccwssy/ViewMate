using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbyChineseSearch.Web.Api
{
    [Route("/{Web}/components/strmassistant/strmassistant.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public class GetEmbyChineseSearchJs
    {
        public string Web { get; set; }

        public string ResourceName { get; set; }
    }
}
