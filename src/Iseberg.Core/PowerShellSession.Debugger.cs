using System.Management.Automation;

namespace Iseberg.Core;

public sealed partial class PowerShellSession
{
    private readonly Queue<DebugWork> debugWork = new();
    private readonly Dictionary<int, BreakpointSpec> breakpointSpecs = [];
    private bool resumeRequested;
    public bool IsDebuggerPaused
    {
        get { lock (sync) return State == SessionState.Debugging && !resumeRequested; }
    }

    private sealed record DebugWork(Action Execute, Action<Exception> Fail);

    // ProcessCommand must run on the suspended pipeline's DebuggerStop thread, not a second pipeline.
    private Task<T> PausedQueryAsync<T>(Func<T> query)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (State != SessionState.Debugging || resumeRequested)
                throw new InvalidOperationException("The debugger is not paused.");
            debugWork.Enqueue(new(() =>
            {
                try { result.TrySetResult(query()); }
                catch (Exception exception) { result.TrySetException(exception); }
            }, exception => result.TrySetException(exception)));
            debuggerWake.Set();
        }
        return result.Task;
    }

    private Task<T> BreakpointQueryAsync<T>(Func<T> query) =>
        State == SessionState.Debugging ? PausedQueryAsync(query) : QueryAsync(_ => query());

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
        Output?.Invoke(new("[DBG]: PS> " + script + Environment.NewLine, OutputKind.Command, 11));
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
        using var output = new PSDataCollection<PSObject>();
        runspace.Debugger.ProcessCommand(command, output);
        var errors = output.Where(value => value.BaseObject is ErrorRecord).Select(value => value.ToString()).ToArray();
        if (errors.Length > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        return output.ToArray();
    }

    public Task<DebugSnapshot> InspectAsync(IEnumerable<string> watches)
    {
        var expressions = watches.ToArray();
        return PausedQueryAsync(() =>
        {
            var variables = Inspect("Microsoft.PowerShell.Utility\\Get-Variable").Select(value => value.BaseObject).OfType<PSVariable>()
                .OrderBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
                .Select(variable => DescribeValue("$" + variable.Name, variable.Value)).ToArray();
            var stack = runspace.Debugger.GetCallStack().Select(frame =>
                new DebugFrame(frame.FunctionName, frame.ScriptName, frame.ScriptLineNumber)).ToArray();
            var values = expressions.Select(expression =>
            {
                try
                {
                    var results = Inspect(expression);
                    return results.Length == 0 ? DescribeValue(expression, null) :
                        results.Length == 1 ? DescribeValue(expression, results[0].BaseObject) :
                        new DebugValue(expression, string.Join(", ", results.Select(value => value.ToString())), "Object[]");
                }
                catch (Exception exception) when (exception is RuntimeException or InvalidOperationException)
                {
                    return new DebugValue(expression, "", "", exception.Message);
                }
            }).ToArray();
            return new DebugSnapshot(variables, values, stack);
        });
    }

    private static DebugValue DescribeValue(string name, object? value)
    {
        if (value is null) return new(name, "$null", "");
        try { return new(name, value.ToString() ?? "", value.GetType().Name); }
        catch (Exception exception) { return new(name, "", value.GetType().Name, exception.Message); }
    }

    public Task<IReadOnlyList<DebugBreakpoint>> GetBreakpointsAsync() =>
        BreakpointQueryAsync<IReadOnlyList<DebugBreakpoint>>(() => runspace.Debugger.GetBreakpoints()
            .Select(DescribeBreakpoint).OrderBy(breakpoint => breakpoint.Id).ToArray());

    public Task<DebugBreakpoint> AddBreakpointAsync(BreakpointSpec spec) =>
        BreakpointQueryAsync(() => DescribeBreakpoint(CreateBreakpoint(spec)));

    public Task<DebugBreakpoint> UpdateBreakpointAsync(int id, BreakpointSpec spec) =>
        BreakpointQueryAsync(() =>
        {
            var previous = FindBreakpoint(id);
            var replacement = CreateBreakpoint(spec);
            runspace.Debugger.RemoveBreakpoint(previous);
            breakpointSpecs.Remove(id);
            return DescribeBreakpoint(replacement);
        });

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
                string.Equals(breakpoint.Script, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToList();
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
        if (!spec.Enabled) runspace.Debugger.DisableBreakpoint(breakpoint);
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
