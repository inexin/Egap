using Egami.Fonts;
using Egami.Fonts.Layout;

namespace Egap.Documents.Layout;

/// <summary>Which side of a line break a position belongs to where one index is both the end of a line and the start of the next.</summary>
public enum CaretAffinity
{
    /// <summary>The start of the next line.</summary>
    Downstream,

    /// <summary>The end of the line before.</summary>
    Upstream,
}

/// <summary>
/// A place in the laid-out text (Phase 6 design Q13, Build Plan task 21b): a paragraph and an index into its text as the
/// layout shows it (field results in place of their codes, without the list label), with the side of a line break.
/// <see cref="DocumentLayout.Locate"/> gives the model inline and offset it falls in.
/// </summary>
/// <param name="Paragraph">The paragraph.</param>
/// <param name="Index">The index into its laid-out text.</param>
/// <param name="Affinity">The side of a line break.</param>
public readonly record struct DocumentPosition(Paragraph Paragraph, int Index, CaretAffinity Affinity = CaretAffinity.Downstream);

/// <summary>A caret or selection rectangle on a page.</summary>
/// <param name="Page">The page.</param>
/// <param name="Bounds">The rectangle in points.</param>
public readonly record struct PageRect(PageLayout Page, LayoutRect Bounds);

/// <summary>Hit testing, carets and selections over the laid-out pages, on Egami's glyph positions.</summary>
internal static class LayoutInteraction
{
    /// <summary>The line nearest <paramref name="y"/> on the page (then nearest <paramref name="x"/>), and the position in it nearest <paramref name="x"/>.</summary>
    public static DocumentPosition? HitTest(PageLayout page, double x, double y)
    {
        LineBox? best = null;
        double bestDistance = double.MaxValue;
        foreach (LineBox line in page.Boxes.SelectMany(b => b.Descendants()).OfType<LineBox>())
        {
            LayoutRect r = line.Bounds;
            double dy = y < r.Y ? r.Y - y : y > r.Bottom ? y - r.Bottom : 0;
            double dx = x < r.X ? r.X - x : x > r.Right ? x - r.Right : 0;
            double distance = (dy * 1000) + dx;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = line;
            }
        }

        if (best is null)
        {
            return null;
        }

