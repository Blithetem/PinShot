using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PinShot.App.Editor;

/// <summary>
/// 标注层：覆盖在选区之上，负责绘制标注、鼠标交互、撤销重做，以及导出合成。
/// 自身坐标是 DIP，内部统一折算为「选区图像像素」。
/// </summary>
/// <remarks>
/// 二次编辑能力（本轮新增）：
/// <list type="bullet">
/// <item>点击已有标注 → 选中（虚线框 + 8 个手柄），拖动移动，手柄拖动缩放；</item>
/// <item><c>Delete</c>/<c>Backspace</c> 删除选中；<c>Esc</c> 先取消选中/取消折线，再交给上层取消截图；</item>
/// <item>工具条改颜色/线宽/字号/文字背景时，同步作用到选中标注（订阅 <see cref="DrawingStyle"/> 的属性变更）；</item>
/// <item>滚轮调线宽、<c>Ctrl+滚轮</c> 调透明度（选中优先，否则作用于当前画笔）。</item>
/// </list>
/// </remarks>
public sealed class AnnotationLayer : FrameworkElement
{
    private const int MaxUndoDepth = 100;
    private const double HandleSize = 8;
    private const double HandleHitTolerance = 9;
    private const int StyleUndoCoalesceMs = 450;
    private const double MinResizeSize = 2;

    private static readonly double[] ThicknessSteps = { 1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20 };
    private static readonly double[] OpacitySteps = { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0 };

    private readonly List<IAnnotation> _annotations = new();
    private readonly List<List<IAnnotation>> _undoStack = new();
    private readonly List<List<IAnnotation>> _redoStack = new();

    private AnnotationStyle _drawingStyle = new();

    private IAnnotation? _draft;
    private PenAnnotation? _penDraft;
    private PolylineAnnotation? _polylineDraft;
    private Point _polylineCursor;
    private Point _startImage;
    private bool _dragging;
    private bool _erasing;

    private IAnnotation? _selected;
    private int _selectedIndex = -1;
    private bool _movingSelected;
    private Point _moveLastPoint;

    private int _resizeHandle = -1;
    private Rect _resizeStartBounds;
    private IAnnotation? _resizeOriginal;
    private List<IAnnotation>? _resizeSnapshot;
    private bool _resizeUndoPushed;

    private List<IAnnotation>? _pendingSnapshot;
    private long _lastStyleUndoAt;

    private bool _spotlightEllipse = true;
    private int _magnifierZoom = 4;
    private string _watermarkText = "$yyyy-MM-dd HH:mm:ss$";
    private double _watermarkAngle = -30;
    private double _watermarkOpacity = 0.22;

    public AnnotationLayer()
    {
        ClipToBounds = true;
        Focusable = true;
        _drawingStyle.PropertyChanged += OnDrawingStyleChanged;
    }

