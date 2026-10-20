using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PinShot.App.Editor;

/// <summary>轻量文字输入框（WPF 没有内置 InputBox）。</summary>
public sealed class TextInputDialog : Window
{
    private readonly TextBox _textBox = new()
    {
        Width = 340,
        Height = 72,
        FontSize = 16,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Top,
    };

    public TextInputDialog(string? initialText = null)
    {
        Title = initialText is null ? "输入文字" : "编辑文字";
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));

        var panel = new StackPanel { Margin = new Thickness(12) };
        _textBox.Text = initialText ?? string.Empty;
        panel.Children.Add(_textBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var ok = new Button
        {
            Content = "确定",
            Width = 76,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };
        ok.Click += (_, _) =>
        {
            Text = _textBox.Text;
            DialogResult = true;
        };

        var cancel = new Button { Content = "取消", Width = 76, IsCancel = true };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => _textBox.Focus();
    }

    public string? Text { get; private set; }
}
