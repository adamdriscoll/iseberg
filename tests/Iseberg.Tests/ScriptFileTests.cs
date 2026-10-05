using System.Text;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class ScriptFileTests
{
    [Fact]
    public async Task TracksDirtyStateAndWritesUtf8WithoutBom()
    {
        var path = TempFile();
        try
        {
            var file = new ScriptFile("Untitled.ps1");
            Assert.False(file.IsDirty);
            file.Text = "Write-Output 'hello'\r\n";
            Assert.True(file.IsDirty);
            await file.SaveAsync(path);
            Assert.False(file.IsDirty);
            Assert.Equal(file.Text, await File.ReadAllTextAsync(path));
            Assert.Equal("UTF-8", file.EncodingName);
            file.Text += "# comment";
            Assert.EndsWith("*", file.Title);
            file.Text = "Write-Output 'hello'\r\n";
            Assert.False(file.IsDirty);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    public async Task RoundTripsEncodingAndLineEndings(string kind)
    {
        Encoding encoding = kind switch
        {
            "utf8-bom" => new UTF8Encoding(true),
            "utf16-le" => new UnicodeEncoding(false, true),
            "utf16-be" => new UnicodeEncoding(true, true),
            _ => new UTF32Encoding(false, true)
        };
        var path = TempFile();
        try
        {
            var text = "# caf\u00e9\r\nWrite-Output '\u03bb'\r\n";
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
            await File.WriteAllBytesAsync(path, bytes);
            var file = await ScriptFile.OpenAsync(path);
            Assert.Equal(text, file.Text);
            Assert.False(file.IsDirty);
            await file.SaveAsync(path);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FailedSaveDoesNotMarkDocumentAsSaved()
    {
        var file = new ScriptFile("Untitled.ps1") { Text = "unsaved" };
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => file.SaveAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "script.ps1")));
        Assert.True(file.IsDirty);
        Assert.Null(file.Path);
    }

    [Fact]
    public async Task ExistingFileKeepsItsUnixPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = TempFile();
        try
        {
            await File.WriteAllTextAsync(path, "'private'");
            var permissions = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, permissions);
            var file = await ScriptFile.OpenAsync(path);
            file.Text = "'still private'";
            await file.SaveAsync(path);
            Assert.Equal(permissions, File.GetUnixFileMode(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParserReportsIncompleteInputAndMultilineFoldRegions()
    {
        var analysis = EditorAnalysis.Analyze("function Test {\n    if ($true) {\n        'hello'\n    }\n}\n");
        Assert.Empty(analysis.Errors);
        Assert.Equal(2, analysis.Folds.Count);
        Assert.Contains(EditorAnalysis.Analyze("if ($true) {").Errors, e => e.IncompleteInput);
    }

    [Fact]
    public async Task SettingsPersistAndClampZoom()
    {
        var path = TempFile();
        try
        {
            var settings = new UserSettings { Zoom = 900, Layout = "Right", LoadProfiles = true, RecentFiles = ["test.ps1"] };
            await settings.SaveAsync(path);
            var loaded = await UserSettings.LoadAsync(path);
            Assert.Equal(400, loaded.Zoom);
            Assert.Equal("Right", loaded.Layout);
            Assert.True(loaded.LoadProfiles);
            Assert.Equal("test.ps1", loaded.RecentFiles.Single());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DebuggerConfigurationPersistsAllBreakpointFieldsAndIndependentSessions()
    {
        var path = TempFile();
        var line = new BreakpointSpec(BreakpointKind.Line, TempFile(), Line: 7, Condition: "$n -gt 2", Enabled: false);
        var variable = new BreakpointSpec(BreakpointKind.Variable, Target: "tracked",
            AccessMode: System.Management.Automation.VariableAccessMode.ReadWrite, Action: "Write-Host 'hit'; break");
        try
        {
            var settings = new UserSettings
            {
                DebuggerSessions =
                [
                    new() { Name = "PowerShell 1", Watches = ["$n", "$obj.Child"], Breakpoints = [line, variable] },
                    new() { Name = "PowerShell 2", Watches = ["$other"], Breakpoints = [new(BreakpointKind.Command, Target: "Get-Process")] }
                ]
            };
            await settings.SaveAsync(path);
            var loaded = await UserSettings.LoadAsync(path);
            Assert.Equal([line, variable], loaded.DebuggerSessions[0].Breakpoints);
            Assert.Equal(["$n", "$obj.Child"], loaded.DebuggerSessions[0].Watches);
            Assert.Equal("$other", loaded.DebuggerSessions[1].Watches.Single());
            var copy = loaded.Copy();
            copy.DebuggerSessions[0].Watches.Clear();
            Assert.Equal(2, loaded.DebuggerSessions[0].Watches.Count);
            loaded.DebuggerSessions[0].Breakpoints.Add(line with { Line = 0 });
            Assert.Throws<InvalidDataException>(loaded.Normalize);
        }
        finally { File.Delete(path); }
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "iseberg-file-" + Guid.NewGuid().ToString("N") + ".ps1");
}
