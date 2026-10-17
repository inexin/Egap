using System.Text;

namespace Egap.Pdf.Layout;

/// <summary>
/// Paragraphs from blocks (design Q3, Build Plan task 5). A block is split before a line that starts a paragraph: one that
/// follows a bigger step than the block's usual line pitch, one with a first-line indent that the next line gives back, one
/// that starts at a new left edge the lines after it keep (a quote set in), one that starts with a list marker, and one that
/// follows a line short enough for its first word to have fitted. Code blocks split only at bigger steps. Paragraphs that a column or page break, or a pull quote set
/// in the column, interrupts are joined again, and a word broken with a hyphen at a line end is joined again.
/// </summary>
internal static class Paragraphs
{
    /// <summary>Characters that end a sentence, and closing quotes and brackets that may follow them.</summary>
    private const string Enders = ".!?:;";
    private const string Closers = "\"')]\u201D\u2019";

    /// <summary>Splits each block into paragraphs, keeping their order.</summary>
    public static List<BlockDraft> Split(IEnumerable<BlockDraft> blocks)
    {
        List<BlockDraft> paragraphs = [];
        foreach (BlockDraft block in blocks)
        {
            List<int> starts = Starts(block.Lines);
            for (int k = 0; k < starts.Count; k++)
            {
                int end = k + 1 < starts.Count ? starts[k + 1] : block.Lines.Count;
                paragraphs.Add(new BlockDraft(block.Role, block.Lines.GetRange(starts[k], end - starts[k]))
                {
                    Order = block.Order,
                    Level = block.Level,
                    Marker = block.Marker,
                    Confidence = block.Confidence,
                    Tag = block.Tag,
                    Continues = k == 0 && block.Continues,
                });
            }
        }

        return paragraphs;
    }

    /// <summary>
    /// Marks each paragraph that carries on the one before it in <paramref name="order"/> across a break: a column break (the
    /// next starts higher up) or a jump over something set in the column (a pull quote, a figure: another block lies between
    /// them). <paramref name="all"/> holds every block on the page, to find what lies between; by default the order itself.
    /// </summary>
    public static void JoinAcrossColumns(List<BlockDraft> order, IReadOnlyList<BlockDraft>? all = null)
    {
        bool indentStyle = IndentStyle(order);
        for (int i = 1; i < order.Count; i++)
        {
            BlockDraft before = order[i - 1], next = order[i];
            if (before.Role != LayoutRole.Text || next.Role != LayoutRole.Text)
            {
                continue;
            }

            double size = before.Lines[^1].Size;
            if (Math.Abs(next.Lines[0].Size - size) > size * 0.1)
            {
                continue;
            }

            bool columnBreak = next.Box.Top < before.Box.Bottom - size;
            bool sameColumn = Math.Abs(next.Box.Left - before.Box.Left) < size * 2 || Math.Abs(next.Lines[^1].Box.Left - before.Lines[^1].Box.Left) < size;
            bool jump = !columnBreak && sameColumn && next.Box.Top - before.Box.Bottom > size * 2 && (all ?? order).Any(b => b != before && b != next
                && b.Box.Top >= before.Box.Bottom - 1 && b.Box.Bottom <= next.Box.Top + 1
                && Math.Min(b.Box.Right, next.Box.Right) > Math.Max(b.Box.Left, next.Box.Left));
            if ((columnBreak || jump) && Carries(before, next, indentStyle))
            {
                next.Continues = true;
            }
        }
    }

    /// <summary>
    /// Whether the paragraphs of a page mark their starts with a first-line indent: half or more of those with two lines or
    /// more have one.
    /// </summary>
    public static bool IndentStyle(IEnumerable<BlockDraft> paragraphs)
    {
        int many = 0, indented = 0;
        foreach (BlockDraft p in paragraphs.Where(p => p.Role == LayoutRole.Text && p.Lines.Count >= 2))
        {
            many++;
            indented += Indented(p) ? 1 : 0;
        }

        return many > 0 && indented * 2 >= many;
    }

