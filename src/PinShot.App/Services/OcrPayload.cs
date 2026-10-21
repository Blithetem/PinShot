using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace PinShot.App.Services;

/// <summary>
/// 内嵌 OCR 插件的释放器。
/// 分发单个 exe 时，插件以 zip 形式作为嵌入资源打包；首次启动释放到磁盘，之后直接复用。
/// 如果用户手动把 ocr-plugin 目录放在 exe 旁边，则优先使用那份，不重复释放。
/// </summary>
internal static class OcrPayload
{
    private const string ResourceName = "PinShot.OcrPayload.zip";
    private const string PluginFolderName = "ocr-plugin";
    private const string PluginDllName = "PinShot.Ocr.WinRt.dll";
    private const string MarkerName = ".payload-version";

    /// <summary>插件当前版本；改了插件内容就改这里，触发重新释放。</summary>
    private const string PayloadVersion = "1";

    private static bool _done;

    /// <summary>确保插件可用；没有内嵌资源时静默跳过（精简版 exe）。</summary>
    public static void EnsureAvailable()
    {
        if (_done)
        {
            return;
        }

        _done = true;

        try
        {
            // 1) exe 旁边已经有插件 → 用户自己放的，直接用它
            if (HasPlugin(AppContext.BaseDirectory))
            {
                return;
            }

            // 2) exe 旁边可写 → 释放到旁边，保持"绿色软件"布局
            if (TryExtractTo(AppContext.BaseDirectory))
            {
                return;
            }

            // 3) 只读目录（Program Files 等）→ 释放到 LocalAppData，加载器会兜底去那里找
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PinShot");

            TryExtractTo(local);
        }
        catch
        {
            // 释放失败不影响主程序，OCR 会提示未安装
        }
    }

    /// <summary>备用插件目录（局部应用数据），供加载器兜底。</summary>
    public static string FallbackPluginDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PinShot", PluginFolderName);

    private static bool HasPlugin(string root)
        => File.Exists(Path.Combine(root, PluginFolderName, PluginDllName));

    private static bool TryExtractTo(string root)
    {
        var folder = Path.Combine(root, PluginFolderName);
        var marker = Path.Combine(folder, MarkerName);

        if (File.Exists(Path.Combine(folder, PluginDllName)) &&
            File.Exists(marker) &&
            File.ReadAllText(marker).Trim() == PayloadVersion)
        {
            return true;
        }

        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName);

        if (stream is null)
        {
            // 没有内嵌资源：精简版 exe，不做事
            return false;
        }

        Directory.CreateDirectory(folder);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            // 扁平化：只取文件名，避免 zip 里的目录结构带来意外
            var destination = Path.Combine(folder, entry.Name);
            entry.ExtractToFile(destination, overwrite: true);
        }

        File.WriteAllText(marker, PayloadVersion);
        return true;
    }
}
