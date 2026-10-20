using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PinShot.App.Editor;

public sealed class RectangleAnnotation : AnnotationBase
{
    public RectangleAnnotation(AnnotationStyle style, Point start, Point end) : base(style)
    {
        Start = start;
        End = end;
    }

    public Point Start { get; set; }

    public Point End { get; set; }

    public override Rect Bounds => Normalize(Start, End);

    public override void Draw(DrawingContext dc, BitmapSource? background)
        => dc.DrawRectangle(null, CreatePen(), Bounds);

    public override void Translate(double dx, double dy)
    {
        Start = new Point(Start.X + dx, Start.Y + dy);
        End = new Point(End.X + dx, End.Y + dy);
    }

    public override void Scale(double sx, double sy, Point anchor)
    {
        Start = ScalePoint(Start, anchor, sx, sy);
        End = ScalePoint(End, anchor, sx, sy);
    }

    protected override IAnnotation CloneCore()
    {
        var clone = new RectangleAnnotation(Style, Start, End);
        clone.Style = Style.Clone();
        return clone;
    }
}

public sealed class EllipseAnnotation : AnnotationBase
{
    public EllipseAnnotation(AnnotationStyle style, Point start, Point end) : base(style)
    {
        Start = start;
        End = end;
    }

    public Point Start { get; set; }

    public Point End { get; set; }

    public override Rect Bounds => Normalize(Start, End);

    public override void Draw(DrawingContext dc, BitmapSource? background)
    {
        var bounds = Bounds;
        dc.DrawEllipse(null, CreatePen(),
            new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2),
            bounds.Width / 2, bounds.Height / 2);
    }

    public override void Translate(double dx, double dy)
    {
        Start = new Point(Start.X + dx, Start.Y + dy);
        End = new Point(End.X + dx, End.Y + dy);
    }

    public override void Scale(double sx, double sy, Point anchor)
    {
        Start = ScalePoint(Start, anchor, sx, sy);
        End = ScalePoint(End, anchor, sx, sy);
    }

    protected override IAnnotation CloneCore()
    {
        var clone = new EllipseAnnotation(Style, Start, End);
        clone.Style = Style.Clone();
        return clone;
    }
}

public sealed class LineAnnotation : AnnotationBase
{
    public LineAnnotation(AnnotationStyle style, Point start, Point end) : base(style)
    {
        Start = start;
        End = end;
    }

    public Point Start { get; set; }

    public Point End { get; set; }

    public override Rect Bounds => Normalize(Start, End);

    public override void Draw(DrawingContext dc, BitmapSource? background)
        => dc.DrawLine(CreatePen(), Start, End);

    public override void Translate(double dx, double dy)
    {
        Start = new Point(Start.X + dx, Start.Y + dy);
        End = new Point(End.X + dx, End.Y + dy);
    }

    public override void Scale(double sx, double sy, Point anchor)
    {
        Start = ScalePoint(Start, anchor, sx, sy);
        End = ScalePoint(End, anchor, sx, sy);
    }

    protected override IAnnotation CloneCore()
    {
        var clone = new LineAnnotation(Style, Start, End);
        clone.Style = Style.Clone();
        return clone;
    }
}

public sealed class ArrowAnnotation : AnnotationBase
{
    public ArrowAnnotation(AnnotationStyle style, Point start, Point end) : base(style)
    {
        Start = start;
        End = end;
    }

    public Point Start { get; set; }

    public Point End { get; set; }

    public override Rect Bounds
    {
        get
        {
            var head = Style.Thickness * 6;
            var bounds = Normalize(Start, End);
            bounds.Inflate(head, head);
            return bounds;
        }
    }

    public override void Draw(DrawingContext dc, BitmapSource? background)
    {
        var direction = End - Start;
        var length = direction.Length;
        if (length < 2)
        {
            return;
        }

        var unit = direction / length;
        var perpendicular = new Vector(-unit.Y, unit.X);
        var headLength = Math.Min(length * 0.45, Style.Thickness * 7);
        var halfWidth = headLength * 0.45;

        dc.DrawLine(CreatePen(), Start, End);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(End, true, true);
            ctx.LineTo(End - unit * headLength + perpendicular * halfWidth, true, false);
            ctx.LineTo(End - unit * headLength - perpendicular * halfWidth, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(CreateBrush(), null, geometry);
    }

    public override void Translate(double dx, double dy)
    {
        Start = new Point(Start.X + dx, Start.Y + dy);
        End = new Point(End.X + dx, End.Y + dy);
    }

    public override void Scale(double sx, double sy, Point anchor)
    {
        Start = ScalePoint(Start, anchor, sx, sy);
        End = ScalePoint(End, anchor, sx, sy);
    }

    protected override IAnnotation CloneCore()
    {
        var clone = new ArrowAnnotation(Style, Start, End);
        clone.Style = Style.Clone();
        return clone;
    }
}

/// <summary>自由画笔；<see cref="IsHighlighter"/> 为 true 时表现为半透明高亮。</summary>
public sealed class PenAnnotation : AnnotationBase
{
    public PenAnnotation(AnnotationStyle style, bool isHighlighter) : base(style)
    {
        IsHighlighter = isHighlighter;
    }

