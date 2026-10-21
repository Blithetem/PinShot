using System.Windows.Input;

namespace PinShot.App.Interop;

/// <summary>把 "Ctrl+Shift+S" 这类文本和 Win32 热键参数互相转换。</summary>
public static class HotkeyParser
{
    /// <summary>MOD_WIN（Native 里没有定义该常量）。</summary>
    private const uint ModWin = 0x0008;

    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var mods = 0u;
        var keyPart = string.Empty;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= Native.MOD_CONTROL;
                    break;
                case "shift":
                    mods |= Native.MOD_SHIFT;
                    break;
                case "alt":
                    mods |= Native.MOD_ALT;
                    break;
                case "win":
                case "windows":
                    mods |= ModWin;
                    break;
                default:
                    keyPart = part;
                    break;
            }
        }

        if (keyPart.Length == 0 || !Enum.TryParse<Key>(keyPart, true, out var key) || key == Key.None)
        {
            return false;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            return false;
        }

        modifiers = mods;
        virtualKey = vk;
        return true;
    }

    /// <summary>把按下的一组键格式化成可读文本；不是有效组合时返回 null。</summary>
    public static string? Format(Key key, ModifierKeys modifierKeys)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None)
        {
            return null;
        }

        var parts = new List<string>();
        if (modifierKeys.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifierKeys.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifierKeys.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifierKeys.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        var name = key.ToString();
        if (name.Length == 0)
        {
            return null;
        }

        parts.Add(name);
        return string.Join("+", parts);
    }

    /// <summary>给界面展示用的友好文本（"Ctrl+Shift+S" → "Ctrl + Shift + S"）。</summary>
    public static string Pretty(string? text)
        => string.IsNullOrWhiteSpace(text) ? "未设置" : text.Replace("+", " + ");
}
