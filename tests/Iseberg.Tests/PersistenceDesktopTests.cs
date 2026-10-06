using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AvaloniaEdit;
using System.Text.Json;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class PersistenceDesktopTests
{
    [AvaloniaFact]
    public async Task ClosingDuringNestedPromptRequiresConsentAndCancelRestoresEditing()
    {
        var settingsPath = TempPath();
        var window = new MainWindow([], settingsPath: settingsPath);
        try
        {
            await new UserSettings { LoadProfiles = false, AutoSaveMinutes = 0 }.SaveAsync(settingsPath);
            Layout(window);
            await WaitFor(() => File.Exists(settingsPath + ".workbench.json") &&
                window.Workbench.SelectedSession?.Engine.State == SessionState.Ready);
            var session = window.Workbench.SelectedSession!;
            var execution = session.Engine.ExecuteAsync("$Host.EnterNestedPrompt(); 'returned'");
            var editor = window.FindEditor("ConsoleEditor")!;
            await WaitFor(() => session.Engine.IsNestedPromptActive && !editor.IsReadOnly);
            window.Close();
            await WaitFor(() => window.OwnedWindows.Any());
            Assert.True(editor.IsReadOnly);
            ClickChoice(window, "Cancel");
            await WaitFor(() => !window.OwnedWindows.Any() && !editor.IsReadOnly);
            Assert.True(window.IsVisible);
            Assert.True(session.Engine.IsNestedPromptActive);
            Assert.False(execution.IsCompleted);
            window.Close();
            await WaitFor(() => window.OwnedWindows.Any());
            ClickChoice(window, "Stop");
            await WaitFor(() => !window.IsVisible);
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(SessionState.Disposed, session.Engine.State);
        }
        finally
        {
            foreach (var session in window.Workbench.Sessions)
                if (session.Engine.State != SessionState.Disposed) await session.Engine.DisposeAsync();
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            File.Delete(settingsPath);
            File.Delete(settingsPath + ".workbench.json");
        }
    }

    [AvaloniaFact]
    public async Task RecoveryUsesTheOwningRestoredTabWithoutReplacingDiskAndCleanExitKeepsTheWorkbench()
    {
        var settingsPath = TempPath();
        var scriptPath = TempPath();
        var recoveryDirectory = settingsPath + ".recovery";
        var id = Guid.NewGuid();
        var recoveryPath = Path.Combine(recoveryDirectory, id.ToString("N") + ".json");
        MainWindow? window = null;
        try
        {
            await File.WriteAllTextAsync(scriptPath, "original disk text");
            await new UserSettings { AutoSaveMinutes = 0 }.SaveAsync(settingsPath);
            Directory.CreateDirectory(recoveryDirectory);
            await File.WriteAllTextAsync(recoveryPath, JsonSerializer.Serialize(new RecoveredScript(
                id, Path.GetFileName(scriptPath), scriptPath, "", Encoding: new(1252, false), SessionName: "PowerShell 3")));
            await new WorkbenchStateStore(settingsPath + ".workbench.json").SaveAsync(new()
            {
                Sessions = [new()
                {
                    Name = "PowerShell 3", Documents = [new()
                    {
                        RecoveryId = id, Name = Path.GetFileName(scriptPath), Path = scriptPath
                    }]
                }]
            });
            window = new MainWindow([], settingsPath: settingsPath, recoveryDirectory: recoveryDirectory);
            Layout(window);
            await WaitFor(() => window.OwnedWindows.Any());
            ClickChoice(window, "Recover");
            await WaitFor(() => window.Workbench.SelectedSession?.Files.Count == 2 &&
                !window.OwnedWindows.Any() && !File.Exists(recoveryPath));
            var session = window.Workbench.SelectedSession!;
            var recovered = session.SelectedFile!;
            Assert.Equal("PowerShell 3", session.Name);
            Assert.Null(recovered.File.Path);
            Assert.True(recovered.File.IsDirty);
            Assert.Equal("", recovered.Document.Text);
            Assert.Equal(1252, recovered.File.EncodingChoice.CodePage);
            Assert.Equal("original disk text", session.Files[0].Document.Text);
            Assert.Equal("original disk text", await File.ReadAllTextAsync(scriptPath));
            Assert.False(File.Exists(recoveryPath));
            window.Close();
            await WaitFor(() => window.OwnedWindows.Any());
            ClickChoice(window, "Cancel");
            await WaitFor(() => !window.OwnedWindows.Any() && !window.FindEditor("ScriptEditor")!.IsReadOnly);
            Assert.True(window.IsVisible);
            Assert.Single(window.Workbench.Sessions);
            window.Close();
            await WaitFor(() => window.OwnedWindows.Any());
            var failures = new List<WorkbenchErrorEventArgs>();
            window.Editor.ErrorOccurred += (_, failure) => failures.Add(failure);
            ClickChoice(window, "Don't Save");
            await WaitFor(() => !window.IsVisible || failures.Count > 0);
            Assert.True(failures.Count == 0, string.Join("\n", failures.Select(failure => failure.Exception.ToString())));
            var saved = await new WorkbenchStateStore(settingsPath + ".workbench.json").LoadAsync();
            Assert.NotNull(saved);
            Assert.Equal("PowerShell 3", saved.Sessions.Single().Name);
            Assert.Equal(2, saved.Sessions.Single().Documents.Count);
            Assert.Equal(scriptPath, saved.Sessions.Single().Documents[0].Path);
            Assert.Empty(Directory.EnumerateFiles(recoveryDirectory));
        }
        finally
        {
            if (window is not null)
            {
                foreach (var session in window.Workbench.Sessions)
                    if (session.Engine.State != SessionState.Disposed) await session.Engine.DisposeAsync();
                foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
                window.Close();
            }
            File.Delete(settingsPath);
            File.Delete(settingsPath + ".workbench.json");
            File.Delete(scriptPath);
            if (Directory.Exists(recoveryDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(recoveryDirectory)) File.Delete(file);
                Directory.Delete(recoveryDirectory);
            }
        }
    }

    [AvaloniaFact]
    public async Task RestartRestoresOrderedTabsSelectionsCaretEncodingAndDebuggerConfigurationIntoFreshRunspaces()
    {
        var settingsPath = TempPath();
        var scriptPath = TempPath();
        var first = new MainWindow([], false, settingsPath: settingsPath);
        var second = new MainWindow([], false, settingsPath: settingsPath);
        var original = new SessionModel("PowerShell 1");
        var another = new SessionModel("PowerShell 3");
        try
        {
            await File.WriteAllTextAsync(scriptPath, "'disk version'\r\n");
            await original.Engine.InitializeAsync();
            await another.Engine.InitializeAsync();
            await another.Engine.ExecuteAsync("$privateRestartValue = 42");
            first.Workbench.Sessions.Add(original);
            first.Workbench.Sessions.Add(another);
            first.Workbench.SelectedSession = another;
            Layout(first);
            await first.OpenFileAsync(scriptPath);
            var saved = another.SelectedFile!;
            saved.File.Text = "edits must not enter session metadata";
            saved.Document.Text = saved.File.Text;
            saved.File.SetEncoding(new(1200, true));
            var editor = first.FindEditor("ScriptEditor")!;
            editor.CaretOffset = 6;
            another.DebuggerPaneVisible = true;
            original.Files.Add(new(ScriptFile.CreateUntitled("Untitled7.ps1", new(1252, false))));
            original.SelectedFile = original.Files[0];
            await first.SaveWorkbenchAsync(released: true);
            await new UserSettings
            {
                DebuggerSessions = [new() { Name = "PowerShell 3", Watches = ["$privateRestartValue"],
                    Breakpoints = [new(BreakpointKind.Command, Target: "Get-Process", Enabled: false)] }]
            }.SaveAsync(settingsPath);
            second.Close();
            second = new MainWindow([], false, await UserSettings.LoadAsync(settingsPath), settingsPath);
            Layout(second);
            await second.RestoreWorkbenchAsync();
            Assert.Equal(["PowerShell 1", "PowerShell 3"], second.Workbench.Sessions.Select(session => session.Name));
            var restored = second.Workbench.SelectedSession!;
            Assert.Equal("PowerShell 3", restored.Name);
            Assert.Equal("'disk version'\r\n", restored.SelectedFile!.Document.Text);
            Assert.False(restored.SelectedFile.File.IsDirty);
            Assert.Equal(65001, restored.SelectedFile.File.EncodingChoice.CodePage);
            Assert.Equal(6, second.FindEditor("ScriptEditor")!.CaretOffset);
            Assert.True(restored.DebuggerPaneVisible);
            Assert.Equal("$privateRestartValue", restored.Watches.Single());
            Assert.False(restored.Breakpoints.Single().Spec.Enabled);
            Assert.Null(restored.DebugSnapshot);
            Assert.NotEqual(another.Engine.RunspaceId, restored.Engine.RunspaceId);
            var entries = new List<OutputEntry>();
            restored.Engine.Output += entries.Add;
            await restored.Engine.ExecuteAsync("Test-Path variable:privateRestartValue");
            Assert.Contains(entries, entry => entry.Text.Contains("False"));
            var input = restored.Engine.ExecuteAsync("Read-Host 'restored input'");
            await WaitFor(() => second.OwnedWindows.Any());
            var dialog = Assert.IsType<HostInputWindow>(Assert.Single(second.OwnedWindows));
            dialog.FindControl<TextBox>("HostInput")!.Text = "restored response";
            dialog.FindControl<Button>("HostInputSubmit")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await input.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(entries, entry => entry.Text.Contains("restored response"));
            Assert.Equal("Untitled7.ps1", second.Workbench.Sessions[0].SelectedFile!.File.Name);
            Assert.Equal(1252, second.Workbench.Sessions[0].SelectedFile!.File.EncodingChoice.CodePage);
            Assert.False(second.Workbench.Sessions[0].SelectedFile!.File.IsDirty);
        }
        finally
        {
            await original.Engine.DisposeAsync();
            await another.Engine.DisposeAsync();
            foreach (var session in second.Workbench.Sessions) await session.Engine.DisposeAsync();
            first.Close();
            second.Close();
            File.Delete(settingsPath);
            File.Delete(settingsPath + ".workbench.json");
            File.Delete(scriptPath);
        }
    }

    [AvaloniaFact]
    public async Task ExternalChangeKeepEditsDoesNotAuthorizeOverwriteAndReloadClearsDirtyState()
    {
        var path = TempPath();
        var window = new MainWindow([], false);
        var session = new SessionModel("PowerShell 1");
        try
        {
            await File.WriteAllTextAsync(path, "original");
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            Layout(window);
            await window.OpenFileAsync(path);
            var tab = session.SelectedFile!;
            tab.Document.Text = "my edits";
            await File.WriteAllTextAsync(path, "external");
            var check = window.CheckExternalFilesAsync();
            await WaitFor(() => window.OwnedWindows.Any());
            ClickChoice(window, UiText.Get("KeepEdits"));
            await check;
            Assert.Equal("my edits", tab.Document.Text);
            Assert.True(tab.File.IsDirty);
            await Assert.ThrowsAsync<FileConflictException>(() => tab.File.SaveAsync(path));
            var reload = window.ReloadFileAsync(tab);
            await WaitFor(() => window.OwnedWindows.Any());
            ClickChoice(window, UiText.Get("ReloadFile"));
            Assert.True(await reload);
            Assert.Equal("external", tab.Document.Text);
            Assert.False(tab.File.IsDirty);
            Assert.False(tab.Document.UndoStack.CanUndo);
            Assert.Equal("external", await File.ReadAllTextAsync(path));
        }
        finally
        {
            await session.Engine.DisposeAsync();
            window.Close();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task EncodingPickerReturnsExplicitReloadOrConversionAndCancelHasNoEffect()
    {
        var owner = new MainWindow([], false);
        Layout(owner);
        try
        {
            var dialog = new EncodingWindow(new(65001, false), true);
            var result = dialog.ShowDialog<EncodingSelection?>(owner);
            dialog.FindControl<ComboBox>("FileEncoding")!.SelectedIndex = 1;
            dialog.FindControl<Button>("ReloadEncoding")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var selected = await result;
            Assert.NotNull(selected);
            Assert.True(selected.Reload);
            dialog = new EncodingWindow(new(65001, false), false);
            result = dialog.ShowDialog<EncodingSelection?>(owner);
            Assert.False(dialog.FindControl<Button>("ReloadEncoding")!.IsEnabled);
            dialog.FindControl<Button>("Cancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(await result);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public void ImportedThemeIsADraftUntilAppliedAndDefaultsKeepGeometry()
    {
        var original = new UserSettings { Geometry = new() { WindowWidth = 1400, TopScriptRatio = 0.75 } };
        var window = new OptionsWindow(original, _ => Task.CompletedTask);
        try
        {
            Layout(window);
            var theme = new ThemeFile { FontFamily = "Consolas", FontSize = 13, Theme = new() { Name = "Imported" } };
            theme.Theme.Colors["Script.Background"] = "#112233";
            window.ApplyImportedTheme(theme);
            Assert.Equal("#112233", window.Draft.Theme.Colors["Script.Background"]);
            Assert.Equal(13, window.Draft.FontSize);
            Assert.Equal("Imported", window.Draft.CustomThemes.Single().Name);
            Assert.Equal("#FFFFFF", original.Theme.Colors["Script.Background"]);
            window.FindControl<Button>("RestoreDefaults")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1400, window.Draft.Geometry.WindowWidth);
            Assert.Equal(0.75, window.Draft.Geometry.TopScriptRatio);
            Assert.Equal("Imported", window.Draft.CustomThemes.Single().Name);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void WindowAndPaneGeometryAreAppliedOnConstruction()
    {
        var window = new MainWindow([], false, new()
        {
            ShowCommands = true, Geometry = new()
            {
                WindowWidth = 1400, WindowHeight = 900, TopScriptRatio = 0.75, CommandsWidth = 330
            }
        });
        try
        {
            Layout(window);
            Assert.Equal(1400, window.Width);
            Assert.Equal(900, window.Height);
            var grid = window.FindControl<Grid>("PaneGrid")!;
            Assert.Equal(0.75, grid.RowDefinitions[0].Height.Value);
            Assert.Equal(0.25, grid.RowDefinitions[2].Height.Value);
            var outer = window.FindControl<Grid>("OuterGrid")!;
            Assert.Equal(330, outer.ColumnDefinitions[2].Width.Value);
        }
        finally { window.Close(); }
    }

    private static void ClickChoice(Window owner, string label) =>
        owner.OwnedWindows.Single().GetLogicalDescendants().OfType<Button>()
            .Single(button => button.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, timeout.Token);
        }
    }

    private static void Layout(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "iseberg-desktop-persistence-" + Guid.NewGuid().ToString("N") + ".ps1");
}
