using System.Management.Automation.Language;

namespace Iseberg.Core;

public sealed record ScriptAnalysis(Token[] Tokens, ParseError[] Errors, IReadOnlyList<(int Start, int End)> Folds);

public static class EditorAnalysis
{
    public static ScriptAnalysis Analyze(string text)
    {
        Parser.ParseInput(text, out var tokens, out var errors);
        var stack = new Stack<Token>();
        var folds = new List<(int Start, int End)>();
        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.LCurly)
                stack.Push(token);
            else if (token.Kind == TokenKind.RCurly && stack.TryPop(out var open) &&
                     token.Extent.EndLineNumber > open.Extent.StartLineNumber)
                folds.Add((open.Extent.StartOffset, token.Extent.EndOffset));
        }
        return new(tokens, errors, folds.OrderBy(f => f.Start).ToArray());
    }
}
