using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Iseberg;

public sealed class ToolbarIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<ToolbarIcon, string>(nameof(Kind), "New");
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public ToolbarIcon() { Width = 18; Height = 18; }

    public override void Render(DrawingContext context)
    {
        var stroke = new Pen(Brushes.DimGray, 1);
        void Shape(string path, IBrush fill) => context.DrawGeometry(fill, stroke, Geometry.Parse(path));
        switch (Kind)
        {
            case "New": Shape("M3,1 L11,1 15,5 15,17 3,17 Z", Brushes.White); Shape("M11,1 L11,5 15,5", Brushes.LightGray); break;
            case "Open": Shape("M1,5 L1,3 7,3 9,5 16,5 16,15 1,15 Z", Brushes.Goldenrod); Shape("M1,15 L4,8 18,8 15,15 Z", Brushes.Khaki); break;
            case "Save":
            case "SaveAll":
                Shape("M2,1 L14,1 16,3 16,17 2,17 Z", Brushes.SteelBlue);
                Shape("M5,1 L12,1 12,7 5,7 Z", Brushes.WhiteSmoke);
                Shape("M5,10 L13,10 13,17 5,17 Z", Brushes.White);
                if (Kind == "SaveAll") Shape("M0,4 L0,18 12,18", Brushes.Transparent);
                break;
            case "Run": Shape("M5,2 L15,9 5,16 Z", Brushes.ForestGreen); break;
            case "Selection": Shape("M1,2 L10,2 10,5 1,5 Z M1,7 L7,7 7,10 1,10 Z M1,12 L7,12 7,15 1,15 Z", Brushes.SteelBlue); Shape("M9,7 L17,12 9,17 Z", Brushes.ForestGreen); break;
            case "Stop": Shape("M3,3 L15,3 15,15 3,15 Z", Brushes.DarkRed); break;
            case "Copy": Shape("M2,1 L11,1 11,13 2,13 Z", Brushes.White); Shape("M6,5 L15,5 15,17 6,17 Z", Brushes.White); break;
            case "Paste": Shape("M3,3 L15,3 15,17 3,17 Z", Brushes.Goldenrod); Shape("M6,1 L12,1 12,5 6,5 Z", Brushes.LightGray); Shape("M7,7 L17,7 17,18 7,18 Z", Brushes.White); break;
            case "Cut": context.DrawLine(new Pen(Brushes.SteelBlue, 2), new(4, 14), new(14, 2)); context.DrawLine(new Pen(Brushes.SteelBlue, 2), new(14, 14), new(4, 2)); context.DrawEllipse(null, new Pen(Brushes.DimGray, 1.5), new Point(3, 14), 2.5, 2.5); context.DrawEllipse(null, new Pen(Brushes.DimGray, 1.5), new Point(15, 14), 2.5, 2.5); break;
            case "Undo": Shape("M7,2 L1,7 7,11 7,8 C14,6 17,12 13,16 C20,12 16,3 7,5 Z", Brushes.SteelBlue); break;
            case "Redo": Shape("M11,2 L17,7 11,11 11,8 C4,6 1,12 5,16 C-2,12 2,3 11,5 Z", Brushes.SteelBlue); break;
            default:
                Shape("M1,2 L17,2 17,16 1,16 Z", Brushes.White);
                Shape("M1,2 L17,2 17,5 1,5 Z", Brushes.SteelBlue);
                if (Kind == "Top") Shape("M2,11 L16,11 16,15 2,15 Z", Brushes.MidnightBlue);
                if (Kind == "Right") Shape("M2,6 L8,6 8,15 2,15 Z", Brushes.MidnightBlue);
                if (Kind == "Commands") Shape("M11,6 L16,6 16,15 11,15 Z", Brushes.LightGray);
                break;
        }
    }
}
