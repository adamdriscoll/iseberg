namespace Iseberg.Core;

public sealed class IseSnippetService(string? userDirectory = null)
{
    private readonly object sync = new();
    private readonly List<PowerShellSnippet> imported = [];
    public string UserDirectory { get; } = userDirectory ?? SnippetCatalog.UserDirectory;
    private IReadOnlyList<string> Directories => userDirectory is null ? SnippetCatalog.DefaultDirectories : [UserDirectory];

    public IEnumerable<FileInfo> GetUserFiles() => Directories.Where(Directory.Exists)
        .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        .Where(IsSnippetFile).Order(StringComparer.Ordinal).Select(path => new FileInfo(path));

    public void Create(string title, string description, string text, string author, int caretOffset, bool force)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title is "." or ".." || title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            title.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 || title.EndsWith('.') || title.EndsWith(' '))
            throw new ArgumentException("The snippet title must be a valid filename without directory separators.", nameof(title));
        if (caretOffset < 0 || caretOffset > text.Length) throw new ArgumentOutOfRangeException(nameof(caretOffset));
        SnippetCatalog.SaveAsync(Path.Combine(UserDirectory, title + ".snippets.ps1xml"),
            [new(title, description, author, text, caretOffset, Indent: false)], force).GetAwaiter().GetResult();
    }

    public void Import(string path, bool recurse)
    {
        var paths = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(IsSnippetFile).Order(StringComparer.Ordinal).ToArray()
            : File.Exists(path) && IsSnippetFile(path) ? [path] :
                throw new FileNotFoundException("Specify an existing snippet file or directory.", path);
        // Validate the whole import before publishing any entries.
        var snippets = paths.SelectMany(file => SnippetCatalog.Parse(File.ReadAllText(file))).ToArray();
        lock (sync)
            foreach (var snippet in snippets)
                if (!imported.Contains(snippet)) imported.Add(snippet);
    }

    public async Task<SnippetLoadResult> LoadAsync()
    {
        var result = await SnippetCatalog.LoadAsync(Directories);
        lock (sync) return result with { Snippets = result.Snippets.Concat(imported).Distinct().ToArray() };
    }

    private static bool IsSnippetFile(string path) => path.EndsWith(".snippets.ps1xml", StringComparison.OrdinalIgnoreCase);
}
