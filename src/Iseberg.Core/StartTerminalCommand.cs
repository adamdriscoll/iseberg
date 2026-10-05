using System.Management.Automation;

namespace Iseberg.Core;

[Cmdlet(VerbsLifecycle.Start, "IsebergTerminal")]
[OutputType(typeof(int))]
public sealed class StartTerminalCommand : PSCmdlet
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly object sync = new();
    private bool finished;

    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string FilePath { get; set; } = "";

    [Parameter(Position = 1)]
    public string[] ArgumentList { get; set; } = [];

    [Parameter]
    public string? WorkingDirectory { get; set; }

    protected override void EndProcessing()
    {
        try
        {
            if (Host.PrivateData?.BaseObject is not WorkbenchHostServices services || services.IsRunspacePushed())
                throw new PSNotSupportedException("Start-IsebergTerminal requires a local Iseberg runspace.");
            var executable = InvokeCommand.GetCommand(FilePath, CommandTypes.Application) as ApplicationInfo
                ?? throw new CommandNotFoundException($"Native application '{FilePath}' was not found.");
            var directory = WorkingDirectory is null ? SessionState.Path.CurrentFileSystemLocation.ProviderPath :
                SessionState.Path.GetUnresolvedProviderPathFromPSPath(WorkingDirectory);
            WriteWarning("Input/output will use a separate system terminal. Stop terminates the application and its child processes.");
            var exitCode = TerminalApplication.RunAsync(executable.Path, ArgumentList, directory, cancellation.Token)
                .GetAwaiter().GetResult();
            SessionState.PSVariable.Set("LASTEXITCODE", exitCode);
            WriteObject(exitCode);
        }
        catch (OperationCanceledException) { throw new PipelineStoppedException(); }
        finally
        {
            lock (sync) { finished = true; cancellation.Dispose(); }
        }
    }

    protected override void StopProcessing()
    {
        lock (sync) if (!finished) cancellation.Cancel();
    }
}
