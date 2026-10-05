using System.Text.Json;

namespace Iseberg.Core;

public sealed record RecoveredScript(Guid Id, string Name, string? Path, string Text, int OwnerProcessId = 0,
    DateTime OwnerStartedUtc = default, ScriptEncoding? Encoding = null, string? SessionName = null);

public sealed class ScriptRecovery(string? directory = null)
{
    private readonly string directory = directory ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iseberg", "Recovery");
    private readonly int ownerProcessId = Environment.ProcessId;
    private readonly DateTime ownerStartedUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public async Task SaveAsync(Guid id, ScriptFile file, string? sessionName = null)
    {
        if (!file.IsDirty) { Remove(id); return; }
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var path = SnapshotPath(id);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
                await JsonSerializer.SerializeAsync(stream, new RecoveredScript(id, file.Name, file.Path, file.Text,
                    ownerProcessId, ownerStartedUtc, file.EncodingChoice, sessionName));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public async Task<IReadOnlyList<RecoveredScript>> ReadAsync()
    {
        if (!Directory.Exists(directory)) return [];
        var scripts = new List<RecoveredScript>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order())
        {
            var script = JsonSerializer.Deserialize<RecoveredScript>(await File.ReadAllTextAsync(path))
                ?? throw new InvalidDataException($"Empty recovery file: {path}");
            if (script.Id == Guid.Empty || string.IsNullOrWhiteSpace(script.Name) || script.Text is null)
                throw new InvalidDataException($"Invalid recovery file: {path}");
            if (script.Encoding is not null) _ = script.Encoding.CreateEncoding();
            if (!OwnerIsRunning(script)) scripts.Add(script);
        }
        return scripts;
    }

    public void Remove(Guid id)
    {
        try { File.Delete(SnapshotPath(id)); }
        catch (DirectoryNotFoundException)
        {
            // No snapshot remains when autosave never created the directory or it was removed.
        }
    }
    private static bool OwnerIsRunning(RecoveredScript script)
    {
        if (script.OwnerProcessId <= 0) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(script.OwnerProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime() == script.OwnerStartedUtc;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    private string SnapshotPath(Guid id) => System.IO.Path.Combine(directory, id.ToString("N") + ".json");
}
