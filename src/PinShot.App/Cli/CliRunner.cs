using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using PinShot.App.Capture;
using PinShot.App.Pin;
using PinShot.App.Services;

namespace PinShot.App.Cli;

/// <summary>命令行模式执行器：不创建托盘和热键，执行完命令即退出。</summary>
public static class CliRunner
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    private const int AttachParentProcess = -1;

    private static bool _consoleAttached;

    public static void AttachToParentConsole()
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

    public static void DetachConsole()
    {
        if (!_consoleAttached)
        {
            return;
        }

        _consoleAttached = false;
        FreeConsole();
    }

    public static void WriteLine(string message)
    {
        if (_consoleAttached)
        {
            Console.WriteLine(message);
            Console.Out.Flush();
        }
    }

    /// <summary>执行命令。返回 true 表示已同步完成，调用方应立即 Shutdown。</summary>
    public static bool Run(CliOptions options, Application app)
    {
        switch (options.Command)
        {
            case CliCommand.Help:
                WriteLine(CliOptions.HelpText);
                return true;

            case CliCommand.CaptureFull:
                return CaptureFull(options, app);

            case CliCommand.CaptureRegion:
                if (options.Region.HasValue)
                {
                    return CaptureRegionDirect(options);
                }

                CaptureRegion(options, app);
                return false; // 等待用户选区，完成后由回调 Shutdown

            case CliCommand.Pin:
                Pin(options, app);
                return false; // 贴图窗口关闭后由 ShutdownMode 退出

            default:
                return false;
        }
    }

    private static bool CaptureFull(CliOptions options, Application app)
    {
        try
        {
            if (options.DelayMs > 0)
            {
                Thread.Sleep(options.DelayMs);
            }

            var (snapshot, _) = ScreenCapture.CaptureVirtualScreen();
            Deliver(snapshot, options);
            return true;
        }
        catch (Exception ex)
        {
            WriteLine($"error: {ex.Message}");
            return true;
        }
    }

    private static void CaptureRegion(CliOptions options, Application app)
    {
        try
        {
            if (options.DelayMs > 0)
            {
                Thread.Sleep(options.DelayMs);
            }

            var (snapshot, bounds) = ScreenCapture.CaptureVirtualScreen();
            var overlay = new CaptureOverlay(snapshot, bounds, OverlayPurpose.Capture);

            overlay.Completed += result =>
            {
                try
                {
                    if (result is null)
                    {
                        WriteLine("cancelled");
                        app.Shutdown(2);
                        return;
                    }

                    Deliver(result.Image, options);
                }
                catch (Exception ex)
                {
                    WriteLine($"error: {ex.Message}");
                }

                app.Shutdown(0);
            };

            overlay.Show();
        }
        catch (Exception ex)
        {
            WriteLine($"error: {ex.Message}");
            app.Shutdown(1);
        }
    }

    /// <summary>带 --region 时直接裁剪，不弹选区界面（供脚本 / 自动化调用）。</summary>
    private static bool CaptureRegionDirect(CliOptions options)
    {
        try
        {
            if (options.DelayMs > 0)
            {
                Thread.Sleep(options.DelayMs);
            }

            var (snapshot, bounds) = ScreenCapture.CaptureVirtualScreen();
            var (rx, ry, rw, rh) = options.Region!.Value;

            var x = Math.Clamp(rx - bounds.X, 0, Math.Max(0, snapshot.PixelWidth - 1));
            var y = Math.Clamp(ry - bounds.Y, 0, Math.Max(0, snapshot.PixelHeight - 1));
            var width = Math.Clamp(rw, 1, Math.Max(1, snapshot.PixelWidth - x));
            var height = Math.Clamp(rh, 1, Math.Max(1, snapshot.PixelHeight - y));

            var cropped = new CroppedBitmap(snapshot, new Int32Rect(x, y, width, height));
            cropped.Freeze();

            Deliver(cropped, options);
            return true;
        }
        catch (Exception ex)
        {
            WriteLine($"error: {ex.Message}");
            return true;
        }
    }

    private static void Pin(CliOptions options, Application app)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(options.InputPath) || !File.Exists(options.InputPath))
            {
                WriteLine($"error: 找不到图片 {options.InputPath}");
                app.Shutdown(1);
                return;
            }

            var image = LoadImage(options.InputPath);
            app.ShutdownMode = ShutdownMode.OnLastWindowClose;
            PinManager.Show(image);
            WriteLine("pinned");
        }
        catch (Exception ex)
        {
            WriteLine($"error: {ex.Message}");
            app.Shutdown(1);
        }
    }

    private static BitmapSource LoadImage(string path)
    {
        var fullPath = Path.GetFullPath(path);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 输出结果：
    /// 1) 指定了 --output → 存到该路径；
    /// 2) 指定了 --clipboard → 复制到剪贴板；
    /// 3) 都没给 → 存到设置里的默认目录 + 时间戳文件名，和截图界面里保存的默认路径一致。
    /// </summary>
    private static void Deliver(BitmapSource image, CliOptions options)
    {
        var settings = AppSettings.Load();

        if (!string.IsNullOrWhiteSpace(options.Output))
        {
            var path = Path.GetFullPath(options.Output);
            SaveImage(image, path, settings);
            WriteLine(path);

            if (options.Clipboard)
            {
                Clipboard.SetImage(image);
            }

            return;
        }

        if (options.Clipboard)
        {
            Clipboard.SetImage(image);
            WriteLine("clipboard");
            return;
        }

        var saved = settings.BuildDefaultFilePath();
        SaveImage(image, saved, settings);
        WriteLine(saved);
    }

    /// <summary>按扩展名选择编码器（png / jpg / bmp），JPG 用配置里的质量。</summary>
    private static void SaveImage(BitmapSource image, string path, AppSettings settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = settings.JpegQuality },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };

        encoder.Frames.Add(BitmapFrame.Create(image));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
