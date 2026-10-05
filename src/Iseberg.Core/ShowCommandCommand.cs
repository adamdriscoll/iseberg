using System.Management.Automation;

namespace Iseberg.Core;

[Cmdlet(VerbsCommon.Show, "IsebergCommand")]
[OutputType(typeof(string))]
public sealed class ShowCommandCommand : PSCmdlet
{
    [Parameter(Position = 0)]
    [ValidateNotNullOrEmpty]
    public string? Name { get; set; }

    [Parameter]
    public SwitchParameter PassThru { get; set; }

    [Parameter]
    public SwitchParameter NoCommonParameter { get; set; }

    [Parameter]
    [ValidateRange(300, int.MaxValue)]
    public int Width { get; set; } = 360;

    [Parameter]
    [ValidateRange(300, int.MaxValue)]
    public int Height { get; set; } = 410;

    protected override void EndProcessing()
    {
        if (Host.PrivateData?.BaseObject is not WorkbenchHostServices host)
            throw new PSNotSupportedException("This Show-Command implementation requires the Iseberg host.");
        var name = Name;
        if (string.IsNullOrEmpty(name))
        {
            var commands = InvokeCommand.InvokeScript(false, ScriptBlock.Create("Get-Command"), null)
                .Select(entry => entry.BaseObject).OfType<CommandInfo>();
            name = host.ShowCommand(new() { Commands = PowerShellSession.DescribeCommands(commands) });
            if (name is null) return;
        }
        var command = InvokeCommand.GetCommand(name, CommandTypes.All)
            ?? throw new CommandNotFoundException($"Command '{name}' was not found in this PowerShell tab.");
        var description = CommandForm.Describe(command);
        if (NoCommonParameter)
            description = description with
            {
                ParameterSets = description.ParameterSets.Select(set => set with
                {
                    Parameters = set.Parameters.Where(parameter => !parameter.IsCommon).ToArray()
                }).ToArray()
            };
        // Nested invocations stay on the pipeline thread; desktop queries would wait on its gate.
        var help = InvokeCommand.InvokeScript(false,
            ScriptBlock.Create("Get-Help -Name $args[0] -Full"), null, name);
        var helpText = InvokeCommand.InvokeScript(false,
            ScriptBlock.Create("$args[0] | Out-String -Width 100"), null, new object[] { help });
        var helpDocument = CommandHelpDocument.FromHelp(description.Name, help);
        var script = host.ShowCommand(new()
        {
            Command = description, PassThru = PassThru, Width = Width, Height = Height,
            HelpText = string.Join(Environment.NewLine, helpText.Select(entry => entry.ToString())),
            HelpDocument = helpDocument,
            HelpUri = PowerShellSession.FindHelpUri(help)
        });
        if (script is null) return;
        if (PassThru)
            WriteObject(script);
        else
            foreach (var entry in InvokeCommand.InvokeScript(false, ScriptBlock.Create(script), null))
                WriteObject(entry);
    }
}
