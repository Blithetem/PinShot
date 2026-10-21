using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PinShot.App.Capture;
using PinShot.App.Ocr;

namespace PinShot.App.Editor;

/// <summary>长截图等独立图片的编辑窗口：支持标注、复制、保存、贴图。</summary>
public partial class ImageEditorWindow : Window
{
    private readonly BitmapSource _image;
    private AnnotationLayer? _annotationLayer;
    private EditorToolbar? _toolbar;

    public ImageEditorWindow(BitmapSource image)
    {
        InitializeComponent();

        _image = image;

        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(workArea.Width - 60, 480);
        var maxHeight = Math.Max(workArea.Height - 60, 320);

        // 保证工具栏能在一到两行内放下，避免按钮被窗口边缘裁掉。
        Width = Math.Min(Math.Max(image.PixelWidth + 24, 860), maxWidth);
        Height = Math.Min(Math.Max(image.PixelHeight + 100, 360), maxHeight);
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;

        Loaded += (_, _) => SetupEditor();
        PreviewKeyDown += OnKeyDown;
        Scroller.SizeChanged += (_, _) => UpdateWheelMode();
    }

    public event Action<CaptureResult?>? Completed;

    private void SetupEditor()
    {
        if (_annotationLayer is not null)
        {
            return;
        }

        EditorCanvas.Width = _image.PixelWidth;
        EditorCanvas.Height = _image.PixelHeight;

        BackgroundImage.Source = _image;
        BackgroundImage.Width = _image.PixelWidth;
        BackgroundImage.Height = _image.PixelHeight;

        _annotationLayer = new AnnotationLayer { Background = _image };
        // 画布坐标与图片像素 1:1，必须显式给标注层尺寸，否则 ActualWidth=0 时不会绘制、也点不中。
        _annotationLayer.Width = _image.PixelWidth;
        _annotationLayer.Height = _image.PixelHeight;
        Canvas.SetLeft(_annotationLayer, 0);
        Canvas.SetTop(_annotationLayer, 0);
        _annotationLayer.TextRequested += OnTextRequested;
        _annotationLayer.TextEditRequested += OnTextEditRequested;
        _annotationLayer.LabelRequested += OnLabelRequested;
        _annotationLayer.LabelEditRequested += OnLabelEditRequested;
        _annotationLayer.Changed += () =>
            _toolbar?.UpdateState(_annotationLayer.CanUndo, _annotationLayer.CanRedo);
        EditorCanvas.Children.Add(_annotationLayer);

        // 长图场景没有"滚动长截图/录制当前选区"，把这两个动作按钮隐藏掉。
        // 默认用矩形工具：打开编辑窗口就能直接画，不用先点工具。
        _toolbar = new EditorToolbar(showLongShotAction: false, showRecordAction: false,
            defaultTool: EditorTool.Rectangle);
        _toolbar.ToolSelected += tool =>
        {
            _annotationLayer.Tool = tool;
            _annotationLayer.Focus();
        };
        _toolbar.ColorSelected += color =>
        {
            _annotationLayer.DrawingStyle.Color = color;
            _annotationLayer.Focus();
        };
        _toolbar.ThicknessSelected += thickness =>
        {
            _annotationLayer.DrawingStyle.Thickness = thickness;
            _annotationLayer.Focus();
        };
        _toolbar.UndoRequested += () =>
        {
            _annotationLayer.Undo();
            _annotationLayer.Focus();
        };
        _toolbar.RedoRequested += () =>
        {
            _annotationLayer.Redo();
            _annotationLayer.Focus();
        };
        _toolbar.CopyRequested += () => Complete(CaptureAction.Copy);
        _toolbar.SaveRequested += () => Complete(CaptureAction.Save);
        _toolbar.PinRequested += () => Complete(CaptureAction.Pin);
        _toolbar.OcrRequested += RunOcr;
        _toolbar.CancelRequested += Cancel;

        ToolbarHost.Content = _toolbar;

        _annotationLayer.Focus();
        _toolbar.UpdateState(false, false);
        UpdateWheelMode();
    }

