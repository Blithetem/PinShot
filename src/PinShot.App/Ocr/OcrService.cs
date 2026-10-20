using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;

namespace PinShot.App.Ocr;

/// <summary>
/// OCR 服务：优先调用在线视觉模型（体积为零、识别质量最高），
/// 不可用时回退 Windows 内置 OCR。
/// 在线服务配置见 %LOCALAPPDATA%\PinShot\ocr.json。
/// </summary>
public static class OcrService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public static bool IsAvailable => true;

    public static string? UnavailableReason => null;

    public static async Task<string> RecognizeAsync(BitmapSource image, double scale = 2.0)
    {
        var settings = OcrSettings.Load();

        // 默认走本地 Windows OCR（快、离线）；在线 OCR 需要在 ocr.json 里显式开启
        if (settings.UseOnline)
        {
            try
            {
                var online = await RecognizeOnlineAsync(image);
                if (!string.IsNullOrWhiteSpace(online))
                {
                    return online;
                }
            }
            catch
            {
                // 在线 OCR 失败时回退本地
            }
        }

        return await RecognizeWithWindowsOcr(image, scale);
    }

    private static async Task<string> RecognizeOnlineAsync(BitmapSource image)
    {
        var settings = OcrSettings.Load();

        // 未配置服务地址/凭据时直接回退本地，不在请求里带空值
        if (string.IsNullOrWhiteSpace(settings.Endpoint) ||
            string.IsNullOrWhiteSpace(settings.ApiKey) ||
            string.IsNullOrWhiteSpace(settings.Model))
        {
            return string.Empty;
        }

        // 控制请求体积：最大宽 1600
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

    private static async Task<string> RecognizeWithWindowsOcr(BitmapSource image, double scale)
    {
        var prepared = Preprocess(image, scale);
        var stream = await ToRandomAccessStreamAsync(prepared);

        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0]);

        if (engine is null)
        {
            throw new InvalidOperationException("OCR 引擎不可用");
        }

        var result = await engine.RecognizeAsync(softwareBitmap);

        var lines = new List<string>();
        foreach (var line in result.Lines)
        {
            lines.Add(NormalizeSpaces(line.Text));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string NormalizeSpaces(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line;
        }

        var builder = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            var current = line[i];

            if (current == ' ' && i > 0 && i < line.Length - 1 &&
                IsCjk(line[i - 1]) && IsCjk(line[i + 1]))
            {
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static bool IsCjk(char value)
        => (value >= 0x4E00 && value <= 0x9FFF) ||
           (value >= 0x3400 && value <= 0x4DBF) ||
           (value >= 0x3000 && value <= 0x303F) ||
           (value >= 0xFF00 && value <= 0xFFEF);

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

    private static async Task<InMemoryRandomAccessStream> ToRandomAccessStreamAsync(BitmapSource image)
    {
        var bytes = EncodePng(image);

        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        return stream;
    }
}