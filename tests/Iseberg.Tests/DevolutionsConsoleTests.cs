using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using Devolutions.Terminal;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class DevolutionsConsoleTests
{
    [Fact]
    public async Task ClassicIsTheDefaultAndConsolePreferenceRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            Assert.Equal("Classic", new UserSettings().ConsoleMode);
            await File.WriteAllTextAsync(path, """{"FontSize":12}""");
            Assert.Equal("Classic", (await UserSettings.LoadAsync(path)).ConsoleMode);
            await new UserSettings { ConsoleMode = "Devolutions" }.SaveAsync(path);
            Assert.Equal("Devolutions", (await UserSettings.LoadAsync(path)).ConsoleMode);
            var invalid = new UserSettings { ConsoleMode = "Unknown" };
            invalid.Normalize();
            Assert.Equal("Classic", invalid.ConsoleMode);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void OptionsCanSelectAndRestoreTheConsole()
    {
        var applied = new UserSettings();
        var dialog = new OptionsWindow(applied, settings => { applied = settings; return Task.CompletedTask; });
        try
        {
            var combo = dialog.FindControl<ComboBox>("ConsoleMode")!;
            Assert.Equal(0, combo.SelectedIndex);
            combo.SelectedIndex = 1;
            dialog.FindControl<Button>("ApplyOptions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Devolutions", applied.ConsoleMode);
            dialog.FindControl<Button>("RestoreDefaults")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Classic", dialog.Draft.ConsoleMode);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task SwitchingConsolesPreservesRunspaceTranscriptDraftAndHistory()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var owner = new MainWindow([], initializeOnOpen: false, settingsPath: settingsPath);
        var session = new SessionModel("PowerShell 1");
        try
        {
            await session.Engine.InitializeAsync();
            owner.Workbench.Sessions.Add(session);
            owner.Workbench.SelectedSession = session;
            Layout(owner);
            Assert.False(owner.FindControl<ContentControl>("TerminalOutputHost")!.IsVisible);
            await session.Engine.ExecuteAsync("$keptVariable = 42; 'old-output'");
            session.FlushOutput();
            session.Input = "$keptVariable";
            session.History.Add("'old-output'");
            SelectMode(owner, "DevolutionsConsole");
            await WaitFor(() => session.UseDevolutionsConsole);
            Assert.Equal("$keptVariable", session.Input);
            Assert.DoesNotContain("old-output", session.ConsoleDocument.Text);
            var terminal = Assert.IsType<TermControl>(owner.FindControl<ContentControl>("TerminalOutputHost")!.Content);
            Assert.Contains("old-output", Text(terminal));
            Assert.DoesNotContain("\x1b", session.ClassicConsole.Document.Text, StringComparison.Ordinal);
            var editor = owner.FindControl<TextEditor>("ConsoleEditor")!;
            Enter(editor);
            await WaitFor(() => session.Engine.State == SessionState.Ready && session.Input == "");
            session.FlushOutput();
            Assert.Contains("42", Text(terminal));
            SelectMode(owner, "ClassicConsole");
            await WaitFor(() => !session.UseDevolutionsConsole);
            Assert.Contains("old-output", editor.Text);
            Assert.Contains("42", editor.Text);
            Assert.Contains("$keptVariable", session.History);
            Assert.False(owner.FindControl<ContentControl>("TerminalOutputHost")!.IsVisible);
            Assert.Equal("Classic", (await UserSettings.LoadAsync(settingsPath)).ConsoleMode);
        }
        finally { await session.Engine.DisposeAsync(); owner.Close(); File.Delete(settingsPath); }
    }

    [AvaloniaFact]
    public async Task TerminalRendersStreamingAnsiCursorControlsAndClearsWithoutLosingDraft()
    {
        var session = new SessionModel("PowerShell 1");
        await session.Engine.InitializeAsync();
        var view = new TerminalOutputView(session, new UserSettings());
        try
        {
            session.ClassicConsole.Append(new("line one\nline two\n"));
            session.ClassicConsole.Append(new("\x1b[31"));
            session.ClassicConsole.Append(new("mred\x1b[0m\rreplaced\x1b[K"));
            Assert.Contains("line one\nline two", Text(view.Control));
            Assert.Contains("replaced", Text(view.Control));
            Assert.DoesNotContain("red", Text(view.Control));
            Assert.Contains("Unsupported terminal control", session.ClassicConsole.Document.Text);
            session.SetConsoleMode(true);
            session.Input = "unsent draft";
            session.ClearOutput();
            Assert.Equal("unsent draft", session.Input);
            Assert.DoesNotContain("line one", Text(view.Control));
            Assert.Empty(session.TerminalOutput);
            Assert.False(view.Control.Engine.CursorVisible);
        }
        finally { await view.CloseAsync(); await session.Engine.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task TerminalInputRetainsMultilineCompletionAndHistory()
    {
        var owner = new MainWindow([], initializeOnOpen: false, preferences: new() { ConsoleMode = "Devolutions" });
        var session = new SessionModel("PowerShell 1");
        try
        {
            await session.Engine.InitializeAsync();
            owner.Workbench.Sessions.Add(session);
            owner.Workbench.SelectedSession = session;
            Layout(owner);
            var editor = owner.FindControl<TextEditor>("ConsoleEditor")!;
            session.Input = "if ($true) {";
            Assert.False(editor.IsReadOnly);
            Assert.True(session.Console.HasPrompt);
            Assert.Same(session.Console, editor.TextArea.ReadOnlySectionProvider);
            Assert.True(EditorAnalysis.Analyze(session.Input).Errors.Any(error => error.IncompleteInput));
            Enter(editor);
            Assert.EndsWith(Environment.NewLine, session.Input);
            session.Input += "'multiline-result' }";
            Enter(editor);
            await WaitFor(() => session.Engine.State == SessionState.Ready && session.Input == "");
            session.FlushOutput();
            session.Input = "draft";
            Send(editor, Key.Up);
            Assert.StartsWith("if ($true)", session.Input);
            Send(editor, Key.Down);
            Assert.Equal("draft", session.Input);
            session.Input = "Get-Proc";
            editor.CaretOffset = editor.Document.TextLength;
            Send(editor, Key.Tab);
            await WaitFor(() => session.Input == "Get-Process");
            Assert.False(session.Console.CanInsert(0));
            var terminal = Assert.IsType<TermControl>(owner.FindControl<ContentControl>("TerminalOutputHost")!.Content);
            terminal.Focus();
            terminal.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, KeyModifiers = KeyModifiers.Control });
            await WaitFor(() => Completion(owner) is not null);
            Assert.Same(editor.TextArea, Completion(owner)!.TextArea);
        }
        finally { await session.Engine.DisposeAsync(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task IndependentTerminalScreensSurviveTabSwitchingAndClosing()
    {
        var owner = new MainWindow([], initializeOnOpen: false, preferences: new() { ConsoleMode = "Devolutions" });
        var first = new SessionModel("First");
        var second = new SessionModel("Second");
        try
        {
            await first.Engine.InitializeAsync();
            await second.Engine.InitializeAsync();
            owner.Workbench.Sessions.Add(first);
            owner.Workbench.Sessions.Add(second);
            owner.Workbench.SelectedSession = first;
            Layout(owner);
            first.ClassicConsole.Append(new("first screen"));
            var host = owner.FindControl<ContentControl>("TerminalOutputHost")!;
            var firstControl = Assert.IsType<TermControl>(host.Content);
            owner.Workbench.SelectedSession = second;
            Dispatcher.UIThread.RunJobs();
            second.ClassicConsole.Append(new("second screen"));
            Assert.Contains("second screen", Text(Assert.IsType<TermControl>(host.Content)));
            Assert.DoesNotContain("first screen", Text(Assert.IsType<TermControl>(host.Content)));
            owner.Workbench.SelectedSession = first;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(firstControl, host.Content);
            Assert.Contains("first screen", Text(firstControl));
            owner.Workbench.Sessions.Remove(second);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(firstControl, host.Content);
        }
        finally { await first.Engine.DisposeAsync(); await second.Engine.DisposeAsync(); owner.Close(); }
    }

    [Fact]
    public async Task EmbeddedLaunchUsesResolvedLiteralArgumentsDirectoryAndExitStatus()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var session = new PowerShellSession();
        TerminalRequest? request = null;
        session.TerminalRequested += value => { request = value; value.Response.TrySetResult(7); };
        var output = new List<OutputEntry>();
        session.Output += output.Add;
        await session.InitializeAsync();
        await session.ExecuteAsync("Start-IsebergTerminal cmd.exe -ArgumentList @('a b', 'x\"y', '%PATH%', '$(literal)', '') ; \"last=$LASTEXITCODE\"");
        Assert.NotNull(request);
        Assert.True(Path.IsPathFullyQualified(request.Executable));
        Assert.Equal(new[] { "a b", "x\"y", "%PATH%", "$(literal)", "" }, request.Arguments);
        Assert.True(Directory.Exists(request.WorkingDirectory));
        Assert.Contains(output, entry => entry.Text.Contains("last=7"));
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Warning);
    }

    [AvaloniaFact]
    public async Task EmbeddedNativeApplicationAcceptsRealInputAndReturnsToTheSameRunspace()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = FindPowerShell();
        var script = """
            [Console]::WriteLine("native-ready")
            $value = [Console]::ReadLine()
            [Console]::WriteLine("native-input:$value;redirected=$([Console]::IsOutputRedirected)")
            exit 7
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var owner = new MainWindow([], initializeOnOpen: false, preferences: new() { ConsoleMode = "Devolutions" });
        var session = new SessionModel("PowerShell 1");
        Task? execution = null;
        try
        {
            await session.Engine.InitializeAsync();
            owner.Workbench.Sessions.Add(session);
            owner.Workbench.SelectedSession = session;
            Layout(owner);
            execution = session.Engine.ExecuteAsync($"$kept = 123; $nativeExit = Start-IsebergTerminal '{executable.Replace("'", "''")}' " +
                $"-ArgumentList @('-NoLogo', '-NoProfile', '-EncodedCommand', '{encoded}'); \"exit=$nativeExit;last=$LASTEXITCODE;kept=$kept\"");
            var host = owner.FindControl<ContentControl>("TerminalOutputHost")!;
            await WaitFor(() => host.Content is TermControl { Name: "DevolutionsApplication" } terminal && Text(terminal).Contains("native-ready"));
            var terminal = Assert.IsType<TermControl>(host.Content);
            Assert.False(owner.FindControl<TextEditor>("ConsoleEditor")!.IsVisible);
            terminal.WriteInput("actual console input\r");
            await WaitFor(() => execution.IsCompleted);
            await execution;
            session.FlushOutput();
            Assert.Equal(SessionState.Ready, session.Engine.State);
            Assert.IsType<TermControl>(host.Content);
            Assert.Equal("DevolutionsOutput", ((TermControl)host.Content!).Name);
            Assert.Contains("native-input:actual console input;redirected=False", session.ClassicConsole.Document.Text);
            Assert.Contains("exit=7;last=7;kept=123", session.ClassicConsole.Document.Text);
            Assert.True(owner.FindControl<TextEditor>("ConsoleEditor")!.IsVisible);
        }
        finally { await session.Engine.StopAsync(); if (execution is not null) await execution; await session.Engine.DisposeAsync(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task StopCancelsEmbeddedNativeApplicationAndReleasesTheExecutionGate()
    {
        if (!OperatingSystem.IsWindows()) return;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::WriteLine('waiting-input'); [Console]::ReadLine()"));
        var owner = new MainWindow([], initializeOnOpen: false, preferences: new() { ConsoleMode = "Devolutions" });
        var session = new SessionModel("PowerShell 1");
        Task? execution = null;
        try
        {
            await session.Engine.InitializeAsync();
            owner.Workbench.Sessions.Add(session);
            owner.Workbench.SelectedSession = session;
            Layout(owner);
            execution = session.Engine.ExecuteAsync($"Start-IsebergTerminal '{FindPowerShell().Replace("'", "''")}' " +
                $"-ArgumentList @('-NoProfile', '-EncodedCommand', '{encoded}')");
            var host = owner.FindControl<ContentControl>("TerminalOutputHost")!;
            await WaitFor(() => host.Content is TermControl { Name: "DevolutionsApplication" } terminal && Text(terminal).Contains("waiting-input"));
            var terminal = Assert.IsType<TermControl>(host.Content);
            using var process = Process.GetProcessById(terminal.ProcessMetadata!.ProcessId);
            await session.Engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitFor(() => host.Content is TermControl { Name: "DevolutionsOutput" });
            Assert.True(process.HasExited);
            Assert.Equal(SessionState.Ready, session.Engine.State);
            await session.Engine.ExecuteAsync("'after-stop'");
            session.FlushOutput();
            Assert.Contains("after-stop", Text(Assert.IsType<TermControl>(host.Content)));
        }
        finally { await session.Engine.StopAsync(); if (execution is not null) await execution; await session.Engine.DisposeAsync(); owner.Close(); }
    }

    private static string FindPowerShell() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(directory => Path.Combine(directory, "pwsh.exe")).First(File.Exists);
    private static CompletionWindow? Completion(MainWindow window) => typeof(MainWindow)
        .GetField("completion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(window) as CompletionWindow;
    private static string Text(TermControl terminal) => string.Join("\n", terminal.Engine.CreateSnapshot(includeHistory: true).Buffer.Lines
        .Select(line => string.Concat(line.Cells.Select(cell => cell.Text)).TrimEnd()));
    private static void Layout(Window window)
    {
        window.Show();
        window.Measure(new Size(1180, 780));
        window.Arrange(new Rect(0, 0, 1180, 780));
        Dispatcher.UIThread.RunJobs();
    }
    private static void Enter(TextEditor editor)
    {
        editor.TextArea.Focus();
        editor.CaretOffset = editor.Document.TextLength;
        Send(editor, Key.Enter);
    }
    private static void Send(TextEditor editor, Key key) =>
        editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
    private static void SelectMode(MainWindow owner, string tag) =>
        owner.FindControl<Menu>("WorkbenchMenu")!.GetLogicalDescendants().OfType<MenuItem>().Single(item => item.Tag as string == tag)
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Terminal transition did not complete.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
