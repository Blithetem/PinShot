using System.IO;
using System.Text.Json;

namespace PinShot.App.Ocr;

/// <summary>在线 OCR 配置：%LOCALAPPDATA%\PinShot\ocr.json。</summary>
/// <remarks>
/// 默认使用本地 Windows OCR。要启用在线 OCR，请自行在本机配置文件里填写
/// Endpoint / ApiKey / Model，并把 UseOnline 改成 true；仓库里不保存任何服务地址与凭据。
/// </remarks>
public sealed class OcrSettings
{
    /// <summary>OpenAI 兼容的接口根地址，例如 https://your-endpoint/v1。</summary>
    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>是否启用在线 OCR（默认关闭，用本地 Windows OCR）。</summary>
    public bool UseOnline { get; set; }

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PinShot", "ocr.json");

    public static OcrSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var settings = JsonSerializer.Deserialize<OcrSettings>(json);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // 配置损坏时用默认值
        }

        return new OcrSettings();
    }

    public static void Save(OcrSettings settings)
    {
        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(ConfigPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}