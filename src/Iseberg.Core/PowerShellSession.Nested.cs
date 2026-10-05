using System.Management.Automation;

namespace Iseberg.Core;

public sealed partial class PowerShellSession
{
    private readonly Stack<NestedFrame> nestedFrames = new();
    private sealed class NestedFrame
    {
        public Queue<DebugWork> Work { get; } = new();
        public bool Exit { get; set; }
        public bool Busy { get; set; }
    }
    public bool IsNestedPromptActive
    {
        get { lock (sync) return State == SessionState.NestedPrompt && nestedFrames.TryPeek(out var frame) && !frame.Exit && !frame.Busy && !stopRequested; }
    }
    public string NestedPrompt
    {
        get { lock (sync) return $"[Nested {nestedFrames.Count}]: PS> "; }
    }

    private void EnterNestedPrompt()
    {
        if (IsRemote)
            throw new PSNotSupportedException("Graphical nested prompts are local-only. Use remote breakpoints instead.");
        if (State == SessionState.Debugging)
            throw new PSNotSupportedException("Nested host prompts inside debugger evaluation are not supported. Use the debugger console directly.");
        var frame = new NestedFrame();
        SessionState previous;
        lock (sync)
        {
            if (stopRequested) throw new PipelineStoppedException();
            previous = State;
            nestedFrames.Push(frame);
            SetState(SessionState.NestedPrompt);
        }
        try
        {
            while (true)
            {
                DebugWork? work;
                lock (sync)
                {
                    if (frame.Exit || stopRequested) break;
                    work = frame.Work.TryDequeue(out var next) ? next : null;
                }
                if (work is null) debuggerWake.WaitOne();
                else work.Execute();
            }
            lock (sync)
                if (stopRequested) throw new PipelineStoppedException();
        }
        finally
        {
            lock (sync)
            {
                nestedFrames.Pop();
                while (frame.Work.TryDequeue(out var work))
                    work.Fail(new InvalidOperationException("The nested prompt has exited."));
                SetState(previous);
            }
        }
    }

    private void ExitNestedPrompt()
    {
        lock (sync)
        {
            if (!nestedFrames.TryPeek(out var frame))
                throw new PSNotSupportedException("No nested prompt is active.");
            frame.Exit = true;
            debuggerWake.Set();
        }
    }

    private Task<T> NestedQueryAsync<T>(Func<PowerShell, T> query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsNestedPromptActive) throw new InvalidOperationException("No nested prompt is available.");
            var frame = nestedFrames.Peek();
            frame.Busy = true;
            StateChanged?.Invoke(State);
            frame.Work.Enqueue(new(() =>
            {
                T? value = default;
                Exception? failure = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Nested pipelines must execute on the thread that called EnterNestedPrompt.
                    using var shell = PowerShell.Create(RunspaceMode.CurrentRunspace);
                    using var registration = cancellationToken.Register(() => shell.Stop());
                    value = query(shell);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    lock (sync)
                    {
                        frame.Busy = false;
                        StateChanged?.Invoke(State);
                    }
                }
                if (cancellationToken.IsCancellationRequested) result.TrySetCanceled(cancellationToken);
                else if (failure is not null) result.TrySetException(failure);
                else result.TrySetResult(value!);
            }, exception => result.TrySetException(exception)));
            debuggerWake.Set();
        }
        return result.Task.WaitAsync(cancellationToken);
    }

    public Task EvaluateNestedAsync(string script) => NestedQueryAsync(shell =>
    {
        var prompt = NestedPrompt;
        Output?.Invoke(new(prompt + script + Environment.NewLine, OutputKind.Command, prompt.Length));
        shell.Streams.Error.DataAdded += (_, args) =>
            Output?.Invoke(new(shell.Streams.Error[args.Index].ToString() + Environment.NewLine, OutputKind.Error));
        try { shell.AddScript(script, useLocalScope: false).AddCommand("Out-Default").Invoke(); }
        catch (RuntimeException exception) when (exception is not PipelineStoppedException)
        { Output?.Invoke(new(exception.ErrorRecord.ToString() + Environment.NewLine, OutputKind.Error)); }
        return true;
    });
}
