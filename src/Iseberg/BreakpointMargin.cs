using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Iseberg.Core;

namespace Iseberg;

public sealed class BreakpointMargin(Func<ScriptTab?> file, Func<DebugLocation?> debug, Func<EditorTheme> theme) : AbstractMargin
{
    public event Action<int>? BreakpointRequested;

    protected override Size MeasureOverride(Size availableSize) => new(20, 0);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var background = DesktopTheme.HighContrast ? DesktopTheme.Brush("WindowBrush") :
            new SolidColorBrush(Color.Parse(theme().Colors["Script.Background"]));
        context.DrawRectangle(background, null, new Rect(Bounds.Size));
        if (file() is not { } current || TextView is not { VisualLinesValid: true } view) return;
        var breakpoints = current.LineBreakpoints.ToDictionary(spec => spec.Line);
        foreach (var line in view.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            var y = line.VisualTop - view.VerticalOffset + line.TextLines[0].Height / 2;
            var center = new Point(Bounds.Width / 2, y);
            var radius = Math.Clamp(line.TextLines[0].Height * .3, 1, 5);
            if (breakpoints.TryGetValue(number, out var breakpoint))
            {
                var brush = BreakpointVisuals.MarkerBrush(theme(), paused: false);
                context.DrawEllipse(breakpoint.Enabled ? brush : null, new Pen(brush, 1.5), center, radius, radius);
            }
            if (BreakpointVisuals.IsPausedLine(current, debug(), number))
            {
                var arrow = new StreamGeometry();
                using (var geometry = arrow.Open())
                {
                    geometry.BeginFigure(new Point(center.X - radius, y - radius * .8), true);
                    geometry.LineTo(new Point(center.X + radius, y));
                    geometry.LineTo(new Point(center.X - radius, y + radius * .8));
                    geometry.EndFigure(true);
                }
                var brush = BreakpointVisuals.MarkerBrush(theme(), paused: true);
                context.DrawGeometry(brush, new Pen(background, 1), arrow);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || TextView is not { } view ||
            file() is null) return;
        view.EnsureVisualLines();
        var point = e.GetPosition(view);
        if (point.Y < 0 || point.Y >= view.Bounds.Height || point.Y + view.VerticalOffset >= view.DocumentHeight) return;
        var line = view.GetVisualLineFromVisualTop(point.Y + view.VerticalOffset);
        if (line is null) return;
        e.Handled = true;
        BreakpointRequested?.Invoke(line.FirstDocumentLine.LineNumber);
    }
}

internal static class BreakpointVisuals
{
    public static bool IsPausedLine(ScriptTab current, DebugLocation? location, int line) =>
        location is not null && location.Line == line &&
        string.Equals(location.ScriptPath, current.File.Path, !current.File.IsRemote && OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static Color MarkerColor(EditorTheme theme, bool paused)
    {
        var background = Color.Parse(theme.Colors["Script.Background"]);
        var dark = (background.R * 299 + background.G * 587 + background.B * 114) < 128000;
        return Color.Parse(paused ? dark ? "#FFD54F" : "#A66A00" : dark ? "#FF6B6B" : "#B71C1C");
    }

    public static IBrush MarkerBrush(EditorTheme theme, bool paused) =>
        DesktopTheme.HighContrast ? DesktopTheme.Brush("WindowTextBrush") : new SolidColorBrush(MarkerColor(theme, paused));
}
