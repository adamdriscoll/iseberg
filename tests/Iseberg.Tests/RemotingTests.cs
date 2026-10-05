using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using Iseberg.Core;
using Xunit;
using SessionState = Iseberg.Core.SessionState;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class RemotingTests
{
    [Fact]
    public async Task DelayedServerStartupWaitsForTheReadinessMarker()
    {
        await using var server = await RemoteServer.StartAsync(startupDelayMilliseconds: 16000);
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ConnectAsync(server.Connection);
        Assert.True(session.IsRemote);
        await session.ExitRemoteSessionAsync();
        Assert.False(session.IsRemote);
    }

    [Fact]
    public async Task RemoteCommandDiscoveryDoesNotSerializeUnusedMetadata()
    {
        await using var server = await RemoteServer.StartAsync();
        await using var session = new PowerShellSession();
        var output = new ConcurrentQueue<OutputEntry>();
        session.Output += output.Enqueue;
        await session.InitializeAsync();
        await session.ConnectAsync(server.Connection);
        await session.ExecuteAsync("""
            $global:unusedMetadataSerializationCount = 0
            function global:Get-Command {
                Microsoft.PowerShell.Core\Get-Command -Name Get-Process |
                    Add-Member -MemberType ScriptProperty -Name UnusedMetadata -Value {
                        $global:unusedMetadataSerializationCount++
                        'This property is not part of command discovery.'
                    } -PassThru
            }
            """);
        Assert.Equal("Get-Process", (await session.GetCommandsAsync()).Single().Name);
        await session.ExecuteAsync("'unused-metadata=' + $global:unusedMetadataSerializationCount");
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("unused-metadata=0"));
    }

    [Fact]
    public async Task DedicatedConnectionRoutesExecutionMetadataCompletionAndRestoresLocalState()
    {
        await using var server = await RemoteServer.StartAsync();
        await using var session = new PowerShellSession();
        var output = new ConcurrentQueue<OutputEntry>();
        session.Output += output.Enqueue;
        var changes = 0;
        session.RunspaceChanged += () => changes++;
        await session.InitializeAsync();
        var localId = session.RunspaceId;
        await session.ExecuteAsync("$localOnly = 'local-marker'");
        await session.ConnectAsync(server.Connection);
        Assert.True(session.IsRemote);
        Assert.True(session.IsRunspacePushed);
        Assert.NotEqual(localId, session.RunspaceId);
        Assert.StartsWith("[", session.Prompt);
        await session.ExecuteAsync("$remoteOnly = 42; function Get-RemoteAnswer { $remoteOnly }; if ($null -eq $localOnly) { 'isolated-marker' }; Get-RemoteAnswer");
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("isolated-marker"));
        Assert.Contains(await session.GetCommandsAsync(), command => command.Name == "Get-RemoteAnswer");
        var form = await session.GetCommandFormAsync("Get-Process");
        Assert.Contains(form.ParameterSets.SelectMany(set => set.Parameters), parameter => parameter.Name == "Name");
        await session.ExecuteAsync("""
            function Test-RemoteForm {
                [CmdletBinding()]
                param([Parameter(Mandatory)][ValidateSet('red','blue')][string]$Color,
                    [switch]$Force, [AllowEmptyString()][string]$Optional)
            }
            Set-Alias Invoke-RemoteForm Test-RemoteForm
            """);
        var custom = await session.GetCommandFormAsync("Invoke-RemoteForm");
        Assert.Equal("Invoke-RemoteForm", custom.InvocationName);
        var parameters = custom.ParameterSets.Single().Parameters;
        Assert.Contains(parameters, parameter => parameter.Name == "Color" && parameter.IsMandatory &&
            parameter.Kind == CommandParameterKind.Choice && parameter.Choices.SequenceEqual(["red", "blue"]));
        Assert.Contains(parameters, parameter => parameter.Name == "Force" && parameter.Kind == CommandParameterKind.Switch);
        Assert.Contains(parameters, parameter => parameter.Name == "Optional" && parameter.AllowsEmptyString);
        Assert.Contains(parameters, parameter => parameter.Name == "Verbose" && parameter.IsCommon);
        var completion = await session.CompleteAsync("$remoteO", 8);
        Assert.Contains(completion.Matches, match => match.CompletionText == "$remoteOnly");
        session.InputRequested += request => request.Response.TrySetResult("remote-input-marker");
        await session.ExecuteAsync("Read-Host 'Remote input'");
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("remote-input-marker"));
        await session.ExecuteAsync("Exit-PSSession");
        Assert.False(session.IsRemote);
        Assert.Equal(localId, session.RunspaceId);
        await session.ExecuteAsync("$localOnly; if ($null -eq $remoteOnly) { 'restored-marker' }");
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("local-marker"));
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("restored-marker"));
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task InteractiveHostPushPopPreservesTheLocalRunspace()
    {
        await using var server = await RemoteServer.StartAsync();
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        var original = session.RunspaceId;
        await session.ExecuteAsync($$"""
            $testRunspace = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace(
                [System.Management.Automation.Runspaces.NamedPipeConnectionInfo]::new({{server.ProcessId}}),
                $Host, [System.Management.Automation.Runspaces.TypeTable]::LoadDefaultTypeFiles())
            $testRunspace.Open()
            $constructor = [System.Management.Automation.Runspaces.PSSession].GetConstructors(
                [System.Reflection.BindingFlags]'Instance,NonPublic')[0]
            $testSession = $constructor.Invoke(@($testRunspace))
            Enter-PSSession -Session $testSession
            """);
        Assert.True(session.IsRunspacePushed);
        Assert.True(session.IsRemote);
        await session.ExecuteAsync("$processMarker = 123");
        await session.ExecuteAsync("Exit-PSSession");
        Assert.False(session.IsRunspacePushed);
        Assert.Equal(original, session.RunspaceId);
        var output = new ConcurrentQueue<OutputEntry>();
        session.Output += output.Enqueue;
        await session.ExecuteAsync("$testRunspace.RunspaceStateInfo.State");
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("Opened"));
        await session.ExecuteAsync("""
            $testRunspace.GetType().GetProperty('ShouldCloseOnPop', [System.Reflection.BindingFlags]'Instance,NonPublic').SetValue($testRunspace, $true)
            Enter-PSSession -Session $testSession
            """);
        Assert.True(session.IsRemote);
        await session.ExecuteAsync("Exit-PSSession");
        await session.ExecuteAsync("$testRunspace.RunspaceStateInfo.State");
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("Closed"));
    }

    [Fact]
    public async Task RemoteFilesPreserveBytesAndAreBoundToTheirConnection()
    {
        await using var server = await RemoteServer.StartAsync();
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ConnectAsync(server.Connection);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "'remote.ps1");
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("'before'\r\n")).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var file = await session.OpenRemoteFileAsync(path);
            Assert.True(file.IsRemote);
            Assert.False(file.IsDirty);
            Assert.Equal("UTF-16 LE", file.EncodingName);
            Assert.Equal("'before'\r\n", file.Text);
            Assert.True(await session.RemoteFileExistsAsync(path));
            Assert.False(await session.RemoteFileExistsAsync(path + ".missing"));
            file.Text = "'after'\r\n";
            await Assert.ThrowsAsync<InvalidOperationException>(() => file.SaveAsync(path));
            await session.SaveRemoteFileAsync(file);
            Assert.False(file.IsDirty);
            Assert.Equal(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(file.Text)), await File.ReadAllBytesAsync(path));
            await session.ExitRemoteSessionAsync();
            file.Text = "'unsaved'";
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveRemoteFileAsync(file));
            Assert.True(file.IsDirty);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenRemoteFileAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FailedRemoteSaveKeepsDirtyTextAndNewFilesReceiveRemoteIdentity()
    {
        await using var server = await RemoteServer.StartAsync();
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ConnectAsync(server.Connection);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-new-remote.ps1");
        var file = new ScriptFile("Untitled.ps1") { Text = "'new-remote-marker'\n" };
        try
        {
            await Assert.ThrowsAsync<RemoteException>(() => session.SaveRemoteFileAsync(file, Path.Combine(path, "missing", "script.ps1")));
            Assert.True(file.IsDirty);
            Assert.False(file.IsRemote);
            await session.SaveRemoteFileAsync(file, path);
            Assert.True(file.IsRemote);
            Assert.Equal(session.RunspaceId, file.RemoteRunspaceId);
            Assert.False(file.IsDirty);
            file.Text = "'changed'";
            await session.ExitRemoteSessionAsync();
            await session.ConnectAsync(server.Connection);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveRemoteFileAsync(file));
            Assert.True(file.IsDirty);
            Assert.Equal("'new-remote-marker'\n", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RemoteStopAndTransportLossLeaveTheLocalSessionUsable()
    {
        await using var server = await RemoteServer.StartAsync();
        await using var session = new PowerShellSession();
        var output = new ConcurrentQueue<OutputEntry>();
        session.Output += output.Enqueue;
        await session.InitializeAsync();
        var original = session.RunspaceId;
        await session.ConnectAsync(server.Connection);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Output += entry =>
        {
            if (entry.Kind == OutputKind.Output && entry.Text.Contains("remote-sleep-marker")) started.TrySetResult();
        };
        var execution = session.ExecuteAsync("Write-Output 'remote-sleep-marker'; Start-Sleep -Seconds 30");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync();
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(session.IsRemote);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += state =>
        {
            if (state == SessionState.Ready && !session.IsRemote) restored.TrySetResult();
        };
        server.Kill();
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(original, session.RunspaceId);
        Assert.Contains(output, entry => entry.Kind == OutputKind.Error && entry.Text.Contains("Remote connection ended"));
        await session.ExecuteAsync("'still-usable'");
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("still-usable"));
    }

    [Fact]
    public async Task RemoteDebuggerPausesInspectsEvaluatesReadsSourceAndResumes()
    {
        await using var server = await RemoteServer.StartAsync();
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ConnectAsync(server.Connection);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-debug.ps1");
        await File.WriteAllTextAsync(path, "$remoteValue = 21\n$remoteValue = $remoteValue * 2\n$remoteValue\n");
        var paused = new TaskCompletionSource<DebugLocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DebuggerStopped += location => { if (location is not null) paused.TrySetResult(location); };
        try
        {
            var breakpoint = await session.AddBreakpointAsync(new(BreakpointKind.Line, path, Line: 2,
                Condition: "$remoteValue -eq 21", Enabled: false));
            Assert.False(breakpoint.Spec.Enabled);
            await session.SetBreakpointEnabledAsync(breakpoint.Id, true);
            Assert.Equal("$remoteValue -eq 21", (await session.GetBreakpointsAsync()).Single().Spec.Condition);
            var execution = session.ExecuteAsync("", path);
            var location = await paused.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(2, location.Line);
            var snapshot = await session.InspectAsync(["$remoteValue", "[pscustomobject]@{ Answer = 42 }"]);
            Assert.Contains(snapshot.Variables, value => value.Name == "$remoteValue" && value.Value == "21");
            Assert.Equal("21", snapshot.Watches[0].Value);
            Assert.NotNull(snapshot.Watches[1].Reference);
            Assert.Contains((await session.GetValueChildrenAsync(snapshot.Watches[1].Reference!.Value)).Values,
                value => value.Name == "Answer" && value.Value == "42");
            Assert.NotEmpty(snapshot.CallStack);
            var source = await session.OpenRemoteFileAsync(path);
            Assert.Contains("$remoteValue", source.Text);
            var completed = await session.CompleteAsync("$remoteV", 8);
            Assert.Contains(completed.Matches, match => match.CompletionText == "$remoteValue");
            await session.EvaluateAsync("$remoteValue = 40");
            Assert.Equal("40", (await session.InspectAsync(["$remoteValue"])).Watches.Single().Value);
            var stepped = new TaskCompletionSource<DebugLocation>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.DebuggerStopped += next => { if (next is not null) stepped.TrySetResult(next); };
            session.Resume(DebuggerResumeAction.StepOver);
            Assert.Equal(3, (await stepped.Task.WaitAsync(TimeSpan.FromSeconds(20))).Line);
            Assert.Equal("80", (await session.InspectAsync(["$remoteValue"])).Watches.Single().Value);
            session.Resume(DebuggerResumeAction.Continue);
            await execution.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(SessionState.Ready, session.State);
        }
        finally { await session.StopAsync(); File.Delete(path); }
    }

    internal sealed class RemoteServer : IAsyncDisposable
    {
        private readonly Process process;
        public int ProcessId => process.Id;
        public NamedPipeConnectionInfo Connection => new(ProcessId) { OpenTimeout = 10000 };
        private RemoteServer(Process process) => this.process = process;
        public void Kill()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }

        public static async Task<RemoteServer> StartAsync(int startupDelayMilliseconds = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(startupDelayMilliseconds);
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                $"Start-Sleep -Milliseconds {startupDelayMilliseconds}; Write-Output 'remote-server-ready'; Start-Sleep -Seconds 180" })
                start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the remoting test server.");
            try
            {
                Assert.Equal("remote-server-ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TestTimeouts.PowerShellStartup));
                return new(process);
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Kill();
            await process.WaitForExitAsync();
            process.Dispose();
        }
    }
}
