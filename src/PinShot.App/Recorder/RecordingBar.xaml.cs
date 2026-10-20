using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace PinShot.App.Recorder;

/// <summary>录制中的悬浮控制条：红点 + 计时 + 停止按钮。</summary>
public partial class RecordingBar : Window
{
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _timer;

    public RecordingBar(System.Drawing.Rectangle area)
    {
        InitializeComponent();

        _area = area;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => UpdateTime();

        StopButton.Click += (_, _) => StopRequested?.Invoke();

        Loaded += (_, _) =>
        {
            // 控制条不参与屏幕捕获，避免被录进视频
            try
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                Interop.Native.SetWindowDisplayAffinity(handle, Interop.Native.WDA_EXCLUDEFROMCAPTURE);
            }
            catch
            {
                // 老系统不支持时忽略
            }

            // SizeToContent 在 Loaded 时可能还没算完尺寸，延后一帧再定位
            Dispatcher.BeginInvoke(new Action(PositionBar), DispatcherPriority.Loaded);

            _stopwatch.Start();
            _timer.Start();
            UpdateTime();
        };

        Closed += (_, _) =>
        {
            _timer.Stop();
            _stopwatch.Stop();
        };
    }

    public event Action? StopRequested;

    private System.Drawing.Rectangle _area;

    private void PositionBar()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;

        Left = Math.Clamp(_area.Right - ActualWidth, workArea.Left,
            Math.Max(workArea.Left, workArea.Right - ActualWidth));

        var top = _area.Top - ActualHeight - 8;
        if (top < workArea.Top)
        {
            top = _area.Bottom + 8;
        }

        Top = Math.Clamp(top, workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - ActualHeight));
    }

    private void UpdateTime()
    {
        var elapsed = _stopwatch.Elapsed;
        TimeText.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}
