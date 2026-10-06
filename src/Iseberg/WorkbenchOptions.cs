using Iseberg.Core;

namespace Iseberg;

/// <summary>Construction-time configuration for an embedded <see cref="WorkbenchControl"/>.</summary>
public sealed record WorkbenchOptions
{
    /// <summary>Shows the workbench's built-in menu. Defaults to true.</summary>
    public bool ShowMenu { get; init; } = true;
    /// <summary>Allows the toolbar, subject to user preferences. Defaults to true.</summary>
    public bool ShowToolbar { get; init; } = true;
    /// <summary>Shows status, caret, encoding, and zoom controls. Defaults to true.</summary>
    public bool ShowStatusBar { get; init; } = true;
    /// <summary>Allows the session tab strip when multiple or remote sessions exist. Defaults to true.</summary>
    public bool ShowSessionTabs { get; init; } = true;
    /// <summary>Shows the script editor. At least one editor pane must be enabled.</summary>
    public bool ShowScriptPane { get; init; } = true;
    /// <summary>Shows the interactive console. At least one editor pane must be enabled.</summary>
    public bool ShowConsolePane { get; init; } = true;
    /// <summary>Allows the command explorer pane. Defaults to true.</summary>
    public bool EnableCommandsPane { get; init; } = true;
    /// <summary>Allows debugger inspection panes. Does not disable the PowerShell debugger.</summary>
    public bool EnableDebuggerPane { get; init; } = true;
    /// <summary>Creates an initial PowerShell session and untitled document during initialization.</summary>
    public bool CreateInitialSession { get; init; } = true;
    /// <summary>Enables settings, session metadata, and recovery persistence. Defaults to false.</summary>
    public bool EnablePersistence { get; init; }
    /// <summary>Absolute host-owned settings file path; required when persistence is enabled.</summary>
    public string? SettingsPath { get; init; }
    /// <summary>Optional snippet directory. When omitted, the engine's user snippet directory is used.</summary>
    public string? SnippetDirectory { get; init; }
    /// <summary>Enables Iseberg release checks, subject to user preferences. Defaults to false.</summary>
    public bool EnableUpdateChecks { get; init; }
    /// <summary>Displays modal dialogs for UI-operation failures in addition to the error event.</summary>
    public bool ShowErrorDialogs { get; init; }
    /// <summary>Initial preferences, copied at construction; persisted preferences take precedence.</summary>
    public UserSettings? Preferences { get; init; }
    /// <summary>Local scripts to open during initialization, copied at construction.</summary>
    public IReadOnlyList<string> StartupFiles { get; init; } = [];

    internal void Validate()
    {
        if (!ShowScriptPane && !ShowConsolePane)
            throw new ArgumentException("At least one editor pane must be visible.");
        if (EnablePersistence && (string.IsNullOrWhiteSpace(SettingsPath) || !Path.IsPathFullyQualified(SettingsPath)))
            throw new ArgumentException("Persistence requires an absolute host-owned SettingsPath.");
        if (!EnablePersistence && SettingsPath is not null)
            throw new ArgumentException("EnablePersistence must be true when SettingsPath is specified.");
        if (SnippetDirectory is not null && (string.IsNullOrWhiteSpace(SnippetDirectory) || !Path.IsPathFullyQualified(SnippetDirectory)))
            throw new ArgumentException("SnippetDirectory must be an absolute directory path.");
        ArgumentNullException.ThrowIfNull(StartupFiles);
        if (StartupFiles.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("StartupFiles cannot contain empty paths.");
        if (!CreateInitialSession && StartupFiles.Count > 0)
            throw new ArgumentException("StartupFiles requires CreateInitialSession.");
    }
}

/// <summary>A failure reported by a workbench UI action or automatic operation.</summary>
public sealed class WorkbenchErrorEventArgs(string title, Exception exception) : EventArgs
{
    /// <summary>The operation's user-facing error title.</summary>
    public string Title { get; } = title;
    /// <summary>The original failure, also logged through <see cref="System.Diagnostics.Trace"/>.</summary>
    public Exception Exception { get; } = exception;
}
