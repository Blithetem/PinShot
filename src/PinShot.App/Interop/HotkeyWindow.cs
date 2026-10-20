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

    // PixPin 习惯键位：Ctrl+1 截图。与上面的主热键并存，谁注册成功都能触发截图。
    private static readonly (uint Modifiers, uint VirtualKey, string Display)[] ExtraScreenshotCandidates =
    {
        (Native.MOD_CONTROL, 0x31, "Ctrl+1"),
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

    public HotkeyWindow()
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

        if (TryRegister(_source.Handle, ScreenshotHotkeyId, ScreenshotCandidates, out var screenshot))
        {
            HotkeyDisplay = screenshot;
        }
        else
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            throw new InvalidOperationException(
                "截图热键注册失败（Ctrl+Shift+S / Ctrl+Shift+X / Ctrl+Alt+S 均被占用），请通过托盘菜单截图。");
        }

        // 附加的 PixPin 习惯键位，注册不上不影响主流程
        TryRegister(_source.Handle, ExtraScreenshotHotkeyId, ExtraScreenshotCandidates, out _);

        RecordHotkeyDisplay = TryRegister(_source.Handle, RecordHotkeyId, RecordCandidates, out var record)
            ? record
            : "托盘菜单";

        if (TryRegister(_source.Handle, PinHotkeyId, PinCandidates, out var pin))
        {
            PinHotkeyDisplay = pin;
        }

        if (TryRegister(_source.Handle, RestorePinHotkeyId, RestorePinCandidates, out var restore))
        {
            RestorePinHotkeyDisplay = restore;
        }
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
