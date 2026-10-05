using Xunit;

namespace Iseberg.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PowerShellPolicyCollection
{
    // Process-scoped execution policy is shared even by otherwise isolated runspaces.
    public const string Name = "PowerShell execution policy";
}
