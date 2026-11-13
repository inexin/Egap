using System.Text.RegularExpressions;

using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Which .NET engine a translated pattern runs on. A pattern on the engine without backtracking gives the same answer on any
/// machine; one on the backtracking engine can run into the time limit on a slow one.
/// </summary>
public sealed class PcreRegexTests
{
    /// <summary>Patterns whose parts read ahead: groups, conditions, escapes in braces, names, classes and replacements' kin.</summary>
    private static readonly string[] Tricky =
    [
        "(?(')a)", "(?(<)a)", "(?(R)a)", "(?()a)", "(?(<n>)a|b)", "(?('n')a)", "(?(1)a|b)", "(?(R1)a)", "(?(R&n)a)", "(?(DEFINE)(?<n>a))",
        "(?(?=a)a|b)", "(?<n>a)\\k<n>\\k'n'\\k{n}\\g{n}\\g{-1}\\g1\\g-1\\g<n>", "(?P<n>a)(?P=n)(?P>n)", "(?'n'a)(?&n)",
        "\\x{41}\\o{101}\\x41\\101\\0\\cA\\N\\p{Greek}\\P{^L}\\pL\\p{Xan}\\Q.*\\E", "[[:alpha:][:^digit:]\\d\\x{41}-\\x{5A}\\p{L}\\Q]\\E]",
        "[a-\\d]", "[\\w--\\d]", "[a&&b||c]", "a{1,2}b{,3}c{2}d{1,}?e*+", "(*UTF)(*UCP)(*CR)(*FAIL)(*ACCEPT)", "(?i:a)(?-i)(?^)(?#c)(?|a)",
        "(?x) a # c \n b", "(?>a)(?=a)(?!a)(?<=a)(?<!a)(*pla:a)(*nlb:a)", "\\K\\G\\b\\B\\A\\z\\Z\\R\\X\\h\\H\\v\\V\\C", "^$.|",
    ];

    [Fact]
    public void NoPrefixOfATrickyPatternThrows()
    {
        List<string> failures = [];
        foreach (string pattern in Tricky)
        {
            for (int length = 0; length <= pattern.Length; length++)
            {
                Translate(pattern[..length], failures);
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void NoShortPatternThrows()
    {
        const string Symbols = "()?<>'{}[]\\:^$|*+-&=!PRkgxopQEN1an#,";
        List<string> failures = [];
        foreach (char a in Symbols)
        {
            Translate(a.ToString(), failures);
            foreach (char b in Symbols)
            {
                Translate(string.Concat(a, b), failures);
                foreach (char c in Symbols)
                {
                    Translate(string.Concat(a, b, c), failures);
                }
            }
        }

        Assert.Empty(failures);
    }

    private static void Translate(string pattern, List<string> failures)
    {
        try
        {
            _ = PcreRegex.Translate(pattern, ignoreCase: false)?.For("abc").IsMatch("abc");
            _ = PcreRegex.Get(pattern, ignoreCase: true);
        }
        catch (RegexMatchTimeoutException)
        {
        }
        catch (Exception e)
        {
            failures.Add($"{pattern}: {e.GetType().Name}: {e.Message}");
        }
    }

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
