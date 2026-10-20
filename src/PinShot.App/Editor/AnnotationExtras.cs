using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PinShot.App.Editor;

/// <summary>折线：逐点点击形成多段线，可自由折返。</summary>
public sealed class PolylineAnnotation : AnnotationBase
{
    public PolylineAnnotation(AnnotationStyle style, Point first) : base(style)
    {
        Points.Add(first);
    }

    private PolylineAnnotation(AnnotationStyle style) : base(style)
    {
    }

    public List<Point> Points { get; } = new();

    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0)
            {
                return Rect.Empty;
            }

            var padding = Math.Max(3, Style.Thickness * 2 + 2);
            var minX = Points.Min(p => p.X);
            var minY = Points.Min(p => p.Y);
            var maxX = Points.Max(p => p.X);
            var maxY = Points.Max(p => p.Y);
            return new Rect(minX - padding, minY - padding,
                maxX - minX + padding * 2, maxY - minY + padding * 2);
        }
    }

    public override void Draw(DrawingContext dc, BitmapSource? background)
    {
        if (Points.Count == 1)
        {
            dc.DrawEllipse(CreateBrush(), null, Points[0], 2.5, 2.5);
            return;
        }

        if (Points.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Points[0], false, false);
            ctx.PolyLineTo(Points.Skip(1).ToList(), true, true);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, CreatePen(), geometry);
    }

    public override bool HitTest(Point imagePoint, double tolerance)
    {
        if (Points.Count == 0)
        {
            return false;
        }

        if (Points.Count == 1)
        {
            return (imagePoint - Points[0]).Length <= Math.Max(tolerance, Style.Thickness + 3);
        }

        var limit = Math.Max(tolerance, Style.Thickness + 3);
        for (var i = 1; i < Points.Count; i++)
        {
            if (DistanceToSegment(imagePoint, Points[i - 1], Points[i]) <= limit)
            {
                return true;
            }
        }

        return false;
    }

    public override void Translate(double dx, double dy)
    {
        for (var i = 0; i < Points.Count; i++)
        {
            Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy);
        }
    }

    public override void Scale(double sx, double sy, Point anchor)
    {
        for (var i = 0; i < Points.Count; i++)
        {
            Points[i] = ScalePoint(Points[i], anchor, sx, sy);
        }
    }

    protected override IAnnotation CloneCore()
    {
        var clone = new PolylineAnnotation(Style);
        clone.Points.AddRange(Points);
        clone.Style = Style.Clone();
        return clone;
    }

    private static double DistanceToSegment(Point point, Point a, Point b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared;
        if (lengthSquared < 0.0001)
        {
            return (point - a).Length;
        }

        var t = Math.Clamp(((point - a) * ab) / lengthSquared, 0, 1);
        var projection = a + ab * t;
        return (point - projection).Length;
    }
}

/// <summary>聚光灯：选区（椭圆或矩形）以外区域整体压暗，内部保持原亮度。</summary>
public sealed class SpotlightAnnotation : AnnotationBase
{
    public SpotlightAnnotation(AnnotationStyle style, Rect area, bool isEllipse) : base(style)
    {
        Area = area;
        IsEllipse = isEllipse;
    }

    public Rect Area { get; set; }

    public bool IsEllipse { get; set; }

    public override Rect Bounds => Area;

    public override void Draw(DrawingContext dc, BitmapSource? background)
    {
        if (background is null || Area.Width < 1 || Area.Height < 1)
        {
            return;
        }

        var outer = new Rect(0, 0, background.PixelWidth, background.PixelHeight);
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry(outer));
        group.Children.Add(IsEllipse
            ? new EllipseGeometry(Area)
            : new RectangleGeometry(Area));
        group.Freeze();

