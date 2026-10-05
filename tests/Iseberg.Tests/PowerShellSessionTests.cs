using System.Collections.Concurrent;
using System.Management.Automation;
using Iseberg.Core;
using Xunit;
using SessionState = Iseberg.Core.SessionState;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class PowerShellSessionTests
{
    [Fact]
    public async Task AllUsersProfilesUseTheEngineInstallationDirectory()
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        await session.InitializeAsync();
        await session.ExecuteAsync("$PROFILE.AllUsersCurrentHost; $PROFILE.AllUsersAllHosts");
        var engineHome = Path.GetDirectoryName(typeof(PSObject).Assembly.Location)!;
        Assert.Contains(Path.Combine(engineHome, "Iseberg_profile.ps1"), Text(output));
        Assert.Contains(Path.Combine(engineHome, "profile.ps1"), Text(output));
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
    }

    [Fact]
    public async Task ExecutionBeforeInitializationDoesNotChangeReadiness()
    {
        await using var session = new PowerShellSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync("'not ready'"));
        Assert.Equal(SessionState.Starting, session.State);
        await session.InitializeAsync();
        Assert.Equal(SessionState.Ready, session.State);
    }

    [Fact]
    public async Task StopImmediatelyAfterRunStartsCannotMissPipelineStartup()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        Task? stopping = null;
        session.StateChanged += state => { if (state == SessionState.Running) stopping = session.StopAsync(); };
        await session.ExecuteAsync("Start-Sleep -Seconds 30").WaitAsync(TimeSpan.FromSeconds(10));
        if (stopping is not null) await stopping;
        Assert.Equal(SessionState.Ready, session.State);
    }

    [Fact]
    public async Task StopAlsoCancelsAUserDefinedPromptFunction()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Output += entry =>
        {
            if (entry.Kind == OutputKind.Output && entry.Text.Contains("prompt-started-marker"))
                started.TrySetResult();
        };
        var execution = session.ExecuteAsync("function prompt { Write-Host 'prompt-started-marker'; Start-Sleep -Seconds 30; 'custom> ' }");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync();
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionState.Ready, session.State);
        Assert.Equal("PS> ", session.Prompt);
        await session.ExecuteAsync("function prompt { 'restored> ' }");
        Assert.Equal("restored> ", session.Prompt);
    }

    [Fact]
    public async Task ExecutesPowerShell7AndPreservesState()
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        await session.InitializeAsync();
        await session.ExecuteAsync("$value = 21; function Get-Answer { $value * 2 }; $PSVersionTable.PSVersion.Major");
        await session.ExecuteAsync("Get-Answer");
        Assert.Equal(SessionState.Ready, session.State);
        Assert.Contains("7", Text(output));
        Assert.Contains("42", Text(output));
        Assert.DoesNotContain(output, e => e.Kind == OutputKind.Error);
    }

    [Fact]
    public async Task SessionsAreIndependent()
    {
        await using var first = new PowerShellSession();
        await using var second = new PowerShellSession();
        var output = Capture(second);
        await first.InitializeAsync();
        await second.InitializeAsync();
        await first.ExecuteAsync("$privateValue = 'first'");
        await second.ExecuteAsync("if ($null -eq $privateValue) { 'isolated' }");
        Assert.Contains("isolated", Text(output));
    }

    [Fact]
    public async Task StreamsHostOutputErrorsWarningsAndProgress()
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        var progress = new ConcurrentQueue<ProgressUpdate>();
        session.ProgressChanged += progress.Enqueue;
        await session.InitializeAsync();
        await session.ExecuteAsync("Write-Host 'host-text'; Write-Warning 'warning-text'; Write-Error 'error-text'; Write-Progress -Activity 'Working' -Status 'Testing' -PercentComplete 50");
        Assert.Contains("host-text", Text(output));
        Assert.Contains(output, e => e.Kind == OutputKind.Error && e.Text.Contains("error-text"));
        Assert.Contains(output, e => e.Kind == OutputKind.Warning && e.Text.Contains("warning-text"));
        Assert.Contains(progress, p => p.Percent == 50);
    }

    [Fact]
    public async Task StopCancelsRunningCommandWithoutLosingRunspace()
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        await session.InitializeAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Output += entry => { if (entry.Text.Contains("started-marker") && entry.Kind != OutputKind.Command) started.TrySetResult(); };
        var execution = session.ExecuteAsync("$kept = 123; Write-Host 'started-marker'; Start-Sleep -Seconds 30");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync("'overlap'"));
        await session.StopAsync();
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        await session.ExecuteAsync("$kept");
        Assert.Contains("123", Text(output));
        Assert.Equal(SessionState.Ready, session.State);
    }

    [Fact]
    public async Task ReadHostUsesGraphicalInputHandler()
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        session.InputRequested += request => request.Response.TrySetResult("Ada");
        await session.InitializeAsync();
        await session.ExecuteAsync("$name = Read-Host 'Name'; Write-Output \"Hello $name\"");
        Assert.Contains("Hello Ada", Text(output));
    }

    [Fact]
    public async Task CancellingPromptDoesNotHangPipeline()
    {
        await using var session = new PowerShellSession();
        session.InputRequested += request => request.Response.TrySetCanceled();
        await session.InitializeAsync();
        await session.ExecuteAsync("Read-Host 'Cancel me'").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionState.Ready, session.State);
    }

    [Fact]
    public async Task CompletionUsesSessionCommandsAndVariables()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ExecuteAsync("function Get-IsebergAnswer { 42 }; $IsebergVariable = 10");
        var command = await session.CompleteAsync("Get-IsebergA", 12);
        var variable = await session.CompleteAsync("$IsebergV", 9);
        Assert.Contains(command.Matches, m => m.CompletionText == "Get-IsebergAnswer");
        Assert.Contains(variable.Matches, m => m.CompletionText == "$IsebergVariable");
        Assert.Equal(0, command.Start);
        Assert.Equal(12, command.Length);
    }

    [Fact]
    public async Task CommandExplorerAndHelpUseRealMetadata()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        var commands = await session.GetCommandsAsync();
        Assert.Contains(commands, c => c.Name == "Get-Process");
        var help = await session.GetHelpAsync("Get-Process");
        Assert.Contains("Get-Process", help);
    }

    [Fact]
    public async Task ConsoleShowCommandDoesNotInvokeTheUnavailableNativeDialog()
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        ShowCommandRequest? shown = null;
        session.ShowCommandRequested += request =>
        {
            shown = request;
            request.Response.TrySetResult(null);
        };
        await session.InitializeAsync();
        await session.ExecuteAsync("Show-Command Get-Process").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error &&
            entry.Text.Contains("Exception has been thrown by the target of an invocation."));
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        Assert.NotNull(shown);
        Assert.Equal("Get-Process", shown.Command!.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsoleShowCommandRunsOrReturnsTextInTheOriginatingPipeline(bool passThru)
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        await session.InitializeAsync();
        await session.ExecuteAsync("""
            $showCommandValue = 21
            function Test-ShowCommand {
                [CmdletBinding()] param([int] $Value)
                [pscustomobject]@{ Answer = $Value * 2 }
            }
            Set-Alias Invoke-ShowCommand Test-ShowCommand
            """);
        string? generated = null;
        session.ShowCommandRequested += request =>
        {
            Assert.Equal(passThru, request.PassThru);
            var form = new CommandForm(request.Command!);
            form.Value("Value").Included = true;
            form.Value("Value").Text = "$showCommandValue";
            form.Value("Value").IsExpression = true;
            generated = form.Build().Script;
            request.Response.TrySetResult(generated);
        };
        await session.ExecuteAsync("$result = Show-Command Invoke-ShowCommand" + (passThru ? " -PassThru" : "") +
            "; $result.GetType().Name; $result; if ($result.Answer) { \"answer=$($result.Answer)\" }")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        if (passThru)
        {
            Assert.Contains("String", Text(output));
            Assert.Contains(generated!, Text(output));
            Assert.DoesNotContain("answer=42", Text(output));
        }
        else
        {
            Assert.Contains("PSCustomObject", Text(output));
            Assert.Contains("answer=42", Text(output));
        }
        Assert.Equal(SessionState.Ready, session.State);
    }

    [Fact]
    public async Task ConsoleShowCommandWithoutNameSelectsACommandBeforeOpeningItsForm()
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        await session.InitializeAsync();
        var requests = new List<ShowCommandRequest>();
        var formReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ShowCommandRequested += request =>
        {
            requests.Add(request);
            if (request.Command is null)
            {
                Assert.Contains(request.Commands, command => command.Name == "Get-Process");
                request.Response.TrySetResult("Microsoft.PowerShell.Management\\Get-Process");
            }
            else
            {
                Assert.Equal("Get-Process", request.Command.Name);
                Assert.Equal("Microsoft.PowerShell.Management\\Get-Process", request.Command.InvocationName);
                Assert.All(request.Command.ParameterSets, set => Assert.DoesNotContain(set.Parameters, parameter => parameter.IsCommon));
                Assert.Contains("Get-Process", request.HelpText);
                Assert.Equal(550, request.Width);
                Assert.Equal(650, request.Height);
                request.Response.TrySetResult(null);
                formReady.TrySetResult();
            }
        };
        var execution = session.ExecuteAsync("Show-Command -NoCommonParameter -Width 550 -Height 650");
        await Task.WhenAny(formReady.Task, execution).WaitAsync(TestTimeouts.CommandDiscovery);
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        Assert.Equal(2, requests.Count);
    }

    [Theory]
    [InlineData("Show-Command Get-Process")]
    [InlineData("Show-Command")]
    public async Task StopCancelsAConsoleShowCommandRequestWithoutHanging(string command)
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ExecuteAsync("$retained = 123");
        var shown = new TaskCompletionSource<ShowCommandRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ShowCommandRequested += request => shown.TrySetResult(request);
        var execution = session.ExecuteAsync(command);
        var request = await shown.Task.WaitAsync(TestTimeouts.CommandDiscovery);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(request.Response.Task.IsCanceled);
        Assert.Equal(SessionState.Ready, session.State);
        var output = Capture(session);
        await session.ExecuteAsync("$retained");
        Assert.Contains("123", Text(output));
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
    }

    [Fact]
    public async Task ConsoleShowCommandWithoutDesktopHandlerReportsAnExplicitHostError()
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        await session.InitializeAsync();
        await session.ExecuteAsync("Show-Command Get-Process").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(output, entry => entry.Kind == OutputKind.Error &&
            entry.Text.Contains("No Show-Command handler is attached"));
        Assert.Equal(SessionState.Ready, session.State);
    }

    [Fact]
    public async Task ConsoleShowCommandKeepsPrecedenceAfterRepeatedUtilityImports()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        var showCount = 0;
        session.ShowCommandRequested += request =>
        {
            showCount++;
            Assert.Equal("Get-Process", request.Command!.Name);
            request.Response.TrySetResult(null);
        };
        var output = Capture(session);
        await session.ExecuteAsync("Show-Command Get-Process; Import-Module Microsoft.PowerShell.Utility -Force; Show-Command Get-Process")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, showCount);
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
    }

    [Fact]
    public async Task ConsoleShowCommandMissingNameReportsAnErrorWithoutOpeningAForm()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        var shown = false;
        session.ShowCommandRequested += request => { shown = true; request.Response.TrySetResult(null); };
        var output = Capture(session);
        await session.ExecuteAsync("Show-Command No-IsebergCommand").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(shown);
        Assert.Contains(output, entry => entry.Kind == OutputKind.Error && entry.Text.Contains("No-IsebergCommand"));
        Assert.Equal(SessionState.Ready, session.State);
    }

    [Fact]
    public async Task StopAlsoCancelsTheCommandRunFromTheShowCommandForm()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ExecuteAsync("function Test-ShowCommand { Write-Host 'show-command-running'; Start-Sleep -Seconds 30 }");
        session.ShowCommandRequested += request => request.Response.TrySetResult("& 'Test-ShowCommand'");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Output += entry =>
        {
            if (entry.Kind == OutputKind.Output && entry.Text.Contains("show-command-running"))
                started.TrySetResult();
        };
        var execution = session.ExecuteAsync("Show-Command Test-ShowCommand");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionState.Ready, session.State);
    }

    [Fact]
    public async Task ConsoleShowCommandPreservesTheSelectedCommandsErrorAndWarningStreams()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ExecuteAsync("""
            function Test-ShowCommand {
                Write-Error 'show-command-error'
                Write-Warning 'show-command-warning'
                Write-Output 'show-command-output'
            }
            """);
        session.ShowCommandRequested += request => request.Response.TrySetResult("& 'Test-ShowCommand'");
        var output = Capture(session);
        await session.ExecuteAsync("Show-Command Test-ShowCommand").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(output, entry => entry.Kind == OutputKind.Error && entry.Text.Contains("show-command-error"));
        Assert.Contains(output, entry => entry.Kind == OutputKind.Warning && entry.Text.Contains("show-command-warning"));
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("show-command-output"));
    }

    [Fact]
    public async Task ConsoleShowCommandCancelAndSelectedCommandErrorsLeaveSessionUsable()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ExecuteAsync("function Test-ShowCommand { throw 'selected-command-error' }");
        var showCount = 0;
        session.ShowCommandRequested += request =>
            request.Response.TrySetResult(++showCount == 1 ? null : "& 'Test-ShowCommand'");
        var output = Capture(session);
        await session.ExecuteAsync("Show-Command Test-ShowCommand; 'after-cancel'").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("after-cancel", Text(output));
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        await session.ExecuteAsync("Show-Command Test-ShowCommand").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(output, entry => entry.Kind == OutputKind.Error && entry.Text.Contains("selected-command-error"));
        await session.ExecuteAsync("'after-error'");
        Assert.Contains("after-error", Text(output));
        Assert.Equal(SessionState.Ready, session.State);
    }

    [Fact]
    public async Task SavedScriptHasRealScriptRootAndRetainsVariables()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "script with 'quote.ps1");
        var previousPolicy = Environment.GetEnvironmentVariable("PSExecutionPolicyPreference");
        try
        {
            await File.WriteAllTextAsync(path, "$scriptRoot = $PSScriptRoot; Write-Output $scriptRoot");
            await using var session = new PowerShellSession();
            var output = Capture(session);
            await session.InitializeAsync();
            if (OperatingSystem.IsWindows())
                await session.ExecuteAsync("Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force");
            await session.ExecuteAsync("", path);
            await session.ExecuteAsync("$scriptRoot");
            Assert.DoesNotContain(output, e => e.Kind == OutputKind.Error);
            Assert.Contains(directory, Text(output));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", previousPolicy);
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task DebuggerBreaksStepsAndContinues()
    {
        var path = Path.Combine(Path.GetTempPath(), "iseberg-debug-" + Guid.NewGuid().ToString("N") + ".ps1");
        var previousPolicy = Environment.GetEnvironmentVariable("PSExecutionPolicyPreference");
        await File.WriteAllTextAsync(path, "$debugValue = 1\n$debugValue += 1\nWrite-Output $debugValue\n");
        try
        {
            await using var session = new PowerShellSession();
            var output = Capture(session);
            await session.InitializeAsync();
            if (OperatingSystem.IsWindows())
                await session.ExecuteAsync("Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force");
            await session.SetBreakpointsAsync(path, [2]);
            var locations = new ConcurrentQueue<DebugLocation>();
            var paused = new TaskCompletionSource<DebugLocation>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.DebuggerStopped += location =>
            {
                if (location is null) return;
                locations.Enqueue(location);
                paused.TrySetResult(location);
            };
            var execution = session.ExecuteAsync("", path);
            var first = await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, first.Line);
            Assert.Equal(SessionState.Debugging, session.State);
            paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Resume(DebuggerResumeAction.StepOver);
            var next = await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(3, next.Line);
            session.Resume(DebuggerResumeAction.Continue);
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("2", Text(output));
            Assert.Equal(SessionState.Ready, session.State);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", previousPolicy);
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Clear-Host")]
    [InlineData("clear")]
    [InlineData("cls")]
    public async Task ClearHostClearsGraphicalConsole(string command)
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        var clearCount = 0;
        session.ConsoleCleared += () => clearCount++;
        await session.InitializeAsync();
        await session.ExecuteAsync("$retainedAfterClear = 42");
        await session.ExecuteAsync(command);
        Assert.Equal(1, clearCount);
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        await session.ExecuteAsync("$retainedAfterClear");
        Assert.Contains("42", Text(output));
    }

    [Fact]
    public async Task RuntimeErrorsAreVisibleAndSessionRecovers()
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        await session.InitializeAsync();
        await session.ExecuteAsync("throw 'test-failure'");
        await session.ExecuteAsync("'recovered'");
        Assert.Contains(output, e => e.Kind == OutputKind.Error && e.Text.Contains("test-failure"));
        Assert.Contains("recovered", Text(output));
    }

    private static ConcurrentQueue<OutputEntry> Capture(PowerShellSession session)
    {
        var output = new ConcurrentQueue<OutputEntry>();
        session.Output += output.Enqueue;
        return output;
    }
    private static string Text(IEnumerable<OutputEntry> output) => string.Concat(output.Where(e => e.Kind != OutputKind.Command).Select(e => e.Text));
}
