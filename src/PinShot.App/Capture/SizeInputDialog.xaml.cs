using System.Windows;
using System.Windows.Input;

namespace PinShot.App.Capture;

/// <summary>手动输入截图宽高的小对话框。</summary>
public partial class SizeInputDialog : Window
{
    public SizeInputDialog(int width, int height)
    {
        InitializeComponent();

        WidthBox.Text = width.ToString();
        HeightBox.Text = height.ToString();

        Loaded += (_, _) =>
        {
            WidthBox.SelectAll();
            WidthBox.Focus();
        };
    }

    public int WidthValue { get; private set; }

    public int HeightValue { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text.Trim(), out var width) ||
            !int.TryParse(HeightBox.Text.Trim(), out var height) ||
            width <= 0 || height <= 0)
        {
            WidthBox.Focus();
            return;
        }

        WidthValue = width;
        HeightValue = height;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
