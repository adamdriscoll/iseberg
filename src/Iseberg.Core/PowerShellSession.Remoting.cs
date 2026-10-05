using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text.Json;

namespace Iseberg.Core;

public sealed partial class PowerShellSession
{
    private Runspace? pushedRunspace;
    private bool ownsPushedRunspace;
    private Runspace? connectingRunspace;
    private readonly Dictionary<Guid, Dictionary<int, BreakpointSpec>> runspaceBreakpointSpecs = [];
    public event Action? RunspaceChanged;
    public bool IsRunspacePushed => pushedRunspace is not null;
    public bool IsRemote => runspace.ConnectionInfo is not null;
    public Guid RunspaceId => runspace.InstanceId;
    public string? RemoteComputerName => runspace.ConnectionInfo?.ComputerName;
    public string DebugPrompt => IsRemote ? $"[{RemoteComputerName}]: [DBG]: PS> " : "[DBG]: PS> ";

    public async Task ConnectAsync(RunspaceConnectionInfo connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await gate.WaitAsync(cancellationToken);
        Runspace? remote = null;
        try
        {
            EnsureReady();
            if (IsRunspacePushed) throw new InvalidOperationException("Exit the current remote session before connecting.");
            lock (sync) stopRequested = false;
            SetState(SessionState.Running);
            remote = RunspaceFactory.CreateRunspace(connection, host, TypeTable.LoadDefaultTypeFiles());
            lock (sync) connectingRunspace = remote;
            using var registration = cancellationToken.Register(remote.CloseAsync);
            await Task.Run(remote.Open, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            PushRunspace(remote, true);
            remote = null;
            await Task.Run(RefreshPrompt);
        }
        finally
        {
            lock (sync) connectingRunspace = null;
            remote?.Dispose();
            if (!disposed && State != SessionState.Starting) SetState(SessionState.Ready);
            gate.Release();
        }
    }

    public async Task ExitRemoteSessionAsync()
    {
        if (!await gate.WaitAsync(0)) throw new InvalidOperationException("Stop execution before leaving the remote session.");
        var started = false;
        try
        {
            EnsureReady();
            if (!IsRunspacePushed) throw new InvalidOperationException("No interactive session is active.");
            lock (sync) stopRequested = false;
            started = true;
            SetState(SessionState.Running);
            PopRunspace();
            await Task.Run(RefreshPrompt);
        }
        finally
        {
            if (started && !disposed) SetState(SessionState.Ready);
            gate.Release();
        }
    }

    private void PushRunspace(Runspace remote, bool owned)
    {
        ArgumentNullException.ThrowIfNull(remote);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsRunspacePushed) throw new InvalidOperationException("Nested interactive sessions are not supported. Exit the current session first.");
            if (remote.RunspaceStateInfo.State != RunspaceState.Opened)
                throw new InvalidOperationException("Only an opened runspace can be pushed.");
            owned |= HostOwnsRunspace(remote);
            remote.Debugger.SetDebugMode(DebugModes.LocalScript | DebugModes.RemoteScript);
            remote.Debugger.DebuggerStop += OnDebuggerStop;
            remote.StateChanged += OnRemoteStateChanged;
            SaveRunspaceBreakpoints();
            pushedRunspace = remote;
            ownsPushedRunspace = owned;
            RestoreRunspaceBreakpoints();
            debugValues.Clear();
        }
        RunspaceChanged?.Invoke();
    }

    private void PopRunspace()
    {
        Runspace remote;
        bool owned;
        lock (sync)
        {
            remote = pushedRunspace ?? throw new InvalidOperationException("No interactive session is active.");
            remote.Debugger.DebuggerStop -= OnDebuggerStop;
            remote.StateChanged -= OnRemoteStateChanged;
            SaveRunspaceBreakpoints();
            owned = ownsPushedRunspace;
            pushedRunspace = null;
            ownsPushedRunspace = false;
            RestoreRunspaceBreakpoints();
            debugValues.Clear();
        }
        if (owned)
        {
            runspaceBreakpointSpecs.Remove(remote.InstanceId);
            remote.Dispose();
        }
        RunspaceChanged?.Invoke();
    }

    private static bool HostOwnsRunspace(Runspace remote)
    {
        if (remote.ConnectionInfo is null) return false;
        // The SDK's ownership flag is internal; reusable PSSession runspaces must remain open.
        var ownership = remote.GetType().GetProperty("ShouldCloseOnPop", BindingFlags.Instance | BindingFlags.NonPublic);
        return ownership?.GetValue(remote) is bool owned ? owned :
            throw new PSNotSupportedException("This PowerShell SDK does not expose interactive runspace ownership.");
    }

    private void SaveRunspaceBreakpoints() =>
        runspaceBreakpointSpecs[RunspaceId] = new(breakpointSpecs);

    private void RestoreRunspaceBreakpoints()
    {
        breakpointSpecs.Clear();
        if (runspaceBreakpointSpecs.TryGetValue(RunspaceId, out var saved))
            foreach (var entry in saved) breakpointSpecs.Add(entry.Key, entry.Value);
    }

    private async void OnRemoteStateChanged(object? sender, RunspaceStateEventArgs args)
    {
        if (sender is not Runspace remote || args.RunspaceStateInfo.State is not
            (RunspaceState.Broken or RunspaceState.Closed or RunspaceState.Disconnected)) return;
        Output?.Invoke(new($"Remote connection ended: {args.RunspaceStateInfo.Reason?.Message ?? args.RunspaceStateInfo.State.ToString()}{Environment.NewLine}", OutputKind.Error));
        await gate.WaitAsync();
        try
        {
            if (disposed || pushedRunspace != remote) return;
            SetState(SessionState.Running);
            PopRunspace();
            await Task.Run(RefreshPrompt);
            SetState(SessionState.Ready);
        }
        catch (Exception exception) when (exception is RuntimeException or InvalidOperationException)
        {
            Output?.Invoke(new($"Could not restore the local session: {exception.Message}{Environment.NewLine}", OutputKind.Error));
        }
        finally { gate.Release(); }
    }

    private static bool IsExitSessionCommand(string script)
    {
        var ast = Parser.ParseInput(script, out _, out var errors);
        return errors.Length == 0 && ast.EndBlock?.Statements is { Count: 1 } statements &&
            (statements[0] is ExitStatementAst ||
            statements[0] is PipelineAst { PipelineElements.Count: 1 } pipeline &&
            pipeline.PipelineElements[0] is CommandAst { CommandElements.Count: 1 } command &&
            (string.Equals(command.GetCommandName(), "Exit-PSSession", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(command.GetCommandName(), "Exit-PSHostProcess", StringComparison.OrdinalIgnoreCase)));
    }

    private void EnsureRemoteFile(ScriptFile file)
    {
        if (!IsRemote || file.RemoteRunspaceId != RunspaceId)
            throw new InvalidOperationException("This document belongs to another remote connection. Reconnect and reopen it before saving or running it.");
    }

    public Task<ScriptFile> OpenRemoteFileAsync(string path) => OpenRemoteFileAsync(path, null);

    public Task<ScriptFile> OpenRemoteFileAsync(string path, ScriptEncoding? choice = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!IsRemote) throw new InvalidOperationException("Connect to a remote session before opening remote files.");
        var id = RunspaceId;
        var computer = RemoteComputerName!;
        var command = new PSCommand().AddScript("""
            param($path)
            $ErrorActionPreference = 'Stop'
            $item = Get-Item -LiteralPath $path
            if ($item.PSProvider.Name -ne 'FileSystem' -or $item.PSIsContainer) { throw 'Select a filesystem file.' }
            [pscustomobject]@{ Path = $item.FullName; Content = [Convert]::ToBase64String([IO.File]::ReadAllBytes($item.FullName)) }
            """, useLocalScope: true).AddArgument(path);
        return FileQueryAsync(command, values =>
        {
            if (RunspaceId != id) throw new InvalidOperationException("The remote connection changed while opening this file.");
            var value = values.Single();
            return ScriptFile.FromRemoteBytes(value.Properties["Path"].Value.ToString()!,
                Convert.FromBase64String(value.Properties["Content"].Value.ToString()!), id, computer, choice);
        }, id);
    }

    public Task SaveRemoteFileAsync(ScriptFile file, string? path = null) =>
        SaveRemoteFileCoreAsync(file, path, file.SavedVersion, useSavedVersion: true);

    // Explicit consent: null requires absence; a hash authorizes only that server-side content.
    public Task SaveRemoteFileAsync(ScriptFile file, string? path, string? expectedVersion) =>
        SaveRemoteFileCoreAsync(file, path, expectedVersion, useSavedVersion: false);

    private async Task SaveRemoteFileCoreAsync(ScriptFile file, string? path, string? expectedVersion, bool useSavedVersion)
    {
        if (file.IsRemote) EnsureRemoteFile(file);
        else if (!IsRemote || file.Path is not null)
            throw new InvalidOperationException("Only remote documents or untitled scripts can be saved to the remote session.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path ?? file.Path);
        var id = RunspaceId;
        var computer = RemoteComputerName!;
        var snapshot = file.Text;
        var snapshotEncoding = file.EncodingChoice;
        var bytes = ScriptFile.Encode(snapshot, snapshotEncoding);
        var command = new PSCommand().AddScript("""
            param($path, $content, $expectedVersion, $originalPath, $useSavedVersion)
            $ErrorActionPreference = 'Stop'
            $provider = $null
            $drive = $null
            $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path, [ref]$provider, [ref]$drive)
            if ($provider.Name -ne 'FileSystem') { throw 'Select a filesystem file.' }
            if ($useSavedVersion) {
                $samePath = $false
                if ($originalPath) {
                    $originalFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($originalPath)
                    $comparison = if ([Environment]::OSVersion.Platform -eq 'Win32NT') {
                        [StringComparison]::OrdinalIgnoreCase
                    } else { [StringComparison]::Ordinal }
                    $samePath = [string]::Equals($originalFull, $full, $comparison)
                }
                if (!$samePath) { $expectedVersion = $null }
            }
            $temp = $full + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
            $current = $null
            try {
                [IO.File]::WriteAllBytes($temp, [Convert]::FromBase64String($content))
                try {
                    $current = [IO.File]::Open($full, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                        [IO.FileShare]::Read -bor [IO.FileShare]::Delete)
                } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
                $actualVersion = $null
                if ($current) {
                    $sha = [Security.Cryptography.SHA256]::Create()
                    try { $actualVersion = [BitConverter]::ToString($sha.ComputeHash($current)).Replace('-', '') }
                    finally { $sha.Dispose() }
                }
                $sameVersion = if ($null -eq $expectedVersion) { $null -eq $actualVersion }
                    else { $null -ne $actualVersion -and [string]::Equals($expectedVersion, $actualVersion, [StringComparison]::OrdinalIgnoreCase) }
                if (!$sameVersion) {
                    [pscustomobject]@{ Path = $full; Conflict = $true; ExpectedVersion = $expectedVersion; ActualVersion = $actualVersion }
                    return
                }
                if ($current) {
                    if ($IsLinux -or $IsMacOS) { [IO.File]::SetUnixFileMode($temp, [IO.File]::GetUnixFileMode($full)) }
                    [IO.File]::Replace($temp, $full, [System.Management.Automation.Language.NullString]::Value)
                } else { [IO.File]::Move($temp, $full) }
                [pscustomobject]@{ Path = $full; Conflict = $false }
            } finally {
                if ($current) { $current.Dispose() }
                if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
            }
            """, useLocalScope: true).AddArgument(path ?? file.Path).AddArgument(Convert.ToBase64String(bytes))
            .AddArgument(expectedVersion).AddArgument(file.IsRemote ? file.Path : null).AddArgument(useSavedVersion);
        var savedPath = await FileQueryAsync(command, values =>
        {
            var result = values.Single();
            var savedPath = result.Properties["Path"].Value.ToString()!;
            if (LanguagePrimitives.ConvertTo<bool>(result.Properties["Conflict"].Value))
                throw new FileConflictException(savedPath, result.Properties["ExpectedVersion"].Value?.ToString(),
                    result.Properties["ActualVersion"].Value?.ToString());
            return savedPath;
        }, id);
        file.MarkRemoteSaved(savedPath, snapshot, snapshotEncoding, ScriptFile.GetVersion(bytes), id, computer);
    }

    private Task<T> FileQueryAsync<T>(PSCommand command, Func<PSObject[], T> read, Guid? expectedRunspace = null) =>
        IsDebuggerPaused ? PausedQueryAsync(() =>
        {
            CheckFileRunspace(expectedRunspace);
            return read(Inspect(command));
        }) : QueryAsync(shell =>
        {
            CheckFileRunspace(expectedRunspace);
            shell.Commands = command;
            var values = shell.Invoke();
            ThrowQueryErrors(shell);
            return read(values.ToArray());
        }, waitForGate: true);

    private void CheckFileRunspace(Guid? expected)
    {
        if (!IsRemote || expected is { } id && RunspaceId != id)
            throw new InvalidOperationException("The remote connection changed. Reopen the file in the active connection.");
    }

    public Task<bool> RemoteFileExistsAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FileQueryAsync(new PSCommand().AddCommand("Test-Path").AddParameter("LiteralPath", path),
            values => LanguagePrimitives.ConvertTo<bool>(values.Single()), RunspaceId);
    }

    public Task<string?> GetRemoteFileVersionAsync(string path) => ReadRemoteFileVersionAsync(path);

    public Task<string?> ReadRemoteFileVersionAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var command = new PSCommand().AddScript("""
            param($path)
            $ErrorActionPreference = 'Stop'
            $provider = $null
            $drive = $null
            $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path, [ref]$provider, [ref]$drive)
            if ($provider.Name -ne 'FileSystem') { throw 'Select a filesystem file.' }
            $current = $null
            try {
                try {
                    $current = [IO.File]::Open($full, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                        [IO.FileShare]::Read -bor [IO.FileShare]::Delete)
                } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
                $version = $null
                if ($current) {
                    $sha = [Security.Cryptography.SHA256]::Create()
                    try { $version = [BitConverter]::ToString($sha.ComputeHash($current)).Replace('-', '') }
                    finally { $sha.Dispose() }
                }
                [pscustomobject]@{ Version = $version }
            } finally { if ($current) { $current.Dispose() } }
            """, useLocalScope: true).AddArgument(path);
        return FileQueryAsync(command, values => values.Single().Properties["Version"].Value?.ToString(), RunspaceId);
    }

    public async Task<bool> HasRemoteFileChangesAsync(ScriptFile file)
    {
        EnsureRemoteFile(file);
        return !ScriptFile.VersionsMatch(file.SavedVersion, await ReadRemoteFileVersionAsync(file.Path!));
    }

    private static CommandFormDescription GetRemoteCommandForm(PowerShell shell, string name, string? module)
    {
        // Project metadata on the server: deserialized CommandInfo cannot be cast to the local SDK type.
        shell.AddScript("""
            param($name, $module)
            $ErrorActionPreference = 'Stop'
            $parameters = @{ Name = [System.Management.Automation.WildcardPattern]::Escape($name) }
            if ($module) { $parameters.Module = $module }
            $command = Get-Command @parameters | Where-Object Name -EQ $name | Select-Object -First 1
            if (!$command) { throw "Command '$name' was not found." }
            $resolved = $command
            while ($resolved -is [System.Management.Automation.AliasInfo]) { $resolved = $resolved.ResolvedCommand }
            if (!$resolved) { throw "Cannot resolve alias '$name'." }
            $common = @('Verbose','Debug','ErrorAction','WarningAction','InformationAction','ProgressAction',
                'ErrorVariable','WarningVariable','InformationVariable','OutVariable','OutBuffer','PipelineVariable','WhatIf','Confirm')
            $sets = @($resolved.ParameterSets | ForEach-Object {
                $set = $_
                $items = @($set.Parameters | Sort-Object @{Expression='IsMandatory';Descending=$true},
                    @{Expression={if ($_.Position -lt 0) { [int]::MaxValue } else { $_.Position }}}, Name | ForEach-Object {
                    $p = $_
                    $type = $p.ParameterType
                    $element = if ($type.IsArray) { $type.GetElementType() } else { $type }
                    $nullable = [Nullable]::GetUnderlyingType($element)
                    if ($nullable) { $element = $nullable }
                    $validation = $p.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] } | Select-Object -First 1
                    $choices = @()
                    if ($validation) { $choices = @($validation.ValidValues) }
                    elseif ($element.IsEnum) { $choices = @([Enum]::GetNames($element)) }
                    elseif ($type.IsArray -and $element -eq [bool]) { $choices = @('True','False') }
                    $kind = if (!$type.IsArray -and $element -eq [System.Management.Automation.SwitchParameter]) { 1 }
                        elseif ($element -eq [bool]) { 2 } elseif ($choices.Count) { 3 } else { 0 }
                    $help = $p.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.HelpMessage } | Select-Object -First 1
                    [ordered]@{
                        Name=$p.Name; TypeName=$type.Name; Kind=$kind; IsMandatory=$p.IsMandatory
                        Position=$(if ($p.Position -lt 0) { $null } else { $p.Position }); IsArray=$type.IsArray
                        IsCommon=($p.Name -in $common)
                        AllowsEmptyString=[bool]@($p.Attributes | Where-Object { $_ -is [System.Management.Automation.AllowEmptyStringAttribute] }).Count
                        AcceptsPipelineInput=($p.ValueFromPipeline -or $p.ValueFromPipelineByPropertyName)
                        HelpMessage=$(if ($help) { $help.HelpMessage } else { '' })
                        Aliases=@($p.Aliases); Choices=$choices
                        ChoicesIgnoreCase=$(if ($validation) { $validation.IgnoreCase } else { $true })
                    }
                })
                [ordered]@{ Name=$set.Name; IsDefault=$set.IsDefault; Parameters=$items }
            })
            if (!$sets.Count) { $sets = @(@{Name='__AllParameterSets';IsDefault=$true;Parameters=@()}) }
            [ordered]@{
                Name=$command.Name
                InvocationName=$(if ($command.ModuleName) { $command.ModuleName + '\' + $command.Name } else { $command.Name })
                ParameterSets=$sets
            } | ConvertTo-Json -Depth 12 -Compress
            """, useLocalScope: true).AddArgument(name).AddArgument(module);
        var json = shell.Invoke<string>();
        ThrowQueryErrors(shell);
        return JsonSerializer.Deserialize<CommandFormDescription>(json.Single())
            ?? throw new InvalidOperationException("Remote command metadata was empty.");
    }
}
