using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SpotifyLyricsWallpaper.Services;

namespace SpotifyLyricsWallpaper.Forms;

public sealed class SettingsForm : Form
{
    private readonly WebView2 _webView;
    private readonly string _wwwDir;
    private readonly Func<object> _getStateSnapshot;
    private readonly Action<string, JsonElement> _onPropertyChanged;
    private readonly Action<AppSettings> _onAppSettingsChanged;
    private readonly Action _onResetDefaults;
    private readonly Action<string[]> _onResetSection;
    private readonly Action<Dictionary<string, JsonElement>> _onImportPreset;
    private readonly Func<string, string?, Task<bool>> _onSaveVisualPreset;
    private readonly Func<string, bool> _onApplyVisualPreset;
    private readonly Func<string, Task<bool>> _onOverwriteVisualPreset;
    private readonly Func<string, string, bool> _onRenameVisualPreset;
    private readonly Func<string, string, bool> _onUpdatePresetThumb;
    private readonly Func<string, bool> _onDeleteVisualPreset;
    private readonly Func<bool> _onNextVisualPreset;
    private bool _initialized;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public SettingsForm(
        string wwwDir,
        Func<object> getStateSnapshot,
        Action<string, JsonElement> onPropertyChanged,
        Action<AppSettings> onAppSettingsChanged,
        Action onResetDefaults,
        Action<string[]> onResetSection,
        Action<Dictionary<string, JsonElement>> onImportPreset,
        Func<string, string?, Task<bool>> onSaveVisualPreset,
        Func<string, bool> onApplyVisualPreset,
        Func<string, Task<bool>> onOverwriteVisualPreset,
        Func<string, string, bool> onRenameVisualPreset,
        Func<string, string, bool> onUpdatePresetThumb,
        Func<string, bool> onDeleteVisualPreset,
        Func<bool> onNextVisualPreset)
    {
        _wwwDir = wwwDir;
        _getStateSnapshot = getStateSnapshot;
        _onPropertyChanged = onPropertyChanged;
        _onAppSettingsChanged = onAppSettingsChanged;
        _onResetDefaults = onResetDefaults;
        _onResetSection = onResetSection;
        _onImportPreset = onImportPreset;
        _onSaveVisualPreset = onSaveVisualPreset;
        _onApplyVisualPreset = onApplyVisualPreset;
        _onOverwriteVisualPreset = onOverwriteVisualPreset;
        _onRenameVisualPreset = onRenameVisualPreset;
        _onUpdatePresetThumb = onUpdatePresetThumb;
        _onDeleteVisualPreset = onDeleteVisualPreset;
        _onNextVisualPreset = onNextVisualPreset;

        Text = "Settings — Spotify Lyrics Wallpaper";
        Size = new Size(960, 700);
        MinimumSize = new Size(740, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 18, 22);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(18, 18, 22)
        };
        Controls.Add(_webView);

