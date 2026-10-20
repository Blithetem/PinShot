using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using PinShot.App.Gif;

namespace PinShot.App.Recorder;

/// <summary>
/// 录制结束后的操作条：保存/复制视频，或转成 GIF 保存/复制。
/// 关闭窗口时如果还没处理，会把视频保留到 recordings 目录，避免录完就丢。
/// </summary>
public partial class RecordingResultWindow : Window
{
    private readonly string _aviPath;
    private readonly IReadOnlyList<BitmapSource> _gifFrames;
    private readonly TimeSpan _duration;
    private readonly int _width;
    private readonly int _height;

    private bool _handled;

    public RecordingResultWindow(string aviPath, IReadOnlyList<BitmapSource> gifFrames,
        TimeSpan duration, int width, int height)
    {
        InitializeComponent();

        _aviPath = aviPath;
        _gifFrames = gifFrames;
        _duration = duration;
        _width = width;
        _height = height;

        TitleText.Text = "录制完成";
        InfoText.Text =
            $"时长 {FormatDuration(duration)}    画面 {width}×{height}    " +
            $"文件 {FormatSize(new FileInfo(aviPath).Length)}    GIF {gifFrames.Count} 帧";

        if (gifFrames.Count == 0)
        {
            SaveGifButton.IsEnabled = false;
            CopyGifButton.IsEnabled = false;
        }

        SaveVideoButton.Click += (_, _) => SaveVideo();
        CopyVideoButton.Click += (_, _) => CopyVideo();
        SaveGifButton.Click += (_, _) => SaveGif();
        CopyGifButton.Click += (_, _) => CopyGif();
        CloseButton.Click += (_, _) => Close();

        Closed += (_, _) => OnClosedHandler();
    }

    private void SaveVideo()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "AVI 视频|*.avi",
            FileName = $"录屏_{DateTime.Now:yyyyMMdd_HHmmss}.avi",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.Move(_aviPath, dialog.FileName, true);
            CopyFileToClipboard(dialog.FileName);
            _handled = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "PinShot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyVideo()
    {
        try
        {
            var target = Path.Combine(RecordingsDirectory(), Path.GetFileName(_aviPath));
            File.Move(_aviPath, target, true);
            CopyFileToClipboard(target);
            _handled = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败：{ex.Message}", "PinShot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveGif()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "GIF 图片|*.gif",
            FileName = $"录屏GIF_{DateTime.Now:yyyyMMdd_HHmmss}.gif",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            GifEncoder.Save(_gifFrames, dialog.FileName);
            CopyFileToClipboard(dialog.FileName);
            _handled = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存 GIF 失败：{ex.Message}", "PinShot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyGif()
    {
        try
        {
            var target = Path.Combine(RecordingsDirectory(),
                $"录屏GIF_{DateTime.Now:yyyyMMdd_HHmmss}.gif");
            GifEncoder.Save(_gifFrames, target);
            CopyFileToClipboard(target);
            _handled = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"生成 GIF 失败：{ex.Message}", "PinShot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClosedHandler()
    {
        if (_handled || !File.Exists(_aviPath))
        {
            return;
        }

        try
        {
            var target = Path.Combine(RecordingsDirectory(), Path.GetFileName(_aviPath));
            File.Move(_aviPath, target, true);
        }
        catch
        {
            // 保留失败时让临时文件留在原处
        }
    }

    private static string RecordingsDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PinShot", "recordings");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void CopyFileToClipboard(string path)
    {
        try
        {
            var files = new System.Collections.Specialized.StringCollection { path };
            Clipboard.SetFileDropList(files);
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:F1} MB"
            : $"{bytes / 1024.0:F0} KB";
}
