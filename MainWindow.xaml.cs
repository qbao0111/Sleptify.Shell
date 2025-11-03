using Microsoft.Web.WebView2.Core;
using Sleptify.Shell.Interop;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace Sleptify.Shell
{
    public partial class MainWindow : Window
    {
        private SpotifyAuth? _auth;
        private string _clientId = "9867291a481640d48da2e88f2ee194f1";
        private const string Redirect = "http://127.0.0.1:5173/callback";
        private Rect _restoreBounds;
        private bool _isMaximized = false;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            var wa = screen.WorkingArea;
            MaxHeight = wa.Height;
            MaxWidth = wa.Width;
        }

        // Chặn WindowState=Maximized (ta tự kiểm soát maximize theo WorkingArea)
        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Scope đã bổ sung user-read-recently-played + user-top-read + playlist-modify-private
            _auth = new SpotifyAuth(_clientId, Redirect,
                "streaming user-read-email user-read-private user-read-playback-state user-modify-playback-state playlist-read-private playlist-read-collaborative user-read-recently-played user-top-read playlist-modify-private");

            await Web.EnsureCoreWebView2Async();

            // Map virtual host => thư mục ui-dist
            string uiFolder = FindUiFolder();
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app", uiFolder, CoreWebView2HostResourceAccessKind.Allow);

            Web.CoreWebView2.Settings.IsWebMessageEnabled = true;
            Web.CoreWebView2.Settings.AreDevToolsEnabled = true;

            // Điều hướng index.html
            Web.CoreWebView2.Navigate("https://app/index.html");
            Web.CoreWebView2.WebMessageReceived += OnMsg;

            // Đảm bảo token có đủ scope (nếu token cache cũ thiếu -> ép re-consent)
            await EnsureScopesOrReauthAsync();

            // Bơm token + ping connect()
            if (_auth?.AccessToken != null)
            {
                await PushTokenToWebAsync(_auth.AccessToken);
                await PushTokenAndPingAsync();
            }
        }

        private static string FindUiFolder()
        {
            var baseDir = AppContext.BaseDirectory; // bin\...\ khi chạy
            var prod = Path.Combine(baseDir, "ui-dist");
            if (Directory.Exists(prod)) return prod;

            // Fallback khi F5
            var dev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "ui-dist"));
            if (Directory.Exists(dev)) return dev;

            throw new DirectoryNotFoundException(
                $"Không tìm thấy ui-dist ở:\n{prod}\nhoặc\n{dev}\nHãy copy dist/ vào ui-dist.");
        }

        private async Task PushTokenToWebAsync(string token)
        {
            // Lưu vào localStorage cho HTML
            await Web.ExecuteScriptAsync($"localStorage.setItem('spotify_token','{token}');");
        }

        private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_auth == null) return;

            // Overload hiện có (không có tham số)
            bool ok = await _auth.EnsureTokenAsync();
            if (!ok)
            {
                MessageBox.Show("Spotify auth failed.");
                return;
            }

            await PushTokenToWebAsync(_auth.AccessToken!);
            await PushTokenAndPingAsync();
            MessageBox.Show("Connected to Spotify!");
        }

        private async Task PushTokenAndPingAsync()
        {
            if (_auth?.AccessToken == null) return;
            await Web.ExecuteScriptAsync($"localStorage.setItem('spotify_token','{_auth.AccessToken}');");
            // Gọi connect() trong index.html nếu có
            await Web.ExecuteScriptAsync("window.connect && window.connect()");
        }

        private async void OnMsg(object? s, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.TryGetWebMessageAsString();
                using var doc = JsonDocument.Parse(json);
                var type = doc.RootElement.GetProperty("type").GetString();

                if (type == "spotify:transfer")
                {
                    var deviceId = doc.RootElement.GetProperty("id").GetString()!;
                    var api = new SpotifyApi(_auth!);
                    await api.TransferPlaybackAsync(deviceId, true);
                }
                else if (type == "spotify:play:uri")
                {
                    var uri = doc.RootElement.GetProperty("uri").GetString()!;
                    var api = new SpotifyApi(_auth!);
                    await api.PlayUriAsync(uri);
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        /* ================= Window chrome ================= */
        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try
                {
                    if (e.ClickCount == 2)
                    {
                        ToggleMaximizeRestore();
                    }
                    else if (!_isMaximized)
                    {
                        DragMove();
                    }
                }
                catch { }
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximizeRestore();
        }

        private void ToggleMaximizeRestore()
        {
            if (!_isMaximized)
            {
                _restoreBounds = new Rect(Left, Top, Width, Height);

                var hwnd = new WindowInteropHelper(this).Handle;
                var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
                var wa = screen.WorkingArea;

                Left = wa.Left; Top = wa.Top; Width = wa.Width; Height = wa.Height;

                _isMaximized = true;

                MaxBtn.ToolTip = "Restore";
                ((TextBlock)MaxBtn.Content).Text = "\uE923"; // restore icon
            }
            else
            {
                Left = _restoreBounds.Left; Top = _restoreBounds.Top;
                Width = _restoreBounds.Width; Height = _restoreBounds.Height;

                _isMaximized = false;

                MaxBtn.ToolTip = "Maximize";
                ((TextBlock)MaxBtn.Content).Text = "\uE922"; // maximize icon
            }
        }

        /* ================= Auth helpers ================= */

        private async Task<bool> HasScopesAsync(string accessToken)
        {
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                // test user-top-read
                using var r1 = await http.GetAsync("https://api.spotify.com/v1/me/top/artists?limit=1");
                if (r1.StatusCode == System.Net.HttpStatusCode.Forbidden) return false;

                // test user-read-recently-played
                using var r2 = await http.GetAsync("https://api.spotify.com/v1/me/player/recently-played?limit=1");
                if (r2.StatusCode == System.Net.HttpStatusCode.Forbidden) return false;

                return true;
            }
            catch { return false; }
        }

        private async Task EnsureScopesOrReauthAsync()
        {
            if (_auth == null) return;

            // Nếu đang có token hợp lệ và đủ scope -> giữ nguyên
            if (_auth.IsValid() && await HasScopesAsync(_auth.AccessToken!))
                return;

            // Thiếu scope hoặc token hết hạn -> ép re-consent (show_dialog=true)
            bool ok = await _auth.EnsureTokenAsync();
            if (!ok)
            {
                MessageBox.Show("Spotify re-auth failed.");
            }
        }
    }
}
