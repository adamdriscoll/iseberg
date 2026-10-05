using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Iseberg.Core;

public sealed class ScriptFile : INotifyPropertyChanged
{
    private string text = "";
    private string savedText = "";
    private string? path;
    private ScriptEncoding encodingChoice = ScriptEncoding.Choices[0];
    private ScriptEncoding savedEncodingChoice = ScriptEncoding.Choices[0];
    private bool detachedDraft;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string UntitledName { get; }
    public SortedSet<int> Breakpoints { get; } = [];
    public int CaretOffset { get; set; }
    public string? Path => path;
    public Guid? RemoteRunspaceId { get; private set; }
    public string? RemoteComputerName { get; private set; }
    public bool IsRemote => RemoteRunspaceId is not null;
    public string Name => path is null ? UntitledName : path.Split('\\', '/')[^1];
    public string Title => Name + (IsRemote ? $" [{RemoteComputerName}]" : "") + (IsDirty ? "*" : "");
    public bool IsDirty => detachedDraft || text != savedText || !encodingChoice.Matches(savedEncodingChoice);
    public ScriptEncoding EncodingChoice => encodingChoice;
    public ScriptEncoding SavedEncodingChoice => savedEncodingChoice;
    public string EncodingName => encodingChoice.DisplayName;
    public string? SavedVersion { get; private set; }
    public string Text
    {
        get => text;
        set
        {
            if (text == value) return;
            text = value;
            Changed();
            Changed(nameof(IsDirty));
            Changed(nameof(Title));
        }
    }

    public ScriptFile(string untitledName) => UntitledName = untitledName;

    public void SetEncoding(ScriptEncoding choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        choice.CreateEncoding().GetByteCount(text);
        if (encodingChoice.Matches(choice)) return;
        encodingChoice = choice;
        Changed(nameof(EncodingChoice));
        Changed(nameof(EncodingName));
        Changed(nameof(IsDirty));
        Changed(nameof(Title));
    }

    public static Task<ScriptFile> OpenAsync(string filePath) => OpenAsync(filePath, null);

    public static async Task<ScriptFile> OpenAsync(string filePath, ScriptEncoding? choice = null) =>
        FromBytes(filePath, await File.ReadAllBytesAsync(filePath), choice);

    public static ScriptFile FromBytes(string filePath, byte[] bytes, ScriptEncoding? choice = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var file = DecodeBytes(System.IO.Path.GetFileName(filePath), bytes, choice);
        file.path = System.IO.Path.GetFullPath(filePath);
        return file;
    }

    public static ScriptFile CreateUntitled(string name, ScriptEncoding? encoding = null)
    {
        encoding ??= ScriptEncoding.Choices[0];
        _ = encoding.CreateEncoding();
        return new ScriptFile(name) { encodingChoice = encoding, savedEncodingChoice = encoding };
    }

    public static ScriptFile FromRecovery(string name, string text, ScriptEncoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var file = new ScriptFile(name) { detachedDraft = true };
        if (encoding is not null) file.SetEncoding(encoding);
        file.Text = text;
        return file;
    }

