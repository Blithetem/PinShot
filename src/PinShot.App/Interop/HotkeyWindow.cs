using System.Windows.Interop;
using PinShot.App.Interop;

namespace PinShot.App.Interop;

/// <summary>
/// 隐藏的消息窗口，用于接收 WM_HOTKEY。使用 HwndSource 而不创建可见 Window。
/// 截图与录屏各注册一个热键，首选组合被占用时自动尝试备用组合。
/// </summary>
public sealed class HotkeyWindow : IDisposable
{
    private const int ScreenshotHotkeyId = 0x5010;
    private const int RecordHotkeyId = 0x5011;
    private const int PinHotkeyId = 0x5012;
    private const int RestorePinHotkeyId = 0x5013;
    private const int ExtraScreenshotHotkeyId = 0x5014;

    private static readonly (uint Modifiers, uint VirtualKey, string Display)[] ScreenshotCandidates =
    {
        // Ctrl+Shift+A / Ctrl+Alt+A 在本机分别被其他程序和 PixPin 占用，改用 Ctrl+Shift+S
        (Native.MOD_CONTROL | Native.MOD_SHIFT, 0x53, "Ctrl+Shift+S"),
        (Native.MOD_CONTROL | Native.MOD_SHIFT, 0x58, "Ctrl+Shift+X"),
        (Native.MOD_CONTROL | Native.MOD_ALT, 0x53, "Ctrl+Alt+S"),
    };

    private static readonly (uint Modifiers, uint VirtualKey, string Display)[] PinCandidates =
    {
        (Native.MOD_CONTROL, 0x32, "Ctrl+2"),
    };

    private static readonly (uint Modifiers, uint VirtualKey, string Display)[] RestorePinCandidates =
    {
        (Native.MOD_CONTROL, 0x33, "Ctrl+3"),
    };

    private static readonly (uint Modifiers, uint VirtualKey, string Display)[] RecordCandidates =
    {
        (Native.MOD_CONTROL | Native.MOD_SHIFT, 0x52, "Ctrl+Shift+R"),
        (Native.MOD_CONTROL | Native.MOD_ALT, 0x52, "Ctrl+Alt+R"),
    };

    private readonly HwndSource _source;
    private bool _disposed;

    /// <summary>截图热键触发。</summary>
    public event Action? HotkeyPressed;

    /// <summary>录屏热键触发（录制中按下用于停止）。</summary>
    public event Action? RecordHotkeyPressed;

    /// <summary>Ctrl+2：把剪贴板内容贴到屏幕。</summary>
    public event Action? PinHotkeyPressed;

    /// <summary>Ctrl+3：恢复最近关闭的贴图。</summary>
    public event Action? RestorePinHotkeyPressed;

    /// <summary>实际注册成功的热键组合。</summary>
    public string HotkeyDisplay { get; }

    /// <summary>录屏热键组合，注册失败时为「托盘菜单」。</summary>
    public string RecordHotkeyDisplay { get; }

    /// <summary>贴图热键组合，注册失败时为空。</summary>
    public string PinHotkeyDisplay { get; } = string.Empty;

    /// <summary>恢复贴图热键组合，注册失败时为空。</summary>
    public string RestorePinHotkeyDisplay { get; } = string.Empty;

    /// <summary>
    /// 注册全局热键。优先使用传入的配置值，注册失败时回退到内置候选组合，
    /// 保证即使用户配置了一个被占用/非法的键也不会失去截图入口。
    /// </summary>
    public HotkeyWindow(string? captureHotkey = null, string? pinHotkey = null,
        string? restorePinHotkey = null, string? recordHotkey = null)
    {
        var parameters = new HwndSourceParameters("PinShotHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = Native.WS_EX_TOOLWINDOW,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        if (TryRegisterPreferred(_source.Handle, ScreenshotHotkeyId, captureHotkey,
                ScreenshotCandidates, out var screenshot))
        {
            HotkeyDisplay = screenshot;
        }
        else
        {
            // 用户把截图热键也清空了：允许没有热键，入口走托盘菜单
            HotkeyDisplay = "托盘菜单";
        }

        // 附加的 PixPin 习惯键位，注册不上不影响主流程
        if (HotkeyParser.TryParse("Ctrl+1", out var extraMods, out var extraKey) &&
            !string.Equals(captureHotkey, "Ctrl+1", StringComparison.OrdinalIgnoreCase))
        {
            Native.RegisterHotKey(_source.Handle, ExtraScreenshotHotkeyId,
                extraMods | Native.MOD_NOREPEAT, extraKey);
        }

        RecordHotkeyDisplay = TryRegisterPreferred(_source.Handle, RecordHotkeyId, recordHotkey,
            RecordCandidates, out var record)
            ? record
            : "托盘菜单";

        if (TryRegisterPreferred(_source.Handle, PinHotkeyId, pinHotkey, PinCandidates, out var pin))
        {
            PinHotkeyDisplay = pin;
        }

        if (TryRegisterPreferred(_source.Handle, RestorePinHotkeyId, restorePinHotkey,
                RestorePinCandidates, out var restore))
        {
            RestorePinHotkeyDisplay = restore;
        }
    }

    /// <summary>先试用户配置的组合，失败再按内置候选逐个尝试。</summary>
    /// <remarks>配置为空表示明确「不设置」：不注册，也不回退到候选键。</remarks>
    private static bool TryRegisterPreferred(IntPtr handle, int id, string? preferred,
        (uint Modifiers, uint VirtualKey, string Display)[] fallbacks, out string display)
    {
        if (string.IsNullOrWhiteSpace(preferred))
        {
            display = string.Empty;
            return false;
        }

        if (HotkeyParser.TryParse(preferred, out var modifiers, out var virtualKey) &&
            Native.RegisterHotKey(handle, id, modifiers | Native.MOD_NOREPEAT, virtualKey))
        {
            display = preferred!;
            return true;
        }

        return TryRegister(handle, id, fallbacks, out display);
    }

    private static bool TryRegister(IntPtr handle, int id,
        (uint Modifiers, uint VirtualKey, string Display)[] candidates, out string display)
    {
        foreach (var candidate in candidates)
        {
            if (Native.RegisterHotKey(handle, id,
                    candidate.Modifiers | Native.MOD_NOREPEAT, candidate.VirtualKey))
            {
                display = candidate.Display;
                return true;
            }
        }

        display = string.Empty;
        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        var id = wParam.ToInt32();
        if (id == ScreenshotHotkeyId || id == ExtraScreenshotHotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke();
        }
        else if (id == RecordHotkeyId)
        {
            handled = true;
            RecordHotkeyPressed?.Invoke();
        }
        else if (id == PinHotkeyId)
        {
            handled = true;
            PinHotkeyPressed?.Invoke();
        }
        else if (id == RestorePinHotkeyId)
        {
            handled = true;
            RestorePinHotkeyPressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Native.UnregisterHotKey(_source.Handle, ScreenshotHotkeyId);
        Native.UnregisterHotKey(_source.Handle, ExtraScreenshotHotkeyId);
        Native.UnregisterHotKey(_source.Handle, RecordHotkeyId);
        Native.UnregisterHotKey(_source.Handle, PinHotkeyId);
        Native.UnregisterHotKey(_source.Handle, RestorePinHotkeyId);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
