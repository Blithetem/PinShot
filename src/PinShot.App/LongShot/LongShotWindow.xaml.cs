using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PinShot.App.Interop;
using PinShot.App.Recorder;

namespace PinShot.App.LongShot;

/// <summary>
/// 长截图：选区确认后覆盖全屏（鼠标穿透），按固定间隔抓取选区并与上一帧比对，
/// 自动把新滚动出来的内容拼到画布上。
/// </summary>
public partial class LongShotWindow : Window
{
    private readonly Int32Rect _region;
    private readonly Int32Rect _virtualBounds;
    private readonly IntPtr _previousWindow;
    private readonly LongShotSession _session;
    private readonly DispatcherTimer _timer;

    private Bitmap? _bitmap;
    private Graphics? _graphics;

    public LongShotWindow(Int32Rect region, Int32Rect virtualBounds, IntPtr previousWindow = default)
    {
        InitializeComponent();

        _region = region;
        _virtualBounds = virtualBounds;
        _previousWindow = previousWindow;
        _session = new LongShotSession(region.Width, region.Height);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _timer.Tick += OnTick;

        Log("窗口创建");

        Loaded += OnLoaded;
        PreviewKeyDown += OnKeyDown;
        Closed += (_, _) =>
        {
            _timer.Stop();

            _bar?.Close();
            _bar = null;

            _frame?.Close();
            _frame = null;
        };
    }

    public event Action<BitmapSource?>? Completed;

    /// <summary>供自检/自动流程调用：等价于按 Enter 完成。</summary>
    public void CompleteNow()
    {
        Finish();
    }

    private LongShotBar? _bar;
    private RecordingFrame? _frame;



    private void CancelNow()
    {
        _timer.Stop();

        try
        {
            Completed?.Invoke(null);
        }
        catch
        {
            // 回调异常不能阻止窗口关闭
        }
        finally
        {
            Close();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(handle, Native.HWND_TOPMOST,
            _virtualBounds.X, _virtualBounds.Y, _virtualBounds.Width, _virtualBounds.Height,
            Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);

        // 鼠标穿透 + 不激活：滚轮/点击落到目标窗口，遮罩不抢焦点
        var extended = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);
        Native.SetWindowLong(handle, Native.GWL_EXSTYLE,
            extended | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);

        Log($"遮罩就绪(穿透), 目标窗口={_previousWindow}");

        // 悬浮控制条：完成/取消用鼠标点
        _bar = new LongShotBar();
        _bar.DoneRequested += Finish;
        _bar.CancelRequested += CancelNow;
        _bar.Show();
        _bar.PlaceNear(_region, _virtualBounds);

        LayoutRegion();
        UpdateMask();

        // 首帧立即抓取，避免定时器第一次触发前画面是空的
        var first = GrabFrame();
        if (first is not null)
        {
            _session.AddFrame(first);
            PreviewImage.Source = ToBitmapSource(first);
            UpdateInfo();
        }

        _timer.Start();
        Log("首帧完成，定时器启动（请在选区内滚动页面）");

        // 不使用全屏遮罩：隐藏窗口本身，只保留边框 + 悬浮条 + 后台抓帧，
        // 这样焦点和鼠标滚轮完全不受影响（否则遮罩会干扰滚轮路由）。
        Hide();
        Log("遮罩已隐藏，仅保留边框和悬浮条");

        // 选区边框：只画红框、鼠标穿透、不参与屏幕捕获（不会进结果图）
        try
        {
            _frame = new RecordingFrame(
                new System.Drawing.Rectangle(_region.X, _region.Y, _region.Width, _region.Height),
                _virtualBounds);
            _frame.Show();
        }
        catch (Exception ex)
        {
            Log($"边框显示失败: {ex.Message}");
        }

        // 把焦点还给截图前的窗口，滚轮才会滚动它
        if (_previousWindow != IntPtr.Zero)
        {
            var restored = Native.SetForegroundWindow(_previousWindow);
            Log($"恢复焦点: {restored}");
        }
    }

