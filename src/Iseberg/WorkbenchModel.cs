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
        if (Engine.State == SessionState.Ready && output.IsEmpty)
        {
            changed |= !Console.HasPrompt;
            Console.ShowPrompt(Engine.Prompt);
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

    public WorkbenchModel()
    {
        Sessions.CollectionChanged += (_, _) => Changed(nameof(HasMultipleSessions));
    }

    public SessionModel? SelectedSession
    {
        get => selectedSession;
        set { selectedSession = value; Changed(); }
    }
}
