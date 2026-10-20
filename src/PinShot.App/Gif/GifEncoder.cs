using System.IO;
using System.Windows.Media.Imaging;

namespace PinShot.App.Gif;

/// <summary>
/// 把一组位图帧编码成动画 GIF。
/// 用 WPF 的 GifBitmapEncoder（每帧独立调色板、全帧写入），兼容性最好；
/// ImageSharp 的全局调色板会按第一帧生成，滚动/动画内容的新颜色会被量化成黑。
/// </summary>
public static class GifEncoder
{
    public static int Save(IReadOnlyList<BitmapSource> frames, string path, int fps = 10)
    {
        if (frames.Count == 0)
        {
            throw new InvalidOperationException("没有可保存的帧");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new GifBitmapEncoder();
        foreach (var frame in frames)
        {
            encoder.Frames.Add(BitmapFrame.Create(frame));
        }

        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        // WPF 编码器把每帧延迟写成 0，这里补上正确值
        GifDelayPatcher.Apply(path, Math.Max(2, 100 / Math.Clamp(fps, 5, 20)));
        return frames.Count;
    }
}