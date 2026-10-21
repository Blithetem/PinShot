using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using PinShot.App.Capture;
using PinShot.App.Services;

namespace PinShot.App.Cli;

/// <summary>
/// 不初始化 WPF 的命令行快速路径。
/// 截图类命令只需要 GDI + System.Drawing，走这里可以省掉约 250ms 的 WPF 启动开销。
/// <see cref="CliCommand.Pin"/> 和交互式选区需要 WPF 窗口，仍然交给 <see cref="CliRunner"/>。
/// </summary>
internal static class CliFastPath
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    private const int AttachParentProcess = -1;

    private static bool _consoleAttached;

    /// <summary>返回 true 表示命令已经处理完，调用方应直接退出进程。</summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;

        var options = CliOptions.Parse(args);
        if (options.Command == CliCommand.None)
        {
            return false;
        }

        // 需要 WPF 窗口的命令留给正常启动流程
        if (options.Command == CliCommand.Pin)
        {
            return false;
        }

        if (options.Command == CliCommand.CaptureRegion && options.Region is null)
        {
            return false;
        }

        AttachToParentConsole();

        switch (options.Command)
        {
            case CliCommand.Help:
                WriteLine(CliOptions.HelpText);
                break;

            case CliCommand.CaptureFull:
                exitCode = CaptureFull(options);
                break;

            case CliCommand.CaptureRegion:
                exitCode = CaptureRegion(options);
                break;
        }

        return true;
    }

    public static void DetachConsole()
    {
        if (!_consoleAttached)
        {
            return;
        }

        _consoleAttached = false;
        FreeConsole();
    }

    private static int CaptureFull(CliOptions options)
    {
        try
        {
            if (options.DelayMs > 0)
            {
                Thread.Sleep(options.DelayMs);
            }

            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var bitmap = CaptureBitmap(new Rectangle(bounds.Left, bounds.Top,
                bounds.Width, bounds.Height));

            return Deliver(bitmap, options);
        }
        catch (Exception ex)
        {
            WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int CaptureRegion(CliOptions options)
    {
        try
        {
            if (options.DelayMs > 0)
            {
                Thread.Sleep(options.DelayMs);
            }

            var (x, y, width, height) = options.Region!.Value;
            using var bitmap = CaptureBitmap(new Rectangle(x, y, width, height));

            return Deliver(bitmap, options);
        }
        catch (Exception ex)
        {
            WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>用与主流程相同的 GDI 抓取（BitBlt + CAPTUREBLT + 黑块重试），但输出 System.Drawing.Bitmap。</summary>
    private static Bitmap CaptureBitmap(Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentException("截图区域必须是正数宽高");
        }

        var pixels = new byte[region.Width * region.Height * 4];
        if (!GdiCapture.TryCapture(region, pixels))
        {
            throw new InvalidOperationException("抓屏失败");
        }

        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, region.Width, region.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    /// <summary>
    /// 输出结果：
    /// 1) 指定了 --output → 存到该路径；
    /// 2) 指定了 --clipboard → 复制到剪贴板；
    /// 3) 都没给 → 存到设置里的默认目录 + 时间戳文件名，和截图界面保存的默认路径一致。
    /// </summary>
    private static int Deliver(Bitmap bitmap, CliOptions options)
    {
        var settings = AppSettings.Load();

        if (!string.IsNullOrWhiteSpace(options.Output))
        {
            var path = Path.GetFullPath(options.Output);
            SaveBitmap(bitmap, path, settings);
            WriteLine(path);

            if (options.Clipboard)
            {
                System.Windows.Forms.Clipboard.SetImage(bitmap);
            }

            return 0;
        }

        if (options.Clipboard)
        {
            System.Windows.Forms.Clipboard.SetImage(bitmap);
            WriteLine("clipboard");
            return 0;
        }

        var saved = settings.BuildDefaultFilePath();
        SaveBitmap(bitmap, saved, settings);
        WriteLine(saved);
        return 0;
    }

    /// <summary>按扩展名选择编码器（png / jpg / bmp），JPG 用配置里的质量。</summary>
    private static void SaveBitmap(Bitmap bitmap, string path, AppSettings settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".jpg":
            case ".jpeg":
                var encoder = ImageCodecInfo.GetImageEncoders()
                    .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
                using (var parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(Encoder.Quality,
                        (long)Math.Clamp(settings.JpegQuality, 10, 100));
                    bitmap.Save(path, encoder, parameters);
                }

                break;

            case ".bmp":
                bitmap.Save(path, ImageFormat.Bmp);
                break;

            default:
                bitmap.Save(path, ImageFormat.Png);
                break;
        }
    }

    private static void AttachToParentConsole()
    {
        if (_consoleAttached)
        {
            return;
        }

        if (AttachConsole(AttachParentProcess))
        {
            _consoleAttached = true;

            // 让中文路径在 CMD / PowerShell 里正常显示
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch
            {
                // 控制台不支持时忽略
            }
        }
    }

    private static void WriteLine(string message)
    {
        if (_consoleAttached)
        {
            Console.WriteLine(message);
            Console.Out.Flush();
        }
    }
}