    private void LayoutRegion()
    {
        var region = new Rect(_region.X, _region.Y, _region.Width, _region.Height);
        System.Windows.Controls.Canvas.SetLeft(PreviewImage, region.X);
        System.Windows.Controls.Canvas.SetTop(PreviewImage, region.Y);
        PreviewImage.Width = region.Width;
        PreviewImage.Height = region.Height;

        var infoTop = region.Y - 40;
        if (infoTop < 8)
        {
            infoTop = region.Y + 8;
        }

        InfoBar.Margin = new Thickness(Math.Max(8, region.X), infoTop, 0, 0);
        InfoBar.HorizontalAlignment = HorizontalAlignment.Left;
        InfoBar.VerticalAlignment = VerticalAlignment.Top;
    }

    private void UpdateMask()
    {
        var geometry = new System.Windows.Media.GeometryGroup
        {
            FillRule = System.Windows.Media.FillRule.EvenOdd,
        };
        geometry.Children.Add(new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, ActualWidth, ActualHeight)));
        geometry.Children.Add(new System.Windows.Media.RectangleGeometry(
            new Rect(_region.X, _region.Y, _region.Width, _region.Height)));

        MaskPath.Data = geometry;

        System.Windows.Controls.Canvas.SetLeft(BorderRect, _region.X);
        System.Windows.Controls.Canvas.SetTop(BorderRect, _region.Y);
        BorderRect.Width = _region.Width;
        BorderRect.Height = _region.Height;
        BorderRect.Visibility = Visibility.Visible;
    }

    private int _tickCount;
    private int _noProgress;
    private int _scrollAccumulator;
    private const int ScrollEveryTicks = 3;
    private const int NoProgressLimit = 6;

    private void OnTick(object? sender, EventArgs e)
    {
        var frame = GrabFrame();
        _tickCount++;

        if (frame is null)
        {
            return;
        }

        var added = _session.AddFrame(frame);
        PreviewImage.Source = ToBitmapSource(frame);
        UpdateInfo();
        _bar?.UpdateProgress(_session.Width, _session.CurrentHeight);

        if (_session.IsFull)
        {
            Finish();
            return;
        }

        if (added)
        {
            _noProgress = 0;
        }
        else
        {
            _noProgress++;
        }

        if (_tickCount % 8 == 0)
        {
            Log($"滚动中: tick={_tickCount}, 已拼接 {_session.CurrentHeight} px, 无进展={_noProgress}");
        }
    }



    private void UpdateInfo()
    {
        InfoText.Text = $"已捕获 {_session.Width} × {_session.CurrentHeight} px";
    }

    private static void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pinshot-longshot.log"),
                $"{DateTime.Now:HH:mm:ss} {message}\n");
        }
        catch
        {
            // 日志失败忽略
        }
    }

    private byte[]? GrabFrame()
    {
        try
        {
            if (_bitmap is null)
            {
                _bitmap = new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppRgb);
                _graphics = Graphics.FromImage(_bitmap);
            }

            _graphics!.CopyFromScreen(_region.X, _region.Y, 0, 0,
                new System.Drawing.Size(_region.Width, _region.Height), CopyPixelOperation.SourceCopy);

            var rect = new Rectangle(0, 0, _region.Width, _region.Height);
            var data = _bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                var bytes = new byte[_region.Width * _region.Height * 4];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                _bitmap.UnlockBits(data);
            }
        }
        catch
        {
            return null;
        }
    }

    private BitmapSource ToBitmapSource(byte[] frame)
    {
        var bitmap = BitmapSource.Create(_region.Width, _region.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, frame, _region.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _timer.Stop();

            try
            {
                Completed?.Invoke(null);
            }
            catch
            {
                // 回调异常不能阻止窗口关闭
            }
            finally
            {
                Close();
            }
        }
        else if (e.Key == Key.Enter)
        {
            Finish();
        }
    }

    private void Finish()
    {
        _timer.Stop();

        BitmapSource? result = null;
        try
        {
            Log($"结束: 已拼接 {_session.CurrentHeight} px，开始生成位图");
            result = _session.BuildBitmap();
            Log($"位图生成完成: {result.PixelWidth}×{result.PixelHeight}");
        }
        catch (Exception ex)
        {
            Log($"生成位图失败: {ex.Message}");
        }

        try
        {
            Completed?.Invoke(result);
        }
        catch (Exception ex)
        {
            Log($"完成回调异常: {ex.Message}");
        }
        finally
        {
            // 无论回调成功与否都要关闭窗口，否则透明遮罩会残留在屏幕上
            Hide();
            Close();
        }
    }
}
