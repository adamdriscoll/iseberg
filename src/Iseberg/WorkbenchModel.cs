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
    public Guid RecoveryId { get; } = Guid.NewGuid();
    private readonly List<(TextAnchor Anchor, BreakpointSpec Spec)> breakpointAnchors = [];
    public ScriptFile File { get; private set; }
    public TextDocument Document { get; }
    public ScriptTab(ScriptFile file)
    {
        File = file;
        Document = new(file.Text);
        Document.Changing += (_, change) =>
        {
            if (change.RemovalLength > 0 && Document.GetText(change.Offset, change.RemovalLength).Contains('\n'))
                breakpointAnchors.RemoveAll(entry => !entry.Anchor.IsDeleted &&
                    entry.Anchor.Offset >= change.Offset && entry.Anchor.Offset < change.Offset + change.RemovalLength);
        };
        Document.TextChanged += (_, _) =>
        {
            File.Text = Document.Text;
            breakpointAnchors.RemoveAll(entry => entry.Anchor.IsDeleted);
            File.Breakpoints.Clear();
            foreach (var entry in breakpointAnchors)
                File.Breakpoints.Add(entry.Anchor.Line);
        };
    }

    public void ToggleBreakpoint(int line)
    {
        var existing = breakpointAnchors.FindIndex(entry => !entry.Anchor.IsDeleted && entry.Anchor.Line == line);
        if (existing >= 0)
        {
            breakpointAnchors.RemoveAt(existing);
            File.Breakpoints.Remove(line);
        }
        else
        {
            SetBreakpoint(new(BreakpointKind.Line, File.Path, Line: line));
        }
    }

    public IReadOnlyList<BreakpointSpec> LineBreakpoints => breakpointAnchors.Where(entry => !entry.Anchor.IsDeleted)
        .Select(entry => entry.Spec with { ScriptPath = File.Path, Line = entry.Anchor.Line }).DistinctBy(spec => spec.Line).ToArray();

    public void SetBreakpoint(BreakpointSpec spec)
    {
        if (spec.Kind != BreakpointKind.Line || spec.Line < 1 || spec.Line > Document.LineCount)
            throw new ArgumentException("The breakpoint must refer to a line in this document.");
        breakpointAnchors.RemoveAll(entry => !entry.Anchor.IsDeleted && entry.Anchor.Line == spec.Line);
        var anchor = Document.CreateAnchor(Document.GetLineByNumber(spec.Line).Offset);
        anchor.MovementType = AnchorMovementType.AfterInsertion;
        breakpointAnchors.Add((anchor, spec));
        File.Breakpoints.Add(spec.Line);
    }

    public void ReplaceBreakpoints(IEnumerable<BreakpointSpec> specs)
    {
        ClearBreakpoints();
        foreach (var spec in specs.Where(spec => spec.Line > 0 && spec.Line <= Document.LineCount)) SetBreakpoint(spec);
    }

    public void AcknowledgeBreakpointLines()
    {
        for (var index = 0; index < breakpointAnchors.Count; index++)
        {
            var entry = breakpointAnchors[index];
            if (!entry.Anchor.IsDeleted)
                breakpointAnchors[index] = (entry.Anchor, entry.Spec with { Line = entry.Anchor.Line });
        }
    }

    public void ApplyBreakpointChange(BreakpointSpec previous, BreakpointSpec? replacement)
    {
        var index = breakpointAnchors.FindIndex(entry => !entry.Anchor.IsDeleted && entry.Spec.Line == previous.Line);
        if (index < 0) index = breakpointAnchors.FindIndex(entry => !entry.Anchor.IsDeleted && entry.Anchor.Line == previous.Line);
        if (index >= 0)
        {
            var entry = breakpointAnchors[index];
            if (replacement is not null && replacement.Line == previous.Line)
            {
                breakpointAnchors[index] = (entry.Anchor, replacement);
                return;
            }
            File.Breakpoints.Remove(entry.Anchor.Line);
            breakpointAnchors.RemoveAt(index);
        }
        if (replacement is { Line: > 0 } && replacement.Line <= Document.LineCount) SetBreakpoint(replacement);
    }

    public void ClearBreakpoints()
    {
        breakpointAnchors.Clear();
        File.Breakpoints.Clear();
    }

    public void Reload(ScriptFile file)
    {
        var specs = LineBreakpoints;
        var caret = File.CaretOffset;
        File = file;
        Document.Text = file.Text;
        Document.UndoStack.ClearAll();
        File.CaretOffset = Math.Min(caret, Document.TextLength);
        ReplaceBreakpoints(specs.Select(spec => spec with { ScriptPath = file.Path }));
        Changed(nameof(File));
    }
}

