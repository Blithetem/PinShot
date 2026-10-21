using System.IO;
using System.Text.Json;

namespace PinShot.App.Services;

/// <summary>
/// 用户配置：%LOCALAPPDATA%\PinShot\settings.json。
/// 热键、保存目录/格式、截图默认行为都在这里，缺省值与旧行为保持一致。
/// </summary>
public sealed class AppSettings
{
    // ---- 全局热键 ----
    public string CaptureHotkey { get; set; } = "Ctrl+Shift+S";

    public string PinHotkey { get; set; } = "Ctrl+2";

    public string RestorePinHotkey { get; set; } = "Ctrl+3";

    public string RecordHotkey { get; set; } = "Ctrl+Shift+R";

    // ---- 保存 ----
    /// <summary>截图保存目录；空表示用系统图片目录。</summary>
    public string SaveDirectory { get; set; } = string.Empty;

    /// <summary>默认格式：png / jpg / bmp。</summary>
    public string DefaultFormat { get; set; } = "png";

    public int JpegQuality { get; set; } = 90;

    /// <summary>上次保存到的目录，用于下次打开对话框时定位。</summary>
    public string LastSaveDirectory { get; set; } = string.Empty;

    // ---- 截图行为 ----
    /// <summary>新截图默认是否包含鼠标指针。</summary>
    public bool CaptureWithCursor { get; set; }

    /// <summary>新截图默认圆角半径（0 表示关闭）。</summary>
    public int DefaultCornerRadius { get; set; }

    /// <summary>长截图最大高度像素。</summary>
    public int LongShotMaxHeight { get; set; } = 12000;

    /// <summary>当前生效的配置实例。</summary>
    public static AppSettings Current { get; private set; } = new();

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PinShot", "settings.json");

    /// <summary>从磁盘加载（不存在或损坏时用默认值）。</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath));
                if (settings is not null)
                {
                    settings.Normalize();
                    Current = settings;
                    return settings;
                }
            }
        }
        catch
        {
            // 配置损坏时退回默认值
        }

        Current = new AppSettings();
        return Current;
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 写盘失败不影响使用
        }
    }

    /// <summary>把越界或无法识别的值收敛到安全范围。</summary>
    private void Normalize()
    {
        CaptureHotkey = NormalizeHotkey(CaptureHotkey, "Ctrl+Shift+S");
        PinHotkey = NormalizeHotkey(PinHotkey, "Ctrl+2");
        RestorePinHotkey = NormalizeHotkey(RestorePinHotkey, "Ctrl+3");
        RecordHotkey = NormalizeHotkey(RecordHotkey, "Ctrl+Shift+R");

        DefaultFormat = DefaultFormat?.Trim().ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "jpg",
            "bmp" => "bmp",
            _ => "png",
        };

        JpegQuality = Math.Clamp(JpegQuality, 10, 100);
        DefaultCornerRadius = Math.Clamp(DefaultCornerRadius, 0, 64);
        LongShotMaxHeight = Math.Clamp(LongShotMaxHeight, 1000, 50000);
    }

    private static string NormalizeHotkey(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>解析后的截图保存目录（保证存在）。</summary>
    public string ResolveSaveDirectory()
    {
        var directory = SaveDirectory;

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            directory = Path.GetTempPath();
        }

        return directory;
    }
}