    // ---------------- 公共 API（保持与既有调用方兼容） ----------------

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value)
            {
                return;
            }

            // 切换工具时把未完成的折线收尾，避免留下悬空草稿。
            if (_polylineDraft is not null)
            {
                FinishPolylineCore();
            }

            _tool = value;
        }
    }

    private EditorTool _tool = EditorTool.Rectangle;

    public AnnotationStyle DrawingStyle
    {
        get => _drawingStyle;
        set
        {
            var style = value ?? new AnnotationStyle();
            if (ReferenceEquals(style, _drawingStyle))
            {
                return;
            }

            _drawingStyle.PropertyChanged -= OnDrawingStyleChanged;
            _drawingStyle = style;
            _drawingStyle.PropertyChanged += OnDrawingStyleChanged;
        }
    }

    public BitmapSource? Background { get; set; }

    /// <summary>聚光灯形状：true=椭圆，false=矩形。</summary>
    public bool SpotlightEllipse
    {
        get => _spotlightEllipse;
        set
        {
            _spotlightEllipse = value;
            if (_selected is SpotlightAnnotation spotlight && spotlight.IsEllipse != value)
            {
                PushUndoCoalesced();
                spotlight.IsEllipse = value;
                InvalidateVisual();
                Changed?.Invoke();
            }
        }
    }

    /// <summary>放大镜倍率（1–16，常用 2/4/8）。</summary>
    public int MagnifierZoom
    {
        get => _magnifierZoom;
        set
        {
            _magnifierZoom = Math.Clamp(value, 1, 16);
            if (_selected is MagnifierAnnotation magnifier && magnifier.Zoom != _magnifierZoom)
            {
                PushUndoCoalesced();
                magnifier.Zoom = _magnifierZoom;
                InvalidateVisual();
                Changed?.Invoke();
            }
        }
    }

    /// <summary>水印文字模板，支持 <c>$yyyy-MM-dd HH:mm:ss$</c> 时间变量。</summary>
    public string WatermarkText
    {
        get => _watermarkText;
        set
        {
            _watermarkText = string.IsNullOrWhiteSpace(value)
                ? "$yyyy-MM-dd HH:mm:ss$"
                : value;
            ApplyToSelectedWatermark();
        }
    }

    /// <summary>水印旋转角度（度）。</summary>
    public double WatermarkAngle
    {
        get => _watermarkAngle;
        set
        {
            _watermarkAngle = value;
            ApplyToSelectedWatermark();
        }
    }

    /// <summary>水印透明度（0–1）。</summary>
    public double WatermarkOpacity
    {
        get => _watermarkOpacity;
        set
        {
            _watermarkOpacity = Math.Clamp(value, 0.02, 1);
            ApplyToSelectedWatermark();
        }
    }

    /// <summary>滚轮是否用于调线宽/透明度；长图编辑器在需要滚动画布时会关掉。</summary>
    public bool WheelAdjustsStyle { get; set; } = true;

    public bool HasAnnotations => _annotations.Count > 0;

    public int AnnotationCount => _annotations.Count;

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    public bool HasSelection => _selected is not null;

    public bool HasPendingPolyline => _polylineDraft is not null;

    /// <summary>请求输入文字（参数为图像坐标）。</summary>
    public event Action<Point>? TextRequested;

    /// <summary>请求编辑已有文字标注（双击文字时触发）。</summary>
    public event Action<TextAnnotation>? TextEditRequested;

    public event Action? Changed;

    // ---------------- 坐标换算 ----------------

    private double DipsPerPixel =>
        Background is null || Background.PixelWidth == 0 || ActualWidth <= 0
            ? 1
            : ActualWidth / Background.PixelWidth;

    private Point ToImagePoint(Point dipPoint)
    {
        var scale = DipsPerPixel;
        if (scale <= 0)
        {
            return dipPoint;
        }

        return new Point(dipPoint.X / scale, dipPoint.Y / scale);
    }

    // ---------------- 绘制 ----------------

    protected override void OnRender(DrawingContext dc)
    {
        // 透明底板，保证整层参与命中测试（否则空白区域点不到）
        dc.DrawRectangle(Brushes.Transparent, null,
            new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)));

        if (Background is null)
        {
            return;
        }

        var dipWidth = ActualWidth;
        var dipHeight = ActualHeight;
        if (dipWidth <= 0 || dipHeight <= 0)
        {
            return;
        }

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, dipWidth, dipHeight)));
        dc.PushTransform(new ScaleTransform(dipWidth / Background.PixelWidth,
            dipHeight / Background.PixelHeight));

        foreach (var annotation in _annotations)
        {
            annotation.Draw(dc, Background);
        }

        if (_polylineDraft is not null)
        {
            DrawPolylinePreview(dc);
        }
        else
        {
            _draft?.Draw(dc, Background);
        }

        if (_selected is not null)
        {
            DrawSelection(dc, _selected);
        }

        dc.Pop();
        dc.Pop();
    }

    private void DrawPolylinePreview(DrawingContext dc)
    {
        if (_polylineDraft is null)
        {
            return;
        }

        _polylineDraft.Draw(dc, Background);

        // 最后一段跟随光标的橡皮筋
        var points = _polylineDraft.Points;
        if (points.Count > 0)
        {
            var brush = new SolidColorBrush(_polylineDraft.Style.Color)
            {
                Opacity = Math.Clamp(_polylineDraft.Style.Opacity * 0.55, 0, 1),
            };
            brush.Freeze();
            var pen = new Pen(brush, Math.Max(1, _polylineDraft.Style.Thickness))
            {
                DashStyle = new DashStyle(new double[] { 3, 3 }, 0),
            };
            pen.Freeze();
            dc.DrawLine(pen, points[^1], _polylineCursor);

            var dot = new SolidColorBrush(_polylineDraft.Style.Color);
            dot.Freeze();
            foreach (var point in points)
            {
                dc.DrawEllipse(dot, null, point, 2.2, 2.2);
            }
        }
    }

    private void DrawSelection(DrawingContext dc, IAnnotation annotation)
    {
        var bounds = annotation.Bounds;
        if (bounds.IsEmpty)
        {
            return;
        }

        var selectionPen = new Pen(new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xFF)), 1.2)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
        };
        selectionPen.Freeze();

        var frame = bounds;
        frame.Inflate(3, 3);
        dc.DrawRectangle(null, selectionPen, frame);

        var handleBrush = new SolidColorBrush(Colors.White);
        handleBrush.Freeze();
        var handlePen = new Pen(new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xFF)), 1.2);
        handlePen.Freeze();

        foreach (var point in GetHandlePoints(bounds))
        {
            dc.DrawRectangle(handleBrush, handlePen,
                new Rect(point.X - HandleSize / 2, point.Y - HandleSize / 2,
                    HandleSize, HandleSize));
        }
    }

    private static Point[] GetHandlePoints(Rect bounds)
    {
        var left = bounds.Left;
        var top = bounds.Top;
        var right = bounds.Right;
        var bottom = bounds.Bottom;
        var centerX = (left + right) / 2;
        var centerY = (top + bottom) / 2;

        return new[]
        {
            new Point(left, top),       // 0 TopLeft
            new Point(centerX, top),    // 1 Top
            new Point(right, top),      // 2 TopRight
            new Point(right, centerY),  // 3 Right
            new Point(right, bottom),   // 4 BottomRight
            new Point(centerX, bottom), // 5 Bottom
            new Point(left, bottom),    // 6 BottomLeft
            new Point(left, centerY),   // 7 Left
        };
    }

    // ---------------- 鼠标 ----------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var imagePoint = ToImagePoint(e.GetPosition(this));

        // 1) 已选中标注的手柄 → 缩放
        if (_selected is not null && TryHitHandle(imagePoint, out var handle))
        {
            BeginResize(handle);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        // 2) 折线进行中 → 继续加点，双击结束
        if (_polylineDraft is not null)
        {
            if (e.ClickCount >= 2)
            {
                FinishPolylineCore();
            }
            else
            {
                AddPolylinePoint(imagePoint);
            }

            e.Handled = true;
            return;
        }

        // 3) 命中已有标注 → 选中并准备移动
        var hit = HitTestAnnotation(imagePoint);
        if (hit is not null)
        {
            if (e.ClickCount >= 2 && hit is TextAnnotation text)
            {
                _selected = hit;
                _selectedIndex = _annotations.IndexOf(hit);
                InvalidateVisual();
                TextEditRequested?.Invoke(text);
                e.Handled = true;
                return;
            }

            BeginMove(hit, imagePoint);
            e.Handled = true;
            return;
        }

        // 4) 点空白：取消选中后按当前工具绘制
        ClearSelectionCore();

        CaptureMouse();
        _startImage = imagePoint;

        switch (Tool)
        {
            case EditorTool.Text:
                ReleaseMouseCapture();
                TextRequested?.Invoke(imagePoint);
                return;

            case EditorTool.Number:
                Commit(new NumberAnnotation(DrawingStyle, imagePoint, NextNumber()));
                ReleaseMouseCapture();
                return;

            case EditorTool.Watermark:
                AddWatermark();
                ReleaseMouseCapture();
                return;

            case EditorTool.Polyline:
                _polylineDraft = new PolylineAnnotation(DrawingStyle, imagePoint);
                _polylineCursor = imagePoint;
                InvalidateVisual();
                return;

            case EditorTool.Eraser:
                _erasing = true;
                _pendingSnapshot = Snapshot();
                EraseAt(imagePoint);
                return;

            case EditorTool.Pen:
            case EditorTool.Highlighter:
                _penDraft = new PenAnnotation(DrawingStyle, Tool == EditorTool.Highlighter);
                _penDraft.Points.Add(imagePoint);
                _draft = _penDraft;
                _dragging = true;
                InvalidateVisual();
                return;

            default:
                _dragging = true;
                UpdateDraft(imagePoint);
                return;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var imagePoint = ToImagePoint(e.GetPosition(this));

        if (_resizeHandle >= 0)
        {
            UpdateResize(imagePoint);
            return;
        }

        if (_movingSelected && _selected is not null)
        {
            var delta = imagePoint - _moveLastPoint;
            if (delta.Length >= 0.5)
            {
                EnsurePendingUndo();
                _selected.Translate(delta.X, delta.Y);
                _moveLastPoint = imagePoint;
                InvalidateVisual();
            }

            return;
        }

        if (_erasing)
        {
            EraseAt(imagePoint);
            return;
        }

        if (_polylineDraft is not null)
        {
            _polylineCursor = imagePoint;
            InvalidateVisual();
            return;
        }

        if (!_dragging)
        {
            return;
        }

        if (_penDraft is not null)
        {
            var last = _penDraft.Points[^1];
            if ((imagePoint - last).Length >= 1.5)
            {
                _penDraft.Points.Add(imagePoint);
                InvalidateVisual();
            }

            return;
        }

        UpdateDraft(imagePoint);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();

        if (_resizeHandle >= 0)
        {
            var changed = _resizeUndoPushed;
            _resizeHandle = -1;
            _resizeOriginal = null;
            _resizeSnapshot = null;
            _resizeUndoPushed = false;
            if (changed)
            {
                InvalidateVisual();
                Changed?.Invoke();
            }

            e.Handled = true;
            return;
        }

        if (_movingSelected)
        {
            _movingSelected = false;
            _pendingSnapshot = null;
            InvalidateVisual();
            Changed?.Invoke();
            return;
        }

        if (_erasing)
        {
            _erasing = false;
            _pendingSnapshot = null;
            Changed?.Invoke();
            return;
        }

        if (!_dragging)
        {
            return;
        }

        _dragging = false;

        if (_penDraft is not null)
        {
            if (_penDraft.Points.Count >= 2)
            {
                Commit(_penDraft);
            }

            _penDraft = null;
            _draft = null;
            InvalidateVisual();
            return;
        }

        if (_draft is not null)
        {
            Commit(_draft);
        }

        _draft = null;
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);

        // 折线进行中：右键结束（并吃掉事件，避免上层把"右键"当成取消截图）
        if (_polylineDraft is not null)
        {
            FinishPolylineCore();
            e.Handled = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (!WheelAdjustsStyle)
        {
            return;
        }

        var up = e.Delta > 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            AdjustOpacity(up);
        }
        else
        {
            AdjustThickness(up);
        }

        e.Handled = true;
    }

    private void AdjustThickness(bool up)
    {
        var current = DrawingStyle.Thickness;
        var index = NearestIndex(ThicknessSteps, current);
        index = Math.Clamp(index + (up ? 1 : -1), 0, ThicknessSteps.Length - 1);
        var next = ThicknessSteps[index];
        if (Math.Abs(next - current) < 0.001)
        {
            return;
        }

        DrawingStyle.Thickness = next;
    }

    private void AdjustOpacity(bool up)
    {
        var current = DrawingStyle.Opacity;
        var index = NearestIndex(OpacitySteps, current);
        index = Math.Clamp(index + (up ? 1 : -1), 0, OpacitySteps.Length - 1);
        var next = OpacitySteps[index];
        if (Math.Abs(next - current) < 0.001)
        {
            return;
        }

        DrawingStyle.Opacity = next;
    }

    private static int NearestIndex(double[] steps, double value)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < steps.Length; i++)
        {
            var distance = Math.Abs(steps[i] - value);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    // ---------------- 键盘 ----------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Escape:
                if (_polylineDraft is not null)
                {
                    CancelPolylineCore();
                    e.Handled = true;
                }
                else if (_selected is not null)
                {
                    ClearSelectionCore();
                    e.Handled = true;
                }

                break;

            case Key.Enter:
                if (_polylineDraft is not null)
                {
                    FinishPolylineCore();
                    e.Handled = true;
                }

                break;

            case Key.Delete:
            case Key.Back:
                if (_selected is not null)
                {
                    DeleteSelected();
                    e.Handled = true;
                }

                break;
        }
    }

    /// <summary>供上层（窗口级 Esc 处理）调用：先取消折线/选中，返回是否已消费这次 Esc。</summary>
    public bool CancelSelectionOrPending()
    {
        if (_polylineDraft is not null)
        {
            CancelPolylineCore();
            return true;
        }

        if (_selected is not null)
        {
            ClearSelectionCore();
            return true;
        }

        return false;
    }

    /// <summary>结束当前折线（供上层 Enter 处理调用），返回是否确实结束了折线。</summary>
    public bool FinishPolyline()
    {
        if (_polylineDraft is null)
        {
            return false;
        }

        FinishPolylineCore();
        return true;
    }

    /// <summary>结束并提交当前折线。</summary>
    private void FinishPolylineCore()
    {
        var draft = _polylineDraft;
        _polylineDraft = null;
        _draft = null;

        if (draft is not null && draft.Points.Count >= 2)
        {
            Commit(draft);
        }

        InvalidateVisual();
    }

    private void CancelPolylineCore()
    {
        _polylineDraft = null;
        _draft = null;
        InvalidateVisual();
    }

    private void AddPolylinePoint(Point imagePoint)
    {
        if (_polylineDraft is null)
        {
            return;
        }

        var last = _polylineDraft.Points[^1];
        if ((imagePoint - last).Length >= 2)
        {
            _polylineDraft.Points.Add(imagePoint);
        }

        _polylineCursor = imagePoint;
        _draft = _polylineDraft;
        InvalidateVisual();
    }

    // ---------------- 画草稿 ----------------

    private static Rect NormalizeRect(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private void UpdateDraft(Point current)
    {
        _draft = Tool switch
        {
            EditorTool.Rectangle => new RectangleAnnotation(DrawingStyle, _startImage, current),
            EditorTool.Ellipse => new EllipseAnnotation(DrawingStyle, _startImage, current),
            EditorTool.Line => new LineAnnotation(DrawingStyle, _startImage, current),
            EditorTool.Arrow => new ArrowAnnotation(DrawingStyle, _startImage, current),
            EditorTool.Mosaic => new MosaicAnnotation(DrawingStyle, NormalizeRect(_startImage, current)),
            EditorTool.Spotlight => new SpotlightAnnotation(SpotlightStyle(),
                NormalizeRect(_startImage, current), SpotlightEllipse),
            EditorTool.Magnifier => new MagnifierAnnotation(DrawingStyle,
                NormalizeRect(_startImage, current), MagnifierZoom),
            _ => null,
        };

        InvalidateVisual();
    }

    private AnnotationStyle SpotlightStyle()
    {
        var style = DrawingStyle.Clone();
        if (style.Opacity >= 0.99)
        {
            style.Opacity = 0.6;
        }

        return style;
    }

    // ---------------- 选中 / 移动 / 缩放 / 删除 ----------------

    private void BeginMove(IAnnotation annotation, Point imagePoint)
    {
        _selected = annotation;
        _selectedIndex = _annotations.IndexOf(annotation);
        _movingSelected = true;
        _moveLastPoint = imagePoint;
        _pendingSnapshot = Snapshot();
        CaptureMouse();
        InvalidateVisual();
    }

    private void DeleteSelected()
    {
        if (_selected is null)
        {
            return;
        }

        PushUndo();
        _annotations.Remove(_selected);
        _selected = null;
        _selectedIndex = -1;
        InvalidateVisual();
        Changed?.Invoke();
    }

    /// <summary>取消选中（供上层调用），返回是否原本存在选中。</summary>
    public bool ClearSelection()
    {
        if (_selected is null)
        {
            return false;
        }

        ClearSelectionCore();
        return true;
    }

    private void ClearSelectionCore()
    {
        if (_selected is null)
        {
            return;
        }

        _selected = null;
        _selectedIndex = -1;
        _movingSelected = false;
        _resizeHandle = -1;
        InvalidateVisual();
    }

    private bool TryHitHandle(Point imagePoint, out int handle)
    {
        handle = -1;
        if (_selected is null)
        {
            return false;
        }

        var bounds = _selected.Bounds;
        if (bounds.IsEmpty)
        {
            return false;
        }

        var points = GetHandlePoints(bounds);
        for (var i = 0; i < points.Length; i++)
        {
            if (Math.Abs(imagePoint.X - points[i].X) <= HandleHitTolerance &&
                Math.Abs(imagePoint.Y - points[i].Y) <= HandleHitTolerance)
            {
                handle = i;
                return true;
            }
        }

        return false;
    }

    private void BeginResize(int handle)
    {
        if (_selected is null)
        {
            return;
        }

        _resizeHandle = handle;
        _resizeStartBounds = _selected.Bounds;
        _resizeOriginal = _selected.Clone();
        _resizeSnapshot = Snapshot();
        _resizeUndoPushed = false;
        CaptureMouse();
    }

    private void UpdateResize(Point imagePoint)
    {
        if (_selected is null || _resizeOriginal is null || _resizeStartBounds.IsEmpty)
        {
            return;
        }

        var original = _resizeStartBounds;
        var left = original.Left;
        var top = original.Top;
        var right = original.Right;
        var bottom = original.Bottom;

        switch (_resizeHandle)
        {
            case 0: // TopLeft
                left = Math.Min(imagePoint.X, right - MinResizeSize);
                top = Math.Min(imagePoint.Y, bottom - MinResizeSize);
                break;
            case 1: // Top
                top = Math.Min(imagePoint.Y, bottom - MinResizeSize);
                break;
            case 2: // TopRight
                right = Math.Max(imagePoint.X, left + MinResizeSize);
                top = Math.Min(imagePoint.Y, bottom - MinResizeSize);
                break;
            case 3: // Right
                right = Math.Max(imagePoint.X, left + MinResizeSize);
                break;
            case 4: // BottomRight
                right = Math.Max(imagePoint.X, left + MinResizeSize);
                bottom = Math.Max(imagePoint.Y, top + MinResizeSize);
                break;
            case 5: // Bottom
                bottom = Math.Max(imagePoint.Y, top + MinResizeSize);
                break;
            case 6: // BottomLeft
                left = Math.Min(imagePoint.X, right - MinResizeSize);
                bottom = Math.Max(imagePoint.Y, top + MinResizeSize);
                break;
            case 7: // Left
                left = Math.Min(imagePoint.X, right - MinResizeSize);
                break;
            default:
                return;
        }

        var target = new Rect(left, top, Math.Max(MinResizeSize, right - left),
            Math.Max(MinResizeSize, bottom - top));

        var sx = original.Width > 0.5 ? target.Width / original.Width : 1;
        var sy = original.Height > 0.5 ? target.Height / original.Height : 1;

        var anchor = AnchorForHandle(original, _resizeHandle);
        var scaled = _resizeOriginal.Clone();
        scaled.Scale(sx, sy, anchor);

        if (_selectedIndex >= 0 && _selectedIndex < _annotations.Count)
        {
            _annotations[_selectedIndex] = scaled;
        }

        _selected = scaled;

        if (!_resizeUndoPushed)
        {
            _resizeUndoPushed = true;
            PushSnapshot(_resizeSnapshot);
        }

        InvalidateVisual();
    }

    private static Point AnchorForHandle(Rect bounds, int handle) => handle switch
    {
        0 => bounds.BottomRight,
        1 => new Point((bounds.Left + bounds.Right) / 2, bounds.Bottom),
        2 => bounds.BottomLeft,
        3 => new Point(bounds.Left, (bounds.Top + bounds.Bottom) / 2),
        4 => bounds.TopLeft,
        5 => new Point((bounds.Left + bounds.Right) / 2, bounds.Top),
        6 => bounds.TopRight,
        _ => new Point(bounds.Right, (bounds.Top + bounds.Bottom) / 2),
    };

    // ---------------- 命中测试 ----------------

    private IAnnotation? HitTestAnnotation(Point imagePoint)
    {
        var tolerance = Math.Max(4, DrawingStyle.Thickness * 2);

        for (var index = _annotations.Count - 1; index >= 0; index--)
        {
            if (_annotations[index].HitTest(imagePoint, tolerance))
            {
                return _annotations[index];
            }
        }

        return null;
    }

    private void EraseAt(Point imagePoint)
    {
        var tolerance = Math.Max(4, DrawingStyle.Thickness * 2);

        for (var index = _annotations.Count - 1; index >= 0; index--)
        {
            if (!_annotations[index].HitTest(imagePoint, tolerance))
            {
                continue;
            }

            EnsurePendingUndo();
            var removed = _annotations[index];
            _annotations.RemoveAt(index);
            if (ReferenceEquals(_selected, removed))
            {
                _selected = null;
                _selectedIndex = -1;
            }

            InvalidateVisual();
            Changed?.Invoke();
            return;
        }
    }

    // ---------------- 样式同步（工具条 → 选中标注） ----------------

    private void OnDrawingStyleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_selected is null || e.PropertyName is null)
        {
            return;
        }

        if (e.PropertyName is not (nameof(AnnotationStyle.Color) or
            nameof(AnnotationStyle.Thickness) or
            nameof(AnnotationStyle.FontSize) or
            nameof(AnnotationStyle.Opacity) or
            nameof(AnnotationStyle.TextBackground)))
        {
            return;
        }

        PushUndoCoalesced();
        ApplyStyle(_selected, DrawingStyle);
        InvalidateVisual();
        Changed?.Invoke();
    }

    private void ApplyStyle(IAnnotation annotation, AnnotationStyle style)
    {
        annotation.Style.Color = style.Color;
        annotation.Style.Thickness = style.Thickness;
        annotation.Style.FontSize = style.FontSize;
        annotation.Style.Opacity = style.Opacity;
        annotation.Style.TextBackground = style.TextBackground;
    }

    private void ApplyToSelectedWatermark()
    {
        if (_selected is not WatermarkAnnotation watermark)
        {
            return;
        }

        PushUndoCoalesced();
        watermark.TextTemplate = WatermarkText;
        watermark.Angle = WatermarkAngle;
        watermark.Style.Opacity = WatermarkOpacity;
        InvalidateVisual();
        Changed?.Invoke();
    }

    // ---------------- 新建 / 提交 ----------------

    private int NextNumber()
    {
        var max = 0;
        foreach (var annotation in _annotations)
        {
            if (annotation is NumberAnnotation number && number.Number > max)
            {
                max = number.Number;
            }
        }

        return max + 1;
    }

    /// <summary>在整图上添加一条水印（供工具点击或外部调用）。</summary>
    public void AddWatermark(string? text = null)
    {
        if (Background is null)
        {
            return;
        }

        var style = DrawingStyle.Clone();
        style.Opacity = WatermarkOpacity;

        Commit(new WatermarkAnnotation(style,
            string.IsNullOrWhiteSpace(text) ? WatermarkText : text,
            WatermarkAngle,
            new Rect(0, 0, Background.PixelWidth, Background.PixelHeight)));
    }

    private void Commit(IAnnotation annotation)
    {
        PushUndo();
        _annotations.Add(annotation);
        InvalidateVisual();
        Changed?.Invoke();
    }

    /// <summary>在指定图像坐标处添加文字标注。</summary>
    public void AddText(Point imagePoint, string text)
    {
        Commit(new TextAnnotation(DrawingStyle, imagePoint, text));
    }

    /// <summary>修改文字标注内容（双击文字后调用）。</summary>
    public void UpdateText(TextAnnotation annotation, string text)
    {
        PushUndo();
        annotation.Text = text;
        InvalidateVisual();
        Changed?.Invoke();
    }

    /// <summary>整体平移所有标注（选区被调整时保持与画面的相对位置）。</summary>
    public void TranslateAll(double dx, double dy)
    {
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
        {
            return;
        }

        foreach (var annotation in _annotations)
        {
            annotation.Translate(dx, dy);
        }

        InvalidateVisual();
    }

    // ---------------- 撤销 / 重做 ----------------

    private List<IAnnotation> Snapshot()
        => _annotations.Select(annotation => annotation.Clone()).ToList();

    private void EnsurePendingUndo()
    {
        if (_pendingSnapshot is null)
        {
            return;
        }

        PushSnapshot(_pendingSnapshot);
        _pendingSnapshot = null;
    }

    private void PushUndo()
    {
        _lastStyleUndoAt = 0;
        PushSnapshot(Snapshot());
    }

    private void PushUndoCoalesced()
    {
        var now = Environment.TickCount64;
        if (now - _lastStyleUndoAt < StyleUndoCoalesceMs)
        {
            return;
        }

        _lastStyleUndoAt = now;
        PushSnapshot(Snapshot());
    }

    private void PushSnapshot(List<IAnnotation>? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        _undoStack.Add(snapshot);
        if (_undoStack.Count > MaxUndoDepth)
        {
            _undoStack.RemoveAt(0);
        }

        _redoStack.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        _redoStack.Add(Snapshot());
        var previous = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _annotations.Clear();
        _annotations.AddRange(previous);
        ClearSelectionCore();
        InvalidateVisual();
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        _undoStack.Add(Snapshot());
        var next = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _annotations.Clear();
        _annotations.AddRange(next);
        ClearSelectionCore();
        InvalidateVisual();
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_annotations.Count == 0)
        {
            return;
        }

        PushUndo();
        _annotations.Clear();
        ClearSelectionCore();
        InvalidateVisual();
        Changed?.Invoke();
    }

    // ---------------- 导出 ----------------

    /// <summary>把背景与全部标注按 1:1 像素合成为新位图。</summary>
    public BitmapSource Render(BitmapSource background)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(background, new Rect(0, 0, background.PixelWidth, background.PixelHeight));

            foreach (var annotation in _annotations)
            {
                annotation.Draw(dc, background);
            }
        }

        var target = new RenderTargetBitmap(background.PixelWidth, background.PixelHeight,
            96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
