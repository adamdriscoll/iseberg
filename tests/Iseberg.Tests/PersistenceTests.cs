using System.Diagnostics;
using System.Text.Json;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task WorkbenchRoundTripContainsConfigurationNotRuntimeOrDocumentText()
    {
        var path = TempPath();
        try
        {
            var state = new WorkbenchState
            {
                SelectedSession = 1,
                Sessions =
                [
                    new() { Name = "PowerShell 1", Documents = [new() { Name = "Untitled7.ps1", CaretOffset = 12 }] },
                    new()
                    {
                        Name = "PowerShell 3", ShowDebugger = true,
                        Documents = [new() { Name = "saved.ps1", Path = Path.GetFullPath("saved.ps1"), Encoding = new(1252, false) }]
                    }
                ]
            };
            var store = new WorkbenchStateStore(path);
            await store.SaveAsync(state);
            var loaded = await store.LoadAsync();
            Assert.NotNull(loaded);
            Assert.Equal(1, loaded.SelectedSession);
            Assert.Equal("PowerShell 3", loaded.Sessions[1].Name);
            Assert.Equal(1252, loaded.Sessions[1].Documents[0].Encoding.CodePage);
            Assert.True(loaded.Sessions[1].ShowDebugger);
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("\"Text\"", json);
            Assert.DoesNotContain("Runspace", json);
            Assert.DoesNotContain("DebugSnapshot", json);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ARunningInstanceIsNotRestoredAndMalformedStateIsReported()
    {
        var path = TempPath();
        try
        {
            using var process = Process.GetCurrentProcess();
            var store = new WorkbenchStateStore(path);
            await store.SaveAsync(new()
            {
                OwnerProcessId = process.Id, OwnerStartedUtc = process.StartTime.ToUniversalTime(),
                Sessions = [new() { Name = "PowerShell 1" }]
            });
            Assert.Null(await store.LoadAsync());
            await File.WriteAllTextAsync(path, """{"Version":99}""");
            await Assert.ThrowsAsync<InvalidDataException>(store.LoadAsync);
            await File.WriteAllTextAsync(path, """{"Sessions":[{"Name":"PowerShell 1"},{"Name":"PowerShell 1"}]}""");
            await Assert.ThrowsAsync<InvalidDataException>(store.LoadAsync);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task GeometryPersistsClampsAndSettingsCopiesAreIndependent()
    {
        var path = TempPath();
        try
        {
            var settings = new UserSettings
            {
                Geometry = new()
                {
                    WindowWidth = 1420, WindowHeight = 900, WindowX = -1200, WindowY = 60, Maximized = true,
                    TopScriptRatio = 0.7, RightScriptRatio = 0.4, DebuggerWidth = 420, CommandsWidth = 320
                }
            };
            await settings.SaveAsync(path);
            var loaded = await UserSettings.LoadAsync(path);
            Assert.Equal(JsonSerializer.Serialize(settings.Geometry), JsonSerializer.Serialize(loaded.Geometry));
            var copy = loaded.Copy();
            copy.Geometry.TopScriptRatio = double.NaN;
            copy.Geometry.CommandsWidth = -100;
            copy.Geometry.WindowWidth = double.PositiveInfinity;
            copy.Normalize();
            Assert.Equal(0.6, copy.Geometry.TopScriptRatio);
            Assert.Equal(150, copy.Geometry.CommandsWidth);
            Assert.Equal(1180, copy.Geometry.WindowWidth);
            Assert.Equal(0.7, loaded.Geometry.TopScriptRatio);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ThemeRoundTripIncludesFontsAndEveryColor()
    {
        var path = TempPath();
        try
        {
            var settings = new UserSettings { FontFamily = "Consolas", FontSize = 14 };
            settings.Theme.Name = "My theme";
            settings.Theme.Colors["Script.Background"] = "#112233";
            await ThemeFile.FromSettings(settings).SaveAsync(path);
            var loaded = await ThemeFile.LoadAsync(path);
            Assert.Equal(JsonSerializer.Serialize(settings.Theme), JsonSerializer.Serialize(loaded.Theme));
            Assert.Equal("Consolas", loaded.FontFamily);
            Assert.Equal(14, loaded.FontSize);
            loaded.Theme.Colors["Script.Background"] = "invalid";
            await Assert.ThrowsAsync<InvalidDataException>(() => loaded.SaveAsync(path));
            Assert.Equal("#112233", (await ThemeFile.LoadAsync(path)).Theme.Colors["Script.Background"]);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Version":1,"Theme":{},"FontFamily":"Consolas","FontSize":12}""")]
    [InlineData("""{"Version":99,"Theme":null,"FontFamily":"Consolas","FontSize":12}""")]
    public async Task InvalidThemeFilesDoNotBecomeDefaultThemes(string json)
    {
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(path, json);
            var error = await Record.ExceptionAsync(() => ThemeFile.LoadAsync(path));
            Assert.True(error is InvalidDataException or JsonException, error?.ToString() ?? "No error reported.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PrintingEscapesAllScriptAndTitleMarkupAndUsesNoExternalResources()
    {
        var html = ScriptPrint.CreateHtml("<title>&", "\n<script>alert('secret')</script>\r\n\t$x < 2", "Print", "Hint");
        Assert.Contains("&lt;title&gt;&amp;", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("https://", html);
        Assert.Contains("window.print()", html);
        Assert.Contains("@media print", html);
        Assert.Contains("<pre><code>\n", html);
        var path = TempPath();
        try
        {
            await ScriptPrint.WriteAsync(path, html);
            Assert.Equal(html, await File.ReadAllTextAsync(path));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { File.Delete(path); }
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "iseberg-persistence-" + Guid.NewGuid().ToString("N") + ".json");
}
