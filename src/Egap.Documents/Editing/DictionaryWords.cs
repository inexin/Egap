using System.Text;
using System.Text.RegularExpressions;

using Egami.Fonts.Unicode;

namespace Egap.Documents;

/// <summary>
/// Whole-word matching for text written without spaces between words (Thai, Lao, Khmer, Myanmar): a regular expression's
/// <c>\b</c> never falls between two of their letters, so an edge of the search text that is such a letter (line breaking
/// class SA) must fall where Egami's word dictionaries end a word.
/// </summary>
internal static class DictionaryWords
{
    /// <summary>Whether the first or last character of <paramref name="text"/> is a complex-context (SA) letter.</summary>
    public static bool IsComplexContext(string text, bool first)
    {
        if (text.Length == 0)
        {
            return false;
        }

        int at = first ? 0 : text.Length - 1;
        if (!first && at > 0 && char.IsSurrogatePair(text[at - 1], text[at]))
        {
            at--;
        }

        return Rune.TryGetRuneAt(text, at, out Rune rune) && UnicodeData.GetLineBreakClass(rune.Value) == LineBreakClass.ComplexContext;
    }

    /// <summary>Whether a word, as the dictionaries segment <paramref name="text"/>, starts or ends at <paramref name="index"/>.</summary>
    public static bool IsBoundary(string text, int index)
    {
        if (index <= 0 || index >= text.Length)
        {
            return true;
        }

        foreach (TextSegment segment in TextSegmenter.Words(text, null))
        {
            if (segment.Start >= index)
            {
                return segment.Start == index;
            }
        }

        return false;
    }

    /// <summary>A whole-word pattern whose dictionary-checked edges <see cref="WholeWords"/> tests after each match.</summary>
    public sealed class WholeWordRegex(string pattern, RegexOptions options, TimeSpan timeout, bool checkStart, bool checkEnd)
        : Regex(pattern, options, timeout)
    {
        /// <summary>The matches in <paramref name="text"/> whose checked edges fall on word boundaries.</summary>
        public IEnumerable<Match> WholeWords(string text)
        {
            for (Match match = Match(text); match.Success;)
            {
                if ((!checkStart || IsBoundary(text, match.Index)) && (!checkEnd || IsBoundary(text, match.Index + match.Length)))
                {
                    yield return match;
                    match = match.NextMatch();
                }
                else if (match.Index < text.Length)
                {
                    match = Match(text, match.Index + 1);
                }
                else
                {
                    break;
                }
            }
        }
    }
}
