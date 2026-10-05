using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class AnsiOutputTests
{
    [Fact]
    public void StylesPersistAcrossWritesAndResetToStreamDefaults()
    {
        var parser = new AnsiOutputParser();
        Assert.Empty(parser.Parse(new("\x1b[3")));
        var red = Assert.Single(parser.Parse(new("1;44;1;4mred", OutputKind.Error)));
        Assert.Equal("red", red.Text);
        Assert.Equal(OutputKind.Error, red.Kind);
        Assert.Equal(new("#800000", "#000080", true, true), red.Style);
        Assert.Equal(red.Style, Assert.Single(parser.Parse(new("still-red"))).Style);
        Assert.Null(Assert.Single(parser.Parse(new("\x1b[0mplain"))).Style);
    }

    [Theory]
    [InlineData("\x1b[38;5;196m", "#FF0000")]
    [InlineData("\x1b[38;5;244m", "#808080")]
    [InlineData("\x1b[38;2;12;34;56m", "#0C2238")]
    [InlineData("\x1b[92m", "#00FF00")]
    public void SupportsIndexedAndTrueColor(string prefix, string color) =>
        Assert.Equal(color, Assert.Single(new AnsiOutputParser().Parse(new(prefix + "text"))).Style?.Foreground);

    [Fact]
    public void CursorEraseAndHyperlinksCannotRewriteTranscript()
    {
        var buffer = new ConsoleBuffer();
        buffer.ShowPrompt("PS> ");
        buffer.Input = "draft";
        buffer.Append(new("protected\x1b[2J\x1b[H\x1b]8;;https://example.test\x1b\\link\x1b]8;;\a"));
        Assert.Contains("protectedlink", buffer.Document.Text);
        Assert.Contains("Unsupported terminal control", buffer.Document.Text);
        Assert.DoesNotContain('\x1b', buffer.Document.Text);
        Assert.Equal("draft", buffer.Input);
        Assert.False(buffer.CanInsert(0));
        Assert.False(buffer.CanInsert(buffer.InputStart - 1));
    }

    [Fact]
    public void SpansDoNotMergeDifferentStylesAndPromptOffsetsUseVisibleText()
    {
        var buffer = new ConsoleBuffer();
        buffer.ShowPrompt("\x1b[32mPS>\x1b[0m ");
        buffer.Input = "draft";
        buffer.AppendBatch([new("a\x1b[31mb"), new("\x1b[0mc"), new("d", Style: new("#FFFFFF", "#FF0000"))]);
        Assert.Equal(4, buffer.Spans.Count);
        Assert.Equal("abcdPS> draft", buffer.Document.Text);
        Assert.Equal(8, buffer.InputStart);
        Assert.Equal("#008000", buffer.PromptParts[0].Style?.Foreground);
        Assert.Equal("draft", buffer.Input);
        buffer.Append(new("\x1b[32mPS>\x1b[0m 'code'\n", OutputKind.Command, 12));
        Assert.Equal("draft", buffer.Input);
        Assert.Equal(" 'code'\n", buffer.Document.GetText(buffer.Spans[^1].CodeStart, buffer.Spans[^1].End - buffer.Spans[^1].CodeStart));
        Assert.Equal("#008000", buffer.Spans[^1].PromptStyles![0].Style?.Foreground);
        buffer.Clear();
        buffer.Append(new("default"));
        Assert.Null(buffer.Spans[0].Style);
    }

    [Fact]
    public void IncompleteSequencesAreReportedAndCannotConsumeLaterOutput()
    {
        var buffer = new ConsoleBuffer();
        buffer.Append(new("before\x1b[38;"));
        buffer.CompleteOutput();
        buffer.Append(new("after"));
        Assert.Contains("before", buffer.Document.Text);
        Assert.Contains("after", buffer.Document.Text);
        Assert.Contains("Unsupported terminal control", buffer.Document.Text);
        Assert.DoesNotContain('\x1b', buffer.Document.Text);
    }

    [Fact]
    public void TrimmingPreservesStyleAndCurrentInput()
    {
        var buffer = new ConsoleBuffer();
        buffer.ShowPrompt("PS> ");
        buffer.Input = "draft";
        buffer.Append(new("\x1b[31m" + new string('x', 510_000)));
        buffer.Append(new("tail"));
        Assert.Equal("#800000", buffer.Spans[^1].Style?.Foreground);
        Assert.Equal("draft", buffer.Input);
        Assert.InRange(buffer.TranscriptEnd, 1, 500_000);
        Assert.Equal(buffer.TranscriptEnd + 4, buffer.InputStart);
    }
}
