using System.Management.Automation;

namespace Iseberg.Core;

public abstract class IseSnippetCommand : PSCmdlet
{
    protected IseSnippetService Snippets =>
        Host.PrivateData?.BaseObject is WorkbenchHostServices services && !services.IsRunspacePushed()
            ? services.Snippets : throw new PSNotSupportedException("ISE snippet commands require a local Iseberg PowerShell tab.");

    protected string FileSystemPath(string path)
    {
        var result = SessionState.Path.GetUnresolvedProviderPathFromPSPath(path, out var provider, out _);
        if (provider.Name != "FileSystem") throw new PSNotSupportedException("ISE snippets require a FileSystem path.");
        return result;
    }
}

[Cmdlet(VerbsCommon.New, "IseSnippet", SupportsShouldProcess = true)]
public sealed class NewIseSnippetCommand : IseSnippetCommand
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string Title { get; set; } = "";
    [Parameter(Mandatory = true, Position = 1)]
    [ValidateNotNullOrEmpty]
    public string Description { get; set; } = "";
    [Parameter(Mandatory = true, Position = 2)]
    [ValidateNotNullOrEmpty]
    public string Text { get; set; } = "";
    [Parameter]
    public string Author { get; set; } = "";
    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int CaretOffset { get; set; }
    [Parameter]
    public SwitchParameter Force { get; set; }

    protected override void EndProcessing()
    {
        var snippets = Snippets;
        if (ShouldProcess(Title, "Create ISE snippet"))
            snippets.Create(Title, Description, Text, Author, CaretOffset, Force);
    }
}

[Cmdlet(VerbsCommon.Get, "IseSnippet")]
[OutputType(typeof(FileInfo))]
public sealed class GetIseSnippetCommand : IseSnippetCommand
{
    protected override void EndProcessing()
    {
        foreach (var file in Snippets.GetUserFiles()) WriteObject(file);
    }
}

[Cmdlet(VerbsData.Import, "IseSnippet", DefaultParameterSetName = "FromFolder")]
public sealed class ImportIseSnippetCommand : IseSnippetCommand
{
    [Parameter(Mandatory = true, Position = 0, ParameterSetName = "FromFolder")]
    [ValidateNotNullOrEmpty]
    public string Path { get; set; } = "";
    [Parameter(Mandatory = true, ParameterSetName = "FromModule")]
    [ValidateNotNullOrEmpty]
    public string Module { get; set; } = "";
    [Parameter(ParameterSetName = "FromModule")]
    public SwitchParameter ListAvailable { get; set; }
    [Parameter]
    public SwitchParameter Recurse { get; set; }

    protected override void EndProcessing()
    {
        var snippets = Snippets;
        if (ParameterSetName == "FromFolder")
        {
            snippets.Import(FileSystemPath(Path), Recurse);
            return;
        }
        var modules = InvokeCommand.InvokeScript(false, ScriptBlock.Create(
            "Get-Module -Name $args[0] -ListAvailable:($args[1]) -ErrorAction Stop"), null, Module, ListAvailable.IsPresent)
            .Select(value => value.BaseObject).OfType<PSModuleInfo>().ToArray();
        if (modules.Length == 0) throw new InvalidOperationException($"Module '{Module}' was not found. Use -ListAvailable for installed modules.");
        foreach (var module in modules)
        {
            var directory = System.IO.Path.Combine(module.ModuleBase, "Snippets");
            if (Directory.Exists(directory)) snippets.Import(directory, Recurse);
        }
    }
}
