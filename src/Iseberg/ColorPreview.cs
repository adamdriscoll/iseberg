using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Iseberg;

public sealed class ColorPreview : Control
{
    public static readonly StyledProperty<Color?> ColorProperty = AvaloniaProperty.Register<ColorPreview, Color?>(nameof(Color));
    public Color? Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    static ColorPreview() => AffectsRender<ColorPreview>(ColorProperty);
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Color is { } color ? new SolidColorBrush(color) :
            DesktopTheme.HighContrast ? DesktopTheme.Brush("ControlBrush") : new SolidColorBrush(Avalonia.Media.Color.Parse("#EEEEEE")),
            new Rect(Bounds.Size));
        if (Color is not null) return;
        var pen = new Pen(DesktopTheme.Brush("WindowBrush"), 2);
        for (var x = 0; x < Bounds.Width; x += 21) context.DrawLine(pen, new(x, 0), new(x, Bounds.Height));
        for (var y = 0; y < Bounds.Height; y += 28) context.DrawLine(pen, new(0, y), new(Bounds.Width, y));
    }
}
