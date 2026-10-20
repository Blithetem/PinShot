using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace PinShot.App.Interop;

/// <summary>
/// 窗口吸附：按 Z 序枚举顶层窗口，找到包含鼠标点的最上层窗口，返回其 DWM 扩展边界（物理像素）。
/// 优先选择有标题的应用窗口；没有时再退化为无标题窗口，但跳过覆盖整个屏幕的壁纸/遮罩层。
/// </summary>
public static class WindowSnap
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "MultitaskingViewFrame",
        "ForegroundStaging",
        "Windows.Internal.Shell.TabProxyWindow",
        "TaskListThumbnailWnd",
        "tooltips_class32",
        "#32768",
    };

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>查找鼠标下最合适的窗口：优先有标题的应用窗口，其次无标题的非全屏窗口。</summary>
    public static Int32Rect? FindTopWindowAt(int screenX, int screenY, int excludeProcessId)
    {
        return Enumerate(screenX, screenY, excludeProcessId, requireTitle: true)
               ?? Enumerate(screenX, screenY, excludeProcessId, requireTitle: false);
    }

    private static Int32Rect? Enumerate(int screenX, int screenY, int excludeProcessId,
        bool requireTitle)
    {
        var virtualWidth = GetSystemMetrics(SmCxVirtualScreen);
        var virtualHeight = GetSystemMetrics(SmCyVirtualScreen);
        Int32Rect? found = null;

        EnumWindowsProc callback = (hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsCloaked(hwnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == (uint)excludeProcessId)
            {
                return true;
            }

            if (ExcludedClasses.Contains(GetClassNameOf(hwnd)))
            {
                return true;
            }

            var hasTitle = GetWindowTextLength(hwnd) > 0;
            if (requireTitle != hasTitle)
            {
                return true;
            }

            if (!TryGetBounds(hwnd, out var rect))
            {
                return true;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 8 || height < 8)
            {
                return true;
            }

            // 铺满整个虚拟屏幕的窗口一律跳过：壁纸层、桌面合成层，
            // 以及远程桌面（ToDesk/向日葵等）的全屏窗口——它们不是用户的截图目标。
            if (width >= virtualWidth && height >= virtualHeight)
            {
                return true;
            }

            if (screenX < rect.Left || screenX >= rect.Right ||
                screenY < rect.Top || screenY >= rect.Bottom)
            {
                return true;
            }

            found = new Int32Rect(rect.Left, rect.Top, width, height);
            return false;
        };

        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }

    private static bool TryGetBounds(IntPtr hwnd, out RECT rect)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<RECT>()) == 0)
        {
            return true;
        }

        return GetWindowRect(hwnd, out rect);
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) != 0)
        {
            return false;
        }

        return cloaked != 0;
    }

    private static string GetClassNameOf(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        var length = GetClassName(handle, builder, builder.Capacity);
        return length > 0 ? builder.ToString(0, length) : string.Empty;
    }
}
