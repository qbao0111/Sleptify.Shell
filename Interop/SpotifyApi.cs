
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sleptify.Shell.Interop
{
    public class SpotifyApi
    {
        private readonly SpotifyAuth _auth;
        private readonly HttpClient _http = new HttpClient();
        public SpotifyApi(SpotifyAuth auth){ _auth = auth; }

        private async Task<HttpRequestMessage> BuildAsync(HttpMethod method, string url, HttpContent? content=null){
            await _auth.EnsureTokenAsync();
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _auth.AccessToken);
            if(content!=null) req.Content = content;
            return req;
        }

        public async Task TransferPlaybackAsync(string deviceId, bool play){
            var url = "https://api.spotify.com/v1/me/player";
            var body = new { device_ids = new[]{ deviceId }, play };
            var req = await BuildAsync(HttpMethod.Put, url, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
            var res = await _http.SendAsync(req);
            res.EnsureSuccessStatusCode();
        }

        public async Task PlayUriAsync(string uri){
            var url = "https://api.spotify.com/v1/me/player/play";
            var body = new { uris = new[]{ uri } };
            var req = await BuildAsync(HttpMethod.Put, url, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
            var res = await _http.SendAsync(req);
            res.EnsureSuccessStatusCode();
        }
    }
}
