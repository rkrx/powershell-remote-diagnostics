using WindowsPowerShellRc;

namespace WindowsPowerShellRc.Tests;

public class ScriptDisplayTests
{
    [Fact]
    public void CompactBlocksAndPipelinesAreIndentedAndExplained()
    {
        string script = "$items = 1..3; foreach ($item in $items) { $item | Write-Output }";
        string display = ScriptDisplay.Format(script, "Inspect each item", 80);
        Assert.StartsWith("# Inspect each item\n", display);
        Assert.Contains("\nforeach ($item in $items) {\n    $item |\n        Write-Output\n}", display);
    }

    [Fact]
    public void QuotedSyntaxAndExistingCommentsRemainIntact()
    {
        string script = "# Explain the literal\n$value = 'a; { b | c }'; $value";
        string display = ScriptDisplay.Format(script, "Do not duplicate this", 100);
        Assert.StartsWith("# Explain the literal\n", display);
        Assert.DoesNotContain("Do not duplicate", display);
        Assert.Contains("'a; { b | c }'", display);
    }

    [Fact]
    public void LongLinesWrapToWindowWidthWithoutTruncationOrControlInjection()
    {
        string display = ScriptDisplay.Format("Write-Output 'abcdefghijklmnopqrstuvwxyz0123456789'", "Read only", 24);
        Assert.All(display.Split('\n'), line => Assert.True(line.Length <= 24, line));
        Assert.Contains("↪", display);
        string unwrapped = display.Replace("\n    ↪ ", "");
        Assert.Contains("abcdefghijklmnopqrstuvwxyz0123456789", unwrapped);
        Assert.DoesNotContain('\u001b', ScriptDisplay.Format("'\u001b[31m'", "test", 80));
    }

    [Fact]
    public void MultilineLiteralsAreNotReformattedAsCode()
    {
        string script = "$text = @'\n{keep;this|literal}\n'@\n$text";
        Assert.Contains("@'\n{keep;this|literal}\n'@", ScriptDisplay.Format(script, "Inspect literal", 80));
    }

    [Fact]
    public void WideCharactersFitAndWindowsNewlinesAreDisplayedNormally()
    {
        string display = ScriptDisplay.Format("# Read text\r\nWrite-Output '界界界界界界'", "test", 14);
        Assert.All(display.Split('\n'), line => Assert.True(line.Sum(c => c == '界' ? 2 : 1) <= 14, line));
        Assert.DoesNotContain("\\u{D}", display);
    }

    [Fact]
    public void LiteralTabsAreVisibleInsteadOfSilentlyChangedToSpaces()
    {
        Assert.Contains("'a\\u{9}\\u{9} b'", ScriptDisplay.Format("Write-Output 'a\t\t b'", "Inspect literal", 80));
    }

    [Fact]
    public void InlineBlockCommentsDoNotSplitACommand()
    {
        Assert.Equal("Write-Output before <# explain #> after",
            ScriptDisplay.Format("Write-Output before <# explain #> after", "Inspect arguments", 80));
    }

    [Fact]
    public void MultilineArraysAndExistingPipelineBreaksKeepNestedIndentation()
    {
        string display = ScriptDisplay.Format("$items = @(\nGet-Item . |\nSelect-Object Name\n)", "Inspect item names", 80);
        Assert.Contains("@(\n    Get-Item . |\n        Select-Object Name\n)", display);
    }

    [Fact]
    public void SubexpressionsDoNotLoseTheEnclosingBlockIndentation()
    {
        string display = ScriptDisplay.Format("foreach ($i in 1..2) { $x = $(Get-Item .); Write-Output $x }", "Inspect items", 80);
        Assert.Contains(";\n    Write-Output $x\n}", display);
    }

    [Fact]
    public void MultipleStatementsGetAnExplanationRegardlessOfTheirSeparator()
    {
        Assert.StartsWith("# Inspect value\n", ScriptDisplay.Format("$value = 1\n$value", "Inspect value", 80));
    }
}
