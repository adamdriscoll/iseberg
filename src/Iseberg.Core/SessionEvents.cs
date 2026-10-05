using System.Management.Automation;

namespace Iseberg.Core;

public enum OutputKind { Output, Error, Warning, Verbose, Debug, Information, Command }
public enum SessionState { Starting, Ready, Running, Debugging, NestedPrompt, Disposed }

public sealed record OutputStyle(string? Foreground = null, string? Background = null, bool Bold = false, bool Underline = false, bool Inverse = false);
public sealed record OutputEntry(string Text, OutputKind Kind = OutputKind.Output, int CodeStart = 0, OutputStyle? Style = null);
public sealed record DebugLocation(string? ScriptPath, int Line, int Column, string Message);
public sealed record ProgressUpdate(string Activity, string Status, int Percent, bool Completed);
public sealed record CompletionSet(int Start, int Length, IReadOnlyList<CompletionResult> Matches);
public sealed record CommandDescription(string Name, string Module, string Kind, string Definition);

public sealed class ShowCommandRequest
{
    public CommandFormDescription? Command { get; init; }
    public IReadOnlyList<CommandDescription> Commands { get; init; } = [];
    public string HelpText { get; init; } = "";
    public CommandHelpDocument? HelpDocument { get; init; }
    public Uri? HelpUri { get; init; }
    public bool PassThru { get; init; }
    public double Width { get; init; } = 360;
    public double Height { get; init; } = 410;
    public TaskCompletionSource<string?> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class InputRequest(string caption, string message, bool secret = false)
{
    public string Caption { get; } = caption;
    public string Message { get; } = message;
    public bool Secret { get; } = secret;
    public IReadOnlyList<PromptChoice> Choices { get; init; } = [];
    public IReadOnlyList<int> DefaultChoices { get; init; } = [];
    public bool MultipleChoice { get; init; }
    public TaskCompletionSource<string> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed record PromptChoice(string Label, string Help);

public sealed class CommandErrorRequest(string message)
{
    public string Message { get; } = message;
    public TaskCompletionSource Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
