using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PinShot.App.Interop;

namespace PinShot.App.Services;

/// <summary>设置窗口：热键、保存目录/格式、截图默认行为。</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        _settings = settings;
        LoadIntoUi(settings);
    }

    /// <summary>保存成功后的回调参数；取消时为 null。</summary>
    public AppSettings? Result { get; private set; }

    /// <summary>热键是否发生变化（调用方据此重新注册全局热键）。</summary>
    public bool HotkeysChanged { get; private set; }

    private void LoadIntoUi(AppSettings settings)
    {
        LoadHotkeyInto(CaptureHotkeyBox, settings.CaptureHotkey);
        LoadHotkeyInto(PinHotkeyBox, settings.PinHotkey);
        LoadHotkeyInto(RestoreHotkeyBox, settings.RestorePinHotkey);
        LoadHotkeyInto(RecordHotkeyBox, settings.RecordHotkey);

        DirectoryBox.Text = settings.ResolveSaveDirectory();
        SelectFormat(settings.DefaultFormat);
        QualitySlider.Value = settings.JpegQuality;
        QualityText.Text = settings.JpegQuality.ToString();
        CursorCheck.IsChecked = settings.CaptureWithCursor;
        SelectCorner(settings.DefaultCornerRadius);

        Mp4Check.IsChecked = settings.ConvertRecordingToMp4;
        FfmpegBox.Text = settings.FfmpegPath ?? string.Empty;
        UpdateFfmpegStatus();
        UpdateQualityVisibility();
    }

    /// <summary>把配置值装进热键框：Tag 存原始文本，空表示「未设置」。</summary>
    private static void LoadHotkeyInto(TextBox box, string? value)
    {
        box.Tag = value ?? string.Empty;
        box.Text = HotkeyParser.Pretty(value);
    }

    private void SelectFormat(string format)
    {
        var index = format switch
        {
            "jpg" => 1,
            "bmp" => 2,
            _ => 0,
        };

        FormatBox.SelectedIndex = index;
    }

    private void SelectCorner(int radius)
    {
        CornerBox.SelectedIndex = radius switch
        {
            <= 0 => 0,
            <= 8 => 1,
            <= 16 => 2,
            _ => 3,
        };
    }

    private static string CurrentFormatTag(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "png";

    private void OnHotkeyFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.SelectAll();
        }
    }

    /// <summary>在热键框里直接按下组合键即可录入。</summary>
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, null);
            return;
        }

        // Delete / Backspace：清空该热键（= 不设置）
        if (e.Key is Key.Delete or Key.Back)
        {
            box.Tag = string.Empty;
            box.Text = "未设置";
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Tab or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt)
        {
            return;
        }

        var text = HotkeyParser.Format(e.Key, Keyboard.Modifiers);
        if (text is null)
        {
            return;
        }

        box.Text = HotkeyParser.Pretty(text);
        box.Tag = text;
        e.Handled = true;
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QualityRow is not null)
        {
            UpdateQualityVisibility();
        }
    }

    private void UpdateQualityVisibility()
    {
        if (QualityRow is not null)
        {
            QualityRow.Visibility = CurrentFormatTag(FormatBox) == "jpg"
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择截图默认保存目录",
            InitialDirectory = Directory.Exists(DirectoryBox.Text) ? DirectoryBox.Text : string.Empty,
        };

        if (dialog.ShowDialog(this) == true)
        {
            DirectoryBox.Text = dialog.FolderName;
        }
    }

    private void OnBrowseFfmpeg(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "ffmpeg|ffmpeg.exe|可执行文件|*.exe",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            FfmpegBox.Text = dialog.FileName;
            UpdateFfmpegStatus();
        }
    }

    private void UpdateFfmpegStatus()
    {
        if (FfmpegStatusText is null)
        {
            return;
        }

        var configured = FfmpegBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
        {
            FfmpegStatusText.Text = "已指定路径，录制结束后会自动转 MP4。";
            return;
        }

        FfmpegStatusText.Text = Recorder.VideoTranscoder.IsAvailable
            ? "已自动检测到 ffmpeg，录制结束后会自动转 MP4。"
            : "没找到 ffmpeg：录制会保持 AVI（体积较大）。可以手动指定路径。";
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "恢复所有设置为默认值？", "PinShot",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        LoadIntoUi(new AppSettings());
        StatusText.Text = "已载入默认值，点「保存」后生效。";
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var capture = ResolveHotkey(CaptureHotkeyBox, "Ctrl+Shift+S");
        var pin = ResolveHotkey(PinHotkeyBox, "Ctrl+2");
        var restore = ResolveHotkey(RestoreHotkeyBox, "Ctrl+3");
        var record = ResolveHotkey(RecordHotkeyBox, "Ctrl+Shift+R");

        var changed = capture != _settings.CaptureHotkey || pin != _settings.PinHotkey ||
                      restore != _settings.RestorePinHotkey || record != _settings.RecordHotkey;

        if (changed)
        {
            var message = $"热键修改将在重启 PinShot 后生效。\n\n截图 {capture}\n贴图 {pin}\n" +
                          $"恢复贴图 {restore}\n录屏 {record}";

            MessageBox.Show(this, message, "PinShot", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        HotkeysChanged = changed;

        _settings.CaptureHotkey = capture;
        _settings.PinHotkey = pin;
        _settings.RestorePinHotkey = restore;
        _settings.RecordHotkey = record;

        var directory = DirectoryBox.Text?.Trim() ?? string.Empty;
        _settings.SaveDirectory = directory;
        _settings.DefaultFormat = CurrentFormatTag(FormatBox);
        _settings.JpegQuality = (int)QualitySlider.Value;
        _settings.CaptureWithCursor = CursorCheck.IsChecked == true;
        _settings.DefaultCornerRadius = CornerBox.SelectedIndex switch
        {
            1 => 8,
            2 => 16,
            3 => 24,
            _ => 0,
        };

        _settings.ConvertRecordingToMp4 = Mp4Check.IsChecked == true;
        _settings.FfmpegPath = FfmpegBox.Text?.Trim() ?? string.Empty;

        _settings.Save();
        Result = _settings;
        DialogResult = true;
    }

    /// <summary>优先用录入时记下的原始组合；没有改动就保持原值。</summary>
    /// <summary>取热键框的值；空字符串表示「不设置」。</summary>
    private static string ResolveHotkey(TextBox box, string fallback)
    {
        // Tag 始终是权威值（空 = 用户清空了）
        if (box.Tag is string tag)
        {
            return tag.Trim();
        }

        var compact = box.Text?.Replace(" ", string.Empty) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(compact) || compact == "未设置")
        {
            return string.Empty;
        }

        return compact;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }
}
