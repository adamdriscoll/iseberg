using System.Management.Automation.Language;
using System.Management.Automation;

namespace Iseberg.Core;

public sealed record ScriptAnalysis(Token[] Tokens, ParseError[] Errors, IReadOnlyList<(int Start, int End)> Folds);

public static class EditorAnalysis
{
    public static (int Open, int Close)? MatchingBrace(string text, int caret)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(caret);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(caret, text.Length);
        var stack = new Stack<(TokenKind Kind, int Offset)>();
        var pairs = new List<(int Open, int Close)>();
        Parser.ParseInput(text, out var tokens, out _);
        foreach (var token in Flatten(tokens))
        {
            var kind = token.Kind switch
            {
                TokenKind.AtCurly => TokenKind.LCurly,
                TokenKind.AtParen or TokenKind.DollarParen => TokenKind.LParen,
                _ => token.Kind
            };
            if (kind is TokenKind.LCurly or TokenKind.LParen or TokenKind.LBracket)
                stack.Push((kind, token.Extent.EndOffset - 1));
            else if (kind is TokenKind.RCurly or TokenKind.RParen or TokenKind.RBracket)
            {
                var expected = kind switch
                {
                    TokenKind.RCurly => TokenKind.LCurly,
                    TokenKind.RParen => TokenKind.LParen,
                    _ => TokenKind.LBracket
                };
                if (stack.TryPeek(out var open) && open.Kind == expected)
                {
                    stack.Pop();
                    pairs.Add((open.Offset, token.Extent.StartOffset));
                }
                else stack.Clear();
            }
        }
        foreach (var offset in new[] { caret, caret - 1 })
            foreach (var pair in pairs)
                if (pair.Open == offset || pair.Close == offset) return pair;
        return null;
    }

    private static IEnumerable<Token> Flatten(IEnumerable<Token> tokens)
    {
        foreach (var token in tokens)
        {
            if (token is StringExpandableToken { NestedTokens: { } nested })
            {
                foreach (var child in Flatten(nested)) yield return child;
            }
            else yield return token;
        }
    }

    public static IReadOnlySet<CompletionResultType>? CompletionFilter(string text, int caret)
    {
        if (caret < 1 || caret > text.Length) return null;
        Parser.ParseInput(text, out var tokens, out _);
        var current = TokenAtCaret(tokens, caret);
        var inString = current?.Kind is TokenKind.StringLiteral or TokenKind.StringExpandable or TokenKind.HereStringLiteral or TokenKind.HereStringExpandable;
        if (current?.Kind == TokenKind.Comment) return null;
        var character = text[caret - 1];
        if (character is '\\' or '/' && (inString || current?.Kind is TokenKind.Generic or TokenKind.Identifier))
            return new HashSet<CompletionResultType> { CompletionResultType.ProviderItem, CompletionResultType.ProviderContainer };
        if (character == '$' && current?.Kind is TokenKind.StringExpandable or TokenKind.HereStringExpandable)
            return new HashSet<CompletionResultType> { CompletionResultType.Variable };
        if (inString) return null;
        return character switch
        {
            '$' => new HashSet<CompletionResultType> { CompletionResultType.Variable },
            '-' when current?.Kind is TokenKind.Parameter or TokenKind.Generic ||
                     current?.TokenFlags.HasFlag(TokenFlags.CommandName) == true =>
                new HashSet<CompletionResultType> { CompletionResultType.Command, CompletionResultType.ParameterName },
            ':' when caret >= 2 && text[caret - 2] == ':' =>
                new HashSet<CompletionResultType> { CompletionResultType.Method, CompletionResultType.Property },
            '.' when caret >= 2 && !char.IsWhiteSpace(text[caret - 2]) && current?.Kind != TokenKind.Number &&
                         tokens.LastOrDefault(t => t.Extent.EndOffset <= caret - 1)?.Kind != TokenKind.Number &&
                         text[caret - 2] is not ('*' or '?') =>
                new HashSet<CompletionResultType> { CompletionResultType.Method, CompletionResultType.Property, CompletionResultType.Type, CompletionResultType.Namespace },
            '[' => new HashSet<CompletionResultType> { CompletionResultType.Type, CompletionResultType.Namespace },
            ' ' when tokens.LastOrDefault(t => t.Extent.EndOffset <= caret - 1 && t.Kind != TokenKind.EndOfInput)?.Kind == TokenKind.Parameter =>
                new HashSet<CompletionResultType> { CompletionResultType.ParameterValue },
            _ => null
        };
    }

    private static Token? TokenAtCaret(IEnumerable<Token> tokens, int caret)
    {
        var token = tokens.FirstOrDefault(t => t.Extent.StartOffset < caret && t.Extent.EndOffset >= caret);
        if (token is StringExpandableToken { NestedTokens: { } nested })
            return TokenAtCaret(nested, caret) ?? token;
        return token;
    }

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
