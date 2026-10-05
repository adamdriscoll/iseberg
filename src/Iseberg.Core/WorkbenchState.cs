using System.Diagnostics;
using System.Text.Json;

namespace Iseberg.Core;

public sealed class WorkbenchState
{
    public int Version { get; set; } = 1;
    public int OwnerProcessId { get; set; }
    public DateTime OwnerStartedUtc { get; set; }
    public int SelectedSession { get; set; }
    public List<WorkbenchSessionState> Sessions { get; set; } = [];

    public void Validate()
    {
        if (Version != 1 || Sessions is null ||
            Sessions.Any(session => session is null || !session.Name.StartsWith("PowerShell ", StringComparison.Ordinal) ||
                !int.TryParse(session.Name["PowerShell ".Length..], out var number) || number <= 0 ||
                session.Documents is null || session.Documents.Any(document => document is null ||
                    string.IsNullOrWhiteSpace(document.Name) || document.CaretOffset < 0 || document.Encoding is null ||
                    document.Path is not null && !System.IO.Path.IsPathFullyQualified(document.Path))) ||
            Sessions.Select(session => session.Name).Distinct().Count() != Sessions.Count)
            throw new InvalidDataException("The saved workbench configuration is invalid or unsupported.");
        foreach (var document in Sessions.SelectMany(session => session.Documents))
            _ = document.Encoding.CreateEncoding();
    }

    public bool OwnerIsRunning()
    {
        if (OwnerProcessId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(OwnerProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime() == OwnerStartedUtc;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

public sealed class WorkbenchSessionState
{
    public string Name { get; set; } = "";
    public int SelectedDocument { get; set; }
    public bool ShowDebugger { get; set; }
    public List<WorkbenchDocumentState> Documents { get; set; } = [];
}

public sealed class WorkbenchDocumentState
{
    public Guid RecoveryId { get; set; }
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public ScriptEncoding Encoding { get; set; } = new(65001, false);
    public int CaretOffset { get; set; }
}

public sealed class WorkbenchStateStore(string? path = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iseberg", "workbench.json");

    public async Task<WorkbenchState?> LoadAsync()
    {
        if (!File.Exists(Path)) return null;
        var state = JsonSerializer.Deserialize<WorkbenchState>(await File.ReadAllTextAsync(Path))
            ?? throw new InvalidDataException("The saved workbench configuration is empty.");
        state.Validate();
        return state.OwnerIsRunning() ? null : state;
    }

    public async Task SaveAsync(WorkbenchState state)
    {
        state.Validate();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
                await JsonSerializer.SerializeAsync(stream, state, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, Path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

public sealed class WorkbenchGeometry
{
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 780;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool Maximized { get; set; }
    public double TopScriptRatio { get; set; } = 0.6;
    public double RightScriptRatio { get; set; } = 0.6;
    public double DebuggerWidth { get; set; } = 360;
    public double CommandsWidth { get; set; } = 290;

    public void Normalize()
    {
        WindowWidth = Clamp(WindowWidth, 760, 10000, 1180);
        WindowHeight = Clamp(WindowHeight, 500, 10000, 780);
        TopScriptRatio = Clamp(TopScriptRatio, 0.1, 0.9, 0.6);
        RightScriptRatio = Clamp(RightScriptRatio, 0.1, 0.9, 0.6);
        DebuggerWidth = Clamp(DebuggerWidth, 340, 2000, 360);
        CommandsWidth = Clamp(CommandsWidth, 150, 2000, 290);
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
