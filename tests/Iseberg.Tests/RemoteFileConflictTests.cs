using System.Text;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class RemoteFileConflictTests
{
    [Fact]
    public async Task RemoteByteVersionsDetectExternalChangesAndRequireCurrentConsent()
    {
        await using var server = await RemotingTests.RemoteServer.StartAsync();
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ConnectAsync(server.Connection);
        var path = ScriptEncodingTests.TestPath();
        try
        {
            await File.WriteAllTextAsync(path, "'before'");
            var timestamp = File.GetLastWriteTimeUtc(path);
            var file = await session.OpenRemoteFileAsync(path);
            Assert.Equal(file.SavedVersion, await session.ReadRemoteFileVersionAsync(path));
            Assert.False(await session.HasRemoteFileChangesAsync(file));
            file.Text = "'mine'";
            await File.WriteAllTextAsync(path, "'after!'");
            File.SetLastWriteTimeUtc(path, timestamp);
            Assert.True(await session.HasRemoteFileChangesAsync(file));
            await Assert.ThrowsAsync<FileConflictException>(() => session.SaveRemoteFileAsync(file));
            var consent = await session.ReadRemoteFileVersionAsync(path);
            await File.WriteAllTextAsync(path, "'newer'");
            await Assert.ThrowsAsync<FileConflictException>(() => session.SaveRemoteFileAsync(file, path, consent));
            Assert.Equal("'newer'", await File.ReadAllTextAsync(path));
            Assert.True(file.IsDirty);
            await session.SaveRemoteFileAsync(file, path, await session.ReadRemoteFileVersionAsync(path));
            Assert.False(file.IsDirty);
            Assert.Equal("'mine'", await File.ReadAllTextAsync(path));
            File.Delete(path);
            Assert.True(await session.HasRemoteFileChangesAsync(file));
            Assert.Null(await session.ReadRemoteFileVersionAsync(path));
            file.Text = "'recreated'";
            await Assert.ThrowsAsync<FileConflictException>(() => session.SaveRemoteFileAsync(file));
            Assert.False(File.Exists(path));
            await session.SaveRemoteFileAsync(file, path, expectedVersion: null);
            Assert.Equal("'recreated'", await File.ReadAllTextAsync(path));
            Assert.False(file.IsDirty);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RemoteSaveAsChecksVersionsUsingTheServersProviderPath()
    {
        await using var server = await RemotingTests.RemoteServer.StartAsync();
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ConnectAsync(server.Connection);
        var original = ScriptEncodingTests.TestPath();
        var destination = ScriptEncodingTests.TestPath();
        var providerPath = "FileConflictTest:\\" + Path.GetFileName(destination);
        try
        {
            await session.ExecuteAsync("New-PSDrive -Name FileConflictTest -PSProvider FileSystem -Scope Global -Root '" +
                Directory.GetCurrentDirectory().Replace("'", "''") + "' | Out-Null");
            await File.WriteAllTextAsync(original, "'original'");
            await File.WriteAllTextAsync(destination, "'destination'");
            var file = await session.OpenRemoteFileAsync(original);
            file.Text = "'mine'";
            await Assert.ThrowsAsync<FileConflictException>(() => session.SaveRemoteFileAsync(file, providerPath));
            Assert.Equal(original, file.Path);
            Assert.True(file.IsDirty);
            await session.SaveRemoteFileAsync(file, providerPath,
                await session.ReadRemoteFileVersionAsync(providerPath));
            Assert.Equal(destination, file.Path);
            Assert.Equal("'mine'", await File.ReadAllTextAsync(destination));
            Assert.False(file.IsDirty);
            file.Text = "'next'";
            await session.SaveRemoteFileAsync(file, providerPath);
            Assert.Equal("'next'", await File.ReadAllTextAsync(destination));
            var untitled = new ScriptFile("Untitled.ps1") { Text = "'new'" };
            await Assert.ThrowsAsync<FileConflictException>(() => session.SaveRemoteFileAsync(untitled, providerPath));
            Assert.False(untitled.IsRemote);
            Assert.Null(untitled.Path);
            Assert.True(untitled.IsDirty);
        }
        finally { File.Delete(original); File.Delete(destination); }
    }

    [Fact]
    public async Task RemoteLegacyDecodingAndUnicodeConversionAreStrict()
    {
        await using var server = await RemotingTests.RemoteServer.StartAsync();
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ConnectAsync(server.Connection);
        var path = ScriptEncodingTests.TestPath();
        try
        {
            await File.WriteAllBytesAsync(path, [0x23, 0xe9, 0x0d, 0x0a]);
            await Assert.ThrowsAsync<DecoderFallbackException>(() => session.OpenRemoteFileAsync(path));
            var legacy = ScriptEncoding.Choices.Single(choice => choice.CodePage == 1252);
            var file = await session.OpenRemoteFileAsync(path, legacy);
            Assert.Equal("#\u00e9\r\n", file.Text);
            var version = file.SavedVersion;
            file.Text += "\u03bb";
            await Assert.ThrowsAsync<EncoderFallbackException>(() => session.SaveRemoteFileAsync(file));
            Assert.Equal(version, file.SavedVersion);
            Assert.Equal(legacy, file.SavedEncodingChoice);
            Assert.True(file.IsDirty);
            var unicode = ScriptEncoding.Choices.Single(choice => choice.CodePage == 12001 && choice.EmitBom);
            file.SetEncoding(unicode);
            await session.SaveRemoteFileAsync(file);
            Assert.False(file.IsDirty);
            Assert.Equal(unicode, file.SavedEncodingChoice);
            Assert.Equal(unicode.CreateEncoding().GetPreamble().Concat(unicode.CreateEncoding().GetBytes(file.Text)),
                await File.ReadAllBytesAsync(path));
            Assert.Equal(unicode, (await session.OpenRemoteFileAsync(path)).EncodingChoice);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RemoteSaveAcknowledgesOnlyTheTextAndEncodingCapturedBeforeWaiting()
    {
        await using var server = await RemotingTests.RemoteServer.StartAsync();
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ConnectAsync(server.Connection);
        var path = ScriptEncodingTests.TestPath();
        var file = new ScriptFile("Untitled.ps1") { Text = "'snapshot'\r\n" };
        var snapshotChoice = file.EncodingChoice;
        try
        {
            var execution = session.ExecuteAsync("Start-Sleep -Milliseconds 500");
            var save = session.SaveRemoteFileAsync(file, path);
            file.SetEncoding(ScriptEncoding.Choices.Single(choice => choice.CodePage == 1200 && choice.EmitBom));
            file.Text = "'newer'\r\n";
            await execution;
            await save;
            Assert.True(file.IsDirty);
            Assert.Equal(snapshotChoice, file.SavedEncodingChoice);
            Assert.Equal("'snapshot'\r\n", await File.ReadAllTextAsync(path));
            Assert.Equal(await ScriptFile.ReadVersionAsync(path), file.SavedVersion);
            file.Text = "'snapshot'\r\n";
            Assert.True(file.IsDirty);
            file.SetEncoding(snapshotChoice);
            Assert.False(file.IsDirty);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RemoteConsentIsCheckedAfterTheSaveWaitsForExecution()
    {
        await using var server = await RemotingTests.RemoteServer.StartAsync();
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ConnectAsync(server.Connection);
        var path = ScriptEncodingTests.TestPath();
        try
        {
            await File.WriteAllTextAsync(path, "'original'");
            var file = await session.OpenRemoteFileAsync(path);
            var consent = await session.ReadRemoteFileVersionAsync(path);
            file.SetEncoding(ScriptEncoding.Choices.Single(choice => choice.CodePage == 1201 && choice.EmitBom));
            var execution = session.ExecuteAsync("Start-Sleep -Milliseconds 500");
            var save = session.SaveRemoteFileAsync(file, path, consent);
            await File.WriteAllTextAsync(path, "'changed-while-waiting'");
            await execution;
            await Assert.ThrowsAsync<FileConflictException>(() => save);
            Assert.True(file.IsDirty);
            Assert.Equal(consent, file.SavedVersion);
            Assert.Equal("'changed-while-waiting'", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(Directory.GetCurrentDirectory(), Path.GetFileName(path) + ".*.tmp"));
        }
        finally { File.Delete(path); }
    }
}