    public bool IsHighlighter { get; }

    public List<Point> Points { get; } = new();

    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0)
            {
                return Rect.Empty;
            }

            var minX = Points.Min(p => p.X);
            var minY = Points.Min(p => p.Y);
            var maxX = Points.Max(p => p.X);
            var maxY = Points.Max(p => p.Y);
            var padding = Style.Thickness * (IsHighlighter ? 4 : 2) + 2;
            return new Rect(minX - padding, minY - padding,
                maxX - minX + padding * 2, maxY - minY + padding * 2);
        }
    }

    public override void Draw(DrawingContext dc, BitmapSource? background)
    {
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

        var pen = IsHighlighter
            ? new Pen(CreateBrush(0.35), Math.Max(6, Style.Thickness * 5))
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            }
            : CreatePen();

        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
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
        var clone = new PenAnnotation(Style, IsHighlighter);
        clone.Points.AddRange(Points);
        clone.Style = Style.Clone();
        return clone;
    }
}

public sealed class TextAnnotation : AnnotationBase
{
    public TextAnnotation(AnnotationStyle style, Point origin, string text) : base(style)
    {
        Origin = origin;
        Text = text;
    }

    public Point Origin { get; set; }

    public string Text { get; set; }

    public override Rect Bounds
    {
        get
        {
            var formatted = CreateFormattedText();
            var padding = BackgroundPadding;
            return new Rect(Origin.X - padding, Origin.Y - padding,
                formatted.Width + padding * 2, formatted.Height + padding * 2);
        }
    }

    private double BackgroundPadding => Style.TextBackground == TextBackgroundKind.None
        ? 0
        : Math.Max(3, Style.FontSize * 0.22);

    public override void Draw(DrawingContext dc, BitmapSource? background)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        var formatted = CreateFormattedText();

        if (Style.TextBackground != TextBackgroundKind.None)
        {
            var padding = BackgroundPadding;
            var rect = new Rect(Origin.X - padding, Origin.Y - padding,
                formatted.Width + padding * 2, formatted.Height + padding * 2);
            dc.DrawRoundedRectangle(CreateBackgroundBrush(), null, rect, 3, 3);
        }

        dc.DrawText(formatted, Origin);
    }

    private Brush CreateBackgroundBrush()
    {
        var color = Style.TextBackground switch
        {
            TextBackgroundKind.White => Color.FromRgb(0xFF, 0xFF, 0xFF),
            TextBackgroundKind.Yellow => Color.FromRgb(0xF2, 0xC2, 0x00),
            TextBackgroundKind.Black => Color.FromRgb(0x11, 0x11, 0x11),
            _ => Colors.Transparent,
        };

        var brush = new SolidColorBrush(color)
        {
            Opacity = Math.Clamp(Style.Opacity * 0.92, 0, 1),
        };
        brush.Freeze();
        return brush;
    }

    private FormattedText CreateFormattedText()
    {
        var typeface = new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal);

        return new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, Style.FontSize, CreateBrush(), 1.0);
    }

    public override void Translate(double dx, double dy)
        => Origin = new Point(Origin.X + dx, Origin.Y + dy);

    public override void Scale(double sx, double sy, Point anchor)
    {
        Origin = ScalePoint(Origin, anchor, sx, sy);
        Style.FontSize = ScaleFontSize(Style.FontSize, sx, sy);
    }

    protected override IAnnotation CloneCore()
    {
        var clone = new TextAnnotation(Style, Origin, Text);
        clone.Style = Style.Clone();
        return clone;
    }
}

public sealed class NumberAnnotation : AnnotationBase
{
    public NumberAnnotation(AnnotationStyle style, Point center, int number) : base(style)
    {
        Center = center;
        Number = number;
    }

    public Point Center { get; set; }

    public int Number { get; set; }

    private double Radius => Math.Max(8, Style.FontSize * 0.8);

    public override Rect Bounds
        => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