    /// <summary>
    /// 图片能完整显示时，滚轮用来调线宽/透明度；长图需要滚动时把滚轮让给 ScrollViewer。
    /// </summary>
    private void UpdateWheelMode()
    {
        if (_annotationLayer is null)
        {
            return;
        }

        var fitsWidth = _image.PixelWidth <= Scroller.ViewportWidth + 2;
        var fitsHeight = _image.PixelHeight <= Scroller.ViewportHeight + 2;
        _annotationLayer.WheelAdjustsStyle = fitsWidth && fitsHeight;
    }

    /// <summary>当前鼠标位置（DIP），用于把输入框贴到点击处。</summary>
    private Point CursorDip()
    {
        try
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Point(cursor.X / dpi.DpiScaleX, cursor.Y / dpi.DpiScaleY);
        }
        catch
        {
            return new Point(240, 240);
        }
    }

    private void OnTextRequested(Point imagePoint)
    {
        var dialog = new TextInputDialog(null, CursorDip()) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Text))
        {
            _annotationLayer?.AddText(imagePoint, dialog.Text!);
        }

        _annotationLayer?.Focus();
    }

    private void OnTextEditRequested(TextAnnotation annotation)
    {
        var dialog = new TextInputDialog(annotation.Text, CursorDip()) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _annotationLayer?.UpdateText(annotation, dialog.Text ?? string.Empty);
        }

        _annotationLayer?.Focus();
    }

    private void OnLabelRequested(Point imagePoint)
    {
        var dialog = new TextInputDialog(null, CursorDip()) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Text))
        {
            _annotationLayer?.AddLabel(imagePoint, dialog.Text!);
        }

        _annotationLayer?.Focus();
    }

    private void OnLabelEditRequested(LabelAnnotation label)
    {
        var dialog = new TextInputDialog(label.Text, CursorDip()) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _annotationLayer?.UpdateLabel(label, dialog.Text ?? string.Empty);
        }

        _annotationLayer?.Focus();
    }

    /// <summary>识别原图文字（走系统的本地 OCR，或按 ocr.json 配置走在线模型）。</summary>
    private async void RunOcr()
    {
        try
        {
            var text = await Task.Run(() => OcrService.RecognizeAsync(_image));
            var resultWindow = new OcrResultWindow(text) { Owner = this };
            resultWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"识别失败：{ex.Message}", "PinShot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _annotationLayer?.Focus();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                // 先取消折线/取消选中，再退化为取消整张编辑
                if (_annotationLayer?.CancelSelectionOrPending() == true)
                {
                    e.Handled = true;
                    break;
                }

                Cancel();
                e.Handled = true;
                break;

            case Key.Enter:
                // 折线进行中：Enter 只结束折线，不提交整张图
                if (_annotationLayer?.FinishPolyline() == true)
                {
                    e.Handled = true;
                    break;
                }

                Complete(CaptureAction.Copy);
                e.Handled = true;
                break;

            case Key.S when Keyboard.Modifiers == ModifierKeys.Control:
                Complete(CaptureAction.Save);
                e.Handled = true;
                break;

            case Key.P:
                Complete(CaptureAction.Pin);
                e.Handled = true;
                break;

            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
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

            case Key.Y when Keyboard.Modifiers == ModifierKeys.Control:
                _annotationLayer?.Redo();
                e.Handled = true;
                break;
        }
    }

    private void Complete(CaptureAction action)
    {
        var output = _annotationLayer is { HasAnnotations: true }
            ? _annotationLayer.Render(_image)
            : _image;

        Completed?.Invoke(new CaptureResult(output, action));
        Close();
    }

    private void Cancel()
    {
        Completed?.Invoke(null);
        Close();
    }
}
