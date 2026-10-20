using System.Windows.Media.Imaging;

namespace PinShot.App.Capture;

public enum CaptureAction
{
    Copy,
    Save,
    Pin,
}

/// <summary>一次截图的结果：裁剪后的图像 + 用户选择的后继动作。</summary>
public sealed record CaptureResult(BitmapSource Image, CaptureAction Action);
