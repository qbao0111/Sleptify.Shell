using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sleptify.Shell.Interop
{
    public class SpotifyAuth
    {
        private const string TokenFile = "spotify_token.json";
        private readonly string _clientId;
        private readonly string _redirectUri;
        private readonly string _scope;
        private string? _codeVerifier;
        private string? _codeChallenge;

        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public DateTime ExpiresAtUtc { get; set; }

        public string ClientId => _clientId;
        public string RedirectUri => _redirectUri;
        public string Scope => _scope;

        public SpotifyAuth(string clientId, string redirectUri, string scope)
        {
            _clientId = clientId;
            _redirectUri = redirectUri;
            _scope = scope;

            // Tự động load token cache nếu có
            LoadToken();
        }

        private string AppDataFilePath()
        {
            var app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(app, "Sleptify");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, TokenFile);
        }

        private void LoadToken()
        {
            try
            {
                var file = AppDataFilePath();
                if (!File.Exists(file)) return;
                var json = File.ReadAllText(file);
                var doc = JsonDocument.Parse(json);
                AccessToken = doc.RootElement.GetProperty("access_token").GetString();
                RefreshToken = doc.RootElement.GetProperty("refresh_token").GetString();
                if (doc.RootElement.TryGetProperty("expires_at", out var ea))
                    ExpiresAtUtc = ea.GetDateTime();
            }
            catch { }
        }

        private void SaveToken()
        {
            try
            {
                var file = AppDataFilePath();
                var json = JsonSerializer.Serialize(new
                {
                    access_token = AccessToken,
                    refresh_token = RefreshToken,
                    expires_at = ExpiresAtUtc
                });
                File.WriteAllText(file, json);
            }
            catch { }
        }

        private void ClearToken()
        {
            AccessToken = null;
            RefreshToken = null;
            ExpiresAtUtc = DateTime.MinValue;
            try
            {
                var file = AppDataFilePath();
                if (File.Exists(file)) File.Delete(file);
            }
            catch { }
        }

        public bool IsValid()
        {
            return !string.IsNullOrEmpty(AccessToken) && DateTime.UtcNow < ExpiresAtUtc;
        }

        /* ===========================================================
         *   HÀM CHÍNH: EnsureTokenAsync (2 overload)
         * =========================================================== */

        /// <summary>
        /// Phiên bản cơ bản – tự động refresh nếu hết hạn.
        /// </summary>
        public async Task<bool> EnsureTokenAsync()
        {
            if (IsValid()) return true;

            if (!string.IsNullOrEmpty(RefreshToken))
            {
                if (await RefreshAsync()) return true;
            }

            return await FullAuthAsync(showDialog: false);
        }

        /// <summary>
        /// Phiên bản mở rộng – ép re-consent (show_dialog=true, force xoá cache).
        /// </summary>
        public async Task<bool> EnsureTokenAsync(bool force, bool showDialog)
        {
            if (force) ClearToken();
            if (IsValid()) return true;

            if (!string.IsNullOrEmpty(RefreshToken) && !force)
            {
                if (await RefreshAsync()) return true;
            }

            return await FullAuthAsync(showDialog);
        }

        /* ===========================================================
         *   AUTH FLOW – PKCE
         * =========================================================== */

        private async Task<bool> FullAuthAsync(bool showDialog)
        {
            try
            {
                // Tạo PKCE
                (_codeVerifier, _codeChallenge) = Pkce.GenerateCodes();

                var authUrl =
                    "https://accounts.spotify.com/authorize" +
                    "?client_id=" + Uri.EscapeDataString(_clientId) +
                    "&response_type=code" +
                    "&redirect_uri=" + Uri.EscapeDataString(_redirectUri) +
                    "&scope=" + Uri.EscapeDataString(_scope) +
                    "&code_challenge_method=S256" +
                    "&code_challenge=" + _codeChallenge +
                    (showDialog ? "&show_dialog=true" : "");

                Process.Start(new ProcessStartInfo
                {
                    FileName = authUrl,
                    UseShellExecute = true
                });

                // Chờ callback code
                var code = await WaitForAuthCodeAsync();
                if (string.IsNullOrEmpty(code))
                    return false;

                // Exchange code lấy token
                return await ExchangeCodeAsync(code);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Auth error: " + ex.Message);
                return false;
            }
        }

        private async Task<bool> ExchangeCodeAsync(string code)
        {
            using var http = new HttpClient();
            var body = new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = _redirectUri,
                ["code_verifier"] = _codeVerifier ?? ""
            };
            var res = await http.PostAsync("https://accounts.spotify.com/api/token",
                new FormUrlEncodedContent(body));
            if (!res.IsSuccessStatusCode) return false;

            var json = await res.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            AccessToken = doc.RootElement.GetProperty("access_token").GetString();
            RefreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt)
                ? rt.GetString()
                : RefreshToken;
            var expires = doc.RootElement.GetProperty("expires_in").GetInt32();
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expires - 30);
            SaveToken();
            return true;
        }

        public async Task<bool> RefreshAsync()
        {
            if (string.IsNullOrEmpty(RefreshToken)) return false;
            try
            {
                using var http = new HttpClient();
                var body = new Dictionary<string, string>
                {
                    ["client_id"] = _clientId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = RefreshToken
                };
                var res = await http.PostAsync("https://accounts.spotify.com/api/token",
                    new FormUrlEncodedContent(body));
                if (!res.IsSuccessStatusCode) return false;

                var json = await res.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                AccessToken = doc.RootElement.GetProperty("access_token").GetString();
                if (doc.RootElement.TryGetProperty("refresh_token", out var rt))
                    RefreshToken = rt.GetString();
                var expires = doc.RootElement.GetProperty("expires_in").GetInt32();
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expires - 30);
                SaveToken();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Refresh token failed: " + ex.Message);
                return false;
            }
        }

        /* ===========================================================
         *   LISTENER – Nhận code qua RedirectUri (http://127.0.0.1:5173/callback)
         * =========================================================== */
        private async Task<string?> WaitForAuthCodeAsync()
        {
            var listener = new System.Net.HttpListener();
            listener.Prefixes.Add(_redirectUri.EndsWith("/") ? _redirectUri : _redirectUri + "/");
            listener.Start();

            try
            {
                var ctx = await listener.GetContextAsync();
                var req = ctx.Request;
                var resp = ctx.Response;

                var code = req.QueryString["code"];
                var html = "<html><body style='font-family:sans-serif;text-align:center;padding-top:40px;'>Sleptify connected.<br/>You can close this window.</body></html>";
                var buf = Encoding.UTF8.GetBytes(html);
                resp.ContentLength64 = buf.Length;
                await resp.OutputStream.WriteAsync(buf, 0, buf.Length);
                resp.OutputStream.Close();
                listener.Stop();
                return code;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("WaitAuth error: " + ex.Message);
                return null;
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }
    }

    /* ===========================================================
     *   PKCE Utilities
     * =========================================================== */
    internal static class Pkce
    {
        public static (string verifier, string challenge) GenerateCodes()
        {
            var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            var bytes = new byte[32];
            rng.GetBytes(bytes);
            var verifier = Base64UrlEncode(bytes);
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var challenge = Base64UrlEncode(sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            return (verifier, challenge);
        }

        private static string Base64UrlEncode(byte[] input)
        {
            return Convert.ToBase64String(input)
                .Replace("+", "-").Replace("/", "_").Replace("=", "");
        }
    }
}
