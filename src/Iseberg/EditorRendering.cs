using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Iseberg.Core;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Text.RegularExpressions;

namespace Iseberg;

public sealed class PowerShellColorizer : DocumentColorizingTransformer
{
    private static readonly Regex XmlTokens = new("""(?<Comment><!--.*?-->)|</?(?<Tag>[\w:.-]+)|(?<Attribute>[\w:.-]+)\s*=\s*(?<Value>"[^"]*"|'[^']*')""",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public ScriptAnalysis? Analysis { get; set; }
    public EditorTheme Theme { get; set; } = new();
    public string Pane { get; set; } = "Script";
    protected override void ColorizeLine(DocumentLine line)
    {
        if (Analysis is null || DesktopTheme.HighContrast) return;
        Token? previous = null;
        foreach (var token in Analysis.Tokens)
        {
            if (token.Extent.StartOffset >= line.EndOffset) break;
            var color = ColorFor(token, previous, Theme, Pane);
            previous = token;
            if (token.Extent.EndOffset <= line.Offset) continue;
            if (color is null) continue;
            ChangeLinePart(Math.Max(line.Offset, token.Extent.StartOffset), Math.Min(line.EndOffset, token.Extent.EndOffset),
                element => element.TextRunProperties.SetForegroundBrush(color));
            if (token is StringExpandableToken { NestedTokens: { } nested })
                foreach (var variable in nested)
                    if (variable.Extent.StartOffset < line.EndOffset && variable.Extent.EndOffset > line.Offset)
                        ChangeLinePart(Math.Max(line.Offset, variable.Extent.StartOffset), Math.Min(line.EndOffset, variable.Extent.EndOffset),
                            element => element.TextRunProperties.SetForegroundBrush(Brush("Variable")));
            if (token is StringToken && token.Text.Contains('<'))
                foreach (Match match in XmlTokens.Matches(token.Text))
                    foreach (var name in new[] { "Comment", "Tag", "Attribute", "Value" })
                    {
                        var group = match.Groups[name];
                        var start = token.Extent.StartOffset + group.Index;
                        var end = start + group.Length;
                        if (group.Success && start < line.EndOffset && end > line.Offset)
                            ChangeLinePart(Math.Max(line.Offset, start), Math.Min(line.EndOffset, end),
                                element => element.TextRunProperties.SetForegroundBrush(new SolidColorBrush(Color.Parse(Theme.Colors["Xml." + name]))));
                    }
        }
    }

    private IBrush Brush(string kind) => new SolidColorBrush(Color.Parse(Theme.Colors[Pane + "." + kind]));

    public static IBrush? ColorFor(Token token, Token? previous, EditorTheme theme, string pane)
    {
        IBrush Brush(string kind) => new SolidColorBrush(Color.Parse(theme.Colors[pane + "." + kind]));
        if (previous?.Kind is TokenKind.Function or TokenKind.Filter) return Brush("Function");
        if (token.TokenFlags.HasFlag(TokenFlags.AttributeName)) return Brush("Attribute");
        if (token.Kind == TokenKind.Label) return Brush("Label");
        if (token.Kind == TokenKind.Comment) return Brush("Comment");
        if (token.Kind is TokenKind.StringLiteral or TokenKind.StringExpandable or TokenKind.HereStringLiteral or TokenKind.HereStringExpandable) return Brush("String");
        if (token.Kind is TokenKind.Variable or TokenKind.SplattedVariable) return Brush("Variable");
        if (token.Kind == TokenKind.Number) return Brush("Number");
        if (token.Kind == TokenKind.Parameter) return Brush("Parameter");
        if (token.TokenFlags.HasFlag(TokenFlags.Keyword)) return Brush("Keyword");
        if (token.TokenFlags.HasFlag(TokenFlags.CommandName)) return Brush("Command");
        if (token.TokenFlags.HasFlag(TokenFlags.TypeName)) return Brush("Type");
        if (token.TokenFlags.HasFlag(TokenFlags.MemberName)) return Brush("Member");
        if ((token.TokenFlags & (TokenFlags.BinaryOperator | TokenFlags.UnaryOperator | TokenFlags.AssignmentOperator)) != 0) return Brush("Operator");
        if (token.Kind is TokenKind.Identifier or TokenKind.Generic) return Brush("CommandArgument");
        return null;
    }
}

public sealed class ConsoleColorizer(Func<SessionModel?> session, Func<EditorTheme> theme) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        if (session() is not { } current || DesktopTheme.HighContrast) return;
        foreach (var span in current.OutputSpans)
        {
            if (span.Start >= line.EndOffset) break;
            if (span.End <= line.Offset) continue;
            var key = span.Kind switch
            {
                OutputKind.Error => "Stream.Error",
                OutputKind.Warning => "Stream.Warning",
                OutputKind.Verbose => "Stream.Verbose",
                OutputKind.Debug => "Stream.Debug",
                _ => "Console.Foreground"
            };
            ChangeLinePart(Math.Max(line.Offset, span.Start), Math.Min(line.EndOffset, span.End),
                element => element.TextRunProperties.SetForegroundBrush(new SolidColorBrush(Color.Parse(theme().Colors[key]))));
            if (span.Analysis is null) continue;
            Token? previous = null;
            foreach (var token in span.Analysis.Tokens)
            {
                var start = span.CodeStart + token.Extent.StartOffset;
                var end = span.CodeStart + token.Extent.EndOffset;
                if (start >= line.EndOffset) break;
                var brush = PowerShellColorizer.ColorFor(token, previous, theme(), "Console");
                previous = token;
                if (brush is null || end <= line.Offset) continue;
                ChangeLinePart(Math.Max(line.Offset, start), Math.Min(line.EndOffset, end),
                    element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
        if (!current.Console.HasPrompt || line.EndOffset <= current.Console.InputStart) return;
        var analysis = current.Console.InputAnalysis;
        Token? prior = null;
        foreach (var token in analysis.Tokens)
        {
            var start = current.Console.InputStart + token.Extent.StartOffset;
            var end = current.Console.InputStart + token.Extent.EndOffset;
            var brush = PowerShellColorizer.ColorFor(token, prior, theme(), "Console");
            prior = token;
            if (brush is not null && start < line.EndOffset && end > line.Offset)
                ChangeLinePart(Math.Max(line.Offset, start), Math.Min(line.EndOffset, end),
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
        var breakpoints = current.LineBreakpoints.ToDictionary(spec => spec.Line);
        foreach (var line in textView.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            var isDebug = debug() is { } location && location.Line == number &&
                string.Equals(location.ScriptPath, current.File.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            var isBreakpoint = current.File.Breakpoints.Contains(number);
            var enabled = !breakpoints.TryGetValue(number, out var spec) || spec.Enabled;
            if (!isDebug && !isBreakpoint) continue;
            var y = line.VisualTop - textView.ScrollOffset.Y;
            context.DrawRectangle(DesktopTheme.HighContrast ? DesktopTheme.Brush("ControlBrush") : isDebug ? new SolidColorBrush(Color.Parse("#FFF4B0")) : new SolidColorBrush(Color.Parse("#FADADA")),
                null, new Rect(0, y, textView.Bounds.Width, line.Height));
            var marker = DesktopTheme.HighContrast ? DesktopTheme.Brush("ControlTextBrush") : isDebug ? Brushes.Goldenrod : Brushes.DarkRed;
            context.DrawEllipse(isDebug || enabled ? marker : null,
                enabled ? null : new Pen(marker, 1), new Point(5, y + line.Height / 2), 4, 4);
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
