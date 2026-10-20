using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PinShot.App.Editor;

namespace PinShot.App.Pin;

/// <summary>
/// 贴图窗口：无边框置顶，支持拖动/缩放/透明度/锁定/置顶/标题/缩略图/标注/图像处理/分组/历史。
/// 生命周期与历史由 <see cref="PinManager"/> 统一管理；构造函数仍可直接 new（会自动登记）。
/// </summary>
public partial class PinWindow : Window
{
    private enum ResizeZone
    {
        None,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    private enum ImageOp
    {
        RotateRight,
        RotateLeft,
        FlipHorizontal,
        FlipVertical,
    }

    private const double EdgeSize = 8;
    private const double MinSize = 40;
    private const double MinScale = 0.1;
    private const double MaxScale = 8.0;
    private const double OpacityStep = 0.1;
    private const double BrightnessStep = 0.1;

    private static readonly Brush TopmostBorderBrush = Frozen(Color.FromRgb(0x2D, 0x8C, 0xFF));
    private static readonly Brush NormalBorderBrush = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LockedBorderBrush = Frozen(Color.FromRgb(0xFF, 0x8A, 0x00));
    private static readonly Brush ThroughBorderBrush = Frozen(Color.FromRgb(0x2F, 0xBF, 0x71));

    private readonly PinContent _content;
    private readonly BitmapSource _source;
    private readonly List<ImageOp> _imageOps = new();
    private readonly List<string> _tempFiles = new();
    private readonly DispatcherTimer _badgeTimer;

    private BitmapSource _displayBitmap = null!;
    private BitmapSource _viewBitmap = null!;

    private double _dpiScale = 1.0;
    private double _scale = 1.0;
    private double _imageOpacity = 1.0;
    private double _brightness = 1.0;
    private bool _grayscale;
    private bool _inverted;
    private bool _locked;
    private bool _shadow = true;
    private bool _annotationMode;
    private bool _thumbnailMode;
    private bool _throughMouse;
    private bool _active;
    private bool _destroying;
    private bool _closeHandled;
    private bool _middleReset;
    private double _savedScale = 1.0;
    private double _savedOpacity = 1.0;

    private bool _hasPendingPosition;
    private double _pendingLeft;
    private double _pendingTop;

    private string _title = string.Empty;
    private string _groupName = PinGroup.DefaultName;

    private Rect _thumbnailCrop;
    private bool _thumbnailPanning;
    private Point _panStartScreen;
    private Rect _panStartCrop;

    private ResizeZone _resizeZone = ResizeZone.None;
    private Point _resizeStartScreen;
    private Rect _resizeStartRect;

    private AnnotationLayer? _annotationLayer;
    private PinAnnotationBar? _annotationBar;
    private PinThroughBar? _throughBar;

    private MenuItem _copyMenu = null!;
    private MenuItem _copyOriginalMenu = null!;
    private MenuItem _copyTextMenu = null!;
    private MenuItem _copyColorMenu = null!;
    private MenuItem _copyFilesMenu = null!;
    private MenuItem _openFilesMenu = null!;
    private MenuItem _openFolderMenu = null!;
    private MenuItem _topmostMenu = null!;
    private MenuItem _lockMenu = null!;
    private MenuItem _shadowMenu = null!;
    private MenuItem _annotationMenu = null!;
    private MenuItem _thumbnailMenu = null!;
    private MenuItem _throughMenu = null!;
    private MenuItem _imageMenu = null!;
    private MenuItem _groupMenu = null!;
    private MenuItem _showAllMenu = null!;

    /// <summary>直接以图像创建贴图（保持旧入口可用）。登记由构造函数内的 PinManager.Register 完成。</summary>
    public PinWindow(BitmapSource image)
        : this(new PinContent { Kind = PinContentKind.Image, Image = image })
    {
    }

    internal PinWindow(PinContent content)
        : this(content, null)
    {
    }

    internal PinWindow(PinContent content, PinWindowState? state)
    {
        InitializeComponent();

        _content = content;
        _source = content.Image ?? PinContentRenderer.RenderText(content.Text ?? string.Empty);
        _title = state?.Title ?? content.Title ?? string.Empty;
        _groupName = state?.Group ?? PinManager.ActiveGroupName ?? PinGroup.DefaultName;
        _shadow = PinManager.ShowShadow;
        _dpiScale = DetectDpiScale();

        if (state is not null && !double.IsNaN(state.Left) && !double.IsNaN(state.Top))
        {
            _hasPendingPosition = true;
            _pendingLeft = state.Left;
            _pendingTop = state.Top;
        }

        BuildContextMenu();
        WireEvents();

        _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _badgeTimer.Tick += (_, _) =>
        {
            _badgeTimer.Stop();
            BadgeHost.Visibility = Visibility.Collapsed;
        };

        RefreshView(keepCenter: false);

        if (state is { Width: > 0, Height: > 0 })
        {
            Width = state.Width;
            Height = state.Height;
            _scale = Math.Max(MinScale, Width * _dpiScale / Math.Max(1, _viewBitmap.PixelWidth));
        }

        UpdateTitle();
        UpdateFrameVisual();
        PinManager.Register(this);
    }

    // ---------------- 对外只读状态 ----------------

    public PinContentKind Kind => _content.Kind;

    /// <summary>当前显示用的位图（含图像处理结果，不含标注；缩略图模式下为裁剪区域）。</summary>
    public BitmapSource CurrentImage => _viewBitmap;

    /// <summary>原始位图（图像处理前、标注前）。</summary>
    public BitmapSource SourceImage => _source;

    public bool IsLocked => _locked;

    public double Scale => _scale;

    public double ImageOpacity => _imageOpacity;

    public string PinTitle => _title;

    public string GroupName => _groupName;

    internal bool IsDestroying => _destroying;

    // ---------------- 初始化 ----------------

