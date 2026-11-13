using System.Text.RegularExpressions;

using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Which .NET engine a translated pattern runs on. A pattern on the engine without backtracking gives the same answer on any
/// machine; one on the backtracking engine can run into the time limit on a slow one.
/// </summary>
public sealed class PcreRegexTests
{
    [Theory]
    [InlineData("(a|aa)+$")]
    [InlineData("^(a+)+$")]
    [InlineData(@"(a|aa)+\Z")]
    [InlineData("(?m)a+$")]
    [InlineData(@"\P{Greek}+")]
    [InlineData(@"[\P{Latin}x]+$")]
    public void PatternsRunWithoutBacktrackingOnTextWithoutCarriageReturns(string pattern)
    {
        PcreRegex regex = PcreRegex.Get(pattern, ignoreCase: false)!;

        Assert.True(regex.For(new string('a', 25) + "b").Options.HasFlag(RegexOptions.NonBacktracking));
    }

    [Fact]
    public void EndOfSubjectCountsACarriageReturnAsALineBreak()
    {
        PcreRegex regex = PcreRegex.Get("a$", ignoreCase: false)!;

        Assert.Matches(regex.For("a\r"), "a\r");
        Assert.Matches(regex.For("a\n"), "a\n");
        Assert.DoesNotMatch(regex.For("a\nb"), "a\nb");
    }

    [Theory]
    [InlineData("1", @"\P{Greek}", true)]
    [InlineData(" ", @"\P{Latin}", true)]
    [InlineData("!", @"\P{Han}", true)]
    [InlineData("Ω", @"\P{Greek}", false)]
    [InlineData("\U0001F600", @"^\P{Greek}$", true)]
    [InlineData("\U0001F600", @"^[\P{Greek}]$", true)]
    [InlineData("\U0001F600", @"^[^\P{Greek}]$", false)]
    public void NegatedScriptsTakeEverythingOutsideTheScript(string text, string pattern, bool expected)
    {
        PcreRegex regex = PcreRegex.Get(pattern, ignoreCase: false)!;

        Assert.Equal(expected, regex.For(text).IsMatch(text));
    }
}
