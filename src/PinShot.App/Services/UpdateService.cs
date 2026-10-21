using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinShot.App.Services;

/// <summary>远程 version.json 的描述。</summary>
internal sealed class UpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>相对路径或完整 URL；相对路径基于 version.json 所在目录。</summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// 自更新：从更新目录读取 version.json，比版本号更高就下载新版 exe 并替换。
/// 更新目录：http://dapps.tpo.xzoa.com/D%3A/work/pinshot/
/// </summary>
internal static class UpdateService
{
    /// <summary>更新清单地址（下载机 D:\work\pinshot）。</summary>
    public const string ManifestUrl = "http://dapps.tpo.xzoa.com/D%3A/work/pinshot/version.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>当前程序版本（与 csproj 的 Version 一致）。</summary>
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>检查更新；返回 null 表示已是最新或检查失败。</summary>
    public static async Task<UpdateManifest?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = CreateClient(TimeSpan.FromSeconds(12));
            var json = await http.GetStringAsync(ManifestUrl, cancellationToken);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);

            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                return null;
            }

            if (!Version.TryParse(manifest.Version, out var remote) ||
                !Version.TryParse(CurrentVersion, out var local))
            {
                return null;
            }

            return remote > local ? manifest : null;
        }
        catch
        {
            // 网络不通、服务器没有清单都当作"没有更新"
            return null;
        }
    }

    /// <summary>下载新版 exe 到临时目录，返回本地路径。</summary>
    public static async Task<string> DownloadAsync(UpdateManifest manifest,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var url = ResolveFileUrl(manifest.File);
        var directory = Path.Combine(Path.GetTempPath(), "PinShot-update");
        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, "PinShot.new.exe");
        if (File.Exists(target))
        {
            File.Delete(target);
        }

        using var http = CreateClient(TimeSpan.FromMinutes(10));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? manifest.Size;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = File.Create(target))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;

                if (total > 0)
                {
                    progress?.Report((double)received / total);
                }
            }
        }

        // 校验完整性，避免下载到半截文件
        if (!string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            var actual = await HashFileAsync(target);
            if (!string.Equals(actual, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                throw new InvalidOperationException("下载的文件校验失败，可能没下载完整");
            }
        }

        return target;
    }

    /// <summary>
    /// 用批处理脚本完成替换：等待本进程退出 → 备份旧 exe → 覆盖 → 启动新版。
    /// Windows 不能替换正在运行的 exe，只能交给外部脚本。
    /// </summary>
    public static void Apply(string newExePath)
    {
        var currentExe = Environment.ProcessPath
                         ?? throw new InvalidOperationException("拿不到当前程序路径");

        var directory = Path.Combine(Path.GetTempPath(), "PinShot-update");
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "apply.cmd");
        var backup = currentExe + ".old";

        var content = $"""
            @echo off
            ping 127.0.0.1 -n 3 >nul
            if exist "{backup}" del /f /q "{backup}" >nul 2>nul
            move /y "{currentExe}" "{backup}" >nul 2>nul
            copy /y "{newExePath}" "{currentExe}" >nul 2>nul
            if exist "{currentExe}" (
                start "" "{currentExe}"
            ) else (
                move /y "{backup}" "{currentExe}" >nul 2>nul
                start "" "{currentExe}"
            )
            del /f /q "%~f0" >nul 2>nul
            """;

        // 批处理用系统 ANSI 编码写，中文路径才不会乱码
        File.WriteAllText(script, content, Encoding.Default);

        var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };

        System.Diagnostics.Process.Start(startInfo);
    }

    private static string ResolveFileUrl(string file)
    {
        if (Uri.TryCreate(file, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        var baseUri = new Uri(ManifestUrl);
        return new Uri(baseUri, file).ToString();
    }

    private static HttpClient CreateClient(TimeSpan timeout)
        => new() { Timeout = timeout };

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
