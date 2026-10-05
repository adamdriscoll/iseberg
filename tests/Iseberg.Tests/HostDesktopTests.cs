using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class HostDesktopTests
{
    [AvaloniaTheory]
    [InlineData("Classic")]
    [InlineData("Devolutions")]
    public async Task MultipleChoiceDialogSubmitsSelectionAndStopClosesPendingInput(string consoleMode)
    {
        var owner = new MainWindow([], initializeOnOpen: false, preferences: new() { ConsoleMode = consoleMode });
        var session = new SessionModel("PowerShell 1");
        await session.Engine.InitializeAsync();
        owner.Workbench.Sessions.Add(session);
        owner.Workbench.SelectedSession = session;
        owner.Show();
        try
        {
            var script = """
                $choices = [System.Collections.ObjectModel.Collection[System.Management.Automation.Host.ChoiceDescription]]::new()
                $choices.Add([System.Management.Automation.Host.ChoiceDescription]::new('First', 'first help'))
                $choices.Add([System.Management.Automation.Host.ChoiceDescription]::new('Second', 'second help'))
                $selected = $Host.UI.PromptForChoice('Pick', 'Select several', $choices, [int[]]@(1))
                "selected=$($selected -join ',')"
                """;
            var execution = session.Engine.ExecuteAsync(script);
            await WaitFor(() => owner.OwnedWindows.OfType<HostInputWindow>().Any());
            var dialog = owner.OwnedWindows.OfType<HostInputWindow>().Single();
            Assert.True(dialog.FindControl<CheckBox>("HostChoice1")!.IsChecked);
            dialog.FindControl<CheckBox>("HostChoice0")!.IsChecked = true;
            dialog.FindControl<Button>("HostInputSubmit")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            session.FlushOutput();
            Assert.Contains("selected=0,1", session.ClassicConsole.Document.Text);
            execution = session.Engine.ExecuteAsync("Read-Host -AsSecureString 'password'");
            await WaitFor(() => owner.OwnedWindows.OfType<HostInputWindow>().Any());
            dialog = owner.OwnedWindows.OfType<HostInputWindow>().Single();
            Assert.Equal('*', dialog.FindControl<TextBox>("HostInput")!.PasswordChar);
            await session.Engine.StopAsync();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitFor(() => !owner.OwnedWindows.OfType<HostInputWindow>().Any());
        }
        finally { await session.Engine.DisposeAsync(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task InputCancelAndSingleChoiceRequireSelection()
    {
        var owner = new MainWindow([], initializeOnOpen: false);
        owner.Show();
        try
        {
            var request = new InputRequest("Single", "Pick") { Choices = [new("First", "help"), new("Second", "")] };
            var dialog = new HostInputWindow(request);
            var response = dialog.ShowDialog<string?>(owner);
            Assert.False(dialog.FindControl<Button>("HostInputSubmit")!.IsEnabled);
            dialog.FindControl<RadioButton>("HostChoice1")!.IsChecked = true;
            Assert.True(dialog.FindControl<Button>("HostInputSubmit")!.IsEnabled);
            dialog.FindControl<Button>("HostInputSubmit")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("1", await response);
            dialog = new HostInputWindow(new("Text", "Enter a value"));
            response = dialog.ShowDialog<string?>(owner);
            dialog.FindControl<Button>("HostInputCancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(await response);
        }
        finally { owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Classic")]
    [InlineData("Devolutions")]
    public async Task NestedPromptConsoleRemainsEditableAndRestoresPromptOnExit(string consoleMode)
    {
        var owner = new MainWindow([], initializeOnOpen: false, preferences: new() { ConsoleMode = consoleMode });
        var session = new SessionModel("PowerShell 1");
        await session.Engine.InitializeAsync();
        owner.Workbench.Sessions.Add(session);
        owner.Workbench.SelectedSession = session;
        owner.Show();
        owner.Measure(new Size(1200, 800));
        owner.Arrange(new Rect(0, 0, 1200, 800));
        try
        {
            var execution = session.Engine.ExecuteAsync("$Host.EnterNestedPrompt(); 'returned'");
            var editor = owner.FindControl<TextEditor>("ConsoleEditor")!;
            await WaitFor(() => session.Engine.IsNestedPromptActive && !editor.IsReadOnly);
            session.FlushOutput();
            Assert.Contains("[Nested 1]: PS> ", session.ConsoleDocument.Text);
            Assert.False(session.Console.CanInsert(0));
            session.Input = "Write-Host 'nested-ui-output'";
            editor.CaretOffset = editor.Document.TextLength;
            editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await WaitFor(() => session.Engine.IsNestedPromptActive && session.Input == "");
            session.FlushOutput();
            Assert.Contains("nested-ui-output", session.ClassicConsole.Document.Text);
            session.Input = "exit";
            editor.CaretOffset = editor.Document.TextLength;
            editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitFor(() => !editor.IsReadOnly && session.Engine.State == SessionState.Ready);
            session.FlushOutput();
            Assert.EndsWith(session.Engine.Prompt, session.ConsoleDocument.Text);
        }
        finally { await session.Engine.DisposeAsync(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ShowCommandErrorPopupDisplaysErrorsAndStopClosesIt()
    {
        var owner = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        await session.Engine.InitializeAsync();
        owner.Workbench.Sessions.Add(session);
        owner.Workbench.SelectedSession = session;
        session.Engine.ShowCommandRequested += request => request.Response.TrySetResult("Write-Error 'visible-popup-error'");
        owner.Show();
        try
        {
            var execution = session.Engine.ExecuteAsync("Show-Command Get-Process -ErrorPopup");
            await WaitFor(() => owner.OwnedWindows.Any(window => window.Name == "CommandErrorPopup"));
            var popup = owner.OwnedWindows.Single(window => window.Name == "CommandErrorPopup");
            Assert.Contains("visible-popup-error", popup.FindControl<TextBox>("CommandErrorMessage")!.Text);
            Assert.True(popup.FindControl<TextBox>("CommandErrorMessage")!.IsReadOnly);
            await session.Engine.StopAsync();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitFor(() => !owner.OwnedWindows.Any(window => window.Name == "CommandErrorPopup"));
        }
        finally { await session.Engine.DisposeAsync(); owner.Close(); }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Host UI transition did not complete.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task AnsiStyleReachesConsoleTextRuns()
    {
        var owner = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        await session.Engine.InitializeAsync();
        owner.Workbench.Sessions.Add(session);
        owner.Workbench.SelectedSession = session;
        owner.Show();
        try
        {
            session.Console.Append(new("\x1b[31;44;1;4mcolored\x1b[0m\n"));
            session.Console.ShowPrompt("PS> ");
            owner.Measure(new Size(1200, 800));
            owner.Arrange(new Rect(0, 0, 1200, 800));
            var editor = owner.FindControl<TextEditor>("ConsoleEditor")!;
            editor.TextArea.TextView.Redraw();
            editor.TextArea.TextView.EnsureVisualLines();
            var styled = editor.TextArea.TextView.VisualLines.SelectMany(line => line.Elements)
                .First(element => element.DocumentLength > 0).TextRunProperties;
            Assert.Equal(Color.Parse("#800000"), Assert.IsAssignableFrom<ISolidColorBrush>(styled.ForegroundBrush).Color);
            Assert.Equal(Color.Parse("#000080"), Assert.IsAssignableFrom<ISolidColorBrush>(styled.BackgroundBrush).Color);
            Assert.Equal(FontWeight.Bold, styled.Typeface.Weight);
            Assert.Contains(styled.TextDecorations, decoration => decoration.Location == TextDecorationLocation.Underline);
        }
        finally { await session.Engine.DisposeAsync(); owner.Close(); }
    }
}
