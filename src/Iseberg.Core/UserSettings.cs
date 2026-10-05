using System.Text.Json;

namespace Iseberg.Core;

public sealed class UserSettings
{
    public double Zoom { get; set; } = 100;
    public string Layout { get; set; } = "Top";
    public bool ShowCommands { get; set; } = true;
    public bool ShowLineNumbers { get; set; } = true;
    public bool WordWrap { get; set; }
    public bool LoadProfiles { get; set; }
    public bool ShowOutlining { get; set; } = true;
    public bool WarnDuplicateFiles { get; set; } = true;
    public bool PromptToSaveBeforeRun { get; set; } = true;
    public bool ConsoleIntelliSense { get; set; } = true;
    public bool ConsoleCompletionOnEnter { get; set; } = true;
    public bool ScriptIntelliSense { get; set; } = true;
    public bool ScriptCompletionOnEnter { get; set; } = true;
    public int IntelliSenseTimeoutSeconds { get; set; } = 3;
    public bool UseLocalHelp { get; set; } = true;
    public HelpViewSettings HelpView { get; set; } = new();
    public bool ShowToolbar { get; set; } = true;
    public bool UseDefaultSnippets { get; set; } = true;
    public int AutoSaveMinutes { get; set; } = 2;
    public int RecentFileCount { get; set; } = 10;
    public string FontFamily { get; set; } = "Lucida Console";
    public double FontSize { get; set; } = 9;
    public bool FixedWidthFontsOnly { get; set; }
    public EditorTheme Theme { get; set; } = new();
    public List<EditorTheme> CustomThemes { get; set; } = [];
    public List<string> RecentFiles { get; set; } = [];
    public List<DebuggerSessionSettings> DebuggerSessions { get; set; } = [];
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iseberg", "settings.json");

    public static async Task<UserSettings> LoadAsync(string? path = null)
    {
        path ??= SettingsPath;
        if (!File.Exists(path)) return new();
        var json = await File.ReadAllTextAsync(path);
        var settings = JsonSerializer.Deserialize<UserSettings>(json) ?? throw new InvalidDataException("The settings file is empty.");
        settings.Normalize();
        return settings;
    }

    public UserSettings Copy() => JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(this))!;

    public void Normalize()
    {
        Zoom = double.IsFinite(Zoom) ? Math.Clamp(Zoom, 20, 400) : 100;
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 6, 72) : 9;
        if (string.IsNullOrWhiteSpace(FontFamily)) FontFamily = "Lucida Console";
        AutoSaveMinutes = Math.Clamp(AutoSaveMinutes, 0, 120);
        RecentFileCount = Math.Clamp(RecentFileCount, 0, 100);
        IntelliSenseTimeoutSeconds = Math.Clamp(IntelliSenseTimeoutSeconds, 1, 30);
        Theme ??= new();
        Theme.Normalize();
        HelpView ??= new();
        HelpView.Normalize();
        CustomThemes ??= [];
        if (CustomThemes.Any(theme => theme is null)) throw new InvalidDataException("A saved theme is empty.");
        foreach (var theme in CustomThemes) theme.Normalize();
        RecentFiles ??= [];
        if (RecentFiles.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("A recent-file path is empty.");
        if (RecentFiles.Count > RecentFileCount) RecentFiles.RemoveRange(RecentFileCount, RecentFiles.Count - RecentFileCount);
        if (Layout is not ("Top" or "Right" or "Maximized")) Layout = "Top";
        DebuggerSessions ??= [];
        if (DebuggerSessions.Any(session => session is null || string.IsNullOrWhiteSpace(session.Name)) ||
            DebuggerSessions.DistinctBy(session => session.Name).Count() != DebuggerSessions.Count)
            throw new InvalidDataException("Saved debugger sessions must have unique names.");
        foreach (var session in DebuggerSessions)
        {
            if (session.Watches is null || session.Watches.Any(string.IsNullOrWhiteSpace) || session.Breakpoints is null)
                throw new InvalidDataException("A saved debugger configuration is invalid.");
            foreach (var breakpoint in session.Breakpoints)
            {
                if (breakpoint is null) throw new InvalidDataException("A saved breakpoint is empty.");
                try { breakpoint.Validate(); }
                catch (Exception exception) when (exception is ArgumentException or System.Management.Automation.ParseException)
                { throw new InvalidDataException("A saved breakpoint is invalid.", exception); }
            }
        }
    }

    public async Task SaveAsync(string? path = null)
    {
        path ??= SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class DebuggerSessionSettings
{
    public string Name { get; set; } = "";
    public List<string> Watches { get; set; } = [];
    public List<BreakpointSpec> Breakpoints { get; set; } = [];
}