public sealed class SessionModel : ObservableModel
{
    private ScriptTab? selectedFile;
    private readonly ConcurrentQueue<OutputEntry> output = new();
    public string Name { get; }
    public string DisplayName => Engine.IsRemote ? $"{Name} [{Engine.RemoteComputerName}]" : Name;
    internal void RefreshRunspaceIdentity() => Changed(nameof(DisplayName));
    public PowerShellSession Engine { get; } = new();
    public ObservableCollection<ScriptTab> Files { get; } = [];
    public ConsoleBuffer Console { get; } = new();
    public TextDocument ConsoleDocument => Console.Document;
    public List<ConsoleBuffer.Span> OutputSpans => Console.Spans;
    public List<string> History { get; } = [];
    public int HistoryIndex { get; set; }
    public string DraftInput { get; set; } = "";
    public string Input { get => Console.Input; set => Console.Input = value; }
    public int ConsoleCaretOffset { get; set; }
    public ConsoleCompletion? Completion { get; set; }
    public IReadOnlyList<CommandDescription> Commands { get; set; } = [];
    public string? SelectedCommand { get; set; }
    public Dictionary<string, CommandForm> CommandForms { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DebugLocation? DebugLocation { get; set; }
    public List<string> Watches { get; } = [];
    public DebugSnapshot? DebugSnapshot { get; set; }
    public int SelectedDebugFrame { get; set; }
    public bool EditingBreakpoints { get; set; }
    public IReadOnlyList<DebugBreakpoint> Breakpoints { get; set; } = [];
    public bool DebuggerPaneVisible { get; set; }
    internal int DebugRevisionCounter;
    internal SemaphoreSlim DebugRefreshGate { get; } = new(1, 1);
    public bool Evaluating { get; set; }
    internal bool PendingRunspaceRefresh { get; set; }
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
        var changed = !output.IsEmpty;
        var batch = new List<OutputEntry>();
        while (batch.Count < 2000 && output.TryDequeue(out var entry)) batch.Add(entry);
        if (batch.Count > 0) Console.AppendBatch(batch);
        if ((Engine.State == SessionState.Ready || Engine.IsDebuggerPaused || Engine.IsNestedPromptActive) && !Evaluating && output.IsEmpty)
        {
            changed |= Console.CompleteOutput();
            changed |= !Console.HasPrompt;
            Console.ShowPrompt(Engine.IsNestedPromptActive ? Engine.NestedPrompt :
                Engine.State == SessionState.Debugging ? Engine.DebugPrompt : Engine.Prompt);
        }
        return changed;
    }

    public void ClearOutput()
    {
        while (output.TryDequeue(out _)) { }
        Console.Clear();
    }
}

public sealed class WorkbenchModel : ObservableModel
{
    private SessionModel? selectedSession;
    public ObservableCollection<SessionModel> Sessions { get; } = [];
    public bool HasMultipleSessions => Sessions.Count > 1;
    public bool ShowSessionTabs => HasMultipleSessions || Sessions.Any(session => session.Engine.IsRemote);

    public WorkbenchModel()
    {
        Sessions.CollectionChanged += (_, change) =>
        {
            foreach (var session in change.OldItems?.OfType<SessionModel>() ?? []) session.PropertyChanged -= OnSessionPropertyChanged;
            foreach (var session in change.NewItems?.OfType<SessionModel>() ?? []) session.PropertyChanged += OnSessionPropertyChanged;
            Changed(nameof(HasMultipleSessions));
            Changed(nameof(ShowSessionTabs));
        };
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SessionModel.DisplayName)) Changed(nameof(ShowSessionTabs));
    }

    public SessionModel? SelectedSession
    {
        get => selectedSession;
        set { selectedSession = value; Changed(); }
    }
}
