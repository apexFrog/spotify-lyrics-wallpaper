using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SpotifyLyricsWallpaper.Services;

namespace SpotifyLyricsWallpaper.Forms;

public sealed class WallpaperForm : Form
{
    private readonly WebView2 _webView;
    private readonly string _wwwDir;
    private readonly StringBuilder _audioJson = new(2048);
    private bool _webReady;
    private bool _isDesktopMode = true;
    private bool _allowClose;

    public event Action? WebViewReady;
    public event Action<string>? MediaControlRequested;
    public event Action<double>? MediaSeekRequested;

    public WallpaperForm(string wwwDir)
    {
        _wwwDir = wwwDir;

        Text = "Spotify Lyrics Wallpaper";
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.Black
        };
        Controls.Add(_webView);
    }

    public async Task InitializeWebViewAsync()
    {
        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpotifyLyricsWallpaper", "WebView2");
        Directory.CreateDirectory(userDataDir);

        var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
        await _webView.EnsureCoreWebView2Async(env);

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        core.SetVirtualHostNameToFolderMapping(
            "wallpaper.local",
            _wwwDir,
            CoreWebView2HostResourceAccessKind.Allow);

        // Serve local user background/idle images cleanly without CORS/file:// restrictions
        core.AddWebResourceRequestedFilter("https://wallpaper.local/localfile*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnLocalFileRequested;

        // Inject Wallpaper Engine compatibility bridge before index.html runs
        var bridgePath = Path.Combine(_wwwDir, "bridge.js");
        if (File.Exists(bridgePath))
        {
            var bridgeJs = await File.ReadAllTextAsync(bridgePath);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(bridgeJs);
        }

        core.WebMessageReceived += OnWebMessageReceived;

        core.NavigationCompleted += (_, _) =>
        {
            _webReady = true;
            WebViewReady?.Invoke();
        };

        core.Navigate("https://wallpaper.local/index.html");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var tEl))
            {
                var msgType = tEl.GetString();
                if (msgType == "mediaControl")
                {
                    var action = root.TryGetProperty("action", out var aEl) ? aEl.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(action))
                        MediaControlRequested?.Invoke(action);
                }
                else if (msgType == "mediaSeek" && root.TryGetProperty("position", out var pEl) && pEl.TryGetDouble(out var sec))
                {
                    MediaSeekRequested?.Invoke(sec);
                }
            }
        }
        catch { }
    }

    public void DispatchDesktopClick(double nx, double ny)
    {
        if (!_webReady || IsDisposed) return;
        var json = JsonSerializer.Serialize(new { type = "desktopClick", nx, ny });
        PostJsonSafe(json);
    }

    private void OnLocalFileRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            var query = HttpUtility.ParseQueryString(uri.Query);
            var filePath = query["path"];

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    null, 404, "Not Found", "Access-Control-Allow-Origin: *");
                return;
            }

            var bytes = File.ReadAllBytes(filePath);
            var ms = new MemoryStream(bytes);
            string mime = Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };

            e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                ms, 200, "OK",
                $"Content-Type: {mime}\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-cache");
        }
        catch
        {
            e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                null, 500, "Error", "Access-Control-Allow-Origin: *");
        }
    }

    public void ApplyDisplayMode(string mode, Screen screen)
    {
        _isDesktopMode = !string.Equals(mode, "window", StringComparison.OrdinalIgnoreCase);
        if (_isDesktopMode)
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            if (!Visible) Show();
            DesktopWallpaperHost.AttachToDesktop(this, screen);
        }
        else
        {
            DesktopWallpaperHost.DetachFromDesktop(this, screen);
            ShowInTaskbar = true;
            if (!Visible) Show();
            Activate();
        }
    }

    public void ReloadWallpaper()
    {
        if (_webReady && _webView.CoreWebView2 != null)
        {
            _webView.CoreWebView2.Reload();
        }
    }

    public async Task<byte[]?> CapturePreviewPngAsync()
    {
        if (!_webReady || IsDisposed || _webView.CoreWebView2 == null) return null;
        try
        {
            if (InvokeRequired)
            {
                var tcs = new TaskCompletionSource<byte[]?>();
                BeginInvoke(async () =>
                {
                    try { tcs.TrySetResult(await CapturePreviewCoreAsync()); }
                    catch { tcs.TrySetResult(null); }
                });
                return await tcs.Task;
            }
            return await CapturePreviewCoreAsync();
        }
        catch
        {
            return null;
        }
    }

    private async Task<byte[]?> CapturePreviewCoreAsync()
    {
        if (IsDisposed || _webView.CoreWebView2 == null) return null;
        using var ms = new MemoryStream();
        await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
        return ms.ToArray();
    }

    public void PostProperties(Dictionary<string, object?> props)
    {
        if (!_webReady || IsDisposed) return;

        // Convert to Wallpaper Engine format: { propName: { value: ... } }
        var wrapped = new Dictionary<string, object>(props.Count);
        foreach (var kv in props)
        {
            wrapped[kv.Key] = new { value = kv.Value };
        }

        var json = JsonSerializer.Serialize(new { type = "properties", data = wrapped });
        PostJsonSafe(json);
    }

    public void PostMediaProperties(MediaPropsPayload p)
    {
        if (!_webReady || IsDisposed) return;
        var json = JsonSerializer.Serialize(new
        {
            type = "mediaProperties",
            data = new { title = p.Title, artist = p.Artist, albumTitle = p.AlbumTitle }
        });
        PostJsonSafe(json);
    }

    public void PostMediaTimeline(MediaTimelinePayload t)
    {
        if (!_webReady || IsDisposed) return;
        var json = JsonSerializer.Serialize(new
        {
            type = "mediaTimeline",
            data = new { position = t.Position, duration = t.Duration }
        });
        PostJsonSafe(json);
    }

    public void PostMediaPlayback(MediaPlaybackPayload pb)
    {
        if (!_webReady || IsDisposed) return;
        var json = JsonSerializer.Serialize(new
        {
            type = "mediaPlayback",
            data = new { state = pb.State }
        });
        PostJsonSafe(json);
    }

    public void PostMediaThumbnail(MediaThumbnailPayload th)
    {
        if (!_webReady || IsDisposed) return;
        var json = JsonSerializer.Serialize(new
        {
            type = "mediaThumbnail",
            data = new { thumbnail = th.Thumbnail }
        });
        PostJsonSafe(json);
    }

    public void PostAudioSpectrum(float[] bands)
    {
        if (!_webReady || IsDisposed) return;

        string json;
        lock (_audioJson)
        {
            _audioJson.Clear();
            _audioJson.Append("{\"type\":\"audio\",\"data\":[");
            for (int i = 0; i < bands.Length; i++)
            {
                if (i > 0) _audioJson.Append(',');
                _audioJson.Append(bands[i].ToString("0.000", CultureInfo.InvariantCulture));
            }
            _audioJson.Append("]}");
            json = _audioJson.ToString();
        }

        PostJsonSafe(json);
    }

    public void SetPaused(bool paused)
    {
        if (!_webReady || IsDisposed) return;
        PostJsonSafe(paused ? "{\"type\":\"paused\",\"value\":true}" : "{\"type\":\"paused\",\"value\":false}");
    }

    private void PostJsonSafe(string json)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(() =>
                {
                    if (!IsDisposed && _webView.CoreWebView2 != null)
                        _webView.CoreWebView2.PostWebMessageAsJson(json);
                });
            }
            else
            {
                _webView.CoreWebView2?.PostWebMessageAsJson(json);
            }
        }
        catch { }
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // In window mode, closing hides/returns to desktop instead of killing the tray app
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
