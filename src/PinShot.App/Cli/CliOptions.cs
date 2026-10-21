namespace PinShot.App.Cli;

public enum CliCommand
{
    None,
    Help,
    CaptureFull,
    CaptureRegion,
    Pin,
}

/// <summary>命令行参数解析结果。</summary>
public sealed class CliOptions
{
    public CliCommand Command { get; private init; } = CliCommand.None;

    public string? Output { get; private init; }

    public string? InputPath { get; private init; }

    public bool Clipboard { get; private init; }

    public int DelayMs { get; private init; }

    /// <summary>--region 指定的截图区域（x,y,width,height，物理像素）。</summary>
    public (int X, int Y, int Width, int Height)? Region { get; private init; }

    /// <summary>--auto-demo：交互式截图自动框选并复制，用于验证命令行退出流程（测试用）。</summary>
    public bool AutoDemo { get; private init; }

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new CliOptions();
        }

        var command = CliCommand.None;
        string? output = null;
        string? input = null;
        var clipboard = false;
        var delay = 0;
        (int, int, int, int)? region = null;
        var autoDemo = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg.ToLowerInvariant())
            {
                case "--help":
                case "-h":
                case "/?":
                    command = CliCommand.Help;
                    break;

                case "--capture-full":
                case "capture-full":
                    command = CliCommand.CaptureFull;
                    break;

                case "--capture-region":
                case "capture-region":
                    command = CliCommand.CaptureRegion;
                    break;

                case "--pin":
                case "pin":
                    command = CliCommand.Pin;
                    input = NextValue(args, ref index);
                    break;

                case "--output":
                case "-o":
                    output = NextValue(args, ref index);
                    break;

                case "--clipboard":
                case "-c":
                    clipboard = true;
                    break;

                case "--delay":
                    if (int.TryParse(NextValue(args, ref index), out var delayMs) && delayMs > 0)
                    {
                        delay = Math.Min(delayMs, 30000);
                    }

                    break;

                case "--region":
                    region = ParseRegion(NextValue(args, ref index));
                    break;

                case "--auto-demo":
                    autoDemo = true;
                    break;
            }
        }

        return new CliOptions
        {
            Command = command,
            Output = output,
            InputPath = input,
            Clipboard = clipboard,
            DelayMs = delay,
            Region = region,
            AutoDemo = autoDemo,
        };
    }

    /// <summary>解析 "x,y,width,height" 形式的区域参数。</summary>
    private static (int, int, int, int)? ParseRegion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return null;
        }

        if (!int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y) ||
            !int.TryParse(parts[2], out var width) || !int.TryParse(parts[3], out var height))
        {
            return null;
        }

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return (x, y, width, height);
    }

    private static string? NextValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            return null;
        }

        index++;
        return args[index];
    }

    public static string HelpText =>
        """
        PinShot 命令行用法：

          PinShot.exe --capture-full [--output <文件>] [--clipboard] [--delay <毫秒>]
              截取整个虚拟屏幕。
              不给 --output 也不给 --clipboard 时，按「设置」里的默认目录和格式保存，
              文件名形如 截图_20261021_171530.png（与截图界面 Ctrl+S 的默认路径一致），
              并把完整路径打印到控制台。

          PinShot.exe --capture-region [--output <文件>] [--clipboard] [--delay <毫秒>]
              打开交互式选区，框选后按同样的规则保存或复制。

          PinShot.exe --capture-region --region <x,y,w,h> [--output <文件>] [--clipboard]
              带 --region 时不弹选区界面，直接截取指定矩形（物理像素，可负坐标）。

          PinShot.exe --pin <图片路径>
              把指定 PNG/JPG/.... 图片贴到屏幕上。

          PinShot.exe --help
              显示本帮助。

        保存目录与格式来自托盘菜单「设置...」，配置文件：
          %LOCALAPPDATA%\PinShot\settings.json

        退出码：0 成功，1 参数或执行失败。
        """;
}