        dc.DrawGeometry(CreateBrush(), null, group);
    }

    public override bool HitTest(Point imagePoint, double tolerance)
    {
        // 只有点进"亮区"才算命中，避免遮住画布上其它标注的点击。
        var area = Area;
        area.Inflate(tolerance, tolerance);
        return area.Contains(imagePoint);
    }

    public override void Translate(double dx, double dy)
        => Area = new Rect(Area.X + dx, Area.Y + dy, Area.Width, Area.Height);

    public override void Scale(double sx, double sy, Point anchor)
        => Area = ScaleRect(Area, anchor, sx, sy);

    protected override IAnnotation CloneCore()
    {
        var clone = new SpotlightAnnotation(Style, Area, IsEllipse);
        clone.Style = Style.Clone();
        return clone;
    }
}

/// <summary>
/// 水印：整图平铺半透明文字，支持 <c>$yyyy-MM-dd HH:mm:ss$</c> 这类时间变量（渲染时取当前时间）。
/// 覆盖整图、不可点选，通过工具条参数或撤销来调整。
/// </summary>
public sealed class WatermarkAnnotation : AnnotationBase
{
    private static readonly Regex TimeToken = new(@"\$([^$]{1,120})\$", RegexOptions.Compiled);

    public WatermarkAnnotation(AnnotationStyle style, string textTemplate, double angle, Rect area)
        : base(style)
    {
        TextTemplate = textTemplate;
        Angle = angle;
        Area = area;
    }

    public string TextTemplate { get; set; }

    public double Angle { get; set; }

    /// <summary>覆盖范围（保持为整图区域）。</summary>
    public Rect Area { get; set; }

    public override Rect Bounds => Area;

    public override bool HitTest(Point imagePoint, double tolerance) => false;

    public override void Draw(DrawingContext dc, BitmapSource? background)
    {
        if (background is null || string.IsNullOrWhiteSpace(TextTemplate) || Area.Width < 8)
        {
            return;
        }

        var text = ResolveText(TextTemplate);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var width = background.PixelWidth;
        var height = background.PixelHeight;

        // 注意：这里必须直接把平铺文字画进传入的 DrawingContext。
        // 之前用「内层 RenderTargetBitmap 生成缓存」的写法在 AnnotationLayer.Render() 里会嵌套
        // RenderTargetBitmap.Render 导致死锁（--editor-test / 保存长图时卡死）。
        var formatted = CreateFormattedText(text);
        var tileWidth = formatted.Width * 1.5 + 48;
        var tileHeight = formatted.Height * 3.4 + 24;
        if (tileWidth < 24 || tileHeight < 24)
        {
            return;
        }

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));

        var rowIndex = 0;
        for (var y = -tileHeight; y < height + tileHeight; y += tileHeight, rowIndex++)
        {
            var rowOffset = rowIndex % 2 == 0 ? 0 : tileWidth / 2;
            for (var x = -tileWidth; x < width + tileWidth; x += tileWidth)
            {
                var origin = new Point(x + rowOffset, y);
                dc.PushTransform(new RotateTransform(Angle,
                    origin.X + formatted.Width / 2, origin.Y + formatted.Height / 2));
                dc.DrawText(formatted, origin);
                dc.Pop();
            }
        }

        dc.Pop();
    }

    private FormattedText CreateFormattedText(string text)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal,
                FontWeights.Normal, FontStretches.Normal),
            Style.FontSize, CreateBrush(), 1.0);

    /// <summary>把 <c>$格式$</c> 替换成 <see cref="DateTime.Now"/> 的对应格式。</summary>
    public static string ResolveText(string template)
    {
        if (string.IsNullOrEmpty(template) || template.IndexOf('$') < 0)
        {
            return template;
        }

        return TimeToken.Replace(template, match =>
        {
            try
            {
                return DateTime.Now.ToString(match.Groups[1].Value, CultureInfo.CurrentCulture);
            }
            catch (FormatException)
            {
                return match.Value;
            }
        });
    }

    public override void Translate(double dx, double dy)
        => Area = new Rect(Area.X + dx, Area.Y + dy, Area.Width, Area.Height);

    public override void Scale(double sx, double sy, Point anchor)
    {
        Area = ScaleRect(Area, anchor, sx, sy);
        Style.FontSize = ScaleFontSize(Style.FontSize, sx, sy);
    }

    protected override IAnnotation CloneCore()
    {
        var clone = new WatermarkAnnotation(Style, TextTemplate, Angle, Area);
        clone.Style = Style.Clone();
        return clone;
    }
}
/// <summary>放大镜：圆形区域展示按倍率放大的背景内容，带描边。</summary>
public sealed class MagnifierAnnotation : AnnotationBase
{
    private BitmapSource? _cache;
    private Rect _cacheSource;
    private int _cacheZoom;