    /// <summary>
    /// Whether <paramref name="next"/> carries on <paramref name="before"/>. The earlier paragraph ends in a hyphen, or the
    /// later one starts like no paragraph does: never with a list marker or a first-line indent; with an unindented first line
    /// where paragraphs are indented (<paramref name="indentStyle"/>); elsewhere where the earlier one does not end a sentence.
    /// </summary>
    public static bool Carries(BlockDraft before, BlockDraft next, bool indentStyle = false)
    {
        string last = before.Lines[^1].Text.TrimEnd();
        string first = next.Lines[0].Text.TrimStart();
        if (last.Length == 0 || first.Length == 0 || BlockBuilder.IsMarker(next.Lines[0].Words[0]))
        {
            return false;
        }

        if (last[^1] is '-' or '\u00AD')
        {
            return true;
        }

        if (Indented(next))
        {
            return false;
        }

        if (indentStyle && next.Lines.Count >= 2)
        {
            return true;
        }

        string trimmed = last.TrimEnd(Closers.ToCharArray());
        return trimmed.Length > 0 && !Enders.Contains(trimmed[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The text of lines as one paragraph: lines joined by spaces, and a word broken at a line end with a hyphen (U+002D) or a
    /// soft hyphen, when the next line goes on in lower case in the same font, joined without it.
    /// </summary>
    public static string Text(IReadOnlyList<PageLine> lines)
    {
        StringBuilder text = new();
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Text;
            if (i + 1 < lines.Count && Hyphenated(lines[i], lines[i + 1]))
            {
                text.Append(line, 0, line.Length - 1);
                continue;
            }

            text.Append(line);
            if (i + 1 < lines.Count)
            {
                text.Append(' ');
            }
        }

        return text.ToString();
    }

    /// <summary>Joins the text of a paragraph and the one that carries it on, the hyphen of a broken word dropped.</summary>
    public static string Join(string first, string second)
    {
        if (first.Length > 1 && first[^1] is '-' or '\u00AD' && char.IsLetter(first[^2]) && second.Length > 0 && char.IsLower(second[0]))
        {
            return first[..^1] + second;
        }

        return first.Length == 0 ? second : first + " " + second;
    }

    private static bool Indented(BlockDraft paragraph)
    {
        return paragraph.Lines.Count > 1 && paragraph.Lines[0].Box.Left > paragraph.Lines.Skip(1).Min(l => l.Box.Left) + (paragraph.Lines[0].Size * 0.8);
    }

    /// <summary>Whether a line ends in a word broken with a hyphen that the next line finishes.</summary>
    internal static bool Hyphenated(PageLine line, PageLine next)
    {
        PageWord last = line.Words[^1], first = next.Words[0];
        string word = last.Text;
        if (word.Length < 2 || word[^1] is not ('-' or '\u00AD') || !char.IsLetter(word[^2]) || !char.IsLower(first.Text[0]))
        {
            return false;
        }

        return FontTraits.Family(last.Glyphs[^1].Font) == FontTraits.Family(first.Glyphs[0].Font);
    }

    /// <summary>The style most of a line's glyphs are set in: family, size to the half point, bold.</summary>
    private static (string Family, double Size, bool Bold) LineStyle(PageLine line)
    {
        return line.Words.SelectMany(w => w.Glyphs).GroupBy(g => (FontTraits.Family(g.Font), Math.Round(g.Size * 2) / 2, FontTraits.IsBold(g)))
            .MaxBy(g => g.Count())!.Key;
    }

    /// <summary>The indices of the lines that start a paragraph in a block (always 0).</summary>
    private static List<int> Starts(List<PageLine> lines)
    {
        List<int> starts = [0];
        if (lines.Count < 2)
        {
            return starts;
        }

        double size = lines.Select(l => l.Size).Order().ElementAt(lines.Count / 2);
        List<double> steps = [.. Enumerable.Range(1, lines.Count - 1).Select(i => lines[i].Baseline - lines[i - 1].Baseline)];
        double step = steps.Order().ElementAt(steps.Count / 2);
        bool code = lines.All(l => FontTraits.IsMonospace(l.Words[0].Font));
        // The right edge most full lines reach: one long line (a URL, a formula) does not push it out.
        double right = lines.Select(l => l.Box.Right).Order().ElementAt(Math.Min(lines.Count - 1, (lines.Count * 4) / 5));
        // The usual left edge: the one most lines start at, the leftmost of those that tie.
        double left = lines.GroupBy(l => Math.Round(l.Box.Left / (size * 0.5))).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().First().Box.Left;
        double indent = size * 0.8;

        // A gap after a word that is wider than one and a half of the block's usual word gaps sets the word apart.
        double wide = BlockBuilder.WordGap(lines) * size * 1.5;
        int start = 0;
        for (int i = 1; i < lines.Count; i++)
        {
            PageLine before = lines[i - 1], line = lines[i];
            bool split = steps[i - 1] > (step * 1.2) + (size * 0.1);
            if (!code && !split)
            {
                // A line under the text of a list item's or a label's first line carries it on (a hanging indent): the line
                // before starts with a marker or a word set apart by a wide gap, and this line starts where its text does.
                bool underItem = before.Words.Count > 1 && Math.Abs(line.Box.Left - before.Words[1].Box.Left) < indent / 2
                    && (BlockBuilder.IsMarker(before.Words[0]) || before.Words[1].Box.Left - before.Words[0].Box.Right > wide);
                bool firstLineIndent = !underItem && line.Box.Left > left + indent && (i + 1 == lines.Count || Math.Abs(lines[i + 1].Box.Left - left) < indent)
                    && line.Box.Left < left + (size * 8);
                bool newEdge = !underItem && i - 1 > start && Math.Abs(line.Box.Left - before.Box.Left) > indent && i + 1 < lines.Count
                    && Math.Abs(lines[i + 1].Box.Left - line.Box.Left) < indent / 2;
                bool hanging = line.Box.Left < left - indent;
                // A marker starts an item when it stands apart from the text after it or the line is set out from the block.
                bool marker = BlockBuilder.IsMarker(line.Words[0]) && line.Words.Count > 1
                    && (line.Words[1].Box.Left - line.Words[0].Box.Right > wide || line.Box.Left < left - (indent / 2));

                // A line ends its paragraph when the next line's first word would have fitted after it.
                bool shortBefore = right - before.Box.Right > line.Words[0].Box.Width + size;
                bool styled = i == start + 1 && i == 1 && LineStyle(before) != LineStyle(line) && (i + 1 == lines.Count || LineStyle(lines[i + 1]) == LineStyle(line))
                    && !before.Text.TrimEnd().EndsWith('-');
                split = firstLineIndent || newEdge || hanging || marker || styled || (shortBefore && !underItem);
            }

            if (split)
            {
                starts.Add(i);
                start = i;
            }
        }

        return starts;
    }
}
