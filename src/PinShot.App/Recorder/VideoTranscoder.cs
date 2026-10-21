using System.Diagnostics;
using System.IO;

namespace PinShot.App.Recorder;

/// <summary>
/// 录制完成后用 ffmpeg 把 AVI(MJPEG) 转成 H.264 MP4。
/// AVI 没有帧间压缩，体积通常是 MP4 的 10 倍以上；找到 ffmpeg 就自动转，找不到就保持 AVI。
/// </summary>
internal static class VideoTranscoder
{
    private static string? _cached;
    private static bool _searched;

    /// <summary>转码出来的 MP4 相对 AVI 的压缩质量（CRF 越大越小越糊，26 适合屏幕内容）。</summary>
    private const int Crf = 26;

    public static bool IsAvailable => FindFfmpeg() is not null;

    /// <summary>把 AVI 转成 MP4；成功返回 mp4 路径（并删除 AVI），失败返回 null。</summary>
    public static string? TryConvertToMp4(string aviPath)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null || !File.Exists(aviPath))
        {
            return null;
        }

        var mp4Path = Path.ChangeExtension(aviPath, ".mp4");

        try
        {
            if (File.Exists(mp4Path))
            {
                File.Delete(mp4Path);
            }

            var arguments =
                $"-y -loglevel error -i \"{aviPath}\" -c:v libx264 -crf {Crf} -preset veryfast " +
                $"-pix_fmt yuv420p -movflags +faststart -an \"{mp4Path}\"";

            var startInfo = new ProcessStartInfo(ffmpeg, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            // 必须把输出读掉，否则输出缓冲满了会卡住
            _ = process.StandardError.ReadToEnd();
            _ = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(180_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 忽略结束失败
                }

                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(mp4Path))
            {
                return null;
            }

            var mp4Size = new FileInfo(mp4Path).Length;
            var aviSize = new FileInfo(aviPath).Length;

            // 转出来反而更大（比如极短片段），就保留 AVI
            if (mp4Size <= 0 || mp4Size >= aviSize)
            {
                File.Delete(mp4Path);
                return null;
            }

            File.Delete(aviPath);
            return mp4Path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>找 ffmpeg：配置路径 → PATH → 常见安装目录。</summary>
    public static string? FindFfmpeg()
    {
        if (_searched)
        {
            return _cached;
        }

        _searched = true;

        try
        {
            var configured = Services.AppSettings.Current.FfmpegPath;
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                _cached = configured;
                return _cached;
            }

            var fromPath = SearchOnPath();
            if (fromPath is not null)
            {
                _cached = fromPath;
                return _cached;
            }

            _cached = SearchCommonLocations();
            return _cached;
        }
        catch
        {
            return null;
        }
    }

    private static string? SearchOnPath()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        foreach (var directory in pathVariable.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 非法路径，跳过
            }
        }

        return null;
    }

    private static string? SearchCommonLocations()
    {
        var candidates = new[]
        {
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"D:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"D:\Program Files\ffmpeg\bin\ffmpeg.exe",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 带版本号前缀的目录（ffmpeg-master-latest-win64-gpl-shared 这类）
        foreach (var root in new[] { @"D:\Program Files", @"C:\Program Files", @"D:\tools", @"C:\tools" })
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var directory in Directory.GetDirectories(root, "ffmpeg*"))
                {
                    var candidate = Path.Combine(directory, "bin", "ffmpeg.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // 目录不可读，跳过
            }
        }

        return null;
    }
}
