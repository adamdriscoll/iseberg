using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class HostingTests
{
    [AvaloniaFact]
    public async Task WorkbenchUsesThePackagedEditorForDocumentsAnalysisCompletionAndLifetime()
    {
        var control = new WorkbenchControl(new() { EnableCommandsPane = false });
        var host = new Window { Content = control };
        host.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            host.Show();
            await control.InitializeAsync();
            var first = control.CreateDocument("if (");
            var editor = control.ScriptEditorView;
            Assert.Same(editor, control.FindControl<PowerShellEditorControl>("ScriptEditorControl"));
            Assert.Same(first.Document, editor.Document);
            Assert.NotNull(editor.AnalysisProvider);
            Assert.NotNull(editor.CompletionProvider);
            Assert.Equal(EditorAnalysisState.Available, (await editor.AnalyzeAsync()).State);
            Assert.NotEmpty(editor.Analysis.Diagnostics);
            Assert.False(editor.EnableExecutionGestures);
            var second = control.CreateDocument("$value = 42");
            Assert.Same(second.Document, editor.Document);
            Assert.Empty((await editor.AnalyzeAsync()).Diagnostics);
            editor.Select(new(0, 6));
            Assert.Equal("$value", editor.CaptureText(EditorTextScope.SelectionOrCurrentLine).Text);
            second.Document.Text = "Get-D";
            editor.CaretOffset = second.Document.TextLength;
            editor.FocusEditor();
            await editor.ShowCompletionAsync();
            await Task.Delay(200);
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.IsCompletionOpen, "Console prompt/output batching must not dismiss script completion.");
            editor.CloseCompletion();
            host.Content = null;
            Assert.NotSame(second.Document, editor.TextEditor.Document);
            Assert.Equal("", editor.TextEditor.Document.Text);
            second.Document.Insert(0, "# host edit\n");
            host.Content = control;
            Assert.Same(second.Document, editor.TextEditor.Document);
            Assert.Empty((await editor.AnalyzeAsync()).Diagnostics);
            control.SelectDocument(first);
            Assert.Same(first.Document, editor.Document);
            Assert.NotEmpty((await editor.AnalyzeAsync()).Diagnostics);
            await control.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => editor.CaptureText());
            second.Document.Insert(0, "# remains owned\n");
        }
        finally { await control.DisposeAsync(); host.Close(); }
    }

    [AvaloniaFact]
    public async Task ScopedThemeProvidesEditorResources()
    {
        var control = new WorkbenchControl(new() { EnableCommandsPane = false });
        Assert.True(control.TryFindResource("ControlContentThemeFontSize", out _));
        await control.DisposeAsync();
    }

    [AvaloniaFact]
    public void InvalidHostingConfigurationsFailAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new WorkbenchControl(new() { ShowScriptPane = false, ShowConsolePane = false }));
        Assert.Throws<ArgumentException>(() => new WorkbenchControl(new() { EnablePersistence = true }));
        Assert.Throws<ArgumentException>(() => new WorkbenchControl(new() { EnablePersistence = true, SettingsPath = "relative.json" }));
        Assert.Throws<ArgumentException>(() => new WorkbenchControl(new() { SettingsPath = Path.GetFullPath("settings.json") }));
        Assert.Throws<ArgumentException>(() => new WorkbenchControl(new() { SnippetDirectory = "relative" }));
        Assert.Throws<ArgumentException>(() => new WorkbenchControl(new() { CreateInitialSession = false, StartupFiles = ["script.ps1"] }));
    }

    [AvaloniaFact]
    public async Task EmbeddedWorkbenchStartsExplicitlyAndDoesNotChangeTheHost()
    {
        var control = new WorkbenchControl(new() { EnableCommandsPane = false });
        var host = new Window { Title = "My host", Width = 920, Height = 640, Content = control };
        host.Resources["ControlBrush"] = Brushes.Magenta;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => control.InitializeAsync());
            host.Show();
            Assert.Empty(control.Workbench.Sessions);
            Assert.Throws<InvalidOperationException>(() => control.CreateDocument());
            var startup = control.InitializeAsync();
            Assert.Same(startup, control.InitializeAsync());
            await startup;
            Assert.True(control.IsStarted);
            Assert.Single(control.Workbench.Sessions);
            Assert.Equal(SessionState.Ready, control.Workbench.SelectedSession!.Engine.State);
            Assert.Equal("My host", host.Title);
            Assert.Equal(920, host.Width);
            Assert.Equal(640, host.Height);
            Assert.Same(Brushes.Magenta, host.Resources["ControlBrush"]);
            Assert.NotSame(Brushes.Magenta, control.Resources["ControlBrush"]);
            Assert.Empty(host.Styles);
            Assert.False(control.FindControl<Border>("CommandsPane")!.IsVisible);
            Assert.Empty(control.Workbench.SelectedSession.Commands);
        }
        finally { await control.DisposeAsync(); host.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SinglePaneHostsUseTheEntireAvailableEditorArea(bool script, bool console)
    {
        var control = new WorkbenchControl(new()
        {
            ShowScriptPane = script, ShowConsolePane = console, ShowMenu = false,
            ShowToolbar = false, ShowStatusBar = false, ShowSessionTabs = false,
            EnableCommandsPane = false, EnableDebuggerPane = false,
            Preferences = new() { Layout = "Right" }
        });
        var host = new Window { Width = 900, Height = 650, Content = control };
        try
        {
            host.Show();
            await control.InitializeAsync();
            await control.CreateSessionAsync();
            control.Measure(new Size(900, 650));
            control.Arrange(new Rect(0, 0, 900, 650));
            control.UpdateLayout();
            Assert.False(control.FindControl<Menu>("WorkbenchMenu")!.IsVisible);
            Assert.False(control.FindControl<Border>("WorkbenchToolbar")!.IsVisible);
            Assert.False(control.FindControl<Border>("WorkbenchStatusBar")!.IsVisible);
            Assert.False(control.FindControl<TabStrip>("SessionTabs")!.IsVisible);
            Assert.False(control.FindControl<GridSplitter>("PaneSplitter")!.IsVisible);
            Assert.Equal(script, control.FindControl<Grid>("ScriptPane")!.IsVisible);
            Assert.Equal(console, control.FindControl<Grid>("ConsolePane")!.IsVisible);
            var pane = control.FindControl<Grid>(script ? "ScriptPane" : "ConsolePane")!;
            Assert.Equal(control.FindControl<Grid>("PaneGrid")!.Bounds.Size, pane.Bounds.Size);
            Assert.True(control.FindEditor(script ? "ScriptEditor" : "ConsoleEditor")!.IsKeyboardFocusWithin);
        }
        finally { await control.DisposeAsync(); host.Close(); }
    }

    [AvaloniaFact]
    public async Task PublicOperationsCreateSelectSaveRunAndCloseOwnedDocuments()
    {
        var previousPolicy = Environment.GetEnvironmentVariable("PSExecutionPolicyPreference");
        var directory = TempDirectory();
        var path = Path.Combine(directory, "script.ps1");
        var control = new WorkbenchControl(new() { EnableCommandsPane = false, SnippetDirectory = Path.Combine(directory, "snippets") });
        var host = new Window { Content = control };
        try
        {
            host.Show();
            await control.InitializeAsync();
            var original = control.Workbench.SelectedSession!;
            if (OperatingSystem.IsWindows())
                await control.ExecuteAsync("Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force");
            var script = control.CreateDocument("'from-document'");
            Assert.True(script.File.IsDirty);
            Assert.True(await control.SaveDocumentAsync(script, path));
            Assert.Equal("'from-document'", await File.ReadAllTextAsync(path));
            await control.RunDocumentAsync();
            Assert.Contains("from-document", original.ConsoleDocument.Text);
            var second = await control.CreateSessionAsync();
            await control.ExecuteAsync("$hostValue = 42; $hostValue");
            Assert.Contains("42", second.ConsoleDocument.Text);
            control.SelectDocument(script);
            Assert.Same(original, control.Workbench.SelectedSession);
            Assert.Same(script.Document, control.FindEditor("ScriptEditor")!.Document);
            control.SelectSession(second);
            Assert.Throws<ArgumentException>(() => control.SelectDocument(new(new ScriptFile("foreign.ps1"))));
            await Assert.ThrowsAsync<ArgumentException>(() => control.SaveDocumentAsync(script, "relative.ps1"));
            Assert.True(await control.CloseDocumentAsync(script));
            Assert.DoesNotContain(script, original.Files);
            Assert.True(await control.CloseSessionAsync(original));
            Assert.Equal(SessionState.Disposed, original.Engine.State);
            Assert.Single(control.Workbench.Sessions);
        }
        finally
        {
            await control.DisposeAsync(); host.Close(); Directory.Delete(directory, true);
            Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", previousPolicy);
        }
    }

    [AvaloniaFact]
    public async Task InitializationCopiesOptionsAndSupportsAnEmptyWorkbench()
    {
        var preferences = new UserSettings { WordWrap = true };
        var control = new WorkbenchControl(new() { CreateInitialSession = false, Preferences = preferences, EnableCommandsPane = false });
        var host = new Window { Content = control };
        preferences.WordWrap = false;
        try
        {
            host.Show();
            await control.InitializeAsync();
            Assert.Empty(control.Workbench.Sessions);
            Assert.True(control.FindEditor("ScriptEditor")!.WordWrap);
            await Assert.ThrowsAsync<InvalidOperationException>(() => control.ExecuteAsync("42"));
            await control.CreateSessionAsync();
            await control.ExecuteAsync("42");
            Assert.Contains("42", control.Workbench.SelectedSession!.ConsoleDocument.Text);
        }
        finally { await control.DisposeAsync(); host.Close(); }
    }

    [AvaloniaFact]
    public async Task ExitNotifiesTheHostAndUiFailuresRaiseEventsWithoutModalDialogs()
    {
        var control = new WorkbenchControl(new() { EnableCommandsPane = false });
        var host = new Window { Content = control };
        var exitRequested = false;
        WorkbenchErrorEventArgs? error = null;
        control.CloseRequested += (_, _) => exitRequested = true;
        control.ErrorOccurred += (_, args) => error = args;
        try
        {
            host.Show();
            await control.InitializeAsync();
            var menus = control.FindControl<Menu>("WorkbenchMenu")!.GetLogicalDescendants().OfType<MenuItem>().ToArray();
            menus.First(menu => menu.Tag as string == "Exit").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(exitRequested);
            Assert.True(host.IsVisible);
            Assert.True(control.IsStarted);
            menus.First(menu => menu.Tag as string == "Commands").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.NotNull(error);
            Assert.IsType<NotSupportedException>(error.Exception);
            Assert.Empty(host.OwnedWindows);
            Assert.Contains(error.Exception.Message, control.FindControl<TextBlock>("StatusText")!.Text);
            await Assert.ThrowsAsync<FileNotFoundException>(() => control.OpenFileAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ps1")));
        }
        finally { await control.DisposeAsync(); host.Close(); }
    }

    [AvaloniaFact]
    public async Task CloseCancellationPreservesTheControlAndApprovedCloseDoesNotCloseTheHost()
    {
        var control = new WorkbenchControl(new() { EnableCommandsPane = false });
        var host = new Window { Content = control };
        try
        {
            host.Show();
            await control.InitializeAsync();
            var session = control.Workbench.SelectedSession!;
            control.CreateDocument("unsaved text");
            var closing = control.RequestCloseAsync();
            await WaitFor(() => host.OwnedWindows.Any());
            ClickChoice(host, "Cancel");
            Assert.False(await closing);
            Assert.True(control.IsStarted);
            Assert.False(control.FindEditor("ScriptEditor")!.IsReadOnly);
            closing = control.RequestCloseAsync();
            await WaitFor(() => host.OwnedWindows.Any());
            ClickChoice(host, "Don't Save");
            Assert.True(await closing);
            Assert.True(host.IsVisible);
            Assert.Equal(SessionState.Disposed, session.Engine.State);
            Assert.False(control.IsStarted);
            Assert.True(await control.RequestCloseAsync());
        }
        finally { await control.DisposeAsync(); host.Close(); }
    }

    [AvaloniaFact]
    public async Task DisposalStopsExecutionAndCanBeAwaitedRepeatedly()
    {
        var control = new WorkbenchControl(new() { EnableCommandsPane = false });
        var host = new Window { Content = control };
        try
        {
            host.Show();
            await control.InitializeAsync();
            var session = control.Workbench.SelectedSession!;
            var execution = control.ExecuteAsync("Start-Sleep -Seconds 30");
            await WaitFor(() => session.Engine.State == SessionState.Running);
            await control.DisposeAsync();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
            await control.DisposeAsync();
            await session.Engine.DisposeAsync();
            Assert.Equal(SessionState.Disposed, session.Engine.State);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => control.InitializeAsync());
            Assert.Throws<ObjectDisposedException>(() => control.CreateDocument());
        }
        finally { await control.DisposeAsync(); host.Close(); }
    }

    [AvaloniaFact]
    public async Task DisposalDuringInitializationDoesNotRestartTheControl()
    {
        var control = new WorkbenchControl(new() { EnableCommandsPane = false });
        var host = new Window { Content = control };
        try
        {
            host.Show();
            var initialization = control.InitializeAsync();
            var disposal = control.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => initialization);
            await disposal;
            Assert.False(control.IsStarted);
            Assert.False(control.IsEnabled);
            Assert.All(control.Workbench.Sessions, session => Assert.Equal(SessionState.Disposed, session.Engine.State));
        }
        finally { await control.DisposeAsync(); host.Close(); }
    }

    [AvaloniaFact]
    public async Task TwoEmbeddedControlsKeepIndependentSessionsAndLifetimes()
    {
        var first = new WorkbenchControl(new() { EnableCommandsPane = false });
        var second = new WorkbenchControl(new() { EnableCommandsPane = false });
        var panel = new StackPanel();
        panel.Children.Add(first);
        panel.Children.Add(second);
        var host = new Window { Content = panel };
        try
        {
            host.Show();
            await first.InitializeAsync();
            await second.InitializeAsync();
            await first.ExecuteAsync("$onlyInFirst = 42");
            await second.ExecuteAsync("Test-Path variable:onlyInFirst");
            Assert.Contains("False", second.Workbench.SelectedSession!.ConsoleDocument.Text);
            await first.DisposeAsync();
            panel.Children.Remove(first);
            await second.ExecuteAsync("'still-running'");
            Assert.Contains("still-running", second.Workbench.SelectedSession.ConsoleDocument.Text);
            Assert.True(second.IsStarted);
            Assert.True(host.IsVisible);
        }
        finally { await first.DisposeAsync(); await second.DisposeAsync(); host.Close(); }
    }

    [AvaloniaFact]
    public async Task PersistenceUsesOnlyTheExplicitHostLocationAndBadSettingsPropagate()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "settings.json");
        var control = new WorkbenchControl(new() { EnablePersistence = true, SettingsPath = path, EnableCommandsPane = false });
        var host = new Window { Content = control };
        try
        {
            host.Show();
            await control.InitializeAsync();
            Assert.True(await control.RequestCloseAsync());
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".workbench.json"));
            await File.WriteAllTextAsync(path, "not json");
            var replacement = new WorkbenchControl(new() { EnablePersistence = true, SettingsPath = path, EnableCommandsPane = false });
            host.Content = replacement;
            try
            {
                await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => replacement.InitializeAsync());
                Assert.False(replacement.IsStarted);
            }
            finally { await replacement.DisposeAsync(); }
        }
        finally { await control.DisposeAsync(); host.Close(); Directory.Delete(directory, true); }
    }

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-hosting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void ClickChoice(Window host, string label) => host.OwnedWindows.Single()
        .GetLogicalDescendants().OfType<Button>().Single(button => button.Content as string == label)
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(condition());
    }
}
