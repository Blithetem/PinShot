using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PinShot.App.Interop;

namespace PinShot.App.Recorder;

/// <summary>
/// 录制期间的区域框：屏幕四周全透明、鼠标穿透，只画录制范围的红框与四角标记。
/// 窗口标记为「不参与屏幕捕获」，避免红框被录进视频。
/// </summary>
public partial class RecordingFrame : Window
{
    private const double CornerLength = 18;
    private const double CornerThickness = 3;

    private readonly System.Drawing.Rectangle _area;
    private readonly Int32Rect _virtualBounds;

    public RecordingFrame(System.Drawing.Rectangle area, Int32Rect virtualBounds)
    {
        InitializeComponent();

        _area = area;
        _virtualBounds = virtualBounds;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        Native.SetWindowPos(handle, Native.HWND_TOPMOST,
            _virtualBounds.X, _virtualBounds.Y, _virtualBounds.Width, _virtualBounds.Height,
            Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);

        var extended = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);
        Native.SetWindowLong(handle, Native.GWL_EXSTYLE,
            extended | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW);

        // Windows 10 2004+：让本窗口不出现在屏幕捕获里，红框不会被录进视频
        Native.SetWindowDisplayAffinity(handle, Native.WDA_EXCLUDEFROMCAPTURE);

        LayoutFrame();
    }

    private void LayoutFrame()
    {
        var left = _area.Left - _virtualBounds.X;
        var top = _area.Top - _virtualBounds.Y;
        var width = _area.Width;
        var height = _area.Height;

        Canvas.SetLeft(FrameBorder, left);
        Canvas.SetTop(FrameBorder, top);
        FrameBorder.Width = Math.Max(1, width);
        FrameBorder.Height = Math.Max(1, height);

        AddCorner(left, top, 1, 1);
        AddCorner(left + width, top, -1, 1);
        AddCorner(left, top + height, 1, -1);
        AddCorner(left + width, top + height, -1, -1);
    }

    private void AddCorner(double x, double y, int dirX, int dirY)
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
        brush.Freeze();

        var horizontal = new Border
        {
            Width = CornerLength,
            Height = CornerThickness,
            Background = brush,
            IsHitTestVisible = false,
        };

        var vertical = new Border
        {
            Width = CornerThickness,
            Height = CornerLength,
            Background = brush,
            IsHitTestVisible = false,
        };

        HorizontalPosition(horizontal, x, y, dirX, dirY);
        VerticalPosition(vertical, x, y, dirX, dirY);

        CornerLayer.Children.Add(horizontal);
        CornerLayer.Children.Add(vertical);
    }

    private static void HorizontalPosition(FrameworkElement element, double x, double y,
        int dirX, int dirY)
    {
        Canvas.SetLeft(element, dirX > 0 ? x : x - CornerLength);
        Canvas.SetTop(element, dirY > 0 ? y : y - CornerThickness);
    }

    private static void VerticalPosition(FrameworkElement element, double x, double y,
        int dirX, int dirY)
    {
        Canvas.SetLeft(element, dirX > 0 ? x : x - CornerThickness);
        Canvas.SetTop(element, dirY > 0 ? y : y - CornerLength);
    }
}
