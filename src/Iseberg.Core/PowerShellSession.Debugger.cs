using System.Collections;
using System.Management.Automation;

namespace Iseberg.Core;

public sealed partial class PowerShellSession
{
    private readonly Queue<DebugWork> debugWork = new();
    private readonly Dictionary<int, BreakpointSpec> breakpointSpecs = [];
    private readonly Dictionary<long, object> debugValues = [];
    private long nextDebugValue;
    private bool resumeRequested;
    public bool IsDebuggerPaused
    {
        get { lock (sync) return State == SessionState.Debugging && !resumeRequested; }
    }

    private sealed record DebugWork(Action Execute, Action<Exception> Fail);

    // ProcessCommand must run on the suspended pipeline's DebuggerStop thread, not a second pipeline.
    private Task<T> PausedQueryAsync<T>(Func<T> query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (State != SessionState.Debugging || resumeRequested)
                throw new InvalidOperationException("The debugger is not paused.");
            debugWork.Enqueue(new(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var registration = cancellationToken.Register(() => runspace.Debugger.StopProcessCommand());
                    var value = query();
                    cancellationToken.ThrowIfCancellationRequested();
                    result.TrySetResult(value);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                { result.TrySetCanceled(cancellationToken); }
                catch (Exception exception) { result.TrySetException(exception); }
            }, exception => result.TrySetException(exception)));
            debuggerWake.Set();
        }
        return result.Task.WaitAsync(cancellationToken);
    }

    private async Task<T> BreakpointQueryAsync<T>(Func<T> query, bool mutation = true)
    {
        if (mutation) await PauseForBreakpointEditAsync();
        return State == SessionState.Debugging ? await PausedQueryAsync(query) : await QueryAsync(_ => query(), waitForGate: mutation);
    }

    public async Task PauseForBreakpointEditAsync(CancellationToken cancellationToken = default)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(SessionState state)
        {
            if (state is SessionState.Debugging or SessionState.Ready) ready.TrySetResult();
            else if (state == SessionState.Disposed) ready.TrySetException(new ObjectDisposedException(nameof(PowerShellSession)));
        }
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (State == SessionState.Ready || IsDebuggerPaused) return;
            if (State != SessionState.Running || stopRequested)
                throw new InvalidOperationException("Wait for execution to stop before editing breakpoints.");
            StateChanged += Changed;
            try { BreakAll(); }
            catch { StateChanged -= Changed; throw; }
        }
        try { await ready.Task.WaitAsync(cancellationToken); }
        finally { StateChanged -= Changed; }
    }

    public void BreakAll()
    {
        lock (sync)
        {
            if (State != SessionState.Running || stopRequested)
                throw new InvalidOperationException("A script must be running to break execution.");
            runspace.Debugger.SetDebuggerStepMode(true);
        }
    }

    public Task EvaluateAsync(string script) => PausedQueryAsync(() =>
    {
        Output?.Invoke(new(DebugPrompt + script + Environment.NewLine, OutputKind.Command, DebugPrompt.Length));
        var command = new PSCommand();
        command.AddScript(script, useLocalScope: false).AddCommand("Out-Default");
        using var output = new PSDataCollection<PSObject>();
        var result = runspace.Debugger.ProcessCommand(command, output);
        if (result.ResumeAction is { } action)
        {
            lock (sync)
            {
                resumeAction = action;
                resumeRequested = true;
            }
        }
        return true;
    });

    private PSObject[] Inspect(string script)
    {
        var command = new PSCommand();
        command.AddScript(script, useLocalScope: false);
        return Inspect(command);
    }

    private PSObject[] Inspect(PSCommand command)
    {
        using var output = new PSDataCollection<PSObject>();
        runspace.Debugger.ProcessCommand(command, output);
        var errors = output.Where(value => value.BaseObject is ErrorRecord).Select(value => value.ToString()).ToArray();
        if (errors.Length > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        return output.ToArray();
    }

    public Task<DebugSnapshot> InspectAsync(IEnumerable<string> watches, int frameIndex = 0)
    {
        var expressions = watches.ToArray();
        return PausedQueryAsync(() =>
        {
            if (IsRemote && frameIndex != 0)
                throw new InvalidOperationException("Remote PowerShell exposes evaluation only in the stopped frame. Select the stopped frame to inspect variables.");
            var frames = IsRemote ? [] : runspace.Debugger.GetCallStack().ToArray();
            if (frameIndex < 0 || frameIndex > 0 && frameIndex >= frames.Length)
                throw new InvalidOperationException("The selected call-stack frame no longer exists.");
            var scope = frames.Take(frameIndex).Count(frame => frame.InvocationInfo.InvocationName != ".");
            var remoteVariables = IsRemote ? Inspect("Microsoft.PowerShell.Utility\\Get-Variable") : [];
            var frameVariables = IsRemote ? [] : frameIndex == 0
                ? Inspect("Microsoft.PowerShell.Utility\\Get-Variable").Select(value => value.BaseObject).OfType<PSVariable>()
                : Inspect($"Microsoft.PowerShell.Utility\\Get-Variable -Scope {scope}")
                    .Select(value => value.BaseObject).OfType<PSVariable>();
            var scopedVariables = frameVariables.ToArray();
            var frameLocals = !IsRemote && frames.Length > 0 ? frames[frameIndex].GetFrameVariables() : new Dictionary<string, PSVariable>();
            // Call-stack depth is not scope depth across dotted/module boundaries.
            if (frameIndex > 0 && (!frameLocals.TryGetValue("MyInvocation", out var expected) ||
                !ReferenceEquals(expected.Value, scopedVariables.FirstOrDefault(variable => variable.Name.Equals("MyInvocation", StringComparison.OrdinalIgnoreCase))?.Value)))
                throw new InvalidOperationException("PowerShell does not expose the selected frame's variable scope. Select another frame.");
            debugValues.Clear();
            frameVariables = scopedVariables.Concat(frameLocals.Values).DistinctBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase);
            var variables = IsRemote ? remoteVariables
                .OrderBy(value => value.Properties["Name"]?.Value?.ToString(), StringComparer.OrdinalIgnoreCase)
                .Select(value => DescribeValue("$" + value.Properties["Name"]?.Value, value.Properties["Value"]?.Value)).ToArray() : frameVariables
                .OrderBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
                .Select(variable => DescribeValue("$" + variable.Name, variable.Value)).ToArray();
            var stack = IsRemote ? Inspect(new PSCommand().AddCommand("Get-PSCallStack")).Select((frame, index) =>
                new DebugFrame(frame.Properties["FunctionName"].Value?.ToString() ?? "<remote>",
                    frame.Properties["ScriptName"].Value?.ToString(),
                    Convert.ToInt32(frame.Properties["ScriptLineNumber"].Value), index)).ToArray() : frames.Select((frame, index) =>
                new DebugFrame(frame.FunctionName, frame.ScriptName, frame.ScriptLineNumber, index)).ToArray();
            var values = expressions.Select(expression =>
            {
                try
                {
                    var results = Inspect(expression);
                    return results.Length == 0 ? DescribeValue(expression, null) :
                        results.Length == 1 ? DescribeValue(expression, results[0]) :
                        DescribeValue(expression, results);
                }
                catch (Exception exception) when (exception is RuntimeException or InvalidOperationException)
                {
                    return new DebugValue(expression, "", "", exception.Message);
                }
            }).ToArray();
            return new DebugSnapshot(variables, values, stack);
        });
    }

    private DebugValue DescribeValue(string name, object? value)
    {
        if (value is null) return new(name, "$null", "");
        var wrapped = PSObject.AsPSObject(value);
        var underlying = wrapped.BaseObject;
        var type = underlying.GetType();
        var expandable = underlying is IList or IDictionary ||
            !(type.IsPrimitive || type.IsEnum || underlying is string or decimal or DateTime or DateTimeOffset or TimeSpan or Guid) &&
            wrapped.Properties.Any();
        long? reference = null;
        if (expandable)
        {
            reference = ++nextDebugValue;
            debugValues.Add(reference.Value, value);
        }
        try
        {
            var preview = value.ToString() ?? "";
            if (preview.Length > 512) preview = preview[..512] + "...";
            return new(name, preview, type.Name, Reference: reference);
        }
        catch (Exception exception) { return new(name, "", type.Name, exception.Message, reference); }
    }

    public Task<DebugChildren> GetValueChildrenAsync(long reference, int offset = 0, int count = 100) =>
        PausedQueryAsync(() =>
        {
            if (offset < 0 || count is < 1 or > 100)
                throw new InvalidOperationException("Invalid debugger object page.");
            if (!debugValues.TryGetValue(reference, out var value))
                throw new InvalidOperationException("This debugger value has expired. Refresh the inspector.");
            var wrapped = PSObject.AsPSObject(value);
            IEnumerable<(string Name, Func<object?> Read)> Members()
            {
                if (wrapped.BaseObject is IDictionary dictionary)
                {
                    foreach (var key in dictionary.Keys)
                    {
                        var captured = key;
                        yield return ($"[{key}]", () => dictionary[captured]);
                    }
                }
                else if (wrapped.BaseObject is IList list)
                {
                    for (var index = 0; index < list.Count; index++)
                    {
                        var captured = index;
                        yield return ($"[{index}]", () => list[captured]);
                    }
                }
                else
                    foreach (var property in wrapped.Properties.Where(property => property.IsGettable))
                        yield return (property.Name, () => property.Value);
            }
            var page = Members().Skip(offset).Take(count + 1).ToArray();
            var children = page.Take(count).Select(member =>
            {
                try { return DescribeValue(member.Name, member.Read()); }
                catch (Exception exception) { return new DebugValue(member.Name, "", "", exception.Message); }
            }).ToArray();
            return new DebugChildren(children, page.Length > count ? offset + count : null);
        });

    private Task<CompletionSet> CompletePausedAsync(string text, int cursor, CancellationToken cancellationToken) =>
        PausedQueryAsync(() =>
        {
            var command = new PSCommand().AddCommand("TabExpansion2").AddArgument(text).AddArgument(cursor);
            var result = Inspect(command).Select(value => value.BaseObject).OfType<CommandCompletion>().FirstOrDefault()
                ?? throw new InvalidOperationException("PowerShell completion did not return a completion result.");
            return new CompletionSet(result.ReplacementIndex, result.ReplacementLength, result.CompletionMatches.ToArray());
        }, cancellationToken);

    public Task<IReadOnlyList<DebugBreakpoint>> GetBreakpointsAsync() =>
        BreakpointQueryAsync<IReadOnlyList<DebugBreakpoint>>(() => runspace.Debugger.GetBreakpoints()
            .Select(DescribeBreakpoint).OrderBy(breakpoint => breakpoint.Id).ToArray(), mutation: false);

    public Task<DebugBreakpoint> AddBreakpointAsync(BreakpointSpec spec)
    {
        spec.Validate();
        return BreakpointQueryAsync(() => DescribeBreakpoint(CreateBreakpoint(spec)));
    }

    public Task<DebugBreakpoint> UpdateBreakpointAsync(int id, BreakpointSpec spec)
    {
        spec.Validate();
        return BreakpointQueryAsync(() =>
        {
            var previous = FindBreakpoint(id);
            var replacement = CreateBreakpoint(spec);
            runspace.Debugger.RemoveBreakpoint(previous);
            breakpointSpecs.Remove(id);
            return DescribeBreakpoint(replacement);
        });
    }

    public Task SetBreakpointEnabledAsync(int id, bool enabled) => BreakpointQueryAsync(() =>
    {
        var breakpoint = FindBreakpoint(id);
        if (enabled) runspace.Debugger.EnableBreakpoint(breakpoint);
        else runspace.Debugger.DisableBreakpoint(breakpoint);
        if (breakpointSpecs.TryGetValue(id, out var spec)) breakpointSpecs[id] = spec with { Enabled = enabled };
        return true;
    });

    public Task RemoveBreakpointAsync(int id) => BreakpointQueryAsync(() =>
    {
        runspace.Debugger.RemoveBreakpoint(FindBreakpoint(id));
        breakpointSpecs.Remove(id);
        return true;
    });

    public Task RemoveAllBreakpointsAsync() => BreakpointQueryAsync(() =>
    {
        foreach (var breakpoint in runspace.Debugger.GetBreakpoints()) runspace.Debugger.RemoveBreakpoint(breakpoint);
        breakpointSpecs.Clear();
        return true;
    });

    public Task SetLineBreakpointsAsync(string path, IEnumerable<BreakpointSpec> specs)
    {
        var requested = specs.Select(spec => spec with { Kind = BreakpointKind.Line, ScriptPath = path }).ToArray();
        foreach (var spec in requested) spec.Validate();
        if (requested.DistinctBy(spec => spec.Line).Count() != requested.Length)
            throw new ArgumentException("Only one editor breakpoint can be specified per line.");
        return BreakpointQueryAsync(() =>
        {
            var existing = runspace.Debugger.GetBreakpoints().OfType<LineBreakpoint>().Where(breakpoint =>
                string.Equals(breakpoint.Script, path, !IsRemote && OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToList();
            var added = new List<Breakpoint>();
            try
            {
                foreach (var spec in requested)
                {
                    var unchanged = existing.FirstOrDefault(breakpoint => DescribeBreakpoint(breakpoint).Spec == spec);
                    if (unchanged is not null) existing.Remove(unchanged);
                    else added.Add(CreateBreakpoint(spec));
                }
            }
            catch
            {
                foreach (var breakpoint in added)
                {
                    runspace.Debugger.RemoveBreakpoint(breakpoint);
                    breakpointSpecs.Remove(breakpoint.Id);
                }
                throw;
            }
            foreach (var breakpoint in existing)
            {
                runspace.Debugger.RemoveBreakpoint(breakpoint);
                breakpointSpecs.Remove(breakpoint.Id);
            }
            return true;
        });
    }

    private Breakpoint FindBreakpoint(int id) => runspace.Debugger.GetBreakpoint(id) ??
        throw new InvalidOperationException($"Breakpoint #{id} no longer exists.");

    private static ScriptBlock? BreakpointAction(BreakpointSpec spec) =>
        !string.IsNullOrWhiteSpace(spec.Condition) ? ScriptBlock.Create($"if ({spec.Condition}) {{ break }}") :
        !string.IsNullOrWhiteSpace(spec.Action) ? ScriptBlock.Create(spec.Action) : null;

    private Breakpoint CreateBreakpoint(BreakpointSpec spec)
    {
        spec.Validate();
        var action = BreakpointAction(spec);
        var script = string.IsNullOrWhiteSpace(spec.ScriptPath) ? null : spec.ScriptPath;
        Breakpoint breakpoint = spec.Kind switch
        {
            BreakpointKind.Line => runspace.Debugger.SetLineBreakpoint(script!, spec.Line, 0, action),
            BreakpointKind.Command => runspace.Debugger.SetCommandBreakpoint(spec.Target, action, script),
            BreakpointKind.Variable => runspace.Debugger.SetVariableBreakpoint(spec.Target.TrimStart('$'), spec.AccessMode, action, script),
            _ => throw new ArgumentException("Invalid breakpoint kind.")
        };
        if (!spec.Enabled) breakpoint = runspace.Debugger.DisableBreakpoint(breakpoint);
        breakpointSpecs[breakpoint.Id] = spec;
        return breakpoint;
    }

    private DebugBreakpoint DescribeBreakpoint(Breakpoint breakpoint)
    {
        if (!breakpointSpecs.TryGetValue(breakpoint.Id, out var spec))
            spec = breakpoint switch
            {
                LineBreakpoint line => new(BreakpointKind.Line, line.Script, Line: line.Line, Action: line.Action?.ToString() ?? ""),
                CommandBreakpoint command => new(BreakpointKind.Command, command.Script, command.Command, Action: command.Action?.ToString() ?? ""),
                VariableBreakpoint variable => new(BreakpointKind.Variable, variable.Script, variable.Variable, AccessMode: variable.AccessMode, Action: variable.Action?.ToString() ?? ""),
                _ => throw new InvalidOperationException("Unknown PowerShell breakpoint type.")
            };
        return new(breakpoint.Id, spec with { Enabled = breakpoint.Enabled }, breakpoint.HitCount);
    }
}
