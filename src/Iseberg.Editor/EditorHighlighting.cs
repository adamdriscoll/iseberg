using System.Xml;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace Iseberg.Editor;

internal static class EditorHighlighting
{
    public static IHighlightingDefinition Create(bool dark)
    {
        using var stream = typeof(TextEditor).Assembly.GetManifestResourceStream("AvaloniaEdit.Highlighting.Resources.PowerShell.xshd")
            ?? throw new InvalidOperationException("AvaloniaEdit's PowerShell syntax definition is unavailable.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        foreach (var color in definition.NamedHighlightingColors)
            color.Foreground = new SimpleHighlightingBrush(Color.Parse(color.Name switch
            {
                "Comment" => dark ? "#90EE90" : "#006400",
                "String" or "Char" => dark ? "#FFCB6B" : "#8B0000",
                "Variable" => dark ? "#FFAB70" : "#A63000",
                "NumberLiteral" or "ReferenceTypes" => dark ? "#D9A9FF" : "#800080",
                "ExceptionKeywords" => dark ? "#80CBC4" : "#007080",
                "Punctuation" or "Operators" => dark ? "#F0F0F0" : "#202020",
                _ => dark ? "#82AAFF" : "#0000C0"
            }));
        return definition;
    }
}
