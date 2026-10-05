using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
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
    public async Task ClosingDuringCommandDiscoveryDoesNotPublishMetadataOrOpenAnErrorDialog()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcherErrors = new List<Exception>();
        void OnDispatcherError(object? sender, Avalonia.Threading.DispatcherUnhandledExceptionEventArgs args)
        {
            dispatcherErrors.Add(args.Exception);
            args.Handled = true;
        }
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += OnDispatcherError;
        try
        {
            await session.Engine.InitializeAsync();
            await session.Engine.ExecuteAsync("""
                function Get-Command {
                    Write-Host 'closing-discovery-started'
                    Start-Sleep -Milliseconds 500
                    Microsoft.PowerShell.Core\Get-Command @args
                }
                """);
            session.Engine.Output += entry =>
            {
                if (entry.Kind == OutputKind.Output && entry.Text.Contains("closing-discovery-started"))
                    started.TrySetResult();
            };
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            Layout(window);
            session.SelectedCommand = "Get-Process";
            window.FindControl<Button>("CommandRefreshButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            window.Close();
            await session.Engine.DisposeAsync();
            await Task.Delay(100);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Empty(session.Commands);
            Assert.Empty(dispatcherErrors);
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException -= OnDispatcherError;
            if (session.Engine.State != Iseberg.Core.SessionState.Disposed) await session.Engine.DisposeAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RemoteConnectionDialogValidatesAndCreatesBothTransportConfigurations()
    {
        var owner = new MainWindow([], initializeOnOpen: false);
        Layout(owner);
        try
        {
            foreach (var transport in new[] { 0, 1 })
            {
                var window = new RemoteConnectionWindow();
                var result = window.ShowDialog<System.Management.Automation.Runspaces.RunspaceConnectionInfo?>(owner);
                window.FindControl<ComboBox>("RemoteTransport")!.SelectedIndex = transport;
                var connect = window.GetLogicalDescendants().OfType<Button>().Single(button => button.Content as string == UiText.Get("RemoteConnect"));
                connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(UiText.Get("RemoteAddressRequired"), window.FindControl<TextBlock>("RemoteConnectionError")!.Text);
                Assert.Equal(transport == 1, window.FindControl<TextBox>("RemotePassword")!.IsEnabled);
                window.FindControl<TextBox>("RemoteAddress")!.Text = transport == 0 ? "test-host" : "https://test-host:5986/wsman";
                window.FindControl<TextBox>("RemoteUser")!.Text = "test-user";
                if (transport == 0) window.FindControl<TextBox>("RemotePort")!.Text = "2022";
                connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var connection = await result;
                if (transport == 0)
                {
                    var ssh = Assert.IsType<System.Management.Automation.Runspaces.SSHConnectionInfo>(connection);
                    Assert.Equal("test-host", ssh.ComputerName);
                    Assert.Equal("test-user", ssh.UserName);
                    Assert.Equal(2022, ssh.Port);
                    Assert.Equal("powershell", ssh.Subsystem);
                }
                else
                {
                    var wsman = Assert.IsType<System.Management.Automation.Runspaces.WSManConnectionInfo>(connection);
                    Assert.Equal(new Uri("https://test-host:5986/wsman"), wsman.ConnectionUri);
                    Assert.Equal("test-user", wsman.Credential.UserName);
                    wsman.Credential.Password.Dispose();
                }
            }
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task RemoteTabDebuggerNavigatesRemoteSourceAndRestoresLocalBreakpointContext()
    {
        await using var server = await RemotingTests.RemoteServer.StartAsync();
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-remote-ui.ps1");
        await File.WriteAllTextAsync(path, "$remoteUiValue = 21\n$remoteUiValue = 42\n$remoteUiValue\n");
        Task? execution = null;
        try
        {
            await session.Engine.InitializeAsync();
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            Layout(window);
            await window.OpenFileAsync(path);
            var local = session.SelectedFile!;
            await session.Engine.SetBreakpointsAsync(path, [1]);
            await session.Engine.ConnectAsync(server.Connection);
            await WaitForUiAsync(() => session.Commands.Count > 0 && session.DisplayName.Contains('['), TestTimeouts.CommandDiscovery);
            Assert.True(window.FindControl<TabStrip>("SessionTabs")!.Items.Count == 1);
            Assert.True(window.FindControl<TabStrip>("SessionTabs")!.IsVisible);
            await window.OpenRemoteFileAsync(path);
            var remote = session.SelectedFile!;
            Assert.NotSame(local, remote);
            Assert.True(remote.File.IsRemote);
            Assert.Equal(2, session.Files.Count);
            Assert.Empty(await session.Engine.GetBreakpointsAsync());
            await session.Engine.SetBreakpointsAsync(path, [2]);
            session.Watches.Add("$remoteUiValue");
            execution = session.Engine.ExecuteAsync("", path);
            await WaitForUiAsync(() => session.DebugSnapshot is not null);
            Assert.Same(remote, session.SelectedFile);
            Assert.Contains(session.DebugSnapshot!.Variables, value => value.Name == "$remoteUiValue" && value.Value == "21");
            Assert.Contains(session.Engine.RemoteComputerName!, window.FindControl<TextBlock>("DebuggerScope")!.Text);
            Assert.True(window.FindControl<Border>("DebuggerPane")!.IsVisible);
            Assert.True(window.FindControl<TextEditor>("ScriptEditor")!.IsReadOnly);
            session.FlushOutput();
            Assert.Contains(session.Engine.DebugPrompt, session.ConsoleDocument.Text);
            session.Engine.Resume(System.Management.Automation.DebuggerResumeAction.Continue);
            await execution.WaitAsync(TimeSpan.FromSeconds(20));
            await WaitForUiAsync(() => session.DebugSnapshot is null);
            await session.Engine.ExitRemoteSessionAsync();
            await WaitForUiAsync(() => session.Breakpoints.Count == 1 && !session.DisplayName.Contains('['));
            Assert.Equal(1, session.Breakpoints.Single().Spec.Line);
            Assert.Equal(2, session.Files.Count);
        }
        finally
        {
            await session.Engine.StopAsync();
            if (execution is not null) await execution;
            await session.Engine.DisposeAsync();
            window.Close();
            File.Delete(path);
        }
    }

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

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BreakpointGutterIsOutsideTheTextAndClicksToggleTheClickedLine(bool showLineNumbers)
    {
        var window = new MainWindow([], initializeOnOpen: false, preferences: new UserSettings { ShowLineNumbers = showLineNumbers });
        var session = new SessionModel("PowerShell 1");
        var tab = new ScriptTab(new ScriptFile("Untitled1.ps1") { Text = "'first'\n'second'\n" });
        try
        {
            await session.Engine.InitializeAsync();
            session.Files.Add(tab);
            session.SelectedFile = tab;
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            Layout(window);
            var editor = window.FindControl<TextEditor>("ScriptEditor")!;
            var gutter = editor.TextArea.LeftMargins.OfType<Control>().SingleOrDefault(margin => margin.Name == "BreakpointGutter");
            Assert.NotNull(gutter);
            Assert.Contains(gutter, editor.TextArea.LeftMargins);
            var gutterStart = gutter.TranslatePoint(default, window)!.Value;
            var textStart = editor.TextArea.TextView.TranslatePoint(default, window)!.Value;
            Assert.True(gutterStart.X + gutter.Bounds.Width <= textStart.X);
            var caret = editor.CaretOffset;
            var y = editor.TextArea.TextView.GetVisualTopByDocumentLine(2) - editor.TextArea.TextView.VerticalOffset +
                editor.TextArea.TextView.DefaultLineHeight / 2;
            var point = gutter.TranslatePoint(new Point(gutter.Bounds.Width / 2, y), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await WaitForUiAsync(() => tab.LineBreakpoints.Count == 1);
            Assert.Equal(2, tab.LineBreakpoints.Single().Line);
            Assert.Equal(caret, editor.CaretOffset);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await WaitForUiAsync(() => tab.LineBreakpoints.Count == 0);
            var blank = gutter.TranslatePoint(new Point(gutter.Bounds.Width / 2,
                editor.TextArea.TextView.DocumentHeight + editor.TextArea.TextView.DefaultLineHeight), window)!.Value;
            window.MouseDown(blank, MouseButton.Left);
            window.MouseUp(blank, MouseButton.Left);
            Assert.Empty(tab.LineBreakpoints);
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Assert.Empty(tab.LineBreakpoints);
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DebuggerLineRenderingDoesNotOverlayMarkersOrWashOutDarkSyntax(bool paused)
    {
        var theme = new EditorTheme();
        foreach (var key in theme.Colors.Keys.Where(key => key.StartsWith("Script.")).ToArray())
            theme.Colors[key] = theme.Colors[key.Replace("Script.", "Console.")];
        theme.Colors["Script.Background"] = "#012456";
        var window = new MainWindow([], initializeOnOpen: false, preferences: new UserSettings { Theme = theme });
        var session = new SessionModel("PowerShell 1");
        var tab = new ScriptTab(new ScriptFile("Untitled1.ps1") { Text = "Get-Process -Name 'Test'\n" });
        try
        {
            tab.ToggleBreakpoint(1);
            session.Files.Add(tab);
            session.SelectedFile = tab;
            if (paused) session.DebugLocation = new(null, 1, 1, "paused");
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            Layout(window);
            var view = window.FindControl<TextEditor>("ScriptEditor")!.TextArea.TextView;
            view.EnsureVisualLines();
            var drawing = new DrawingGroup();
            using (var context = drawing.Open())
                view.BackgroundRenderers.OfType<ScriptAdornments>().Single().Draw(view, context);
            var shapes = drawing.Children.OfType<GeometryDrawing>().ToArray();
            Assert.Single(shapes);
            Assert.DoesNotContain(shapes, shape => shape.Geometry is EllipseGeometry);
            var highlight = Assert.Single(shapes, shape => shape.Geometry?.Bounds.Width == view.Bounds.Width);
            var fill = Assert.IsType<SolidColorBrush>(highlight.Brush).Color;
            var background = Color.Parse(theme.Colors["Script.Background"]);
            var alpha = fill.A / 255d;
            var composite = Color.FromRgb((byte)(fill.R * alpha + background.R * (1 - alpha)),
                (byte)(fill.G * alpha + background.G * (1 - alpha)), (byte)(fill.B * alpha + background.B * (1 - alpha)));
            Assert.True(Contrast(Color.Parse(theme.Colors["Script.Foreground"]), composite) >= 7);
            foreach (var key in new[] { "Script.Command", "Script.Parameter", "Script.Variable", "Script.String", "Script.Keyword" })
            {
                var foreground = Color.Parse(theme.Colors[key]);
                Assert.True(Contrast(foreground, composite) >= Contrast(foreground, background) * .9, key);
            }
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
            }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        var one = Luminance(first);
        var two = Luminance(second);
        return (Math.Max(one, two) + .05) / (Math.Min(one, two) + .05);
    }

    [AvaloniaFact]
    public async Task BreakpointGutterTracksScrolledAndWrappedDocumentLines()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        var tab = new ScriptTab(new ScriptFile("Untitled1.ps1") { Text = string.Join('\n', Enumerable.Range(1, 120).Select(index => $"$line{index} = '{new string('x', 250)}'")) });
        try
        {
            await session.Engine.InitializeAsync();
            session.Files.Add(tab);
            session.SelectedFile = tab;
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            Layout(window);
            var editor = window.FindControl<TextEditor>("ScriptEditor")!;
            var gutter = editor.TextArea.LeftMargins.OfType<BreakpointMargin>().Single();
            editor.ScrollToLine(60);
            Layout(window);
            var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>().Single();
            scroll.Offset = new Vector(100, scroll.Offset.Y);
            Layout(window);
            var view = editor.TextArea.TextView;
            view.EnsureVisualLines();
            Assert.True(view.VerticalOffset > 0);
            Assert.True(view.HorizontalOffset > 0);
            var y = view.GetVisualTopByDocumentLine(60) - view.VerticalOffset + view.DefaultLineHeight / 2;
            var point = gutter.TranslatePoint(new Point(gutter.Bounds.Width / 2, y), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await WaitForUiAsync(() => tab.LineBreakpoints.Count == 1);
            Assert.Equal(60, tab.LineBreakpoints.Single().Line);

            editor.WordWrap = true;
            scroll.Offset = default;
            Layout(window);
            view.EnsureVisualLines();
            var line = view.GetVisualLine(1)!;
            Assert.True(line.TextLines.Count > 1);
            y = line.VisualTop - view.VerticalOffset + line.TextLines[0].Height + line.TextLines[1].Height / 2;
            point = gutter.TranslatePoint(new Point(gutter.Bounds.Width / 2, y), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await WaitForUiAsync(() => tab.LineBreakpoints.Count == 2);
            Assert.Contains(tab.LineBreakpoints, breakpoint => breakpoint.Line == 1);
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledMarkersStayInTheGutterAndHighContrastDoesNotTintText(bool highContrast)
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        var tab = new ScriptTab(new ScriptFile("Untitled1.ps1") { Text = "$value = 42\n" });
        try
        {
            DesktopTheme.Refresh(highContrast: highContrast);
            tab.SetBreakpoint(new(BreakpointKind.Line, Line: 1, Enabled: false));
            session.Files.Add(tab);
            session.SelectedFile = tab;
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            Layout(window);
            var editor = window.FindControl<TextEditor>("ScriptEditor")!;
            var gutter = editor.TextArea.LeftMargins.OfType<BreakpointMargin>().Single();
            var drawing = new DrawingGroup();
            using (var context = drawing.Open()) gutter.Render(context);
            var marker = Assert.Single(drawing.Children.OfType<GeometryDrawing>(), shape => shape.Brush is null && shape.Pen is not null);
            Assert.True(marker.Geometry!.Bounds.Left >= 0);
            Assert.True(marker.Geometry.Bounds.Right <= gutter.Bounds.Width);
            Assert.True(Contrast(Assert.IsType<SolidColorBrush>(marker.Pen!.Brush).Color,
                Assert.IsType<SolidColorBrush>(drawing.Children.OfType<GeometryDrawing>().First().Brush).Color) >= 3);
            var background = new DrawingGroup();
            using (var context = background.Open())
                editor.TextArea.TextView.BackgroundRenderers.OfType<ScriptAdornments>().Single().Draw(editor.TextArea.TextView, context);
            Assert.Empty(background.Children);
            if (highContrast)
            {
                session.DebugLocation = new(null, 1, 1, "paused");
                using (var context = background.Open())
                    editor.TextArea.TextView.BackgroundRenderers.OfType<ScriptAdornments>().Single().Draw(editor.TextArea.TextView, context);
                Assert.Empty(background.Children);
            }
        }
        finally { DesktopTheme.Refresh(); await session.Engine.DisposeAsync(); window.Close(); }
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
            Assert.Contains(window.FindControl<TreeView>("VariablesList")!.Items.OfType<TreeViewItem>(), item => item.Tag is DebugValue { Name: "$localValue", Value: "21" });
            Assert.Equal("42", ((DebugValue)window.FindControl<TreeView>("WatchesList")!.Items.OfType<TreeViewItem>().Single().Tag!).Value);
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
            await WaitForUiAsync(() => file.LineBreakpoints.Single().Enabled == false && !session.Breakpoints.Single().Spec.Enabled);
            Assert.Equal(3, file.File.Breakpoints.Single());
            Assert.False(session.Breakpoints.Single().Spec.Enabled);

            session.Engine.Resume(System.Management.Automation.DebuggerResumeAction.Continue);
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForUiAsync(() => session.DebugLocation is null && !window.FindControl<TextEditor>("ScriptEditor")!.IsReadOnly);
            Assert.Empty(window.FindControl<TreeView>("VariablesList")!.Items);
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

    private static async Task WaitForUiAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(10));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "The debugger UI did not reach the expected state.");
    }

    [AvaloniaFact]
    public async Task DebuggerTreesExpandFrameSelectionInspectsCallerAndPausedTabCompletes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iseberg-tree-{Guid.NewGuid():N}.ps1");
        var previousPolicy = Environment.GetEnvironmentVariable("PSExecutionPolicyPreference");
        await File.WriteAllTextAsync(path, """
            function Inner {
                $shared = 'inner'
                $innerOnly = 5
                $done = $true
            }
            function Outer {
                $shared = 'outer'
                $obj = [pscustomobject]@{ Child = [pscustomobject]@{ Answer = 42 } }
                Inner
            }
            Outer
            """);
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        try
        {
            await session.Engine.InitializeAsync();
            if (OperatingSystem.IsWindows())
                await session.Engine.ExecuteAsync("Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force");
            session.Watches.Add("$obj");
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            await window.OpenFileAsync(path);
            Layout(window);
            await session.Engine.SetBreakpointsAsync(path, [4]);
            var execution = session.Engine.ExecuteAsync("", path);
            await WaitForUiAsync(() => session.DebugSnapshot is not null);
            var watches = window.FindControl<TreeView>("WatchesList")!;
            var root = watches.Items.OfType<TreeViewItem>().Single();
            root.IsExpanded = true;
            await WaitForUiAsync(() => root.Items.OfType<TreeViewItem>().Any(item => item.Tag is DebugValue { Name: "Child" }));
            var child = root.Items.OfType<TreeViewItem>().Single(item => item.Tag is DebugValue { Name: "Child" });
            child.IsExpanded = true;
            await WaitForUiAsync(() => child.Items.OfType<TreeViewItem>().Any(item => item.Tag is DebugValue { Name: "Answer", Value: "42" }));

            var stack = window.FindControl<ListBox>("CallStackList")!;
            stack.SelectedItem = stack.Items.OfType<DebugFrame>().Single(frame => frame.FunctionName == "Outer");
            await WaitForUiAsync(() => session.DebugSnapshot?.Variables.Any(value => value.Name == "$shared" && value.Value == "outer") == true);
            Assert.Contains("Outer", window.FindControl<TextBlock>("DebuggerScope")!.Text);
            Assert.Contains("stopped frame", window.FindControl<TextBlock>("DebuggerScope")!.Text);
            Assert.DoesNotContain(session.DebugSnapshot!.Variables, value => value.Name == "$innerOnly");

            var console = window.FindControl<TextEditor>("ConsoleEditor")!;
            session.Input = "$innerO";
            console.CaretOffset = console.Document.TextLength;
            console.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
            await WaitForUiAsync(() => session.Input == "$innerOnly");
            session.Engine.Resume(System.Management.Automation.DebuggerResumeAction.Continue);
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForUiAsync(() => session.DebugSnapshot is null);
            Assert.Empty(window.FindControl<TreeView>("VariablesList")!.Items);
        }
        finally
        {
            await session.Engine.DisposeAsync();
            window.Close();
            File.Delete(path);
            Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", previousPolicy);
        }
    }

    [AvaloniaFact]
    public async Task DebuggerConfigurationRestoresToANewSessionAndWatchEditsPersist()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iseberg-persist-{Guid.NewGuid():N}.ps1");
        var settingsPath = path + ".json";
        await File.WriteAllTextAsync(path, "$n = 1\n$n = 2\n");
        var spec = new BreakpointSpec(BreakpointKind.Line, path, Line: 2, Condition: "$n -eq 1", Enabled: false);
        var settings = new UserSettings
        {
            DebuggerSessions = [new() { Name = "PowerShell 1", Watches = ["$n"], Breakpoints = [spec, new(BreakpointKind.Command, Target: "Get-Command")] }]
        };
        var window = new MainWindow([], initializeOnOpen: false, preferences: settings, settingsPath: settingsPath);
        try
        {
            Layout(window);
            var menu = window.FindControl<Menu>("WorkbenchMenu")!;
            var newSession = menu.Items.OfType<MenuItem>().SelectMany(item => item.Items.OfType<MenuItem>()).Single(item => item.Tag as string == "NewSession");
            newSession.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitForUiAsync(() => window.Workbench.SelectedSession?.Commands.Count > 0);
            var session = window.Workbench.SelectedSession!;
            Assert.Equal("$n", session.Watches.Single());
            Assert.Contains(session.Breakpoints, breakpoint => breakpoint.Spec == spec);
            await window.OpenFileAsync(path);
            Assert.Equal(spec, session.SelectedFile!.LineBreakpoints.Single());
            window.FindControl<TextBox>("WatchExpression")!.Text = "$n * 2";
            var addWatch = window.FindControl<Border>("DebuggerPane")!.GetLogicalDescendants().OfType<Button>().Single(button => button.Tag as string == "AddWatch");
            addWatch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForUiAsync(() => File.Exists(settingsPath));
            var saved = await UserSettings.LoadAsync(settingsPath);
            Assert.Equal(["$n", "$n * 2"], saved.DebuggerSessions.Single().Watches);
            Assert.Equal(2, saved.DebuggerSessions.Single().Breakpoints.Count);
            Assert.Contains(spec, saved.DebuggerSessions.Single().Breakpoints);
        }
        finally
        {
            foreach (var session in window.Workbench.Sessions) await session.Engine.DisposeAsync();
            window.Close();
            File.Delete(path);
            File.Delete(settingsPath);
        }
    }

    [AvaloniaFact]
    public void CompletionCannotModifyAReadOnlyScript()
    {
        var editor = new TextEditor { Text = "$var", IsReadOnly = true };
        var completion = new PowerShellCompletion(new System.Management.Automation.CompletionResult("$variable"));
        completion.Complete(editor.TextArea, new SimpleSegment(0, 4), EventArgs.Empty);
        Assert.Equal("$var", editor.Text);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningBreakpointTogglePausesFirstAndPreservesTheOriginallyRequestedSourceLine(bool gutterClick)
    {
        var path = Path.Combine(Path.GetTempPath(), $"iseberg-running-edit-{Guid.NewGuid():N}.ps1");
        var previousPolicy = Environment.GetEnvironmentVariable("PSExecutionPolicyPreference");
        await File.WriteAllTextAsync(path, "Read-Host 'blocked-command'\n$n = 1\n$n = 2\n");
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        try
        {
            await session.Engine.InitializeAsync();
            if (OperatingSystem.IsWindows())
                await session.Engine.ExecuteAsync("Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force");
            var input = new TaskCompletionSource<InputRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Engine.InputRequested += request => input.TrySetResult(request);
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            await window.OpenFileAsync(path);
            Layout(window);
            var execution = session.Engine.ExecuteAsync("", path);
            var request = await input.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var editor = window.FindControl<TextEditor>("ScriptEditor")!;
            if (gutterClick)
            {
                var gutter = editor.TextArea.LeftMargins.OfType<BreakpointMargin>().Single();
                var view = editor.TextArea.TextView;
                var y = view.GetVisualTopByDocumentLine(3) - view.VerticalOffset + view.DefaultLineHeight / 2;
                var point = gutter.TranslatePoint(new Point(gutter.Bounds.Width / 2, y), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
            }
            else
            {
                editor.TextArea.Caret.Line = 3;
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F9 });
            }
            await WaitForUiAsync(() => session.EditingBreakpoints);
            Assert.Equal(Iseberg.Core.SessionState.Running, session.Engine.State);
            Assert.Empty(session.SelectedFile!.LineBreakpoints);
            Assert.Contains("next PowerShell statement", window.FindControl<TextBlock>("StatusText")!.Text);
            request.Response.SetResult("continue");
            await WaitForUiAsync(() => session.Breakpoints.Any(breakpoint => breakpoint.Spec.Line == 3));
            Assert.True(session.Engine.IsDebuggerPaused);
            Assert.Equal(3, session.SelectedFile.LineBreakpoints.Single().Line);
            await session.Engine.StopAsync();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await session.Engine.DisposeAsync();
            window.Close();
            File.Delete(path);
            Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", previousPolicy);
        }
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

    [AvaloniaTheory]
    [InlineData("Top", true)]
    [InlineData("Right", true)]
    [InlineData("Maximized", true)]
    [InlineData("Top", false)]
    [InlineData("Right", false)]
    [InlineData("Maximized", false)]
    public async Task DebuggerDocksToTheRightAtFullHeightInEveryEditorLayout(string layout, bool showCommands)
    {
        var window = new MainWindow([], initializeOnOpen: false,
            preferences: new UserSettings { Layout = layout, ShowCommands = showCommands });
        var session = new SessionModel("PowerShell 1") { DebuggerPaneVisible = true };
        try
        {
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            Layout(window);
            var outer = window.FindControl<Grid>("OuterGrid")!;
            var editor = window.FindControl<Grid>("PaneGrid")!;
            var debugger = window.FindControl<Border>("DebuggerPane")!;
            var divider = window.FindControl<GridSplitter>("DebuggerSplitter")!;
            Assert.Same(outer, debugger.Parent);
            Assert.True(debugger.IsVisible);
            Assert.True(divider.IsVisible);
            Assert.True(debugger.Bounds.Left >= editor.Bounds.Right);
            Assert.Equal(0, debugger.Bounds.Top);
            Assert.Equal(outer.Bounds.Height, debugger.Bounds.Height);
            Assert.Equal(showCommands, window.FindControl<Border>("CommandsPane")!.IsVisible);
            Assert.Equal(2, Grid.GetColumn(debugger));
            Assert.Equal(1, Grid.GetColumn(divider));
            if (showCommands)
                Assert.True(window.FindControl<Border>("CommandsPane")!.Bounds.Left >= debugger.Bounds.Right);
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task DockVisibilityCollapsesUnusedColumnsAndRetainsResizedDebuggerWidth()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1") { DebuggerPaneVisible = true };
        try
        {
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            Layout(window);
            var outer = window.FindControl<Grid>("OuterGrid")!;
            var debugger = window.FindControl<Border>("DebuggerPane")!;
            var commands = window.FindControl<Border>("CommandsPane")!;
            outer.ColumnDefinitions[Grid.GetColumn(debugger)].Width = new GridLength(420);
            Layout(window);
            var actions = window.FindControl<Menu>("WorkbenchMenu")!.Items.OfType<MenuItem>()
                .SelectMany(item => item.Items.OfType<MenuItem>()).ToArray();
            var commandAction = actions.First(item => item.Tag as string == "Commands");
            var debugAction = actions.First(item => item.Tag as string == "DebuggerPanes");
            commandAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Layout(window);
            Assert.False(commands.IsVisible);
            Assert.Equal(420, debugger.Bounds.Width);
            Assert.Equal(0, outer.ColumnDefinitions[3].ActualWidth);
            Assert.Equal(0, outer.ColumnDefinitions[4].ActualWidth);
            debugAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Layout(window);
            Assert.False(debugger.IsVisible);
            Assert.False(window.FindControl<GridSplitter>("DebuggerSplitter")!.IsVisible);
            Assert.Equal(outer.Bounds.Width, window.FindControl<Grid>("PaneGrid")!.Bounds.Width);
            commandAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Layout(window);
            Assert.True(commands.IsVisible);
            Assert.Equal(2, Grid.GetColumn(commands));
            Assert.Equal(1, Grid.GetColumn(window.FindControl<GridSplitter>("CommandSplitter")!));
            debugAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Layout(window);
            Assert.True(debugger.IsVisible);
            Assert.Equal(420, debugger.Bounds.Width);
            Assert.Equal(4, Grid.GetColumn(commands));
            var other = new SessionModel("PowerShell 2");
            window.Workbench.Sessions.Add(other);
            window.Workbench.SelectedSession = other;
            Layout(window);
            Assert.False(debugger.IsVisible);
            Assert.True(commands.IsVisible);
            Assert.Equal(2, Grid.GetColumn(commands));
            window.Workbench.SelectedSession = session;
            Layout(window);
            Assert.True(debugger.IsVisible);
            Assert.Equal(420, debugger.Bounds.Width);
        }
        finally
        {
            foreach (var tab in window.Workbench.Sessions) await tab.Engine.DisposeAsync();
            window.Close();
        }
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
