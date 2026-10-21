using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PinShot.App.Editor;
using PinShot.App.Interop;

namespace PinShot.App.Capture;

/// <summary>
/// 全屏选区窗口。支持：拖拽框选、8 向手柄调整大小、空格拖动整体移动、
/// 方向键微调、放大镜取色，以及选区确认后的标注编辑。
/// </summary>
public partial class CaptureOverlay : Window
{
    private enum OverlayMode
    {
        Selecting,
        Editing,
    }

    private enum Handle
    {
        None,
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left,
        Move,
    }

    private const double HandleTolerance = 8;
    private const int MinSelectionSize = 4;

    private readonly BitmapSource _snapshot;
    private readonly Int32Rect _virtualBounds;
    private readonly OverlayPurpose _purpose;
    private readonly List<Ellipse> _handleShapes = new();

    private OverlayMode _mode = OverlayMode.Selecting;

    private Point _startImagePoint;
    private Point _currentImagePoint;
    private string? _currentHexColor;
    private string? _currentColorText;
    private Color _currentRgbColor;
    private DispatcherTimer? _tipTimer;

    private bool _draggingSelection;
    private Int32Rect? _selection;

    private Handle _activeHandle = Handle.None;
    private Point _dragStartImagePoint;
    private Int32Rect _dragStartSelection;
    private bool _spaceHeld;
    private Int32Rect? _hoverWindow;
    private long _lastHoverProbe;


    private AnnotationLayer? _annotationLayer;
    private EditorToolbar? _toolbar;
    private BitmapSource? _editorBackground;

    /// <summary>当前标注工具；None 表示「选择 / 移动」模式，拖动选区内部可以整体挪动范围。</summary>
    private EditorTool _currentTool = EditorTool.None;

    public CaptureOverlay(BitmapSource snapshot, Int32Rect virtualBounds,
        OverlayPurpose purpose = OverlayPurpose.Capture)
    {
        InitializeComponent();

        _snapshot = snapshot;
        _virtualBounds = virtualBounds;
        _purpose = purpose;

        if (purpose != OverlayPurpose.Capture)
        {
            TipText.Text = purpose switch
            {
                OverlayPurpose.Record => "拖动选择录制区域 · Esc 取消",
                OverlayPurpose.LongShot => "拖动选择长截图区域 · Esc 取消",
                OverlayPurpose.Gif => "拖动选择 GIF 录制区域 · Esc 取消",
                _ => TipText.Text,
            };
        }

        // 必须给窗口一个非 0 的初始几何，否则 WPF 会把 0x0 窗口居中显示，
        // 导致 BgImage 尺寸为 0、鼠标坐标全部错位。
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonUp += (_, _) => Cancel();

        CreateHandles();
        UpdateOptionsBar();
    }

    /// <summary>截图完成时回调；取消时为 null。</summary>
    public event Action<CaptureResult?>? Completed;

    /// <summary>录屏/长图/GIF 模式：选区确认后回调（物理像素矩形，相对虚拟屏幕）。</summary>
    public event Action<Int32Rect>? RegionSelected;

    /// <summary>截图编辑阶段点击工具栏「录制」：按当前选区开始录制。</summary>
    public event Action<Int32Rect>? RecordRequested;

    /// <summary>截图编辑阶段点击工具栏「识别」：把当前选区图像交给 OCR。</summary>
    public event Action<BitmapSource>? OcrRequested;

    /// <summary>截图编辑阶段点击工具栏「滚动」：把当前选区转为滚动长截图。</summary>
    public event Action<Int32Rect>? LongShotRequested;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 用物理像素精确覆盖整个虚拟屏幕，避免多显示器 DPI 差异导致错位。
        var handle = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(handle, Native.HWND_TOPMOST,
            _virtualBounds.X, _virtualBounds.Y, _virtualBounds.Width, _virtualBounds.Height,
            Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);

        Native.SetForegroundWindow(handle);
        Activate();
        Focus();
        Keyboard.Focus(this);

        UpdateMask(null);
        UpdateHandles();

        // 用户按热键后通常不会先移动鼠标，这里主动探测一次光标下的窗口，
        // 否则窗口吸附高亮要等第一次 MouseMove 才会出现。
        Dispatcher.BeginInvoke(new Action(UpdateHoverWindow),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private double ScaleX => ActualWidth <= 0 ? 1 : _snapshot.PixelWidth / ActualWidth;
    private double ScaleY => ActualHeight <= 0 ? 1 : _snapshot.PixelHeight / ActualHeight;

    private Point ToImagePoint(Point positionInImage)
        => new(positionInImage.X * ScaleX, positionInImage.Y * ScaleY);

    private Rect ToDipRect(Int32Rect imageRect)
        => new(imageRect.X / ScaleX, imageRect.Y / ScaleY,
               imageRect.Width / ScaleX, imageRect.Height / ScaleY);

    // ---------------- 选区手柄 ----------------

    private void CreateHandles()
    {
        for (var i = 0; i < 8; i++)
        {
            var shape = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xFF)),
                StrokeThickness = 1.5,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };

