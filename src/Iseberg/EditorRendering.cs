using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Iseberg.Core;
using System.Management.Automation;
using System.Management.Automation.Language;

namespace Iseberg;

public sealed class PowerShellColorizer : DocumentColorizingTransformer
{
    public ScriptAnalysis? Analysis { get; set; }
    protected override void ColorizeLine(DocumentLine line)
    {
        if (Analysis is null) return;
        foreach (var token in Analysis.Tokens)
        {
            if (token.Extent.StartOffset >= line.EndOffset) break;
            if (token.Extent.EndOffset <= line.Offset) continue;
            var color = ColorFor(token);
            if (color is null) continue;
            ChangeLinePart(Math.Max(line.Offset, token.Extent.StartOffset), Math.Min(line.EndOffset, token.Extent.EndOffset),
                element => element.TextRunProperties.SetForegroundBrush(color));
            if (token is StringExpandableToken { NestedTokens: { } nested })
                foreach (var variable in nested)
                    if (variable.Extent.StartOffset < line.EndOffset && variable.Extent.EndOffset > line.Offset)
                        ChangeLinePart(Math.Max(line.Offset, variable.Extent.StartOffset), Math.Min(line.EndOffset, variable.Extent.EndOffset),
                            element => element.TextRunProperties.SetForegroundBrush(Brushes.Purple));
        }
    }

    private static IBrush? ColorFor(Token token)
    {
        if (token.Kind == TokenKind.Comment) return Brushes.DarkGreen;
        if (token.Kind is TokenKind.StringLiteral or TokenKind.StringExpandable or TokenKind.HereStringLiteral or TokenKind.HereStringExpandable) return Brushes.DarkRed;
        if (token.Kind is TokenKind.Variable or TokenKind.SplattedVariable or TokenKind.Number) return Brushes.Purple;
        if (token.Kind == TokenKind.Parameter) return Brushes.DarkGray;
        if (token.TokenFlags.HasFlag(TokenFlags.Keyword)) return Brushes.DarkBlue;
        if (token.TokenFlags.HasFlag(TokenFlags.CommandName)) return Brushes.Blue;
        if (token.TokenFlags.HasFlag(TokenFlags.TypeName)) return Brushes.Teal;
        return null;
    }
}

public sealed class ConsoleColorizer(Func<SessionModel?> session) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        if (session() is not { } current) return;
        foreach (var span in current.OutputSpans)
        {
            if (span.Start >= line.EndOffset) break;
            if (span.End <= line.Offset) continue;
            var brush = span.Kind switch
            {
                OutputKind.Error => Brushes.Red,
                OutputKind.Warning or OutputKind.Verbose or OutputKind.Debug => Brushes.Yellow,
                _ => Brushes.White
            };
            ChangeLinePart(Math.Max(line.Offset, span.Start), Math.Min(line.EndOffset, span.End),
                element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}

public sealed class ScriptAdornments(Func<ScriptTab?> file, Func<DebugLocation?> debug) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;
    public void Draw(TextView textView, DrawingContext context)
    {
        if (file() is not { } current || !textView.VisualLinesValid) return;
        foreach (var line in textView.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            var isDebug = debug() is { } location && location.Line == number &&
                string.Equals(location.ScriptPath, current.File.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            var isBreakpoint = current.File.Breakpoints.Contains(number);
            if (!isDebug && !isBreakpoint) continue;
            var y = line.VisualTop - textView.ScrollOffset.Y;
            context.DrawRectangle(isDebug ? new SolidColorBrush(Color.Parse("#FFF4B0")) : new SolidColorBrush(Color.Parse("#FADADA")),
                null, new Rect(0, y, textView.Bounds.Width, line.Height));
            context.DrawEllipse(isDebug ? Brushes.Goldenrod : Brushes.DarkRed, null, new Point(5, y + line.Height / 2), 4, 4);
        }
    }
}

public sealed class PowerShellCompletion(CompletionResult result) : ICompletionData
{
    public IImage? Image => null;
    public string Text => result.CompletionText;
    public object Content => result.ListItemText;
    public object Description => result.ToolTip;
    public double Priority => 0;
    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, result.CompletionText);
}