    private void WireEvents()
    {
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseMove += OnMouseMove;
        MouseWheel += OnMouseWheel;
        MouseDown += OnMouseDown;
        MouseRightButtonDown += (_, _) =>
        {
            Activate();
            Focus();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
        Activated += (_, _) =>
        {
            _active = true;
            UpdateFrameVisual();
        };
        Deactivated += (_, _) =>
        {
            _active = false;
            UpdateFrameVisual();
        };
        LocationChanged += OnLocationChangedForAux;
        SizeChanged += OnLocationChangedForAux;
        ContextMenuOpening += (_, _) => RefreshMenuState();
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static DropShadowEffect CreateShadow() => new()
    {
        BlurRadius = 18,
        ShadowDepth = 3,
        Direction = 270,
        Opacity = 0.5,
    };

    private double DetectDpiScale()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (dpi.DpiScaleX > 0)
            {
                return dpi.DpiScaleX;
            }
        }
        catch
        {
            // 窗口未连接 PresentationSource 时使用 96 DPI 基准
        }

        return 1.0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _dpiScale = DetectDpiScale();
        ApplyWindowSize(keepCenter: false);

        if (_hasPendingPosition)
        {
            Left = _pendingLeft;
            Top = _pendingTop;
        }
        else
        {
            PlaceOnScreen();
        }

        ClampToScreen();
        UpdateFrameVisual();
        RepositionAuxWindows();
        Activate();
        Focus();
    }

    private void PlaceOnScreen()
    {
        var area = SystemParameters.WorkArea;
        var maxWidth = area.Width * 0.85;
        var maxHeight = area.Height * 0.85;

        if (Width > maxWidth || Height > maxHeight)
        {
            var shrink = Math.Min(maxWidth / Math.Max(1, Width), maxHeight / Math.Max(1, Height));
            _scale = Math.Max(MinScale, _scale * shrink);
            ApplyWindowSize(keepCenter: false);
        }

        Left = area.Left + (area.Width - Width) / 2.0;
        Top = area.Top + (area.Height - Height) / 2.0;
    }

    private void OnLocationChangedForAux(object? sender, EventArgs e) => RepositionAuxWindows();

    private void RepositionAuxWindows()
    {
        try
        {
            if (_annotationMode && _annotationBar is { IsVisible: true })
            {
                _annotationBar.PlaceNear(this);
            }

            if (_throughMouse && _throughBar is { IsVisible: true })
            {
                _throughBar.PlaceNear(this);
            }
        }
        catch
        {
            // 辅助窗口定位失败不影响主贴图
        }
    }

    // ---------------- 图像处理 / 视图 ----------------

    private bool CanProcessImage => _content.Kind == PinContentKind.Image;

    private void RefreshView(bool keepCenter)
    {
        _displayBitmap = BuildDisplayBitmap();
        _viewBitmap = _thumbnailMode && CanProcessImage ? CreateThumbnailBitmap() : _displayBitmap;
        PinImage.Source = _viewBitmap;

        if (_annotationLayer is not null)
        {
            _annotationLayer.Background = _viewBitmap;
            _annotationLayer.InvalidateVisual();
        }

        ApplyWindowSize(keepCenter && IsLoaded);
    }

    private void ApplyWindowSize(bool keepCenter)
    {
        var width = Math.Max(MinSize, _viewBitmap.PixelWidth / _dpiScale * _scale);
        var height = Math.Max(MinSize, _viewBitmap.PixelHeight / _dpiScale * _scale);

        if (keepCenter && !double.IsNaN(Left) && !double.IsNaN(Top))
        {
            var centerX = Left + Width / 2.0;
            var centerY = Top + Height / 2.0;
            Width = width;
            Height = height;
            Left = centerX - width / 2.0;
            Top = centerY - height / 2.0;
        }
        else
        {
            Width = width;
            Height = height;
        }
    }

    private BitmapSource BuildDisplayBitmap()
    {
        BitmapSource work = _source;
        foreach (var op in _imageOps)
        {
            work = ApplyTransform(work, op);
        }

        if (_grayscale || _inverted || Math.Abs(_brightness - 1.0) > 0.001 || _imageOpacity < 0.999)
        {
            work = ApplyPixelEffects(work);
        }

        return work;
    }

    private static BitmapSource ApplyTransform(BitmapSource source, ImageOp op)
    {
        Transform transform = op switch
        {
            ImageOp.RotateRight => new RotateTransform(90),
            ImageOp.RotateLeft => new RotateTransform(-90),
            ImageOp.FlipHorizontal => new ScaleTransform(-1, 1),
            ImageOp.FlipVertical => new ScaleTransform(1, -1),
            _ => Transform.Identity,
        };

        var transformed = new TransformedBitmap(source, transform);
        transformed.Freeze();
        return transformed;
    }

    private BitmapSource ApplyPixelEffects(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);

        var grayscale = _grayscale;
        var inverted = _inverted;
        var brightness = _brightness;
        var opacity = _imageOpacity;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            int b = pixels[i];
            int g = pixels[i + 1];
            int r = pixels[i + 2];
            var a = pixels[i + 3];

            if (inverted)
            {
                r = 255 - r;
                g = 255 - g;
                b = 255 - b;
            }

            if (grayscale)
            {
                var luminance = (int)Math.Clamp(0.299 * r + 0.587 * g + 0.114 * b, 0, 255);
                r = g = b = luminance;
            }

            if (Math.Abs(brightness - 1.0) > 0.001)
            {
                r = (int)Math.Clamp(r * brightness, 0, 255);
                g = (int)Math.Clamp(g * brightness, 0, 255);
                b = (int)Math.Clamp(b * brightness, 0, 255);
            }

            pixels[i + 3] = (byte)Math.Clamp(a * opacity, 0, 255);
            pixels[i] = (byte)b;
            pixels[i + 1] = (byte)g;
            pixels[i + 2] = (byte)r;
        }

        var result = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight,
            bgra.DpiX > 0 ? bgra.DpiX : 96, bgra.DpiY > 0 ? bgra.DpiY : 96,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private BitmapSource CreateThumbnailBitmap()
    {
        var width = Math.Max(1, _displayBitmap.PixelWidth);
        var height = Math.Max(1, _displayBitmap.PixelHeight);
        var cropX = Math.Clamp(_thumbnailCrop.X, 0, width - 1);
        var cropY = Math.Clamp(_thumbnailCrop.Y, 0, height - 1);
        var cropWidth = Math.Clamp(_thumbnailCrop.Width, 1, width - cropX);
        var cropHeight = Math.Clamp(_thumbnailCrop.Height, 1, height - cropY);

        var cropped = new CroppedBitmap(_displayBitmap, new Int32Rect(
            (int)Math.Round(cropX), (int)Math.Round(cropY),
            Math.Max(1, (int)Math.Round(cropWidth)), Math.Max(1, (int)Math.Round(cropHeight))));
        cropped.Freeze();
        return cropped;
    }

