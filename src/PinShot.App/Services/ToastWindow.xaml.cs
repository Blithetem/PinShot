using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PinShot.App.Services;

/// <summary>右下角短暂提示，用于启动反馈和重复启动提醒（不抢焦点、不拦截鼠标）。</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;

    public ToastWindow(string title, string body, int durationMs = 2600)
    {
        InitializeComponent();

        TitleText.Text = title;
        BodyText.Text = body;
        Opacity = 0;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            FadeOutAndClose();
        };

        Loaded += (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - ActualWidth - 24;
            Top = workArea.Bottom - ActualHeight - 24;

            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            _timer.Start();
        };
    }

    private void FadeOutAndClose()
    {
        var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        animation.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, animation);
    }
}
