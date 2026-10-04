using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Iseberg.Core;

public sealed record PowerShellSnippet(string Title, string Description, string Author, string Code,
    int CaretOffset = -1, bool Indent = true, bool IsBuiltIn = false, string Compatibility = "")
{
    public override string ToString() => Title;

    public (string Text, int Caret) Expand(string indentation, string newLine)
    {
        var caret = CaretOffset < 0 ? Code.Length : CaretOffset;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(caret, Code.Length);
        var result = new StringBuilder();
        var expandedCaret = 0;
        var separator = newLine + (Indent ? indentation : "");
        for (var index = 0; index < Code.Length; index++)
        {
            if (index == caret) expandedCaret = result.Length;
            if (Code[index] is '\r' or '\n')
            {
                result.Append(separator);
                if (Code[index] == '\r' && index + 1 < Code.Length && Code[index + 1] == '\n')
                {
                    if (caret == index + 1) expandedCaret = result.Length;
                    index++;
                }
            }
            else result.Append(Code[index]);
        }
        if (caret == Code.Length) expandedCaret = result.Length;
        return (result.ToString(), expandedCaret);
    }
}

public sealed record SnippetLoadResult(IReadOnlyList<PowerShellSnippet> Snippets, IReadOnlyList<string> Errors);

public static class SnippetCatalog
{
    private static readonly XNamespace Namespace = "http://schemas.microsoft.com/PowerShell/Snippets";
    private const string Legacy = "Requires Windows PowerShell 5.1; workflow and classic DSC syntax are not supported by PowerShell 7.";
    public static string UserDirectory => Path.Combine(Path.GetDirectoryName(UserSettings.SettingsPath)!, "Snippets");
    public static IReadOnlyList<PowerShellSnippet> BuiltIns { get; } = Array.AsReadOnly(new[]
    {
        Template("if", "Conditional execution.", "if ($condition) {\n    <caret>\n}"),
        Template("if-else", "Conditional execution with an alternative.", "if ($condition) {\n    <caret>\n}\nelse {\n    \n}"),
        Template("for", "Counter-controlled loop.", "for ($index = 0; $index -lt $count; $index++) {\n    <caret>\n}"),
        Template("foreach", "Iterate over a collection.", "foreach ($item in $collection) {\n    <caret>\n}"),
        Template("function", "Function with two parameters.", "function Verb-Noun {\n    param($First, $Second)\n\n    <caret>\n}"),
        Template("Cmdlet (advanced function)", "Pipeline-aware advanced function.", """
            function Verb-Noun {
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory, ValueFromPipeline)]
                    [object] $InputObject
                )
                process {
                    <caret>
                }
            }
            """),
        Template("Cmdlet (advanced function) - complete", "Advanced function with help, validation, lifecycle blocks and ShouldProcess.", """
            function Verb-Noun {
                <#
                .SYNOPSIS
                    Describe the command.
                .DESCRIPTION
                    Describe its behavior and requirements.
                .PARAMETER InputObject
                    The value to process.
                .EXAMPLE
                    'example' | Verb-Noun -WhatIf
                .INPUTS
                    System.String
                .OUTPUTS
                    System.Object
                .NOTES
                    Add author and version information.
                .LINK
                    Get-Help
                #>
                [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
                [OutputType([object])]
                param(
                    [Parameter(Mandatory, ValueFromPipeline, ValueFromPipelineByPropertyName)]
                    [ValidateNotNullOrEmpty()]
                    [string[]] $InputObject
                )
                begin {
                }
                process {
                    foreach ($value in $InputObject) {
                        if ($PSCmdlet.ShouldProcess($value, 'Process')) {
                            <caret>
                        }
                    }
                }
                end {
                }
            }
            """),
        Template("switch", "Dispatch by value.", "switch ($value) {\n    'First' { <caret>; break }\n    'Second' { ; break }\n    default { }\n}"),
        Template("while", "Test before each iteration.", "while ($condition) {\n    <caret>\n}"),
        Template("do-while", "Execute once, then continue while true.", "do {\n    <caret>\n} while ($condition)"),
        Template("do-until", "Execute once, then continue until true.", "do {\n    <caret>\n} until ($condition)"),
        Template("try-catch-finally", "Handle terminating errors and always clean up.", "try {\n    <caret>\n}\ncatch {\n    Write-Error -ErrorRecord $_\n}\nfinally {\n    \n}"),
        Template("try-finally", "Always clean up after an operation.", "try {\n    <caret>\n}\nfinally {\n    \n}"),
        Template("Comment block", "Multiline comment.", "<#\n<caret>\n#>"),
        Template("Workflow InlineScript", "Run PowerShell inside a workflow.", "InlineScript {\n    <caret>\n}", Legacy),
        Template("Workflow Parallel", "Run workflow activities in parallel.", "parallel {\n    <caret>\n}", Legacy),
        Template("Workflow Sequence", "Run workflow activities in sequence.", "sequence {\n    <caret>\n}", Legacy),
        Template("Workflow ForEachParallel", "Process workflow items in parallel.", "foreach -parallel ($item in $collection) {\n    <caret>\n}", Legacy),
        Template("Workflow (simple)", "Windows PowerShell workflow.", "workflow Invoke-Workflow {\n    param([string] $Name)\n\n    <caret>\n}", Legacy),
        Template("Workflow (advanced)", "Parameterized workflow with parallel and inline activities.", """
            workflow Invoke-Workflow {
                param(
                    [Parameter(Mandatory)]
                    [string[]] $ComputerName
                )
                foreach -parallel ($computer in $ComputerName) {
                    sequence {
                        InlineScript {
                            $target = $using:computer
                            <caret>
                        }
                    }
                }
            }
            """, Legacy),
        Template("DSC Resource Provider (simple)", "Script-based DSC resource functions.", """
            function Get-TargetResource {
                [CmdletBinding()]
                [OutputType([hashtable])]
                param([Parameter(Mandatory)][string] $Name)
                <caret>
                return @{ Name = $Name }
            }
            function Test-TargetResource {
                [CmdletBinding()]
                [OutputType([bool])]
                param([Parameter(Mandatory)][string] $Name)
                return $false
            }
            function Set-TargetResource {
                [CmdletBinding()]
                param([Parameter(Mandatory)][string] $Name)
            }
            """, Legacy),
        Template("DSC Configuration (simple)", "Classic DSC node configuration.", "configuration Workstation {\n    param([string[]] $ComputerName = 'localhost')\n    Import-DscResource -ModuleName PSDesiredStateConfiguration\n    Node $ComputerName {\n        <caret>\n    }\n}", Legacy),
        Template("DSC Configuration (using ConfigurationData)", "Use AllNodes in a classic DSC configuration.", "configuration Workstation {\n    Import-DscResource -ModuleName PSDesiredStateConfiguration\n    Node $AllNodes.NodeName {\n        <caret>\n    }\n}\nWorkstation -ConfigurationData $ConfigurationData", Legacy),
        Template("DSC ConfigurationData", "Separate node data from configuration.", "$ConfigurationData = @{\n    AllNodes = @(\n        @{\n            NodeName = 'localhost'\n            <caret>\n        }\n    )\n}", Legacy),
        Template("DSC Resource with Class (simple)", "Class-based DSC resource contract.", """
            [DscResource()]
            class ManagedItem {
                [DscProperty(Key)]
                [string] $Name

                [DscProperty(Mandatory)]
                [string] $Value

                [void] Set() {
                    <caret>
                }
                [bool] Test() {
                    return $false
                }
                [ManagedItem] Get() {
                    return $this
                }
            }
            """, Legacy),
        Template("class (simple)", "PowerShell class with a constructor and method.", "class Item {\n    [string] $Name\n\n    Item([string] $name) {\n        $this.Name = $name\n    }\n\n    [string] Describe() {\n        <caret>\n        return $this.Name\n    }\n}")
    });

