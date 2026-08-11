namespace StorageHub.Desktop.Tests;

public sealed class VtTerminalBufferTests
{
    [Fact]
    public void CarriageReturnAndCursorAddressingRewriteTheScreen()
    {
        var terminal = new VtTerminalBuffer(8, 3);

        terminal.Feed("hello\rX\u001b[2;3HZ");
        var lines = terminal.Snapshot().Text.Split('\n');

        Assert.StartsWith("Xello", lines[0], StringComparison.Ordinal);
        Assert.Equal('Z', lines[1][2]);
    }

    [Fact]
    public void SplitAnsiSequencesPreserveColorUntilReset()
    {
        var terminal = new VtTerminalBuffer(12, 2);

        terminal.Feed("\u001b[3");
        terminal.Feed("1mred\u001b[0m plain");
        var snapshot = terminal.Snapshot();
        var red = snapshot.Runs.Single(run => run.Start == 0 && run.Length == 3);
        var plain = snapshot.Runs.Single(run => run.Start <= 4 && run.Start + run.Length > 4);

        Assert.NotEqual(plain.Foreground, red.Foreground);
        Assert.Contains("red plain", snapshot.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EraseDisplayAndOscTitlesDoNotLeakControlText()
    {
        var terminal = new VtTerminalBuffer(10, 2);

        terminal.Feed("old\u001b]0;secret title\a\u001b[2Jnew");
        var snapshot = terminal.Snapshot();

        Assert.StartsWith("new", snapshot.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("old", snapshot.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", snapshot.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ResizeAndScrollingKeepBoundedVisibleOutput()
    {
        var terminal = new VtTerminalBuffer(4, 2);
        terminal.Feed("one\r\ntwo\r\nthree");

        terminal.Resize(6, 3);
        var snapshot = terminal.Snapshot();

        Assert.Contains("one", snapshot.Text, StringComparison.Ordinal);
        Assert.Contains("two", snapshot.Text, StringComparison.Ordinal);
        Assert.Contains("thre", snapshot.Text, StringComparison.Ordinal);
        Assert.Contains("e ", snapshot.Text, StringComparison.Ordinal);
        Assert.InRange(snapshot.CursorOffset, 0, snapshot.Text.Length);
    }

    [Fact]
    public void InsertDeleteAndEraseCharacterCommandsEditInPlace()
    {
        var terminal = new VtTerminalBuffer(10, 2);

        terminal.Feed("abcde\u001b[3G\u001b[2@\u001b[1P\u001b[2X");
        var firstLine = terminal.Snapshot().Text.Split('\n')[0];

        Assert.Equal("ab  de    ", firstLine);
    }
}
