using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.Json;
using SpotifyLyricsWallpaper.Forms;
using SpotifyLyricsWallpaper.Services;

namespace SpotifyLyricsWallpaper;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly string _wwwDir;
    private readonly StoredConfig _config;
    private readonly Dictionary<string, JsonElement> _schemaDefaults = new();
    private readonly WallpaperForm _wallpaperForm;
    private readonly AudioCaptureService _audioService;
    private readonly SmtcMediaService _mediaService;
    private readonly DesktopClickForwarder _desktopClickForwarder;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _saveDebounceTimer;
    private readonly System.Windows.Forms.Timer _fullscreenTimer;
    private readonly System.Windows.Forms.Timer _rotationTimer;
    private readonly Random _rng = new();

    private SettingsForm? _settingsForm;
    private ToolStripMenuItem _settingsMenuItem = null!;
    private ToolStripMenuItem _nextPresetMenuItem = null!;
    private ToolStripMenuItem _modeMenuItem = null!;
    private ToolStripMenuItem _pauseMenuItem = null!;
    private ToolStripMenuItem _reloadMenuItem = null!;
    private ToolStripMenuItem _exitMenuItem = null!;
    private bool _userPaused;
    private bool _fullscreenPaused;

    public TrayApplicationContext()
    {
        _wwwDir = Path.Combine(AppContext.BaseDirectory, "www");
        _config = ConfigManager.Load();
        LoadSchemaDefaults();

        _wallpaperForm = new WallpaperForm(_wwwDir);
        _audioService = new AudioCaptureService();
        _mediaService = new SmtcMediaService();
        _desktopClickForwarder = new DesktopClickForwarder();

        _saveDebounceTimer = new System.Windows.Forms.Timer { Interval = 350 };
        _saveDebounceTimer.Tick += (_, _) =>
        {
            _saveDebounceTimer.Stop();
            ConfigManager.Save(_config);
        };

        _fullscreenTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _fullscreenTimer.Tick += CheckFullscreenTick;
        _fullscreenTimer.Start();

        _rotationTimer = new System.Windows.Forms.Timer();
        _rotationTimer.Tick += (_, _) =>
        {
            if (_userPaused || _fullscreenPaused) return;
            if (SwitchToNextPreset())
                _settingsForm?.PushStateToUi();
        };
        ConfigureRotationTimer();

        _trayIcon = CreateTrayIcon();
        ApplyTrayLocalization();

        WireEvents();
        _ = StartAllAsync();
    }

    private void LoadSchemaDefaults()
    {
        try
        {
            var projectJsonPath = Path.Combine(_wwwDir, "project.json");
            if (!File.Exists(projectJsonPath)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(projectJsonPath));
            if (!doc.RootElement.TryGetProperty("general", out var gen) ||
                !gen.TryGetProperty("properties", out var props))
                return;

            foreach (var p in props.EnumerateObject())
            {
                if (p.Value.TryGetProperty("value", out var val))
                {
                    _schemaDefaults[p.Name] = val.Clone();
                }
            }
        }
        catch { }
    }

    private string ResolveUiLang()
    {
        var pref = (_config.App.UiLanguage ?? "auto").ToLowerInvariant();
        if (pref is "ru" or "en" or "zh" or "ja" or "ko") return pref;
        var sys = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return sys is "ru" or "en" or "zh" or "ja" or "ko" ? sys : "en";
    }

    private string TrayText(string key) => (ResolveUiLang(), key) switch
    {
        ("ru", "settings") => "Настройки…",
        ("ru", "nextPreset") => "Следующий пресет",
        ("ru", "modeToDesktop") => "Режим: Окно (переключить на Обои)",
        ("ru", "modeToWindow") => "Режим: Обои (переключить в Окно)",
        ("ru", "pause") => "Пауза",
        ("ru", "resume") => "Продолжить",
        ("ru", "reload") => "Перезагрузить обои",
        ("ru", "exit") => "Выход",

        ("zh", "settings") => "设置…",
        ("zh", "nextPreset") => "下一个预设",
        ("zh", "modeToDesktop") => "模式：窗口（切换为壁纸）",
        ("zh", "modeToWindow") => "模式：壁纸（切换为窗口）",
        ("zh", "pause") => "暂停",
        ("zh", "resume") => "继续",
        ("zh", "reload") => "重新加载壁纸",
        ("zh", "exit") => "退出",

        ("ja", "settings") => "設定…",
        ("ja", "nextPreset") => "次のプリセット",
        ("ja", "modeToDesktop") => "モード: ウィンドウ（壁紙へ切替）",
        ("ja", "modeToWindow") => "モード: 壁紙（ウィンドウへ切替）",
        ("ja", "pause") => "一時停止",
        ("ja", "resume") => "再開",
        ("ja", "reload") => "壁紙を再読み込み",
        ("ja", "exit") => "終了",

        ("ko", "settings") => "설정…",
        ("ko", "nextPreset") => "다음 프리셋",
        ("ko", "modeToDesktop") => "모드: 창 (배경화면으로 전환)",
        ("ko", "modeToWindow") => "모드: 배경화면 (창으로 전환)",
        ("ko", "pause") => "일시 정지",
        ("ko", "resume") => "계속",
        ("ko", "reload") => "배경화면 새로고침",
        ("ko", "exit") => "종료",

        (_, "settings") => "Settings…",
        (_, "nextPreset") => "Next preset",
        (_, "modeToDesktop") => "Mode: Window (switch to Wallpaper)",
        (_, "modeToWindow") => "Mode: Wallpaper (switch to Window)",
        (_, "pause") => "Pause",
        (_, "resume") => "Resume",
        (_, "reload") => "Reload wallpaper",
        (_, "exit") => "Exit",
        _ => key
    };

    private void ApplyTrayLocalization()
    {
        if (_settingsMenuItem == null) return;
        _settingsMenuItem.Text = TrayText("settings");
        _nextPresetMenuItem.Text = TrayText("nextPreset");
        _modeMenuItem.Text = _config.App.DisplayMode == "window"
            ? TrayText("modeToDesktop")
            : TrayText("modeToWindow");
        _pauseMenuItem.Text = _userPaused ? TrayText("resume") : TrayText("pause");
        _reloadMenuItem.Text = TrayText("reload");
        _exitMenuItem.Text = TrayText("exit");
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();

        _settingsMenuItem = new ToolStripMenuItem();
        _settingsMenuItem.Font = new Font(_settingsMenuItem.Font, FontStyle.Bold);
        _settingsMenuItem.Click += (_, _) => ShowSettings();

        _nextPresetMenuItem = new ToolStripMenuItem();
        _nextPresetMenuItem.Click += (_, _) =>
        {
            if (SwitchToNextPreset())
                _settingsForm?.PushStateToUi();
        };

        _modeMenuItem = new ToolStripMenuItem();
        _modeMenuItem.Click += (_, _) => ToggleDisplayMode();

        _pauseMenuItem = new ToolStripMenuItem();
        _pauseMenuItem.Click += (_, _) => ToggleManualPause();

        _reloadMenuItem = new ToolStripMenuItem();
        _reloadMenuItem.Click += (_, _) => _wallpaperForm.ReloadWallpaper();

        _exitMenuItem = new ToolStripMenuItem();
        _exitMenuItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(_settingsMenuItem);
        menu.Items.Add(_nextPresetMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_modeMenuItem);
        menu.Items.Add(_pauseMenuItem);
        menu.Items.Add(_reloadMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitMenuItem);

        var icon = new NotifyIcon
        {
            Icon = CreateAppIcon(),
            Text = "Spotify Lyrics Wallpaper",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => ShowSettings();
        return icon;
    }

    private static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var bgBrush = new LinearGradientBrush(
            new Rectangle(2, 2, 28, 28),
            Color.FromArgb(29, 215, 96),
            Color.FromArgb(16, 130, 210),
            45f);
        g.FillEllipse(bgBrush, 2, 2, 28, 28);

        using var barBrush = new SolidBrush(Color.White);
        g.FillRectangle(barBrush, 9, 14, 3, 9);
        g.FillRectangle(barBrush, 14.5f, 9, 3, 14);
        g.FillRectangle(barBrush, 20, 12, 3, 11);

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    private void WireEvents()
    {
        _wallpaperForm.WebViewReady += () =>
        {
            PushAllWallpaperProperties();
            _wallpaperForm.PostMediaProperties(_mediaService.LastProps);
            _wallpaperForm.PostMediaPlayback(_mediaService.LastPlayback);
            _wallpaperForm.PostMediaTimeline(_mediaService.LastTimeline);
            _wallpaperForm.PostMediaThumbnail(_mediaService.LastThumbnail);
        };

        _wallpaperForm.MediaControlRequested += action => _ = _mediaService.SendMediaCommandAsync(action);
        _wallpaperForm.MediaSeekRequested += sec => _ = _mediaService.SeekToSecondsAsync(sec);

        _desktopClickForwarder.DesktopClicked += (sx, sy) =>
        {
            if (_config.App.DisplayMode == "window") return;
            var screen = DesktopWallpaperHost.ResolveScreen(_config.App.MonitorDeviceName);
            var b = screen.Bounds;
            if (sx < b.Left || sx >= b.Right || sy < b.Top || sy >= b.Bottom) return;
            double nx = (sx - b.Left) / (double)Math.Max(1, b.Width);
            double ny = (sy - b.Top) / (double)Math.Max(1, b.Height);
            _wallpaperForm.DispatchDesktopClick(nx, ny);
        };

        _audioService.SpectrumReady += bands => _wallpaperForm.PostAudioSpectrum(bands);
        _mediaService.PropertiesChanged += p => _wallpaperForm.PostMediaProperties(p);
        _mediaService.TimelineChanged += t => _wallpaperForm.PostMediaTimeline(t);
        _mediaService.PlaybackChanged += pb => _wallpaperForm.PostMediaPlayback(pb);
        _mediaService.ThumbnailChanged += th => _wallpaperForm.PostMediaThumbnail(th);
    }

    private async Task StartAllAsync()
    {
        var screen = DesktopWallpaperHost.ResolveScreen(_config.App.MonitorDeviceName);
        _wallpaperForm.ApplyDisplayMode(_config.App.DisplayMode, screen);

        await _wallpaperForm.InitializeWebViewAsync();
        _audioService.Start(_config.App.AudioDeviceId, _config.App.AudioFps);
        await _mediaService.StartAsync();

        if (!File.Exists(ConfigManager.ConfigFilePath))
        {
            ConfigManager.Save(_config);
            ShowSettings();
        }
    }

    private void ConfigureRotationTimer()
    {
        _rotationTimer.Stop();
        if (!_config.App.PresetRotationEnabled) return;
        int minutes = Math.Clamp(_config.App.PresetRotationMinutes, 1, 1440);
        _rotationTimer.Interval = minutes * 60 * 1000;
        _rotationTimer.Start();
    }

    private bool SwitchToNextPreset()
    {
        var list = PresetManager.ListSummaries();
        if (list.Count == 0) return false;
        if (list.Count == 1) return OnApplyVisualPreset(list[0].Id);

        int curIdx = list.FindIndex(p => p.Id == _config.App.ActivePresetId);
        int nextIdx;
        if (string.Equals(_config.App.PresetRotationOrder, "random", StringComparison.OrdinalIgnoreCase))
        {
            do { nextIdx = _rng.Next(list.Count); } while (nextIdx == curIdx);
        }
        else
        {
            nextIdx = (curIdx + 1) % list.Count;
        }

        return OnApplyVisualPreset(list[nextIdx].Id);
    }

    private void PushAllWallpaperProperties()
    {
        var merged = new Dictionary<string, object?>();
        foreach (var kv in _schemaDefaults)
            merged[kv.Key] = ExtractJsonValue(kv.Value);
        foreach (var kv in _config.Properties)
            merged[kv.Key] = ExtractJsonValue(kv.Value);

        _wallpaperForm.PostProperties(merged);
    }

    private static object? ExtractJsonValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        _ => el.ToString()
    };

    private void OnPropertyChanged(string key, JsonElement val)
    {
        _config.Properties[key] = val.Clone();
        _wallpaperForm.PostProperties(new Dictionary<string, object?> { [key] = ExtractJsonValue(val) });
        ScheduleSave();
    }

    private void OnAppSettingsChanged(AppSettings updated)
    {
        bool displayChanged =
            _config.App.DisplayMode != updated.DisplayMode ||
            _config.App.MonitorDeviceName != updated.MonitorDeviceName;

        bool audioChanged =
            _config.App.AudioDeviceId != updated.AudioDeviceId ||
            _config.App.AudioFps != updated.AudioFps;

        bool rotationChanged =
            _config.App.PresetRotationEnabled != updated.PresetRotationEnabled ||
            _config.App.PresetRotationMinutes != updated.PresetRotationMinutes;

        if (_config.App.Autostart != updated.Autostart)
        {
            ConfigManager.SetAutostart(updated.Autostart);
        }

        _config.App = updated;
        ApplyTrayLocalization();

        if (displayChanged)
        {
            var screen = DesktopWallpaperHost.ResolveScreen(_config.App.MonitorDeviceName);
            _wallpaperForm.ApplyDisplayMode(_config.App.DisplayMode, screen);
        }

        if (audioChanged)
        {
            _audioService.Start(_config.App.AudioDeviceId, _config.App.AudioFps);
        }

        if (rotationChanged)
        {
            ConfigureRotationTimer();
        }

        if (!_config.App.PauseOnFullscreen && _fullscreenPaused)
        {
            _fullscreenPaused = false;
            UpdatePauseState();
        }

        ScheduleSave();
    }

    private void OnResetDefaults()
    {
        _config.Properties.Clear();
        _config.App.ActivePresetId = "";
        PushAllWallpaperProperties();
        ConfigManager.Save(_config);
    }

    private void OnResetSection(string[] keys)
    {
        foreach (var k in keys)
            _config.Properties.Remove(k);
        PushAllWallpaperProperties();
        ConfigManager.Save(_config);
    }

    private void OnImportPreset(Dictionary<string, JsonElement> imported)
    {
        _config.Properties.Clear();
        foreach (var kv in imported)
            _config.Properties[kv.Key] = kv.Value.Clone();
        _config.App.ActivePresetId = "";
        PushAllWallpaperProperties();
        ConfigManager.Save(_config);
    }

    private Dictionary<string, JsonElement> BuildCurrentPropertiesSnapshot()
    {
        var merged = new Dictionary<string, JsonElement>();
        foreach (var kv in _schemaDefaults)
            merged[kv.Key] = kv.Value.Clone();
        foreach (var kv in _config.Properties)
            merged[kv.Key] = kv.Value.Clone();
        return merged;
    }

    private async Task<bool> OnSaveVisualPresetAsync(string name, string? customImagePath)
    {
        byte[]? shot = null;
        if (string.IsNullOrWhiteSpace(customImagePath))
            shot = await _wallpaperForm.CapturePreviewPngAsync();

        var rec = PresetManager.SaveNew(name, BuildCurrentPropertiesSnapshot(), shot, customImagePath);
        _config.App.ActivePresetId = rec.Id;
        ConfigManager.Save(_config);
        return true;
    }

    private bool OnApplyVisualPreset(string id)
    {
        var rec = PresetManager.LoadPreset(id);
        if (rec == null) return false;

        _config.Properties.Clear();
        foreach (var kv in rec.Properties)
            _config.Properties[kv.Key] = kv.Value.Clone();
        _config.App.ActivePresetId = rec.Id;
        PushAllWallpaperProperties();
        ConfigManager.Save(_config);
        return true;
    }

    private async Task<bool> OnOverwriteVisualPresetAsync(string id)
    {
        var shot = await _wallpaperForm.CapturePreviewPngAsync();
        return PresetManager.Overwrite(id, BuildCurrentPropertiesSnapshot(), shot);
    }

    private bool OnRenameVisualPreset(string id, string name) => PresetManager.Rename(id, name);

    private bool OnUpdatePresetThumb(string id, string imagePath) => PresetManager.UpdateThumbnail(id, imagePath);

    private bool OnDeleteVisualPreset(string id)
    {
        var ok = PresetManager.Delete(id);
        if (ok && _config.App.ActivePresetId == id)
        {
            _config.App.ActivePresetId = "";
            ConfigManager.Save(_config);
        }
        return ok;
    }

    private object GetSettingsSnapshot()
    {
        var monitors = Screen.AllScreens.Select((s, i) => new
        {
            deviceName = s.DeviceName,
            label = $"{i + 1}: {s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " ★" : ""),
            primary = s.Primary
        }).ToList();

        var audioDevices = AudioCaptureService.ListOutputDevices().Select(d => new
        {
            id = d.Id,
            name = d.Name + (d.IsDefault ? " ★" : ""),
            isDefault = d.IsDefault
        }).ToList();

        var mergedValues = new Dictionary<string, object?>();
        foreach (var kv in _schemaDefaults)
            mergedValues[kv.Key] = ExtractJsonValue(kv.Value);
        foreach (var kv in _config.Properties)
            mergedValues[kv.Key] = ExtractJsonValue(kv.Value);

        var presets = PresetManager.ListSummaries();

        return new
        {
            app = _config.App,
            values = mergedValues,
            monitors,
            audioDevices,
            presets
        };
    }

    private void ShowSettings()
    {
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(
                _wwwDir,
                GetSettingsSnapshot,
                OnPropertyChanged,
                OnAppSettingsChanged,
                OnResetDefaults,
                OnResetSection,
                OnImportPreset,
                OnSaveVisualPresetAsync,
                OnApplyVisualPreset,
                OnOverwriteVisualPresetAsync,
                OnRenameVisualPreset,
                OnUpdatePresetThumb,
                OnDeleteVisualPreset,
                SwitchToNextPreset);
            _settingsForm.Show();
        }
        else
        {
            if (_settingsForm.WindowState == FormWindowState.Minimized)
                _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.Show();
            _settingsForm.Activate();
            _settingsForm.PushStateToUi();
        }
    }

    private void ToggleDisplayMode()
    {
        var next = _config.App.DisplayMode == "window" ? "desktop" : "window";
        OnAppSettingsChanged(new AppSettings
        {
            MonitorDeviceName = _config.App.MonitorDeviceName,
            DisplayMode = next,
            Autostart = _config.App.Autostart,
            PauseOnFullscreen = _config.App.PauseOnFullscreen,
            AudioDeviceId = _config.App.AudioDeviceId,
            AudioFps = _config.App.AudioFps,
            UiLanguage = _config.App.UiLanguage,
            ActivePresetId = _config.App.ActivePresetId,
            PresetRotationEnabled = _config.App.PresetRotationEnabled,
            PresetRotationMinutes = _config.App.PresetRotationMinutes,
            PresetRotationOrder = _config.App.PresetRotationOrder
        });
        _settingsForm?.PushStateToUi();
    }

    private void ToggleManualPause()
    {
        _userPaused = !_userPaused;
        ApplyTrayLocalization();
        UpdatePauseState();
    }

    private void CheckFullscreenTick(object? sender, EventArgs e)
    {
        if (!_config.App.PauseOnFullscreen || _config.App.DisplayMode == "window")
        {
            if (_fullscreenPaused)
            {
                _fullscreenPaused = false;
                UpdatePauseState();
            }
            return;
        }

        var screen = DesktopWallpaperHost.ResolveScreen(_config.App.MonitorDeviceName);
        var settingsHandle = _settingsForm is { IsDisposed: false, IsHandleCreated: true }
            ? _settingsForm.Handle
            : IntPtr.Zero;

        bool fs = DesktopWallpaperHost.IsFullscreenAppOnScreen(screen, _wallpaperForm.Handle, settingsHandle);
        if (fs != _fullscreenPaused)
        {
            _fullscreenPaused = fs;
            UpdatePauseState();
        }
    }

    private void UpdatePauseState()
    {
        bool paused = _userPaused || _fullscreenPaused;
        _audioService.SetPaused(paused);
        _wallpaperForm.SetPaused(paused);
    }

    private void ScheduleSave()
    {
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    private void ExitApplication()
    {
        _rotationTimer.Stop();
        _fullscreenTimer.Stop();
        _saveDebounceTimer.Stop();
        ConfigManager.Save(_config);

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        _desktopClickForwarder.Dispose();
        _audioService.Dispose();
        _mediaService.Dispose();

        _settingsForm?.Close();
        DesktopWallpaperHost.RestoreDesktopWallpaper(_wallpaperForm);
        _wallpaperForm.ForceClose();

        ExitThread();
    }
}
