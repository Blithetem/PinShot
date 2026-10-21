using System.Text;
using PinShot.Ocr.Abstractions;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace PinShot.Ocr.WinRt;

/// <summary>
/// 基于系统自带 Windows.Media.Ocr 的离线识别引擎。
/// 这里是唯一引用 WinRT 投影的地方，主程序不引用它。
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    public string Name => "Windows OCR";

    public IReadOnlyList<string> Languages { get; } = LoadLanguages();

    private static IReadOnlyList<string> LoadLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(language => language.LanguageTag)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<string> RecognizeAsync(byte[] pngBytes, string? languageTag = null)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0)
        {
            return string.Empty;
        }

        var engine = CreateEngine(languageTag);
        if (engine is null)
        {
            throw new InvalidOperationException("系统没有可用的 OCR 语言包");
        }

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync();
        var result = await engine.RecognizeAsync(bitmap);

        var lines = new List<string>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            lines.Add(NormalizeSpaces(line.Text));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static OcrEngine? CreateEngine(string? languageTag)
    {
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            try
            {
                var language = new Windows.Globalization.Language(languageTag);
                var byTag = OcrEngine.TryCreateFromLanguage(language);
                if (byTag is not null)
                {
                    return byTag;
                }
            }
            catch
            {
                // 语言标签无效时回退到系统语言
            }
        }

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is not null)
        {
            return engine;
        }

        var available = OcrEngine.AvailableRecognizerLanguages;
        return available.Count > 0 ? OcrEngine.TryCreateFromLanguage(available[0]) : null;
    }

    /// <summary>中文里每个字之间会被插入空格，这里去掉；英文单词之间的空格保留。</summary>
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
}
