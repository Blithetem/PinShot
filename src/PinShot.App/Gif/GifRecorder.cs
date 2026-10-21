using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PinShot.App.Capture;

namespace PinShot.App.Gif;

/// <summary>
/// GIF 录制：按固定帧率抓取选区并缩放，停止后用 WPF GifBitmapEncoder 编码。
/// 帧保存在内存中，因此限制最大帧数与宽度，避免录制过久占满内存。
/// </summary>
public sealed class GifRecorder : IDisposable
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private readonly Rectangle _area;
    private readonly int _fps;
    private readonly int _maxFrames;
    private readonly double _scale;
    private readonly int _targetWidth;
    private readonly int _targetHeight;
    private readonly List<BitmapSource> _frames = new();
    private readonly object _frameLock = new();

    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;

    public GifRecorder(Rectangle area, int fps = 8, int maxFrames = 240, int maxWidth = 720)
    {
        if (area.Width < 4 || area.Height < 4)
        {
            throw new ArgumentException("录制区域太小", nameof(area));
        }

        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var clamped = Rectangle.Intersect(area, virtualScreen);
        if (clamped.Width < 4 || clamped.Height < 4)
        {
            throw new ArgumentException("录制区域超出屏幕范围", nameof(area));
        }

        _area = clamped;
        _fps = Math.Clamp(fps, 5, 20);
        _maxFrames = Math.Clamp(maxFrames, 10, 600);

        _scale = Math.Min(1.0, (double)maxWidth / area.Width);
        _targetWidth = Math.Max(2, (int)(area.Width * _scale));
        _targetHeight = Math.Max(2, (int)(area.Height * _scale));

    }

    public string? OutputPath { get; private set; }

    public Exception? Failure { get; private set; }

    public int FrameCount
    {
        get
        {
            lock (_frameLock)
            {
                return _frames.Count;
            }
        }
    }

    public bool IsFull => FrameCount >= _maxFrames;

    public event Action? FrameCaptured;

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("GIF 录制已在进行中");
        }

        _running = true;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PinShotGifRecorder",
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;

        try
        {
            _thread?.Join(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 忽略线程退出异常
        }
    }

    private void Run()
    {
        Bitmap? bitmap = null;
        Graphics? graphics = null;

        try
        {
            bitmap = new Bitmap(_area.Width, _area.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            graphics = Graphics.FromImage(bitmap);

            var frameBytes = _area.Width * _area.Height * 4;
            var rect = new Rectangle(0, 0, _area.Width, _area.Height);
            var targetInterval = 1000.0 / _fps;

            while (_running && FrameCount < _maxFrames)
            {
                var start = Stopwatch.GetTimestamp();

                // 等 DWM 合成完成再抓帧，避免抓到半成品画面（黑块/撕裂）
                var pixels = new byte[frameBytes];
                GdiCapture.TryCapture(_area, pixels);

                var source = BitmapSource.Create(_area.Width, _area.Height, 96, 96,
                    PixelFormats.Bgra32, null, pixels, _area.Width * 4);
                source.Freeze();

                BitmapSource frame = source;
                if (Math.Abs(_scale - 1.0) > 0.001)
                {
                    var scaled = new TransformedBitmap(source, new ScaleTransform(_scale, _scale));
                    scaled.Freeze();
                    frame = scaled;
                }

                lock (_frameLock)
                {
                    _frames.Add(frame);
                }

                FrameCaptured?.Invoke();

                var elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                var delay = (int)(targetInterval - elapsed);
                if (delay > 0)
                {
                    Thread.Sleep(delay);
                }
            }
        }
        catch (Exception ex)
        {
            Failure = ex;
        }
        finally
        {
            graphics?.Dispose();
            bitmap?.Dispose();
        }
    }

    /// <summary>把已录制帧编码成 GIF 并保存，返回帧数。</summary>
    public int Save(string path)
    {
        List<BitmapSource> frames;
        lock (_frameLock)
        {
            frames = new List<BitmapSource>(_frames);
        }

        if (frames.Count == 0)
        {
            throw new InvalidOperationException("没有可保存的帧");
        }

        OutputPath = path;

        // 统一走 ImageSharp 编码器：全局调色板 + 抖动，比 WPF 编码器清晰得多
        return GifEncoder.Save(frames, path, _fps);
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
