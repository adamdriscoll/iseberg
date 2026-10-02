using System.Collections.Concurrent;
using System.Management.Automation;
using Iseberg.Core;
using Xunit;
using SessionState = Iseberg.Core.SessionState;

namespace Iseberg.Tests;

public sealed class PowerShellSessionTests
{
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
