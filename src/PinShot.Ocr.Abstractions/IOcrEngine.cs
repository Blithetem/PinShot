namespace PinShot.Ocr.Abstractions;

/// <summary>
/// OCR 引擎插件契约。主程序只依赖本接口，不引用任何 WinRT 投影，
/// 因此主程序体积可以做到很小；真正的识别实现放在可选插件里。
/// </summary>
public interface IOcrEngine
{
    /// <summary>引擎名称，用于界面提示（例如 "Windows OCR"）。</summary>
    string Name { get; }

    /// <summary>可用的识别语言标签，例如 "zh-Hans-CN"。</summary>
    IReadOnlyList<string> Languages { get; }

    /// <summary>
    /// 识别一张 PNG 图片中的文字。
    /// </summary>
    /// <param name="pngBytes">PNG 编码后的图片字节。</param>
    /// <param name="languageTag">语言标签；null 表示按系统语言自动选择。</param>
    /// <returns>识别出的文字，按行分隔。</returns>
    Task<string> RecognizeAsync(byte[] pngBytes, string? languageTag = null);
}
