namespace Iseberg.Tests;

internal static class TestTimeouts
{
    // Cold PowerShell module discovery on CI is not part of the dialog/Stop response budget.
    public static readonly TimeSpan CommandDiscovery = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PowerShellStartup = TimeSpan.FromSeconds(60);
}
