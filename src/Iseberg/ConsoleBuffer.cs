using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Iseberg.Core;

namespace Iseberg;

public sealed class ConsoleBuffer : IReadOnlySectionProvider
{
    public sealed record Span(int Start, int End, OutputKind Kind, int CodeStart = 0, ScriptAnalysis? Analysis = null);
    public TextDocument Document { get; } = new();
    public List<Span> Spans { get; } = [];
    public int TranscriptEnd { get; private set; }
    public int InputStart { get; private set; }
    public bool HasPrompt { get; private set; }
    private string draft = "";
    private string prompt = "";
    private string? analyzedInput;
    private ScriptAnalysis? inputAnalysis;
    public ScriptAnalysis InputAnalysis
    {
        get
        {
            var input = Input;
            if (inputAnalysis is null || analyzedInput != input)
            {
                analyzedInput = input;
                inputAnalysis = EditorAnalysis.Analyze(input);
            }
            return inputAnalysis;
        }
    }

    public string Input
    {
        get => HasPrompt ? Document.GetText(InputStart, Document.TextLength - InputStart) : draft;
        set
        {
            if (HasPrompt) Document.Replace(InputStart, Document.TextLength - InputStart, value);
            else draft = value;
        }
    }

    public bool CanInsert(int offset) => HasPrompt && offset >= InputStart && offset <= Document.TextLength;

    public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
    {
        var start = Math.Max(InputStart, segment.Offset);
        var end = Math.Min(Document.TextLength, segment.EndOffset);
        if (HasPrompt && end > start) yield return new SimpleSegment(start, end - start);
    }

    public void ShowPrompt(string value)
    {
        if (HasPrompt && prompt == value) return;
        var input = Input;
        Mutate(() =>
        {
            Document.Replace(TranscriptEnd, Document.TextLength - TranscriptEnd, value + input);
            prompt = value;
            InputStart = TranscriptEnd + value.Length;
            HasPrompt = true;
        });
    }

    public void HidePrompt()
    {
        if (!HasPrompt) return;
        draft = Input;
        Mutate(() =>
        {
            Document.Remove(TranscriptEnd, Document.TextLength - TranscriptEnd);
            HasPrompt = false;
            InputStart = TranscriptEnd;
        });
    }

    public void Append(OutputEntry entry) => AppendBatch([entry]);

    public void AppendBatch(IEnumerable<OutputEntry> entries)
    {
        Mutate(() =>
        {
            foreach (var entry in entries) AppendEntry(entry);
            Trim();
        });
    }

    private void AppendEntry(OutputEntry entry)
    {
        var start = TranscriptEnd;
        Document.Insert(start, entry.Text);
        TranscriptEnd += entry.Text.Length;
        InputStart += entry.Text.Length;
        if (entry.Kind != OutputKind.Command && Spans.Count > 0 && Spans[^1].Kind == entry.Kind && Spans[^1].End == start)
            Spans[^1] = Spans[^1] with { End = TranscriptEnd };
        else
            Spans.Add(new(start, TranscriptEnd, entry.Kind, start + entry.CodeStart,
                entry.Kind == OutputKind.Command ? EditorAnalysis.Analyze(entry.Text[entry.CodeStart..]) : null));
    }

    private void Trim()
    {
        if (TranscriptEnd <= 500_000) return;
        var line = Document.GetLineByOffset(TranscriptEnd - 400_000);
        var removed = line.Offset > 0 ? line.Offset : TranscriptEnd - 400_000;
        if (TranscriptEnd - removed > 500_000) removed = TranscriptEnd - 400_000;
        Document.Remove(0, removed);
        TranscriptEnd -= removed;
        InputStart -= removed;
        var retained = Spans.Where(s => s.End > removed).Select(s => s with
        {
            Start = Math.Max(0, s.Start - removed), End = s.End - removed, CodeStart = s.CodeStart - removed
        }).ToArray();
        Spans.Clear();
        Spans.AddRange(retained);
    }

    public void Clear()
    {
        var input = Input;
        Mutate(() =>
        {
            Document.Text = HasPrompt ? prompt + input : "";
            TranscriptEnd = 0;
            InputStart = HasPrompt ? prompt.Length : 0;
            Spans.Clear();
        });
    }

    private void Mutate(Action action)
    {
        Document.BeginUpdate();
        try { action(); }
        finally { Document.EndUpdate(); Document.UndoStack.ClearAll(); }
    }
}