    private static PowerShellSnippet Template(string title, string description, string code, string compatibility = "")
    {
        var offset = code.IndexOf("<caret>", StringComparison.Ordinal);
        return new(title, description, "Iseberg", code.Replace("<caret>", "", StringComparison.Ordinal), offset, IsBuiltIn: true, Compatibility: compatibility);
    }

    public static IReadOnlyList<PowerShellSnippet> Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (document.Root?.Name != Namespace + "Snippets") throw new InvalidDataException("Expected a PowerShell Snippets document.");
        var snippets = new List<PowerShellSnippet>();
        foreach (var element in document.Root.Elements(Namespace + "Snippet"))
        {
            if (!Version.TryParse(element.Attribute("Version")?.Value, out var version) || version > new Version(1, 0, 0))
                throw new InvalidDataException("Only snippet schema version 1.0.0 is supported.");
            var header = element.Element(Namespace + "Header") ?? throw new InvalidDataException("A snippet is missing its Header.");
            string Required(string name) => header.Element(Namespace + name)?.Value ??
                throw new InvalidDataException($"A snippet is missing {name}.");
            var title = Required("Title");
            if (string.IsNullOrWhiteSpace(title)) throw new InvalidDataException("A snippet title cannot be empty.");
            var script = element.Element(Namespace + "Code")?.Elements(Namespace + "Script")
                .FirstOrDefault(s => string.Equals(s.Attribute("Language")?.Value, "PowerShell", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"Snippet '{title}' is missing its PowerShell Script.");
            var offset = -1;
            if (script.Attribute("CaretOffset") is { } caret &&
                (!int.TryParse(caret.Value, out offset) || offset < 0 || offset > script.Value.Length))
                throw new InvalidDataException($"Snippet '{title}' has an invalid CaretOffset.");
            var indent = true;
            if (script.Attribute("Indent") is { } indentation && !bool.TryParse(indentation.Value, out indent))
                throw new InvalidDataException($"Snippet '{title}' has an invalid Indent value.");
            snippets.Add(new(title, Required("Description"), Required("Author"), script.Value, offset, indent));
        }
        if (snippets.Count == 0) throw new InvalidDataException("The file contains no snippets.");
        return snippets;
    }

