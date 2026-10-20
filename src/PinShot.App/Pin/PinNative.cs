using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PinShot.App.Pin;

/// <summary>贴图窗口需要的少量 Win32 辅助（鼠标穿透、全局热键）。Interop\Native.cs 归其他任务维护，这里独立声明。</summary>
internal static class PinNative
{
    private const int GWL_EXSTYLE = -20;

    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public static void EnableClickThrough(Window window, bool enabled)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetStyle(handle);
        var updated = enabled
            ? style | WS_EX_TRANSPARENT
            : style & ~WS_EX_TRANSPARENT;
        SetStyle(handle, updated);
    }

    public static void MakeToolWindowNoActivate(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetStyle(handle, GetStyle(handle) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private static int GetStyle(IntPtr handle)
        => IntPtr.Size == 8
            ? (int)GetWindowLongPtr64(handle, GWL_EXSTYLE).ToInt64()
            : GetWindowLong32(handle, GWL_EXSTYLE);

    private static void SetStyle(IntPtr handle, int style)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(handle, GWL_EXSTYLE, new IntPtr(style));
        }
        else
        {
            SetWindowLong32(handle, GWL_EXSTYLE, style);
        }
    }
}
