using System.Windows;

namespace PinShot.App.Editor;

/// <summary>OCR 识别结果窗口：显示文字，可复制。</summary>
public partial class OcrResultWindow : Window
{
    public OcrResultWindow(string text)
    {
        InitializeComponent();

        ResultBox.Text = string.IsNullOrWhiteSpace(text) ? "（没有识别到文字）" : text;
        Loaded += (_, _) => ResultBox.Focus();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ResultBox.Text);
            Close();
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}