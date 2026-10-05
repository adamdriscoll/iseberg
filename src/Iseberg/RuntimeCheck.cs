using Iseberg.Core;

namespace Iseberg;

internal static class RuntimeCheck
{
    public static async Task RunAsync()
    {
        await using var session = new PowerShellSession();
        var output = new List<OutputEntry>();
        session.Output += output.Add;
        await session.InitializeAsync();
        await session.ExecuteAsync("""
            $ErrorActionPreference = 'Stop'
            Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility
            $null = Get-Process -Id $PID
            $value = [pscustomobject]@{ Answer = 42 } | ConvertTo-Json -Compress | ConvertFrom-Json
            if ($value.Answer -ne 42) { throw 'JSON cmdlets failed' }
            Add-Type -TypeDefinition 'public static class IsebergRuntimeProbe { public static int Answer => 42; }'
            if ([IsebergRuntimeProbe]::Answer -ne 42) { throw 'Dynamic compilation failed' }
            'iseberg-runtime-ok'
            """);
        if (output.Any(entry => entry.Kind == OutputKind.Error) ||
            !output.Any(entry => entry.Text.Contains("iseberg-runtime-ok", StringComparison.Ordinal)))
            throw new InvalidOperationException("PowerShell runtime check failed: " + string.Concat(output.Select(entry => entry.Text)));
        var commands = await session.GetCommandsAsync();
        if (!commands.Any(command => command.Name == "Get-Process"))
            throw new InvalidOperationException("PowerShell command discovery failed.");
        var completion = await session.CompleteAsync("Get-Pro", 7);
        if (completion.Matches.Count == 0)
            throw new InvalidOperationException("PowerShell completion failed.");
        if (EditorAnalysis.CommandNameAtCaret("Get-Process", 5) != "Get-Process")
            throw new InvalidOperationException("PowerShell parser failed.");
        _ = await session.GetCommandFormAsync("Get-Process");
        _ = await session.GetHelpDocumentAsync("Get-Process");
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DebuggerStopped += location => { if (location is not null) paused.TrySetResult(); };
        await session.AddBreakpointAsync(new(BreakpointKind.Command, Target: "Write-Output"));
        var execution = session.ExecuteAsync("Write-Output 42");
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var inspection = await session.InspectAsync(["6 * 7"]).WaitAsync(TimeSpan.FromSeconds(10));
            if (inspection.Watches.Single().Value != "42")
                throw new InvalidOperationException("PowerShell debugger inspection failed.");
            session.Resume(System.Management.Automation.DebuggerResumeAction.Continue);
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await session.StopAsync();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
        _ = new UserSettings().Copy();
        Console.WriteLine($"Iseberg runtime check passed: PowerShell {session.Version}, .NET {Environment.Version}.");
    }
}
