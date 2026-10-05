using System.Text;
using System.Text.Json;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class ScriptEncodingTests
{
    [Fact]
    public async Task LegacyBytesRequireExplicitChoiceAndRoundTripWithoutGuessing()
    {
        var path = TestPath();
        byte[] bytes = [0x23, 0x20, 0x63, 0x61, 0x66, 0xe9, 0x0d, 0x0a, 0x23, 0x0a];
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            await Assert.ThrowsAsync<DecoderFallbackException>(() => ScriptFile.OpenAsync(path));
            var choice = ScriptEncoding.Choices.Single(choice => choice.CodePage == 1252);
            var file = await ScriptFile.OpenAsync(path, choice);
            Assert.Equal("# caf\u00e9\r\n#\n", file.Text);
            Assert.Equal(choice, file.EncodingChoice);
            Assert.Equal(choice, file.SavedEncodingChoice);
            Assert.False(file.IsDirty);
            await file.SaveAsync(path);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(1200)]
    [InlineData(1201)]
    [InlineData(12000)]
    [InlineData(12001)]
    public async Task UnicodeWithoutBomUsesExplicitChoiceAndPreservesMixedLineEndings(int codePage)
    {
        var path = TestPath();
        var choice = ScriptEncoding.Choices.Single(choice => choice.CodePage == codePage && !choice.EmitBom);
        var text = "# \u03bb\r\n'line'\n'last'\r";
        var bytes = choice.CreateEncoding().GetBytes(text);
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            var file = await ScriptFile.OpenAsync(path, choice);
            Assert.Equal(text, file.Text);
            await file.SaveAsync(path);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EncodingOnlyChangesAreDirtyAndSaveWithTheSelectedBomAndEndian()
    {
        var path = TestPath();
        var file = new ScriptFile("Untitled.ps1") { Text = "# caf\u00e9\r\n" };
        var changes = new List<string?>();
        file.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        try
        {
            await file.SaveAsync(path);
            var original = file.EncodingChoice;
            var choice = ScriptEncoding.Choices.Single(choice => choice.CodePage == 12001 && choice.EmitBom);
            file.SetEncoding(choice);
            Assert.True(file.IsDirty);
            Assert.Equal(original, file.SavedEncodingChoice);
            Assert.EndsWith("*", file.Title);
            Assert.Contains(nameof(ScriptFile.EncodingChoice), changes);
            Assert.Contains(nameof(ScriptFile.EncodingName), changes);
            file.SetEncoding(original);
            Assert.False(file.IsDirty);
            file.SetEncoding(choice);
            await file.SaveAsync(path);
            Assert.False(file.IsDirty);
            Assert.Equal(choice, file.SavedEncodingChoice);
            Assert.Equal(choice.CreateEncoding().GetPreamble().Concat(choice.CreateEncoding().GetBytes(file.Text)),
                await File.ReadAllBytesAsync(path));
            Assert.Equal(choice, (await ScriptFile.OpenAsync(path)).EncodingChoice);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConversionRejectsUnrepresentableCharactersWithoutChangingTheChoice()
    {
        var file = new ScriptFile("Untitled.ps1") { Text = "'\u03bb'" };
        var original = file.EncodingChoice;
        Assert.Throws<EncoderFallbackException>(() =>
            file.SetEncoding(ScriptEncoding.Choices.Single(choice => choice.CodePage == 1252)));
        Assert.Equal(original, file.EncodingChoice);
        Assert.Equal("'\u03bb'", file.Text);
        Assert.True(file.IsDirty);
        Assert.Throws<ArgumentException>(() => new ScriptEncoding(1252, true, "Invalid BOM").CreateEncoding());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScriptEncoding(0, false).CreateEncoding());
    }

    [Fact]
    public void EncodingChoicesSerializeWithTheirCodePageBomAndLabel()
    {
        foreach (var choice in ScriptEncoding.Choices)
        {
            var json = JsonSerializer.Serialize(choice);
            Assert.DoesNotContain(nameof(ScriptEncoding.DisplayName), json);
            Assert.Equal(choice, JsonSerializer.Deserialize<ScriptEncoding>(json));
        }
        Assert.Equal("UTF-8", new ScriptEncoding(65001, false).DisplayName);
        Assert.Equal(new ScriptEncoding(12001, true), JsonSerializer.Deserialize<ScriptEncoding>(
            """{"CodePage":12001,"EmitBom":true,"DisplayName":"stale persisted label"}"""));
    }

    [Fact]
    public async Task LocalSaveAcknowledgesItsTextAndEncodingSnapshotOnly()
    {
        var path = TestPath();
        var snapshot = "# " + new string('a', 256 * 1024) + "\r\n";
        var file = new ScriptFile("Untitled.ps1") { Text = snapshot };
        var snapshotChoice = file.EncodingChoice;
        try
        {
            var save = file.SaveAsync(path);
            file.SetEncoding(ScriptEncoding.Choices.Single(choice => choice.CodePage == 65001 && choice.EmitBom));
            file.Text = "'newer'";
            await save;
            Assert.Equal(snapshotChoice.CreateEncoding().GetBytes(snapshot), await File.ReadAllBytesAsync(path));
            Assert.True(file.IsDirty);
            file.Text = snapshot;
            Assert.True(file.IsDirty);
            file.SetEncoding(snapshotChoice);
            Assert.False(file.IsDirty);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task StrictSaveFailureKeepsTheSavedTextEncodingAndVersion()
    {
        var path = TestPath();
        var file = new ScriptFile("Untitled.ps1") { Text = "# caf\u00e9" };
        try
        {
            await file.SaveAsync(path);
            var originalChoice = file.EncodingChoice;
            var version = file.SavedVersion;
            file.SetEncoding(ScriptEncoding.Choices.Single(choice => choice.CodePage == 1252));
            file.Text = "# \u03bb";
            await Assert.ThrowsAsync<EncoderFallbackException>(() => file.SaveAsync(path));
            Assert.Equal(version, file.SavedVersion);
            Assert.Equal(originalChoice, file.SavedEncodingChoice);
            Assert.Equal("# caf\u00e9", await File.ReadAllTextAsync(path));
            file.Text = "# caf\u00e9";
            Assert.True(file.IsDirty);
            file.SetEncoding(originalChoice);
            Assert.False(file.IsDirty);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FailedEncodingOnlySaveDoesNotAcknowledgeTheNewChoice()
    {
        var path = TestPath();
        var file = new ScriptFile("Untitled.ps1") { Text = "'saved'" };
        try
        {
            await file.SaveAsync(path);
            var original = file.EncodingChoice;
            var version = file.SavedVersion;
            file.SetEncoding(ScriptEncoding.Choices.Single(choice => choice.CodePage == 65001 && choice.EmitBom));
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                file.SaveAsync(Path.Combine(path, "missing.ps1")));
            Assert.True(file.IsDirty);
            Assert.Equal(path, file.Path);
            Assert.Equal(version, file.SavedVersion);
            Assert.Equal(original, file.SavedEncodingChoice);
            file.SetEncoding(original);
            Assert.False(file.IsDirty);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ByteFactoriesCreateCleanReplacementsWithoutModifyingDirtyDocuments()
    {
        var path = TestPath();
        var file = new ScriptFile("Untitled.ps1") { Text = "'unsaved'" };
        var choice = ScriptEncoding.Choices.Single(choice => choice.CodePage == 1252);
        byte[] bytes = [0x23, 0xe9];
        var replacement = ScriptFile.FromBytes(path, bytes, choice);
        Assert.Equal("#\u00e9", replacement.Text);
        Assert.False(replacement.IsDirty);
        Assert.Equal("'unsaved'", file.Text);
        Assert.True(file.IsDirty);
        var remote = ScriptFile.FromRemoteBytes("Z:\\remote-only.ps1", bytes, Guid.NewGuid(), "server", choice);
        Assert.Equal(replacement.Text, remote.Text);
        Assert.Equal(replacement.SavedVersion, remote.SavedVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(remote.HasExternalChangesAsync);
    }

    [Fact]
    public async Task DetachedDraftFactoryRetainsEncodingAndEmptyDraftsAreDirtyUntilSaved()
    {
        var path = TestPath();
        var empty = ScriptFile.FromRecovery("Recovered.ps1", "");
        var choice = ScriptEncoding.Choices.Single(choice => choice.CodePage == 1252);
        var unrepresentable = ScriptFile.FromRecovery("Recovered.ps1", "\u03bb", encoding: choice);
        Assert.True(unrepresentable.IsDirty);
        Assert.Null(unrepresentable.Path);
        Assert.Equal(choice, unrepresentable.EncodingChoice);
        Assert.Equal("\u03bb", unrepresentable.Text);
        try
        {
            await Assert.ThrowsAsync<EncoderFallbackException>(() => unrepresentable.SaveAsync(path));
            Assert.True(unrepresentable.IsDirty);
            Assert.Null(unrepresentable.SavedVersion);
            Assert.True(empty.IsDirty);
            Assert.Null(empty.Path);
            empty.Text = "'edited'";
            empty.Text = "";
            empty.SetEncoding(choice);
            empty.SetEncoding(ScriptEncoding.Choices[0]);
            Assert.True(empty.IsDirty);
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                empty.SaveAsync(Path.Combine(path, "missing.ps1")));
            Assert.True(empty.IsDirty);
            await empty.SaveAsync(path);
            Assert.False(empty.IsDirty);
            Assert.Empty(await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UntitledFactoryCreatesACleanBlankEncodingBaseline()
    {
        var choice = ScriptEncoding.Choices.Single(choice => choice.CodePage == 12001 && choice.EmitBom);
        var file = ScriptFile.CreateUntitled("Blank.ps1", encoding: choice);
        Assert.Equal("Blank.ps1", file.Name);
        Assert.Equal("", file.Text);
        Assert.Equal(choice, file.EncodingChoice);
        Assert.Null(file.Path);
        Assert.Null(file.SavedVersion);
        Assert.False(file.IsDirty);
        file.SetEncoding(ScriptEncoding.Choices[0]);
        Assert.True(file.IsDirty);
        Assert.Equal(choice, file.SavedEncodingChoice);
        file.SetEncoding(choice);
        Assert.False(file.IsDirty);
        Assert.False(ScriptFile.CreateUntitled("Default.ps1").IsDirty);
    }

    internal static string TestPath() =>
        Path.Combine(Directory.GetCurrentDirectory(), "iseberg-core-" + Guid.NewGuid().ToString("N") + ".ps1");
}
