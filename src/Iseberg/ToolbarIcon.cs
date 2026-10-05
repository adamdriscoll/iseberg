using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Iseberg;

public sealed class ToolbarIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<ToolbarIcon, string>(nameof(Kind), "New");
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    static ToolbarIcon() => AffectsRender<ToolbarIcon>(KindProperty);
    public ToolbarIcon() { Width = 18; Height = 18; }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        DesktopTheme.Changed += InvalidateVisual;
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DesktopTheme.Changed -= InvalidateVisual;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        using var scale = context.PushTransform(Matrix.CreateScale(Bounds.Width / 18, Bounds.Height / 18));
        var ink = DesktopTheme.HighContrast ? DesktopTheme.Brush("ControlTextBrush") : Brushes.DimGray;
        var stroke = new Pen(ink, 1);
        void Shape(string path, IBrush fill) => context.DrawGeometry(
            DesktopTheme.HighContrast && fill != Brushes.Transparent ? DesktopTheme.Brush("WindowBrush") : fill,
            stroke, Geometry.Parse(path));
        switch (Kind)
        {
            case "New": Shape("M3,1 L11,1 15,5 15,17 3,17 Z", Brushes.White); Shape("M11,1 L11,5 15,5", Brushes.LightGray); break;
            case "Encoding":
                Shape("M2,1 L16,1 16,17 2,17 Z", Brushes.White);
                Shape("M5,13 L8,5 11,13 M6,10 L10,10 M12,5 L14,5 M12,9 L14,9 M12,13 L14,13", Brushes.Transparent);
                break;
            case "Reload":
                Shape("M14,5 C6,-1 0,7 4,13 C8,19 17,14 16,8 M14,1 L14,5 10,5", Brushes.Transparent);
                break;
            case "Print":
                Shape("M5,1 L13,1 13,7 5,7 Z", Brushes.White);
                Shape("M1,6 L17,6 17,13 1,13 Z", Brushes.LightGray);
                Shape("M5,10 L13,10 13,17 5,17 Z M7,13 L11,13", Brushes.White);
                break;
            case "OpenRemoteFile":
            case "Open": Shape("M1,5 L1,3 7,3 9,5 16,5 16,15 1,15 Z", Brushes.Goldenrod); Shape("M1,15 L4,8 18,8 15,15 Z", Brushes.Khaki); break;
            case "Save":
            case "SaveAll":
                Shape("M2,1 L14,1 16,3 16,17 2,17 Z", Brushes.SteelBlue);
                Shape("M5,1 L12,1 12,7 5,7 Z", Brushes.WhiteSmoke);
                Shape("M5,10 L13,10 13,17 5,17 Z", Brushes.White);
                if (Kind == "SaveAll") Shape("M0,4 L0,18 12,18", Brushes.Transparent);
                break;
            case "Run": case "Continue": Shape("M5,2 L15,9 5,16 Z", Brushes.ForestGreen); break;
            case "Selection": case "RunSelection": Shape("M1,2 L10,2 10,5 1,5 Z M1,7 L7,7 7,10 1,10 Z M1,12 L7,12 7,15 1,15 Z", Brushes.SteelBlue); Shape("M9,7 L17,12 9,17 Z", Brushes.ForestGreen); break;
            case "Stop": Shape("M3,3 L15,3 15,15 3,15 Z", Brushes.DarkRed); break;
            case "BreakAll": Shape("M4,3 L7,3 7,15 4,15 Z M11,3 L14,3 14,15 11,15 Z", Brushes.Goldenrod); break;
            case "Copy": Shape("M2,1 L11,1 11,13 2,13 Z", Brushes.White); Shape("M6,5 L15,5 15,17 6,17 Z", Brushes.White); break;
            case "Paste": Shape("M3,3 L15,3 15,17 3,17 Z", Brushes.Goldenrod); Shape("M6,1 L12,1 12,5 6,5 Z", Brushes.LightGray); Shape("M7,7 L17,7 17,18 7,18 Z", Brushes.White); break;
            case "Cut": context.DrawLine(new Pen(ink, 2), new(4, 14), new(14, 2)); context.DrawLine(new Pen(ink, 2), new(14, 14), new(4, 2)); context.DrawEllipse(null, new Pen(ink, 1.5), new Point(3, 14), 2.5, 2.5); context.DrawEllipse(null, new Pen(ink, 1.5), new Point(15, 14), 2.5, 2.5); break;
            case "Undo": Shape("M7,2 L1,7 7,11 7,8 C14,6 17,12 13,16 C20,12 16,3 7,5 Z", Brushes.SteelBlue); break;
            case "Redo": Shape("M11,2 L17,7 11,11 11,8 C4,6 1,12 5,16 C-2,12 2,3 11,5 Z", Brushes.SteelBlue); break;
            case "Close": case "CloseSession": case "Exit": case "ExitRemoteSession":
                context.DrawLine(new Pen(ink, 1.5), new(4, 4), new(14, 14));
                context.DrawLine(new Pen(ink, 1.5), new(14, 4), new(4, 14)); break;
            case "SaveAs":
                Shape("M2,1 L14,1 16,3 16,17 2,17 Z", Brushes.SteelBlue);
                Shape("M5,1 L12,1 12,7 5,7 Z", Brushes.White);
                Shape("M7,15 L14,8 17,11 10,18 6,18 Z", Brushes.Goldenrod); break;
            case "NewSession":
                Shape("M1,2 L17,2 17,16 1,16 Z", Brushes.SteelBlue);
                Shape("M4,5 L7,8 4,11 M9,11 L13,11", Brushes.Transparent);
                context.DrawLine(new Pen(ink, 2), new(11, 3), new(17, 3));
                context.DrawLine(new Pen(ink, 2), new(14, 0), new(14, 6)); break;
            case "NewRemoteSession":
                Shape("M1,1 L10,1 10,9 1,9 Z M8,9 L17,9 17,17 8,17 Z", Brushes.SteelBlue);
                Shape("M3,4 L6,6 3,8 M10,12 L13,14 10,16 M11,4 L15,4 15,8", Brushes.Transparent);
                break;
            case "Find": case "Replace": case "GoToLine":
                context.DrawEllipse(null, new Pen(ink, 2), new(7, 7), 5, 5);
                context.DrawLine(new Pen(ink, 2), new(11, 11), new(17, 17));
                if (Kind == "Replace") Shape("M1,15 L8,15 6,12 M8,15 L6,18", Brushes.Transparent); break;
            case "Breakpoint": case "RemoveBreakpoints": case "NewBreakpoint":
                context.DrawEllipse(DesktopTheme.HighContrast ? DesktopTheme.Brush("ControlTextBrush") : Brushes.DarkRed, null, new(9, 9), 6, 6);
                if (Kind == "RemoveBreakpoints") context.DrawLine(new Pen(DesktopTheme.Brush("WindowBrush"), 2), new(4, 14), new(14, 4));
                if (Kind == "NewBreakpoint")
                {
                    var plus = new Pen(DesktopTheme.Brush("WindowBrush"), 2);
                    context.DrawLine(plus, new(5, 9), new(13, 9));
                    context.DrawLine(plus, new(9, 5), new(9, 13));
                }
                break;
            case "StepInto": case "StepOver": case "StepOut":
                Shape(Kind == "StepOut" ? "M9,14 L9,3 M5,7 L9,3 13,7" : "M9,2 L9,12 M5,8 L9,12 13,8", Brushes.Transparent);
                Shape("M2,15 L16,15 16,17 2,17 Z", Brushes.SteelBlue);
                if (Kind == "StepOver") Shape("M2,9 C2,0 16,0 16,9", Brushes.Transparent); break;
            case "Help": case "About":
                context.DrawEllipse(DesktopTheme.HighContrast ? DesktopTheme.Brush("WindowBrush") : Brushes.LightBlue, stroke, new(9, 9), 8, 8);
                Shape("M6,6 C6,2 13,2 13,6 C13,9 9,8 9,11 M9,13 L9,15", Brushes.Transparent); break;
            case "Question":
                context.DrawEllipse(DesktopTheme.HighContrast ? DesktopTheme.Brush("SelectionBrush") : Brushes.RoyalBlue,
                    stroke, new(9, 9), 8, 8);
                context.DrawGeometry(null, new Pen(DesktopTheme.HighContrast ? DesktopTheme.Brush("SelectionTextBrush") : Brushes.White, 1.5),
                    Geometry.Parse("M6,6 C6,2 13,2 13,6 C13,9 9,8 9,11 M9,13 L9,15")); break;
            case "Options":
                Shape("M7,1 L11,1 12,4 15,4 17,7 15,9 17,12 15,15 12,14 11,17 7,17 6,14 3,15 1,12 3,9 1,7 3,4 6,4 Z", Brushes.LightGray);
                context.DrawEllipse(null, stroke, new(9, 9), 3, 3); break;
            case "Snippets": case "Complete": case "MatchBrace": case "SelectBrace":
            case "CreateSnippet": case "ImportSnippets": case "ExportSnippets":
                Shape("M6,2 L3,2 3,7 1,9 3,11 3,16 6,16 M12,2 L15,2 15,7 17,9 15,11 15,16 12,16", Brushes.Transparent);
                if (Kind == "Complete") Shape("M9,3 L7,9 11,9 9,15", Brushes.Goldenrod);
                if (Kind is "MatchBrace" or "SelectBrace") Shape("M6,9 L12,9 M9,6 L12,9 9,12", Brushes.Transparent);
                if (Kind == "CreateSnippet") Shape("M6,9 L12,9 M9,6 L9,12", Brushes.Transparent);
                if (Kind is "ImportSnippets" or "ExportSnippets")
                    Shape(Kind == "ImportSnippets" ? "M9,4 L9,14 M6,11 L9,14 12,11" : "M9,14 L9,4 M6,7 L9,4 12,7", Brushes.Transparent);
                break;
            case "Clear":
                Shape("M12,1 L16,3 9,15 3,12 Z", Brushes.Khaki);
                Shape("M3,12 L9,15 7,18 1,15 Z", Brushes.SteelBlue); break;
            case "Profiles": case "AutoProfiles": case "ExecutionPolicy":
                Shape("M3,1 L15,1 15,17 3,17 Z", Brushes.White);
                Shape(Kind is "Profiles" or "AutoProfiles" ? "M6,5 L12,5 M6,8 L12,8 M6,11 L10,11" : "M5,8 L8,12 13,5", Brushes.Transparent); break;
            case "SelectAll":
                Shape("M2,2 L16,2 16,16 2,16 Z M5,5 L13,5 M5,9 L13,9 M5,13 L13,13", Brushes.Transparent); break;
            case "ZoomIn": case "ZoomOut":
                context.DrawEllipse(null, stroke, new(7, 7), 6, 6);
                context.DrawLine(stroke, new(12, 12), new(17, 17));
                context.DrawLine(stroke, new(3, 7), new(11, 7));
                if (Kind == "ZoomIn") context.DrawLine(stroke, new(7, 3), new(7, 11)); break;
            case "Fold":
                Shape("M3,3 L15,3 15,15 3,15 Z M6,9 L12,9", Brushes.White); break;
            case "LineNumbers": case "WordWrap":
                Shape("M1,3 L3,3 M1,8 L3,8 M1,13 L3,13 M6,3 L17,3 M6,8 L17,8 M6,13 L13,13", Brushes.Transparent); break;
            case "Top": case "Right": case "Maximized": case "Commands": case "ShowCommand": case "FocusScript": case "FocusConsole": case "DebuggerPanes":
                Shape("M1,2 L17,2 17,16 1,16 Z", Brushes.White);
                Shape("M1,2 L17,2 17,5 1,5 Z", Brushes.SteelBlue);
                if (Kind == "Top") Shape("M2,11 L16,11 16,15 2,15 Z", Brushes.MidnightBlue);
                if (Kind == "Right") Shape("M2,6 L8,6 8,15 2,15 Z", Brushes.MidnightBlue);
                if (Kind == "Commands") Shape("M11,6 L16,6 16,15 11,15 Z", Brushes.LightGray);
                if (Kind == "ShowCommand") Shape("M3,7 L6,7 6,10 3,10 Z M8,7 L15,7 M3,12 L6,12 6,15 3,15 Z M8,12 L15,12", Brushes.Transparent);
                if (Kind == "FocusScript") Shape("M3,7 L15,7 M3,10 L15,10 M3,13 L10,13", Brushes.Transparent);
                if (Kind == "FocusConsole") Shape("M3,7 L6,10 3,13 M8,13 L13,13", Brushes.Transparent);
                if (Kind == "DebuggerPanes") Shape("M2,11 L16,11 M7,11 L7,15 M12,11 L12,15", Brushes.Transparent);
                break;
            default: throw new InvalidOperationException($"Unknown toolbar icon: {Kind}");
        }
    }
}
