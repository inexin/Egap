namespace Egap.Pdf.Conversion.Tests.Synthetic;

/// <summary>What a block on a synthetic page is, as the analysis should find it (design Q4).</summary>
internal enum TruthRole
{
    Title,
    Heading,
    Paragraph,
    ListItem,
    Quote,
    Code,
    Caption,
    Footnote,
    PageHeader,
    PageFooter,
    PageNumber,
}

/// <summary>How a synthetic table is ruled (design Q5): every cell edge, only the top, the line under the header and the bottom, or not at all.</summary>
internal enum TruthRules
{
    All,
    Partial,
    None,
}

/// <summary>A box in page points from the page's top left, y down, as the canvas draws.</summary>
internal readonly record struct TruthBox(double Left, double Top, double Right, double Bottom)
{
    public static TruthBox Union(IEnumerable<TruthBox> boxes)
    {
        List<TruthBox> all = [.. boxes];
        return new(all.Min(b => b.Left), all.Min(b => b.Top), all.Max(b => b.Right), all.Max(b => b.Bottom));
    }

    public bool Contains(double x, double y, double tolerance = 0.5)
    {
        return x >= Left - tolerance && x <= Right + tolerance && y >= Top - tolerance && y <= Bottom + tolerance;
    }
}

/// <summary>One drawn line of a block: the text as drawn (with a hyphen where a word was split) and its box.</summary>
internal sealed record TruthLine(string Text, TruthBox Box, double Baseline);

/// <summary>
/// One block as drawn on one page: its role, its text as the analysis should give it back (lines joined by spaces, split
/// words joined without their hyphen), its lines, its place in reading order across the document, and the heading or list
/// level. <see cref="Continues"/> is set on the second part of a paragraph broken across a column or page, which the
/// analysis should join to the part before (design Q3).
/// </summary>
internal sealed record TruthBlock(int Page, TruthRole Role, string Text, IReadOnlyList<TruthLine> Lines, int Order, int Level = 0, bool Continues = false,
    string? Marker = null, string? FontName = null, double FontSize = 0, bool Bold = false, bool Italic = false)
{
    public TruthBox Box => TruthBox.Union(Lines.Select(l => l.Box));
}

/// <summary>One cell of a synthetic table, at its top-left grid position, with its span.</summary>
internal sealed record TruthCell(int Row, int Column, int RowSpan, int ColumnSpan, string Text, bool Header);

/// <summary>
/// One table as drawn on one page. The second part of a table broken across pages has <see cref="Continues"/> set, and
/// <see cref="RepeatsHeader"/> when its header rows were drawn again; its rows count from 0 on each part.
/// </summary>
internal sealed record TruthTable(int Page, TruthBox Box, int Rows, int Columns, IReadOnlyList<TruthCell> Cells, TruthRules Rules, int Order,
    bool Continues = false, bool RepeatsHeader = false);
