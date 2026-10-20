using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PinShot.App.Editor;

/// <summary>
/// 一个标注。坐标全部使用「选区图像像素」坐标系，原点为选区左上角。
/// 绘制时由 <see cref="AnnotationLayer"/> 统一压入缩放变换，因此这里可以按 1:1 像素坐标绘制。
/// </summary>
public interface IAnnotation
{
    AnnotationStyle Style { get; set; }

    Rect Bounds { get; }

    /// <param name="dc">已应用缩放变换的绘图上下文。</param>
    /// <param name="background">选区图像，马赛克/放大镜等需要采样背景的实现使用。</param>
    void Draw(DrawingContext dc, BitmapSource? background);

    bool HitTest(Point imagePoint, double tolerance);

    /// <summary>整体平移（选区被调整时保持标注与画面的相对位置）。</summary>
    void Translate(double dx, double dy);

    /// <summary>以 <paramref name="anchor"/> 为不动点，按比例缩放几何（选中框手柄拖动时使用）。</summary>
    void Scale(double sx, double sy, Point anchor);

    IAnnotation Clone();
}

/// <summary>标注基类：提供样式、粗略命中测试与克隆。</summary>
public abstract class AnnotationBase : IAnnotation
{
    public AnnotationStyle Style { get; set; }

    protected AnnotationBase(AnnotationStyle style)
    {
        Style = style.Clone();
    }

    public abstract Rect Bounds { get; }

    public abstract void Draw(DrawingContext dc, BitmapSource? background);

    public virtual bool HitTest(Point imagePoint, double tolerance)
    {
        var bounds = Bounds;
        return !bounds.IsEmpty &&
               new Rect(bounds.X - tolerance, bounds.Y - tolerance,
                   Math.Max(0, bounds.Width) + tolerance * 2,
                   Math.Max(0, bounds.Height) + tolerance * 2).Contains(imagePoint);
    }

    public abstract void Translate(double dx, double dy);

    public abstract void Scale(double sx, double sy, Point anchor);

    public IAnnotation Clone() => CloneCore();

    protected abstract IAnnotation CloneCore();

    // ---- 通用绘制工具 ----

    protected Brush CreateBrush(double opacityMultiplier = 1)
    {
        var color = Style.Color;
        var brush = new SolidColorBrush(color)
        {
            Opacity = Math.Clamp(Style.Opacity * opacityMultiplier, 0, 1),
        };
        brush.Freeze();
        return brush;
    }

    protected Pen CreatePen(double thicknessScale = 1)
    {
        var pen = new Pen(CreateBrush(), Math.Max(0.5, Style.Thickness * thicknessScale))
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }

    protected static Rect Normalize(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    protected static Point ScalePoint(Point point, Point anchor, double sx, double sy)
        => new(anchor.X + (point.X - anchor.X) * sx, anchor.Y + (point.Y - anchor.Y) * sy);

    protected static Rect ScaleRect(Rect rect, Point anchor, double sx, double sy)
    {
        var p1 = ScalePoint(rect.TopLeft, anchor, sx, sy);
        var p2 = ScalePoint(rect.BottomRight, anchor, sx, sy);
        return Normalize(p1, p2);
    }

    /// <summary>缩放手柄拖动时文字类标注的字号缩放：按面积比开方，限制在合理范围。</summary>
    protected static double ScaleFontSize(double current, double sx, double sy)
    {
        var factor = Math.Sqrt(Math.Max(0.01, Math.Abs(sx * sy)));
        return Math.Clamp(current * factor, 8, 200);
    }
}