        Load += async (_, _) =>
        {
            EnableDarkTitleBar();
            await EnsureInitializedAsync();
        };
    }

    private void EnableDarkTitleBar()
    {
        try
        {
            int useDark = 1;
            DwmSetWindowAttribute(Handle, 20, ref useDark, sizeof(int));
        }
        catch { }
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;

        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpotifyLyricsWallpaper", "WebView2");
        Directory.CreateDirectory(userDataDir);

        var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
        await _webView.EnsureCoreWebView2Async(env);

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;

        core.SetVirtualHostNameToFolderMapping(
            "wallpaper.local",
            _wwwDir,
            CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;
        core.Navigate("https://wallpaper.local/settings.html");
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "ready":
                    PushStateToUi();
                    break;

                case "setWindowTitle":
                {
                    var title = root.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(title)) Text = title;
                    break;
                }

                case "setProperty":
                {
                    var key = root.GetProperty("key").GetString();
                    var val = root.GetProperty("value").Clone();
                    if (!string.IsNullOrWhiteSpace(key))
                        _onPropertyChanged(key, val);
                    break;
                }

                case "setAppSettings":
                {
                    var appJson = root.GetProperty("app").GetRawText();
                    var app = JsonSerializer.Deserialize<AppSettings>(appJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (app != null)
                        _onAppSettingsChanged(app);
                    break;
                }

                case "pickFile":
                {
                    var propKey = root.GetProperty("key").GetString() ?? "";
                    using var dlg = new OpenFileDialog
                    {
                        Title = "Select image",
                        Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|All files|*.*"
                    };
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        var val = JsonSerializer.SerializeToElement(dlg.FileName);
                        _onPropertyChanged(propKey, val);
                        SendToUi(new { type = "filePicked", key = propKey, path = dlg.FileName });
                    }
                    break;
                }

                case "pickPresetImage":
                {
                    var presetId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    using var dlg = new OpenFileDialog
                    {
                        Title = "Select preset thumbnail",
                        Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|All files|*.*"
                    };
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        if (!string.IsNullOrWhiteSpace(presetId))
                        {
                            if (_onUpdatePresetThumb(presetId, dlg.FileName))
                            {
                                PushStateToUi();
                                SendToUi(new { type = "toastKey", key = "toastThumbUpdated" });
                            }
                        }
                        else
                        {
                            var preview = PresetManager.BuildPreviewDataUrlFromFile(dlg.FileName) ?? "";
                            SendToUi(new { type = "presetImagePicked", path = dlg.FileName, preview });
                        }
                    }
                    break;
                }

                case "saveVisualPreset":
                {
                    var name = root.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    var customPath = root.TryGetProperty("customImagePath", out var pEl) ? pEl.GetString() : null;
                    if (await _onSaveVisualPreset(name, customPath))
                    {
                        PushStateToUi();
                        SendToUi(new { type = "toastKey", key = "toastPresetSaved" });
                    }
                    break;
                }

                case "applyVisualPreset":
                {
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(id) && _onApplyVisualPreset(id))
                    {
                        PushStateToUi();
                        SendToUi(new { type = "toastKey", key = "toastPresetApplied" });
                    }
                    break;
                }

                case "overwriteVisualPreset":
                {
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(id) && await _onOverwriteVisualPreset(id))
                    {
                        PushStateToUi();
                        SendToUi(new { type = "toastKey", key = "toastPresetUpdated" });
                    }
                    break;
                }

                case "renameVisualPreset":
                {
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var name = root.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(id) && _onRenameVisualPreset(id, name))
                    {
                        PushStateToUi();
                        SendToUi(new { type = "toastKey", key = "toastPresetRenamed" });
                    }
                    break;
                }

                case "deleteVisualPreset":
                {
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(id) && _onDeleteVisualPreset(id))
                    {
                        PushStateToUi();
                        SendToUi(new { type = "toastKey", key = "toastPresetDeleted" });
                    }
                    break;
                }

                case "nextVisualPreset":
                {
                    if (_onNextVisualPreset())
                    {
                        PushStateToUi();
                        SendToUi(new { type = "toastKey", key = "toastPresetApplied" });
                    }
                    break;
                }

                case "resetDefaults":
                    _onResetDefaults();
                    PushStateToUi();
                    break;

                case "resetSection":
                {
                    if (root.TryGetProperty("keys", out var kEl) && kEl.ValueKind == JsonValueKind.Array)
                    {
                        var keys = kEl.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => x.GetString()!)
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .ToArray();
                        if (keys.Length > 0)
                        {
                            _onResetSection(keys);
                            PushStateToUi();
                            SendToUi(new { type = "toastKey", key = "toastSectionReset" });
                        }
                    }
                    break;
                }

                case "exportPreset":
                {
                    using var dlg = new SaveFileDialog
                    {
                        Title = "Export preset",
                        Filter = "JSON preset|*.json",
                        FileName = "spotify-lyrics-preset.json"
                    };
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        var cfg = ConfigManager.Load();
                        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(cfg.Properties,
                            new JsonSerializerOptions { WriteIndented = true }));
                        SendToUi(new { type = "toastKey", key = "toastPresetSaved" });
                    }
                    break;
                }

                case "importPreset":
                {
                    using var dlg = new OpenFileDialog
                    {
                        Title = "Import preset",
                        Filter = "JSON preset|*.json"
                    };
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        var json = File.ReadAllText(dlg.FileName);
                        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                        if (dict != null)
                        {
                            _onImportPreset(dict);
                            PushStateToUi();
                            SendToUi(new { type = "toastKey", key = "toastPresetApplied" });
                        }
                    }
                    break;
                }
            }
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    public void PushStateToUi()
    {
        SendToUi(new { type = "init", state = _getStateSnapshot() });
    }

    private void SendToUi(object payload)
    {
        if (IsDisposed || _webView.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        _webView.CoreWebView2.PostWebMessageAsJson(json);
    }
}
