using System.Management.Automation;

namespace Iseberg.Core;

public enum OutputKind { Output, Error, Warning, Verbose, Debug, Information, Command }
public enum SessionState { Starting, Ready, Running, Debugging, Disposed }

public sealed record OutputEntry(string Text, OutputKind Kind = OutputKind.Output, int CodeStart = 0);
public sealed record DebugLocation(string? ScriptPath, int Line, int Column, string Message);
public sealed record ProgressUpdate(string Activity, string Status, int Percent, bool Completed);
public sealed record CompletionSet(int Start, int Length, IReadOnlyList<CompletionResult> Matches);
public sealed record CommandDescription(string Name, string Module, string Kind, string Definition);

public sealed class ShowCommandRequest
{
    public CommandFormDescription? Command { get; init; }
    public IReadOnlyList<CommandDescription> Commands { get; init; } = [];
    public string HelpText { get; init; } = "";
    public Uri? HelpUri { get; init; }
    public bool PassThru { get; init; }
    public double Width { get; init; } = 620;
    public double Height { get; init; } = 700;
    public TaskCompletionSource<string?> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class InputRequest(string caption, string message, bool secret = false)
{
    public string Caption { get; } = caption;
    public string Message { get; } = message;
    public bool Secret { get; } = secret;
    public TaskCompletionSource<string> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
