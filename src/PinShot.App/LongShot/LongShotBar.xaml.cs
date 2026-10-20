using System.Windows;
using System.Windows.Input;

namespace PinShot.App.LongShot;

/// <summary>长截图悬浮工具条：显示进度，提供完成/取消（不依赖键盘焦点）。</summary>
public partial class LongShotBar : Window
{
    public LongShotBar()
    {
        InitializeComponent();

        DoneButton.Click += (_, _) => DoneRequested?.Invoke();
        CancelButton.Click += (_, _) => CancelRequested?.Invoke();

        Loaded += (_, _) =>
        {
            // 不抢焦点：滚轮必须继续落到目标窗口
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var extended = Interop.Native.GetWindowLong(handle, Interop.Native.GWL_EXSTYLE);
            Interop.Native.SetWindowLong(handle, Interop.Native.GWL_EXSTYLE,
                extended | Interop.Native.WS_EX_NOACTIVATE | Interop.Native.WS_EX_TOOLWINDOW);
        };
    }

    public event Action? DoneRequested;

    public event Action? CancelRequested;

    public void UpdateProgress(int width, int height)
    {
        StatusText.Text = $"已捕获 {width} × {height} px";
    }

    public void PlaceNear(Int32Rect region, Int32Rect virtualBounds)
    {
        // 放在选区右上角外侧；上方没空间就放下方
        var left = region.X + region.Width - ActualWidth;
        var top = region.Y - ActualHeight - 10;

        if (top < virtualBounds.Y)
        {
            top = region.Y + region.Height + 10;
        }

        Left = Math.Clamp(left, virtualBounds.X, virtualBounds.X + virtualBounds.Width - ActualWidth);
        Top = Math.Clamp(top, virtualBounds.Y, virtualBounds.Y + virtualBounds.Height - ActualHeight);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        try
        {
            DragMove();
        }
        catch
        {
            // 拖动失败忽略
        }
    }
}