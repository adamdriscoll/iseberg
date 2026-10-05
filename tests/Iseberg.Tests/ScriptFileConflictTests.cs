using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class ScriptFileConflictTests
{
    [Fact]
    public async Task ByteHashDetectsChangesWithTheSameLengthAndTimestamp()
    {
        var path = ScriptEncodingTests.TestPath();
        try
        {
            await File.WriteAllTextAsync(path, "'before'");
            var timestamp = File.GetLastWriteTimeUtc(path);
            var file = await ScriptFile.OpenAsync(path);
            Assert.Equal(await ScriptFile.ReadVersionAsync(path), file.SavedVersion);
            Assert.False(await file.HasExternalChangesAsync());
            await File.WriteAllTextAsync(path, "'after!'");
            File.SetLastWriteTimeUtc(path, timestamp);
            Assert.True(await file.HasExternalChangesAsync());
            file.Text = "'mine'";
            var exception = await Assert.ThrowsAsync<FileConflictException>(() => file.SaveAsync(path));
            Assert.Equal(file.SavedVersion, exception.ExpectedVersion);
            Assert.Equal(await ScriptFile.ReadVersionAsync(path), exception.ActualVersion);
            Assert.Equal("'after!'", await File.ReadAllTextAsync(path));
            Assert.True(file.IsDirty);
            Assert.Empty(Directory.GetFiles(Directory.GetCurrentDirectory(), Path.GetFileName(path) + ".*.tmp"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeletedOriginalRequiresExplicitConsentToRecreate()
    {
        var path = ScriptEncodingTests.TestPath();
        try
        {
            await File.WriteAllTextAsync(path, "'original'");
            var file = await ScriptFile.OpenAsync(path);
            File.Delete(path);
            Assert.Null(await ScriptFile.ReadVersionAsync(path));
            Assert.True(await file.HasExternalChangesAsync());
            file.Text = "'mine'";
            await Assert.ThrowsAsync<FileConflictException>(() => file.SaveAsync(path));
            Assert.False(File.Exists(path));
            Assert.True(file.IsDirty);
            await file.SaveAsync(path, expectedVersion: null);
            Assert.Equal("'mine'", await File.ReadAllTextAsync(path));
            Assert.False(file.IsDirty);
            Assert.False(await file.HasExternalChangesAsync());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SaveAsAndUntitledDocumentsDoNotOverwriteExistingDestinationsByDefault()
    {
        var original = ScriptEncodingTests.TestPath();
        var destination = ScriptEncodingTests.TestPath();
        try
        {
            await File.WriteAllTextAsync(original, "'original'");
            await File.WriteAllTextAsync(destination, "'destination'");
            var file = await ScriptFile.OpenAsync(original);
            var savedVersion = file.SavedVersion;
            file.Text = "'mine'";
            await Assert.ThrowsAsync<FileConflictException>(() => file.SaveAsync(destination));
            Assert.Equal(original, file.Path);
            Assert.Equal(savedVersion, file.SavedVersion);
            Assert.True(file.IsDirty);
            var untitled = new ScriptFile("Untitled.ps1") { Text = "'untitled'" };
            await Assert.ThrowsAsync<FileConflictException>(() => untitled.SaveAsync(destination));
            Assert.Null(untitled.Path);
            Assert.True(untitled.IsDirty);
            Assert.Equal("'destination'", await File.ReadAllTextAsync(destination));
            await file.SaveAsync(destination, await ScriptFile.ReadVersionAsync(destination));
            Assert.Equal(destination, file.Path);
            Assert.False(file.IsDirty);
            Assert.Equal("'mine'", await File.ReadAllTextAsync(destination));
            Assert.Equal("'original'", await File.ReadAllTextAsync(original));
        }
        finally { File.Delete(original); File.Delete(destination); }
    }

    [Fact]
    public async Task ConsentIsInvalidatedByAnotherEditDeletionOrCreation()
    {
        var path = ScriptEncodingTests.TestPath();
        try
        {
            await File.WriteAllTextAsync(path, "'original'");
            var file = await ScriptFile.OpenAsync(path);
            file.Text = "'mine'";
            await File.WriteAllTextAsync(path, "'external'");
            var consent = await ScriptFile.ReadVersionAsync(path);
            await File.WriteAllTextAsync(path, "'newer'");
            await Assert.ThrowsAsync<FileConflictException>(() => file.SaveAsync(path, consent));
            Assert.Equal("'newer'", await File.ReadAllTextAsync(path));
            File.Delete(path);
            await Assert.ThrowsAsync<FileConflictException>(() => file.SaveAsync(path, consent));
            await File.WriteAllTextAsync(path, "'created'");
            await Assert.ThrowsAsync<FileConflictException>(() => file.SaveAsync(path, expectedVersion: null));
            Assert.True(file.IsDirty);
            Assert.Equal("'created'", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SuccessfulSaveStoresTheVersionOfTheBytesActuallyWritten()
    {
        var path = ScriptEncodingTests.TestPath();
        var file = new ScriptFile("Untitled.ps1") { Text = "'first'" };
        try
        {
            await file.SaveAsync(path);
            var first = file.SavedVersion;
            file.Text = "'second'";
            await file.SaveAsync(path);
            Assert.NotEqual(first, file.SavedVersion);
            Assert.Equal(await ScriptFile.ReadVersionAsync(path), file.SavedVersion);
            Assert.False(file.IsDirty);
            Assert.False(await file.HasExternalChangesAsync());
            Assert.False(await new ScriptFile("Untitled.ps1").HasExternalChangesAsync());
            Assert.Null(await ScriptFile.ReadVersionAsync(Path.Combine(path, "missing.ps1")));
        }
        finally { File.Delete(path); }
    }
}
