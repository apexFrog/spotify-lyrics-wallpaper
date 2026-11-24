using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace SpotifyLyricsWallpaper.Services;

public sealed class PresetRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, JsonElement> Properties { get; set; } = new();
}

public sealed class PresetSummaryDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Thumbnail { get; set; } = "";
}

public static class PresetManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string PresetsDir
    {
        get
        {
            var dir = Path.Combine(ConfigManager.AppDataDir, "presets");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string JsonPath(string id) => Path.Combine(PresetsDir, $"{SanitizeId(id)}.json");
    private static string ThumbPath(string id) => Path.Combine(PresetsDir, $"{SanitizeId(id)}.jpg");

    private static string SanitizeId(string id)
    {
        var clean = new string(id.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        return string.IsNullOrWhiteSpace(clean) ? Guid.NewGuid().ToString("N")[..12] : clean;
    }

    public static List<PresetSummaryDto> ListSummaries()
    {
        var result = new List<(DateTime Created, PresetSummaryDto Dto)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(PresetsDir, "*.json"))
            {
                try
                {
                    var rec = JsonSerializer.Deserialize<PresetRecord>(File.ReadAllText(file), JsonOptions);
                    if (rec == null || string.IsNullOrWhiteSpace(rec.Id)) continue;

                    var thumb = LoadThumbnailDataUrl(rec.Id);
                    result.Add((rec.CreatedAt, new PresetSummaryDto
                    {
                        Id = rec.Id,
                        Name = string.IsNullOrWhiteSpace(rec.Name) ? "Preset" : rec.Name,
                        CreatedAt = rec.CreatedAt.ToString("O"),
                        Thumbnail = thumb
                    }));
                }
                catch { }
            }
        }
        catch { }

        return result
            .OrderByDescending(x => x.Created)
            .Select(x => x.Dto)
            .ToList();
    }

    public static PresetRecord? LoadPreset(string id)
    {
        try
        {
            var path = JsonPath(id);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<PresetRecord>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static PresetRecord SaveNew(
        string name,
        Dictionary<string, JsonElement> properties,
        byte[]? screenshotPng,
        string? customImagePath)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var cloned = new Dictionary<string, JsonElement>(properties.Count);
        foreach (var kv in properties)
            cloned[kv.Key] = kv.Value.Clone();

        var rec = new PresetRecord
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? $"Preset {DateTime.Now:HH:mm}" : name.Trim(),
            CreatedAt = DateTime.UtcNow,
            Properties = cloned
        };

        File.WriteAllText(JsonPath(id), JsonSerializer.Serialize(rec, JsonOptions));

        if (!string.IsNullOrWhiteSpace(customImagePath) && File.Exists(customImagePath))
            SaveThumbnailFromFile(id, customImagePath);
        else if (screenshotPng is { Length: > 0 })
            SaveThumbnailFromBytes(id, screenshotPng);

        return rec;
    }

    public static bool Overwrite(
        string id,
        Dictionary<string, JsonElement> properties,
        byte[]? screenshotPng)
    {
        var existing = LoadPreset(id);
        if (existing == null) return false;

        var cloned = new Dictionary<string, JsonElement>(properties.Count);
        foreach (var kv in properties)
            cloned[kv.Key] = kv.Value.Clone();

        existing.Properties = cloned;
        File.WriteAllText(JsonPath(id), JsonSerializer.Serialize(existing, JsonOptions));

        // Only replace thumbnail with screenshot if no thumbnail exists yet
        if (!File.Exists(ThumbPath(id)) && screenshotPng is { Length: > 0 })
            SaveThumbnailFromBytes(id, screenshotPng);

        return true;
    }

    public static bool Rename(string id, string newName)
    {
        var existing = LoadPreset(id);
        if (existing == null || string.IsNullOrWhiteSpace(newName)) return false;

        existing.Name = newName.Trim();
        File.WriteAllText(JsonPath(id), JsonSerializer.Serialize(existing, JsonOptions));
        return true;
    }

    public static bool UpdateThumbnail(string id, string imagePath)
    {
        if (!File.Exists(JsonPath(id)) || !File.Exists(imagePath)) return false;
        return SaveThumbnailFromFile(id, imagePath);
    }

    public static bool Delete(string id)
    {
        try
        {
            var jp = JsonPath(id);
            var tp = ThumbPath(id);
            if (File.Exists(jp)) File.Delete(jp);
            if (File.Exists(tp)) File.Delete(tp);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? BuildPreviewDataUrlFromFile(string imagePath)
    {
        try
        {
            if (!File.Exists(imagePath)) return null;
            using var src = Image.FromFile(imagePath);
            var jpeg = EncodeResizedCoverJpeg(src, 360, 225, 84L);
            return "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);
        }
        catch
        {
            return null;
        }
    }

    private static string LoadThumbnailDataUrl(string id)
    {
        try
        {
            var tp = ThumbPath(id);
            if (!File.Exists(tp)) return "";
            var bytes = File.ReadAllBytes(tp);
            return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        }
        catch
        {
            return "";
        }
    }

    private static bool SaveThumbnailFromFile(string id, string imagePath)
    {
        try
        {
            using var src = Image.FromFile(imagePath);
            var jpeg = EncodeResizedCoverJpeg(src, 360, 225, 86L);
            File.WriteAllBytes(ThumbPath(id), jpeg);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool SaveThumbnailFromBytes(string id, byte[] imageBytes)
    {
        try
        {
            using var ms = new MemoryStream(imageBytes);
            using var src = Image.FromStream(ms);
            var jpeg = EncodeResizedCoverJpeg(src, 360, 225, 86L);
            File.WriteAllBytes(ThumbPath(id), jpeg);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] EncodeResizedCoverJpeg(Image src, int targetW, int targetH, long quality)
    {
        // Center-crop to target aspect ratio (object-fit: cover)
        float srcRatio = (float)src.Width / Math.Max(1, src.Height);
        float dstRatio = (float)targetW / targetH;

        RectangleF srcRect;
        if (srcRatio > dstRatio)
        {
            float cropW = src.Height * dstRatio;
            srcRect = new RectangleF((src.Width - cropW) / 2f, 0, cropW, src.Height);
        }
        else
        {
            float cropH = src.Width / dstRatio;
            srcRect = new RectangleF(0, (src.Height - cropH) / 2f, src.Width, cropH);
        }

        using var bmp = new Bitmap(targetW, targetH);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new RectangleF(0, 0, targetW, targetH), srcRect, GraphicsUnit.Pixel);
        }

        using var outMs = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(e => e.FormatID == ImageFormat.Jpeg.Guid);
        if (encoder != null)
        {
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bmp.Save(outMs, encoder, ep);
        }
        else
        {
            bmp.Save(outMs, ImageFormat.Jpeg);
        }
        return outMs.ToArray();
    }
}
