using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace PinShot.App.Pin;

/// <summary>
/// 一条关闭进历史的贴图快照。图像（含标注/图像处理结果）以 PNG 形式落盘，
/// 重启后仍可 Ctrl+3 恢复。
/// </summary>
public sealed class PinHistoryEntry
{
    public PinContentKind Kind { get; set; } = PinContentKind.Image;

    public string? Title { get; set; }

    public string? Text { get; set; }

    public string? ColorHex { get; set; }

    public List<string>? Files { get; set; }

    /// <summary>关闭时的窗口位置/大小（DIP），恢复时沿用。</summary>
    public double Left { get; set; } = double.NaN;

    public double Top { get; set; } = double.NaN;

    public double Width { get; set; }

    public double Height { get; set; }

    /// <summary>PNG 文件名（相对 pin-history 目录）。</summary>
    public string? ImageFile { get; set; }

    public DateTimeOffset ClosedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public BitmapSource? Image { get; set; }

    /// <summary>恢复成可直接显示的贴图内容。</summary>
    public PinContent ToContent()
    {
        var image = Image;
        if (image is null)
        {
            image = Kind switch
            {
                PinContentKind.Text => PinContentRenderer.RenderText(Text ?? string.Empty),
                PinContentKind.Color => PinContentRenderer.RenderColor(ParseColor(ColorHex)),
                PinContentKind.Files => PinContentRenderer.RenderFiles(Files ?? new List<string>()),
                _ => null,
            };
        }

        return new PinContent
        {
            Kind = Kind,
            Image = image,
            Text = Text,
            ColorHex = ColorHex,
            Files = Files ?? new List<string>(),
            Title = Title,
        };
    }

    internal static System.Windows.Media.Color ParseColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                return (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                    .ConvertFromString(hex)!;
            }
            catch
            {
                // 落到默认色
            }
        }

        return System.Windows.Media.Colors.DodgerBlue;
    }
}

/// <summary>
/// 关闭贴图的历史（最近 N 条，默认 10）。索引写 pin-history.json，图像写 pin-history\*.png。
/// 销毁的贴图不会进入这里。
/// </summary>
public sealed class PinHistory
{
    public const int DefaultCapacity = 10;

    private const string ImageDirectoryName = "pin-history";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();

    public int Capacity { get; set; } = DefaultCapacity;

    public List<PinHistoryEntry> Entries { get; private set; } = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return Entries.Count;
            }
        }
    }

    public static string IndexPath => Path.Combine(PinPaths.BaseDirectory, "pin-history.json");

    public static string ImageDirectory => Path.Combine(PinPaths.BaseDirectory, ImageDirectoryName);

    public static PinHistory Load(int capacity = DefaultCapacity)
    {
        var history = new PinHistory { Capacity = capacity };

        try
        {
            if (File.Exists(IndexPath))
            {
                var entries = JsonSerializer.Deserialize<List<PinHistoryEntry>>(File.ReadAllText(IndexPath));
                if (entries is not null)
                {
                    history.Entries = entries;
                }
            }
        }
        catch
        {
            history.Entries = new List<PinHistoryEntry>();
        }

        // 补载 PNG；图已丢失且非文本/颜色/文件贴图的条目直接丢弃，避免恢复出空窗口
        var kept = new List<PinHistoryEntry>();
        foreach (var entry in history.Entries)
        {
            var file = string.IsNullOrEmpty(entry.ImageFile)
                ? null
                : Path.Combine(ImageDirectory, entry.ImageFile);

            if (file is not null && File.Exists(file))
            {
                try
                {
                    entry.Image = LoadPng(file);
                }
                catch
                {
                    entry.Image = null;
                }
            }

            if (entry.Image is null && entry.Kind == PinContentKind.Image)
            {
                continue;
            }

            kept.Add(entry);
        }

        history.Entries = kept;
        history.Trim(deleteImages: true);
        return history;
    }

    /// <summary>关闭贴图时调用：存 PNG、插到最前、超容量淘汰最旧。</summary>
    public void Add(PinHistoryEntry entry)
    {
        lock (_gate)
        {
            if (Capacity <= 0)
            {
                return;
            }

            try
            {
                PinPaths.EnsureDirectory(ImageDirectoryName);
                var name = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png";
                var path = Path.Combine(ImageDirectory, name);
                SavePng(entry.Image, path);
                entry.ImageFile = name;
            }
            catch
            {
                // 落盘失败仍然保留在内存历史里
                entry.ImageFile = null;
            }

            Entries.Insert(0, entry);
            Trim(deleteImages: true);
            Save();
        }
    }

    /// <summary>取最近关闭的一条（Ctrl+3），并从历史中移除。</summary>
    public PinHistoryEntry? PopLast()
    {
        lock (_gate)
        {
            if (Entries.Count == 0)
            {
                return null;
            }

            var entry = Entries[0];
            Entries.RemoveAt(0);

            // 恢复后该图会被窗口重新接管，历史里的 PNG 不再需要
            DeleteImageFile(entry);
            entry.ImageFile = null;

            Save();
            return entry;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in Entries)
            {
                DeleteImageFile(entry);
            }

            Entries.Clear();
            Save();
        }
    }

    public void Save()
    {
        try
        {
            PinPaths.EnsureDirectory();
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(Entries, JsonOptions));
        }
        catch
        {
            // 历史索引写失败不影响关闭流程
        }
    }

    private void Trim(bool deleteImages)
    {
        while (Entries.Count > Math.Max(0, Capacity))
        {
            var last = Entries[^1];
            Entries.RemoveAt(Entries.Count - 1);
            if (deleteImages)
            {
                DeleteImageFile(last);
            }
        }
    }

    private static void DeleteImageFile(PinHistoryEntry entry)
    {
        if (string.IsNullOrEmpty(entry.ImageFile))
        {
            return;
        }

        try
        {
            var path = Path.Combine(ImageDirectory, entry.ImageFile);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉就留着，下次清理
        }
    }

    private static void SavePng(BitmapSource? image, string path)
    {
        if (image is null)
        {
            return;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource LoadPng(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
