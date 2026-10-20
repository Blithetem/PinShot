using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PinShot.App.Pin;

/// <summary>
/// 贴图统一入口：所有贴图窗口的创建/登记/关闭/销毁/历史恢复/分组视角/持久化都从这里走。
/// <para>
/// 快捷键约定（由 PinWindow 实现）：Esc/Ctrl+W/双击 = 关闭进历史；Ctrl+D = 销毁；
/// Ctrl+3（全局）= 恢复最近关闭；L/T/Space/F2/R/Ctrl+~ 等见 PinWindow。
/// </para>
/// </summary>
public static class PinManager
{
    private static readonly object Gate = new();
    private static readonly List<PinWindow> WindowsList = new();

    private static PinHistory? _history;
    private static PinGroupStore? _store;
    private static PinHotkey? _hotkey;
    private static string? _activeGroup;
    private static bool _initialized;
    private static bool _exitHooked;
    private static bool _suppressHotkey;
    private static bool _suppressConfirmDialogs;

    /// <summary>贴图集合/分组/历史发生变化（托盘菜单等可据此刷新）。</summary>
    public static event Action? PinsChanged;

    // ---------------- 生命周期 ----------------

    /// <summary>提前初始化：加载 pin.json / 历史，并注册全局 Ctrl+3。App 启动时调用一次即可。</summary>
    public static void Initialize() => EnsureInitialized();

    /// <summary>进程退出前调用（可选）：释放全局热键并保存历史索引/分组配置。</summary>
    public static void Shutdown()
    {
        try
        {
            _hotkey?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _hotkey = null;
        SaveAll();
    }

    /// <summary>
    /// 自动化自检用：把 pin.json / 历史目录重定向到指定目录（null 恢复默认）。
    /// 调用前应先关闭所有贴图；会重置管理器内部缓存与全局热键。
    /// </summary>
    public static void UseDataDirectory(string? directory)
    {
        PinPaths.Override(directory);
        ResetAllState();
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            _store = PinGroupStore.Load();
            _history = PinHistory.Load(PinHistory.DefaultCapacity);
            _activeGroup = _store.ActiveGroup;
        }
        catch
        {
            _store ??= new PinGroupStore();
            _history ??= new PinHistory();
        }

        if (!_suppressHotkey && _hotkey is null)
        {
            try
            {
                _hotkey = PinHotkey.TryCreate();
                if (_hotkey is not null)
                {
                    _hotkey.Pressed += RestoreLast;
                    _hotkey.GroupPressed += CycleGroupView;
                }
            }
            catch
            {
                _hotkey = null;
            }
        }

        if (!_exitHooked && Application.Current is not null)
        {
            _exitHooked = true;
            Application.Current.Exit += (_, _) => SaveAll();
        }
    }

