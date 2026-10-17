using Egami.Fonts.Layout;

namespace Egap.Documents.Layout;

/// <summary>A rectangle on a page in points, from the page's top left corner.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    /// <summary>The right edge.</summary>
    public double Right => X + Width;

    /// <summary>The bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>Whether the point is inside.</summary>
    public bool Contains(double x, double y)
    {
        return x >= X && x < Right && y >= Y && y < Bottom;
    }
}

/// <summary>What part of a page a box belongs to.</summary>
public enum PageArea
{
    /// <summary>The main text.</summary>
    Body,

    /// <summary>The header.</summary>
    Header,

    /// <summary>The footer.</summary>
    Footer,

    /// <summary>The footnotes at the foot of the page, or endnotes.</summary>
    Notes,
}

/// <summary>
/// One box of a laid-out page (Phase 6 design Q13): its place on the page, the model node it shows, and the boxes inside it.
/// Boxes are immutable; a new layout makes new boxes.
/// </summary>
public abstract class LayoutBox
{
    private protected LayoutBox(Node? node, LayoutRect bounds, IReadOnlyList<LayoutBox>? children)
    {
        Node = node;
        Bounds = bounds;
        Children = children ?? [];
    }

    /// <summary>The model node this box shows, or null for a box of the layout's own (a column, a note separator).</summary>
    public Node? Node { get; }

    /// <summary>Where the box is on its page, in points.</summary>
    public LayoutRect Bounds { get; }

    /// <summary>The boxes inside this one.</summary>
    public IReadOnlyList<LayoutBox> Children { get; }

    /// <summary>This box and every box inside it, depth first.</summary>
    public IEnumerable<LayoutBox> Descendants()
    {
        Stack<LayoutBox> stack = new();
        stack.Push(this);
        while (stack.Count > 0)
        {
            LayoutBox box = stack.Pop();
            yield return box;
            for (int i = box.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(box.Children[i]);
            }
        }
    }
}

/// <summary>A page area (body column, header, footer or notes) holding block boxes.</summary>
public sealed class AreaBox : LayoutBox
{
    internal AreaBox(PageArea area, Story? story, LayoutRect bounds, IReadOnlyList<LayoutBox> children)
        : base(story, bounds, children)
    {
        Area = area;
    }

    /// <summary>Which area.</summary>
    public PageArea Area { get; }
}

/// <summary>A paragraph, or the part of one on this page or column, holding its lines.</summary>
public sealed class ParagraphBox : LayoutBox
{
    internal ParagraphBox(Paragraph paragraph, LayoutRect bounds, IReadOnlyList<LineBox> lines, bool continued, bool continues, IReadOnlyList<LayoutBox>? objects = null)
        : base(paragraph, bounds, objects is { Count: > 0 } ? [.. lines, .. objects] : lines)
    {
        Lines = lines;
        IsContinuation = continued;
        Continues = continues;
    }

    /// <summary>The paragraph.</summary>
    public Paragraph Paragraph => (Paragraph)Node!;

    /// <summary>The lines in order.</summary>
    public IReadOnlyList<LineBox> Lines { get; }

    /// <summary>Whether the paragraph started on an earlier page or column.</summary>
    public bool IsContinuation { get; }

    /// <summary>Whether the paragraph goes on in the next page or column.</summary>
    public bool Continues { get; }
}

/// <summary>One line of a paragraph: Egami's laid-out line placed on the page.</summary>
public sealed class LineBox : LayoutBox
{
    internal LineBox(Paragraph paragraph, LayoutRect bounds, double baseline, TextLine line, ParagraphContent content)
        : base(paragraph, bounds, null)
    {
        Baseline = baseline;
        Line = line;
        Content = content;
    }

    /// <summary>The baseline's distance from the page's top, in points.</summary>
    public double Baseline { get; }

    /// <summary>Egami's line: its runs, glyphs and text range.</summary>
    public TextLine Line { get; }

    /// <summary>The paragraph's text as laid out, with the maps back to its inlines.</summary>
    internal ParagraphContent Content { get; }

    /// <summary>The line's text.</summary>
    public string Text => Content.Text.Text.Substring(Line.Start, Line.Length);
}

/// <summary>A picture, shape or island in a line or floating on the page.</summary>
public sealed class ObjectBox : LayoutBox
{
    internal ObjectBox(Inline node, LayoutRect bounds, bool floating, IReadOnlyList<LayoutBox>? children = null)
        : base(node, bounds, children)
    {
        IsFloating = floating;
    }

    /// <summary>Whether the object floats (is placed by its anchor) rather than sitting in a line.</summary>
    public bool IsFloating { get; }
}

/// <summary>A table, or the rows of one on this page or column.</summary>
public sealed class TableBox : LayoutBox
{
    internal TableBox(Table table, LayoutRect bounds, IReadOnlyList<LayoutBox> rows)
        : base(table, bounds, rows)
    {
    }
}

/// <summary>A table row on this page or column.</summary>
public sealed class RowBox : LayoutBox
{
    internal RowBox(Row row, LayoutRect bounds, IReadOnlyList<LayoutBox> cells, bool repeatedHeader)
        : base(row, bounds, cells)
    {
        IsRepeatedHeader = repeatedHeader;
    }

    /// <summary>Whether this is a header row repeated at the top of a page.</summary>
    public bool IsRepeatedHeader { get; }
}

/// <summary>A table cell, with its blocks, borders and shading.</summary>
public sealed class CellBox : LayoutBox
{
    internal CellBox(Cell cell, LayoutRect bounds, IReadOnlyList<LayoutBox> blocks, CellBorders borders, Egami.Pixels.Rgba8? fill)
        : base(cell, bounds, blocks)
    {
        Borders = borders;
        Fill = fill;
    }

    /// <summary>The cell.</summary>
    public Cell Cell => (Cell)Node!;

    /// <summary>The cell's borders: its own where it has them, else the table's inside or outside borders.</summary>
    public CellBorders Borders { get; }

    /// <summary>The cell's background colour, or null for none.</summary>
    public Egami.Pixels.Rgba8? Fill { get; }
}
