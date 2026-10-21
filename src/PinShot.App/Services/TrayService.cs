using System.Drawing;
using System.Windows.Forms;

namespace PinShot.App.Services;

/// <summary>托盘菜单动作集合。</summary>
public sealed record TrayActions(
    Action Capture,
    Action LongShot,
    Action Gif,
    Action Record,
    Action Exit,
    Action? CloseAllPins = null,
    Action? DestroyAllPins = null,
    Action? BringPinsOnScreen = null,
    Action? Settings = null);

/// <summary>托盘图标与右键菜单。程序本身没有主窗口，所有入口都在这里和全局热键。</summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _generatedIcon;

    public TrayService(string hotkeyDisplay, string recordHotkeyDisplay, TrayActions actions)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add($"截图 ({hotkeyDisplay})", null, (_, _) => actions.Capture());
        menu.Items.Add("长截图 (滚动拼接)", null, (_, _) => actions.LongShot());
        menu.Items.Add("GIF 录制", null, (_, _) => actions.Gif());
        menu.Items.Add($"录屏 ({recordHotkeyDisplay})", null, (_, _) => actions.Record());

        if (actions.CloseAllPins is not null)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("关闭所有贴图", null, (_, _) => actions.CloseAllPins());
            menu.Items.Add("销毁所有贴图", null, (_, _) => actions.DestroyAllPins?.Invoke());
            menu.Items.Add("贴图拉回屏幕", null, (_, _) => actions.BringPinsOnScreen?.Invoke());
        }

        menu.Items.Add(new ToolStripSeparator());

        if (actions.Settings is not null)
        {
            menu.Items.Add("设置...", null, (_, _) => actions.Settings());
        }

        menu.Items.Add("退出", null, (_, _) => actions.Exit());

        _generatedIcon = CreateTrayIcon();
        _icon = new NotifyIcon
        {
            Icon = _generatedIcon,
            Text = $"PinShot - 截图与贴图 ({hotkeyDisplay})",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => actions.Capture();
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var accent = new SolidBrush(Color.FromArgb(45, 140, 255));
            using var white = new SolidBrush(Color.White);
            using var pen = new Pen(Color.White, 2f);

            graphics.FillEllipse(accent, 1, 1, 30, 30);
            graphics.DrawRectangle(pen, 9, 9, 14, 14);
            graphics.FillRectangle(white, 13, 13, 6, 6);
        }

        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _generatedIcon.Dispose();
    }
}
