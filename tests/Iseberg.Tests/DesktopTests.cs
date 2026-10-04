using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Iseberg.Core;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Iseberg.Tests.TestApplication))]

namespace Iseberg.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class DesktopTests
{
    [AvaloniaFact]
    public async Task ConsoleEnterRunsCommandBeforeTextBoxConsumesTheKey()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        await session.Engine.InitializeAsync();
        window.Workbench.Sessions.Add(session);
        window.Workbench.SelectedSession = session;
        Layout(window);
        var output = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Engine.Output += entry =>
        {
            if (entry.Kind == OutputKind.Output && entry.Text.Contains("console-key-marker")) output.TrySetResult();
        };
        var input = window.FindControl<TextBox>("ConsoleInput")!;
        input.Text = "Write-Output 'console-key-marker'";
        var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
        input.RaiseEvent(key);
        await output.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(key.Handled);
        Assert.Equal("", input.Text);
        await session.Engine.DisposeAsync();
        window.Close();
    }

    [AvaloniaFact]
    public void LineBreakpointsFollowEdits()
    {
        var tab = new ScriptTab(new ScriptFile("test.ps1") { Text = "'first'\n'second'\n" });
        tab.ToggleBreakpoint(2);
        tab.Document.Insert(0, "# inserted\n");
        Assert.Equal(3, tab.File.Breakpoints.Single());
        var line = tab.Document.GetLineByNumber(3);
        tab.Document.Remove(line.Offset, line.TotalLength);
        Assert.Empty(tab.File.Breakpoints);
    }

    [AvaloniaFact]
    public void WorkbenchConstructsAndHasIseLayout()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        Assert.NotNull(window.FindControl<TextEditor>("ScriptEditor"));
        Assert.NotNull(window.FindControl<TextEditor>("ConsoleOutput"));
        Assert.True(window.FindControl<Border>("CommandsPane")!.IsVisible);
        Assert.Equal(12, window.FindControl<TextEditor>("ScriptEditor")!.FontSize);
        Assert.True(window.FindControl<TextEditor>("ConsoleOutput")!.IsReadOnly);
        Assert.False(window.FindControl<TextEditor>("ConsoleOutput")!.Options.AllowScrollBelowDocument);
    }

    [AvaloniaFact]
    public async Task SessionTabRowOnlyAppearsForMultipleSessions()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var first = new SessionModel("PowerShell 1");
        var second = new SessionModel("PowerShell 2");
        try
        {
            var firstFile = new ScriptTab(new ScriptFile("Untitled1.ps1"));
            var otherFile = new ScriptTab(new ScriptFile("Untitled3.ps1"));
            first.Files.Add(firstFile);
            first.Files.Add(otherFile);
            first.SelectedFile = firstFile;
            var secondFile = new ScriptTab(new ScriptFile("Untitled2.ps1"));
            second.Files.Add(secondFile);
            second.SelectedFile = secondFile;
            window.Workbench.Sessions.Add(first);
            window.Workbench.SelectedSession = first;
            Layout(window);
            var tabs = window.FindControl<TabStrip>("SessionTabs")!;
            var panes = window.FindControl<Grid>("OuterGrid")!;
            var singleSessionTop = panes.Bounds.Y;
            Assert.False(tabs.IsVisible);
            Assert.Same(firstFile.Document, window.FindControl<TextEditor>("ScriptEditor")!.Document);
            window.FindControl<TabStrip>("FileTabs")!.SelectedItem = otherFile;
            Layout(window);
            Assert.Same(otherFile, first.SelectedFile);
            Assert.Same(otherFile.Document, window.FindControl<TextEditor>("ScriptEditor")!.Document);

            window.Workbench.Sessions.Add(second);
            window.Workbench.SelectedSession = second;
            Layout(window);
            Assert.True(tabs.IsVisible);
            Assert.True(panes.Bounds.Y > singleSessionTop);
            Assert.Same(secondFile.Document, window.FindControl<TextEditor>("ScriptEditor")!.Document);

            window.Workbench.SelectedSession = first;
            window.Workbench.Sessions.Remove(second);
            Layout(window);
            Assert.False(tabs.IsVisible);
            Assert.Equal(singleSessionTop, panes.Bounds.Y);
            Assert.Same(otherFile.Document, window.FindControl<TextEditor>("ScriptEditor")!.Document);
        }
        finally
        {
            await first.Engine.DisposeAsync();
            await second.Engine.DisposeAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RightLayoutKeepsLongPromptFromHidingConsoleInput()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        window.FindControl<TextBlock>("PromptText")!.Text = "PS " + new string('x', 160) + "> ";
        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.D2,
            KeyModifiers = KeyModifiers.Control
        });
        Layout(window);
        Layout(window);
        var input = window.FindControl<TextBox>("ConsoleInput")!;
        Assert.Equal(0, Grid.GetColumn(input));
        Assert.Equal(1, Grid.GetRow(input));
        Assert.True(input.Bounds.Width > 200, $"Console input width: {input.Bounds.Width}");
        window.Close();
    }

    [AvaloniaFact]
    public void SwitchingDocumentsPreservesUndoAndDirtyState()
    {
        var file = new ScriptFile("Untitled1.ps1");
        var first = new ScriptTab(file);
        var second = new ScriptTab(new ScriptFile("Untitled2.ps1"));
        var editor = new TextEditor { Document = first.Document };
        first.Document.Insert(0, "Get-Process");
        editor.Document = second.Document;
        second.Document.Insert(0, "Get-Date");
        editor.Document = first.Document;
        Assert.Equal("Get-Process", editor.Text);
        Assert.True(file.IsDirty);
        editor.Undo();
        Assert.Equal("", file.Text);
        Assert.False(file.IsDirty);
        Assert.Equal("Get-Date", second.File.Text);
    }

    [AvaloniaFact]
    public void SwitchingWorkbenchFilesRebindsFoldingToTheCurrentDocument()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        var first = new ScriptTab(new ScriptFile("first.ps1") { Text = "function Test {\n    'first'\n}\n" });
        var second = new ScriptTab(new ScriptFile("second.ps1") { Text = "if ($true) {\n    'second'\n}\n" });
        session.Files.Add(first);
        session.Files.Add(second);
        session.SelectedFile = first;
        window.Workbench.Sessions.Add(session);
        window.Workbench.SelectedSession = session;
        Layout(window);
        session.SelectedFile = second;
        Layout(window);
        Assert.Equal(second.Document, window.FindControl<TextEditor>("ScriptEditor")!.Document);
        window.Close();
    }

    private static void Layout(Window window)
    {
        window.ApplyTemplate();
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var content = Assert.IsAssignableFrom<Control>(window.Content);
        content.Measure(new Size(1180, 780));
        content.Arrange(new Rect(0, 0, 1180, 780));
    }
}
