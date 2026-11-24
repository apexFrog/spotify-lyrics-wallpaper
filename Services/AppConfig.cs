using System.Text.Json;
using Microsoft.Win32;

namespace SpotifyLyricsWallpaper.Services;

public sealed class AppSettings
{
    public string MonitorDeviceName { get; set; } = ""; // empty = Primary screen
    public string DisplayMode { get; set; } = "desktop"; // "desktop" or "window"
    public bool Autostart { get; set; } = false;
    public bool PauseOnFullscreen { get; set; } = true;
    public string AudioDeviceId { get; set; } = ""; // empty = Default output device
    public int AudioFps { get; set; } = 30;
    public string UiLanguage { get; set; } = "auto"; // auto, ru, en, zh, ja, ko
    public string ActivePresetId { get; set; } = "";
    public bool PresetRotationEnabled { get; set; } = false;
    public int PresetRotationMinutes { get; set; } = 15;
    public string PresetRotationOrder { get; set; } = "sequential"; // "sequential" or "random"
}

public sealed class StoredConfig
{
    public AppSettings App { get; set; } = new();
    public Dictionary<string, JsonElement> Properties { get; set; } = new();
}

public static class ConfigManager
{
    private const string AutostartKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutostartValueName = "SpotifyLyricsWallpaper";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string AppDataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SpotifyLyricsWallpaper");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string ConfigFilePath => Path.Combine(AppDataDir, "config.json");

    public static StoredConfig Load()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var cfg = JsonSerializer.Deserialize<StoredConfig>(json, JsonOptions);
                if (cfg != null)
                {
                    cfg.App ??= new AppSettings();
                    cfg.Properties ??= new Dictionary<string, JsonElement>();
                    cfg.App.Autostart = IsAutostartEnabled();
                    return cfg;
                }
            }
        }
        catch
        {
            // Fallback to default config on corrupt file
        }

        var fresh = new StoredConfig();
        fresh.App.Autostart = IsAutostartEnabled();
        return fresh;
    }

    public static void Save(StoredConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigFilePath, json);
        }
        catch
        {
            // Ignore transient IO errors
        }
    }

    public static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutostartKeyPath, false);
            return key?.GetValue(AutostartValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutostart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutostartKeyPath, true);
            if (key == null) return;
            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(AutostartValueName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(AutostartValueName, false);
            }
        }
        catch
        {
            // Ignore registry errors
        }
    }
}
