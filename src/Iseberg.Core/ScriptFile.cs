using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace Iseberg.Core;

public sealed class ScriptFile : INotifyPropertyChanged
{
    private string text = "";
    private string savedText = "";
    private string? path;
    private Encoding encoding = new UTF8Encoding(false, true);
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
    public bool IsDirty => text != savedText;
    public string EncodingName => encoding.CodePage switch
    {
        65001 => encoding.GetPreamble().Length == 0 ? "UTF-8" : "UTF-8 BOM",
        1200 => "UTF-16 LE",
        1201 => "UTF-16 BE",
        _ => encoding.WebName
    };
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

    public static async Task<ScriptFile> OpenAsync(string filePath)
    {
        var bytes = await File.ReadAllBytesAsync(filePath);
        var (encoding, skip) = DetectEncoding(bytes);
        var file = new ScriptFile(System.IO.Path.GetFileName(filePath))
        {
            path = System.IO.Path.GetFullPath(filePath),
            encoding = encoding
        };
        file.text = file.savedText = encoding.GetString(bytes, skip, bytes.Length - skip);
        return file;
    }

    public async Task SaveAsync(string filePath)
    {
        if (IsRemote) throw new InvalidOperationException("Use the owning remote session to save this document.");
        var fullPath = System.IO.Path.GetFullPath(filePath);
        var snapshot = Text;
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(snapshot)).ToArray();
            await File.WriteAllBytesAsync(temporary, bytes);
            if (File.Exists(fullPath))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, File.GetUnixFileMode(fullPath));
                File.Replace(temporary, fullPath, null);
            }
            else
                File.Move(temporary, fullPath);
            path = fullPath;
            savedText = snapshot;
            Changed(nameof(Path));
            Changed(nameof(Name));
            Changed(nameof(Title));
            Changed(nameof(IsDirty));
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static ScriptFile FromRemoteBytes(string path, byte[] bytes, Guid runspaceId, string computerName)
    {
        var (encoding, skip) = DetectEncoding(bytes);
        var file = new ScriptFile(path.Split('\\', '/')[^1])
        {
            path = path, encoding = encoding, RemoteRunspaceId = runspaceId, RemoteComputerName = computerName
        };
        file.text = file.savedText = encoding.GetString(bytes, skip, bytes.Length - skip);
        return file;
    }

    internal byte[] Encode(string snapshot) => encoding.GetPreamble().Concat(encoding.GetBytes(snapshot)).ToArray();

    internal void MarkRemoteSaved(string savedPath, string snapshot, Guid runspaceId, string computerName)
    {
        path = savedPath;
        savedText = snapshot;
        RemoteRunspaceId = runspaceId;
        RemoteComputerName = computerName;
        Changed(nameof(Path)); Changed(nameof(Name)); Changed(nameof(Title)); Changed(nameof(IsDirty));
        Changed(nameof(IsRemote)); Changed(nameof(RemoteRunspaceId));
    }

    private static (Encoding Encoding, int Skip) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 })) return (new UTF32Encoding(false, true, true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff })) return (new UTF32Encoding(true, true, true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return (new UTF8Encoding(true, true), 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return (new UnicodeEncoding(false, true, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return (new UnicodeEncoding(true, true, true), 2);
        return (new UTF8Encoding(false, true), 0);
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
