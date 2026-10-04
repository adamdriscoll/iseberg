using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class ScriptRecoveryTests
{
    [Fact]
    public void RemovingANeverCreatedSnapshotDoesNotRequireARecoveryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-recovery-" + Guid.NewGuid().ToString("N"));
        var recovery = new ScriptRecovery(directory);

        var exception = Record.Exception(() => recovery.Remove(Guid.NewGuid()));

        Assert.Null(exception);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task SavingAnUnmodifiedScriptDoesNotRequireARecoveryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-recovery-" + Guid.NewGuid().ToString("N"));
        var recovery = new ScriptRecovery(directory);

        await recovery.SaveAsync(Guid.NewGuid(), new ScriptFile("Untitled.ps1"));

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task RemovingAnExistingSnapshotIsIdempotent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-recovery-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var path = Path.Combine(directory, id.ToString("N") + ".json");
        var recovery = new ScriptRecovery(directory);
        try
        {
            await recovery.SaveAsync(id, new ScriptFile("Untitled.ps1") { Text = "'unsaved'" });
            Assert.True(File.Exists(path));

            recovery.Remove(id);
            recovery.Remove(id);

            Assert.False(File.Exists(path));
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                File.Delete(path);
                Directory.Delete(directory);
            }
        }
    }

    [Fact]
    public async Task RemovingASnapshotAfterItsDirectoryDisappearsIsHarmless()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-recovery-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var path = Path.Combine(directory, id.ToString("N") + ".json");
        var recovery = new ScriptRecovery(directory);
        try
        {
            await recovery.SaveAsync(id, new ScriptFile("Untitled.ps1") { Text = "'unsaved'" });
            File.Delete(path);
            Directory.Delete(directory);

            recovery.Remove(id);

            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                File.Delete(path);
                Directory.Delete(directory);
            }
        }
    }

    [Fact]
    public void RemovingASnapshotDoesNotSuppressOtherDeletionErrors()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-recovery-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var path = Path.Combine(directory, id.ToString("N") + ".json");
        Directory.CreateDirectory(path);
        var recovery = new ScriptRecovery(directory);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => recovery.Remove(id));
            Assert.True(Directory.Exists(path));
        }
        finally
        {
            Directory.Delete(path);
            Directory.Delete(directory);
        }
    }
}
