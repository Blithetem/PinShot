using System.ComponentModel;
using System.Windows.Media;

namespace PinShot.App.Editor;

/// <summary>文字标注的背景样式。</summary>
public enum TextBackgroundKind
{
    None,
    White,
    Yellow,
    Black,
}

/// <summary>标注样式，单位与图像像素一致（绘制时由外层统一缩放）。</summary>
/// <remarks>
/// 实现了 <see cref="INotifyPropertyChanged"/>：<see cref="AnnotationLayer"/> 订阅当前画笔样式，
/// 当用户在工具条上改颜色/线宽/字号/背景/透明度时，能同步作用到"已选中的标注"上。
/// </remarks>
public sealed class AnnotationStyle : INotifyPropertyChanged
{
    private Color _color = Color.FromRgb(0xE8, 0x3A, 0x3A);
    private double _thickness = 4;
    private double _fontSize = 20;
    private double _opacity = 1;
    private TextBackgroundKind _textBackground = TextBackgroundKind.None;

    /// <summary>主色，默认红。</summary>
    public Color Color
    {
        get => _color;
        set => SetField(ref _color, value, nameof(Color));
    }

    /// <summary>线宽，默认 4（与工具条默认高亮档位一致）。</summary>
    public double Thickness
    {
        get => _thickness;
        set => SetField(ref _thickness, value, nameof(Thickness));
    }

    /// <summary>字号，默认 20。</summary>
    public double FontSize
    {
        get => _fontSize;
        set => SetField(ref _fontSize, value, nameof(FontSize));
    }

    /// <summary>透明度乘数（1 = 不透明），Ctrl+滚轮可调。</summary>
    public double Opacity
    {
        get => _opacity;
        set => SetField(ref _opacity, value, nameof(Opacity));
    }

    /// <summary>文字背景，默认无。</summary>
    public TextBackgroundKind TextBackground
    {
        get => _textBackground;
        set => SetField(ref _textBackground, value, nameof(TextBackground));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AnnotationStyle Clone()
    {
        var clone = (AnnotationStyle)MemberwiseClone();
        // 关键：克隆体不能继承原对象的订阅者，否则改克隆样式会回调到原对象所属的层。
        clone.PropertyChanged = null;
        return clone;
    }

    private void SetField<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