    /// <summary>所有贴图窗口的创建都必须经过这里（PinWindow 构造函数也会自动登记）。</summary>
    internal static void Register(PinWindow window)
    {
        EnsureInitialized();

        lock (Gate)
        {
            if (!WindowsList.Contains(window))
            {
                WindowsList.Add(window);
            }
        }

        window.Closed += OnWindowClosed;
        RaiseChanged();
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is PinWindow window)
        {
            window.Closed -= OnWindowClosed;
            Unregister(window);
        }
    }

    private static void Unregister(PinWindow window)
    {
        lock (Gate)
        {
            WindowsList.Remove(window);
        }

        RaiseChanged();
    }

    private static void RaiseChanged()
    {
        try
        {
            PinsChanged?.Invoke();
        }
        catch
        {
            // 订阅者异常不影响贴图
        }
    }

    // ---------------- 查询 ----------------

    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return WindowsList.Count;
            }
        }
    }

    public static IReadOnlyList<PinWindow> Windows
    {
        get
        {
            lock (Gate)
            {
                return WindowsList.ToList();
            }
        }
    }

    public static PinHistory History
    {
        get
        {
            EnsureInitialized();
            return _history ??= new PinHistory();
        }
    }

    public static string? ActiveGroupName
    {
        get
        {
            EnsureInitialized();
            return _activeGroup;
        }
    }

    public static IReadOnlyList<PinGroup> Groups
    {
        get
        {
            EnsureInitialized();
            return _store?.Groups ?? (IReadOnlyList<PinGroup>)new List<PinGroup>();
        }
    }

    /// <summary>新建贴图默认是否带阴影（持久化到 pin.json）。</summary>
    public static bool ShowShadow
    {
        get
        {
            EnsureInitialized();
            return _store?.ShowShadow ?? true;
        }
        set
        {
            EnsureInitialized();
            if (_store is not null)
            {
                _store.ShowShadow = value;
                _store.Save();
            }
        }
    }

    // ---------------- 创建贴图 ----------------

    /// <summary>贴一张图像。screenPosition 为窗口左上角的屏幕 DIP 坐标；不传则居中。</summary>
    public static void Show(BitmapSource image, Point? screenPosition = null)
    {
        if (image is null)
        {
            return;
        }

        Show(new PinContent { Kind = PinContentKind.Image, Image = image }, screenPosition);
    }

    /// <summary>按内容创建贴图（文本/颜色/文件贴图也是位图渲染后显示）。</summary>
    public static void Show(PinContent content, Point? screenPosition = null)
    {
        if (content.Image is null)
        {
            return;
        }

        RunOnUi(() =>
        {
            EnsureInitialized();
            var state = screenPosition is { } position
                ? new PinWindowState { Left = position.X, Top = position.Y }
                : null;

            var window = new PinWindow(content, state);
            window.Show();
            RaiseChanged();
        });
    }

    /// <summary>文本贴图。</summary>
    public static void ShowText(string text)
    {
        var content = new PinContent
        {
            Kind = PinContentKind.Text,
            Text = text,
            Image = PinContentRenderer.RenderText(text),
        };

        Show(content);
    }

    /// <summary>颜色贴图（显示色块 + HEX，可复制颜色值）。</summary>
    public static void ShowColor(Color color)
    {
        var content = new PinContent
        {
            Kind = PinContentKind.Color,
            ColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}",
            Image = PinContentRenderer.RenderColor(color),
        };

        Show(content);
    }

    /// <summary>文件贴图（显示文件名列表，可打开/复制路径/Ctrl+拖拽到其他应用）。</summary>
    public static void ShowFiles(IReadOnlyList<string> paths)
    {
        var list = paths?.Where(path => !string.IsNullOrWhiteSpace(path)).ToList() ?? new List<string>();
        var content = new PinContent
        {
            Kind = PinContentKind.Files,
            Files = list,
            Image = PinContentRenderer.RenderFiles(list),
        };

        Show(content);
    }

    // ---------------- 关闭 / 销毁 / 历史 ----------------

    /// <summary>正常关闭（进历史，Ctrl+3 可恢复）。</summary>
    public static void Close(PinWindow window) => window.ClosePin();

    /// <summary>销毁（不进历史、不可恢复）。</summary>
    public static void Destroy(PinWindow window) => window.DestroyPin();

    /// <summary>关闭全部贴图；destroy=true 为销毁（不进历史，会二次确认）。</summary>
    public static void CloseAll(bool destroy)
    {
        RunOnUi(() =>
        {
            EnsureInitialized();
            var windows = Windows;
            if (windows.Count == 0)
            {
                return;
            }

            if (destroy && !_suppressConfirmDialogs)
            {
                var result = MessageBox.Show(
                    $"确定销毁全部 {windows.Count} 张贴图？\n销毁后不进入历史，无法恢复。",
                    "PinShot", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (result != MessageBoxResult.OK)
                {
                    return;
                }
            }

            foreach (var window in windows)
            {
                if (destroy)
                {
                    window.DestroyPin();
                }
                else
                {
                    window.ClosePin();
                }
            }
        });
    }

    /// <summary>恢复最近关闭的一条贴图（全局 Ctrl+3）。</summary>
    public static void RestoreLast()
    {
        RunOnUi(() =>
        {
            EnsureInitialized();
            var entry = _history?.PopLast();
            if (entry is null)
            {
                return;
            }

            var content = entry.ToContent();
            if (content.Image is null)
            {
                return;
            }

            var state = new PinWindowState
            {
                Left = entry.Left,
                Top = entry.Top,
                Width = entry.Width,
                Height = entry.Height,
                Title = entry.Title,
            };

            var window = new PinWindow(content, state);
            window.Show();
            RaiseChanged();
        });
    }

    /// <summary>关闭流程回调：由 PinWindow.OnClosing 调用，负责写入历史（销毁不写）。</summary>
    internal static void OnWindowClosing(PinWindow window)
    {
        try
        {
            EnsureInitialized();
            if (window.IsDestroying)
            {
                return;
            }

            var entry = window.CreateHistoryEntry();
            _history?.Add(entry);
            RaiseChanged();
        }
        catch
        {
            // 历史写入失败不影响关闭
        }
    }

    // ---------------- 批量 / 窗口救援 ----------------

    /// <summary>保存历史索引 + 分组/阴影配置。</summary>
    public static void SaveAll()
    {
        try
        {
            _history?.Save();
            _store?.Save();
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>把所有贴图拉回屏幕可见范围（分辨率变化/拔显示器后救援）。</summary>
    public static void BringAllOnScreen()
    {
        RunOnUi(() =>
        {
            EnsureInitialized();
            foreach (var window in Windows)
            {
                window.BringOnScreen();
            }
        });
    }

    /// <summary>隐藏除 keep 之外的所有贴图。</summary>
    public static void HideOthers(PinWindow keep)
    {
        foreach (var window in Windows)
        {
            if (!ReferenceEquals(window, keep))
            {
                window.HidePin();
            }
        }

        RaiseChanged();
    }

    /// <summary>显示所有贴图。</summary>
    public static void ShowAllPins()
    {
        foreach (var window in Windows)
        {
            window.ShowPin();
        }

        RaiseChanged();
    }

    // ---------------- 分组 ----------------

    public static PinGroup CreateGroup(string name)
    {
        EnsureInitialized();
        var group = _store?.EnsureGroup(name) ?? PinGroup.Create(name);
        _store?.Save();
        RaiseChanged();
        return group;
    }

    public static void MoveToGroup(PinWindow window, string groupName)
    {
        EnsureInitialized();
        var group = _store?.EnsureGroup(groupName) ?? PinGroup.Create(groupName);
        window.ApplyGroup(group.Name);
        _store?.Save();
        RaiseChanged();
    }

    /// <summary>切换分组视角：null = 全部，其余只显示该组贴图（其余隐藏）。</summary>
    public static void SetGroupView(string? groupName)
    {
        EnsureInitialized();
        _activeGroup = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim();
        if (_store is not null)
        {
            _store.ActiveGroup = _activeGroup;
            _store.Save();
        }

        foreach (var window in Windows)
        {
            window.SetGroupViewVisible(_activeGroup is null
                || string.Equals(window.GroupName, _activeGroup, StringComparison.OrdinalIgnoreCase));
        }

        RaiseChanged();
    }

    /// <summary>Ctrl+~：在「全部 → 各分组」之间循环切换分组视角。</summary>
    public static void CycleGroupView()
    {
        EnsureInitialized();
        var names = new List<string?> { null };
        names.AddRange(Groups.Select(group => group.Name));

        var current = names.FindIndex(name =>
            string.Equals(name, _activeGroup, StringComparison.OrdinalIgnoreCase));
        var next = names[(current + 1) % names.Count];
        SetGroupView(next);
    }

    // ---------------- 内部 ----------------

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    // ---------------- 自检 ----------------

    /// <summary>
    /// 生命周期自检：创建 → 关闭进历史 → Ctrl+3 恢复 → 销毁不进历史 → 历史容量/持久化 → 图像处理/分组。
    /// 返回逐项 PASS/FAIL 文本；reportPath 非空时同时写文件。可在无 Application 的 STA 宿主中直接调用。
    /// 注意：会重定向并清理临时数据目录，务必在没有任何贴图窗口时调用（启动期或独立测试宿主）。
    /// </summary>
    public static string SelfTest(string? reportPath = null)
    {
        var lines = new List<string>();
        var pass = 0;
        var fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok)
            {
                pass++;
            }
            else
            {
                fail++;
            }

            lines.Add($"{(ok ? "PASS" : "FAIL")} {name}{(string.IsNullOrEmpty(detail) ? string.Empty : " :: " + detail)}");
        }

        var previousDirectory = PinPaths.OverrideDirectory;
        var tempDirectory = Path.Combine(Path.GetTempPath(), "PinShotSelfTest_" + Guid.NewGuid().ToString("N"));

        PinPaths.Override(tempDirectory);
        _suppressHotkey = true;
        _suppressConfirmDialogs = true;
        ResetAllState();

        try
        {
            Check("初始窗口数 = 0", Count == 0, $"Count={Count}");
            Check("初始历史 = 0", History.Count == 0, $"History={History.Count}");

            Show(CreateTestImage(120, 80));
            Show(CreateTestImage(90, 90));
            ShowText("自检文本贴图");
            ShowColor(Colors.Orange);
            ShowFiles(new[]
            {
                Path.Combine(tempDirectory, "a.txt"),
                Path.Combine(tempDirectory, "b.png"),
            });
            PumpDispatcher();

            Check("创建 5 张贴图", Count == 5, $"Count={Count}");
            Check("创建后历史为空", History.Count == 0, $"History={History.Count}");

            var first = Windows[0];
            first.ClosePin();
            PumpDispatcher();
            Check("关闭 1 张后窗口数 = 4", Count == 4, $"Count={Count}");
            Check("关闭后历史 = 1", History.Count == 1, $"History={History.Count}");
            Check("历史索引已落盘", File.Exists(PinHistory.IndexPath), PinHistory.IndexPath);

            RestoreLast();
            PumpDispatcher();
            Check("恢复后窗口数 = 5", Count == 5, $"Count={Count}");
            Check("恢复后历史清空", History.Count == 0, $"History={History.Count}");

            var second = Windows[1];
            Destroy(second);
            PumpDispatcher();
            Check("销毁后窗口数 = 4", Count == 4, $"Count={Count}");
            Check("销毁不进历史", History.Count == 0, $"History={History.Count}");

            // 历史持久化：关闭一张，重新从磁盘加载应能拿回图像
            Windows[0].ClosePin();
            PumpDispatcher();
            var reloaded = PinHistory.Load();
            Check("历史可从磁盘重新加载", reloaded.Count == 1, $"History={reloaded.Count}");
            Check("历史图像可读回", reloaded.Entries.Count == 1 && reloaded.Entries[0].Image is not null);
            var restoredEntry = reloaded.PopLast();
            Check("历史条目可取出", restoredEntry?.Image is not null);

            // 清空管理器内的历史，供后续「销毁全部不进历史」断言使用
            History.Clear();
            Check("清空历史后为 0", History.Count == 0, $"History={History.Count}");

            // 图像处理 / 锁定 / 置顶 / 标题
            var imagePin = Windows.FirstOrDefault(window => window.Kind == PinContentKind.Image);
            if (imagePin is null)
            {
                Show(CreateTestImage(64, 48));
                PumpDispatcher();
                imagePin = Windows[^1];
            }

            var processing = imagePin!.RunSelfTestProcessing();
            Check("图像处理（旋转/灰度/反色/亮度/透明/重置/快照）", processing.Ok, processing.Detail);

            imagePin.SetLocked(true);
            Check("锁定状态生效", imagePin.IsLocked);
            imagePin.SetLocked(false);

            var topmostBefore = imagePin.Topmost;
            imagePin.ToggleTopmost();
            Check("置顶切换生效", imagePin.Topmost != topmostBefore);
            imagePin.ToggleTopmost();

            imagePin.SetPinTitle("自检标题");
            Check("标题设置生效", imagePin.PinTitle == "自检标题", imagePin.PinTitle);

            // 分组 + 分组视角
            var group = CreateGroup("自检组");
            MoveToGroup(Windows[0], group.Name);
            Check("移动分组生效",
                string.Equals(Windows[0].GroupName, group.Name, StringComparison.Ordinal),
                Windows[0].GroupName);

            SetGroupView(group.Name);
            PumpDispatcher();
            Check("分组视角只显示该组",
                Windows.All(window => (string.Equals(window.GroupName, group.Name, StringComparison.Ordinal))
                    == (window.Visibility == Visibility.Visible)));

            SetGroupView(null);
            Check("显示全部后都可见", Windows.All(window => window.Visibility == Visibility.Visible));

            // 关闭所有（销毁）
            CloseAll(true);
            PumpDispatcher();
            Check("销毁全部后窗口数 = 0", Count == 0, $"Count={Count}");
            Check("销毁全部不进历史", History.Count == 0, $"History={History.Count}");

            // 历史容量 10
            for (var i = 0; i < 12; i++)
            {
                Show(CreateTestImage(40, 30));
                PumpDispatcher(1);
                Windows[^1].ClosePin();
                PumpDispatcher(1);
            }

            Check("历史容量上限 10", History.Count == 10, $"History={History.Count}");

            CloseAll(true);
            PumpDispatcher();
            Check("收尾窗口数 = 0", Count == 0, $"Count={Count}");
        }
        catch (Exception ex)
        {
            fail++;
            lines.Add("FAIL 自检异常 :: " + ex);
        }
        finally
        {
            try
            {
                _suppressConfirmDialogs = true;
                CloseAll(true);
                PumpDispatcher();
            }
            catch
            {
                // 收尾失败不影响报告
            }

            ResetAllState();
            PinPaths.Override(previousDirectory);
            _suppressHotkey = false;
            _suppressConfirmDialogs = false;

            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
            catch
            {
                // 临时目录残留不影响结果
            }
        }

        var report = string.Join(Environment.NewLine, lines)
            + Environment.NewLine
            + $"RESULT {(fail == 0 ? "PASS" : "FAIL")} pass={pass} fail={fail}";

        try
        {
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                File.WriteAllText(reportPath!, report);
            }
        }
        catch
        {
            // 报告文件写失败不影响返回值
        }

        return report;
    }

    private static void ResetAllState()
    {
        try
        {
            _hotkey?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _hotkey = null;
        _history = null;
        _store = null;
        _activeGroup = null;
        _initialized = false;

        lock (Gate)
        {
            WindowsList.Clear();
        }
    }

    private static void PumpDispatcher(int rounds = 2)
    {
        try
        {
            for (var i = 0; i < rounds; i++)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
        }
        catch
        {
            // 无消息循环时跳过（窗口 Show/Close 本身已同步完成）
        }
    }

    private static BitmapSource CreateTestImage(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x40;
            pixels[i + 1] = 0x80;
            pixels[i + 2] = 0xC0;
            pixels[i + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    // ---------------- 全局热键 Ctrl+3 ----------------

    private sealed class PinHotkey : IDisposable
    {
        private const int RestoreHotkeyId = 0x5020;
        private const int GroupHotkeyId = 0x5021;

        private readonly HwndSource _source;
        private bool _disposed;
        private bool _groupRegistered;

        private PinHotkey(HwndSource source)
        {
            _source = source;
            _source.AddHook(WndProc);
        }

        public event Action? Pressed;

        /// <summary>Ctrl+~：切换分组视角（即使所有贴图被分组视角隐藏也能恢复）。</summary>
        public event Action? GroupPressed;

        public static PinHotkey? TryCreate()
        {
            try
            {
                var parameters = new HwndSourceParameters("PinShotPinHotkey")
                {
                    Width = 0,
                    Height = 0,
                    WindowStyle = 0,
                    ExtendedWindowStyle = PinNative.WS_EX_TOOLWINDOW,
                };

                var source = new HwndSource(parameters);
                var hotkey = new PinHotkey(source);

                if (!PinNative.RegisterHotKey(source.Handle, RestoreHotkeyId,
                        PinNative.MOD_CONTROL | PinNative.MOD_NOREPEAT, 0x33))
                {
                    hotkey.Dispose();
                    return null;
                }

                hotkey._groupRegistered = PinNative.RegisterHotKey(source.Handle, GroupHotkeyId,
                    PinNative.MOD_CONTROL | PinNative.MOD_NOREPEAT, 0xC0);

                return hotkey;
            }
            catch
            {
                return null;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != PinNative.WM_HOTKEY)
            {
                return IntPtr.Zero;
            }

            switch (wParam.ToInt32())
            {
                case RestoreHotkeyId:
                    handled = true;
                    Pressed?.Invoke();
                    break;
                case GroupHotkeyId:
                    handled = true;
                    GroupPressed?.Invoke();
                    break;
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

            try
            {
                PinNative.UnregisterHotKey(_source.Handle, RestoreHotkeyId);
                if (_groupRegistered)
                {
                    PinNative.UnregisterHotKey(_source.Handle, GroupHotkeyId);
                }

                _source.RemoveHook(WndProc);
                _source.Dispose();
            }
            catch
            {
                // 忽略
            }
        }
    }
}