            _handleShapes.Add(shape);
            HandleCanvas.Children.Add(shape);
        }
    }

    private Point[] GetHandlePoints(Rect dip)
    {
        return new[]
        {
            new Point(dip.Left, dip.Top),                       // TopLeft
            new Point(dip.Left + dip.Width / 2, dip.Top),       // Top
            new Point(dip.Right, dip.Top),                      // TopRight
            new Point(dip.Right, dip.Top + dip.Height / 2),     // Right
            new Point(dip.Right, dip.Bottom),                   // BottomRight
            new Point(dip.Left + dip.Width / 2, dip.Bottom),    // Bottom
            new Point(dip.Left, dip.Bottom),                    // BottomLeft
            new Point(dip.Left, dip.Top + dip.Height / 2),      // Left
        };
    }

    private void UpdateHandles()
    {
        if (_mode != OverlayMode.Editing || _selection is not { } selection)
        {
            foreach (var shape in _handleShapes)
            {
                shape.Visibility = Visibility.Collapsed;
            }

            return;
        }

        var points = GetHandlePoints(ToDipRect(selection));
        for (var i = 0; i < _handleShapes.Count; i++)
        {
            var shape = _handleShapes[i];
            Canvas.SetLeft(shape, points[i].X - shape.Width / 2);
            Canvas.SetTop(shape, points[i].Y - shape.Height / 2);
            shape.Visibility = Visibility.Visible;
        }
    }

    private Handle HitTestHandle(Point dipPoint)
    {
        if (_mode != OverlayMode.Editing || _selection is not { } selection)
        {
            return Handle.None;
        }

        var points = GetHandlePoints(ToDipRect(selection));

        for (var i = 0; i < points.Length; i++)
        {
            if (Math.Abs(dipPoint.X - points[i].X) <= HandleTolerance &&
                Math.Abs(dipPoint.Y - points[i].Y) <= HandleTolerance)
            {
                return (Handle)(i + 1);
            }
        }

        return Handle.None;
    }

    private static Int32Rect ResizeSelection(Int32Rect start, Handle handle, int imageWidth,
        int imageHeight, double dx, double dy)
    {
        if (handle == Handle.Move)
        {
            var x = Math.Clamp(start.X + dx, 0, Math.Max(0, imageWidth - start.Width));
            var y = Math.Clamp(start.Y + dy, 0, Math.Max(0, imageHeight - start.Height));
            return new Int32Rect((int)Math.Round(x), (int)Math.Round(y),
                start.Width, start.Height);
        }

        double left = start.X;
        double top = start.Y;
        double right = start.X + start.Width;
        double bottom = start.Y + start.Height;

        switch (handle)
        {
            case Handle.TopLeft: left += dx; top += dy; break;
            case Handle.Top: top += dy; break;
            case Handle.TopRight: right += dx; top += dy; break;
            case Handle.Right: right += dx; break;
            case Handle.BottomRight: right += dx; bottom += dy; break;
            case Handle.Bottom: bottom += dy; break;
            case Handle.BottomLeft: left += dx; bottom += dy; break;
            case Handle.Left: left += dx; break;
        }

        left = Math.Clamp(left, 0, imageWidth - MinSelectionSize);
        top = Math.Clamp(top, 0, imageHeight - MinSelectionSize);
        right = Math.Clamp(right, left + MinSelectionSize, imageWidth);
        bottom = Math.Clamp(bottom, top + MinSelectionSize, imageHeight);

        return new Int32Rect((int)Math.Round(left), (int)Math.Round(top),
            (int)Math.Round(right - left), (int)Math.Round(bottom - top));
    }

    // ---------------- 鼠标 ----------------

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mode != OverlayMode.Editing || _selection is not { } selection)
        {
            return;
        }

        var position = e.GetPosition(this);
        var handle = HitTestHandle(position);
        var insideSelection = ToDipRect(selection).Contains(position);

        Log($"down click={e.ClickCount} tool={_currentTool} inside={insideSelection} handle={handle}");

        // 选择模式下双击选区内部 = 直接复制（和工具栏「复制」按钮同一个动作）。
        // 必须在 MouseDown 判定：MouseUp 上的 ClickCount 不可靠。
        if (e.ClickCount >= 2 && _currentTool == EditorTool.None && insideSelection)
        {
            _activeHandle = Handle.None;
            ReleaseMouseCapture();
            e.Handled = true;
            Log("double-click -> copy");
            Complete(CaptureAction.Copy);
            return;
        }

        if (handle != Handle.None)
        {
            BeginAdjust(handle, position);
            e.Handled = true;
            return;
        }

        // 「选择 / 移动」工具下，拖选区内部 = 整体挪动截图范围（对齐 PixPin）
        if (_currentTool == EditorTool.None && insideSelection)
        {
            BeginAdjust(Handle.Move, position);
            e.Handled = true;
            return;
        }

        if (_spaceHeld && insideSelection)
        {
            BeginAdjust(Handle.Move, position);
            e.Handled = true;
        }
    }

    /// <summary>临时诊断日志：排查选区交互问题用。</summary>
    private static void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pinshot-capture.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch
        {
            // 日志失败不影响交互
        }
    }

    private void BeginAdjust(Handle handle, Point position)
    {
        if (_selection is not { } selection)
        {
            return;
        }

        _activeHandle = handle;
        _dragStartSelection = selection;
        _dragStartImagePoint = ToImagePoint(position);
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(this);

        if (_mode == OverlayMode.Editing && _activeHandle != Handle.None)
        {
            var current = ToImagePoint(position);
            var dx = current.X - _dragStartImagePoint.X;
            var dy = current.Y - _dragStartImagePoint.Y;

            var resized = ResizeSelection(_dragStartSelection, _activeHandle,
                _snapshot.PixelWidth, _snapshot.PixelHeight, dx, dy);

            ApplySelection(resized);
            return;
        }

        // 编辑态：选择模式下悬停在选区内部时给「可移动」光标
        if (_mode == OverlayMode.Editing)
        {
            Cursor = _currentTool == EditorTool.None &&
                     _selection is { } currentSelection &&
                     ToDipRect(currentSelection).Contains(position) &&
                     HitTestHandle(position) == Handle.None
                ? Cursors.SizeAll
                : Cursors.Cross;
            return;
        }

        if (_mode != OverlayMode.Selecting)
        {
            Magnifier.Visibility = Visibility.Collapsed;
            return;
        }

        if (_draggingSelection)
        {
            _currentImagePoint = ToImagePoint(e.GetPosition(this));
            RecomputeSelection();
        }

        UpdateHoverWindow();
        UpdateMagnifier(position);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == OverlayMode.Editing && _activeHandle != Handle.None)
        {
            _activeHandle = Handle.None;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_mode != OverlayMode.Selecting || !_draggingSelection)
        {
            return;
        }

        _draggingSelection = false;
        ReleaseMouseCapture();
        _currentImagePoint = ToImagePoint(e.GetPosition(this));
        RecomputeSelection();
        e.Handled = true;

        // 单击（几乎没拖动）且鼠标下有窗口：直接吸附整个窗口
        var dragDistance = (_currentImagePoint - _startImagePoint).Length;
        if (dragDistance < 4 && _hoverWindow is { } hover)
        {
            _selection = hover;
            SelectionChanged();
        }

        if (_selection is { Width: >= MinSelectionSize, Height: >= MinSelectionSize } selection)
        {
            HideHover();
            if (_purpose != OverlayPurpose.Capture)
            {
                Hide();
                RegionSelected?.Invoke(selection);
                Close();
                return;
            }

            EnterEditMode(selection);
        }
        else
        {
            Cancel();
        }
    }

    private void UpdateHoverWindow()
    {
        if (_mode != OverlayMode.Selecting || _draggingSelection)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastHoverProbe < 60)
        {
            return;
        }

        _lastHoverProbe = now;

        try
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            var rect = WindowSnap.FindTopWindowAt(cursor.X, cursor.Y, Environment.ProcessId);
            _hoverWindow = rect is { } window
                ? new Int32Rect(window.X - _virtualBounds.X, window.Y - _virtualBounds.Y,
                    window.Width, window.Height)
                : null;
        }
        catch
        {
            _hoverWindow = null;
        }

        UpdateHoverVisual();
    }

    private void UpdateHoverVisual()
    {
        if (_hoverWindow is not { } hover)
        {
            HideHover();
            return;
        }

        // 悬停时把遮罩挖洞到该窗口：窗口保持原亮度，其余区域变暗（与 PixPin 一致）
        UpdateMask(hover);

        var dip = ToDipRect(hover);
        HoverRect.Visibility = Visibility.Visible;
        HoverRect.Width = Math.Max(1, dip.Width);
        HoverRect.Height = Math.Max(1, dip.Height);
        Canvas.SetLeft(HoverRect, dip.X);
        Canvas.SetTop(HoverRect, dip.Y);
    }

    private void HideHover()
    {
        HoverRect.Visibility = Visibility.Collapsed;

        if (_hoverWindow is not null)
        {
            _hoverWindow = null;
            UpdateMask(_selection);
        }
    }

    // ---------------- 选区计算 ----------------

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mode != OverlayMode.Selecting || _toolbar?.IsMouseOver == true)
        {
            return;
        }

        var downPosition = e.GetPosition(this);
        _startImagePoint = ToImagePoint(downPosition);
        _currentImagePoint = _startImagePoint;
        _draggingSelection = true;
        _selection = null;

        SelectionChanged();

        CaptureMouse();
        e.Handled = true;
    }

    private void RecomputeSelection()
    {
        var startX = _startImagePoint.X;
        var startY = _startImagePoint.Y;
        var currentX = _currentImagePoint.X;
        var currentY = _currentImagePoint.Y;

        // 按住 Shift 锁定 1:1 正方形（与 PixPin 一致）
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var size = Math.Max(Math.Abs(currentX - startX), Math.Abs(currentY - startY));
            var signX = currentX >= startX ? 1 : -1;
            var signY = currentY >= startY ? 1 : -1;
            currentX = startX + signX * size;
            currentY = startY + signY * size;
        }

        var left = (int)Math.Floor(Math.Min(startX, currentX));
        var top = (int)Math.Floor(Math.Min(startY, currentY));
        var right = (int)Math.Ceiling(Math.Max(startX, currentX));
        var bottom = (int)Math.Ceiling(Math.Max(startY, currentY));

        left = Math.Clamp(left, 0, _snapshot.PixelWidth - 1);
        top = Math.Clamp(top, 0, _snapshot.PixelHeight - 1);
        right = Math.Clamp(right, left + 1, _snapshot.PixelWidth);
        bottom = Math.Clamp(bottom, top + 1, _snapshot.PixelHeight);

        _selection = new Int32Rect(left, top, right - left, bottom - top);
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        if (_selection is not { } selection)
        {
            SelRect.Visibility = Visibility.Collapsed;
            SelRectInner.Visibility = Visibility.Collapsed;
            InfoBar.Visibility = Visibility.Collapsed;
            OptionsBar.Visibility = Visibility.Collapsed;
            UpdateMask(null);
            UpdateHandles();
            return;
        }

        var dip = ToDipRect(selection);
        PositionSelectionBorder(SelRect, dip);
        PositionSelectionBorder(SelRectInner, dip);

        InfoBar.Visibility = Visibility.Visible;
        OptionsBar.Visibility = Visibility.Visible;
        InfoText.Text = $"{selection.Width} × {selection.Height}   ({selection.X}, {selection.Y})";
        PositionInfoBar(dip);
        UpdateMask(selection);
        UpdateHandles();
    }

    private static void PositionSelectionBorder(Rectangle border, Rect dip)
    {
        border.Visibility = Visibility.Visible;
        border.Width = Math.Max(1, dip.Width);
        border.Height = Math.Max(1, dip.Height);
        Canvas.SetLeft(border, dip.X);
        Canvas.SetTop(border, dip.Y);
    }

    /// <summary>调整选区：重建背景裁剪，并把已有标注平移，保持画面相对位置。</summary>
    private void ApplySelection(Int32Rect selection)
    {
        if (_selection is not { } previous)
        {
            _selection = selection;
            SelectionChanged();
            return;
        }

        var dx = selection.X - previous.X;
        var dy = selection.Y - previous.Y;

        _selection = selection;

        if (_mode == OverlayMode.Editing && _annotationLayer is not null)
        {
            var crop = new CroppedBitmap(_snapshot, selection);
            crop.Freeze();
            _editorBackground = crop;
            _annotationLayer.Background = crop;

            if (dx != 0 || dy != 0)
            {
                _annotationLayer.TranslateAll(-dx, -dy);
            }

            var dip = ToDipRect(selection);
            Canvas.SetLeft(_annotationLayer, dip.X);
            Canvas.SetTop(_annotationLayer, dip.Y);
            _annotationLayer.Width = dip.Width;
            _annotationLayer.Height = dip.Height;
            PositionToolbar(dip);
        }

        SelectionChanged();
    }

    private void PositionInfoBar(Rect selectionDip)
    {        var top = selectionDip.Y - 36;
        if (top < 8)
        {
            top = selectionDip.Y + 8;
        }

        InfoBar.HorizontalAlignment = HorizontalAlignment.Left;
        InfoBar.VerticalAlignment = VerticalAlignment.Top;
        InfoBar.Margin = new Thickness(Math.Max(8, selectionDip.X), top, 0, 0);

        // 选项条跟在信息条下方，越界时翻到上方
        var optionsTop = top + 32;
        if (optionsTop + 28 > Math.Max(0, ActualHeight - 8))
        {
            optionsTop = Math.Max(8, top - 34);
        }

        OptionsBar.HorizontalAlignment = HorizontalAlignment.Left;
        OptionsBar.VerticalAlignment = VerticalAlignment.Top;
        OptionsBar.Margin = new Thickness(Math.Max(8, selectionDip.X), optionsTop, 0, 0);
    }

    private void OnInfoBarClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (_selection is not { } selection)
        {
            return;
        }

        var dialog = new SizeInputDialog(selection.Width, selection.Height) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            var width = Math.Min(dialog.WidthValue, _snapshot.PixelWidth - selection.X);
            var height = Math.Min(dialog.HeightValue, _snapshot.PixelHeight - selection.Y);

            if (width >= MinSelectionSize && height >= MinSelectionSize)
            {
                ApplySelection(new Int32Rect(selection.X, selection.Y, width, height));
                ShowTip($"选区已调整为 {width} × {height}");
            }
        }

        Focus();
        Keyboard.Focus(this);
    }

    private void UpdateMask(Int32Rect? hole)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (hole is not { } selection)
        {
            SetMaskRect(MaskTop, 0, 0, width, height);
            SetMaskRect(MaskBottom, 0, 0, 0, 0);
            SetMaskRect(MaskLeft, 0, 0, 0, 0);
            SetMaskRect(MaskRight, 0, 0, 0, 0);
            return;
        }

        var dip = ToDipRect(selection);
        var left = Math.Max(0, dip.X);
        var top = Math.Max(0, dip.Y);
        var right = Math.Min(width, dip.Right);
        var bottom = Math.Min(height, dip.Bottom);

        // 四块遮罩拼出挖洞效果，避免依赖 Geometry 组合
        SetMaskRect(MaskTop, 0, 0, width, top);
        SetMaskRect(MaskBottom, 0, bottom, width, Math.Max(0, height - bottom));
        SetMaskRect(MaskLeft, 0, top, left, Math.Max(0, bottom - top));
        SetMaskRect(MaskRight, right, top, Math.Max(0, width - right), Math.Max(0, bottom - top));

    }

    private static void SetMaskRect(Rectangle shape, double left, double top, double width,
        double height)
    {
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
        shape.Width = Math.Max(0, width);
        shape.Height = Math.Max(0, height);
    }

    // ---------------- 放大镜 ----------------

    private void UpdateMagnifier(Point position)
    {
        if (_mode != OverlayMode.Selecting)
        {
            Magnifier.Visibility = Visibility.Collapsed;
            CrossHorizontal.Visibility = Visibility.Collapsed;
            CrossVertical.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateCrosshair(position);

        var imagePoint = ToImagePoint(position);
        var x = (int)imagePoint.X;
        var y = (int)imagePoint.Y;

        if (x < 0 || y < 0 || x >= _snapshot.PixelWidth || y >= _snapshot.PixelHeight)
        {
            Magnifier.Visibility = Visibility.Collapsed;
            return;
        }

        const int sample = 15;
        var sourceX = Math.Clamp(x - sample / 2, 0, Math.Max(0, _snapshot.PixelWidth - sample));
        var sourceY = Math.Clamp(y - sample / 2, 0, Math.Max(0, _snapshot.PixelHeight - sample));

        var pixels = new byte[sample * sample * 4];
        _snapshot.CopyPixels(new Int32Rect(sourceX, sourceY, sample, sample),
            pixels, sample * 4, 0);

        var bitmap = BitmapSource.Create(sample, sample, 96, 96, PixelFormats.Bgra32,
            null, pixels, sample * 4);
        bitmap.Freeze();
        MagnifierImage.Source = bitmap;

        var index = ((y - sourceY) * sample + (x - sourceX)) * 4;
        var blue = pixels[index];
        var green = pixels[index + 1];
        var red = pixels[index + 2];
        _currentHexColor = $"#{red:X2}{green:X2}{blue:X2}";

        // 按住 Shift 显示 HEX，松开显示 RGB（对齐 PixPin）
        var hexFirst = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _currentColorText = hexFirst
            ? _currentHexColor
            : $"RGB({red}, {green}, {blue})";
        _currentRgbColor = Color.FromRgb(red, green, blue);

        MagnifierText.Text = $"{x},{y}  {_currentColorText}";

        const double width = 150;
        const double height = 168;
        var left = position.X + 20;
        var top = position.Y + 20;

        if (left + width > ActualWidth)
        {
            left = position.X - width - 20;
        }

        if (top + height > ActualHeight)
        {
            top = position.Y - height - 20;
        }

        Magnifier.Margin = new Thickness(Math.Max(0, left), Math.Max(0, top), 0, 0);
        Magnifier.HorizontalAlignment = HorizontalAlignment.Left;
        Magnifier.VerticalAlignment = VerticalAlignment.Top;
        Magnifier.Visibility = Visibility.Visible;
    }

    // ---------------- 编辑模式 ----------------

    /// <summary>外部直接进入编辑（长截图完成后复用编辑/标注流程）。</summary>
    public void EnterEditWithSelection(Int32Rect selection)
    {
        var x = Math.Clamp(selection.X, 0, Math.Max(0, _snapshot.PixelWidth - 1));
        var y = Math.Clamp(selection.Y, 0, Math.Max(0, _snapshot.PixelHeight - 1));
        var width = Math.Clamp(selection.Width, 1, _snapshot.PixelWidth - x);
        var height = Math.Clamp(selection.Height, 1, _snapshot.PixelHeight - y);

        EnterEditMode(new Int32Rect(x, y, width, height));
    }

    private void EnterEditMode(Int32Rect selection)
    {
        ReleaseMouseCapture();
        _mode = OverlayMode.Editing;
        _selection = selection;
        Magnifier.Visibility = Visibility.Collapsed;
        CrossHorizontal.Visibility = Visibility.Collapsed;
        CrossVertical.Visibility = Visibility.Collapsed;

        var crop = new CroppedBitmap(_snapshot, selection);
        crop.Freeze();
        _editorBackground = crop;

        _annotationLayer = new AnnotationLayer { Background = crop };
        _annotationLayer.TextRequested += OnTextRequested;
        _annotationLayer.TextEditRequested += OnTextEditRequested;
        _annotationLayer.Changed += UpdateToolbarState;

        var dip = ToDipRect(selection);
        Canvas.SetLeft(_annotationLayer, dip.X);
        Canvas.SetTop(_annotationLayer, dip.Y);
        _annotationLayer.Width = dip.Width;
        _annotationLayer.Height = dip.Height;
        EditorCanvas.Children.Add(_annotationLayer);

        _toolbar = new EditorToolbar();
        _currentTool = EditorTool.None;
        _toolbar.ToolSelected += tool =>
        {
            _currentTool = tool;

            if (_annotationLayer is not null)
            {
                _annotationLayer.Tool = tool;
                _annotationLayer.Focus();
            }

            // 切换工具会改变工具条高度（上下文选项行），需要重新贴着选区摆放
            ScheduleToolbarReposition();
        };

        // 上下文选项行/工具条尺寸变化时同样重排，避免被屏幕边缘裁掉
        _toolbar.SizeChanged += (_, _) => ScheduleToolbarReposition();
        _toolbar.ColorSelected += color =>
        {
            if (_annotationLayer is not null)
            {
                _annotationLayer.DrawingStyle.Color = color;
                _annotationLayer.Focus();
            }
        };
        _toolbar.ThicknessSelected += thickness =>
        {
            if (_annotationLayer is not null)
            {
                _annotationLayer.DrawingStyle.Thickness = thickness;
                _annotationLayer.Focus();
            }
        };
        _toolbar.UndoRequested += () =>
        {
            _annotationLayer?.Undo();
            _annotationLayer?.Focus();
        };
        _toolbar.RedoRequested += () =>
        {
            _annotationLayer?.Redo();
            _annotationLayer?.Focus();
        };
        _toolbar.CopyRequested += () => Complete(CaptureAction.Copy);
        _toolbar.SaveRequested += () => Complete(CaptureAction.Save);
        _toolbar.PinRequested += () => Complete(CaptureAction.Pin);
        _toolbar.OcrRequested += RequestOcr;
        _toolbar.LongShotRequested += () =>
        {
            if (_selection is not { } region)
            {
                return;
            }

            Hide();
            LongShotRequested?.Invoke(region);
            Close();
        };
        _toolbar.RecordRequested += () =>
        {
            if (_selection is not { } region)
            {
                return;
            }

            Hide();
            RecordRequested?.Invoke(region);
            Close();
        };
        _toolbar.CancelRequested += Cancel;

        EditorCanvas.Children.Add(_toolbar);
        PositionToolbar(dip);

        TipText.Text = "拖动选区内部可整体挪移 · 双击复制 · 拖手柄改大小 · 选工具后标注 · Enter 复制 · Esc 取消";
        _annotationLayer.Focus();
        UpdateToolbarState();
        UpdateHandles();
    }

    private void PositionToolbar(Rect selectionDip)
    {
        if (_toolbar is null)
        {
            return;
        }

        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _toolbar.DesiredSize;

        var left = selectionDip.X + (selectionDip.Width - size.Width) / 2;
        left = Math.Clamp(left, 4, Math.Max(4, ActualWidth - size.Width - 4));

        var top = selectionDip.Y + selectionDip.Height + 10;
        if (top + size.Height > ActualHeight - 4)
        {
            top = selectionDip.Y - size.Height - 10;
        }

        top = Math.Clamp(top, 4, Math.Max(4, ActualHeight - size.Height - 4));

        Canvas.SetLeft(_toolbar, left);
        Canvas.SetTop(_toolbar, top);
    }

    /// <summary>布局完成后再重排工具条，保证上下文选项行展开后能重新贴边。</summary>
    private void ScheduleToolbarReposition()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_toolbar is null || _selection is not { } selection || _mode != OverlayMode.Editing)
            {
                return;
            }

            PositionToolbar(ToDipRect(selection));
        }), DispatcherPriority.Loaded);
    }

    private void UpdateToolbarState()
    {
        if (_toolbar is null || _annotationLayer is null)
        {
            return;
        }

        _toolbar.UpdateState(_annotationLayer.CanUndo, _annotationLayer.CanRedo);
    }

    private void OnTextRequested(Point imagePoint)
    {
        var dialog = new TextInputDialog { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Text))
        {
            _annotationLayer?.AddText(imagePoint, dialog.Text!);
        }

        _annotationLayer?.Focus();
    }

    private void OnTextEditRequested(TextAnnotation annotation)
    {
        var dialog = new TextInputDialog(annotation.Text) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _annotationLayer?.UpdateText(annotation, dialog.Text ?? string.Empty);
        }

        _annotationLayer?.Focus();
    }

    // ---------------- 键盘 ----------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _mode == OverlayMode.Editing)
        {
            _spaceHeld = true;
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                HandleEscape();
                e.Handled = true;
                break;

            case Key.Enter when _mode == OverlayMode.Editing || _selection is not null:
                // 折线未结束时，Enter 先收尾折线而不是直接复制
                if (_mode == OverlayMode.Editing && _annotationLayer?.FinishPolyline() == true)
                {
                    e.Handled = true;
                    break;
                }

                Complete(CaptureAction.Copy);
                e.Handled = true;
                break;

            case Key.C when Keyboard.Modifiers == ModifierKeys.Control && _selection is not null:
                Complete(CaptureAction.Copy);
                e.Handled = true;
                break;

            case Key.S when Keyboard.Modifiers == ModifierKeys.Control && _selection is not null:
                Complete(CaptureAction.Save);
                e.Handled = true;
                break;

            case Key.A when Keyboard.Modifiers == ModifierKeys.Control && _mode == OverlayMode.Selecting:
                SelectWholeScreen();
                e.Handled = true;
                break;

            case Key.R when Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift:
                RestorePreviousSelection(Keyboard.Modifiers == ModifierKeys.Shift);
                e.Handled = true;
                break;

            case Key.R when Keyboard.Modifiers == ModifierKeys.Control:
                CycleCornerRadius();
                e.Handled = true;
                break;

            case Key.OemTilde:
                ToggleCursor();
                e.Handled = true;
                break;

            case Key.P when _selection is not null:
                Complete(CaptureAction.Pin);
                e.Handled = true;
                break;

            case Key.O when _mode == OverlayMode.Editing && _selection is not null:
                RequestOcr();
                e.Handled = true;
                break;

            case Key.C when _mode == OverlayMode.Selecting:
                // 鼠标没动过时 _currentHexColor 可能为空，按当前光标位置实时采样
                UpdateMagnifier(Mouse.GetPosition(this));
                if (_currentColorText is not null)
                {
                    Clipboard.SetText(_currentColorText);
                    ShowTip($"已复制颜色 {_currentColorText}");
                }

                e.Handled = true;
                break;

            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control && _mode == OverlayMode.Editing:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    _annotationLayer?.Redo();
                }
                else
                {
                    _annotationLayer?.Undo();
                }

                e.Handled = true;
                break;

            case Key.Y when Keyboard.Modifiers == ModifierKeys.Control && _mode == OverlayMode.Editing:
                _annotationLayer?.Redo();
                e.Handled = true;
                break;

            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
                if (_mode == OverlayMode.Editing && _selection is not null)
                {
                    Nudge(e.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1);
                    e.Handled = true;
                }

                break;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _spaceHeld = false;
        }
    }

    private void RequestOcr()
    {
        if (_selection is not { } region)
        {
            return;
        }

        var cropped = new CroppedBitmap(_snapshot, region);
        cropped.Freeze();

        Hide();
        OcrRequested?.Invoke(cropped);
        Close();
    }

    private void ShowTip(string text)
    {
        _tipTimer?.Stop();

        var previous = TipText.Text;
        TipText.Text = text;

        _tipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _tipTimer.Tick += (_, _) =>
        {
            _tipTimer?.Stop();
            TipText.Text = previous;
        };
        _tipTimer.Start();
    }

    private void Nudge(Key key, int step)
    {
        if (_selection is not { } selection)
        {
            return;
        }

        var dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
        var dy = key == Key.Up ? -step : key == Key.Down ? step : 0;

        var moved = ResizeSelection(selection, Handle.Move,
            _snapshot.PixelWidth, _snapshot.PixelHeight, dx, dy);

        ApplySelection(moved);
    }

    // ---------------- 截图选项（圆角 / 阴影 / 光标） ----------------

    private static readonly int[] CornerSteps = { 0, 8, 16, 24 };

    private int _cornerRadius;
    private bool _addShadow;
    private bool _showCursor;

    private void OnCornerClicked(object sender, RoutedEventArgs e)
    {
        CycleCornerRadius();
        e.Handled = true;
    }

    private void CycleCornerRadius()
    {
        var index = Array.IndexOf(CornerSteps, _cornerRadius);
        _cornerRadius = CornerSteps[(index + 1) % CornerSteps.Length];
        UpdateOptionsBar();
        ShowTip(_cornerRadius == 0 ? "圆角已关闭" : $"圆角半径 {_cornerRadius}px");
    }

    private void OnShadowClicked(object sender, RoutedEventArgs e)
    {
        _addShadow = !_addShadow;
        UpdateOptionsBar();
        ShowTip(_addShadow ? "复制/保存时会加阴影" : "阴影已关闭");
        e.Handled = true;
    }

    private void OnCursorClicked(object sender, RoutedEventArgs e)
    {
        _showCursor = !_showCursor;
        UpdateOptionsBar();
        ShowTip(_showCursor ? "截图会包含鼠标指针" : "截图不包含鼠标指针");
        e.Handled = true;
    }

    private void UpdateOptionsBar()
    {
        CornerButton.Content = _cornerRadius == 0 ? "圆角" : $"圆角 {_cornerRadius}";
        ShadowButton.Content = _addShadow ? "阴影 ✓" : "阴影";
        CursorButton.Content = _showCursor ? "光标 ✓" : "光标";
    }

    private void ToggleCursor()
    {
        _showCursor = !_showCursor;
        UpdateOptionsBar();
        ShowTip(_showCursor ? "截图会包含鼠标指针" : "截图不包含鼠标指针");
    }

    private void UpdateCrosshair(Point position)
    {
        if (_mode != OverlayMode.Selecting)
        {
            CrossHorizontal.Visibility = Visibility.Collapsed;
            CrossVertical.Visibility = Visibility.Collapsed;
            return;
        }

        CrossHorizontal.Visibility = Visibility.Visible;
        CrossVertical.Visibility = Visibility.Visible;

        CrossHorizontal.X1 = 0;
        CrossHorizontal.X2 = Math.Max(0, ActualWidth);
        CrossHorizontal.Y1 = position.Y;
        CrossHorizontal.Y2 = position.Y;

        CrossVertical.Y1 = 0;
        CrossVertical.Y2 = Math.Max(0, ActualHeight);
        CrossVertical.X1 = position.X;
        CrossVertical.X2 = position.X;
    }

    /// <summary>输出前按当前选项做圆角裁剪、阴影和鼠标指针合成。</summary>
    private BitmapSource Decorate(BitmapSource source)
        => _selection is { } selection
            ? ApplyOptions(source, selection, _virtualBounds, _cornerRadius, _addShadow, _showCursor)
            : source;

    /// <summary>把截图装饰选项（圆角/阴影/光标）应用到输出图像，自检与主流程共用。</summary>
    internal static BitmapSource ApplyOptions(BitmapSource source, Int32Rect selection,
        Int32Rect virtualBounds, int cornerRadius, bool addShadow, bool showCursor)
    {
        var withCursor = ComposeCursor(source, selection, virtualBounds, showCursor);

        if (cornerRadius <= 0 && !addShadow)
        {
            return withCursor;
        }

        const int shadowPadding = 20;
        var padding = addShadow ? shadowPadding : 0;
        var width = source.PixelWidth + padding * 2;
        var height = source.PixelHeight + padding * 2;

        var contentRect = new Rect(padding, padding, source.PixelWidth, source.PixelHeight);
        Geometry clip = cornerRadius > 0
            ? new RectangleGeometry(contentRect, cornerRadius, cornerRadius)
            : new RectangleGeometry(contentRect);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (addShadow)
            {
                var shadowBrush = new SolidColorBrush(Color.FromArgb(0x6B, 0, 0, 0));
                for (var grow = 2; grow <= shadowPadding; grow += 2)
                {
                    var shadowRect = new Rect(
                        contentRect.X - grow + 2,
                        contentRect.Y - grow + 4,
                        contentRect.Width + grow * 2,
                        contentRect.Height + grow * 2);

                    Geometry shadowGeometry = cornerRadius > 0
                        ? new RectangleGeometry(shadowRect, cornerRadius + grow, cornerRadius + grow)
                        : new RectangleGeometry(shadowRect);

                    dc.PushOpacity(0.055);
                    dc.DrawGeometry(shadowBrush, null, shadowGeometry);
                    dc.Pop();
                }
            }

            dc.PushClip(clip);
            dc.DrawImage(withCursor, contentRect);
            dc.Pop();
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>把当前鼠标指针画到截图里（对应 PixPin 的 ~ 显示光标）。</summary>
    private static BitmapSource ComposeCursor(BitmapSource source, Int32Rect selection,
        Int32Rect virtualBounds, bool showCursor)
    {
        if (!showCursor)
        {
            return source;
        }

        try
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            var imageX = cursor.X - virtualBounds.X;
            var imageY = cursor.Y - virtualBounds.Y;

            if (imageX < selection.X || imageY < selection.Y ||
                imageX >= selection.X + selection.Width || imageY >= selection.Y + selection.Height)
            {
                return source;
            }

            var info = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref info) || info.hCursor == IntPtr.Zero || (info.flags & 1) == 0)
            {
                return source;
            }

            var stride = source.PixelWidth * 4;
            var pixels = new byte[stride * source.PixelHeight];
            source.CopyPixels(pixels, stride, 0);

            using var bitmap = new System.Drawing.Bitmap(source.PixelWidth, source.PixelHeight,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(
                new System.Drawing.Rectangle(0, 0, source.PixelWidth, source.PixelHeight),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            bitmap.UnlockBits(data);

            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    DrawIconEx(hdc, imageX - selection.X, imageY - selection.Y,
                        info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }

            var converted = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bitmap.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            converted.Freeze();
            return converted;
        }
        catch
        {
            return source;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public int x;
        public int y;
    }

    private const uint DI_NORMAL = 0x0003;

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    // ---------------- 完成 / 取消 ----------------

    /// <summary>Esc 分三级：先取消折线/选中标注，再退出编辑回到选区，最后才取消整张截图。</summary>
    private void HandleEscape()
    {
        if (_mode == OverlayMode.Editing)
        {
            if (_annotationLayer?.CancelSelectionOrPending() == true)
            {
                return;
            }

            ExitEditMode();
            return;
        }

        Cancel();
    }

    private void ExitEditMode()
    {
        if (_mode != OverlayMode.Editing)
        {
            return;
        }

        if (_annotationLayer is not null)
        {
            EditorCanvas.Children.Remove(_annotationLayer);
            _annotationLayer = null;
        }

        if (_toolbar is not null)
        {
            EditorCanvas.Children.Remove(_toolbar);
            _toolbar = null;
        }

        _editorBackground = null;
        Magnifier.Visibility = Visibility.Collapsed;
        _mode = OverlayMode.Selecting;
        SelectionChanged();

        if (_selection is { } selection)
        {
            ShowTip($"{selection.Width} × {selection.Height} · 回车复制 · Ctrl+S 保存 · P 贴图 · Esc 取消");
        }
    }

    private void SelectWholeScreen()
    {
        if (_mode != OverlayMode.Selecting)
        {
            return;
        }

        var whole = new Int32Rect(0, 0, _snapshot.PixelWidth, _snapshot.PixelHeight);
        _selection = whole;
        SelectionChanged();
        EnterEditMode(whole);
    }

    private void RestorePreviousSelection(bool forward)
    {
        if (_mode == OverlayMode.Editing)
        {
            return;
        }

        var region = CaptureSelectionStore.Previous(forward);
        if (region is not { } rect)
        {
            ShowTip("还没有历史截图区域");
            return;
        }

        rect = ClampRegion(rect);
        _selection = rect;
        SelectionChanged();
        EnterEditMode(rect);
        ShowTip($"已载入历史选区 {rect.Width} × {rect.Height}");
    }

    private Int32Rect ClampRegion(Int32Rect rect)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, _snapshot.PixelWidth - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, _snapshot.PixelHeight - 1));
        var width = Math.Clamp(rect.Width, 2, Math.Max(2, _snapshot.PixelWidth - x));
        var height = Math.Clamp(rect.Height, 2, Math.Max(2, _snapshot.PixelHeight - y));
        return new Int32Rect(x, y, width, height);
    }

    /// <summary>测试用：自动完成一次「复制」，用于验证命令行退出流程。</summary>
    internal void CompleteForTest() => Complete(CaptureAction.Copy);

    private void Complete(CaptureAction action)
    {
        BitmapSource output;

        if (_mode == OverlayMode.Editing && _annotationLayer is not null && _editorBackground is not null)
        {
            output = _annotationLayer.HasAnnotations
                ? _annotationLayer.Render(_editorBackground)
                : _editorBackground;
        }
        else if (_selection is { } selection)
        {
            var cropped = new CroppedBitmap(_snapshot, selection);
            cropped.Freeze();
            output = cropped;
        }
        else
        {
            return;
        }

        if (_selection is { } used)
        {
            CaptureSelectionStore.Remember(used);
        }

        output = Decorate(output);

        Log($"complete action={action} size={output.PixelWidth}x{output.PixelHeight}");

        Hide();

        // 无论回调里发生什么（包括调用方直接 Shutdown），都保证窗口关闭
        try
        {
            Completed?.Invoke(new CaptureResult(output, action));
        }
        finally
        {
            Close();
            Log("complete: window closed");
        }
    }

    private void Cancel()
    {
        Log("cancel");
        Hide();

        try
        {
            Completed?.Invoke(null);
        }
        finally
        {
            Close();
        }
    }
}
