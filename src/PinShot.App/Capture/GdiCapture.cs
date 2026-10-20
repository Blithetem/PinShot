using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace PinShot.App.Capture;

/// <summary>
/// 直接 P/Invoke BitBlt 抓屏，并使用 CAPTUREBLT 标志。
/// GDI+ 的 CopyFromScreen 不带 CAPTUREBLT，抓 Electron/WebView2 等硬件加速窗口时
/// 经常出现黑块或旧内容残留；CAPTUREBLT 会把分层窗口一起合成进来。
/// </summary>
public static class GdiCapture
{
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint DibRgbColors = 0;
    private const int BiRgb = 0;

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbitmap, uint startScan, uint scanLines,
        byte[] bits, ref BitmapInfo bitmapInfo, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    /// <summary>抓取指定区域，返回 BGRA 像素（Top-Down，每行 width*4 字节）。</summary>
    public static bool TryCapture(Rectangle region, byte[] destination)
    {
        if (region.Width <= 0 || region.Height <= 0 ||
            destination.Length < region.Width * region.Height * 4)
        {
            return false;
        }

        // 窗口重绘（滚动/动画）时 BitBlt 可能抓到半成品画面：等合成完成再抓，
        // 并检测黑块比例，异常时重试。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            DwmFlush();

            var captured = CaptureOnce(region, destination);
            if (!captured)
            {
                return false;
            }

            if (!IsMostlyBlack(destination, region))
            {
                return true;
            }

            Thread.Sleep(attempt == 0 ? 20 : 45);
        }

        return true;
    }

    private static bool IsMostlyBlack(byte[] data, Rectangle region)
    {
        var pixels = region.Width * region.Height;
        if (pixels <= 0)
        {
            return false;
        }

        var black = 0;
        var sampled = 0;
        var step = Math.Max(1, pixels / 256);

        for (var index = 0; index < pixels; index += step)
        {
            var offset = index * 4;
            sampled++;

            if (data[offset] < 18 && data[offset + 1] < 18 && data[offset + 2] < 18)
            {
                black++;
            }
        }

        return sampled > 0 && black * 1.0 / sampled > 0.55;
    }

    private static bool CaptureOnce(Rectangle region, byte[] destination)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, region.Width, region.Height);
        if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
        {
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        var previous = SelectObject(memoryDc, bitmap);

        try
        {
            var blitted = BitBlt(memoryDc, 0, 0, region.Width, region.Height,
                screenDc, region.X, region.Y, SrcCopy | CaptureBlt);

            if (!blitted)
            {
                return false;
            }

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = region.Width,
                    Height = -region.Height, // 负高度 = Top-Down
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                    SizeImage = (uint)(region.Width * region.Height * 4),
                },
                Colors = DibRgbColors,
            };

            var scanLines = GetDIBits(memoryDc, bitmap, 0, (uint)region.Height,
                destination, ref info, 0);

            if (scanLines != region.Height)
            {
                return false;
            }

            // GetDIBits 的 32 位 BI_RGB 不保证 alpha，实际往往是 0。
            // AVI/MJPEG 只看 RGB 没事，但 GIF 编码按 RGBA 处理会把整帧当透明。
            var pixelCount = region.Width * region.Height * 4;
            for (var index = 3; index < pixelCount; index += 4)
            {
                destination[index] = 255;
            }

            return true;
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
