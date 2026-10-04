using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace Iseberg.Core;

public sealed class PowerShellSession : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly ManualResetEventSlim debuggerResume = new();
    private readonly Runspace runspace;
    private PowerShell? active;
    private InputRequest? pendingInput;
    private ShowCommandRequest? pendingShowCommand;
    private DebuggerResumeAction resumeAction;
    private bool disposed;
    private bool stopRequested;

    public event Action<OutputEntry>? Output;
    public event Action<SessionState>? StateChanged;
    public event Action<InputRequest>? InputRequested;
    public event Action<ShowCommandRequest>? ShowCommandRequested;
    public event Action<ProgressUpdate>? ProgressChanged;
    public event Action<DebugLocation?>? DebuggerStopped;
    public event Action? ConsoleCleared;
    public SessionState State { get; private set; } = SessionState.Starting;
    public string Prompt { get; private set; } = "PS> ";
    public string Version => PSVersionInfo.PSVersion.ToString();

    public PowerShellSession()
    {
        var host = new WorkbenchHost(entry => Output?.Invoke(entry), ReadInput,
            update => ProgressChanged?.Invoke(update), () => ConsoleCleared?.Invoke(), ReadShowCommand);
        var initialState = InitialSessionState.CreateDefault2();
        // The default Unix function clears a terminal instead of this graphical host.
        initialState.Commands.Remove("Clear-Host", typeof(SessionStateFunctionEntry));
        initialState.Commands.Add(new SessionStateFunctionEntry("Clear-Host", """
            $rawUI = $Host.UI.RawUI
            $rawUI.SetBufferContents(
                [System.Management.Automation.Host.Rectangle]::new(-1, -1, -1, -1),
                [System.Management.Automation.Host.BufferCell]::new(
                    ' ',
                    $rawUI.ForegroundColor,
                    $rawUI.BackgroundColor,
                    [System.Management.Automation.Host.BufferCellType]::Complete))
            $rawUI.CursorPosition = [System.Management.Automation.Host.Coordinates]::new(0, 0)
            """));
        initialState.Commands.Remove("clear", typeof(SessionStateAliasEntry));
        initialState.Commands.Add(new SessionStateAliasEntry("clear", "Clear-Host"));
        // These binary cmdlets must be available even when Restricted prevents loading module type data.
        initialState.Commands.Add(new SessionStateCmdletEntry("Set-ExecutionPolicy", typeof(Microsoft.PowerShell.Commands.SetExecutionPolicyCommand), null));
        initialState.Commands.Add(new SessionStateCmdletEntry("Get-ExecutionPolicy", typeof(Microsoft.PowerShell.Commands.GetExecutionPolicyCommand), null));
        initialState.Commands.Add(new SessionStateCmdletEntry("Show-IsebergCommand", typeof(ShowCommandCommand), null));
        // A function keeps precedence when Utility is auto-imported by commands such as Get-Help.
        initialState.Commands.Remove("Show-Command", typeof(SessionStateFunctionEntry));
        initialState.Commands.Add(new SessionStateFunctionEntry("Show-Command", """
            [CmdletBinding()]
            param(
                [Parameter(Position=0)] [ValidateNotNullOrEmpty()] [string] $Name,
                [switch] $PassThru,
                [switch] $NoCommonParameter,
                [ValidateRange(300, [int]::MaxValue)] [int] $Width = 620,
                [ValidateRange(300, [int]::MaxValue)] [int] $Height = 700
            )
            Show-IsebergCommand @PSBoundParameters
            """));
        runspace = RunspaceFactory.CreateRunspace(host, initialState);
        runspace.ThreadOptions = PSThreadOptions.ReuseThread;
        if (OperatingSystem.IsWindows())
            runspace.ApartmentState = ApartmentState.STA;
    }

    public async Task InitializeAsync()
    {
        await gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (State != SessionState.Starting || runspace.RunspaceStateInfo.State != RunspaceState.BeforeOpen)
                throw new InvalidOperationException("This PowerShell tab has already been initialized.");
            await Task.Run(() =>
            {
                runspace.Open();
                runspace.Debugger.SetDebugMode(DebugModes.LocalScript);
                runspace.Debugger.DebuggerStop += OnDebuggerStop;
                var config = OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PowerShell")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "powershell");
                var profile = PSObject.AsPSObject(Path.Combine(config, "Iseberg_profile.ps1"));
                profile.Properties.Add(new PSNoteProperty("CurrentUserCurrentHost", profile.BaseObject));
                profile.Properties.Add(new PSNoteProperty("CurrentUserAllHosts", Path.Combine(config, "profile.ps1")));
                profile.Properties.Add(new PSNoteProperty("AllUsersCurrentHost", Path.Combine(AppContext.BaseDirectory, "Iseberg_profile.ps1")));
                profile.Properties.Add(new PSNoteProperty("AllUsersAllHosts", Path.Combine(AppContext.BaseDirectory, "profile.ps1")));
                runspace.SessionStateProxy.SetVariable("PROFILE", profile);
                RefreshPrompt();
            });
            SetState(SessionState.Ready);
        }
        finally { gate.Release(); }
    }

    public async Task ExecuteAsync(string script, string? filePath = null)
    {
        if (!await gate.WaitAsync(0))
            throw new InvalidOperationException("This PowerShell tab is busy.");
        var started = false;
        try
        {
            EnsureReady();
            lock (sync) stopRequested = false;
            started = true;
            SetState(SessionState.Running);
            Output?.Invoke(new(Prompt + (filePath ?? script) + Environment.NewLine, OutputKind.Command, Prompt.Length));
            await Task.Run(() =>
            {
                using var shell = CreateShell();
                try
                {
                    if (filePath is null)
                        shell.AddScript(script, useLocalScope: false);
                    else
                        shell.AddScript(". '" + filePath.Replace("'", "''") + "'", useLocalScope: false);
                    shell.AddCommand("Out-Default");
                    using var output = new PSDataCollection<PSObject>();
                    IAsyncResult invocation;
                    lock (sync)
                    {
                        if (stopRequested) throw new PipelineStoppedException();
                        invocation = shell.BeginInvoke<PSObject, PSObject>(null, output);
                        active = shell;
                    }
                    shell.EndInvoke(invocation);
                }
                catch (PipelineStoppedException)
                {
                    Output?.Invoke(new("Execution stopped." + Environment.NewLine, OutputKind.Warning));
                }
                catch (RuntimeException exception)
                {
                    Output?.Invoke(new(exception.ErrorRecord.ToString() + Environment.NewLine, OutputKind.Error));
                }
                finally
                {
                    lock (sync) active = null;
                    DebuggerStopped?.Invoke(null);
                    RefreshPrompt();
                }
            });
        }
        finally
        {
            if (started && !disposed) SetState(SessionState.Ready);
            gate.Release();
        }
    }

    public Task StopAsync()
    {
        lock (sync)
        {
            stopRequested = true;
            pendingInput?.Response.TrySetCanceled();
            pendingShowCommand?.Response.TrySetCanceled();
            resumeAction = DebuggerResumeAction.Stop;
            debuggerResume.Set();
            if (active is { } shell)
                return shell.StopAsync(null, null);
        }
        return Task.CompletedTask;
    }

    public void Resume(DebuggerResumeAction action)
    {
        lock (sync)
        {
            if (State != SessionState.Debugging)
                throw new InvalidOperationException("The debugger is not paused.");
            resumeAction = action;
            debuggerResume.Set();
        }
    }

    public Task SetBreakpointsAsync(string path, IEnumerable<int> lines) =>
        QueryAsync(shell =>
        {
            shell.AddCommand("Get-PSBreakpoint").AddParameter("Script", path)
                .AddCommand("Remove-PSBreakpoint");
            shell.Invoke();
            shell.Commands.Clear();
            var lineArray = lines.ToArray();
            if (lineArray.Length != 0)
                shell.AddCommand("Set-PSBreakpoint").AddParameter("Script", path).AddParameter("Line", lineArray).Invoke();
            ThrowQueryErrors(shell);
            return true;
        });

    public Task<CompletionSet> CompleteAsync(string text, int cursor, CancellationToken cancellationToken = default) =>
        QueryAsync(shell =>
        {
            var result = CommandCompletion.CompleteInput(text, cursor, null, shell);
            return new CompletionSet(result.ReplacementIndex, result.ReplacementLength, result.CompletionMatches.ToArray());
        }, cancellationToken);

    public Task<IReadOnlyList<CommandDescription>> GetCommandsAsync() =>
        QueryAsync<IReadOnlyList<CommandDescription>>(shell =>
        {
            var commands = shell.AddCommand("Get-Command").Invoke<CommandInfo>();
            ThrowQueryErrors(shell);
            return DescribeCommands(commands);
        });

    internal static IReadOnlyList<CommandDescription> DescribeCommands(IEnumerable<CommandInfo> commands) =>
        commands.OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
            .Select(command => new CommandDescription(command.Name, command.ModuleName,
                command.CommandType.ToString(), command.Definition))
            .DistinctBy(command => command.Name).ToArray();

    public Task<string> GetHelpAsync(string name) => QueryAsync(shell =>
    {
        var output = shell.AddCommand("Get-Help").AddParameter("Name", name).AddParameter("Full")
            .AddCommand("Out-String").AddParameter("Width", 100).Invoke<string>();
        ThrowQueryErrors(shell);
        return string.Join(Environment.NewLine, output);
    });

    public Task<CommandFormDescription> GetCommandFormAsync(string name, string? module = null,
        CancellationToken cancellationToken = default) => QueryAsync(shell =>
    {
        shell.AddCommand("Get-Command").AddParameter("Name", WildcardPattern.Escape(name));
        if (!string.IsNullOrEmpty(module)) shell.AddParameter("Module", module);
        var commands = shell.Invoke<CommandInfo>();
        ThrowQueryErrors(shell);
        var command = commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Command '{name}' was not found in this PowerShell tab.");
        return CommandForm.Describe(command);
    }, cancellationToken, waitForGate: true);

    public Task<Uri?> GetHelpUriAsync(string name) => QueryAsync(shell =>
    {
        var help = shell.AddCommand("Get-Help").AddParameter("Name", name).Invoke();
        ThrowQueryErrors(shell);
        return FindHelpUri(help);
    });

    internal static Uri? FindHelpUri(IEnumerable<PSObject> help)
    {
        foreach (var entry in help)
        {
            if (entry.Properties["RelatedLinks"]?.Value is not PSObject links) continue;
            if (links.Properties["navigationLink"]?.Value is not System.Collections.IEnumerable navigation) continue;
            foreach (var link in navigation)
            {
                var value = PSObject.AsPSObject(link).Properties["uri"]?.Value?.ToString();
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") return uri;
            }
        }
        return null;
    }

    private async Task<T> QueryAsync<T>(Func<PowerShell, T> query, CancellationToken cancellationToken = default,
        bool waitForGate = false)
    {
        if (waitForGate) await gate.WaitAsync(cancellationToken);
        else if (!await gate.WaitAsync(0))
            throw new InvalidOperationException("Wait for the running command to finish.");
        try
        {
            EnsureReady();
            return await Task.Run(() =>
            {
                using var shell = CreateShell();
                using var registration = cancellationToken.Register(() => shell.Stop());
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = query(shell);
                    cancellationToken.ThrowIfCancellationRequested();
                    return result;
                }
                catch (PipelineStoppedException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            });
        }
        finally { gate.Release(); }
    }

    private PowerShell CreateShell()
    {
        var shell = PowerShell.Create();
        shell.Runspace = runspace;
        shell.Streams.Error.DataAdded += (_, e) => Output?.Invoke(new(shell.Streams.Error[e.Index].ToString() + Environment.NewLine, OutputKind.Error));
        return shell;
    }

    private static void ThrowQueryErrors(PowerShell shell)
    {
        if (shell.HadErrors)
            throw new InvalidOperationException(string.Join(Environment.NewLine, shell.Streams.Error.Select(e => e.ToString())));
    }

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (State != SessionState.Ready)
            throw new InvalidOperationException("The PowerShell tab is not ready.");
    }

    private string ReadInput(InputRequest request)
    {
        lock (sync) pendingInput = request;
        try
        {
            if (InputRequested is null)
                throw new PSNotSupportedException("No input handler is attached to this host.");
            InputRequested.Invoke(request);
            return request.Response.Task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { throw new PipelineStoppedException(); }
        finally { lock (sync) pendingInput = null; }
    }

    private string? ReadShowCommand(ShowCommandRequest request)
    {
        lock (sync)
        {
            if (stopRequested) throw new PipelineStoppedException();
            pendingShowCommand = request;
        }
        try
        {
            if (ShowCommandRequested is null)
                throw new PSNotSupportedException("No Show-Command handler is attached to this host.");
            ShowCommandRequested.Invoke(request);
            return request.Response.Task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { throw new PipelineStoppedException(); }
        finally { lock (sync) pendingShowCommand = null; }
    }

    private void OnDebuggerStop(object? sender, DebuggerStopEventArgs e)
    {
        lock (sync)
        {
            debuggerResume.Reset();
            resumeAction = DebuggerResumeAction.Continue;
            SetState(SessionState.Debugging);
        }
        DebuggerStopped?.Invoke(new(e.InvocationInfo.ScriptName, e.InvocationInfo.ScriptLineNumber,
            e.InvocationInfo.OffsetInLine, e.InvocationInfo.PositionMessage));
        debuggerResume.Wait();
        lock (sync) e.ResumeAction = resumeAction;
        SetState(SessionState.Running);
        DebuggerStopped?.Invoke(null);
    }

    private void RefreshPrompt()
    {
        using var shell = PowerShell.Create();
        shell.Runspace = runspace;
        shell.AddScript("prompt", useLocalScope: true);
        try
        {
            IAsyncResult invocation;
            lock (sync)
            {
                if (stopRequested) { Prompt = "PS> "; return; }
                invocation = shell.BeginInvoke();
                active = shell;
            }
            var values = shell.EndInvoke(invocation);
            if (shell.HadErrors)
            {
                Output?.Invoke(new("The prompt function failed: " + string.Join("; ", shell.Streams.Error) + Environment.NewLine, OutputKind.Error));
                Prompt = "PS> ";
            }
            else
                Prompt = string.Concat(values.Select(v => v.ToString()));
        }
        catch (PipelineStoppedException)
        {
            Prompt = "PS> ";
            Output?.Invoke(new("Prompt evaluation stopped." + Environment.NewLine, OutputKind.Warning));
        }
        catch (RuntimeException exception)
        {
            Prompt = "PS> ";
            Output?.Invoke(new("The prompt function failed: " + exception.Message + Environment.NewLine, OutputKind.Error));
        }
        finally { lock (sync) active = null; }
    }

    private void SetState(SessionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            if (runspace.Debugger is { } debugger) debugger.DebuggerStop -= OnDebuggerStop;
            await Task.Run(runspace.Dispose);
            debuggerResume.Dispose();
            SetState(SessionState.Disposed);
        }
        finally { gate.Release(); }
    }
}
