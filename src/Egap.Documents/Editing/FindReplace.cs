using System.Text;
using System.Text.RegularExpressions;

namespace Egap.Documents;

/// <summary>How <see cref="Node.Find(string, FindOptions?)"/> and <see cref="Node.Replace(string, string, FindOptions?)"/> match.</summary>
public sealed class FindOptions
{
    /// <summary>Upper and lower case must match.</summary>
    public bool MatchCase { get; init; }

    /// <summary>Only whole words match.</summary>
    public bool WholeWord { get; init; }

    /// <summary>Text with a tracked deletion is left out of matching, as Word's Find does by default with markup shown.</summary>
    public bool SkipDeletedText { get; init; } = true;
}

/// <summary>
/// A match <see cref="Node.Find(string, FindOptions?)"/> found: where it is in its paragraph's text as Find sees it (runs
/// joined, a tab as \t, a line break as \v, deleted text left out unless the options keep it) and the text it matched.
/// </summary>
/// <param name="Paragraph">The paragraph the match is in; matches never span paragraphs.</param>
/// <param name="Start">Where the match starts in the paragraph's text as Find sees it.</param>
/// <param name="Length">How many characters it covers.</param>
/// <param name="Value">The text it matched.</param>
public sealed record TextMatch(Paragraph Paragraph, int Start, int Length, string Value);

/// <summary>
/// Find and Replace over text that crosses run boundaries (Phase 6 design Q3): each paragraph's text is matched as one
/// string, with tabs and line breaks as their control characters; a replacement takes the formatting of the first run the
/// match touches, as Aspose.Words and GemBox.Document do. Matches never span paragraphs.
/// </summary>
internal static class FindReplace
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>The regular expression that finds <paramref name="find"/> as the options say.</summary>
    public static Regex RegexFor(string find, FindOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(find);
        string pattern = Regex.Escape(find);
        if (options.WholeWord)
        {
            pattern = @"\b" + pattern + @"\b";
        }

        return new Regex(pattern, (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, Timeout);
    }

    public static int Replace(Node scope, string find, string replacement, FindOptions? options)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        options ??= new FindOptions();
        return Replace(scope, RegexFor(find, options), _ => replacement, options);
    }

    /// <summary>Every match in the paragraphs under <paramref name="scope"/>, in document order.</summary>
    public static IReadOnlyList<TextMatch> Find(Node scope, Regex regex, FindOptions? options)
    {
        ArgumentNullException.ThrowIfNull(regex);
        options ??= new FindOptions();
        List<TextMatch> found = [];
        foreach (Paragraph paragraph in scope is Paragraph p ? [p] : scope.Descendants<Paragraph>())
        {
            (string text, _) = TextOf(paragraph, options);
            foreach (Match match in regex.Matches(text))
            {
                if (match.Length > 0)
                {
                    found.Add(new TextMatch(paragraph, match.Index, match.Length, match.Value));
                }
            }
        }

        return found;
    }

    public static int Replace(Node scope, Regex regex, Func<Match, string> replacement, FindOptions? options)
    {
        ArgumentNullException.ThrowIfNull(regex);
        ArgumentNullException.ThrowIfNull(replacement);
        options ??= new FindOptions();
        List<Paragraph> paragraphs = scope is Paragraph p ? [p] : [.. scope.Descendants<Paragraph>()];
        int count = 0;
        foreach (Paragraph paragraph in paragraphs)
        {
            count += ReplaceIn(paragraph, regex, replacement, options);
        }

        return count;
    }

    private static int ReplaceIn(Paragraph paragraph, Regex regex, Func<Match, string> replacement, FindOptions options)
    {
        (string text, List<(Inline Node, int Start)> pieces) = TextOf(paragraph, options);
        if (text.Length == 0)
        {
            return 0;
        }

        List<Match> matches = [.. regex.Matches(text).Where(m => m.Length > 0)];

        // From the last match back, so earlier offsets stay right while later text changes.
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            Match match = matches[i];
            Apply(paragraph, pieces, match.Index, match.Length, replacement(match));
        }

        return matches.Count;
    }

    /// <summary>The paragraph's matchable text and where each inline's part of it starts.</summary>
    private static (string Text, List<(Inline Node, int Start)> Pieces) TextOf(Paragraph paragraph, FindOptions options)
    {
        StringBuilder text = new();
        List<(Inline, int)> pieces = [];
        foreach (Node node in paragraph.Descendants())
        {
            if (node.OwnerStory != paragraph.OwnerStory || node is not Inline inline)
            {
                continue;
            }

            if (options.SkipDeletedText && inline.Revision is { Removes: true })
            {
                continue;
            }

            string? piece = inline switch
            {
                Run run => run.Text,
                Tab => "\t",
                Break { Kind: BreakKind.Line } => "\v",
                _ => null,
            };
            if (piece is not null)
            {
                pieces.Add((inline, text.Length));
                text.Append(piece);
            }
        }

        return (text.ToString(), pieces);
    }

    private static void Apply(Paragraph paragraph, List<(Inline Node, int Start)> pieces, int start, int length, string replacement)
    {
        int end = start + length;
        Run? first = null;
        foreach ((Inline node, int pieceStart) in pieces)
        {
            int pieceLength = node is Run run ? run.Text.Length : 1;
            int pieceEnd = pieceStart + pieceLength;
            if (pieceEnd <= start || pieceStart >= end || node.Parent is null)
            {
                continue;
            }

            int from = Math.Max(start, pieceStart) - pieceStart;
            int to = Math.Min(end, pieceEnd) - pieceStart;
            if (node is Run r)
            {
                if (first is null)
                {
                    first = r;
                    r.Text = string.Concat(r.Text.AsSpan(0, from), replacement, r.Text.AsSpan(to));
                }
                else
                {
                    r.Text = r.Text.Remove(from, to - from);
                    if (r.Text.Length == 0)
                    {
                        r.Remove();
                    }
                }
            }
            else
            {
                // A tab or line break in the match: the replacement goes in a new run where the first one was.
                if (first is null)
                {
                    first = new Run(paragraph.Document, replacement);
                    if (PreviousRun(node) is { } before)
                    {
                        first.Format.Apply(before.Format);
                    }

                    Insert(node, first);
                }

                node.Remove();
            }
        }

        // Keep the offsets of the pieces before this match right for the next, earlier match: only text after it changed.
    }

    private static Run? PreviousRun(Node node)
    {
        for (Node? n = node.PreviousSibling; n is not null; n = n.PreviousSibling)
        {
            if (n is Run run)
            {
                return run;
            }
        }

        return null;
    }

    private static void Insert(Node before, Inline inline)
    {
        switch (before.Parent)
        {
            case Paragraph paragraph:
                paragraph.Inlines.Insert(before.IndexInParent, inline);
                break;
            case CompositeInline composite:
                composite.Inlines.Insert(before.IndexInParent, inline);
                break;
        }
    }
}
