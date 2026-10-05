namespace Iseberg.Core;

public sealed record TerminalRequest(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory,
    CancellationToken CancellationToken)
{
    public TaskCompletionSource<int> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
