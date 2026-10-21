using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PinShot.App.Services;

namespace PinShot.App.Editor;

public enum SaveImageFormat
{
    Png,
    Jpeg,
    Bmp,
}

/// <summary>保存对话框：格式选择（PNG/JPEG/BMP）+ JPEG 质量 + 路径。</summary>
public partial class SaveImageDialog : Window
{
    private readonly AppSettings _settings;

    public SaveImageDialog(AppSettings? settings = null)
    {
        InitializeComponent();

        _settings = settings ?? AppSettings.Current;

        SelectFormat(_settings.DefaultFormat);
        QualitySlider.Value = Math.Clamp(_settings.JpegQuality, 10, 100);
        QualityText.Text = ((int)QualitySlider.Value).ToString();
        UpdateQualityVisibility();
        UpdateDefaultPath();

        Loaded += (_, _) => PathBox.CaretIndex = PathBox.Text.Length;
    }

    public string FilePath { get; private set; } = string.Empty;

    public SaveImageFormat Format { get; private set; } = SaveImageFormat.Png;

    public int Quality { get; private set; } = 90;

    private SaveImageFormat CurrentFormat => FormatBox.SelectedIndex switch
    {
        1 => SaveImageFormat.Jpeg,
        2 => SaveImageFormat.Bmp,
        _ => SaveImageFormat.Png,
    };

    private string Extension => CurrentFormat switch
    {
        SaveImageFormat.Jpeg => ".jpg",
        SaveImageFormat.Bmp => ".bmp",
        _ => ".png",
    };

    private void SelectFormat(string format)
    {
        FormatBox.SelectedIndex = format switch
        {
            "jpg" => 1,
            "bmp" => 2,
            _ => 0,
        };
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateQualityVisibility();
        UpdateDefaultPath();
    }

    private void OnQualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityText is not null)
        {
            QualityText.Text = ((int)QualitySlider.Value).ToString();
        }
    }

    private void UpdateQualityVisibility()
    {
        if (QualityRow is not null)
        {
            QualityRow.Visibility = CurrentFormat == SaveImageFormat.Jpeg
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void UpdateDefaultPath()
    {
        if (PathBox is null)
        {
            return;
        }

        // 优先用上次保存的目录，其次是配置里的默认目录，最后才是系统图片目录
        var folder = _settings.LastSaveDirectory;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            folder = _settings.ResolveSaveDirectory();
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Path.GetTempPath();
        }

        PathBox.Text = Path.Combine(folder, $"截图_{DateTime.Now:yyyyMMdd_HHmmss}{Extension}");
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = CurrentFormat switch
            {
                SaveImageFormat.Jpeg => "JPEG 图片|*.jpg;*.jpeg",
                SaveImageFormat.Bmp => "BMP 图片|*.bmp",
                _ => "PNG 图片|*.png",
            },
            FileName = Path.GetFileName(PathBox.Text),
            InitialDirectory = GetDirectory(),
        };

        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = dialog.FileName;
        }
    }

    private string GetDirectory()
    {
        try
        {
            return Path.GetDirectoryName(PathBox.Text) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show("请选择保存路径。", "PinShot", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!Path.HasExtension(path))
        {
            path += Extension;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"路径无效：{ex.Message}", "PinShot", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        FilePath = path;
        Format = CurrentFormat;
        Quality = (int)QualitySlider.Value;

        // 记住这次保存的目录，下次直接定位到这里
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                _settings.LastSaveDirectory = directory;
                _settings.Save();
            }
        }
        catch
        {
            // 记录目录失败不影响保存
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}