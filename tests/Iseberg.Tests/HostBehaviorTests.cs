using System.Collections.Concurrent;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class HostBehaviorTests
{
    private const string ChoiceScript = """
        $choices = [System.Collections.ObjectModel.Collection[System.Management.Automation.Host.ChoiceDescription]]::new()
        $choices.Add([System.Management.Automation.Host.ChoiceDescription]::new('&First', 'first help'))
        $choices.Add([System.Management.Automation.Host.ChoiceDescription]::new('&Second', 'second help'))
        $choices.Add([System.Management.Automation.Host.ChoiceDescription]::new('&Third', 'third help'))
        """;

    [Theory]
    [InlineData("0,2,0", "0,2")]
    [InlineData("", "1")]
    [InlineData("-", "")]
    [InlineData("First,Third", "0,2")]
    public async Task MultipleChoicePreservesDefaultsAndReturnsDistinctSelections(string answer, string expected)
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        session.InputRequested += request =>
        {
            Assert.True(request.MultipleChoice);
            Assert.Equal([1], request.DefaultChoices);
            Assert.Equal("first help", request.Choices[0].Help);
            request.Response.TrySetResult(answer);
        };
        await session.InitializeAsync();
        await session.ExecuteAsync(ChoiceScript + """

            $selected = $Host.UI.PromptForChoice('Pick', 'Choose several', $choices, [int[]]@(1))
            "selected=$($selected -join ',')!"
            """).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("selected=" + expected + "!"));
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
    }

    [Fact]
    public async Task InvalidMultipleChoiceRepromptsAndSingleChoiceRetainsAccelerators()
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        var answers = new Queue<string>(["99", "0,2", "S"]);
        session.InputRequested += request => request.Response.TrySetResult(answers.Dequeue());
        await session.InitializeAsync();
        await session.ExecuteAsync(ChoiceScript + """

            "multi=$($Host.UI.PromptForChoice('Pick', '', $choices, [int[]]@()) -join ',')"
            "single=$($Host.UI.PromptForChoice('Pick', '', $choices, 0))"
            """);
        Assert.Empty(answers);
        Assert.Contains(output, entry => entry.Kind == OutputKind.Warning);
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("multi=0,2"));
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("single=1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopCancelsOutstandingInputAndRetainsRunspace(bool multiple)
    {
        await using var session = new PowerShellSession();
        var requested = new TaskCompletionSource<InputRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.InputRequested += request => requested.TrySetResult(request);
        await session.InitializeAsync();
        var execution = session.ExecuteAsync("$retained = 42; " + (multiple
            ? ChoiceScript + "; $Host.UI.PromptForChoice('Pick', '', $choices, [int[]]@(1))"
            : "Read-Host -AsSecureString 'secret'"));
        var request = await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync();
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(request.Response.Task.IsCanceled);
        var output = Capture(session);
        await session.ExecuteAsync("$retained");
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("42"));
    }

    [Fact]
    public async Task NestedPromptsEvaluateCompleteAndExitInSuspendedScope()
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        await session.InitializeAsync();
        var entered = NextNested(session);
        var execution = session.ExecuteAsync("""
            function Test-NestedScope {
                $retained = 21
                $Host.EnterNestedPrompt()
                "after=$retained"
            }
            Test-NestedScope
            """);
        await entered.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("Nested 1", session.NestedPrompt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync("'overlap'"));
        await session.EvaluateNestedAsync("$retained = 42; \"level=$NestedPromptLevel\"; [pscustomobject]@{ Answer = $retained }");
        var completion = await session.CompleteAsync("$retain", 7);
        Assert.Contains(completion.Matches, match => match.CompletionText == "$retained");
        entered = NextNested(session, 2);
        var inner = session.EvaluateNestedAsync("$Host.EnterNestedPrompt(); 'inner-returned'");
        await entered.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("Nested 2", session.NestedPrompt);
        await session.EvaluateNestedAsync("exit").WaitAsync(TimeSpan.FromSeconds(10));
        await inner.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(session.IsNestedPromptActive);
        Assert.Contains("Nested 1", session.NestedPrompt);
        await session.EvaluateNestedAsync("exit").WaitAsync(TimeSpan.FromSeconds(10));
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionState.Ready, session.State);
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("after=42"));
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("level=1"));
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopUnwindsNestedPromptIncludingRunningNestedCommand(bool running)
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        var entered = NextNested(session);
        var execution = session.ExecuteAsync("$retained = 123; $Host.EnterNestedPrompt(); 'must-not-resume'");
        await entered.WaitAsync(TimeSpan.FromSeconds(10));
        Task? nested = null;
        if (running)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Output += entry =>
            {
                if (entry.Kind == OutputKind.Output && entry.Text.Contains("nested-started")) started.TrySetResult();
            };
            nested = session.EvaluateNestedAsync("Write-Host 'nested-started'; Start-Sleep -Seconds 30");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        if (nested is not null)
            try { await nested.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (System.Management.Automation.PipelineStoppedException) { }
        Assert.Equal(SessionState.Ready, session.State);
        var output = Capture(session);
        await session.ExecuteAsync("$retained");
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("123"));
    }

    [Fact]
    public async Task HostWritesPreserveExplicitAndRawColors()
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        await session.InitializeAsync();
        await session.ExecuteAsync("""
            Write-Host 'red-on-white' -ForegroundColor Red -BackgroundColor White
            $Host.UI.RawUI.ForegroundColor = 'Green'
            $Host.UI.Write('raw-green')
            Write-Warning 'warning'
            """);
        Assert.Contains(output, entry => entry.Text.Contains("red-on-white") && entry.Style?.Foreground == "#FF0000" && entry.Style.Background == "#FFFFFF");
        Assert.Contains(output, entry => entry.Text == "raw-green" && entry.Style?.Foreground == "#00FF00");
        Assert.Contains(output, entry => entry.Kind == OutputKind.Warning && entry.Style is null);
    }

    [Theory]
    [InlineData("$Host.UI.RawUI.CursorPosition = [System.Management.Automation.Host.Coordinates]::new(1,1)")]
    [InlineData("$Host.UI.RawUI.WindowSize = [System.Management.Automation.Host.Size]::new(80,25)")]
    [InlineData("$Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')")]
    [InlineData("$Host.UI.RawUI.FlushInputBuffer()")]
    [InlineData("$Host.UI.RawUI.GetBufferContents([System.Management.Automation.Host.Rectangle]::new(0,0,1,1))")]
    public async Task UnsupportedRawOperationsReportErrors(string script)
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        await session.InitializeAsync();
        await session.ExecuteAsync(script);
        Assert.Contains(output, entry => entry.Kind == OutputKind.Error);
        Assert.Equal(SessionState.Ready, session.State);
    }

    [Theory]
    [InlineData("Write-Error 'popup-nonterminating'; Write-Warning 'popup-warning'; Write-Host 'popup-host'; [pscustomobject]@{ Answer = $popupValue }", true)]
    [InlineData("throw 'popup-terminating'", false)]
    public async Task ShowCommandErrorPopupSeparatesErrorsFromStructuredOutput(string script, bool hasOutput)
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        string? error = null;
        session.ShowCommandRequested += request => request.Response.TrySetResult(script);
        session.CommandErrorRequested += request => { error = request.Message; request.Response.TrySetResult(); };
        await session.InitializeAsync();
        await session.ExecuteAsync("$popupValue = 42; $result = Show-Command Get-Process -ErrorPopup; \"answer=$($result.Answer)\"");
        Assert.Contains(hasOutput ? "popup-nonterminating" : "popup-terminating", error);
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        if (hasOutput)
        {
            Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("answer=42"));
            Assert.Single(output, entry => entry.Kind == OutputKind.Warning && entry.Text.Contains("popup-warning"));
            Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("popup-host"));
        }
        Assert.Equal(SessionState.Ready, session.State);
    }

    [Fact]
    public async Task StopCancelsCommandErrorPopup()
    {
        await using var session = new PowerShellSession();
        session.ShowCommandRequested += request => request.Response.TrySetResult("Write-Error 'popup'");
        var requested = new TaskCompletionSource<CommandErrorRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.CommandErrorRequested += request => requested.TrySetResult(request);
        await session.InitializeAsync();
        var execution = session.ExecuteAsync("Show-Command Get-Process -ErrorPopup");
        var request = await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync();
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(request.Response.Task.IsCanceled);
        Assert.Equal(SessionState.Ready, session.State);
    }

    private static Task NextNested(PowerShellSession session, int depth = 1)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(SessionState state)
        {
            if (state != SessionState.NestedPrompt || !session.IsNestedPromptActive || !session.NestedPrompt.Contains("Nested " + depth)) return;
            session.StateChanged -= Changed;
            result.TrySetResult();
        }
        session.StateChanged += Changed;
        return result.Task;
    }

    [Fact]
    public async Task DebuggerResumeRestoresTheNestedHostPrompt()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ExecuteAsync("function Test-NestedBreakpoint { 'breakpoint-output' }");
        await session.AddBreakpointAsync(new(BreakpointKind.Command, Target: "Test-NestedBreakpoint"));
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DebuggerStopped += location => { if (location is not null) paused.TrySetResult(); };
        var entered = NextNested(session);
        var execution = session.ExecuteAsync("$Host.EnterNestedPrompt()");
        await entered.WaitAsync(TimeSpan.FromSeconds(10));
        var nested = session.EvaluateNestedAsync("Test-NestedBreakpoint");
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionState.Debugging, session.State);
        session.Resume(System.Management.Automation.DebuggerResumeAction.Continue);
        await nested.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(session.IsNestedPromptActive);
        await session.EvaluateNestedAsync("exit");
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionState.Ready, session.State);
    }

    [Fact]
    public async Task PopupWithoutHandlerRetainsTheOriginalExecutionError()
    {
        await using var session = new PowerShellSession();
        var output = Capture(session);
        session.ShowCommandRequested += request => request.Response.TrySetResult("throw 'original-popup-error'");
        await session.InitializeAsync();
        await session.ExecuteAsync("Show-Command Get-Process -ErrorPopup");
        Assert.Contains(output, entry => entry.Kind == OutputKind.Error &&
            entry.Text.Contains("No command error popup handler") && entry.Text.Contains("original-popup-error"));
    }

    private static ConcurrentQueue<OutputEntry> Capture(PowerShellSession session)
    {
        var output = new ConcurrentQueue<OutputEntry>();
        session.Output += output.Enqueue;
        return output;
    }
}