    // ---------------- 图像处理操作（右键菜单 / 数字键） ----------------

    public void RotateRight() => ApplyStructuralOp(ImageOp.RotateRight, "向右旋转 90°");

    public void RotateLeft() => ApplyStructuralOp(ImageOp.RotateLeft, "向左旋转 90°");

    public void FlipHorizontal() => ApplyStructuralOp(ImageOp.FlipHorizontal, "水平翻转");

    public void FlipVertical() => ApplyStructuralOp(ImageOp.FlipVertical, "垂直翻转");

    public void ToggleGrayscale()
    {
        if (!EnsureProcessable())
        {
            return;
        }

        ExitThumbnail();
        _grayscale = !_grayscale;
        RefreshView(keepCenter: true);
        ShowBadge(_grayscale ? "灰度化 开" : "灰度化 关");
    }

    public void ToggleInvert()
    {
        if (!EnsureProcessable())
        {
            return;
        }

        ExitThumbnail();
        _inverted = !_inverted;
        RefreshView(keepCenter: true);
        ShowBadge(_inverted ? "反色 开" : "反色 关");
    }

    public void AdjustBrightness(int direction)
    {
        if (!EnsureProcessable())
        {
            return;
        }

        ExitThumbnail();
        _brightness = Math.Clamp(Math.Round(_brightness + BrightnessStep * direction, 2), 0.2, 3.0);
        RefreshView(keepCenter: true);
        ShowBadge($"亮度 {_brightness:P0}");
    }

    public void AdjustImageOpacity(int direction)
    {
        if (_locked)
        {
            ShowBadge("已锁定");
            return;
        }

        _imageOpacity = Math.Clamp(Math.Round(_imageOpacity + OpacityStep * direction, 2), 0.1, 1.0);
        RefreshView(keepCenter: true);
        ShowBadge($"不透明度 {_imageOpacity:P0}");
    }

    public void ResetProcessing()
    {
        _imageOps.Clear();
        _grayscale = false;
        _inverted = false;
        _brightness = 1.0;
        _imageOpacity = 1.0;
        _thumbnailMode = false;
        ExitAnnotationMode();
        RefreshView(keepCenter: true);
        UpdateFrameVisual();
        ShowBadge("已重置所有处理");
    }

    private void ApplyStructuralOp(ImageOp op, string description)
    {
        if (!EnsureProcessable())
        {
            return;
        }

        ExitThumbnail();
        _imageOps.Add(op);
        _annotationLayer?.Clear();
        RefreshView(keepCenter: true);
        ShowBadge(description);
    }

    private bool EnsureProcessable()
    {
        if (_locked)
        {
            ShowBadge("已锁定");
            return false;
        }

        if (!CanProcessImage)
        {
            ShowBadge("此贴图不支持图像处理");
            return false;
        }

        return true;
    }

    // ---------------- 锁定 / 置顶 / 阴影 / 标题 ----------------

    public void SetLocked(bool locked)
    {
        _locked = locked;
        UpdateFrameVisual();
        ShowBadge(locked ? "已锁定（L 解锁）" : "已解锁");
    }

    private void ToggleLock() => SetLocked(!_locked);

    public void ToggleTopmost()
    {
        Topmost = !Topmost;
        UpdateFrameVisual();
        ShowBadge(Topmost ? "置顶开" : "置顶关");
    }

    private void ToggleShadow()
    {
        _shadow = !_shadow;
        Frame.Effect = _shadow ? CreateShadow() : null;
        PinManager.ShowShadow = _shadow;
        ShowBadge(_shadow ? "阴影开" : "阴影关");
    }

    public void SetPinTitle(string? title)
    {
        _title = title?.Trim() ?? string.Empty;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        if (string.IsNullOrWhiteSpace(_title))
        {
            TitleBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            TitleText.Text = _title;
            TitleBar.Visibility = Visibility.Visible;
        }

        Title = string.IsNullOrWhiteSpace(_title) ? "PinShot 贴图" : $"PinShot 贴图 - {_title}";
    }

    private void PromptTitle()
    {
        var dialog = new TextInputDialog(_title) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            SetPinTitle(dialog.Text);
        }

