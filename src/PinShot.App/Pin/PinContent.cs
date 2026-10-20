using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PinShot.App.Pin;

/// <summary>贴图内容类型。窗口本身统一用位图渲染，但保留原始语义用于右键菜单与历史恢复。</summary>
public enum PinContentKind
{
    Image,
    Text,
    Color,
    Files,
}

/// <summary>一张贴图的内容描述：图像贴图带位图，文本/颜色/文件贴图额外保留可复制/可打开的原始值。</summary>
public sealed class PinContent
{
    public PinContentKind Kind { get; init; } = PinContentKind.Image;

    /// <summary>实际显示（然后叠图像处理）的位图。文本/颜色/文件贴图由 <see cref="PinContentRenderer"/> 现画。</summary>
    public BitmapSource? Image { get; init; }

    /// <summary>文本贴图的纯文本，用于「复制纯文本」。</summary>
    public string? Text { get; init; }

    /// <summary>颜色贴图的 #RRGGBB，用于「复制颜色值」。</summary>
    public string? ColorHex { get; init; }

    /// <summary>文件贴图的路径列表，用于打开/复制/拖拽。</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>贴图标题（显示在底部）。</summary>
    public string? Title { get; init; }
}

/// <summary>Pin 相关文件的存放位置：默认 %LOCALAPPDATA%\PinShot，自检时可重定向到临时目录。</summary>
internal static class PinPaths
{
    private static string? _overrideDirectory;

    public static bool IsOverridden => _overrideDirectory is not null;

    public static string? OverrideDirectory => _overrideDirectory;

    public static string BaseDirectory => _overrideDirectory
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinShot");

    public static void Override(string? directory) => _overrideDirectory = directory;

    public static string EnsureDirectory(string subDirectory = "")
    {
        var directory = string.IsNullOrEmpty(subDirectory)
            ? BaseDirectory
            : Path.Combine(BaseDirectory, subDirectory);
        Directory.CreateDirectory(directory);
        return directory;
    }
}

/// <summary>把文本 / 颜色 / 文件列表画成带卡片背景的位图，交给 <see cref="PinWindow"/> 统一显示。</summary>
internal static class PinContentRenderer
{
    private const int Dpi = 96;

    private static readonly Typeface UiTypeface = new("Microsoft YaHei UI");
    private static readonly Typeface MonoTypeface = new("Consolas");

    public static BitmapSource RenderText(string text)
    {
        text = string.IsNullOrWhiteSpace(text) ? "（空文本）" : text;
        var culture = CultureInfo.CurrentUICulture;

        var formatted = new FormattedText(text, culture, FlowDirection.LeftToRight,
            UiTypeface, 16, Brushes.Black, 1.0)
        {
            MaxTextWidth = 520,
            Trimming = TextTrimming.None,
            LineHeight = 24,
        };

        var width = (int)Math.Ceiling(Math.Clamp(formatted.WidthIncludingTrailingWhitespace + 32, 140, 552));
        var height = (int)Math.Ceiling(Math.Clamp(formatted.Height + 26, 52, 2000));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            DrawCard(dc, width, height);
            dc.DrawText(formatted, new Point(16, 13));
        }

        return Render(visual, width, height);
    }

    public static BitmapSource RenderColor(Color color)
    {
        const int width = 180;
        const int height = 120;
        var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        var foreground = Luminance(color) > 0.55 ? Brushes.Black : Brushes.White;

        var hexText = new FormattedText(hex, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            MonoTypeface, 18, foreground, 1.0);
        var label = new FormattedText("颜色", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            UiTypeface, 11, foreground, 1.0);
        label.SetFontStyle(FontStyles.Normal);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var fill = new SolidColorBrush(color);
            fill.Freeze();
            dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, width, height), 4, 4);
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), 1),
                new Rect(0.5, 0.5, width - 1, height - 1), 4, 4);

            dc.DrawText(hexText, new Point((width - hexText.Width) / 2, height / 2.0 - 16));
            dc.DrawText(label, new Point((width - label.Width) / 2, height / 2.0 + 12));
        }

        return Render(visual, width, height);
    }

    public static BitmapSource RenderFiles(IReadOnlyList<string> paths)
    {
        var shown = Math.Min(paths.Count, 8);
        var rows = Math.Max(shown, 1) + (paths.Count > shown ? 1 : 0);
        const int width = 440;
        const int rowHeight = 26;
        var height = rows * rowHeight + 20;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            DrawCard(dc, width, height);

            var nameBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
            var hintBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
            var iconBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xFF));
            nameBrush.Freeze();
            hintBrush.Freeze();
            iconBrush.Freeze();

            for (var i = 0; i < shown; i++)
            {
                var path = paths[i];
                var top = 10 + i * rowHeight;

                dc.DrawRoundedRectangle(iconBrush, null, new Rect(12, top + 6, 13, 13), 2, 2);

                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = path;
                }

                var fileName = new FormattedText(name, CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, UiTypeface, 13, nameBrush, 1.0)
                {
                    MaxTextWidth = 320,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                dc.DrawText(fileName, new Point(34, top + 4));

                var hint = Directory.Exists(path) ? "文件夹" : (Path.GetExtension(path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "文件");
                var hintText = new FormattedText(hint, CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, UiTypeface, 11, hintBrush, 1.0);
                dc.DrawText(hintText, new Point(width - hintText.Width - 14, top + 6));
            }

            if (paths.Count == 0)
            {
                var empty = new FormattedText("（没有文件）", CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, UiTypeface, 13, hintBrush, 1.0);
                dc.DrawText(empty, new Point(12, 14));
            }
            else if (paths.Count > shown)
            {
                var more = new FormattedText($"…… 等 {paths.Count} 项", CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, UiTypeface, 12, hintBrush, 1.0);
                dc.DrawText(more, new Point(34, 10 + shown * rowHeight + 4));
            }
        }

        return Render(visual, width, height);
    }

    private static void DrawCard(DrawingContext dc, int width, int height)
    {
        var background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
        var border = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8));
        background.Freeze();
        border.Freeze();

        dc.DrawRoundedRectangle(background, null, new Rect(0, 0, width, height), 4, 4);
        dc.DrawRoundedRectangle(null, new Pen(border, 1),
            new Rect(0.5, 0.5, width - 1, height - 1), 4, 4);
    }

    private static BitmapSource Render(DrawingVisual visual, int width, int height)
    {
        var target = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height),
            Dpi, Dpi, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static double Luminance(Color color)
        => (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
}
