using System.Collections.Concurrent;
using System.Management.Automation;
using System.Threading.Channels;
using Iseberg.Core;
using Xunit;
using SessionState = Iseberg.Core.SessionState;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class DebuggerTests
{
    [Fact]
    public async Task PausedEvaluationUsesLocalScopeAndInspectsWatchesAndStack()
    {
        await using var fixture = await DebugScript.CreateAsync("""
            function Test-Local {
                $localValue = 21
                $localValue += 1
                "result=$localValue"
            }
            Test-Local
            """);
        await fixture.Session.AddBreakpointAsync(new(BreakpointKind.Line, fixture.Path, Line: 3));
        var execution = fixture.Run();
        await fixture.Pause();
        var snapshot = await fixture.Session.InspectAsync(["$localValue", "$localValue * 2", "throw 'watch-error'"]);
        Assert.Contains(snapshot.Variables, value => value.Name == "$localValue" && value.Value == "21");
        Assert.Equal("42", snapshot.Watches[1].Value);
        Assert.Contains("watch-error", snapshot.Watches[2].Error);
        Assert.Contains(snapshot.CallStack, frame => frame.FunctionName == "Test-Local" && frame.Line == 3);
        await fixture.Session.EvaluateAsync("$localValue = 40; $localValue");
        Assert.Equal(SessionState.Debugging, fixture.Session.State);
        Assert.Equal("40", (await fixture.Session.InspectAsync(["$localValue"])).Watches[0].Value);
        await fixture.Session.EvaluateAsync("Write-Error 'evaluation-error'");
        Assert.Contains(fixture.Output, entry => entry.Kind == OutputKind.Error && entry.Text.Contains("evaluation-error"));
        fixture.Session.Resume(DebuggerResumeAction.Continue);
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(fixture.Output, entry => entry.Text.Contains("result=41"));
    }

    [Fact]
    public async Task ConditionsEnableDisableAndLiveEditingPreserveOtherBreakpointKinds()
    {
        await using var fixture = await DebugScript.CreateAsync("""
            $n = 0
            $n = 1
            $n = 2
            $n = 3
            "result=$n"
            """);
        var first = await fixture.Session.AddBreakpointAsync(new(BreakpointKind.Line, fixture.Path, Line: 2, Condition: "$n -eq 0"));
        var disabled = await fixture.Session.AddBreakpointAsync(new(BreakpointKind.Line, fixture.Path, Line: 3, Enabled: false));
        var command = await fixture.Session.AddBreakpointAsync(new(BreakpointKind.Command, Target: "Write-Output", Enabled: false));
        var execution = fixture.Run();
        Assert.Equal(2, (await fixture.Pause()).Line);
        await fixture.Session.SetBreakpointEnabledAsync(disabled.Id, true);
        await fixture.Session.UpdateBreakpointAsync(first.Id, first.Spec with { Line = 4, Condition = "$n -eq 2" });
        fixture.Session.Resume(DebuggerResumeAction.Continue);
        Assert.Equal(3, (await fixture.Pause()).Line);
        await fixture.Session.RemoveBreakpointAsync(disabled.Id);
        fixture.Session.Resume(DebuggerResumeAction.Continue);
        Assert.Equal(4, (await fixture.Pause()).Line);
        Assert.Contains(await fixture.Session.GetBreakpointsAsync(), breakpoint => breakpoint.Id == command.Id);
        await fixture.Session.RemoveAllBreakpointsAsync();
        Assert.Empty(await fixture.Session.GetBreakpointsAsync());
        fixture.Session.Resume(DebuggerResumeAction.Continue);
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(BreakpointKind.Command, "Write-Output", VariableAccessMode.Write)]
    [InlineData(BreakpointKind.Variable, "tracked", VariableAccessMode.Write)]
    [InlineData(BreakpointKind.Variable, "tracked", VariableAccessMode.Read)]
    [InlineData(BreakpointKind.Variable, "tracked", VariableAccessMode.ReadWrite)]
    public async Task CommandAndVariableBreakpointsPause(BreakpointKind kind, string target, VariableAccessMode mode)
    {
        await using var fixture = await DebugScript.CreateAsync("$tracked = 42\nWrite-Output $tracked\n");
        await fixture.Session.AddBreakpointAsync(new(kind, fixture.Path, target, AccessMode: mode));
        var execution = fixture.Run();
        await fixture.Pause();
        Assert.Equal(SessionState.Debugging, fixture.Session.State);
        await fixture.Session.RemoveAllBreakpointsAsync();
        fixture.Session.Resume(DebuggerResumeAction.Continue);
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task FalseConditionDoesNotPauseAndLineSynchronizationPreservesIdsAndOtherKinds()
    {
        await using var fixture = await DebugScript.CreateAsync("$n = 1\n$n = 2\n");
        var spec = new BreakpointSpec(BreakpointKind.Line, fixture.Path, Line: 2, Condition: "$n -eq 99");
        var line = await fixture.Session.AddBreakpointAsync(spec);
        var command = await fixture.Session.AddBreakpointAsync(new(BreakpointKind.Command, Target: "Write-Output"));
        await fixture.Session.SetLineBreakpointsAsync(fixture.Path, [spec]);
        Assert.Contains(await fixture.Session.GetBreakpointsAsync(), breakpoint => breakpoint.Id == line.Id);
        await fixture.Run().WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Session.SetBreakpointsAsync(fixture.Path, []);
        Assert.Equal(command.Id, (await fixture.Session.GetBreakpointsAsync()).Single().Id);
    }

    [Fact]
    public async Task BreakAllPausesWithoutAPreexistingBreakpointAndStopReleasesEvaluation()
    {
        await using var fixture = await DebugScript.CreateAsync("""
            Write-Host 'loop-started'
            while ($true) {
                Start-Sleep -Milliseconds 10
            }
            """);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.Output += entry =>
        {
            if (entry.Kind == OutputKind.Output && entry.Text.Contains("loop-started")) started.TrySetResult();
        };
        var execution = fixture.Run();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Session.BreakAll();
        await fixture.Pause();
        var evaluating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.Output += entry =>
        {
            if (entry.Kind == OutputKind.Output && entry.Text.Contains("evaluation-started")) evaluating.TrySetResult();
        };
        var evaluation = fixture.Session.EvaluateAsync("Write-Host 'evaluation-started'; Start-Sleep -Seconds 30");
        await evaluating.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        try { await evaluation.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (PipelineStoppedException) { }
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionState.Ready, fixture.Session.State);
        await fixture.Session.ExecuteAsync("'after-stop'").WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DebuggerConsoleContinueResumesAndInvalidatesInspection()
    {
        await using var fixture = await DebugScript.CreateAsync("$n = 1\n$n += 1\n");
        await fixture.Session.SetBreakpointsAsync(fixture.Path, [2]);
        var execution = fixture.Run();
        await fixture.Pause();
        await fixture.Session.EvaluateAsync("c");
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(fixture.Session.IsDebuggerPaused);
        Assert.Equal(SessionState.Ready, fixture.Session.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.InspectAsync(["$n"]));
    }

    [Fact]
    public async Task InvalidBreakpointEditsLeaveExistingBreakpointsIntact()
    {
        await using var fixture = await DebugScript.CreateAsync("$n = 1\n$n += 1\n");
        var breakpoint = await fixture.Session.AddBreakpointAsync(new(BreakpointKind.Line, fixture.Path, Line: 2));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Session.UpdateBreakpointAsync(breakpoint.Id,
            breakpoint.Spec with { Line = 0 }));
        await Assert.ThrowsAsync<ParseException>(() => fixture.Session.UpdateBreakpointAsync(breakpoint.Id,
            breakpoint.Spec with { Condition = "$n -eq" }));
        Assert.Equal(breakpoint, (await fixture.Session.GetBreakpointsAsync()).Single());
    }

    private sealed class DebugScript : IAsyncDisposable
    {
        private readonly Channel<DebugLocation> pauses = Channel.CreateUnbounded<DebugLocation>();
        private readonly string? previousPolicy = Environment.GetEnvironmentVariable("PSExecutionPolicyPreference");
        public PowerShellSession Session { get; } = new();
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"iseberg-debug-{Guid.NewGuid():N}.ps1");
        public ConcurrentQueue<OutputEntry> Output { get; } = new();

        public static async Task<DebugScript> CreateAsync(string script)
        {
            var fixture = new DebugScript();
            await File.WriteAllTextAsync(fixture.Path, script);
            fixture.Session.Output += fixture.Output.Enqueue;
            fixture.Session.DebuggerStopped += location => { if (location is not null) fixture.pauses.Writer.TryWrite(location); };
            await fixture.Session.InitializeAsync();
            if (OperatingSystem.IsWindows())
                await fixture.Session.ExecuteAsync("Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force");
            return fixture;
        }

        public Task Run() => Session.ExecuteAsync("", Path);
        public async Task<DebugLocation> Pause() => await pauses.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            File.Delete(Path);
            Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", previousPolicy);
        }
    }
}
