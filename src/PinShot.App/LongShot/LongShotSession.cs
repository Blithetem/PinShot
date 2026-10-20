using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PinShot.App.LongShot;

/// <summary>
/// 长截图拼接：逐帧抓取选区，用行签名检测滚动位移，把新增内容追加到画布。
/// 拼接结果按 BGRA 像素数组累积，结束后一次性生成 BitmapSource。
/// </summary>
public sealed class LongShotSession
{
    private const int SignatureSamples = 32;

    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly MemoryStream _output = new();

    private byte[]? _previous;

    public LongShotSession(int width, int height)
    {
        if (width < 4 || height < 4)
        {
            throw new ArgumentException("选区太小");
        }

        _width = width;
        _height = height;
        _stride = width * 4;
    }

    public int Width => _width;

    public int CurrentHeight => (int)(_output.Length / _stride);

    public int MaxHeight { get; set; } = 12000;

    public bool IsFull => CurrentHeight >= MaxHeight;

    /// <summary>追加一帧（BGRA，尺寸为 width×height）。返回是否有新增内容。</summary>
    public bool AddFrame(byte[] frame)
    {
        if (frame.Length != _stride * _height)
        {
            throw new ArgumentException("帧尺寸不匹配", nameof(frame));
        }

        if (_previous is null)
        {
            WriteRows(frame, 0, _height);
            _previous = frame;
            return true;
        }

        var offset = DetectScrollOffset(_previous, frame, _width, _height);
        if (offset <= 0)
        {
            return false;
        }

        if (IsFull || CurrentHeight + offset > MaxHeight)
        {
            return false;
        }

        // 追加新出现的底部 offset 行
        WriteRows(frame, _height - offset, offset);
        _previous = frame;
        return true;
    }

    /// <summary>把已拼接内容生成为位图。高度超过上限时按上限截断。</summary>
    public BitmapSource BuildBitmap()
    {
        var height = Math.Min(CurrentHeight, MaxHeight);
        if (height <= 0)
        {
            height = 1;
        }

        var pixels = _output.ToArray();
        var needed = _stride * height;

        if (pixels.Length < needed)
        {
            Array.Resize(ref pixels, needed);
        }
        else if (pixels.Length > needed)
        {
            Array.Resize(ref pixels, needed);
        }

        var bitmap = BitmapSource.Create(_width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, _stride);
        bitmap.Freeze();
        return bitmap;
    }

    private void WriteRows(byte[] frame, int startRow, int rowCount)
    {
        var offset = startRow * _stride;
        _output.Write(frame, offset, rowCount * _stride);
    }

    /// <summary>
    /// 在下一帧中查找上一帧底部区域的匹配位置，返回滚动像素数（0 表示未滚动）。
    /// 使用行灰度签名做粗匹配，容忍少量噪声。
    /// </summary>
    public static int DetectScrollOffset(byte[] previous, byte[] next, int width, int height)
    {
        var overlapRows = height * 2 / 3;
        if (overlapRows < 8)
        {
            return 0;
        }

        var previousSignatures = new int[overlapRows * SignatureSamples];
        ComputeSignatures(previous, width, height - overlapRows, overlapRows, previousSignatures);

        var maxSearch = height - overlapRows;
        var bestY = -1;
        long bestError = long.MaxValue;

        for (var y = 0; y <= maxSearch; y += 2)
        {
            long error = 0;

            for (var row = 0; row < overlapRows; row += 4)
            {
                var rowBase = (y + row) * width * 4;
                var signatureBase = row * SignatureSamples;

                for (var sample = 0; sample < SignatureSamples; sample += 2)
                {
                    var x = sample * width / SignatureSamples;
                    var index = rowBase + x * 4;
                    var gray = (next[index] + next[index + 1] + next[index + 2]) / 3;
                    var delta = gray - previousSignatures[signatureBase + sample];
                    error += delta * delta;
                }

                if (error > bestError)
                {
                    break;
                }
            }

            if (error < bestError)
            {
                bestError = error;
                bestY = y;
            }
        }

        if (bestY < 0)
        {
            return 0;
        }

        var compared = (overlapRows / 4) * (SignatureSamples / 2);
        var averageError = bestError / (double)Math.Max(1, compared);
        if (averageError > 3000)
        {
            // 匹配度太低，判定为内容变化而不是滚动
            return 0;
        }

        var offset = (height - overlapRows) - bestY;
        if (offset <= 0 || offset > height * 2 / 3)
        {
            return 0;
        }

        return offset;
    }

    private static void ComputeSignatures(byte[] frame, int width, int startRow, int rows,
        int[] destination)
    {
        for (var row = 0; row < rows; row++)
        {
            var rowBase = (startRow + row) * width * 4;
            var signatureBase = row * SignatureSamples;

            for (var sample = 0; sample < SignatureSamples; sample++)
            {
                var x = sample * width / SignatureSamples;
                var index = rowBase + x * 4;
                destination[signatureBase + sample] =
                    (frame[index] + frame[index + 1] + frame[index + 2]) / 3;
            }
        }
    }
}
