using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class CommandFormTests
{
    private const string Function = """
        function Test-IsebergForm {
            [CmdletBinding(DefaultParameterSetName='ByName', SupportsShouldProcess=$true)]
            param(
                [Parameter(Mandatory, ParameterSetName='ByName', Position=0,
                    ValueFromPipelineByPropertyName, HelpMessage='Enter a display name.')]
                [Alias('Label')] [string] $Name,
                [Parameter(Mandatory, ParameterSetName='ById')] [int] $Id,
                [ValidateSet('Fast', 'Safe', IgnoreCase=$false)] [string] $Mode,
                [System.DayOfWeek] $Day,
                [System.DayOfWeek[]] $Days,
                [string[]] $Items,
                [bool[]] $Flags,
                [bool] $Enabled,
                [switch] $Force,
                [AllowEmptyString()] [string] $Empty
            )
            "set=$($PSCmdlet.ParameterSetName);name=$Name;id=$Id;mode=$Mode;day=$Day;items=$($Items -join '|');enabled=$Enabled;force=$Force;empty=<$Empty>"
        }
        Set-Alias Invoke-IsebergForm Test-IsebergForm
        """;

    [Fact]
    public async Task MetadataIncludesSetsTypesValidationAliasesAndCommonParameters()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ExecuteAsync(Function);
        var description = await session.GetCommandFormAsync("Test-IsebergForm");
        var byName = Assert.Single(description.ParameterSets, set => set.IsDefault);
        Assert.Equal("ByName", byName.Name);
        var name = Assert.Single(byName.Parameters, p => p.Name == "Name");
        Assert.True(name.IsMandatory);
        Assert.Equal(0, name.Position);
        Assert.True(name.AcceptsPipelineInput);
        Assert.Contains("Label", name.Aliases);
        Assert.Equal("Enter a display name.", name.HelpMessage);
        Assert.Contains(byName.Parameters, p => p.Name == "Mode" && p.Kind == CommandParameterKind.Choice &&
            p.Choices.SequenceEqual(new[] { "Fast", "Safe" }) && !p.ChoicesIgnoreCase);
        Assert.Contains(byName.Parameters, p => p.Name == "Day" && p.Choices.Contains("Friday"));
        Assert.Contains(byName.Parameters, p => p.Name == "Items" && p.IsArray);
        Assert.Contains(byName.Parameters, p => p.Name == "Flags" && p.IsArray && p.Kind == CommandParameterKind.Boolean);
        Assert.Contains(byName.Parameters, p => p.Name == "Enabled" && p.Kind == CommandParameterKind.Boolean);
        Assert.Contains(byName.Parameters, p => p.Name == "Force" && p.Kind == CommandParameterKind.Switch);
        Assert.Contains(byName.Parameters, p => p.Name == "WhatIf" && p.IsCommon);
        Assert.Contains(byName.Parameters, p => p.Name == "Verbose" && p.IsCommon);
        Assert.Contains(byName.Parameters, p => p.Name == "Empty" && p.AllowsEmptyString);
        Assert.DoesNotContain(byName.Parameters, p => p.Name == "Id");
        var alias = await session.GetCommandFormAsync("Invoke-IsebergForm");
        Assert.Equal("Invoke-IsebergForm", alias.InvocationName);
        Assert.Equal(description.ParameterSets.Select(s => s.Name), alias.ParameterSets.Select(s => s.Name));
    }

    [Fact]
    public async Task GeneratedCommandBindsTypedValuesAndDoesNotExecuteLiteralText()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ExecuteAsync(Function);
        var form = new CommandForm(await session.GetCommandFormAsync("Test-IsebergForm"));
        Assert.Equal(new[] { "Name" }, form.Build().MissingParameters);
        Set(form, "Name", "O'Brien'; $script:Injected = $true; '");
        Set(form, "Mode", "Safe");
        Set(form, "Day", "Friday");
        Set(form, "Items", "one two\r\n$literal\nthree's");
        Set(form, "Flags", "True\nFalse");
        Set(form, "Enabled", "");
        form.Value("Enabled").Boolean = false;
        Set(form, "Force", "");
        form.Value("Force").Boolean = false;
        Set(form, "Empty", "");
        var result = form.Build();
        Assert.True(result.IsValid);
        Assert.Contains("-Force:$false", result.Script);
        Assert.Contains("-Items @('one two', '$literal', 'three''s')", result.Script);
        Assert.Contains("-Flags @($true, $false)", result.Script);
        var output = new ConcurrentQueue<OutputEntry>();
        session.Output += output.Enqueue;
        await session.ExecuteAsync(result.Script);
        await session.ExecuteAsync("if ($script:Injected) { throw 'injected' } else { 'literal-safe' }");
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        var text = string.Concat(output.Where(e => e.Kind == OutputKind.Output).Select(e => e.Text));
        Assert.Contains("name=O'Brien'; $script:Injected = $true; '", text);
        Assert.Contains("items=one two|$literal|three's;enabled=False;force=False;empty=<>", text);
        Assert.Contains("literal-safe", text);
    }

    [Fact]
    public async Task SwitchingSetsRetainsSharedValuesButExcludesHiddenParameters()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ExecuteAsync(Function);
        var form = new CommandForm(await session.GetCommandFormAsync("Test-IsebergForm"));
        Set(form, "Name", "Ada");
        Set(form, "Mode", "Fast");
        form.SelectSet("ById");
        Assert.Equal(new[] { "Id" }, form.Build().MissingParameters);
        Set(form, "Id", "42");
        var result = form.Build();
        Assert.True(result.IsValid);
        Assert.DoesNotContain("-Name", result.Script);
        Assert.Contains("-Id '42'", result.Script);
        Assert.Contains("-Mode 'Fast'", result.Script);
        var output = new ConcurrentQueue<OutputEntry>();
        session.Output += output.Enqueue;
        await session.ExecuteAsync(result.Script);
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("set=ById;name=;id=42"));
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        form.SelectSet("ByName");
        Assert.Contains("-Name 'Ada'", form.Build().Script);
        Assert.DoesNotContain("-Id", form.Build().Script);
    }

    [Fact]
    public async Task ExpressionsAndChoicesAreValidatedAndExplicitlyIncludedEmptyValuesAreRetained()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ExecuteAsync(Function);
        var form = new CommandForm(await session.GetCommandFormAsync("Test-IsebergForm"));
        Set(form, "Name", "$env:USER");
        Assert.Contains("-Name '$env:USER'", form.Build().Script);
        form.Value("Name").IsExpression = true;
        Set(form, "Name", "'Ada' + ' Lovelace'");
        Set(form, "Mode", "fast");
        Assert.Equal(new[] { "Mode" }, form.Build().InvalidChoices);
        Set(form, "Mode", "Fast");
        Set(form, "Empty", "");
        Assert.True(form.Build().IsValid);
        Assert.Contains("-Empty ''", form.Build().Script);
        Set(form, "Name", "'unterminated");
        Assert.Equal(new[] { "Name" }, form.Build().InvalidExpressions);
        Set(form, "Name", "");
        Assert.False(form.Build().IsValid);
        form.Value("Name").Included = false;
        Assert.Contains("Name", form.Build().MissingParameters);
        form.Value("Name").IsExpression = false;
        Set(form, "Name", "Ada");
        Set(form, "Items", "@('one', 'two')");
        form.Value("Items").IsExpression = true;
        var output = new ConcurrentQueue<OutputEntry>();
        session.Output += output.Enqueue;
        await session.ExecuteAsync(form.Build().Script);
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("items=one|two"));
    }

    [Fact]
    public async Task ParameterlessAndRequiredSwitchCommandsProduceInvocableForms()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ExecuteAsync("""
            function Test-NoParameters { 'no-parameters' }
            function Test-RequiredSwitch {
                param([Parameter(Mandatory)] [switch] $Enabled)
                "required-switch=$Enabled"
            }
            """);
        var noParameters = new CommandForm(await session.GetCommandFormAsync("Test-NoParameters"));
        Assert.True(noParameters.Build().IsValid);
        Assert.Empty(noParameters.SelectedSet.Parameters);
        var requiredSwitch = new CommandForm(await session.GetCommandFormAsync("Test-RequiredSwitch"));
        Assert.Equal(new[] { "Enabled" }, requiredSwitch.Build().MissingParameters);
        Set(requiredSwitch, "Enabled", "");
        requiredSwitch.Value("Enabled").Boolean = false;
        Assert.True(requiredSwitch.Build().IsValid);
        var output = new ConcurrentQueue<OutputEntry>();
        session.Output += output.Enqueue;
        await session.ExecuteAsync(noParameters.Build().Script);
        await session.ExecuteAsync(requiredSwitch.Build().Script);
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("no-parameters"));
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("required-switch=False"));
    }

    [Theory]
    [InlineData("Get-Process -Name 'pwsh'", 20, "Get-Process")]
    [InlineData("Get-Process | Sort-Object Name", 28, "Sort-Object")]
    [InlineData("& 'Get-Process' -Name 'pwsh'", 24, "Get-Process")]
    [InlineData("Write-Output (Get-Date -Format o)", 30, "Get-Date")]
    [InlineData("$command = 'Get-Process'", 12, null)]
    public void ShowCommandContextResolvesTheCommandRatherThanItsArgument(string script, int caret, string? expected)
    {
        Assert.Equal(expected, EditorAnalysis.CommandNameAtCaret(script, caret));
    }

    [Fact]
    public async Task MissingCommandsAndBusyOrCancelledQueriesDoNotReturnSuccessShapedMetadata()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetCommandFormAsync("No-IsebergCommand"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetCommandFormAsync("Get-Process", cancellationToken: cancellation.Token));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += state => { if (state == SessionState.Running) started.TrySetResult(); };
        var running = session.ExecuteAsync("Start-Sleep -Seconds 30");
        await started.Task;
        using var waiting = new CancellationTokenSource();
        var query = session.GetCommandFormAsync("Get-Process", cancellationToken: waiting.Token);
        waiting.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        await session.StopAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        var description = await session.GetCommandFormAsync("Get-Process", "Microsoft.PowerShell.Management");
        Assert.Equal("Microsoft.PowerShell.Management\\Get-Process", description.InvocationName);
    }

    [AvaloniaFact]
    public async Task ExpandableEditorsUpdatePreviewAndParameterSetPickerPreservesValues()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ExecuteAsync(Function);
        var view = new CommandFormView();
        var form = new CommandForm(await session.GetCommandFormAsync("Test-IsebergForm"));
        view.ShowCommand(form);
        var window = new Window { Content = view, Width = 400, Height = 650 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(view.Result!.IsValid);
        var name = Parameter(view, "Name");
        Assert.True(name.IsExpanded);
        Input(name, "Name").Text = "Ada";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(view.Result.IsValid);
        Assert.Contains("-Name 'Ada'", view.GetCommand());
        var mode = Parameter(view, "Mode");
        Assert.False(mode.IsExpanded);
        mode.IsExpanded = true;
        var choices = mode.GetLogicalDescendants().OfType<ComboBox>().Single();
        choices.SelectedItem = "Safe";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Contains("-Mode 'Safe'", view.GetCommand());
        var expression = mode.GetLogicalDescendants().OfType<CheckBox>()
            .Single(c => AutomationProperties.GetName(c)?.Contains(UiText.Get("PowerShellExpression")) == true);
        expression.IsChecked = true;
        Input(mode, "Mode").Text = "$mode";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(view.Result.IsValid);
        expression.IsChecked = false;
        Assert.Null(choices.SelectedItem);
        Assert.False(view.Result.IsValid);
        choices.SelectedItem = "Safe";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(view.Result.IsValid);
        var picker = view.FindControl<ComboBox>("ParameterSetPicker")!;
        picker.SelectedItem = form.Description.ParameterSets.Single(s => s.Name == "ById");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(view.Result.IsValid);
        Assert.DoesNotContain("-Name", view.Result.Script);
        Assert.Contains("-Mode 'Safe'", view.Result.Script);
        Input(Parameter(view, "Id"), "Id").Text = "42";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(view.Result.IsValid);
        picker.SelectedItem = form.Description.ParameterSets.Single(s => s.Name == "ByName");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("Ada", Input(Parameter(view, "Name"), "Name").Text);
        Assert.True(view.Result.IsValid);
        Assert.DoesNotContain("-Id", view.Result.Script);
        var days = Parameter(view, "Days");
        days.IsExpanded = true;
        var multiChoice = days.GetLogicalDescendants().OfType<ListBox>().Single();
        multiChoice.SelectedItems!.Add("Monday");
        multiChoice.SelectedItems.Add("Friday");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Contains("-Days @('Monday', 'Friday')", view.GetCommand());
        var common = view.GetLogicalDescendants().OfType<Expander>()
            .Single(e => e.Header as string == UiText.Get("CommonParameters"));
        Assert.False(common.IsExpanded);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PaneInsertRunAndCopyUseTheSameValidatedFormAndSessionsKeepSeparateDrafts()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var first = new SessionModel("First");
        var second = new SessionModel("Second");
        try
        {
            foreach (var session in new[] { first, second })
            {
                await session.Engine.InitializeAsync();
                await session.Engine.ExecuteAsync(Function);
                session.Commands = await session.Engine.GetCommandsAsync();
                var file = new ScriptTab(new ScriptFile("Untitled.ps1"));
                session.Files.Add(file);
                session.SelectedFile = file;
                window.Workbench.Sessions.Add(session);
            }
            window.Workbench.SelectedSession = first;
            window.Show();
            var list = window.FindControl<ListBox>("CommandList")!;
            var view = window.FindControl<CommandFormView>("CommandForm")!;
            list.SelectedItem = first.Commands.Single(c => c.Name == "Test-IsebergForm");
            await WaitFor(() => view.Form is not null);
            Assert.False(window.FindControl<Button>("CommandRunButton")!.IsEnabled);
            Assert.False(window.FindControl<Button>("CommandCopyButton")!.IsEnabled);
            Input(Parameter(view, "Name"), "Name").Text = "First name";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var expected = view.GetCommand();
            Assert.True(window.FindControl<Button>("CommandRunButton")!.IsEnabled);
            window.FindControl<Button>("CommandInsertButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(expected + " ", first.SelectedFile!.Document.Text);
            window.FindControl<Button>("CommandCopyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(expected, await window.Clipboard!.TryGetTextAsync());
            var output = new ConcurrentQueue<OutputEntry>();
            first.Engine.Output += output.Enqueue;
            window.FindControl<Button>("CommandRunButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => output.Any(entry => entry.Kind == OutputKind.Output && entry.Text.Contains("name=First name")));
            await WaitFor(() => first.Engine.State == SessionState.Ready);
            Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
            window.Workbench.SelectedSession = second;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Null(view.Form);
            list.SelectedItem = second.Commands.Single(c => c.Name == "Test-IsebergForm");
            await WaitFor(() => view.Form is not null);
            Assert.False(view.Result!.IsValid);
            Input(Parameter(view, "Name"), "Name").Text = "Second name";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.Workbench.SelectedSession = first;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(expected, view.GetCommand());
        }
        finally
        {
            window.Close();
            await first.Engine.DisposeAsync();
            await second.Engine.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ControlF1OpensTheCommandAtTheArgumentAndRunsTheGeneratedDialogCommand()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("First");
        try
        {
            await session.Engine.InitializeAsync();
            await session.Engine.ExecuteAsync(Function);
            var file = new ScriptTab(new ScriptFile("Untitled.ps1") { Text = "Test-IsebergForm -Name 'existing'" });
            session.Files.Add(file);
            session.SelectedFile = file;
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            window.Show();
            var editor = window.FindControl<AvaloniaEdit.TextEditor>("ScriptEditor")!;
            editor.CaretOffset = editor.Text.Length - 2;
            editor.TextArea.Focus();
            var key = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.F1, KeyModifiers = KeyModifiers.Control
            };
            editor.TextArea.RaiseEvent(key);
            await WaitFor(() => window.OwnedWindows.OfType<ShowCommandWindow>().Any());
            Assert.True(key.Handled);
            var dialog = window.OwnedWindows.OfType<ShowCommandWindow>().Single();
            var view = dialog.FindControl<CommandFormView>("ShowCommandForm")!;
            Assert.Equal("Test-IsebergForm", view.Form!.Description.Name);
            Input(Parameter(view, "Name"), "Name").Text = "From dialog";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var output = new ConcurrentQueue<OutputEntry>();
            session.Engine.Output += output.Enqueue;
            dialog.FindControl<Button>("ShowCommandRun")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => output.Any(entry => entry.Kind == OutputKind.Output && entry.Text.Contains("name=From dialog")));
            await WaitFor(() => session.Engine.State == SessionState.Ready);
            Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
            Assert.Equal("Test-IsebergForm -Name 'existing'", file.Document.Text);
        }
        finally
        {
            window.Close();
            await session.Engine.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task RapidSelectionCannotReplaceTheLatestFormOrCarryItIntoAnotherTab()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var first = new SessionModel("First");
        var second = new SessionModel("Second");
        try
        {
            foreach (var session in new[] { first, second })
            {
                await session.Engine.InitializeAsync();
                session.Commands = await session.Engine.GetCommandsAsync();
                window.Workbench.Sessions.Add(session);
            }
            window.Workbench.SelectedSession = first;
            window.Show();
            var list = window.FindControl<ListBox>("CommandList")!;
            var view = window.FindControl<CommandFormView>("CommandForm")!;
            list.SelectedItem = first.Commands.Single(c => c.Name == "Get-Process");
            list.SelectedItem = first.Commands.Single(c => c.Name == "Get-Date");
            list.SelectedItem = first.Commands.Single(c => c.Name == "Write-Output");
            await WaitFor(() => view.Form?.Description.Name == "Write-Output");
            list.SelectedItem = first.Commands.Single(c => c.Name == "Get-Process");
            window.Workbench.SelectedSession = second;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            list.SelectedItem = second.Commands.Single(c => c.Name == "Get-Date");
            await WaitFor(() => view.Form?.Description.Name == "Get-Date");
            Assert.Equal("Get-Date", second.SelectedCommand);
            Assert.Equal("Get-Process", first.SelectedCommand);
            Assert.Equal("Get-Date", view.Form!.Description.Name);
        }
        finally
        {
            window.Close();
            await first.Engine.DisposeAsync();
            await second.Engine.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ShowCommandDialogUsesTheSharedFormAndReturnsGeneratedTextWithoutExecuting()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ExecuteAsync(Function);
        var form = new CommandForm(await session.GetCommandFormAsync("Test-IsebergForm"));
        var owner = new Window();
        owner.Show();
        var window = new ShowCommandWindow(form, canInsert: true);
        var result = window.ShowDialog<ShowCommandResult?>(owner);
        Assert.False(window.FindControl<Button>("ShowCommandRun")!.IsEnabled);
        var view = window.FindControl<CommandFormView>("ShowCommandForm")!;
        Input(Parameter(view, "Name"), "Name").Text = "Dialog name";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(window.FindControl<Button>("ShowCommandRun")!.IsEnabled);
        window.FindControl<Button>("ShowCommandInsert")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var command = Assert.IsType<ShowCommandResult>(await result);
        Assert.False(command.Run);
        Assert.Equal(form.Build().Script, command.Script);
        Assert.Equal(SessionState.Ready, session.State);
        owner.Close();
    }

    private static void Set(CommandForm form, string name, string text)
    {
        form.Value(name).Text = text;
        form.Value(name).Included = true;
    }

    private static Expander Parameter(CommandFormView view, string name) =>
        view.GetLogicalDescendants().OfType<Expander>().Single(e => (e.Header as string)?.TrimEnd(' ', '*') == name);

    private static TextBox Input(Expander parameter, string name) =>
        parameter.GetLogicalDescendants().OfType<TextBox>().Single(c => AutomationProperties.GetName(c) == name);

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(condition());
    }
}
