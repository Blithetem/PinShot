using System.IO;
using System.Text;
using System.Globalization;
using System.Windows;
using Windows.Media.Ocr;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PinShot.App.Capture;
using PinShot.App.Cli;
using PinShot.App.Editor;
using PinShot.App.Ocr;
using PinShot.App.Gif;
using PinShot.App.Interop;
using PinShot.App.LongShot;
using PinShot.App.Pin;
using PinShot.App.Recorder;
using PinShot.App.Services;

namespace PinShot.App;

public partial class App : Application
{
    private const string InstanceMutexName = "PinShot.SingleInstance";
    private const string ActivateEventName = "PinShot.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private Thread? _activateThread;
    private ToastWindow? _toast;
    private HotkeyWindow? _hotkey;
    private TrayService? _tray;
    private bool _capturing;
    private ScreenRecorder? _recorder;
    private RecordingBar? _recordBar;
    private RecordingFrame? _recordFrame;
    private System.Diagnostics.Stopwatch? _recordStopwatch;
    private GifRecorder? _gifRecorder;

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterCrashHandlers();

        // 载入用户配置（热键 / 保存目录 / 默认格式等），缺失时用默认值
        var settings = AppSettings.Load();

        // 命令行模式：不创建托盘与热键，执行完命令即退出
        var cli = CliOptions.Parse(e.Args);
        if (cli.Command != CliCommand.None)
        {
            base.OnStartup(e);
            CliRunner.AttachToParentConsole();

            if (CliRunner.Run(cli, this))
            {
                CliRunner.DetachConsole();
                Shutdown();
            }

            return;
        }

        _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // 已有实例：通知它弹出提示，然后直接退出（不弹 MessageBox 打断用户）
            NotifyRunningInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 自检入口：直接构造并显示一个贴图窗口，用于验证贴图链路（不经过截图交互）
        if (e.Args.Contains("--pin-test", StringComparer.OrdinalIgnoreCase))
        {
            RunPinSelfTest();
            return;
        }

        // 自检入口：直接进入截图流程，用于自动化验证遮罩与编辑界面
        if (e.Args.Contains("--capture-test", StringComparer.OrdinalIgnoreCase))
        {
            RunCaptureSelfTest();
            return;
        }

        // 自检入口：直接创建长截图窗口，验证 UI 流程不崩
        if (e.Args.Contains("--longshot-ui-test", StringComparer.OrdinalIgnoreCase))
        {
            RunLongShotUiSelfTest();
            return;
        }

        // 自检入口：独立图片编辑窗口（长图结果），检查工具栏是否被裁
        if (e.Args.Contains("--editor-test", StringComparer.OrdinalIgnoreCase))
        {
            RunEditorSelfTest();
            return;
        }

        // 自检入口：截图装饰（圆角 / 阴影 / 光标）输出检查
        if (e.Args.Contains("--decorate-test", StringComparer.OrdinalIgnoreCase))
        {
            RunDecorateSelfTest();
            return;
        }

        // 自检入口：设置窗口（热键解析 + UI 渲染）
        if (e.Args.Contains("--settings-test", StringComparer.OrdinalIgnoreCase))
        {
            RunSettingsSelfTest();
            return;
        }

        // 自检入口：贴图管理（生命周期 / 历史 / 分组 / 图像处理）
        if (e.Args.Contains("--pin-manager-test", StringComparer.OrdinalIgnoreCase))
        {
            RunPinManagerSelfTest();
            return;
        }

        // 自检入口：对指定图片做 OCR，对比不同放大倍数的识别效果
        if (e.Args.Contains("--ocr-file-test", StringComparer.OrdinalIgnoreCase))
        {
            RunOcrFileSelfTest(e.Args);
            return;
        }

        // 自检入口：直接走 OCR 结果窗口（不经过截图工具栏）
        if (e.Args.Contains("--ocr-ui-test", StringComparer.OrdinalIgnoreCase))
        {
            RunOcrUiSelfTest();
            return;
        }

        // 自检入口：验证 OCR 引擎
        if (e.Args.Contains("--ocr-test", StringComparer.OrdinalIgnoreCase))
        {
            RunOcrSelfTest();
            return;
        }

        // 自检入口：模拟完整录制流程（含录制框/控制条）并导出 GIF
        if (e.Args.Contains("--stop-test", StringComparer.OrdinalIgnoreCase))
        {
            RunStopSelfTest();
            return;
        }

        // 自检入口：录制 2 秒并直接导出 GIF，用于检查 GIF 清晰度
        if (e.Args.Contains("--gif-quality-test", StringComparer.OrdinalIgnoreCase))
        {
            RunGifQualitySelfTest();
            return;
        }

        // 自检入口：录制 2 秒后弹结果工具条（不自动退出）
        if (e.Args.Contains("--result-test", StringComparer.OrdinalIgnoreCase))
        {
            RunRecordingResultSelfTest();
            return;
        }

        // 自检入口：录制 3 秒
        if (e.Args.Contains("--record-test", StringComparer.OrdinalIgnoreCase))
        {
            RunRecordSelfTest();
            return;
        }

