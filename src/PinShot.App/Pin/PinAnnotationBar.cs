using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PinShot.App.Editor;

namespace PinShot.App.Pin;

/// <summary>
/// 贴图标注浮动工具栏窗口：独立无边框窗口，避免把工具栏塞进贴图窗口导致被裁切。
/// 只读复用 <see cref="EditorToolbar"/>，不修改它。
/// </summary>
internal sealed class PinAnnotationBar : Window
{
    private readonly EditorToolbar _toolbar;

    public PinAnnotationBar()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        ShowActivated = false;

        _toolbar = new EditorToolbar(showLongShotAction: false, showRecordAction: false,
            showOcrAction: false);
        Content = _toolbar;
    }

    public EditorToolbar Toolbar => _toolbar;

    /// <summary>工具栏上的 Esc / 空格需要转发给贴图窗口。</summary>
    public event Action? EscapeRequested;

    public event Action? ToggleRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            PinNative.MakeToolWindowNoActivate(this);
        }
        catch
        {
            // 仅影响工具窗口行为，失败可忽略
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EscapeRequested?.Invoke();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space)
        {
            ToggleRequested?.Invoke();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>把工具栏放到贴图窗口下方（放不下就放上方），并夹到工作区内。</summary>
    public void PlaceNear(Window owner)
    {
        if (double.IsNaN(owner.Left) || double.IsNaN(owner.Top))
        {
            return;
        }

        UpdateLayout();
        var width = ActualWidth > 0 ? ActualWidth : 720;
        var height = ActualHeight > 0 ? ActualHeight : 44;

        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)(owner.Left + owner.Width / 2), (int)(owner.Top + 10)));
        var area = screen.WorkingArea;

        var left = owner.Left + (owner.Width - width) / 2;
        var top = owner.Top + owner.Height + 8;
        if (top + height > area.Bottom - 4)
        {
            top = owner.Top - height - 8;
        }

        left = Math.Clamp(left, area.Left + 4, Math.Max(area.Left + 4, area.Right - width - 4));
        top = Math.Clamp(top, area.Top + 4, Math.Max(area.Top + 4, area.Bottom - height - 4));

        Left = left;
        Top = top;
    }
}

/// <summary>
/// 鼠标穿透状态下的小浮条：因为贴图本身已经点不到，必须留一个入口恢复交互/关闭。
/// </summary>
internal sealed class PinThroughBar : Window
{
    public PinThroughBar(string caption)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        ShowActivated = false;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 5, 10, 5),
        };
        panel.Children.Add(new TextBlock
        {
            Text = caption,
            Foreground = Brushes.White,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });

        var exit = new Button
        {
            Content = "退出穿透",
            FontSize = 12,
            Padding = new Thickness(8, 2, 8, 2),
            ToolTip = "恢复鼠标交互",
        };
        exit.Click += (_, _) => ExitRequested?.Invoke();
        panel.Children.Add(exit);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1F)),
            CornerRadius = new CornerRadius(8),
            Child = panel,
            Effect = new DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.4,
            },
        };
    }

    public event Action? ExitRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            PinNative.MakeToolWindowNoActivate(this);
        }
        catch
        {
            // 忽略
        }
    }

    public void PlaceNear(Window owner)
    {
        if (double.IsNaN(owner.Left) || double.IsNaN(owner.Top))
        {
            return;
        }

        UpdateLayout();
        var width = ActualWidth > 0 ? ActualWidth : 150;
        var height = ActualHeight > 0 ? ActualHeight : 34;

        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)(owner.Left + owner.Width / 2), (int)(owner.Top + 10)));
        var area = screen.WorkingArea;

        var left = owner.Left + owner.Width - width;
        var top = owner.Top + owner.Height + 6;
        if (top + height > area.Bottom - 4)
        {
            top = owner.Top - height - 6;
        }

        Left = Math.Clamp(left, area.Left + 4, Math.Max(area.Left + 4, area.Right - width - 4));
        Top = Math.Clamp(top, area.Top + 4, Math.Max(area.Top + 4, area.Bottom - height - 4));
    }
}
