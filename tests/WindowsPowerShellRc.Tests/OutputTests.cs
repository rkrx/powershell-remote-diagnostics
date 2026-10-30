using System.Text;
using WindowsPowerShellRc;

namespace WindowsPowerShellRc.Tests;

public class OutputTests
{
    [Fact]
    public void LongUnicodeLineCanBeReconstructedExactly()
    {
        using var store = new OutputStore();
        var output = store.Create(Guid.NewGuid());
        var text = string.Concat(Enumerable.Repeat("😀", 5000)) + "needle";
        output.Append("output", text);
        var first = output.ReadPage();
        Assert.True(first.Lines[0].IsLongLine);
        Assert.True(first.Lines[0].Truncated);
        Assert.Equal(5006, first.Lines[0].TotalCharacters);
        var second = output.ReadPage(line: 1, charOffset: 4096);
        Assert.Equal(text, first.Lines[0].Text + second.Lines[0].Text);
        var match = Assert.Single(output.Search(new SearchOptions("needle", BeforeChars: 2)).Matches);
        Assert.Equal(5000, match.CharOffset);
        Assert.Equal("😀😀needle", match.Context);
    }

    [Fact]
    public void SearchPagesBindCursorAndPreserveLineContext()
    {
        using var store = new OutputStore();
        var output = store.Create(Guid.NewGuid());
        output.Append("output", "before\ncat cat\n\nafter cat");
        var options = new SearchOptions("cat", Limit: 1, BeforeLines: 1, AfterLines: 1);
        var first = output.Search(options);
        Assert.Equal(2, first.Matches[0].Line);
        Assert.Equal(3, first.Matches[0].Lines.Count);
        Assert.NotNull(first.NextCursor);
        var second = output.Search(options with { Cursor = first.NextCursor });
        Assert.Equal(4, second.Matches[0].CharOffset);
        Assert.Throws<ArgumentException>(() => output.Search(new SearchOptions("dog", Cursor: first.NextCursor)));
        Assert.Equal("", output.ReadPage(startLine: 3, lineCount: 1).Lines[0].Text);
    }

    [Fact]
    public void PageHasBoundedTextAndContinuation()
    {
        using var store = new OutputStore();
        var output = store.Create(Guid.NewGuid());
        for (int i = 0; i < 200; i++) output.Append("output", new string('x', 4096));
        var page = output.ReadPage(lineCount: 200);
        Assert.True(page.Lines.Sum(l => Encoding.UTF8.GetByteCount(l.Text)) <= 256 * 1024);
        Assert.NotNull(page.NextLine);
    }

    [Fact]
    public void SearchBudgetRetainsEveryRequestedContextLineWithRetrievalCoordinates()
    {
        using var store = new OutputStore();
        var output = store.Create(Guid.NewGuid());
        for (int i = 0; i < 41; i++) output.Append("output", string.Concat(Enumerable.Repeat("😀", 4096)) + (i == 20 ? "needle" : ""));
        var result = output.Search(new("needle", BeforeLines: 20, AfterLines: 20));
        var match = Assert.Single(result.Matches);
        Assert.Equal(41, match.Lines.Count);
        Assert.True(match.Lines.Sum(l => Encoding.UTF8.GetByteCount(l.Text)) + Encoding.UTF8.GetByteCount(match.Context) <= 256 * 1024);
        Assert.All(match.Lines.Where(l => l.Text.Length == 0), l => Assert.True(l.Truncated));
    }
}