        // 自检入口：长截图拼接算法（合成滚动帧）
        if (e.Args.Contains("--longshot-test", StringComparer.OrdinalIgnoreCase))
        {
            RunLongShotSelfTest();
            return;
        }

        // 自检入口：GIF 录制 2 秒
        if (e.Args.Contains("--gif-test", StringComparer.OrdinalIgnoreCase))
        {
            RunGifSelfTest();
            return;
        }

        var hotkeyDisplay = "托盘菜单";
        var recordHotkeyDisplay = "托盘菜单";

        try
        {
            _hotkey = new HotkeyWindow(settings.CaptureHotkey, settings.PinHotkey,
                settings.RestorePinHotkey, settings.RecordHotkey);
            hotkeyDisplay = _hotkey.HotkeyDisplay;
            recordHotkeyDisplay = _hotkey.RecordHotkeyDisplay;
            _hotkey.HotkeyPressed += BeginCapture;
            _hotkey.RecordHotkeyPressed += ToggleRecord;
            _hotkey.PinHotkeyPressed += PinFromClipboard;
            _hotkey.RestorePinHotkeyPressed += RestoreLastPin;

            // 贴图管理：预加载历史、分组，并注册 Ctrl+~ 等热键
            // 必须在 HotkeyWindow 之后，Ctrl+3 归 HotkeyWindow，PinManager 只做兜底
            PinManager.Initialize();
        }
        catch (InvalidOperationException)
        {
            // 热键被别人占用时不阻塞启动，仍可通过托盘菜单截图
            _hotkey = null;
        }

        _tray = new TrayService(hotkeyDisplay, recordHotkeyDisplay,
            new TrayActions(BeginCapture, BeginLongShot, BeginGif, BeginRecord, ExitApplication,
                CloseAllPins, DestroyAllPins, BringPinsOnScreen, OpenSettings));

        StartActivateListener();
        ShowToast("PinShot 已启动",
            $"截图 {hotkeyDisplay} · 录屏 {recordHotkeyDisplay} · 托盘菜单有长截图 / GIF");

