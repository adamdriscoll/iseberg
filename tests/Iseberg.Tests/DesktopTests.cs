using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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

[Collection(PowerShellPolicyCollection.Name)]
public sealed class DesktopTests
{
    [AvaloniaFact]
    public async Task ConsoleEnterRunsCommandBeforeEditorConsumesTheKey()
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
        var input = window.FindControl<TextEditor>("ConsoleEditor")!;
        session.Input = "Write-Output 'console-key-marker'";
        input.CaretOffset = input.Document.TextLength;
        var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
        input.TextArea.RaiseEvent(key);
        await output.Task.WaitAsync(TimeSpan.FromSeconds(10));
        while (session.Engine.State != SessionState.Ready) await Task.Delay(10);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(key.Handled);
        Assert.Equal("", session.Input);
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
    public void ConditionalDisabledBreakpointsRetainSettingsWhenLinesMove()
    {
        var tab = new ScriptTab(new ScriptFile("test.ps1") { Text = "'first'\n'second'\n" });
        tab.SetBreakpoint(new(BreakpointKind.Line, Line: 2, Condition: "$value -gt 2", Enabled: false));
        tab.Document.Insert(0, "# inserted\n");
        var spec = Assert.Single(tab.LineBreakpoints);
        Assert.Equal(3, spec.Line);
        Assert.Equal("$value -gt 2", spec.Condition);
        Assert.False(spec.Enabled);
        var line = tab.Document.GetLineByNumber(3);
        tab.Document.Remove(line.Offset, line.TotalLength);
        Assert.Empty(tab.LineBreakpoints);
        Assert.Empty(tab.File.Breakpoints);
    }

    [AvaloniaFact]
    public void BreakpointEditorReadsAllKindsAndValidatesConditions()
    {
        var spec = new BreakpointSpec(BreakpointKind.Variable, Target: "tracked", Condition: "$tracked -eq 2",
            Enabled: false, AccessMode: System.Management.Automation.VariableAccessMode.ReadWrite);
        var dialog = new BreakpointWindow(spec);
        Assert.Equal(spec, dialog.ReadSpec());
        dialog.FindControl<ComboBox>("BreakpointKind")!.SelectedItem = BreakpointKind.Line;
        dialog.FindControl<TextBox>("BreakpointScript")!.Text = Path.Combine(Path.GetTempPath(), "test.ps1");
        dialog.FindControl<TextBox>("BreakpointLine")!.Text = "7";
        var line = dialog.ReadSpec();
        line.Validate();
        Assert.Equal(7, line.Line);
        dialog.FindControl<TextBox>("BreakpointCondition")!.Text = "$tracked -eq";
        Assert.Throws<System.Management.Automation.ParseException>(() => dialog.ReadSpec().Validate());
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task PausedWorkbenchEvaluatesConsoleAndShowsLiveInspectionAndBreakpointControls()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iseberg-desktop-debug-{Guid.NewGuid():N}.ps1");
        var previousPolicy = Environment.GetEnvironmentVariable("PSExecutionPolicyPreference");
        await File.WriteAllTextAsync(path, "$localValue = 21\n$localValue += 1\n\"result=$localValue\"\n");
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        try
        {
            await session.Engine.InitializeAsync();
            if (OperatingSystem.IsWindows())
                await session.Engine.ExecuteAsync("Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force");
            var file = new ScriptTab(await ScriptFile.OpenAsync(path));
            file.ToggleBreakpoint(2);
            session.Files.Add(file);
            session.SelectedFile = file;
            session.Watches.Add("$localValue * 2");
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            Layout(window);
            await session.Engine.SetLineBreakpointsAsync(path, file.LineBreakpoints);
            file.Document.Insert(0, "# edited after breakpoint installation\n");
            var execution = session.Engine.ExecuteAsync("", path);
            await WaitForUiAsync(() => session.DebugSnapshot is not null);
            Assert.Equal(3, file.File.Breakpoints.Single());
            var console = window.FindControl<TextEditor>("ConsoleEditor")!;
            Assert.False(console.IsReadOnly);
            Assert.True(window.FindControl<TextEditor>("ScriptEditor")!.IsReadOnly);
            Assert.True(window.FindControl<Border>("DebuggerPane")!.IsVisible);
            Assert.Contains(window.FindControl<ListBox>("VariablesList")!.Items.OfType<DebugValue>(), value => value.Name == "$localValue" && value.Value == "21");
            Assert.Equal("42", window.FindControl<ListBox>("WatchesList")!.Items.OfType<DebugValue>().Single().Value);
            Assert.NotEmpty(window.FindControl<ListBox>("CallStackList")!.Items);

            session.Input = "$localValue = 40";
            console.CaretOffset = console.Document.TextLength;
            console.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await WaitForUiAsync(() => session.DebugSnapshot?.Watches[0].Value == "80");
            Assert.Equal(3, file.File.Breakpoints.Single());
            Assert.True(session.Console.HasPrompt);
            Assert.Equal(Iseberg.Core.SessionState.Debugging, session.Engine.State);
            var breakpoints = window.FindControl<ListBox>("BreakpointsList")!;
            breakpoints.SelectedIndex = 0;
            window.FindControl<Button>("EnableBreakpointButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForUiAsync(() => file.LineBreakpoints.Single().Enabled == false);
            Assert.Equal(3, file.File.Breakpoints.Single());
            Assert.False(session.Breakpoints.Single().Spec.Enabled);

            session.Engine.Resume(System.Management.Automation.DebuggerResumeAction.Continue);
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForUiAsync(() => session.DebugLocation is null && !window.FindControl<TextEditor>("ScriptEditor")!.IsReadOnly);
            Assert.Empty(window.FindControl<ListBox>("VariablesList")!.Items);
            session.FlushOutput();
            Assert.Contains("result=41", session.ConsoleDocument.Text);
        }
        finally
        {
            await session.Engine.DisposeAsync();
            window.Close();
            File.Delete(path);
            Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", previousPolicy);
        }
    }

    private static async Task WaitForUiAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "The debugger UI did not reach the expected state.");
    }

    [AvaloniaFact]
    public void WorkbenchConstructsAndHasIseLayout()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        Assert.NotNull(window.FindControl<TextEditor>("ScriptEditor"));
        Assert.NotNull(window.FindControl<TextEditor>("ConsoleEditor"));
        Assert.True(window.FindControl<Border>("CommandsPane")!.IsVisible);
        Assert.Equal(12, window.FindControl<TextEditor>("ScriptEditor")!.FontSize);
        Assert.False(window.FindControl<TextEditor>("ConsoleEditor")!.Options.AllowScrollBelowDocument);
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
    public async Task RightLayoutWrapsLongPromptsInTheSingleConsoleBuffer()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        await session.Engine.InitializeAsync();
        await session.Engine.ExecuteAsync("function prompt { 'PS ' + ('x' * 160) + '> ' }");
        window.Workbench.Sessions.Add(session);
        window.Workbench.SelectedSession = session;
        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.D2,
            KeyModifiers = KeyModifiers.Control
        });
        Layout(window);
        Layout(window);
        var input = window.FindControl<TextEditor>("ConsoleEditor")!;
        Assert.True(input.WordWrap);
        Assert.True(session.Console.HasPrompt);
        Assert.Equal(session.Engine.Prompt.Length, session.Console.InputStart - session.Console.TranscriptEnd);
        Assert.True(input.Bounds.Width > 200, $"Console input width: {input.Bounds.Width}");
        await session.Engine.DisposeAsync();
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