    public override void Draw(DrawingContext dc, BitmapSource? background)
    {
        var radius = Radius;
        dc.DrawEllipse(CreateBrush(), null, Center, radius, radius);

        var text = new FormattedText(Number.ToString(CultureInfo.InvariantCulture),
            CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold,
                FontStretches.Normal),
            radius * 1.1, Brushes.White, 1.0);

        dc.DrawText(text, new Point(Center.X - text.Width / 2, Center.Y - text.Height / 2));
    }

    public override void Translate(double dx, double dy)
        => Center = new Point(Center.X + dx, Center.Y + dy);

    public override void Scale(double sx, double sy, Point anchor)
    {
        Center = ScalePoint(Center, anchor, sx, sy);
        Style.FontSize = ScaleFontSize(Style.FontSize, sx, sy);
    }

    protected override IAnnotation CloneCore()
    {
        var clone = new NumberAnnotation(Style, Center, Number);
        clone.Style = Style.Clone();
        return clone;
    }
}

/// <summary>矩形马赛克：对背景图按块取平均色后回贴，块边界保持纯色。</summary>
public sealed class MosaicAnnotation : AnnotationBase
{
    private BitmapSource? _cache;
    private Rect _cacheArea;
    private int _cacheBlockSize;

    public MosaicAnnotation(AnnotationStyle style, Rect area, int blockSize = 12) : base(style)
    {
        Area = area;
        BlockSize = blockSize;
    }

    public Rect Area { get; set; }

    public int BlockSize { get; set; }

    public override Rect Bounds => Area;

    public override void Draw(DrawingContext dc, BitmapSource? background)
    {
        if (background is null || Area.Width < 1 || Area.Height < 1)
        {
            return;
        }

        var blockSize = Math.Max(2, BlockSize);
        if (_cache is null || _cacheArea != Area || _cacheBlockSize != blockSize)
        {
            _cache = BuildMosaic(background, Area, blockSize);
            _cacheArea = Area;
            _cacheBlockSize = blockSize;
        }

        if (_cache is not null)
        {
            dc.DrawImage(_cache, Area);
        }
    }

    private static BitmapSource? BuildMosaic(BitmapSource background, Rect area, int blockSize)
    {
        var x = (int)Math.Round(area.X);
        var y = (int)Math.Round(area.Y);
        var width = (int)Math.Round(area.Width);
        var height = (int)Math.Round(area.Height);

        if (width < 1 || height < 1)
        {
            return null;
        }

        x = Math.Clamp(x, 0, Math.Max(0, background.PixelWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, background.PixelHeight - 1));
        width = Math.Clamp(width, 1, background.PixelWidth - x);
        height = Math.Clamp(height, 1, background.PixelHeight - y);

        var bgra = background.Format == PixelFormats.Bgra32
            ? background
            : new FormatConvertedBitmap(background, PixelFormats.Bgra32, null, 0);

        var cropped = new CroppedBitmap(bgra, new Int32Rect(x, y, width, height));
        var stride = width * 4;
        var pixels = new byte[stride * height];
        cropped.CopyPixels(pixels, stride, 0);

        var output = new byte[pixels.Length];

        for (var blockY = 0; blockY < height; blockY += blockSize)
        {
            var maxY = Math.Min(blockY + blockSize, height);
            for (var blockX = 0; blockX < width; blockX += blockSize)
            {
                var maxX = Math.Min(blockX + blockSize, width);
                long blue = 0, green = 0, red = 0, alpha = 0;
                var count = 0;

                for (var py = blockY; py < maxY; py++)
                {
                    var rowBase = py * stride;
                    for (var px = blockX; px < maxX; px++)
                    {
                        var index = rowBase + px * 4;
                        blue += pixels[index];
                        green += pixels[index + 1];
                        red += pixels[index + 2];
                        alpha += pixels[index + 3];
                        count++;
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                var average = new[]
                {
                    (byte)(blue / count), (byte)(green / count),
                    (byte)(red / count), (byte)(alpha / count),
                };

                for (var py = blockY; py < maxY; py++)
                {
                    var rowBase = py * stride;
                    for (var px = blockX; px < maxX; px++)
                    {
                        var index = rowBase + px * 4;
                        output[index] = average[0];
                        output[index + 1] = average[1];
                        output[index + 2] = average[2];
                        output[index + 3] = average[3];
                    }
                }
            }
        }

        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            output, stride);
        result.Freeze();
        return result;
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
        var clone = new MosaicAnnotation(Style, Area, BlockSize);
        clone.Style = Style.Clone();
        return clone;
    }
}
