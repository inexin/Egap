using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>
/// Docstrum-style blocks (design Q3, Build Plan task 4). Phase 3's lines are first cut at gaps much wider than the page's
/// usual word gap, so a line never runs across a column gutter; then each line piece joins the nearest piece below it when
/// each is the other's nearest neighbour, they overlap side to side, their sizes match, nothing is ruled between them and
/// the step between them is no more than the page's usual line pitch allows. Spacing is measured on each page, so dense
/// and airy pages group alike.
/// </summary>
internal static partial class BlockBuilder
{
    /// <summary>Characters used as bullets: round, square, triangular and dash bullets, check marks and Symbol and Wingdings bullets.</summary>
    internal const string Bullets = "\u2022\u25E6\u25AA\u25AB\u25CF\u25CB\u25A0\u25A1\u25BA\u25B8\u2023\u2043\u2013\u2014\u00B7\u2713\u2714\u27A2\uF0B7\uF0A7\uF076\uF0D8*-o\u3009\u00BB\u203A\u2192\u27A4\u25B6\u25C6\u25C7\u2666";

    /// <summary>A line piece with the place its first glyph has in the content stream.</summary>
    internal sealed record Piece(PageLine Line, int Content);

    /// <summary>The blocks of <paramref name="glyphs"/> (in content order), each with its lines top to bottom and its content place.</summary>
    public static List<(BlockDraft Block, int Content)> Build(IReadOnlyList<PageGlyph> glyphs, IReadOnlyList<PagePath> paths, LayoutThresholds thresholds)
    {
        Dictionary<PageGlyph, int> place = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < glyphs.Count; i++)
        {
            place.TryAdd(glyphs[i], i);
        }

        List<PageLine> lines = PageReader.Lines(glyphs);
        double wordGap = WordGap(lines);
        List<Piece> pieces = [.. lines.SelectMany(l => Cut(l, wordGap, thresholds)).Select(l => new Piece(l, l.Words.SelectMany(w => w.Glyphs).Min(g => place[g])))];
        List<PageBox> rules = Rules(paths);
        double pitch = Pitch(pieces, thresholds);

        // Each piece's nearest piece below, and each piece's nearest piece above, among those it could join.
        List<int> byTop = [.. Enumerable.Range(0, pieces.Count).OrderBy(i => pieces[i].Line.Box.Top)];
        int[] below = [.. Enumerable.Repeat(-1, pieces.Count)];
        int[] above = [.. Enumerable.Repeat(-1, pieces.Count)];
        double[] belowStep = new double[pieces.Count], aboveStep = new double[pieces.Count];
        for (int k = 0; k < byTop.Count; k++)
        {
            int a = byTop[k];
            PageLine upper = pieces[a].Line;
            for (int m = k + 1; m < byTop.Count; m++)
            {
                int b = byTop[m];
                PageLine lower = pieces[b].Line;
                if (lower.Box.Top > upper.Box.Bottom + (upper.Size * 4))
                {
                    break;
                }

                double step = lower.Baseline - upper.Baseline;
                if (step < upper.Size * 0.5 || !Compatible(upper, lower, thresholds))
                {
                    continue;
                }

                if (below[a] < 0 || step < belowStep[a])
                {
                    (below[a], belowStep[a]) = (b, step);
                }

                if (above[b] < 0 || step < aboveStep[b])
                {
                    (above[b], aboveStep[b]) = (a, step);
                }
            }
        }

        int[] parent = [.. Enumerable.Range(0, pieces.Count)];
        for (int a = 0; a < pieces.Count; a++)
        {
            int b = below[a];
            if (b < 0 || above[b] != a)
            {
                continue;
            }

            PageLine upper = pieces[a].Line, lower = pieces[b].Line;
            double size = Math.Max(upper.Size, lower.Size);
            if (belowStep[a] <= pitch * thresholds.LinePitch * size && !RuledBetween(upper.Box, lower.Box, rules))
            {
                parent[Find(parent, b)] = Find(parent, a);
            }
        }

