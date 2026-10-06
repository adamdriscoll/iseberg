using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using Iseberg.Core;

namespace Iseberg;

/// <summary>The standalone desktop shell. Embed <see cref="WorkbenchControl"/> in custom hosts instead.</summary>
public sealed class MainWindow : Window
{
    private bool closeApproved;

    /// <summary>The reusable workbench displayed by this window.</summary>
    public WorkbenchControl Editor { get; }
    public WorkbenchModel Workbench => Editor.Workbench;
    public WorkbenchControl.IseObjectModel Scripting => Editor.Scripting;
    public static string ApplicationVersion => WorkbenchControl.ApplicationVersion;

    public MainWindow() : this([]) { }

    public MainWindow(string[] args, bool initializeOnOpen = true, UserSettings? preferences = null, string? settingsPath = null,
        ReleaseUpdateChecker? updateChecker = null, string? recoveryDirectory = null)
    {
        Title = "Iseberg - PowerShell 7 ISE";
        Width = 1180; Height = 780; MinWidth = 760; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIcon.Create();
        DesktopTheme.ApplyWindow(this);
        Editor = new(new WorkbenchOptions
        {
            EnablePersistence = true,
            SettingsPath = settingsPath ?? UserSettings.SettingsPath,
            Preferences = preferences,
            StartupFiles = args,
            EnableUpdateChecks = true,
            ShowErrorDialogs = true
        }, true, updateChecker, recoveryDirectory, settingsPath is not null);
        Content = Editor;
        DataContext = Editor.Workbench;
        Editor.AttachHostWindow(this);
        Dialogs.RegisterNames(this);
        Editor.CloseRequested += (_, _) => Close();
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Source == this) Editor.HandleHostKeyDown(args);
        }, RoutingStrategies.Tunnel);
        if (initializeOnOpen && !Design.IsDesignMode)
        {
            Opened += async (_, _) => await Editor.GuardAsync(Editor.InitializeAsync);
            Closing += OnClosing;
        }
        Closed += async (_, _) => await Editor.GuardAsync(async () => await Editor.DisposeAsync());
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (closeApproved) return;
        args.Cancel = true;
        await Editor.GuardAsync(async () =>
        {
            if (!await Editor.RequestCloseAsync()) return;
            closeApproved = true;
            Close();
        });
    }

    public Task OpenFileAsync(string path) => Editor.OpenFileAsync(path);
    public void OpenActivatedFiles(IEnumerable<string> paths) => Editor.OpenActivatedFiles(paths);
    public Task OpenRemoteFileAsync(string path) => Editor.OpenRemoteFileAsync(path);
    public Task<bool> ReloadFileAsync(ScriptTab tab, ScriptEncoding? encoding = null, bool confirm = true) => Editor.ReloadFileAsync(tab, encoding, confirm);
    public Task CheckExternalFilesAsync() => Editor.CheckExternalFilesAsync();
    public Task SaveWorkbenchAsync(bool released = false) => Editor.SaveWorkbenchAsync(released);
    public Task RestoreWorkbenchAsync() => Editor.RestoreWorkbenchAsync();
    public static void InsertSnippet(TextEditor editor, PowerShellSnippet snippet, string newLine) => WorkbenchControl.InsertSnippet(editor, snippet, newLine);
}