    public static async Task<string?> ReadVersionAsync(string filePath)
    {
        await using var stream = OpenVersionStream(filePath);
        return stream is null ? null : Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    public async Task<bool> HasExternalChangesAsync()
    {
        if (IsRemote) throw new InvalidOperationException("Use the owning remote session to check this document.");
        return path is not null && !VersionsMatch(SavedVersion, await ReadVersionAsync(path));
    }

    public Task SaveAsync(string filePath)
    {
        var fullPath = System.IO.Path.GetFullPath(filePath);
        var samePath = path is not null && string.Equals(path, fullPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return SaveAsync(fullPath, samePath ? SavedVersion : null);
    }

    // A null expected version requires absence; a hash authorizes replacement of only those bytes.
    public async Task SaveAsync(string filePath, string? expectedVersion)
    {
        if (IsRemote) throw new InvalidOperationException("Use the owning remote session to save this document.");
        var fullPath = System.IO.Path.GetFullPath(filePath);
        var snapshot = Text;
        var snapshotEncoding = encodingChoice;
        var bytes = Encode(snapshot, snapshotEncoding);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            // Deny byte writers while checking/replacing, but allow our atomic rename on Windows.
            await using var current = OpenVersionStream(fullPath);
            var actualVersion = current is null ? null : Convert.ToHexString(await SHA256.HashDataAsync(current));
            if (!VersionsMatch(expectedVersion, actualVersion))
                throw new FileConflictException(fullPath, expectedVersion, actualVersion);
            if (current is not null)
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, File.GetUnixFileMode(fullPath));
                File.Replace(temporary, fullPath, null);
            }
            else
                File.Move(temporary, fullPath);
            path = fullPath;
            savedText = snapshot;
            savedEncodingChoice = snapshotEncoding;
            detachedDraft = false;
            SavedVersion = GetVersion(bytes);
            Changed(nameof(Path));
            Changed(nameof(Name));
            Changed(nameof(Title));
            Changed(nameof(IsDirty));
            Changed(nameof(SavedVersion));
            Changed(nameof(SavedEncodingChoice));
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static ScriptFile FromRemoteBytes(string path, byte[] bytes, Guid runspaceId, string computerName) =>
        FromRemoteBytes(path, bytes, runspaceId, computerName, null);

    public static ScriptFile FromRemoteBytes(string path, byte[] bytes, Guid runspaceId, string computerName,
        ScriptEncoding? choice = null)
    {
        var file = DecodeBytes(path.Split('\\', '/')[^1], bytes, choice);
        file.path = path;
        file.RemoteRunspaceId = runspaceId;
        file.RemoteComputerName = computerName;
        return file;
    }

    internal static byte[] Encode(string snapshot, ScriptEncoding choice)
    {
        var encoding = choice.CreateEncoding();
        return encoding.GetPreamble().Concat(encoding.GetBytes(snapshot)).ToArray();
    }

    internal void MarkRemoteSaved(string savedPath, string snapshot, ScriptEncoding snapshotEncoding,
        string version, Guid runspaceId, string computerName)
    {
        path = savedPath;
        savedText = snapshot;
        savedEncodingChoice = snapshotEncoding;
        detachedDraft = false;
        SavedVersion = version;
        RemoteRunspaceId = runspaceId;
        RemoteComputerName = computerName;
        Changed(nameof(Path)); Changed(nameof(Name)); Changed(nameof(Title)); Changed(nameof(IsDirty));
        Changed(nameof(IsRemote)); Changed(nameof(RemoteRunspaceId));
        Changed(nameof(SavedVersion));
        Changed(nameof(SavedEncodingChoice));
    }

    internal static string GetVersion(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static bool VersionsMatch(string? expected, string? actual) =>
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static FileStream? OpenVersionStream(string filePath)
    {
        try { return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static ScriptFile DecodeBytes(string name, byte[] bytes, ScriptEncoding? choice)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var (detected, skip) = DetectEncoding(bytes);
        choice ??= detected;
        if (choice.CodePage != detected.CodePage) skip = 0;
        var file = new ScriptFile(name)
        {
            encodingChoice = choice,
            savedEncodingChoice = choice,
            SavedVersion = GetVersion(bytes)
        };
        file.text = file.savedText = choice.CreateEncoding().GetString(bytes, skip, bytes.Length - skip);
        return file;
    }

    private static (ScriptEncoding Choice, int Skip) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 })) return (new(12000, true, "UTF-32 LE"), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff })) return (new(12001, true, "UTF-32 BE"), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return (new(65001, true, "UTF-8 BOM"), 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return (new(1200, true, "UTF-16 LE"), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return (new(1201, true, "UTF-16 BE"), 2);
        return (ScriptEncoding.Choices[0], 0);
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class FileConflictException(string filePath, string? expectedVersion, string? actualVersion)
    : IOException($"'{filePath}' changed, was deleted, or already exists. Check its contents before saving again.")
{
    public string FilePath { get; } = filePath;
    public string? ExpectedVersion { get; } = expectedVersion;
    public string? ActualVersion { get; } = actualVersion;
}
