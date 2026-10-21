using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;

namespace PinShot.App.Ocr;

/// <summary>
/// OCR 服务：优先在线视觉模型（可选），否则走本地 OCR 插件。
/// 本地识别实现在 ocr-plugin\ 目录（可选安装），主程序本身不含 WinRT 依赖。
/// </summary>
public static class OcrService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>是否具备识别能力（在线已开启或插件已安装）。</summary>
    public static bool IsAvailable
    {
        get
        {
            var settings = OcrSettings.Load();
            if (settings.UseOnline && !string.IsNullOrWhiteSpace(settings.Endpoint))
            {
                return true;
            }

            return OcrPluginLoader.Engine is not null;
        }
    }

    /// <summary>不可用原因，用于界面提示。</summary>
    public static string? UnavailableReason => IsAvailable ? null : OcrPluginLoader.Error;

    public static async Task<string> RecognizeAsync(BitmapSource image, double scale = 2.0)
    {
        var settings = OcrSettings.Load();

        // 在线识别优先级更高（准确率好），失败时静默回退本地插件
        if (settings.UseOnline)
        {
            try
            {
                var online = await RecognizeOnlineAsync(image, settings);
                if (!string.IsNullOrWhiteSpace(online))
                {
                    return online;
                }
            }
            catch
            {
                // 在线失败时回退本地
            }
        }

        var engine = OcrPluginLoader.Engine;
        if (engine is null)
        {
            throw new InvalidOperationException(
                "未安装本地 OCR 插件。把插件目录放到程序下的 ocr-plugin\\ 即可启用离线识别，");
        }

        var prepared = Preprocess(image, scale);
        var pngBytes = EncodePng(prepared);
        return await engine.RecognizeAsync(pngBytes);
    }

    private static async Task<string> RecognizeOnlineAsync(BitmapSource image, OcrSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint) ||
            string.IsNullOrWhiteSpace(settings.ApiKey) ||
            string.IsNullOrWhiteSpace(settings.Model))
        {
            return string.Empty;
        }

        var prepared = LimitWidth(image, 1600);
        var base64 = Convert.ToBase64String(EncodePng(prepared));

        var request = new
        {
            model = settings.Model,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "提取图片中的全部文字，保持原有换行和段落结构，只输出文字内容，不要任何解释。" },
                        new { type = "image_url", image_url = new { url = $"data:image/png;base64,{base64}" } },
                    },
                },
            },
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        using var message = new HttpRequestMessage(HttpMethod.Post,
            settings.Endpoint.TrimEnd('/') + "/chat/completions")
        {
            Content = content,
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        using var response = await Http.SendAsync(message);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        var choice = document.RootElement.GetProperty("choices")[0];
        var text = choice.GetProperty("message").GetProperty("content").GetString();

        return text?.Trim() ?? string.Empty;
    }

    private static BitmapSource LimitWidth(BitmapSource image, double maxWidth)
    {
        if (image.PixelWidth <= maxWidth)
        {
            return image;
        }

        var scale = maxWidth / image.PixelWidth;
        var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(WpfBitmapFrame.Create(image));

        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    private static BitmapSource Preprocess(BitmapSource image, double scale)
    {
        if (scale <= 1.05)
        {
            return image;
        }

        const double maxWidth = 3500;
        var effective = scale;
        if (image.PixelWidth * effective > maxWidth)
        {
            effective = Math.Max(1.0, maxWidth / image.PixelWidth);
        }

        if (effective <= 1.05)
        {
            return image;
        }

        var scaled = new TransformedBitmap(image, new ScaleTransform(effective, effective));
        scaled.Freeze();
        return scaled;
    }
}
