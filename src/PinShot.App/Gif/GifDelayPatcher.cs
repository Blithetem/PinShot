using System.IO;

namespace PinShot.App.Gif;

/// <summary>
/// WPF GifBitmapEncoder 会把每帧延迟写成 0，导致动画飞快。
/// 这里直接改写 GIF 的 Graphic Control Extension 延迟字节，避免引入额外图像库。
/// </summary>
internal static class GifDelayPatcher
{
    /// <param name="delayHundredths">延迟，单位 1/100 秒。</param>
    public static void Apply(string path, int delayHundredths)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 14)
        {
            return;
        }

        var delay = (ushort)Math.Clamp(delayHundredths, 1, 65535);

        var position = 13;
        if ((data[10] & 0x80) != 0)
        {
            position += 3 * (1 << ((data[10] & 0x07) + 1));
        }

        while (position + 1 < data.Length)
        {
            var block = data[position];

            if (block == 0x21)
            {
                var label = data[position + 1];

                if (label == 0xF9)
                {
                    var size = data[position + 2];
                    if (size == 4)
                    {
                        data[position + 4] = (byte)(delay & 0xFF);
                        data[position + 5] = (byte)(delay >> 8);
                    }

                    position += 2 + 1 + size + 1;
                }
                else
                {
                    position += 2;
                    position = SkipSubBlocks(data, position);
                }
            }
            else if (block == 0x2C)
            {
                if (position + 9 >= data.Length)
                {
                    break;
                }

                var flags = data[position + 9];
                position += 10;

                if ((flags & 0x80) != 0)
                {
                    position += 3 * (1 << ((flags & 0x07) + 1));
                }

                position += 1; // LZW minimum code size
                position = SkipSubBlocks(data, position);
            }
            else
            {
                break;
            }
        }

        File.WriteAllBytes(path, data);
    }

    private static int SkipSubBlocks(byte[] data, int position)
    {
        while (position < data.Length)
        {
            var size = data[position];
            position += 1 + size;

            if (size == 0)
            {
                break;
            }
        }

        return position;
    }
}