        WriteInfo($"启动完成：截图热键={hotkeyDisplay}，录屏热键={recordHotkeyDisplay}");
    }

    /// <summary>监听「再次启动」事件：用户重复双击 exe 时给出现有实例的提示。</summary>
    private void StartActivateListener()
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateThread = new Thread(() =>
            {
                try
                {
                    while (_activateEvent.WaitOne())
                    {
                        Dispatcher.Invoke(() =>
                            ShowToast("PinShot 已在运行",
                                "截图用 Ctrl+Shift+S；托盘图标若没显示，请检查任务栏溢出区"));
                    }
                }
                catch (ObjectDisposedException)
                {
                    // 退出时事件已释放
                }
            })
            {
                IsBackground = true,
                Name = "PinShotActivateListener",
            };

            _activateThread.Start();
        }
        catch (Exception ex)
        {
            WriteLog("ActivateListener", ex);
        }
    }

    private static void NotifyRunningInstance()
    {
        try
        {
            using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
            activate.Set();
        }
        catch
        {
            MessageBox.Show(
                "PinShot 已在运行。\n\n截图：Ctrl+Shift+S\n如果看不到托盘图标，请检查任务栏右下角的隐藏图标区。",
                "PinShot", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ShowToast(string title, string body)
    {
        try
        {
            _toast?.Close();
            _toast = new ToastWindow(title, body);
            _toast.Show();
        }
        catch (Exception ex)
        {
            WriteLog("Toast", ex);
        }
    }

    /// <summary>录屏热键：未在录制则开始选区域，正在录制则停止并保存。</summary>
    private void ToggleRecord()
    {
        if (_recorder is not null)
        {
            StopRecording();
        }
        else
        {
            BeginRecord();
        }
    }

    // ---------------- 截图 ----------------

    private void RunLongShotSelfTest()
    {
        try
        {
            const int width = 400;
            const int fullHeight = 3000;
            const int viewHeight = 800;
            const int scrollStep = 40;

            var full = CreateGradientFrame(width, fullHeight);
            var session = new LongShotSession(width, viewHeight);

            for (var y = 0; y + viewHeight <= fullHeight; y += scrollStep)
            {
                var frame = new byte[width * viewHeight * 4];
                Buffer.BlockCopy(full, y * width * 4, frame, 0, frame.Length);
                session.AddFrame(frame);
            }

            var result = session.BuildBitmap();
            var heightOk = Math.Abs(result.PixelHeight - fullHeight) <= scrollStep * 2;

            WriteInfo($"长截图自检：高度={result.PixelHeight}（期望 {fullHeight}），宽度={result.PixelWidth}，结果={(heightOk ? "通过" : "偏差较大")}");

            var path = Path.Combine(Path.GetTempPath(), "PinShot_longshot_test.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            WriteInfo($"长截图测试输出: {path}");
        }
        catch (Exception ex)
        {
            WriteLog("LongShotSelfTest", ex);
        }

        Shutdown();
    }

    private static byte[] CreateGradientFrame(int width, int height)
    {
        var data = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width + x) * 4;
                data[index] = (byte)(y * 255 / Math.Max(1, height - 1));
                data[index + 1] = (byte)(x * 255 / Math.Max(1, width - 1));
                data[index + 2] = (byte)((x + y) % 256);
                data[index + 3] = 255;
            }
        }

        return data;
    }

    private void RunGifSelfTest()
    {
        try
        {
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            var area = new System.Drawing.Rectangle(bounds.Left, bounds.Top,
                Math.Min(640, bounds.Width), Math.Min(400, bounds.Height));
            var path = Path.Combine(Path.GetTempPath(), "PinShot_gif_test.gif");

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            _gifRecorder = new GifRecorder(area, fps: 10, maxFrames: 60);
            _gifRecorder.Start();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();

                var recorder = _gifRecorder;
                _gifRecorder = null;
                recorder?.Stop();

                var failure = recorder?.Failure;
                if (failure is null && recorder is not null)
                {
                    try
                    {
                        var frames = recorder.Save(path);
                        WriteInfo($"GIF 自检：{frames} 帧，{new FileInfo(path).Length} bytes，路径 {path}");
                    }
                    catch (Exception ex)
                    {
                        WriteLog("GifSelfTest.Save", ex);
                    }
                }
                else
                {
                    WriteLog("GifSelfTest", failure ??
                        new InvalidOperationException("GIF 录制无输出"));
                }

                recorder?.Dispose();
                Shutdown();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            WriteLog("GifSelfTest", ex);
            Shutdown();
        }
    }

    private void BeginCapture()
    {
        if (_capturing)
        {
            return;
        }

        _capturing = true;

        try
        {
            var previous = Native.GetForegroundWindow();

            // 截图前把贴图窗口藏起来，否则贴图会被截进图里（PixPin 同样处理）
            HidePinsForCapture();

            var (snapshot, bounds) = ScreenCapture.CaptureVirtualScreen();
            var overlay = new CaptureOverlay(snapshot, bounds);

            overlay.Completed += result =>
            {
                _capturing = false;

                if (result is not null)
                {
                    HandleCapture(result);
                }
            };

            overlay.Closed += (_, _) =>
            {
                _capturing = false;
                RestorePinsAfterCapture();
            };

            // 截图编辑工具栏里的「录制」：按当前选区直接开录
            overlay.RecordRequested += region =>
            {
                _capturing = false;
                StartRecording(region);
            };

            // 截图编辑工具栏里的「滚动」：把当前选区转为长截图
            overlay.LongShotRequested += region =>
            {
                _capturing = false;

                var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
                StartLongShot(region,
                    new Int32Rect(screen.X, screen.Y, screen.Width, screen.Height),
                    previous);
            };

            // 截图编辑工具栏里的「识别」：OCR 当前选区
            overlay.OcrRequested += async image =>
            {
                _capturing = false;

                try
                {
                    var text = await Task.Run(() => OcrService.RecognizeAsync(image));
                    new OcrResultWindow(text).Show();
                }
                catch (Exception ex)
                {
                    WriteLog("Ocr", ex);
                    MessageBox.Show($"识别失败：{ex.Message}", "PinShot",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            overlay.Show();
        }
        catch (Exception ex)
        {
            _capturing = false;
            RestorePinsAfterCapture();
            MessageBox.Show($"截图失败：{ex.Message}", "PinShot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>记录被临时隐藏的贴图窗口，截图结束后原样恢复。</summary>
    private readonly List<PinWindow> _hiddenPins = new();

    private void HidePinsForCapture()
    {
        _hiddenPins.Clear();

        foreach (Window window in Windows)
        {
            if (window is PinWindow pin && pin.IsVisible)
            {
                _hiddenPins.Add(pin);
                pin.Hide();
            }
        }

        if (_hiddenPins.Count > 0)
        {
            // 给 DWM 一点时间完成隐藏后的重绘，避免抓到残影
            Thread.Sleep(40);
        }
    }

    private void RestorePinsAfterCapture()
    {
        if (_hiddenPins.Count == 0)
        {
            return;
        }

        foreach (var pin in _hiddenPins)
        {
            try
            {
                pin.Show();
            }
            catch
            {
                // 贴图窗口可能已被销毁，忽略
            }
        }

        _hiddenPins.Clear();
    }

    /// <summary>Ctrl+2：把剪贴板里的图片 / 文本 / 文件贴到屏幕（对齐 PixPin）。</summary>
    private void PinFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                PinManager.Show(image);
                return;
            }

            if (Clipboard.ContainsText() && !string.IsNullOrWhiteSpace(Clipboard.GetText()))
            {
                PinManager.ShowText(Clipboard.GetText());
                return;
            }

            if (Clipboard.ContainsFileDropList())
            {
                var paths = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (paths.Count > 0)
                {
                    PinManager.ShowFiles(paths);
                    return;
                }
            }

            ShowToast("剪贴板里没有可贴的内容", "图片 / 文本 / 文件都可以贴");
        }
        catch (Exception ex)
        {
            WriteLog("PinFromClipboard", ex);
            ShowToast("贴图失败", ex.Message);
        }
    }

    /// <summary>Ctrl+3：恢复最近关闭的贴图。</summary>
    private void RestoreLastPin()
    {
        try
        {
            PinManager.RestoreLast();
        }
        catch (Exception ex)
        {
            WriteLog("RestoreLastPin", ex);
        }
    }

    /// <summary>托盘「设置...」：热键、保存目录/格式、截图默认行为。</summary>
    private void OpenSettings()
    {
        try
        {
            var window = new SettingsWindow(AppSettings.Current);
            if (window.ShowDialog() == true && window.HotkeysChanged)
            {
                ShowToast("热键已保存", "重启 PinShot 后生效");
            }
        }
        catch (Exception ex)
        {
            WriteLog("OpenSettings", ex);
            MessageBox.Show($"打开设置失败：{ex.Message}", "PinShot", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CloseAllPins()
    {
        try
        {
            PinManager.CloseAll(false);
        }
        catch (Exception ex)
        {
            WriteLog("CloseAllPins", ex);
        }
    }

    private void DestroyAllPins()
    {
        try
        {
            PinManager.CloseAll(true);
        }
        catch (Exception ex)
        {
            WriteLog("DestroyAllPins", ex);
        }
    }

    private void BringPinsOnScreen()
    {
        try
        {
            PinManager.BringAllOnScreen();
        }
        catch (Exception ex)
        {
            WriteLog("BringPinsOnScreen", ex);
        }
    }

    private void HandleCapture(CaptureResult result)
    {
        switch (result.Action)
        {
            case CaptureAction.Copy:
                Clipboard.SetImage(result.Image);
                break;

            case CaptureAction.Save:
                SaveImage(result.Image);
                break;

            case CaptureAction.Pin:
                try
                {
                    PinManager.Show(result.Image);
                }
                catch (Exception ex)
                {
                    WriteLog("HandleCapture.Pin", ex);
                    MessageBox.Show($"贴图失败：{ex.Message}", "PinShot", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                break;
        }
    }

    private void SaveImage(BitmapSource image)
    {
        var dialog = new SaveImageDialog(AppSettings.Current);
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            BitmapEncoder encoder = dialog.Format switch
            {
                SaveImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = dialog.Quality },
                SaveImageFormat.Bmp => new BmpBitmapEncoder(),
                _ => new PngBitmapEncoder(),
            };

            encoder.Frames.Add(BitmapFrame.Create(image));

            using (var stream = File.Create(dialog.FilePath))
            {
                encoder.Save(stream);
            }

            ShowToast("截图已保存", dialog.FilePath);
        }
        catch (Exception ex)
        {
            WriteLog("SaveImage", ex);
            MessageBox.Show($"保存失败：{ex.Message}", "PinShot", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ---------------- 长截图 ----------------

    private void BeginLongShot()
    {
        if (_capturing || _recorder is not null)
        {
            return;
        }

        _capturing = true;

        try
        {
            // 记住截图前的前台窗口：长截图期间要把焦点还给它，滚轮才能滚动它
            var previous = Native.GetForegroundWindow();

            var (snapshot, bounds) = ScreenCapture.CaptureVirtualScreen();
            var overlay = new CaptureOverlay(snapshot, bounds, OverlayPurpose.LongShot);

            overlay.RegionSelected += region =>
            {
                _capturing = false;
                StartLongShot(region, bounds, previous);
            };

            overlay.Closed += (_, _) => _capturing = false;
            overlay.Show();
        }
        catch (Exception ex)
        {
            _capturing = false;
            MessageBox.Show($"长截图初始化失败：{ex.Message}", "PinShot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartLongShot(Int32Rect region, Int32Rect virtualBounds, IntPtr previousWindow)
    {
        try
        {
            WriteInfo($"长截图开始: {region.Width}×{region.Height} @ ({region.X},{region.Y})");

            var window = new LongShotWindow(region, virtualBounds, previousWindow);
            window.Completed += result =>
            {
                try
                {
                    WriteInfo(result is null
                        ? "长截图结束: 取消"
                        : $"长截图结束: {result.PixelWidth}×{result.PixelHeight}");

                    if (result is null)
                    {
                        return;
                    }

                    // 和普通截图统一：不直接保存，先进编辑/标注界面（独立窗口，支持长图滚动查看）
                    var editor = new ImageEditorWindow(result);
                    editor.Completed += editorResult =>
                    {
                        if (editorResult is not null)
                        {
                            HandleCapture(editorResult);
                        }
                    };
                    editor.Show();
                }
                catch (Exception ex)
                {
                    WriteLog("LongShot.Completed", ex);
                }
            };
            window.Show();
        }
        catch (Exception ex)
        {
            WriteLog("StartLongShot", ex);
            MessageBox.Show($"长截图失败：{ex.Message}", "PinShot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void SaveLongShot(BitmapSource image)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 图片|*.png",
            FileName = $"长截图_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = System.IO.File.Create(dialog.FileName);
        encoder.Save(stream);
    }

    // ---------------- GIF ----------------

    private void BeginGif()
    {
        if (_capturing || _gifRecorder is not null || _recorder is not null)
        {
            return;
        }

        _capturing = true;

        try
        {
            var (snapshot, bounds) = ScreenCapture.CaptureVirtualScreen();
            var overlay = new CaptureOverlay(snapshot, bounds, OverlayPurpose.Gif);

            overlay.RegionSelected += region =>
            {
                _capturing = false;
                StartGif(region);
            };

            overlay.Closed += (_, _) => _capturing = false;
            overlay.Show();
        }
        catch (Exception ex)
        {
            _capturing = false;
            MessageBox.Show($"GIF 录制初始化失败：{ex.Message}", "PinShot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartGif(Int32Rect region)
    {
        try
        {
            var area = new System.Drawing.Rectangle(region.X, region.Y, region.Width, region.Height);
            _gifRecorder = new GifRecorder(area);
            _gifRecorder.Start();

            _recordBar = new RecordingBar(area);
            _recordBar.StopRequested += StopGif;
            _recordBar.Show();
        }
        catch (Exception ex)
        {
            WriteLog("StartGif", ex);
            MessageBox.Show($"无法开始 GIF 录制：{ex.Message}", "PinShot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopGif()
    {
        _recordBar?.Close();
        _recordBar = null;

        var recorder = _gifRecorder;
        _gifRecorder = null;

        if (recorder is null)
        {
            return;
        }

        recorder.Stop();

        var failure = recorder.Failure;
        var frameCount = recorder.FrameCount;

        if (failure is not null)
        {
            recorder.Dispose();
            MessageBox.Show($"GIF 录制失败：{failure.Message}", "PinShot",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (frameCount == 0)
        {
            recorder.Dispose();
            MessageBox.Show("没有捕获到帧。", "PinShot", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "GIF 图片|*.gif",
            FileName = $"GIF_{DateTime.Now:yyyyMMdd_HHmmss}.gif",
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                recorder.Save(dialog.FileName);
                CopyFileToClipboard(dialog.FileName);
                ShowToast("GIF 已保存", "已保存并复制到剪贴板（Ctrl+V 可粘贴文件）");
            }
            catch (Exception ex)
            {
                WriteLog("Gif.Save", ex);
                MessageBox.Show($"保存 GIF 失败：{ex.Message}", "PinShot",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        recorder.Dispose();
    }

    // ---------------- 录屏 ----------------

    private void BeginRecord()
    {
        if (_capturing || _recorder is not null)
        {
            return;
        }

        _capturing = true;

        try
        {
            var (snapshot, bounds) = ScreenCapture.CaptureVirtualScreen();
            var overlay = new CaptureOverlay(snapshot, bounds, OverlayPurpose.Record);

            overlay.RegionSelected += region =>
            {
                _capturing = false;
                StartRecording(region);
            };

            overlay.Closed += (_, _) => _capturing = false;
            overlay.Show();
        }
        catch (Exception ex)
        {
            _capturing = false;
            MessageBox.Show($"录屏初始化失败：{ex.Message}", "PinShot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartRecording(Int32Rect region)
    {
        try
        {
            var area = new System.Drawing.Rectangle(region.X, region.Y, region.Width, region.Height);
            WriteInfo($"开始录制：{area.Width}×{area.Height} @ ({area.X},{area.Y})");

            var tempPath = Path.Combine(Path.GetTempPath(),
                $"PinShot_rec_{DateTime.Now:yyyyMMdd_HHmmss}.avi");

            _recorder = new ScreenRecorder(area);
            _recorder.Failed += exception => Dispatcher.Invoke(() =>
            {
                MessageBox.Show($"录制中断：{exception.Message}", "PinShot",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StopRecording();
            });
            _recorder.Start(tempPath);
            _recordStopwatch = System.Diagnostics.Stopwatch.StartNew();

            // 录制区域红框：让用户一眼看出录的是局部还是全屏；框本身不进入录像
            var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
            _recordFrame = new RecordingFrame(area,
                new Int32Rect(virtualScreen.X, virtualScreen.Y, virtualScreen.Width, virtualScreen.Height));
            _recordFrame.Show();

            _recordBar = new RecordingBar(area);
            _recordBar.StopRequested += StopRecording;
            _recordBar.Show();
        }
        catch (Exception ex)
        {
            WriteLog("StartRecording", ex);
            MessageBox.Show($"无法开始录制：{ex.Message}", "PinShot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopRecording()
    {
        _recordBar?.Close();
        _recordBar = null;

        _recordFrame?.Close();
        _recordFrame = null;

        var recorder = _recorder;
        _recorder = null;

        if (recorder is null)
        {
            return;
        }

        // 录制框/控制条刚关闭，屏幕还在刷新；等一小会儿再停止抓帧，
        // 避免最后一帧抓到关闭瞬间的异常画面
        Thread.Sleep(150);

        recorder.Stop();

        var tempPath = recorder.OutputPath;
        var failure = recorder.Failure;
        var gifFrames = recorder.GifFrames;
        var width = recorder.Width;
        var height = recorder.Height;
        var duration = _recordStopwatch?.Elapsed ?? TimeSpan.Zero;
        _recordStopwatch = null;
        recorder.Dispose();

        if (failure is not null)
        {
            MessageBox.Show($"录制失败：{failure.Message}", "PinShot",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (tempPath is null || !File.Exists(tempPath))
        {
            MessageBox.Show("录制文件未生成。", "PinShot", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // 弹出结果操作条：保存/复制视频，或转 GIF 保存/复制
        var resultWindow = new RecordingResultWindow(tempPath, gifFrames, duration, width, height);
        resultWindow.Show();
    }

    private static void CopyFileToClipboard(string path)
    {
        try
        {
            var files = new System.Collections.Specialized.StringCollection { path };
            Clipboard.SetFileDropList(files);
        }
        catch (Exception ex)
        {
            WriteLog("Clipboard.File", ex);
        }
    }

    // ---------------- 自检入口 ----------------

    private void RunCaptureSelfTest()
    {
        _tray = new TrayService("测试模式", "测试模式",
            new TrayActions(BeginCapture, BeginLongShot, BeginGif, BeginRecord, ExitApplication,
                CloseAllPins, DestroyAllPins, BringPinsOnScreen));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            BeginCapture();
        };
        timer.Start();
    }

    private void RunPinSelfTest()
    {
        try
        {
            var (image, _) = ScreenCapture.CaptureVirtualScreen();
            var width = Math.Min(240, image.PixelWidth);
            var height = Math.Min(160, image.PixelHeight);
            var cropped = new CroppedBitmap(image, new Int32Rect(0, 0, width, height));
            cropped.Freeze();

            var pin = new PinWindow(cropped);
            pin.Show();
            WriteLog("PinSelfTest", new InvalidOperationException("贴图窗口已显示，自检等待 2 秒"));
        }
        catch (Exception ex)
        {
            WriteLog("PinSelfTest", ex);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Shutdown();
        };
        timer.Start();
    }

    private void RunLongShotUiSelfTest()
    {
        try
        {
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            var region = new Int32Rect(400, 300, 800, 500);

            // 自检：打开真实长截图窗口，验证边框 + 悬浮条
            var window = new LongShotWindow(region,
                new Int32Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                Native.GetForegroundWindow());
            window.Closed += (_, _) => Shutdown();
            window.Show();
            WriteInfo($"长截图窗口自检: 已打开 区域 {region.Width}x{region.Height}");

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                WriteInfo("长截图窗口自检: 6 秒后关闭");
                window.Close();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            WriteLog("LongShotUiSelfTest", ex);
            Shutdown();
        }
    }

    /// <summary>自检产物目录：%TEMP%\pinshot-selftest，避免把本机绝对路径写进源码。</summary>
    private static string SelfTestDirectory
    {
        get
        {
            var directory = Path.Combine(Path.GetTempPath(), "pinshot-selftest");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    /// <summary>自检：设置窗口 + 热键解析。</summary>
    private void RunSettingsSelfTest()
    {
        try
        {
            var probe = new AppSettings
            {
                CaptureHotkey = "Ctrl+Alt+Shift+P",
                DefaultFormat = "jpg",
                JpegQuality = 75,
            };

            var parsed = HotkeyParser.TryParse(probe.CaptureHotkey, out var modifiers, out var key);

            var window = new SettingsWindow(probe);
            window.Closed += (_, _) => Shutdown();
            window.Show();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();

                if (window.Content is FrameworkElement root)
                {
                    var dpi = VisualTreeHelper.GetDpi(window);
                    var width = Math.Max((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), 1);
                    var height = Math.Max((int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), 1);

                    var shot = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    shot.Render(root);

                    var path = Path.Combine(SelfTestDirectory, "settings-window.png");
                    SavePng(shot, path);

                    WriteInfo($"设置自检: 窗口={root.ActualWidth:F0}x{root.ActualHeight:F0} " +
                              $"热键解析={parsed} mods=0x{modifiers:X} vk=0x{key:X} " +
                              $"格式={probe.DefaultFormat} 质量={probe.JpegQuality} 截图={path}");
                }

                window.Close();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            WriteLog("SettingsSelfTest", ex);
            Shutdown();
        }
    }

    /// <summary>自检：贴图管理生命周期。</summary>
    private void RunPinManagerSelfTest()
    {
        try
        {
            var report = PinManager.SelfTest(
                Path.Combine(SelfTestDirectory, "pin-manager-selftest.txt"));
            WriteInfo("贴图管理自检:\n" + report);
            Console.Out.WriteLine(report);
        }
        catch (Exception ex)
        {
            WriteLog("PinManagerSelfTest", ex);
            Console.Error.WriteLine("FAIL: " + ex.Message);
        }

        Shutdown();
    }

    /// <summary>自检：验证圆角 / 阴影 / 光标三种截图装饰的输出。</summary>
    private void RunDecorateSelfTest()
    {
        try
        {
            const int width = 400;
            const int height = 260;

            var source = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var gradient = new LinearGradientBrush(
                    Color.FromRgb(0x2D, 0x8C, 0xFF),
                    Color.FromRgb(0x8B, 0x5C, 0xF6), 45);
                dc.DrawRectangle(gradient, null, new Rect(0, 0, width, height));

                var text = new FormattedText("圆角 + 阴影 自检", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 20,
                    Brushes.White, 1.0);
                dc.DrawText(text, new Point(24, 112));
            }

            source.Render(visual);
            source.Freeze();

            var selection = new Int32Rect(0, 0, width, height);
            var bounds = new Int32Rect(0, 0, width, height);

            var corner = CaptureOverlay.ApplyOptions(source, selection, bounds, 16, false, false);
            var decorated = CaptureOverlay.ApplyOptions(source, selection, bounds, 16, true, false);

            var docs = SelfTestDirectory;
            SavePng(corner, Path.Combine(docs, "ui-decorate-corner.png"));
            SavePng(decorated, Path.Combine(docs, "ui-decorate-shadow.png"));

            WriteInfo($"装饰自检: 原图={source.PixelWidth}x{source.PixelHeight} " +
                      $"圆角={corner.PixelWidth}x{corner.PixelHeight} " +
                      $"圆角+阴影={decorated.PixelWidth}x{decorated.PixelHeight}");
        }
        catch (Exception ex)
        {
            WriteLog("DecorateSelfTest", ex);
        }

        Shutdown();
    }

    private void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>自检：打开独立图片编辑窗口，检查工具栏是否超出窗口被裁。</summary>
    private void RunEditorSelfTest()
    {
        try
        {
            var bitmap = new RenderTargetBitmap(1000, 700, 96, 96, PixelFormats.Pbgra32);
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)), null,
                    new Rect(0, 0, 1000, 700));
                var text = new FormattedText("长图编辑器工具栏自检", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), 24, Brushes.Black, 1.0);
                dc.DrawText(text, new Point(40, 40));
            }

            bitmap.Render(visual);

            var window = new ImageEditorWindow(bitmap);
            window.Closed += (_, _) => Shutdown();
            window.Show();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();

                if (window.Content is FrameworkElement root)
                {
                    var toolbar = FindChild<EditorToolbar>(root);
                    var dpi = VisualTreeHelper.GetDpi(window);
                    var pixelWidth = Math.Max((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), 1);
                    var pixelHeight = Math.Max((int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), 1);

                    var shot = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
                    shot.Render(root);

                    var path = Path.Combine(SelfTestDirectory, "ui-editor-toolbar.png");
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(shot));
                    using (var fs = File.Create(path))
                    {
                        encoder.Save(fs);
                    }

                    WriteInfo($"编辑器自检: 窗口={window.Width:F0}x{window.Height:F0} " +
                              $"根={root.ActualWidth:F0}x{root.ActualHeight:F0} " +
                              $"工具栏={toolbar?.ActualWidth:F0}x{toolbar?.ActualHeight:F0} " +
                              $"未裁剪={(toolbar is not null && toolbar.ActualWidth <= root.ActualWidth)} " +
                              $"截图={path}");
                }

                window.Close();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            WriteLog("EditorSelfTest", ex);
            Shutdown();
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void RunOcrFileSelfTest(string[] args)
    {
        try
        {
            var index = Array.IndexOf(args, "--ocr-file-test");
            var path = index >= 0 && index + 1 < args.Length
                ? args[index + 1]
                : "C:\\Users\\xianyutian\\PinShot\\docs\\ocr-sample.png";

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            foreach (var scale in new[] { 1.0, 2.0, 3.0 })
            {
                var text = Task.Run(() => OcrService.RecognizeAsync(bitmap, scale))
                    .GetAwaiter().GetResult();
                var preview = text.Replace("\r", " ").Replace("\n", " | ");
                if (preview.Length > 260)
                {
                    preview = preview[..260];
                }

                WriteInfo($"OCR 文件测试 scale={scale}: 字数={text.Length} 预览={preview}");
            }
        }
        catch (Exception ex)
        {
            WriteLog("OcrFileTest", ex);
        }

        Shutdown();
    }

    private void RunOcrUiSelfTest()
    {
        try
        {
            var (image, _) = ScreenCapture.CaptureVirtualScreen();
            var width = Math.Min(900, image.PixelWidth);
            var height = Math.Min(500, image.PixelHeight);
            var cropped = new CroppedBitmap(image, new Int32Rect(200, 200, width, height));
            cropped.Freeze();

            Task.Run(() => OcrService.RecognizeAsync(cropped)).ContinueWith(task =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (task.IsFaulted)
                    {
                        WriteLog("OcrUiTest", task.Exception!);
                    }
                    else
                    {
                        new OcrResultWindow(task.Result).Show();
                    }
                });
            });
        }
        catch (Exception ex)
        {
            WriteLog("OcrUiTest", ex);
            Shutdown();
        }
    }

    private void RunOcrSelfTest()
    {
        try
        {
            var (image, _) = ScreenCapture.CaptureVirtualScreen();

            var text = Task.Run(() => OcrService.RecognizeAsync(image))
                .GetAwaiter().GetResult();

            var preview = text.Length > 120 ? text[..120] : text;
            WriteInfo($"OCR 自检: 语言={string.Join(",", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag))} " +
                      $"字数={text.Length} 预览={preview.Replace("\r", " ").Replace("\n", " | ")}");
        }
        catch (Exception ex)
        {
            WriteLog("OcrSelfTest", ex);
        }

        Shutdown();
    }

    private void RunStopSelfTest()
    {
        try
        {
            var area = new System.Drawing.Rectangle(400, 250, 800, 450);
            var gifPath = Path.Combine(Path.GetTempPath(), "PinShot_stop_test.gif");
            var aviPath = Path.Combine(Path.GetTempPath(), "PinShot_stop_test.avi");
            File.Delete(gifPath);
            File.Delete(aviPath);

            _recorder = new ScreenRecorder(area);
            _recorder.Start(aviPath);

            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            _recordFrame = new RecordingFrame(area,
                new Int32Rect(screen.X, screen.Y, screen.Width, screen.Height));
            _recordFrame.Show();
            _recordBar = new RecordingBar(area);
            _recordBar.Show();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();

                _recordFrame?.Close();
                _recordFrame = null;
                _recordBar?.Close();
                _recordBar = null;

                Thread.Sleep(150);

                var recorder = _recorder;
                _recorder = null;
                recorder?.Stop();
                var frames = recorder?.GifFrames ?? Array.Empty<BitmapSource>();
                recorder?.Dispose();

                try
                {
                    var count = GifEncoder.Save(frames, gifPath);
                    WriteInfo($"停止流程自检: {count} 帧, {new FileInfo(gifPath).Length} bytes, {gifPath}");
                }
                catch (Exception ex)
                {
                    WriteLog("StopTest", ex);
                }

                Shutdown();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            WriteLog("StopTest", ex);
            Shutdown();
        }
    }

    private void RunGifQualitySelfTest()
    {
        try
        {
            var area = new System.Drawing.Rectangle(400, 250, 800, 450);
            var avi = Path.Combine(Path.GetTempPath(), "PinShot_gifq.avi");
            var gif = Path.Combine(Path.GetTempPath(), "PinShot_gifq.gif");

            File.Delete(avi);
            File.Delete(gif);

            _recorder = new ScreenRecorder(area);
            _recorder.Start(avi);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();

                var recorder = _recorder;
                _recorder = null;
                recorder?.Stop();
                var frames = recorder?.GifFrames ?? Array.Empty<BitmapSource>();
                recorder?.Dispose();

                try
                {
                    var count = GifEncoder.Save(frames, gif);
                    var width = frames.Count > 0 ? frames[0].PixelWidth : 0;
                    var height = frames.Count > 0 ? frames[0].PixelHeight : 0;
                    WriteInfo($"GIF 质量自检: {count} 帧, {width}x{height}, {new FileInfo(gif).Length} bytes, 路径 {gif}");
                }
                catch (Exception ex)
                {
                    WriteLog("GifQualityTest", ex);
                }

                Shutdown();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            WriteLog("GifQualityTest", ex);
            Shutdown();
        }
    }

    private void RunRecordingResultSelfTest()
    {
        try
        {
            var area = new System.Drawing.Rectangle(200, 150, 640, 360);
            var path = Path.Combine(Path.GetTempPath(), "PinShot_result_test.avi");

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            _recorder = new ScreenRecorder(area);
            _recorder.Start(path);
            _recordStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                StopRecording();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            WriteLog("RecordingResultSelfTest", ex);
            Shutdown();
        }
    }

    private void RunRecordSelfTest()
    {
        try
        {
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            var area = new System.Drawing.Rectangle(bounds.Left, bounds.Top,
                Math.Min(800, bounds.Width), Math.Min(600, bounds.Height));
            var path = Path.Combine(Path.GetTempPath(), "PinShot_record_test.avi");

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            _recorder = new ScreenRecorder(area);
            _recorder.Start(path);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();

                var recorder = _recorder;
                _recorder = null;
                recorder?.Stop();
                var failure = recorder?.Failure;
                recorder?.Dispose();

                if (failure is null && File.Exists(path))
                {
                    WriteLog("RecordSelfTest", new InvalidOperationException(
                        $"录制成功: {new FileInfo(path).Length} bytes, 路径 {path}"));
                }
                else
                {
                    WriteLog("RecordSelfTest", failure ??
                        new InvalidOperationException("录制文件未生成"));
                }

                Shutdown();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            WriteLog("RecordSelfTest", ex);
            Shutdown();
        }
    }

    // ---------------- 崩溃日志 ----------------

    private void RegisterCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteLog("AppDomain", args.ExceptionObject as Exception);

        DispatcherUnhandledException += (_, args) =>
        {
            WriteLog("Dispatcher", args.Exception);
            MessageBox.Show(
                $"发生未处理错误：{args.Exception.Message}\n\n日志：{LogDirectory}",
                "PinShot", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteLog("Task", args.Exception);
            args.SetObserved();
        };
    }

    private static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PinShot", "logs");

    private static void WriteLog(string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {exception}\n\n";
            File.AppendAllText(Path.Combine(LogDirectory, "error.log"), line, Encoding.UTF8);
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }

    private static void WriteInfo(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(Path.Combine(LogDirectory, "info.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n", Encoding.UTF8);
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }

    private void ExitApplication()
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _recorder?.Stop();

        try
        {
            PinManager.Shutdown();
        }
        catch
        {
            // 退出阶段忽略贴图清理异常
        }

        _hotkey?.Dispose();
        _tray?.Dispose();
        _toast?.Close();

        try
        {
            _activateEvent?.Set();
            _activateEvent?.Dispose();
        }
        catch
        {
            // 退出阶段忽略
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
