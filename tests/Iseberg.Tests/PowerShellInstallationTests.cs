using System.Runtime.InteropServices;
using Xunit;

namespace Iseberg.Tests;

public sealed class PowerShellInstallationTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "iseberg-powershell-" + Guid.NewGuid().ToString("N"));

    public PowerShellInstallationTests()
    {
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "System.Management.Automation.dll"), "");
        File.WriteAllText(Path.Combine(home, "pwsh.dll"), "");
    }

    [Theory]
    [InlineData("7.6.6", "10.0.1", "X64")]
    [InlineData("7.6.7", "10.0.12", "x64")]
    public void AcceptsCompatiblePatchVersions(string version, string runtime, string architecture) =>
        new PowerShellInstallation(home, version, runtime, architecture).Validate(new Version(10, 0, 3), Architecture.X64);

    [Theory]
    [InlineData("7.4.0")]
    [InlineData("7.6.5")]
    [InlineData("7.7.0")]
    [InlineData("7.6.7-preview.1")]
    [InlineData("")]
    public void RejectsUnsupportedPowerShell(string version)
    {
        var installation = new PowerShellInstallation(home, version, "10.0.3", "X64");
        Assert.Contains("7.6.x", Assert.Throws<InvalidOperationException>(() =>
            installation.Validate(new Version(10, 0, 3), Architecture.X64)).Message);
    }

    [Theory]
    [InlineData("9.0.3", "X64", ".NET")]
    [InlineData("11.0.0", "X64", ".NET")]
    [InlineData("", "X64", ".NET")]
    [InlineData("10.0.3", "Arm64", "architecture")]
    public void RejectsIncompatibleRuntimeAndArchitecture(string runtime, string architecture, string message)
    {
        var installation = new PowerShellInstallation(home, "7.6.6", runtime, architecture);
        Assert.Contains(message, Assert.Throws<InvalidOperationException>(() =>
            installation.Validate(new Version(10, 0, 3), Architecture.X64)).Message);
    }

    [Fact]
    public void RejectsIncompleteInstallation()
    {
        File.Delete(Path.Combine(home, "pwsh.dll"));
        var installation = new PowerShellInstallation(home, "7.6.6", "10.0.3", "X64");
        Assert.Contains("incomplete", Assert.Throws<InvalidOperationException>(() =>
            installation.Validate(new Version(10, 0, 3), Architecture.X64)).Message);
    }

    [Fact]
    public void RejectsRelativeHome()
    {
        var installation = new PowerShellInstallation("relative", "7.6.6", "10.0.3", "X64");
        Assert.Contains("incomplete", Assert.Throws<InvalidOperationException>(() =>
            installation.Validate(new Version(10, 0, 3), Architecture.X64)).Message);
    }

    [Fact]
    public void DiscoversInstalledPowerShellWithoutLoadingProfiles()
    {
        var installation = PowerShellInstallation.Find();
        Assert.True(File.Exists(Path.Combine(installation.Home, "System.Management.Automation.dll")));
        installation.Validate(Environment.Version, RuntimeInformation.ProcessArchitecture);
    }

    public void Dispose() => Directory.Delete(home, recursive: true);
}
