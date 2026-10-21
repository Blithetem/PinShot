using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PinShot.App.Editor;

/// <summary>
/// 轻量文字输入框（WPF 没有内置 InputBox）。
/// 样式为深色圆角浮层，位置跟随触发点的光标位置，不再固定弹在屏幕中间。
/// </summary>
public sealed class TextInputDialog : Window
{
    private readonly TextBox _textBox = new()
    {
        Width = 320,
        MinHeight = 64,
        MaxHeight = 220,
        FontSize = 15,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Top,
        Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF4)),
        CaretBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF4)),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(8, 6, 8, 6),
    };

    public TextInputDialog(string? initialText = null, Point? anchorDip = null)
    {
        Title = initialText is null ? "输入文字" : "编辑文字";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _textBox.Text = initialText ?? string.Empty;

        var hint = new TextBlock
        {
            Text = initialText is null ? "输入文字，回车确认" : "修改文字，回车确认",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2)),
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 6),
        };

        var ok = CreateButton("确定", primary: true);
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            Text = _textBox.Text;
            DialogResult = true;
        };

        var cancel = CreateButton("取消", primary: false);
        cancel.IsCancel = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel();
        body.Children.Add(hint);
        body.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)),
            Child = _textBox,
        });
        body.Children.Add(buttons);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF7, 0x1C, 0x1C, 0x1F)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.6,
            },
            Child = body,
        };

        Content = card;

        Loaded += (_, _) =>
        {
            PlaceNear(anchorDip);
            _textBox.Focus();
            _textBox.CaretIndex = _textBox.Text.Length;
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        };
    }

    public string? Text { get; private set; }

    private static Button CreateButton(string text, bool primary)
    {
        var button = new Button
        {
            Content = text,
            Width = 68,
            Height = 28,
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xED)),
            Background = new SolidColorBrush(primary
                ? Color.FromRgb(0x2D, 0x8C, 0xFF)
                : Color.FromRgb(0x33, 0x33, 0x38)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };

        // 圆角模板，避免默认 Button 的方角样式跟深色卡片不搭
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        button.Template = template;

        return button;
    }

    /// <summary>把窗口贴到触发点右下方，并确保不超出工作区。</summary>
    private void PlaceNear(Point? anchorDip)
    {
        if (anchorDip is not { } anchor)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var area = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : 380;
        var height = ActualHeight > 0 ? ActualHeight : 180;

        var left = anchor.X + 16;
        var top = anchor.Y + 18;

        if (left + width > area.Right)
        {
            left = Math.Max(area.Left + 8, anchor.X - width - 16);
        }

        if (top + height > area.Bottom)
        {
            top = Math.Max(area.Top + 8, anchor.Y - height - 18);
        }

        Left = left;
        Top = top;
    }
}
