using System.IO;
using System.Windows;
using PinShot.App.Cli;

namespace PinShot.App;

/// <summary>
/// 自定义入口：先跑不依赖 WPF 的命令行快速路径，只有需要窗口的命令才启动 WPF。
/// 这样 <c>--help</c> / <c>--capture-full</c> / <c>--capture-region --region</c> 能省掉约 250ms 的 WPF 启动。
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Diag.Mark("main-entry");

        try
        {
            if (CliFastPath.TryRun(args, out var exitCode))
            {
                Diag.Mark("cli-done");
                Diag.Flush(args);
                CliFastPath.DetachConsole();
                Environment.Exit(exitCode);
                return;
            }
        }
        catch (Exception ex)
        {
            // 快速路径自身出错时不要静默吞掉，落到控制台后照常退出
            Console.Error.WriteLine("error: " + ex.Message);
            Environment.Exit(1);
            return;
        }

        Diag.Mark("wpf-start");

        // 单文件分发版：首次启动把内嵌的 OCR 插件释放到磁盘
        Services.OcrPayload.EnsureAvailable();

        var app = new App();
        app.InitializeComponent();
        Diag.Mark("wpf-init-done");
        Diag.Flush(args);
        app.Run();
    }
}

/// <summary>启动耗时探针：只有带 --diag-startup 时才记录，正常使用零开销。</summary>
internal static class Diag
{
    private static readonly List<(string Name, double FromProcessStart)> Marks = new();
    private static readonly DateTime ProcessStart = SafeStartTime();

    private static DateTime SafeStartTime()
    {
        try
        {
            return System.Diagnostics.Process.GetCurrentProcess().StartTime;
        }
        catch
        {
            return DateTime.Now;
        }
    }

    public static void Mark(string name)
    {
        Marks.Add((name, (DateTime.Now - ProcessStart).TotalMilliseconds));
    }

    public static void Flush(string[] args)
    {
        if (!args.Any(a => a.Equals("--diag-startup", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        try
        {
            var text = string.Join("\n", Marks.Select(m => $"{m.Name,-16} {m.FromProcessStart,8:N0} ms"));
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "pinshot-startup.txt"), text);
        }
        catch
        {
            // 诊断失败忽略
        }
    }
}
