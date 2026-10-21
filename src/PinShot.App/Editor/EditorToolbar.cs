using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace PinShot.App.Editor;

/// <summary>浮动工具栏：工具选择、颜色、线宽、撤销/重做、完成动作。</summary>
/// <remarks>
/// 本轮新增：
/// <list type="bullet">
/// <item>折线 / 聚光灯 / 放大镜 / 水印 4 个工具按钮；</item>
/// <item>按当前工具显示的"上下文选项行"（字号、文字背景、聚光灯形状、放大镜倍率、水印文字/角度/透明度）；</item>
/// <item><c>Shift+1</c> ~ <c>Shift+0</c> 切换 10 个基础工具；</item>
/// <item>构造参数可隐藏「滚动」「录制」等动作按钮（长图编辑器用）。</item>
/// </list>
/// </remarks>
public sealed class EditorToolbar : Border
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xE8, 0x3A, 0x3A), // 红
        Color.FromRgb(0xF0, 0x8A, 0x24), // 橙
        Color.FromRgb(0xF2, 0xC2, 0x00), // 黄
        Color.FromRgb(0x2F, 0xA8, 0x4F), // 绿
        Color.FromRgb(0x2D, 0x8C, 0xFF), // 蓝
        Color.FromRgb(0x8B, 0x5C, 0xF6), // 紫
        Colors.White,
        Color.FromRgb(0x11, 0x11, 0x11), // 近黑
    };

    /// <summary>Shift+1~0 依次对应的 10 个基础工具。</summary>
    private static readonly EditorTool[] DigitTools =
    {
        EditorTool.Rectangle,
        EditorTool.Ellipse,
        EditorTool.Line,
        EditorTool.Arrow,
        EditorTool.Pen,
        EditorTool.Highlighter,
        EditorTool.Text,
        EditorTool.Number,
        EditorTool.Mosaic,
        EditorTool.Eraser,
    };

    private static readonly double[] FontSizeChoices = { 12, 16, 20, 28 };

    private static readonly double[] WatermarkAngles = { -45, -30, 0, 30, 45, 90 };

    private static readonly double[] WatermarkOpacities = { 0.1, 0.15, 0.22, 0.3, 0.5, 0.7 };

    private readonly Dictionary<EditorTool, Button> _toolButtons = new();
    private readonly Dictionary<Color, Button> _colorButtons = new();
    private readonly Dictionary<double, Button> _thicknessButtons = new();
    private Popup? _colorPopup;
    private Popup? _thicknessPopup;
    private Border? _colorDot;
    private Border? _thicknessDot;

    /// <summary>线宽可选项（二级面板）。</summary>
    private static readonly double[] ThicknessChoices = { 2, 4, 7, 12 };

    /// <summary>马赛克块大小可选项。</summary>
    private static readonly int[] MosaicChoices = { 6, 10, 16 };
    private readonly Dictionary<TextBackgroundKind, Button> _textBackgroundButtons = new();
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    private Popup? _optionsPopup;
    private readonly StackPanel _optionsRow;
    private readonly bool _showLongShotAction;
    private readonly bool _showRecordAction;
    private readonly bool _showOcrAction;

    private EditorTool _currentTool = EditorTool.Rectangle;
    private Window? _hostWindow;
    private double _watermarkAngle = -30;
    private double _watermarkOpacity = 0.22;

    /// <param name="showLongShotAction">是否显示「滚动」动作按钮（长截图入口）。</param>
    /// <param name="showRecordAction">是否显示「录制」动作按钮。</param>
    /// <param name="showOcrAction">是否显示「识别」动作按钮。</param>
    public EditorToolbar(bool showLongShotAction = true, bool showRecordAction = true,
        bool showOcrAction = true)
    {
        _showLongShotAction = showLongShotAction;
        _showRecordAction = showRecordAction;
        _showOcrAction = showOcrAction;

        Background = Brush("ToolbarBackgroundBrush", Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1F));
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(8, 5, 8, 5);
        Effect = new DropShadowEffect
        {
            BlurRadius = 20,
            ShadowDepth = 2,
            Direction = 270,
            Opacity = 0.45,
        };

        // 两组：编辑工具组 / 动作组。窗口不够宽时整组换行，避免单个按钮掉到下一行。
        var drawGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var actionGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        // 「选择 / 移动」：不绘制，拖选区内部可以整体挪动截图范围（对齐 PixPin）
        AddTool(drawGroup, EditorTool.None,
            MakeIcon("M5,2 L5,17 L9,13 L12,19 L15,17 L12,11 L17,11 Z"), "选择 / 移动截图范围");
        AddTool(drawGroup, EditorTool.Rectangle, MakeIcon("M3,4 H17 V16 H3 Z"), "矩形 (Shift+1)");
        AddTool(drawGroup, EditorTool.Ellipse, MakeIcon("M10,3 A7,7 0 1 0 10,17 A7,7 0 1 0 10,3"), "椭圆 (Shift+2)");
        AddTool(drawGroup, EditorTool.Line, MakeIcon("M3,17 L17,3"), "直线 (Shift+3)");
        AddTool(drawGroup, EditorTool.Polyline, MakeIcon("M3,16 L7,7 L11,12 L17,4"), "折线（逐点点击，双击/右键结束）");
        AddTool(drawGroup, EditorTool.Arrow, MakeIcon("M3,17 L16,4 M16,4 L16,10 M16,4 L10,4"), "箭头 (Shift+4)");
        AddTool(drawGroup, EditorTool.Pen, MakeIcon("M4,16 L7,13 L13,7 L16,10 L10,16 Z"), "画笔 (Shift+5)");
        AddTool(drawGroup, EditorTool.Highlighter, MakeIcon("M4,16 L14,6 L16,8 L6,18 Z", filled: true), "高亮 (Shift+6)");
        AddTool(drawGroup, EditorTool.Text, MakeIcon("M4,5 H16 M10,5 V17"), "文字 (Shift+7)");
        AddTool(drawGroup, EditorTool.Label,
            MakeIcon("M3,5 H17 V13 L11,17 H3 Z M6,9 H14"), "标签（色块+文字，飞书同款）");
        AddTool(drawGroup, EditorTool.Number, MakeNumberIcon(), "序号 (Shift+8)");
        AddTool(drawGroup, EditorTool.Mosaic,
            MakeIcon("M3,3 H17 V17 H3 Z M3,8.5 H17 M3,13.5 H17 M8.5,3 V17 M13.5,3 V17"),
            "马赛克（拖框打码，Shift+9）");
        AddTool(drawGroup, EditorTool.Eraser, MakeIcon("M4,15 L10,6 L16,10 L10,18 Z M9,17 H18"), "擦除 (Shift+0)");

        drawGroup.Children.Add(Divider());

        AddTool(drawGroup, EditorTool.Spotlight, MakeIcon("M10,2 A8,8 0 1 0 10,18 A8,8 0 1 0 10,2 M10,6 A4,4 0 1 1 10,14 A4,4 0 1 1 10,6"), "聚光灯（选区外压暗）");
        AddTool(drawGroup, EditorTool.Magnifier, MakeIcon("M8.5,3 A5.5,5.5 0 1 0 8.5,14 A5.5,5.5 0 1 0 8.5,3 M12.4,12.4 L17,17"), "放大镜（圆形放大）");
        AddTool(drawGroup, EditorTool.Watermark, MakeIcon("M3,15 L7,5 M8,15 L12,5 M13,15 L17,5"), "水印（整图平铺）");

        drawGroup.Children.Add(Divider());

        // 颜色/线宽不再固定在工具条上，改为选中工具后在下方显示该工具自己的参数（对齐飞书）

        drawGroup.Children.Add(Divider());

        _undoButton = MakeButton("↶", "撤销 (Ctrl+Z)", () => UndoRequested?.Invoke(), "ToolbarButton");
        _redoButton = MakeButton("↷", "重做 (Ctrl+Y)", () => RedoRequested?.Invoke(), "ToolbarButton");
        drawGroup.Children.Add(_undoButton);
        drawGroup.Children.Add(_redoButton);

        drawGroup.Children.Add(Divider());

        actionGroup.Children.Add(MakeButton("复制", "复制到剪贴板 (Enter)", () => CopyRequested?.Invoke(),
            "ToolbarPrimaryButton"));
        actionGroup.Children.Add(MakeButton("保存", "另存为 (Ctrl+S)", () => SaveRequested?.Invoke(),
            "ToolbarActionButton"));
        actionGroup.Children.Add(MakeButton("贴图", "贴到屏幕 (P)", () => PinRequested?.Invoke(),
            "ToolbarActionButton"));

        if (_showOcrAction)
        {
            actionGroup.Children.Add(MakeButton("识别", "识别选区文字 (O)", () => OcrRequested?.Invoke(),
                "ToolbarActionButton"));
        }

        if (_showLongShotAction)
        {
            actionGroup.Children.Add(MakeButton("滚动", "转为滚动长截图", () => LongShotRequested?.Invoke(),
                "ToolbarActionButton"));
        }

        if (_showRecordAction)
        {
            actionGroup.Children.Add(MakeButton(MakeRecordIcon(), "录制当前选区", () => RecordRequested?.Invoke(),
                "ToolbarActionButton"));
        }

        actionGroup.Children.Add(MakeButton("取消", "取消 (Esc)", () => CancelRequested?.Invoke(),
            "ToolbarActionButton"));

        _optionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        // 参数面板做成贴在「当前工具按钮」下方的气泡，而不是固定在工具条正下方
        _optionsPopup = new Popup
        {
            Child = new Border
            {
                Background = Brush("ToolbarBackgroundBrush", Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1F)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 6, 8, 6),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 3,
                    Direction = 270,
                    Opacity = 0.55,
                },
                Child = _optionsRow,
            },
            StaysOpen = false,
            AllowsTransparency = true,
            Placement = PlacementMode.Custom,
            VerticalOffset = 8,
            HorizontalOffset = 0,
        };

        // 气泡水平居中对准被点击的工具按钮
        _optionsPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            var x = -(popupSize.Width - targetSize.Width) / 2.0;
            var y = targetSize.Height + 8;
            return new[] { new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Horizontal) };
        };

        var mainRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { drawGroup, actionGroup },
        };

        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(mainRow);
        Child = root;

        SelectTool(EditorTool.None);
        SelectColor(Palette[0]);
        SelectThickness(4);
        UpdateState(false, false);

        Loaded += OnToolbarLoaded;
        Unloaded += OnToolbarUnloaded;
    }

    public event Action<EditorTool>? ToolSelected;

    public event Action<Color>? ColorSelected;

    public event Action<double>? ThicknessSelected;

    public event Action? UndoRequested;

    public event Action? RedoRequested;

    public event Action? CopyRequested;

    public event Action? SaveRequested;

    public event Action? PinRequested;

    /// <summary>识别选区文字。</summary>
    public event Action? OcrRequested;

    /// <summary>把当前选区转为滚动长截图。</summary>
    public event Action? LongShotRequested;

    /// <summary>按当前选区开始录制。</summary>
    public event Action? RecordRequested;

    public event Action? CancelRequested;

    public void UpdateState(bool canUndo, bool canRedo)
    {
        _undoButton.IsEnabled = canUndo;
        _redoButton.IsEnabled = canRedo;
    }

    // ---------------- 宿主窗口 / 键盘快捷键 ----------------

    private void OnToolbarLoaded(object sender, RoutedEventArgs e)
    {
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewKeyDown -= OnHostPreviewKeyDown;
            _hostWindow.PreviewKeyDown += OnHostPreviewKeyDown;
        }
    }

    private void OnToolbarUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewKeyDown -= OnHostPreviewKeyDown;
            _hostWindow = null;
        }
    }

    private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift)
        {
            return;
        }

        var index = e.Key switch
        {
            Key.D1 => 0,
            Key.D2 => 1,
            Key.D3 => 2,
            Key.D4 => 3,
            Key.D5 => 4,
            Key.D6 => 5,
            Key.D7 => 6,
            Key.D8 => 7,
            Key.D9 => 8,
            Key.D0 => 9,
            _ => -1,
        };

        if (index < 0 || index >= DigitTools.Length)
        {
            return;
        }

        var tool = DigitTools[index];
        SelectTool(tool);
        ToolSelected?.Invoke(tool);
        ResolveLayer()?.Focus();
        e.Handled = true;
    }

    // ---------------- 工具 ----------------

    private void AddTool(Panel panel, EditorTool tool, FrameworkElement icon, string tip)
    {
        var button = MakeButton(icon, tip, () =>
        {
            SelectTool(tool);
            ToolSelected?.Invoke(tool);
        }, "ToolbarButton");

        _toolButtons[tool] = button;
        panel.Children.Add(button);
    }

    /// <summary>颜色入口按钮：点开是调色板二级面板。</summary>
    private Button CreateColorPicker()
    {
        _colorDot = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(99),
            Background = new SolidColorBrush(Palette[0]),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Width = 4 * 30,
        };

        foreach (var color in Palette)
        {
            AddColor(wrap, color);
        }

        var panel = new Border
        {
            Background = Brush("ToolbarBackgroundBrush", Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1F)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 6, 8, 6),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.5,
            },
            Child = wrap,
        };

        _colorPopup = new Popup
        {
            Child = panel,
            StaysOpen = false,
            AllowsTransparency = true,
            Placement = PlacementMode.Top,
            VerticalOffset = -8,
            HorizontalOffset = -40,
        };

        var button = MakeButton(_colorDot, "颜色（点开选择）", () =>
        {
            if (_colorPopup is not null)
            {
                _colorPopup.IsOpen = !_colorPopup.IsOpen;
            }
        }, "ToolbarButton");

        _colorPopup.PlacementTarget = button;
        return button;
    }

    /// <summary>线宽入口按钮：点开是线宽二级面板。</summary>
    private Button CreateThicknessPicker()
    {
        _thicknessDot = new Border
        {
            Width = 4 + 4 * 1.4,
            Height = 4 + 4 * 1.4,
            CornerRadius = new CornerRadius(99),
            Background = Brush("ToolbarForegroundBrush", Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        foreach (var thickness in ThicknessChoices)
        {
            AddThickness(panel, thickness);
        }

        var border = new Border
        {
            Background = Brush("ToolbarBackgroundBrush", Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1F)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 6, 8, 6),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.5,
            },
            Child = panel,
        };

        _thicknessPopup = new Popup
        {
            Child = border,
            StaysOpen = false,
            AllowsTransparency = true,
            Placement = PlacementMode.Top,
            VerticalOffset = -8,
            HorizontalOffset = -20,
        };

        var button = MakeButton(_thicknessDot, "线宽（点开选择，滚轮可调）", () =>
        {
            if (_thicknessPopup is not null)
            {
                _thicknessPopup.IsOpen = !_thicknessPopup.IsOpen;
            }
        }, "ToolbarButton");

        _thicknessPopup.PlacementTarget = button;
        return button;
    }

    private void AddColor(Panel panel, Color color)
    {
        var button = new Button
        {
            Style = FindStyle("ColorSwatchButton"),
            Background = new SolidColorBrush(color),
            ToolTip = color.ToString(),
            Tag = null,
        };

        button.Click += (_, _) =>
        {
            SelectColor(color);
            ColorSelected?.Invoke(color);

            if (_colorPopup is not null)
            {
                _colorPopup.IsOpen = false;
            }
        };

        _colorButtons[color] = button;
        panel.Children.Add(button);
    }

    private void AddThickness(Panel panel, double thickness)
    {
        var dot = new Border
        {
            Width = 4 + thickness * 1.4,
            Height = 4 + thickness * 1.4,
            CornerRadius = new CornerRadius(99),
            Background = Brush("ToolbarForegroundBrush", Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var button = new Button
        {
            Content = dot,
            Style = FindStyle("ThicknessButton"),
            ToolTip = $"线宽 {thickness}（滚轮可调）",
        };

        button.Click += (_, _) =>
        {
            SelectThickness(thickness);
            ThicknessSelected?.Invoke(thickness);
            ResolveLayer()?.Focus();

            if (_thicknessPopup is not null)
            {
                _thicknessPopup.IsOpen = false;
            }
        };

        _thicknessButtons[thickness] = button;
        panel.Children.Add(button);
    }

    /// <summary>自检用：程序化切换工具，验证选项行内容。</summary>
    internal void SelectToolForTest(EditorTool tool) => SelectTool(tool);

    private void SelectTool(EditorTool tool)
    {
        _currentTool = tool;
        var active = Brush("ToolbarActiveBrush", Color.FromRgb(0x2D, 0x8C, 0xFF));

        foreach (var (key, button) in _toolButtons)
        {
            button.Background = key == tool ? active : Brushes.Transparent;
        }

        RebuildOptionsRow();
    }

    private void SelectColor(Color color)
    {
        if (_colorDot is not null)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _colorDot.Background = brush;
        }

        foreach (var (key, button) in _colorButtons)
        {
            button.Tag = key == color ? "selected" : null;
        }
    }

    private void SelectThickness(double thickness)
    {
        if (_thicknessDot is not null)
        {
            _thicknessDot.Width = 4 + thickness * 1.4;
            _thicknessDot.Height = 4 + thickness * 1.4;
        }

        var active = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        active.Freeze();

        foreach (var (key, button) in _thicknessButtons)
        {
            button.Background = Math.Abs(key - thickness) < 0.01 ? active : Brushes.Transparent;
        }
    }

    // ---------------- 上下文选项行 ----------------

    private void RebuildOptionsRow()
    {
        _optionsRow.Children.Clear();
        _textBackgroundButtons.Clear();

        switch (_currentTool)
        {
            // 几何 / 画笔类：粗细 + 颜色
            case EditorTool.Rectangle:
            case EditorTool.Ellipse:
            case EditorTool.Line:
            case EditorTool.Polyline:
            case EditorTool.Arrow:
            case EditorTool.Pen:
            case EditorTool.Highlighter:
                AddThicknessChoices();
                _optionsRow.Children.Add(Divider());
                AddColorChoices();
                break;

            case EditorTool.Text:
                AddFontSizeChoices();
                _optionsRow.Children.Add(Divider());
                AddTextBackgroundChoices();
                _optionsRow.Children.Add(Divider());
                AddColorChoices();
                break;

            case EditorTool.Label:
                AddFontSizeChoices();
                _optionsRow.Children.Add(Divider());
                AddColorChoices();
                break;

            case EditorTool.Number:
                AddColorChoices();
                break;

            case EditorTool.Mosaic:
                AddMosaicChoices();
                break;

            case EditorTool.Watermark:
                _optionsRow.Children.Add(MakeOptionButton("水印文字",
                    "编辑水印文字，可用 $yyyy-MM-dd HH:mm:ss$ 时间变量", EditWatermarkText, 68));
                AddFontSizeChoices();
                _optionsRow.Children.Add(Divider());
                _optionsRow.Children.Add(MakeOptionButton($"角度 {_watermarkAngle:0}°",
                    "循环切换水印角度", CycleWatermarkAngle, 78));
                _optionsRow.Children.Add(MakeOptionButton($"透明度 {_watermarkOpacity:P0}",
                    "循环切换水印透明度", CycleWatermarkOpacity, 88));
                break;

            case EditorTool.Spotlight:
                var isEllipse = ResolveLayer()?.SpotlightEllipse ?? true;
                _optionsRow.Children.Add(MakeOptionButton("形状：" + (isEllipse ? "椭圆" : "矩形"),
                    "切换聚光灯形状（椭圆/矩形）", ToggleSpotlightShape, 84));
                break;

            case EditorTool.Magnifier:
                _optionsRow.Children.Add(MakeOptionLabel("倍率"));
                foreach (var zoom in new[] { 2, 4, 8 })
                {
                    var value = zoom;
                    var button = MakeOptionButton($"{zoom}×", $"放大 {zoom} 倍",
                        () => SetMagnifierZoom(value), 44,
                        () => (ResolveLayer()?.MagnifierZoom ?? 4) == value ? "selected" : null);
                    _optionsRow.Children.Add(button);
                }

                break;
        }

        _optionsRow.Visibility = _optionsRow.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 参数面板贴到当前工具按钮下方；没有参数就不弹
        if (_optionsPopup is not null)
        {
            if (_optionsRow.Children.Count > 0 && _toolButtons.TryGetValue(_currentTool, out var anchor))
            {
                _optionsPopup.PlacementTarget = anchor;
                _optionsPopup.IsOpen = true;
            }
            else
            {
                _optionsPopup.IsOpen = false;
            }
        }

        if (_currentTool == EditorTool.Text)
        {
            SelectTextBackground(ResolveLayer()?.DrawingStyle.TextBackground ?? TextBackgroundKind.None);
        }
    }

    /// <summary>调色板选项（每个工具自己的参数行里都会出现）。</summary>
    private void AddColorChoices()
    {
        _optionsRow.Children.Add(MakeOptionLabel("颜色"));
        _colorButtons.Clear();

        var current = ResolveLayer()?.DrawingStyle.Color ?? Palette[0];

        foreach (var color in Palette)
        {
            var value = color;
            var swatch = new Button
            {
                Style = FindStyle("ColorSwatchButton"),
                Background = new SolidColorBrush(color),
                ToolTip = color.ToString(),
                Tag = value == current ? "selected" : null,
            };

            swatch.Click += (_, _) =>
            {
                SelectColor(value);
                ColorSelected?.Invoke(value);
            };

            _colorButtons[value] = swatch;
            _optionsRow.Children.Add(swatch);
        }
    }

    /// <summary>线宽选项（几何/画笔类工具用）。</summary>
    private void AddThicknessChoices()
    {
        _optionsRow.Children.Add(MakeOptionLabel("粗细"));
        _thicknessButtons.Clear();

        var current = ResolveLayer()?.DrawingStyle.Thickness ?? 4;

        foreach (var thickness in ThicknessChoices)
        {
            var value = thickness;
            var dot = new Border
            {
                Width = 4 + thickness * 1.4,
                Height = 4 + thickness * 1.4,
                CornerRadius = new CornerRadius(99),
                Background = Brush("ToolbarForegroundBrush", Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var button = new Button
            {
                Content = dot,
                Width = 30,
                Height = 24,
                Style = FindStyle("ToolbarButton"),
                ToolTip = $"线宽 {thickness}（滚轮可调）",
            };

            button.Click += (_, _) =>
            {
                SelectThickness(value);
                ThicknessSelected?.Invoke(value);
                ResolveLayer()?.Focus();
            };

            _thicknessButtons[value] = button;
            _optionsRow.Children.Add(button);
        }

        SelectThickness(current);
    }

    /// <summary>马赛克强度（块大小）。</summary>
    private void AddMosaicChoices()
    {
        _optionsRow.Children.Add(MakeOptionLabel("强度"));

        var current = ResolveLayer()?.MosaicBlockSize ?? 10;
        foreach (var size in MosaicChoices)
        {
            var value = size;
            var label = size switch
            {
                6 => "细",
                16 => "粗",
                _ => "标准",
            };

            var button = MakeOptionButton(label, $"马赛克块 {size}px",
                () =>
                {
                    if (ResolveLayer() is { } layer)
                    {
                        layer.MosaicBlockSize = value;
                    }
                }, 44, () => current == value ? "selected" : null);

            _optionsRow.Children.Add(button);
        }
    }

    private void AddFontSizeChoices()
    {
        _optionsRow.Children.Add(MakeOptionLabel("字号"));
        foreach (var size in FontSizeChoices)
        {
            var value = size;
            var button = MakeOptionButton($"{size:0}", $"字号 {size:0}",
                () => SetFontSize(value), 40,
                () => Math.Abs((ResolveLayer()?.DrawingStyle.FontSize ?? 20) - value) < 0.01
                    ? "selected"
                    : null);
            _optionsRow.Children.Add(button);
        }
    }

    private void AddTextBackgroundChoices()
    {
        _optionsRow.Children.Add(MakeOptionLabel("背景"));
        AddTextBackground(TextBackgroundKind.None, "无背景", "无", Colors.Transparent);
        AddTextBackground(TextBackgroundKind.White, "白色背景", "白", Colors.White);
        AddTextBackground(TextBackgroundKind.Yellow, "黄色背景", "黄", Color.FromRgb(0xF2, 0xC2, 0x00));
        AddTextBackground(TextBackgroundKind.Black, "黑色背景", "黑", Color.FromRgb(0x11, 0x11, 0x11));
    }

    private void AddTextBackground(TextBackgroundKind kind, string tip, string label, Color color)
    {
        var button = new Button
        {
            Content = label,
            ToolTip = tip,
            Style = FindStyle("ToolbarButton"),
            Width = 26,
            Height = 24,
            FontSize = 11,
            Background = kind == TextBackgroundKind.None
                ? Brushes.Transparent
                : new SolidColorBrush(color),
            Foreground = kind == TextBackgroundKind.White
                ? new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22))
                : Brush("ToolbarForegroundBrush", Colors.White),
            Tag = null,
        };

        button.Click += (_, _) =>
        {
            ApplyToLayer(layer =>
            {
                layer.DrawingStyle.TextBackground = kind;
                layer.Focus();
            });
            RefreshOptionHighlights();
            SelectTextBackground(kind);
        };

        _textBackgroundButtons[kind] = button;
        _optionsRow.Children.Add(button);
    }

    private void SelectTextBackground(TextBackgroundKind kind)
    {
        foreach (var (key, button) in _textBackgroundButtons)
        {
            // 选中的用不透明 + 加粗显示，未选中的压暗；颜色本身仍保留在按钮底色上。
            button.Opacity = key == kind ? 1.0 : 0.55;
            button.FontWeight = key == kind ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    private void SetFontSize(double size)
    {
        ApplyToLayer(layer =>
        {
            layer.DrawingStyle.FontSize = size;
            layer.Focus();
        });
        RefreshOptionHighlights();
    }

    private void SetMagnifierZoom(int zoom)
    {
        ApplyToLayer(layer =>
        {
            layer.MagnifierZoom = zoom;
            layer.Focus();
        });
        RefreshOptionHighlights();
    }

    private void ToggleSpotlightShape()
    {
        ApplyToLayer(layer =>
        {
            var next = !layer.SpotlightEllipse;
            layer.SpotlightEllipse = next;
            RebuildOptionsRow();
            layer.Focus();
        });
    }

    private void CycleWatermarkAngle()
    {
        _watermarkAngle = NextInCycle(WatermarkAngles, _watermarkAngle);
        ApplyToLayer(layer =>
        {
            layer.WatermarkAngle = _watermarkAngle;
            layer.Focus();
        });
        RebuildOptionsRow();
    }

    private void CycleWatermarkOpacity()
    {
        _watermarkOpacity = NextInCycle(WatermarkOpacities, _watermarkOpacity);
        ApplyToLayer(layer =>
        {
            layer.WatermarkOpacity = _watermarkOpacity;
            layer.Focus();
        });
        RebuildOptionsRow();
    }

    private void EditWatermarkText()
    {
        var layer = ResolveLayer();
        if (layer is null)
        {
            return;
        }

        var dialog = new TextInputDialog(layer.WatermarkText)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Text))
        {
            layer.WatermarkText = dialog.Text!;
        }

        layer.Focus();
    }

    private static double NextInCycle(double[] values, double current)
    {
        var index = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < values.Length; i++)
        {
            var distance = Math.Abs(values[i] - current);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                index = i;
            }
        }

        return values[(index + 1) % values.Length];
    }

    private void RefreshOptionHighlights()
    {
        RebuildOptionsRow();
    }

    private Button MakeOptionButton(string text, string tip, Action onClick, double width,
        Func<string?>? tagSelector = null)
    {
        var button = new Button
        {
            Content = text,
            ToolTip = tip,
            Style = FindStyle("ToolbarButton"),
            Width = width,
            Height = 24,
            FontSize = 11,
            Foreground = Brush("ToolbarForegroundBrush", Color.FromRgb(0xED, 0xED, 0xED)),
        };

        button.Click += (_, _) => onClick();

        if (tagSelector is not null && tagSelector() is { } tag)
        {
            button.Tag = tag;
            button.Background = Brush("ToolbarActiveBrush", Color.FromRgb(0x2D, 0x8C, 0xFF));
        }

        return button;
    }

    private TextBlock MakeOptionLabel(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 4, 0),
        };

        return label;
    }

    // ---------------- 与 AnnotationLayer 协作 ----------------

    private AnnotationLayer? ResolveLayer() => FindLayer(Window.GetWindow(this));

    private static AnnotationLayer? FindLayer(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is AnnotationLayer layer)
            {
                return layer;
            }

            var nested = FindLayer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void ApplyToLayer(Action<AnnotationLayer> action)
    {
        var layer = ResolveLayer();
        if (layer is not null)
        {
            action(layer);
        }
    }

    // ---------------- 基础控件 ----------------

    private Button MakeButton(object content, string tip, Action onClick, string styleKey)
    {
        var button = new Button
        {
            Content = content,
            ToolTip = tip,
            Style = FindStyle(styleKey),
            Foreground = Brush("ToolbarForegroundBrush", Color.FromRgb(0xED, 0xED, 0xED)),
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    private static Border Divider() => new()
    {
        Width = 1,
        Height = 16,
        Margin = new Thickness(4, 0, 4, 0),
        Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static FrameworkElement MakeIcon(string data, bool filled = false)
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xED));
        brush.Freeze();

        var path = new Path
        {
            Data = Geometry.Parse(data),
            Stroke = brush,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = filled ? brush : null,
        };

        return new Viewbox
        {
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            Child = path,
        };
    }

    private static FrameworkElement MakeNumberIcon()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xED));
        brush.Freeze();

        var grid = new Grid { Width = 18, Height = 18 };
        grid.Children.Add(new Ellipse
        {
            Stroke = brush,
            StrokeThickness = 1.5,
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        grid.Children.Add(new TextBlock
        {
            Text = "1",
            Foreground = brush,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
        });

        return grid;
    }

    private static FrameworkElement MakeRecordIcon()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
        brush.Freeze();

        return new Grid
        {
            Width = 16,
            Height = 16,
            Children =
            {
                new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = brush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }

    private Style? FindStyle(string key)
        => TryFindResource(key) as Style ?? Application.Current.TryFindResource(key) as Style;

    private Brush Brush(string key, Color fallback)
        => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);


}