        Focus();
    }

    private void UpdateFrameVisual()
    {
        Frame.BorderBrush = _locked
            ? LockedBorderBrush
            : _throughMouse
                ? ThroughBorderBrush
                : _active || Topmost
                    ? TopmostBorderBrush
                    : NormalBorderBrush;

        Frame.BorderThickness = new Thickness(_locked ? 2 : 1);
    }

    // ---------------- 分组 / 可见性 ----------------

    internal void ApplyGroup(string groupName)
    {
        _groupName = string.IsNullOrWhiteSpace(groupName) ? PinGroup.DefaultName : groupName.Trim();
    }

    internal void SetGroupViewVisible(bool visible)
    {
        Visibility = visible ? Visibility.Visible : Visibility.Hidden;
    }

    internal void ShowPin() => Visibility = Visibility.Visible;

    internal void HidePin() => Visibility = Visibility.Hidden;

    internal void BringOnScreen()
    {
        ClampToScreen();
        Visibility = Visibility.Visible;
    }

    private void MoveToNewGroup()
    {
        var dialog = new TextInputDialog(string.Empty) { Owner = this };
        dialog.Title = "移动到新分组";
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Text))
        {
            return;
        }

        PinManager.MoveToGroup(this, dialog.Text!.Trim());
        ShowBadge($"已移动到分组：{_groupName}");
    }

    private void ClampToScreen()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }

        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
        const double Visible = 60;

        var minLeft = screenLeft - Width + Visible;
        var maxLeft = screenRight - Visible;
        var minTop = screenTop - Height + Visible;
        var maxTop = screenBottom - Visible;

        if (minLeft <= maxLeft)
        {
            Left = Math.Clamp(Left, minLeft, maxLeft);
        }

        if (minTop <= maxTop)
        {
            Top = Math.Clamp(Top, minTop, maxTop);
        }
    }

    // ---------------- 标注模式 ----------------

    private void SetupAnnotationLayer()
    {
        _annotationLayer = new AnnotationLayer
        {
            Background = _viewBitmap,
            Focusable = true,
        };
        _annotationLayer.TextRequested += OnAnnotationTextRequested;
        _annotationLayer.TextEditRequested += OnAnnotationTextEditRequested;
        _annotationLayer.Changed += () =>
            _annotationBar?.Toolbar.UpdateState(_annotationLayer.CanUndo, _annotationLayer.CanRedo);

        AnnotationHost.Children.Add(_annotationLayer);
    }

    private void OnAnnotationTextRequested(Point imagePoint)
    {
        var dialog = new TextInputDialog { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Text))
        {
            _annotationLayer?.AddText(imagePoint, dialog.Text!);
        }

        FocusAnnotationLayer();
    }

    private void OnAnnotationTextEditRequested(TextAnnotation annotation)
    {
        var dialog = new TextInputDialog(annotation.Text) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _annotationLayer?.UpdateText(annotation, dialog.Text ?? string.Empty);
        }

        FocusAnnotationLayer();
    }

    private void EnterAnnotationMode()
    {
        if (_throughMouse)
        {
            ShowBadge("鼠标穿透状态下不能标注");
            return;
        }

        if (_annotationLayer is null)
        {
            SetupAnnotationLayer();
        }

        _annotationMode = true;
        AnnotationHost.Visibility = Visibility.Visible;
        if (_annotationLayer is not null)
        {
            _annotationLayer.Background = _viewBitmap;
            _annotationLayer.InvalidateVisual();
        }

        if (_annotationBar is null)
        {
            _annotationBar = CreateAnnotationBar();
        }

        if (!_annotationBar.IsVisible)
        {
            _annotationBar.Show();
        }

        _annotationBar.PlaceNear(this);
        _annotationLayer?.Focus();
        UpdateFrameVisual();
        ShowBadge("标注模式：Space 退出");
    }

    private PinAnnotationBar CreateAnnotationBar()
    {
        var bar = new PinAnnotationBar();
        var toolbar = bar.Toolbar;

        toolbar.ToolSelected += tool =>
        {
            if (_annotationLayer is not null)
            {
                _annotationLayer.Tool = tool;
            }

            FocusAnnotationLayer();
        };
        toolbar.ColorSelected += color =>
        {
            if (_annotationLayer is not null)
            {
                _annotationLayer.DrawingStyle.Color = color;
            }

            FocusAnnotationLayer();
        };
        toolbar.ThicknessSelected += thickness =>
        {
            if (_annotationLayer is not null)
            {
                _annotationLayer.DrawingStyle.Thickness = thickness;
            }

            FocusAnnotationLayer();
        };
        toolbar.UndoRequested += () =>
        {
            _annotationLayer?.Undo();
            FocusAnnotationLayer();
        };
        toolbar.RedoRequested += () =>
        {
            _annotationLayer?.Redo();
            FocusAnnotationLayer();
        };
        toolbar.CopyRequested += () => SafeCopy();
        toolbar.SaveRequested += SafeSave;
        toolbar.PinRequested += ExitAnnotationMode;
        toolbar.CancelRequested += ExitAnnotationMode;

        bar.EscapeRequested += ExitAnnotationMode;
        bar.ToggleRequested += ExitAnnotationMode;
        return bar;
    }

    private void ExitAnnotationMode()
    {
        if (!_annotationMode)
        {
            return;
        }

        _annotationMode = false;
        AnnotationHost.Visibility = Visibility.Collapsed;
        _annotationBar?.Hide();
        Focus();
        UpdateFrameVisual();
    }

    private void FocusAnnotationLayer()
    {
        Activate();
        _annotationLayer?.Focus();
    }

    // ---------------- 缩略图模式 ----------------

    private void ToggleThumbnail()
    {
        if (!CanProcessImage)
        {
            ShowBadge("此贴图不支持缩略图");
            return;
        }

        if (_thumbnailMode)
        {
            ExitThumbnail();
            ShowBadge("缩略图模式关");
            return;
        }

        ExitAnnotationMode();
        var width = _displayBitmap.PixelWidth;
        var height = _displayBitmap.PixelHeight;
        var aspect = Width > 0 && Height > 0
            ? Width / Height
            : (double)width / Math.Max(1, height);

        var cropWidth = Math.Max(16, (int)Math.Round(width * 0.6));
        var cropHeight = Math.Max(16, (int)Math.Round(cropWidth / Math.Max(0.05, aspect)));
        if (cropHeight > height)
        {
            cropHeight = height;
            cropWidth = Math.Max(16, (int)Math.Round(cropHeight * aspect));
        }

        if (cropWidth > width)
        {
            cropWidth = width;
            cropHeight = Math.Max(16, (int)Math.Round(cropWidth / Math.Max(0.05, aspect)));
        }

        _thumbnailCrop = new Rect(
            (width - cropWidth) / 2.0,
            (height - cropHeight) / 2.0,
            Math.Max(1, cropWidth),
            Math.Max(1, cropHeight));

        _thumbnailMode = true;
        RefreshView(keepCenter: true);
        ShowBadge("缩略图模式：Shift+拖拽调整区域，R 退出");
    }

    private void ExitThumbnail()
    {
        if (!_thumbnailMode)
        {
            return;
        }

        _thumbnailMode = false;
        RefreshView(keepCenter: true);
    }

    private void PanThumbnail(double dxDips, double dyDips)
    {
        if (!_thumbnailMode)
        {
            return;
        }

        var pixelsPerDip = _viewBitmap.PixelWidth / Math.Max(1.0, ActualWidth);
        var dx = dxDips * pixelsPerDip;
        var dy = dyDips * pixelsPerDip;

        var maxX = Math.Max(0, _displayBitmap.PixelWidth - _thumbnailCrop.Width);
        var maxY = Math.Max(0, _displayBitmap.PixelHeight - _thumbnailCrop.Height);

        _thumbnailCrop = new Rect(
            Math.Clamp(_thumbnailCrop.X + dx, 0, maxX),
            Math.Clamp(_thumbnailCrop.Y + dy, 0, maxY),
            _thumbnailCrop.Width,
            _thumbnailCrop.Height);

        RefreshView(keepCenter: false);
    }

    // ---------------- 鼠标 ----------------

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Activate();
        Focus();

        if (_throughMouse)
        {
            return;
        }

        var position = e.GetPosition(this);

        if (e.ClickCount >= 2 && !_annotationMode && !_locked)
        {
            ClosePin();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            StartDragOut();
            e.Handled = true;
            return;
        }

        if (_locked)
        {
            e.Handled = true;
            return;
        }

        if (_thumbnailMode)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                _thumbnailPanning = true;
                _panStartScreen = PointToScreen(position);
                _panStartCrop = _thumbnailCrop;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            TryDragMove();
            e.Handled = true;
            return;
        }

        var zone = HitTestResizeZone(position);
        if (zone != ResizeZone.None)
        {
            _resizeZone = zone;
            _resizeStartScreen = PointToScreen(position);
            _resizeStartRect = new Rect(Left, Top, Width, Height);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        TryDragMove();
        e.Handled = true;
    }

    private void TryDragMove()
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 在非按下状态下调用 DragMove 会抛异常，忽略
        }

        RepositionAuxWindows();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        Activate();
        Focus();

        if (_throughMouse || _locked)
        {
            return;
        }

        if (!_middleReset)
        {
            _savedScale = _scale;
            _savedOpacity = _imageOpacity;
            _scale = 1.0;
            _imageOpacity = 1.0;
            _middleReset = true;
            RefreshView(keepCenter: true);
            ShowBadge("已重置为原始大小 / 不透明");
        }
        else
        {
            _scale = _savedScale;
            _imageOpacity = _savedOpacity;
            _middleReset = false;
            RefreshView(keepCenter: true);
            ShowBadge($"恢复缩放 {_scale:P0} / 不透明度 {_imageOpacity:P0}");
        }

        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_thumbnailPanning)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var current = PointToScreen(e.GetPosition(this));
            var dx = (current.X - _panStartScreen.X) / _dpiScale;
            var dy = (current.Y - _panStartScreen.Y) / _dpiScale;

            _thumbnailCrop = _panStartCrop;
            PanThumbnail(-dx, -dy);
            e.Handled = true;
            return;
        }

        if (_resizeZone == ResizeZone.None)
        {
            Cursor = _locked || _thumbnailMode
                ? Cursors.Arrow
                : CursorForZone(HitTestResizeZone(e.GetPosition(this)));
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var screen = PointToScreen(e.GetPosition(this));
        var moveX = screen.X - _resizeStartScreen.X;
        var moveY = screen.Y - _resizeStartScreen.Y;

        var x = _resizeStartRect.X;
        var y = _resizeStartRect.Y;
        var width = _resizeStartRect.Width;
        var height = _resizeStartRect.Height;

        switch (_resizeZone)
        {
            case ResizeZone.Left:
            case ResizeZone.TopLeft:
            case ResizeZone.BottomLeft:
                x += moveX;
                width -= moveX;
                break;
            case ResizeZone.Right:
            case ResizeZone.TopRight:
            case ResizeZone.BottomRight:
                width += moveX;
                break;
        }

        switch (_resizeZone)
        {
            case ResizeZone.Top:
            case ResizeZone.TopLeft:
            case ResizeZone.TopRight:
                y += moveY;
                height -= moveY;
                break;
            case ResizeZone.Bottom:
            case ResizeZone.BottomLeft:
            case ResizeZone.BottomRight:
                height += moveY;
                break;
        }

        if (width < MinSize)
        {
            if (_resizeZone is ResizeZone.Left or ResizeZone.TopLeft or ResizeZone.BottomLeft)
            {
                x -= MinSize - width;
            }

            width = MinSize;
        }

        if (height < MinSize)
        {
            if (_resizeZone is ResizeZone.Top or ResizeZone.TopLeft or ResizeZone.TopRight)
            {
                y -= MinSize - height;
            }

            height = MinSize;
        }

        Left = x;
        Top = y;
        Width = width;
        Height = height;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_thumbnailPanning)
        {
            _thumbnailPanning = false;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_resizeZone == ResizeZone.None)
        {
            return;
        }

        _resizeZone = ResizeZone.None;
        ReleaseMouseCapture();
        _scale = Math.Max(MinScale, Width * _dpiScale / Math.Max(1, _viewBitmap.PixelWidth));
        _middleReset = false;
        e.Handled = true;
    }

    private ResizeZone HitTestResizeZone(Point position)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return ResizeZone.None;
        }

        var left = position.X <= EdgeSize;
        var right = position.X >= width - EdgeSize;
        var top = position.Y <= EdgeSize;
        var bottom = position.Y >= height - EdgeSize;

        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => ResizeZone.TopLeft,
            (_, true, true, _) => ResizeZone.TopRight,
            (true, _, _, true) => ResizeZone.BottomLeft,
            (_, true, _, true) => ResizeZone.BottomRight,
            (true, _, _, _) => ResizeZone.Left,
            (_, true, _, _) => ResizeZone.Right,
            (_, _, true, _) => ResizeZone.Top,
            (_, _, _, true) => ResizeZone.Bottom,
            _ => ResizeZone.None,
        };
    }

    private static Cursor CursorForZone(ResizeZone zone) => zone switch
    {
        ResizeZone.Left or ResizeZone.Right => Cursors.SizeWE,
        ResizeZone.Top or ResizeZone.Bottom => Cursors.SizeNS,
        ResizeZone.TopLeft or ResizeZone.BottomRight => Cursors.SizeNWSE,
        ResizeZone.TopRight or ResizeZone.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_throughMouse)
        {
            return;
        }

        Activate();

        if (_locked)
        {
            ShowBadge("已锁定");
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            AdjustImageOpacity(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (_thumbnailMode && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            PanThumbnail(0, e.Delta > 0 ? -12 : 12);
            e.Handled = true;
            return;
        }

        var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        var target = Math.Clamp(_scale * factor, MinScale, MaxScale);
        if (Math.Abs(target - _scale) < 0.0001)
        {
            e.Handled = true;
            return;
        }

        _scale = target;
        _middleReset = false;
        RefreshView(keepCenter: true);
        ShowBadge($"缩放 {_scale:P0}");
        e.Handled = true;
    }

    // ---------------- 键盘 ----------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_throughMouse)
        {
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.Escape:
                if (_annotationMode)
                {
                    ExitAnnotationMode();
                }
                else
                {
                    ClosePin();
                }

                e.Handled = true;
                return;

            case Key.Space:
                if (_annotationMode)
                {
                    ExitAnnotationMode();
                }
                else
                {
                    EnterAnnotationMode();
                }

                e.Handled = true;
                return;

            case Key.F2:
                PromptTitle();
                e.Handled = true;
                return;

            case Key.OemTilde:
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    PinManager.CycleGroupView();
                    ShowBadge(PinManager.ActiveGroupName is null
                        ? "分组视角：全部"
                        : $"分组视角：{PinManager.ActiveGroupName}");
                    e.Handled = true;
                }

                return;
        }

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.W:
                    ClosePin();
                    break;
                case Key.D:
                    DestroyPin();
                    break;
                case Key.C:
                    SafeCopy();
                    break;
                case Key.S:
                    SafeSave();
                    break;
                case Key.T:
                    ToggleTopmost();
                    break;
                case Key.D3:
                    // 全局热键之外的兜底：窗口有焦点时 Ctrl+3 也能恢复
                    PinManager.RestoreLast();
                    break;
                default:
                    return;
            }

            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            if (_locked || _annotationMode)
            {
                return;
            }

            var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10.0 : 1.0;
            switch (e.Key)
            {
                case Key.Left:
                    Left -= step;
                    break;
                case Key.Right:
                    Left += step;
                    break;
                case Key.Up:
                    Top -= step;
                    break;
                case Key.Down:
                    Top += step;
                    break;
            }

            e.Handled = true;
            return;
        }

        if (_annotationMode)
        {
            // 标注时把其余快捷键让给 AnnotationLayer
            return;
        }

        switch (e.Key)
        {
            case Key.L:
                ToggleLock();
                break;
            case Key.T:
                ToggleTopmost();
                break;
            case Key.R:
                ToggleThumbnail();
                break;
            case Key.D1:
                RotateRight();
                break;
            case Key.D2:
                RotateLeft();
                break;
            case Key.D3:
                FlipHorizontal();
                break;
            case Key.D4:
                FlipVertical();
                break;
            case Key.D5:
                ToggleGrayscale();
                break;
            case Key.D6:
                ToggleInvert();
                break;
            case Key.D7:
                AdjustBrightness(1);
                break;
            case Key.D8:
                AdjustBrightness(-1);
                break;
            case Key.D9:
                AdjustImageOpacity(1);
                break;
            case Key.OemMinus:
                AdjustImageOpacity(-1);
                break;
            case Key.D0:
            case Key.NumPad0:
                ResetProcessing();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ---------------- 复制 / 保存 / 拖拽 ----------------

    public void SafeCopy() => SafeCopy(original: false);

    private void SafeCopy(bool original)
    {
        var bitmap = original ? _source : CreateSnapshot();
        if (!TryClipboard(() => Clipboard.SetImage(bitmap)))
        {
            ShowBadge("复制失败：剪贴板被占用");
            return;
        }

        ShowBadge(original ? "已复制原始图像" : "已复制图像");
    }

    private void CopyPlainText()
    {
        if (string.IsNullOrEmpty(_content.Text))
        {
            return;
        }

        if (!TryClipboard(() => Clipboard.SetText(_content.Text)))
        {
            ShowBadge("复制失败：剪贴板被占用");
            return;
        }

        ShowBadge("已复制纯文本");
    }

    private void CopyColorValue()
    {
        if (string.IsNullOrEmpty(_content.ColorHex))
        {
            return;
        }

        if (!TryClipboard(() => Clipboard.SetText(_content.ColorHex)))
        {
            ShowBadge("复制失败：剪贴板被占用");
            return;
        }

        ShowBadge($"已复制颜色 {_content.ColorHex}");
    }

    private void CopyFilePaths()
    {
        if (_content.Files.Count == 0)
        {
            return;
        }

        var collection = new StringCollection();
        foreach (var file in _content.Files)
        {
            collection.Add(file);
        }

        if (!TryClipboard(() => Clipboard.SetFileDropList(collection)))
        {
            ShowBadge("复制失败：剪贴板被占用");
            return;
        }

        ShowBadge("已复制文件路径");
    }

    private void OpenFiles()
    {
        foreach (var path in _content.Files)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
                    {
                        UseShellExecute = true,
                    });
                }
                else if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }
            catch
            {
                ShowBadge("打开失败");
            }
        }
    }

    private void OpenContainingFolder()
    {
        var first = _content.Files.FirstOrDefault();
        if (string.IsNullOrEmpty(first))
        {
            return;
        }

        try
        {
            if (Directory.Exists(first))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{first}\"")
                {
                    UseShellExecute = true,
                });
            }
            else if (File.Exists(first))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"")
                {
                    UseShellExecute = true,
                });
            }
        }
        catch
        {
            ShowBadge("打开所在文件夹失败");
        }
    }

    private static bool TryClipboard(Action action)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception)
            {
                Thread.Sleep(60);
            }
        }

        return false;
    }

    private void SafeSave()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG 图片|*.png",
                FileName = $"贴图_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(CreateSnapshot()));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            ShowBadge("已保存");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "PinShot", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>合成当前显示内容 + 标注（用于复制/保存/历史/拖拽）。</summary>
    public BitmapSource CreateSnapshot()
    {
        try
        {
            if (_annotationLayer is { HasAnnotations: true })
            {
                return _annotationLayer.Render(_viewBitmap);
            }
        }
        catch
        {
            // 合成失败就退回纯图
        }

        return _viewBitmap;
    }

    private void StartDragOut()
    {
        try
        {
            var data = BuildDragData();
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
            ShowBadge("已拖拽到其他应用");
        }
        catch (Exception ex)
        {
            ShowBadge($"拖拽失败：{ex.Message}");
        }
    }

    /// <summary>构造 Ctrl+拖拽 的数据（位图 + FileDrop + PNG 流），便于自检与复用。</summary>
    public DataObject BuildDragData()
    {
        var snapshot = CreateSnapshot();
        var data = new DataObject();
        data.SetImage(snapshot);

        var files = new StringCollection();
        if (_content.Kind == PinContentKind.Files)
        {
            foreach (var path in _content.Files.Where(File.Exists))
            {
                files.Add(path);
            }
        }

        if (files.Count == 0)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"PinShot_{Guid.NewGuid():N}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(snapshot));
            using (var stream = File.Create(tempPath))
            {
                encoder.Save(stream);
            }

            _tempFiles.Add(tempPath);
            files.Add(tempPath);
        }

        data.SetFileDropList(files);

        using var pngStream = new MemoryStream();
        var pngEncoder = new PngBitmapEncoder();
        pngEncoder.Frames.Add(BitmapFrame.Create(snapshot));
        pngEncoder.Save(pngStream);
        pngStream.Position = 0;
        data.SetData("PNG", pngStream.ToArray(), true);

        return data;
    }

    // ---------------- 鼠标穿透 ----------------

    private void ToggleMouseThrough()
    {
        _throughMouse = !_throughMouse;
        PinNative.EnableClickThrough(this, _throughMouse);

        if (_throughMouse)
        {
            EnsureThroughBar();
            _throughBar?.Show();
            _throughBar?.PlaceNear(this);
            ShowBadge("鼠标穿透：点击浮条退出");
        }
        else
        {
            _throughBar?.Hide();
            ShowBadge("已恢复鼠标交互");
        }

        UpdateFrameVisual();
    }

    private void EnsureThroughBar()
    {
        if (_throughBar is not null)
        {
            return;
        }

        _throughBar = new PinThroughBar("鼠标穿透中");
        _throughBar.ExitRequested += OnExitThroughRequested;
    }

    private void OnExitThroughRequested()
    {
        if (_throughMouse)
        {
            ToggleMouseThrough();
        }
    }

    // ---------------- 保存/恢复历史状态 ----------------

    internal PinHistoryEntry CreateHistoryEntry() => new()
    {
        Kind = _content.Kind,
        Title = string.IsNullOrWhiteSpace(_title) ? null : _title,
        Text = _content.Text,
        ColorHex = _content.ColorHex,
        Files = _content.Files.Count > 0 ? _content.Files.ToList() : null,
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,
        Image = CreateSnapshot(),
    };

    // ---------------- 关闭 / 销毁 ----------------

    public void ClosePin() => Close();

    public void DestroyPin()
    {
        _destroying = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeHandled)
        {
            _closeHandled = true;

            if (_annotationMode)
            {
                ExitAnnotationMode();
            }

            _annotationBar?.Close();
            _annotationBar = null;

            if (_throughBar is not null)
            {
                _throughBar.ExitRequested -= OnExitThroughRequested;
                _throughBar.Close();
                _throughBar = null;
            }

            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }

            foreach (var temp in _tempFiles)
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch
                {
                    // 临时文件残留不影响使用
                }
            }

            _tempFiles.Clear();

            if (!_destroying)
            {
                PinManager.OnWindowClosing(this);
            }
        }

        base.OnClosing(e);
    }

    // ---------------- 提示 ----------------

    private void ShowBadge(string message)
    {
        BadgeText.Text = message;
        BadgeHost.Visibility = Visibility.Visible;
        _badgeTimer?.Stop();
        _badgeTimer?.Start();
    }

    // ---------------- 右键菜单 ----------------

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        _copyMenu = MakeItem("复制当前图像", "Ctrl+C", () => SafeCopy());
        menu.Items.Add(_copyMenu);

        _copyOriginalMenu = MakeItem("复制原始图像", null, () => SafeCopy(original: true));
        menu.Items.Add(_copyOriginalMenu);

        _copyTextMenu = MakeItem("复制纯文本", null, CopyPlainText);
        menu.Items.Add(_copyTextMenu);

        _copyColorMenu = MakeItem("复制颜色值", null, CopyColorValue);
        menu.Items.Add(_copyColorMenu);

        _copyFilesMenu = MakeItem("复制文件路径", null, CopyFilePaths);
        menu.Items.Add(_copyFilesMenu);

        _openFilesMenu = MakeItem("打开文件", null, OpenFiles);
        menu.Items.Add(_openFilesMenu);

        _openFolderMenu = MakeItem("打开所在文件夹", null, OpenContainingFolder);
        menu.Items.Add(_openFolderMenu);

        menu.Items.Add(MakeItem("另存为...", "Ctrl+S", SafeSave));
        menu.Items.Add(new Separator());

        _topmostMenu = MakeItem("置顶", "T", ToggleTopmost, isCheckable: true);
        menu.Items.Add(_topmostMenu);

        _lockMenu = MakeItem("锁定", "L", ToggleLock, isCheckable: true);
        menu.Items.Add(_lockMenu);

        _shadowMenu = MakeItem("阴影", null, ToggleShadow, isCheckable: true);
        menu.Items.Add(_shadowMenu);

        _annotationMenu = MakeItem("标注", "Space", () =>
        {
            if (_annotationMode)
            {
                ExitAnnotationMode();
            }
            else
            {
                EnterAnnotationMode();
            }
        }, isCheckable: true);
        menu.Items.Add(_annotationMenu);

        _thumbnailMenu = MakeItem("缩略图", "R", ToggleThumbnail, isCheckable: true);
        menu.Items.Add(_thumbnailMenu);

        menu.Items.Add(MakeItem("设置标题...", "F2", PromptTitle));
        menu.Items.Add(new Separator());

        menu.Items.Add(MakeItem("隐藏其他贴图", null, () =>
        {
            PinManager.HideOthers(this);
            ShowBadge("已隐藏其他贴图");
        }));

        _showAllMenu = MakeItem("显示所有贴图", null, () =>
        {
            PinManager.ShowAllPins();
            ShowBadge("已显示所有贴图");
        });
        menu.Items.Add(_showAllMenu);

        menu.Items.Add(MakeItem("移动到新分组...", null, MoveToNewGroup));

        _groupMenu = new MenuItem { Header = "切换分组" };
        menu.Items.Add(_groupMenu);

        _throughMenu = MakeItem("鼠标穿透", null, ToggleMouseThrough, isCheckable: true);
        menu.Items.Add(_throughMenu);

        menu.Items.Add(new Separator());

        _imageMenu = new MenuItem { Header = "图像处理" };
        _imageMenu.Items.Add(MakeItem("向右旋转 90°", "1", RotateRight));
        _imageMenu.Items.Add(MakeItem("向左旋转 90°", "2", RotateLeft));
        _imageMenu.Items.Add(MakeItem("水平翻转", "3", FlipHorizontal));
        _imageMenu.Items.Add(MakeItem("垂直翻转", "4", FlipVertical));
        _imageMenu.Items.Add(new Separator());
        _imageMenu.Items.Add(MakeItem("灰度化", "5", ToggleGrayscale));
        _imageMenu.Items.Add(MakeItem("反色", "6", ToggleInvert));
        _imageMenu.Items.Add(MakeItem("亮度 +10%", "7", () => AdjustBrightness(1)));
        _imageMenu.Items.Add(MakeItem("亮度 -10%", "8", () => AdjustBrightness(-1)));
        _imageMenu.Items.Add(MakeItem("不透明度 +10%", "Ctrl+滚轮上", () => AdjustImageOpacity(1)));
        _imageMenu.Items.Add(MakeItem("不透明度 -10%", "Ctrl+滚轮下", () => AdjustImageOpacity(-1)));
        _imageMenu.Items.Add(new Separator());
        _imageMenu.Items.Add(MakeItem("重置所有处理", "0", ResetProcessing));
        menu.Items.Add(_imageMenu);

        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("关闭", "Esc", ClosePin));
        menu.Items.Add(MakeItem("销毁（不进历史）", "Ctrl+D", DestroyPin));

        menu.Opened += (_, _) => RefreshMenuState();
        Frame.ContextMenu = menu;
    }

    private static MenuItem MakeItem(string header, string? gesture, Action action,
        bool isCheckable = false)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = isCheckable,
        };

        if (!string.IsNullOrEmpty(gesture))
        {
            item.InputGestureText = gesture;
        }

        item.Click += (_, _) => action();
        return item;
    }

    private void RefreshMenuState()
    {
        _topmostMenu.IsChecked = Topmost;
        _lockMenu.IsChecked = _locked;
        _shadowMenu.IsChecked = _shadow;
        _annotationMenu.IsChecked = _annotationMode;
        _thumbnailMenu.IsChecked = _thumbnailMode;
        _throughMenu.IsChecked = _throughMouse;
        _imageMenu.IsEnabled = CanProcessImage;
        _copyOriginalMenu.IsEnabled = CanProcessImage;

        _copyTextMenu.Visibility = _content.Text is null ? Visibility.Collapsed : Visibility.Visible;
        _copyColorMenu.Visibility = _content.ColorHex is null ? Visibility.Collapsed : Visibility.Visible;

        var hasFiles = _content.Files.Count > 0;
        _copyFilesMenu.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        _openFilesMenu.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        _openFolderMenu.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;

        _showAllMenu.Visibility = PinManager.Windows.Any(w => w != this && w.Visibility != Visibility.Visible)
            ? Visibility.Visible
            : Visibility.Collapsed;

        RebuildGroupMenu();
    }

    private void RebuildGroupMenu()
    {
        _groupMenu.Items.Clear();

        var all = MakeItem("显示全部", null, () => PinManager.SetGroupView(null), isCheckable: true);
        all.IsChecked = PinManager.ActiveGroupName is null;
        _groupMenu.Items.Add(all);
        _groupMenu.Items.Add(new Separator());

        foreach (var group in PinManager.Groups)
        {
            var name = group.Name;
            var item = new MenuItem
            {
                Header = CreateGroupHeader(group.Name, group.ToColor()),
                IsCheckable = true,
                IsChecked = string.Equals(_groupName, name, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) =>
            {
                PinManager.MoveToGroup(this, name);
                PinManager.SetGroupView(name);
                ShowBadge($"分组：{name}");
            };
            _groupMenu.Items.Add(item);
        }
    }

    private static object CreateGroupHeader(string name, Color color)
    {
        var dot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text = name,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { dot, label },
        };
    }

    // ---------------- 自检辅助 ----------------

    /// <summary>自检：跑一遍旋转/灰度/反色/亮度/不透明度/重置/快照，返回是否全部正常。</summary>
    internal (bool Ok, string Detail) RunSelfTestProcessing()
    {
        if (!CanProcessImage)
        {
            return (false, "非图像贴图");
        }

        var beforeWidth = _source.PixelWidth;
        var beforeHeight = _source.PixelHeight;

        RotateRight();
        var rotateOk = _viewBitmap.PixelWidth == beforeHeight && _viewBitmap.PixelHeight == beforeWidth;

        ToggleGrayscale();
        ToggleInvert();
        AdjustBrightness(1);
        AdjustImageOpacity(-1);
        var effectsOk = _grayscale && _inverted
            && _brightness > 1.0 && _imageOpacity < 1.0;

        ResetProcessing();
        var resetOk = !_grayscale && !_inverted
            && Math.Abs(_brightness - 1.0) < 0.001
            && Math.Abs(_imageOpacity - 1.0) < 0.001
            && _viewBitmap.PixelWidth == beforeWidth
            && _viewBitmap.PixelHeight == beforeHeight;

        var snapshot = CreateSnapshot();
        var snapshotOk = snapshot.PixelWidth == beforeWidth && snapshot.PixelHeight == beforeHeight;

        return (rotateOk && effectsOk && resetOk && snapshotOk,
            $"rotate={rotateOk} effects={effectsOk} reset={resetOk} snapshot={snapshotOk}");
    }
}

/// <summary>恢复贴图时沿用的窗口状态（位置/大小/标题/分组）。</summary>
internal sealed class PinWindowState
{
    public double Left { get; init; } = double.NaN;

    public double Top { get; init; } = double.NaN;

    public double Width { get; init; }

    public double Height { get; init; }

    public string? Title { get; init; }

    public string? Group { get; init; }
}
