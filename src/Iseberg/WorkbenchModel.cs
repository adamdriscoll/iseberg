using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AvaloniaEdit.Document;
using Iseberg.Core;

namespace Iseberg;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class ScriptTab : ObservableModel
{
    private readonly List<TextAnchor> breakpointAnchors = [];
    public ScriptFile File { get; }
    public TextDocument Document { get; }
    public ScriptTab(ScriptFile file)
    {
        File = file;
        Document = new(file.Text);
        Document.Changing += (_, change) =>
        {
            if (change.RemovalLength > 0 && Document.GetText(change.Offset, change.RemovalLength).Contains('\n'))
                breakpointAnchors.RemoveAll(anchor => !anchor.IsDeleted &&
                    anchor.Offset >= change.Offset && anchor.Offset < change.Offset + change.RemovalLength);
        };
        Document.TextChanged += (_, _) =>
        {
            File.Text = Document.Text;
            breakpointAnchors.RemoveAll(anchor => anchor.IsDeleted);
            File.Breakpoints.Clear();
            foreach (var anchor in breakpointAnchors)
                File.Breakpoints.Add(anchor.Line);
        };
    }

    public void ToggleBreakpoint(int line)
    {
        var existing = breakpointAnchors.FirstOrDefault(anchor => !anchor.IsDeleted && anchor.Line == line);
        if (existing is not null)
        {
            breakpointAnchors.Remove(existing);
            File.Breakpoints.Remove(line);
        }
        else
        {
            var anchor = Document.CreateAnchor(Document.GetLineByNumber(line).Offset);
            anchor.MovementType = AnchorMovementType.AfterInsertion;
            breakpointAnchors.Add(anchor);
            File.Breakpoints.Add(line);
        }
    }

    public void ClearBreakpoints()
    {
        breakpointAnchors.Clear();
        File.Breakpoints.Clear();
    }
}

public sealed class SessionModel : ObservableModel
{
    private ScriptTab? selectedFile;
    private readonly ConcurrentQueue<OutputEntry> output = new();
    public string Name { get; }
    public PowerShellSession Engine { get; } = new();
    public ObservableCollection<ScriptTab> Files { get; } = [];
    public TextDocument ConsoleDocument { get; } = new();
    public List<(int Start, int End, OutputKind Kind)> OutputSpans { get; } = [];
    public List<string> History { get; } = [];
    public int HistoryIndex { get; set; }
    public string DraftInput { get; set; } = "";
    public string Input { get; set; } = "";
    public ConsoleCompletion? Completion { get; set; }
    public IReadOnlyList<CommandDescription> Commands { get; set; } = [];
    public DebugLocation? DebugLocation { get; set; }
    public ScriptTab? SelectedFile
    {
        get => selectedFile;
        set { selectedFile = value; Changed(); }
    }

    public sealed record ConsoleCompletion(string Original, string LastText, int LastCaret, int Index, CompletionSet Results);

    public SessionModel(string name)
    {
        Name = name;
        Engine.Output += output.Enqueue;
    }

    public bool FlushOutput()
    {
        if (output.IsEmpty) return false;
        ConsoleDocument.BeginUpdate();
        try
        {
            var count = 0;
            while (count++ < 2000 && output.TryDequeue(out var entry))
            {
                var start = ConsoleDocument.TextLength;
                ConsoleDocument.Insert(start, entry.Text);
                if (OutputSpans.Count > 0 && OutputSpans[^1].Kind == entry.Kind && OutputSpans[^1].End == start)
                    OutputSpans[^1] = (OutputSpans[^1].Start, ConsoleDocument.TextLength, entry.Kind);
                else
                    OutputSpans.Add((start, ConsoleDocument.TextLength, entry.Kind));
            }
            if (ConsoleDocument.TextLength > 500_000)
            {
                var removed = ConsoleDocument.GetLineByOffset(ConsoleDocument.TextLength - 400_000).Offset;
                ConsoleDocument.Remove(0, removed);
                var spans = OutputSpans.Where(s => s.End > removed)
                    .Select(s => (Math.Max(0, s.Start - removed), s.End - removed, s.Kind)).ToArray();
                OutputSpans.Clear();
                OutputSpans.AddRange(spans);
            }
        }
        finally { ConsoleDocument.EndUpdate(); }
        return true;
    }

    public void ClearOutput()
    {
        while (output.TryDequeue(out _)) { }
        ConsoleDocument.Text = "";
        OutputSpans.Clear();
    }
}

public sealed class WorkbenchModel : ObservableModel
{
    private SessionModel? selectedSession;
    public ObservableCollection<SessionModel> Sessions { get; } = [];
    public SessionModel? SelectedSession
    {
        get => selectedSession;
        set { selectedSession = value; Changed(); }
    }
}