    public MagnifierAnnotation(AnnotationStyle style, Rect area, int zoom) : base(style)
    {
        Area = area;
        Zoom = zoom;
    }

    public Rect Area { get; set; }

    public int Zoom { get; set; }

    public override Rect Bounds => Area;

    public override void Draw(DrawingContext dc, BitmapSource? background)
    {
        if (background is null || Area.Width < 6 || Area.Height < 6)
        {
            return;
        }

        var side = Math.Min(Area.Width, Area.Height);
        var center = new Point(Area.X + Area.Width / 2, Area.Y + Area.Height / 2);
        var radius = side / 2;
        var dest = new Rect(center.X - radius, center.Y - radius, side, side);
        var zoom = Math.Clamp(Zoom, 1, 16);

        var sourceSide = side / zoom;
        var sourceX = Math.Clamp(center.X - sourceSide / 2, 0,
            Math.Max(0, background.PixelWidth - sourceSide));
        var sourceY = Math.Clamp(center.Y - sourceSide / 2, 0,
            Math.Max(0, background.PixelHeight - sourceSide));
        sourceSide = Math.Min(sourceSide,
            Math.Min(background.PixelWidth - sourceX, background.PixelHeight - sourceY));
        if (sourceSide < 2)
        {
            return;
        }

        var source = new Rect(sourceX, sourceY, sourceSide, sourceSide);
        if (_cache is null || _cacheSource != source || _cacheZoom != zoom)
        {
            _cache = BuildZoomed(background, source);
            _cacheSource = source;
            _cacheZoom = zoom;
        }

        var ellipse = new EllipseGeometry(center, radius, radius);
        ellipse.Freeze();

        dc.PushClip(ellipse);
        if (_cache is not null)
        {
            dc.DrawImage(_cache, dest);
        }

        dc.Pop();

        var outline = new Pen(CreateBrush(), Math.Max(1.5, Style.Thickness))
        {
            LineJoin = PenLineJoin.Round,
        };
        outline.Freeze();
        dc.DrawEllipse(null, outline, center, radius, radius);
    }

    private static BitmapSource? BuildZoomed(BitmapSource background, Rect source)
    {
        var x = (int)Math.Round(source.X);
        var y = (int)Math.Round(source.Y);
        var width = (int)Math.Max(1, Math.Round(source.Width));
        var height = (int)Math.Max(1, Math.Round(source.Height));

        if (x < 0 || y < 0 || x + width > background.PixelWidth ||
            y + height > background.PixelHeight)
        {
            return null;
        }

        var cropped = new CroppedBitmap(background, new Int32Rect(x, y, width, height));
        cropped.Freeze();
        return cropped;
    }

    public override bool HitTest(Point imagePoint, double tolerance)
    {
        var area = Area;
        area.Inflate(tolerance, tolerance);
        return area.Contains(imagePoint);
    }

    public override void Translate(double dx, double dy)
        => Area = new Rect(Area.X + dx, Area.Y + dy, Area.Width, Area.Height);

    public override void Scale(double sx, double sy, Point anchor)
    {
        Area = ScaleRect(Area, anchor, sx, sy);
        _cache = null;
    }

    protected override IAnnotation CloneCore()
    {
        var clone = new MagnifierAnnotation(Style, Area, Zoom);
        clone.Style = Style.Clone();
        return clone;
    }
}
