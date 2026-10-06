using System.Collections.Immutable;

namespace Iseberg.Editor;

/// <summary>A zero-based UTF-16 offset and length, not a byte or Unicode scalar range.</summary>
public readonly record struct EditorTextSpan
{
    public int Start { get; }
    public int Length { get; }
    public int End => checked(Start + Length);

    public EditorTextSpan(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, int.MaxValue - start);
        Start = start;
        Length = length;
    }

    internal void Validate(int textLength)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(End, textLength);
    }
}

public enum EditorTextScope { Document, SelectionOrCurrentLine }
public enum EditorDiagnosticSeverity { Information, Warning, Error }
public enum EditorAnalysisState { Unavailable, Pending, Available, Failed }
public enum EditorExecutionRequestKind { Document, SelectionOrCurrentLine, Stop }

/// <summary>Immutable captured text. Span locates it in the document at Version.</summary>
public sealed record EditorTextSnapshot(string Text, long Version, EditorTextSpan Span);
public sealed record EditorDiagnostic(string Code, string Message, EditorDiagnosticSeverity Severity, EditorTextSpan Span);
public sealed record EditorAnalysisRequest(string Text, long Version);
public sealed record EditorAnalysisResult(long Version, EditorAnalysisState State, ImmutableArray<EditorDiagnostic> Diagnostics);
public sealed record EditorCompletionRequest(string Text, long Version, int CaretOffset);
public sealed record EditorCompletionItem(string Text, string DisplayText, string? Description = null);
public sealed record EditorCompletionList(long Version, EditorTextSpan ReplacementSpan, ImmutableArray<EditorCompletionItem> Items);

/// <summary>Host-supplied completion only. No implicit engine or session fallback.</summary>
public interface IEditorCompletionProvider
{
    Task<EditorCompletionList> CompleteAsync(EditorCompletionRequest request, CancellationToken cancellationToken);
}

/// <summary>Explicit host diagnostics. The editor has no built-in parser or engine fallback.</summary>
public interface IEditorAnalysisProvider
{
    Task<EditorAnalysisResult> AnalyzeAsync(EditorAnalysisRequest request, CancellationToken cancellationToken);
}

public sealed class EditorExecutionRequestedEventArgs(EditorExecutionRequestKind kind, EditorTextSnapshot? snapshot) : EventArgs
{
    public EditorExecutionRequestKind Kind { get; } = kind;
    /// <summary>Null for Stop: the host, not this editor, owns the invocation.</summary>
    public EditorTextSnapshot? Snapshot { get; } = snapshot;
}

public sealed class EditorErrorEventArgs(string operation, Exception exception) : EventArgs
{
    public string Operation { get; } = operation;
    public Exception Exception { get; } = exception;
}