    public static string Serialize(IEnumerable<PowerShellSnippet> snippets)
    {
        var entries = snippets.Select(s =>
        {
            if (string.IsNullOrWhiteSpace(s.Title) || s.CaretOffset < -1 || s.CaretOffset > s.Code.Length)
                throw new InvalidDataException("Invalid snippet title or caret offset.");
            var script = new XElement(Namespace + "Script", new XAttribute("Language", "PowerShell"),
                new XAttribute("Indent", s.Indent), s.Code);
            if (s.CaretOffset >= 0) script.Add(new XAttribute("CaretOffset", s.CaretOffset));
            return new XElement(Namespace + "Snippet", new XAttribute("Version", "1.0.0"),
                new XElement(Namespace + "Header", new XElement(Namespace + "Title", s.Title),
                    new XElement(Namespace + "Description", s.Description), new XElement(Namespace + "Author", s.Author)),
                new XElement(Namespace + "Code", script));
        });
        var xml = new StringBuilder();
        using (var writer = XmlWriter.Create(xml, new XmlWriterSettings
        {
            Indent = true, OmitXmlDeclaration = true, NewLineHandling = NewLineHandling.Entitize
        }))
            new XDocument(new XElement(Namespace + "Snippets", entries)).Save(writer);
        return xml.ToString();
    }

    public static async Task SaveAsync(string path, IEnumerable<PowerShellSnippet> snippets)
    {
        var xml = Serialize(snippets);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, xml, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<SnippetLoadResult> LoadAsync(IEnumerable<string>? directories = null)
    {
        directories ??= OperatingSystem.IsWindows()
            ? new[] { UserDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WindowsPowerShell", "Snippets") }
            : new[] { UserDirectory };
        var snippets = new List<PowerShellSnippet>();
        var errors = new List<string>();
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                             .Where(p => p.EndsWith(".snippets.ps1xml", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
                {
                    try { snippets.AddRange(Parse(await File.ReadAllTextAsync(path))); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
                    { errors.Add(path + ": " + exception.Message); }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { errors.Add(directory + ": " + exception.Message); }
        }
        return new(snippets.Distinct().ToArray(), errors);
    }
}
