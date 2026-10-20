using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PinShot.App.Interop;

namespace PinShot.App.Capture;

/// <summary>
/// 屏幕捕获：当前使用 GDI CopyFromScreen（稳定、支持多屏拼接）。
/// 后续替换为 Windows.Graphics.Capture / DXGI Desktop Duplication 以降低延迟并支持
/// 独占全屏与 HDR 场景。
/// </summary>
public static class ScreenCapture
{
    public static (BitmapSource Image, Int32Rect Bounds) CaptureVirtualScreen()
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size,
                CopyPixelOperation.SourceCopy);
        }

        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return (source, new Int32Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        }
        finally
        {
            Native.DeleteObject(hBitmap);
        }
    }
}