        List<(BlockDraft, int)> blocks = [];
        foreach (IGrouping<int, int> group in Enumerable.Range(0, pieces.Count).GroupBy(i => Find(parent, i)))
        {
            List<Piece> members = [.. group.Select(i => pieces[i]).OrderBy(p => p.Line.Box.Top).ThenBy(p => p.Line.Box.Left)];
            blocks.Add((new BlockDraft(LayoutRole.Text, [.. members.Select(p => p.Line)]), members.Min(p => p.Content)));
        }

        return blocks;
    }

    /// <summary>
    /// The page's usual gap between words on a line, in multiples of the text size: the median of the gaps on horizontal lines,
    /// 0.25 when there are none.
    /// </summary>
    internal static double WordGap(IEnumerable<PageLine> lines)
    {
        List<double> gaps = [];
        foreach (PageLine line in lines.Where(l => l.IsHorizontal))
        {
            for (int i = 1; i < line.Words.Count; i++)
            {
                double gap = line.Words[i].Box.Left - line.Words[i - 1].Box.Right;
                if (gap > 0)
                {
                    gaps.Add(gap / Math.Max(line.Size, 0.1));
                }
            }
        }

        return gaps.Count == 0 ? 0.25 : Median(gaps);
    }

    /// <summary>A line cut into pieces at gaps wider than the gutter threshold; a line that is not horizontal stays whole.</summary>
    internal static IEnumerable<PageLine> Cut(PageLine line, double wordGap, LayoutThresholds thresholds)
    {
        if (!line.IsHorizontal || line.Words.Count < 2)
        {
            yield return line;
            yield break;
        }

        double gutter = Math.Max(thresholds.GutterWordGaps * wordGap, thresholds.GutterSize) * line.Size;
        int start = 0;
        for (int i = 1; i < line.Words.Count; i++)
        {
            // A list marker keeps its hanging indent's gap without being cut off its item.
            bool marker = i == 1 && IsMarker(line.Words[0]);
            if (!marker && line.Words[i].Box.Left - line.Words[i - 1].Box.Right > gutter)
            {
                yield return new PageLine([.. line.Words.Skip(start).Take(i - start)]);
                start = i;
            }
        }

        yield return start == 0 ? line : new PageLine([.. line.Words.Skip(start)]);
    }

    /// <summary>
    /// Whether a word on the page looks like a list marker: as <see cref="IsMarker(string)"/> says, or a single character in a
    /// symbol font (Wingdings, Symbol, ZapfDingbats), where any letter may draw a bullet.
    /// </summary>
    internal static bool IsMarker(PageWord word)
    {
        if (IsMarker(word.Text))
        {
            return true;
        }

        // Embedded subsets are often flagged symbolic whatever they draw, so the flag counts only for a glyph with no letter
        // or digit for text (a private-use code); the font's name counts for any.
        string family = FontTraits.Family(word.Font);
        return word.Text.Length == 1 && word.Font is PdfFont font
            && (family.Contains("ding", StringComparison.OrdinalIgnoreCase) || family.StartsWith("Symbol", StringComparison.OrdinalIgnoreCase)
                || (font.Flags.HasFlag(PdfFontFlags.Symbolic) && char.GetUnicodeCategory(word.Text[0]) == System.Globalization.UnicodeCategory.PrivateUse));
    }

    /// <summary>
    /// Whether a word looks like a list marker: a bullet, a dash, or a number or letter with a full stop or bracket, such as
    /// "1.", "a)", "(iv)" or "2.3.1".
    /// </summary>
    internal static bool IsMarker(string word)
    {
        if (word.Length == 0)
        {
            return false;
        }

        if (word.All(c => Bullets.Contains(c, StringComparison.Ordinal)) && word.Length <= 2)
        {
            return true;
        }

        // A number, letter or roman numeral, or several joined by dots ("2.3.1", "II.4.6"), with a bracket or a closing dot,
        // bracket or colon, or a dot inside: "1.", "a)", "(iv)", "[12]", "2.3.1".
        bool marked = word[0] is '(' or '[' || word[^1] is '.' or ')' or ']' or ':' || word.Contains('.', StringComparison.Ordinal);

        // A colon ends only a number ("1:"); "CCL:" is an abbreviation, not a roman numeral.
        bool colon = word[^1] == ':' && (word.Length < 2 || !char.IsAsciiDigit(word[^2]));
        // Letters alone joined by dots are an abbreviation ("U.S.", "e.g."), not an outline number.
        bool abbreviation = word.Count(c => c == '.') >= 2 && word.Where(char.IsLetterOrDigit).All(char.IsLetter) && !Numbering().Match(word).Value.All(c => "ivxIVX.".Contains(c, StringComparison.Ordinal));
        return marked && !colon && !abbreviation && word.Length <= 10 && Numbering().IsMatch(word) && !Year().IsMatch(word);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[(\[]?(?:\d{1,3}|[A-Za-z]|[ivxlcIVXLC]{1,5})(?:[.\-](?:\d{1,3}|[A-Za-z]|[ivxIVX]{1,4}))*[.)\]:]?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Numbering();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{4}\.?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Year();

    /// <summary>Thin horizontal strokes and filled bars: table rules and separators that keep the text above and below apart.</summary>
    internal static List<PageBox> Rules(IReadOnlyList<PagePath> paths)
    {
        return [.. paths.Where(p => p.Bounds.Width > 10 && ((p.Stroked && p.Bounds.Height < 2) || (p.Filled && p.Bounds.Height < 2.5))).Select(p => p.Bounds)];
    }

    internal static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>The page's usual line pitch in text sizes: the median step from a piece to its nearest compatible piece below; 1.2 when there is none.</summary>
    private static double Pitch(List<Piece> pieces, LayoutThresholds thresholds)
    {
        List<double> steps = [];
        List<Piece> byTop = [.. pieces.OrderBy(p => p.Line.Box.Top)];
        for (int k = 0; k < byTop.Count; k++)
        {
            PageLine upper = byTop[k].Line;
            double best = double.MaxValue;
            for (int m = k + 1; m < byTop.Count; m++)
            {
                PageLine lower = byTop[m].Line;
                if (lower.Box.Top > upper.Box.Bottom + (upper.Size * 3))
                {
                    break;
                }

                double step = lower.Baseline - upper.Baseline;
                if (step >= upper.Size * 0.5 && Compatible(upper, lower, thresholds))
                {
                    best = Math.Min(best, step);
                }
            }

            if (best < double.MaxValue)
            {
                steps.Add(best / upper.Size);
            }
        }

        return steps.Count == 0 ? 1.2 : Median(steps);
    }

    /// <summary>Whether two pieces could be lines of one block: both horizontal, sizes alike, overlapping side to side.</summary>
    private static bool Compatible(PageLine upper, PageLine lower, LayoutThresholds thresholds)
    {
        if (upper.IsHorizontal != lower.IsHorizontal || !upper.IsHorizontal)
        {
            return false;
        }

        double ratio = Math.Max(upper.Size, lower.Size) / Math.Max(Math.Min(upper.Size, lower.Size), 0.01);
        double overlap = Math.Min(upper.Box.Right, lower.Box.Right) - Math.Max(upper.Box.Left, lower.Box.Left);
        if (ratio > thresholds.SizeRatio)
        {
            return false;
        }

        // Code lines are indented by structure, not by paragraph, so a short line such as "{" need not overlap its neighbour.
        return overlap >= thresholds.Overlap * Math.Min(upper.Box.Width, lower.Box.Width)
            || (FontTraits.IsMonospace(upper.Words[0].Font) && FontTraits.IsMonospace(lower.Words[0].Font) && overlap > -4 * upper.Size);
    }

    private static bool RuledBetween(PageBox upper, PageBox lower, List<PageBox> rules)
    {
        double left = Math.Max(upper.Left, lower.Left), right = Math.Min(upper.Right, lower.Right);
        return rules.Any(r => r.CenterY > upper.Bottom - 1 && r.CenterY < lower.Top + 1 && r.Left < right && r.Right > left);
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }

        return i;
    }
}
