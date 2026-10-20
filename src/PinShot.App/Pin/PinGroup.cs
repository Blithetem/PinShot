using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media;

namespace PinShot.App.Pin;

/// <summary>贴图分组：只有名字和颜色，不保存窗口位置/大小。</summary>
public sealed class PinGroup
{
    public const string DefaultName = "默认";

    public string Name { get; set; } = DefaultName;

    /// <summary>ARGB 颜色值（#AARRGGBB 的 uint 形式），JSON 友好。</summary>
    public uint ColorArgb { get; set; } = 0xFF2D8CFF;

    public static PinGroup Create(string name, uint? argb = null) => new()
    {
        Name = name,
        ColorArgb = argb ?? RandomColorArgb(),
    };

    public static uint RandomColorArgb()
    {
        // 固定一组高辨识度颜色，避免完全随机带来的脏色
        uint[] palette =
        {
            0xFF2D8CFF, 0xFF2FA84F, 0xFFF08A24, 0xFF8B5CF6,
            0xFFE83A3A, 0xFF12B5B0, 0xFFC2185B, 0xFF6D8B32,
        };
        return palette[Random.Shared.Next(palette.Length)];
    }

    public Color ToColor()
    {
        var value = ColorArgb;
        return Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    public static uint FromColor(Color color)
        => ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
}

/// <summary>
/// 贴图配置：%LOCALAPPDATA%\PinShot\pin.json。
/// 只持久化「分组名/颜色」「当前分组视角」「默认阴影开关」——窗口本身不恢复（对齐 PixPin）。
/// </summary>
public sealed class PinGroupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public List<PinGroup> Groups { get; set; } = new() { new PinGroup { Name = PinGroup.DefaultName } };

    /// <summary>当前分组视角；null = 显示全部。</summary>
    public string? ActiveGroup { get; set; }

    /// <summary>新建贴图默认是否带阴影。</summary>
    public bool ShowShadow { get; set; } = true;

    public static string ConfigPath => Path.Combine(PinPaths.BaseDirectory, "pin.json");

    public PinGroup EnsureGroup(string name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? PinGroup.DefaultName : name.Trim();
        var group = Groups.FirstOrDefault(g =>
            string.Equals(g.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            group = PinGroup.Create(trimmed);
            Groups.Add(group);
        }

        return group;
    }

    public static PinGroupStore Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var store = JsonSerializer.Deserialize<PinGroupStore>(File.ReadAllText(ConfigPath));
                if (store is not null)
                {
                    store.Groups ??= new List<PinGroup>();
                    if (store.Groups.Count == 0)
                    {
                        store.Groups.Add(new PinGroup { Name = PinGroup.DefaultName });
                    }

                    return store;
                }
            }
        }
        catch
        {
            // 配置损坏时用默认值，不阻断贴图
        }

        return new PinGroupStore();
    }

    public void Save()
    {
        try
        {
            PinPaths.EnsureDirectory();
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 配置写失败不影响贴图本身
        }
    }
}