        int index = IndexAt(best, x - best.Bounds.X);
        bool atEnd = index == best.Line.End && !best.Line.HardBreak && best.Line.End < best.Content.Text.Text.Length;
        return Position(best, index, atEnd ? CaretAffinity.Upstream : CaretAffinity.Downstream);
    }

    /// <summary>The caret's rectangle (a line one point wide, the line's height) for a position, or null when its paragraph is not laid out.</summary>
    public static PageRect? Caret(DocumentLayout layout, DocumentPosition position)
    {
        foreach ((PageLayout page, LineBox line) in Lines(layout, position.Paragraph))
        {
            int index = position.Index + line.Content.LabelLength;
            bool inLine = index >= line.Line.Start && (index < line.Line.End || (index == line.Line.End && (position.Affinity == CaretAffinity.Upstream || IsLast(line))));
            if (inLine)
            {
                double x = line.Bounds.X + CaretX(line.Line, index);
                return new PageRect(page, new LayoutRect(x, line.Bounds.Y, 1, line.Bounds.Height));
            }
        }

        return null;
    }

    /// <summary>The rectangles covering the text from <paramref name="start"/> to <paramref name="end"/>, line by line across pages.</summary>
    public static List<PageRect> Selection(DocumentLayout layout, DocumentPosition start, DocumentPosition end)
    {
        List<Paragraph> order = [.. layout.Document.Descendants<Paragraph>()];
        Dictionary<Paragraph, int> rank = [];
        for (int i = 0; i < order.Count; i++)
        {
            rank[order[i]] = i;
        }

        if (!rank.TryGetValue(start.Paragraph, out int from) || !rank.TryGetValue(end.Paragraph, out int to))
        {
            return [];
        }

        if (from > to || (from == to && start.Index > end.Index))
        {
            (start, end, from, to) = (end, start, to, from);
        }

        List<PageRect> rects = [];
        for (int p = from; p <= to; p++)
        {
            Paragraph paragraph = order[p];
            foreach ((PageLayout page, LineBox line) in Lines(layout, paragraph))
            {
                int label = line.Content.LabelLength;
                int a = p == from ? start.Index + label : label;
                int b = p == to ? end.Index + label : int.MaxValue;
                int lineStart = Math.Max(line.Line.Start, a);
                int lineEnd = Math.Min(line.Line.End, b);
                if (lineStart > lineEnd || (lineStart == lineEnd && !(p < to && lineEnd == line.Line.End)))
                {
                    continue;
                }

                double x1 = CaretX(line.Line, lineStart);
                double x2 = CaretX(line.Line, lineEnd);

                // A selection going on past the line's end covers the line's trailing space, as editors draw it.
                if (lineEnd == line.Line.End && b > line.Line.End)
                {
                    x2 = Math.Max(x2, line.Line.Left + line.Line.Width + 3);
                }

                rects.Add(new PageRect(page, new LayoutRect(line.Bounds.X + Math.Min(x1, x2), line.Bounds.Y, Math.Abs(x2 - x1), line.Bounds.Height)));
            }
        }

        return rects;
    }

    /// <summary>The rectangles covering an inline's text (the runs inside it for a container), line by line.</summary>
    public static List<PageRect> TextRects(DocumentLayout layout, Inline node)
    {
        if (node.Ancestors().OfType<Paragraph>().FirstOrDefault() is not { } paragraph)
        {
            return [];
        }

        int start = int.MaxValue;
        int end = -1;
        foreach ((_, LineBox line) in Lines(layout, paragraph))
        {
            foreach (InlineSpan span in line.Content.Spans)
            {
                if (span.Node == node || span.Node.Ancestors().Contains(node))
                {
                    start = Math.Min(start, span.Start);
                    end = Math.Max(end, span.Start + span.Length);
                }
            }

            break;
        }

        List<PageRect> rects = [];
        if (end < 0)
        {
            return rects;
        }

        foreach ((PageLayout page, LineBox line) in Lines(layout, paragraph))
        {
            int a = Math.Max(line.Line.Start, start);
            int b = Math.Min(line.Line.End, end);
            if (a >= b)
            {
                continue;
            }

            double x1 = CaretX(line.Line, a);
            double x2 = b == line.Line.End ? Math.Max(CaretX(line.Line, b), LastGlyphEnd(line.Line)) : CaretX(line.Line, b);
            rects.Add(new PageRect(page, new LayoutRect(line.Bounds.X + Math.Min(x1, x2), line.Bounds.Y, Math.Abs(x2 - x1), line.Bounds.Height)));
        }

        return rects;
    }

    private static double LastGlyphEnd(TextLine line)
    {
        return line.Runs.Count == 0 ? line.Left : line.Runs.Max(r => r.X + r.Advance);
    }

    /// <summary>The inline and the offset in its text a position falls in, or null at a paragraph's end or in text no inline gave (a label).</summary>
    public static (Inline Node, int Offset)? Locate(DocumentLayout layout, DocumentPosition position)
    {
        foreach ((_, LineBox line) in Lines(layout, position.Paragraph))
        {
            int index = position.Index + line.Content.LabelLength;
            foreach (InlineSpan span in line.Content.Spans)
            {
                if (index >= span.Start && index < span.Start + span.Length)
                {
                    return (span.Node, span.NodeOffset + (index - span.Start));
                }
            }

            break;
        }

        return null;
    }

    private static IEnumerable<(PageLayout Page, LineBox Line)> Lines(DocumentLayout layout, Paragraph paragraph)
    {
        foreach ((PageLayout page, LayoutBox box) in layout.FindBoxes(paragraph))
        {
            if (box is LineBox line)
            {
                yield return (page, line);
            }
        }
    }

    private static bool IsLast(LineBox line)
    {
        return line.Line.End >= line.Content.Text.Text.Length;
    }

    private static DocumentPosition Position(LineBox line, int index, CaretAffinity affinity)
    {
        return new DocumentPosition((Paragraph)line.Node!, Math.Max(0, index - line.Content.LabelLength), affinity);
    }

    /// <summary>The x, from the line box's left, of the caret before the character at <paramref name="index"/>.</summary>
    internal static double CaretX(TextLine line, int index)
    {
        double end = line.Left;
        foreach (LayoutRun run in line.Runs)
        {
            end = Math.Max(end, run.X + run.Advance);
            if (index < run.Start || index > run.Start + run.Length)
            {
                continue;
            }

            if (index == run.Start + run.Length && run.Length > 0)
            {
                continue;
            }

            bool rtl = run.BidiLevel % 2 == 1;
            if (run.Glyphs is GlyphRun glyphs && glyphs.Count > 0)
            {
                ReadOnlySpan<int> clusters = glyphs.Clusters;
                ReadOnlySpan<System.Numerics.Vector2> positions = glyphs.Positions;
                for (int g = 0; g < glyphs.Count; g++)
                {
                    if (clusters[g] >= index)
                    {
                        // A cluster's glyph found: its left edge (its right for right-to-left text).
                        return rtl ? positions[g].X + Advance(glyphs, g, run) : positions[g].X;
                    }
                }

                return rtl ? run.X : run.X + run.Advance;
            }

            double fraction = run.Length == 0 ? 0 : (double)(index - run.Start) / run.Length;
            return rtl ? run.X + run.Advance - (fraction * run.Advance) : run.X + (fraction * run.Advance);
        }

        return end;
    }

    private static double Advance(GlyphRun glyphs, int g, LayoutRun run)
    {
        return g + 1 < glyphs.Count ? glyphs.Positions[g + 1].X - glyphs.Positions[g].X : run.X + run.Advance - glyphs.Positions[g].X;
    }

    /// <summary>The index of the caret position nearest <paramref name="x"/> (from the line box's left) in a line.</summary>
    private static int IndexAt(LineBox box, double x)
    {
        TextLine line = box.Line;
        int best = line.Start;
        double bestDistance = double.MaxValue;
        int last = line.HardBreak && line.End > line.Start ? line.End - 1 : line.End;
        for (int index = line.Start; index <= last; index++)
        {
            if (index > line.Start && index < line.End && char.IsLowSurrogate(box.Content.Text.Text[index]))
            {
                continue;
            }

            double distance = Math.Abs(CaretX(line, index) - x);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = index;
            }
        }

        return Math.Max(best, box.Content.LabelLength > line.Start ? box.Content.LabelLength : best);
    }
}
