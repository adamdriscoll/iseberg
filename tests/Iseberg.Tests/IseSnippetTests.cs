using System.Collections.Concurrent;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class IseSnippetTests
{
    [Fact]
    public async Task CmdletsCreateFileInfoOutputAndImportOnlyIntoTheirOwnSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-snippet-" + Guid.NewGuid().ToString("N"));
        var user = Path.Combine(directory, "user");
        var external = Path.Combine(directory, "external");
        Directory.CreateDirectory(external);
        await using var first = new PowerShellSession(user);
        await using var second = new PowerShellSession(user);
        var output = new ConcurrentQueue<OutputEntry>();
        first.Output += output.Enqueue;
        try
        {
            await first.InitializeAsync();
            await second.InitializeAsync();
            await first.ExecuteAsync("""
                New-IseSnippet -Title Example -Description 'description & metadata' -Text 'Get-Process' -Author Author -CaretOffset 3
                $file = Get-IseSnippet
                "type=$($file.GetType().FullName)"
                "file=$($file.Name)"
                New-IseSnippet -Title Skipped -Description d -Text t -WhatIf
                """);
            Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
            var text = string.Concat(output.Select(entry => entry.Text));
            Assert.Contains("type=System.IO.FileInfo", text);
            Assert.Contains("file=Example.snippets.ps1xml", text);
            Assert.Single(first.Snippets.GetUserFiles());
            var created = SnippetCatalog.Parse(await File.ReadAllTextAsync(first.Snippets.GetUserFiles().Single().FullName)).Single();
            Assert.Equal("description & metadata", created.Description);
            Assert.Equal(3, created.CaretOffset);
            Assert.False(created.Indent);
            await SnippetCatalog.SaveAsync(Path.Combine(external, "import.snippets.ps1xml"), [new("Imported", "external", "a", "never execute")]);
            await SnippetCatalog.SaveAsync(Path.Combine(external, "nested", "nested.snippets.ps1xml"), [new("Nested", "nested", "a", "text")]);
            await first.ExecuteAsync($"Import-IseSnippet -Path '{external.Replace("'", "''")}'");
            Assert.Contains((await first.Snippets.LoadAsync()).Snippets, snippet => snippet.Title == "Imported");
            Assert.DoesNotContain((await first.Snippets.LoadAsync()).Snippets, snippet => snippet.Title == "Nested");
            Assert.DoesNotContain((await second.Snippets.LoadAsync()).Snippets, snippet => snippet.Title == "Imported");
            Assert.Single(first.Snippets.GetUserFiles());
            await first.ExecuteAsync($"Import-IseSnippet -Path '{external.Replace("'", "''")}' -Recurse");
            Assert.Contains((await first.Snippets.LoadAsync()).Snippets, snippet => snippet.Title == "Nested");
            Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ModuleImportsDiscoverSnippetsWithoutExecutingTheModule()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-module-snippet-" + Guid.NewGuid().ToString("N"));
        var modules = Path.Combine(directory, "modules");
        var module = Path.Combine(modules, "CompatibilitySnippetFixture");
        Directory.CreateDirectory(module);
        await File.WriteAllTextAsync(Path.Combine(module, "CompatibilitySnippetFixture.psm1"), "throw 'must never execute module code'");
        await SnippetCatalog.SaveAsync(Path.Combine(module, "Snippets", "module.snippets.ps1xml"),
            [new("Module snippet", "module description", "", "snippet code")]);
        await using var session = new PowerShellSession(Path.Combine(directory, "user"));
        var entries = new ConcurrentQueue<OutputEntry>();
        session.Output += entry => { if (entry.Kind != OutputKind.Command) entries.Enqueue(entry); };
        try
        {
            await session.InitializeAsync();
            await session.ExecuteAsync($$"""
                $previousPath = $env:PSModulePath
                try {
                    $env:PSModulePath = '{{modules.Replace("'", "''")}}' + [IO.Path]::PathSeparator + $previousPath
                    Import-IseSnippet -Module CompatibilitySnippetFixture -ListAvailable
                    try { Import-IseSnippet -Module CompatibilitySnippetFixture -ErrorAction Stop }
                    catch { 'unloaded-module-rejected' }
                }
                finally { $env:PSModulePath = $previousPath }
                """);
            Assert.DoesNotContain(entries, entry => entry.Kind == OutputKind.Error);
            Assert.Contains("unloaded-module-rejected", string.Concat(entries.Select(entry => entry.Text)));
            Assert.Contains((await session.Snippets.LoadAsync()).Snippets, snippet => snippet.Title == "Module snippet");
            Assert.Empty(session.Snippets.GetUserFiles());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CreationAndImportsRejectOverwriteTraversalAndMalformedXml()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-snippet-" + Guid.NewGuid().ToString("N"));
        var service = new IseSnippetService(directory);
        try
        {
            service.Create("Example", "d", "old", "", 0, false);
            Assert.Throws<IOException>(() => service.Create("Example", "d", "new", "", 0, false));
            service.Create("Example", "d", "new", "", 0, true);
            Assert.Equal("new", (await service.LoadAsync()).Snippets.Single().Code);
            Assert.Throws<ArgumentException>(() => service.Create("../escape", "d", "t", "", 0, false));
            Assert.Throws<ArgumentException>(() => service.Create("..\\escape", "d", "t", "", 0, false));
            Assert.Throws<ArgumentOutOfRangeException>(() => service.Create("Caret", "d", "t", "", 2, false));
            var import = Path.Combine(directory, "import");
            await SnippetCatalog.SaveAsync(Path.Combine(import, "a.snippets.ps1xml"), [new("Partial", "d", "", "t")]);
            await File.WriteAllTextAsync(Path.Combine(import, "z.snippets.ps1xml"), "<broken");
            Assert.Throws<System.Xml.XmlException>(() => service.Import(import, false));
            File.Delete(Path.Combine(import, "a.snippets.ps1xml"));
            File.Delete(Path.Combine(import, "z.snippets.ps1xml"));
            Assert.DoesNotContain((await service.LoadAsync()).Snippets, snippet => snippet.Title == "Partial");
        }
        finally { Directory.Delete(directory, true); }
    }
}
