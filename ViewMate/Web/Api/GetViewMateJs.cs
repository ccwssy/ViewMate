using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace ViewMate.Web.Api
{
    [Route("/{Web}/components/strmassistant/strmassistant.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public class GetViewMateJs
    {
        public string Web { get; set; }

        public string ResourceName { get; set; }
    }
}
