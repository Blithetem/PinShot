using System.IO;
using System.Text.Json;
using System.Windows;

namespace PinShot.App.Capture;

/// <summary>记住最近的截图区域，支持 R / Shift+R 快速还原（对齐 PixPin）。</summary>
public static class CaptureSelectionStore
{
    private const int MaxItems = 10;
    private const int MaxFileBytes = 64 * 1024;

    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static List<Int32Rect>? _items;
    private static int _cursor;

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PinShot");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "selection.json");
        }
    }

    /// <summary>记录一次成功的截图区域（最新的在前，最多 10 条）。</summary>
    public static void Remember(Int32Rect region)
    {
        if (region.Width < 2 || region.Height < 2)
        {
            return;
        }

        lock (Sync)
        {
            var items = Load();

            items.RemoveAll(item => item.X == region.X && item.Y == region.Y &&
                                    item.Width == region.Width && item.Height == region.Height);
            items.Insert(0, region);

            if (items.Count > MaxItems)
            {
                items.RemoveRange(MaxItems, items.Count - MaxItems);
            }

            _items = items;
            _cursor = 0;
            Save(items);
        }
    }

    /// <summary>
    /// 取上一条选区。<paramref name="forward"/> 为 true 时在历史里循环切换。
    /// 返回 null 表示没有记录。
    /// </summary>
    public static Int32Rect? Previous(bool forward)
    {
        lock (Sync)
        {
            var items = Load();
            if (items.Count == 0)
            {
                return null;
            }

            if (forward)
            {
                _cursor = (_cursor + 1) % items.Count;
            }
            else
            {
                _cursor = 0;
            }

            return items[_cursor];
        }
    }

    public static int Count
    {
        get
        {
            lock (Sync)
            {
                return Load().Count;
            }
        }
    }

    private static List<Int32Rect> Load()
    {
        if (_items is not null)
        {
            return _items;
        }

        _items = new List<Int32Rect>();

        try
        {
            var path = FilePath;
            if (!File.Exists(path) || new FileInfo(path).Length is 0 or > MaxFileBytes)
            {
                return _items;
            }

            var payload = JsonSerializer.Deserialize<SelectionFile>(File.ReadAllText(path));
            if (payload?.Items is null)
            {
                return _items;
            }

            foreach (var item in payload.Items)
            {
                if (item is { Width: >= 2, Height: >= 2 } &&
                    _items.Count < MaxItems)
                {
                    _items.Add(item);
                }
            }
        }
        catch
        {
            // 损坏的配置文件直接忽略，不影响截图主流程。
            _items = new List<Int32Rect>();
        }

        return _items;
    }

    private static void Save(List<Int32Rect> items)
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(new SelectionFile { Items = items }, JsonOptions));
        }
        catch
        {
            // 写盘失败不影响取址。
        }
    }

    private sealed class SelectionFile
    {
        public List<Int32Rect>? Items { get; set; }
    }
}
