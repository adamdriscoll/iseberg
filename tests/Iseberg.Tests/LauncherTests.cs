using System.Diagnostics;
using Xunit;

namespace Iseberg.Tests;

public sealed class LauncherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "iseberg launcher " + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WindowsLauncherStartsWithoutArguments()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunAsync("pwsh_ise", false);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("FIND", result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsLauncherResolvesSiblingExecutableAndPreservesRelativeQuotedPaths(bool powerShell)
    {
        if (!OperatingSystem.IsWindows()) return;
        var workingDirectory = Path.Combine(root, "working directory");
        Directory.CreateDirectory(workingDirectory);
        await File.WriteAllTextAsync(Path.Combine(workingDirectory, "first script & spaces.ps1"), "first script content");
        await File.WriteAllTextAsync(Path.Combine(workingDirectory, "second module.psm1"), "second script content");
        var command = powerShell
            ? "pwsh_ise 'script content' 'first script & spaces.ps1' 'second module.psm1'"
            : "pwsh_ise \"script content\" \"first script & spaces.ps1\" \"second module.psm1\"";
        var result = await RunAsync(command, powerShell);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("first script content", result.Output);
        Assert.Contains("second script content", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsLauncherPreservesExecutableExitCode(bool powerShell)
    {
        if (!OperatingSystem.IsWindows()) return;
        var workingDirectory = Path.Combine(root, "working directory");
        Directory.CreateDirectory(workingDirectory);
        await File.WriteAllTextAsync(Path.Combine(workingDirectory, "script.ps1"), "script content");
        var command = powerShell ? "pwsh_ise 'missing text' script.ps1" : "pwsh_ise \"missing text\" script.ps1";
        var result = await RunAsync(command, powerShell);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsLauncherReportsMissingExecutable(bool powerShell)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunAsync("pwsh_ise", powerShell, includeExecutable: false);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Iseberg.exe", result.Error);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string command, bool powerShell, bool includeExecutable = true)
    {
        var install = Path.Combine(root, "install & directory");
        var workingDirectory = Path.Combine(root, "working directory");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(workingDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "pwsh_ise.cmd"), Path.Combine(install, "pwsh_ise.cmd"));
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var commandInterpreter = Path.Combine(systemDirectory, "cmd.exe");
        // FIND exercises quoted file arguments and exit codes without opening the desktop.
        if (includeExecutable)
            File.Copy(Path.Combine(systemDirectory, "find.exe"), Path.Combine(install, "Iseberg.exe"));
        var start = new ProcessStartInfo(powerShell ? "pwsh" : commandInterpreter)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = workingDirectory
        };
        if (powerShell)
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(command + "; exit $LASTEXITCODE");
        }
        else
        {
            start.Arguments = $"/d /s /c \"{command}\"";
        }
        start.Environment["PATH"] = install + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the launcher test.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TestTimeouts.PowerShellStartup);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
