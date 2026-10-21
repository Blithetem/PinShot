using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PinShot.App.Capture;
using SharpAvi.Codecs;
using SharpAvi.Output;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace PinShot.App.Recorder;

/// <summary>
/// 屏幕录制：GDI 抓帧 → SharpAvi(MJPEG) 写成 AVI。
/// 不依赖系统 H.264 编码器，兼容性最好；代价是文件比 MP4 大。
/// 同时按 GIF 帧率收集缩放后的帧，供录制结束后直接导出 GIF。
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    // GIF 帧参数：宽度上限越大越清晰，但体积和内存也越高。
    // 720 宽在聊天窗口里够清晰，体积约为 1280 宽的三分之一。
    private const int GifMaxWidth = 720;
    private const int GifMaxFrames = 180;
    private const int GifFps = 8;

    private readonly Rectangle _area;
    private readonly int _fps;
    private readonly int _quality;
    private readonly int _frameBytes;
    private readonly double _gifEveryNthFrame;
    private double _gifAccumulator;
    private readonly List<BitmapSource> _gifFrames = new();
    private readonly object _gifLock = new();

    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;
    private int _frameIndex;

    /// <param name="area">录制区域。</param>
    /// <param name="fps">帧率；12fps 对屏幕操作演示足够，体积约为 30fps 的三分之一。</param>
    /// <param name="quality">JPEG 质量；65 能保持文字可读，体积明显小于 80。</param>
    public ScreenRecorder(Rectangle area, int fps = 12, int quality = 65)
    {
        if (area.Width < 2 || area.Height < 2)
        {
            throw new ArgumentException("录制区域太小", nameof(area));
        }

        // 窗口吸附的 DWM 扩展边界可能包含阴影、超出屏幕，负坐标会让 BitBlt 抓到全黑
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var clamped = Rectangle.Intersect(area, virtualScreen);
        if (clamped.Width < 2 || clamped.Height < 2)
        {
            throw new ArgumentException("录制区域超出屏幕范围", nameof(area));
        }

        _area = new Rectangle(clamped.X, clamped.Y,
            clamped.Width - (clamped.Width % 2), clamped.Height - (clamped.Height % 2));
        _fps = Math.Clamp(fps, 5, 60);
        _quality = Math.Clamp(quality, 20, 95);
        _frameBytes = _area.Width * _area.Height * 4;

        // 用浮点步进，避免 fps(12) 除 GifFps(8) 取整成 1 导致 GIF 实际帧率不对
        _gifEveryNthFrame = Math.Max(1.0, _fps / (double)GifFps);
    }

    public string? OutputPath { get; private set; }

    public Exception? Failure { get; private set; }

    public int Width => _area.Width;

    public int Height => _area.Height;

    /// <summary>录制期间按 GIF 帧率收集的帧（已缩放到 720 宽以内）。</summary>
    public IReadOnlyList<BitmapSource> GifFrames
    {
        get
        {
            lock (_gifLock)
            {
                var frames = _gifFrames.ToArray();

                // 停止瞬间抓到的最后一帧常常是刷新中的异常画面，丢弃
                return frames.Length > 1 ? frames[..^1] : frames;
            }
        }
    }

    public event Action<Exception>? Failed;

    public void Start(string outputPath)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("录制已在进行中");
        }

        OutputPath = outputPath;
        _running = true;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PinShotRecorder",
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;

        try
        {
            _thread?.Join(TimeSpan.FromSeconds(8));
        }
        catch
        {
            // 忽略线程退出异常
        }
    }

    private void Run()
    {
        AviWriter? writer = null;

        try
        {
            writer = new AviWriter(OutputPath!)
            {
                FramesPerSecond = _fps,
                EmitIndex1 = true,
            };

            var stream = EncodingStreamFactory.AddMJpegWpfVideoStream(
                writer, _area.Width, _area.Height, _quality);

            var frame = new byte[_frameBytes];
            var targetIntervalMs = 1000.0 / _fps;

            while (_running)
            {
                var frameStart = Stopwatch.GetTimestamp();

                if (!GdiCapture.TryCapture(_area, frame))
                {
                    Thread.Sleep(2);
                }

                stream.WriteFrame(true, frame, 0, _frameBytes);

                _frameIndex++;
                _gifAccumulator += 1.0;
                if (_gifAccumulator >= _gifEveryNthFrame)
                {
                    _gifAccumulator -= _gifEveryNthFrame;
                    CollectGifFrame(frame);
                }

                var elapsedMs = (Stopwatch.GetTimestamp() - frameStart) * 1000.0 / Stopwatch.Frequency;
                var delay = (int)(targetIntervalMs - elapsedMs);
                if (delay > 0)
                {
                    Thread.Sleep(delay);
                }
            }

            writer.Close();
        }
        catch (Exception ex)
        {
            Failure = ex;
            Failed?.Invoke(ex);
        }
        finally
        {
            if (writer is not null)
            {
                try
                {
                    writer.Close();
                }
                catch
                {
                    // 已经关闭过，忽略
                }
            }
        }
    }

    private void CollectGifFrame(byte[] frame)
    {
        try
        {
            lock (_gifLock)
            {
                if (_gifFrames.Count >= GifMaxFrames)
                {
                    return;
                }
            }

            // frame 是录制线程复用的缓冲区，BitmapSource.Create 不会复制像素数据，
            // 必须自己拷贝一份，否则后续帧会覆盖已收集帧的内容（GIF 画面错乱）。
            var pixels = new byte[frame.Length];
            Buffer.BlockCopy(frame, 0, pixels, 0, frame.Length);

            var source = BitmapSource.Create(_area.Width, _area.Height, 96, 96,
                PixelFormats.Bgra32, null, pixels, _area.Width * 4);
            source.Freeze();

            BitmapSource result = source;
            if (_area.Width > GifMaxWidth)
            {
                var scale = (double)GifMaxWidth / _area.Width;
                var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                scaled.Freeze();
                result = scaled;
            }

            lock (_gifLock)
            {
                if (_gifFrames.Count < GifMaxFrames)
                {
                    _gifFrames.Add(result);
                }
            }
        }
        catch
        {
            // GIF 帧收集失败不影响 AVI 录制
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
