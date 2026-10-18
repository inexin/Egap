namespace Egap.Pdf.Layout;

/// <summary>
/// A block found on a page: its role, its lines in reading order, its place in the page's reading order, and for headings and
/// list items their level (and a list item's marker). Every analysis stage reads and refines these; the public
/// <c>PdfPageLayout</c> is made from them.
/// </summary>
internal sealed class BlockDraft(LayoutRole role, List<PageLine> lines)
{
    public LayoutRole Role { get; set; } = role;

    public List<PageLine> Lines { get; } = lines;

    public PageBox Box => PageBox.Around(Lines.Select(l => l.Box));

    public int Order { get; set; }

    /// <summary>A heading's level (1 for the top) or a list item's nesting depth (1 for the outermost list); 0 otherwise.</summary>
    public int Level { get; set; }

    /// <summary>A list item's marker as written (a bullet, "1.", "a)"), or null.</summary>
    public string? Marker { get; set; }

    /// <summary>How sure the analysis is of the role and grouping, from 0 to 1; tags give 1.</summary>
    public double Confidence { get; set; } = 1;

    /// <summary>The structure element the block comes from, when tags gave it.</summary>
    public TagNode? Tag { get; set; }

    /// <summary>Whether the block carries on the paragraph before it, across a column or page break (task 5).</summary>
    public bool Continues { get; set; }

    /// <summary>Whether the role is settled (a region detector named it), so role detection leaves it.</summary>
    public bool Fixed { get; set; }

    /// <summary>Whether the block is made of artifact glyphs: page furniture, dropped unless it is a header, footer or page number.</summary>
    public bool Artifact { get; set; }

    /// <summary>The block's text: its lines joined, words broken at a line end with a hyphen joined again.</summary>
    public string Text => Paragraphs.Text(Lines);

    public override string ToString()
    {
        return $"{Role} {Order}: {Text}";
    }
}

/// <summary>One cell of a found table at its top-left grid position, with its span and lines.</summary>
internal sealed class CellDraft(int row, int column, int rowSpan, int columnSpan, bool header, List<PageLine> lines)
{
    public int Row { get; } = row;

    public int Column { get; } = column;

    public int RowSpan { get; } = rowSpan;

    public int ColumnSpan { get; } = columnSpan;

    public bool Header { get; } = header;

    public List<PageLine> Lines { get; } = lines;

    /// <summary>The cell's area: given by ruling or alignment, or the box around its text when nothing else says.</summary>
    public PageBox Box { get; set; } = PageBox.Around(lines.Select(l => l.Box));

    public string Text => string.Join(' ', Lines.Select(l => l.Text));

    /// <summary>Tables inside the cell (a table nested in a cell, as tags give it).</summary>
    public List<TableDraft> Tables { get; init; } = [];
}

/// <summary>A table found on a page: its cells on a grid of <see cref="Rows"/> by <see cref="Columns"/>.</summary>
internal sealed class TableDraft(List<CellDraft> cells, int rows, int columns)
{
    public List<CellDraft> Cells { get; } = cells;

    public int Rows { get; } = rows;

    public int Columns { get; } = columns;

    public PageBox Box { get; set; } = PageBox.Around(cells.Select(c => c.Box).Where(b => !b.IsEmpty));

    /// <summary>Whether ruling lines drew the grid (task 7) rather than text alignment (task 8) or tags.</summary>
    public bool IsRuled { get; set; }

    /// <summary>Whether the table carries on a table from the page before (task 8).</summary>
    public bool Continues { get; set; }

    /// <summary>Whether, carrying on, its first row repeats the earlier part's header.</summary>
    public bool RepeatsHeader { get; set; }

    public int Order { get; set; }

    public double Confidence { get; set; } = 1;

    public TagNode? Tag { get; set; }

    /// <summary>For a table from tags, the TR element of each row, so a row broken by a page joins its other part.</summary>
    public List<TagNode?>? RowTags { get; set; }

    /// <summary>For a table from tags, the rows that are a piece of a row a page broke: some cell has no content at all.</summary>
    public HashSet<int>? Pieces { get; set; }
}

/// <summary>A figure found on a page: its images, paths and any text inside it, and its alternate text when tags give one.</summary>
internal sealed class FigureDraft(PageBox box)
{
    public PageBox Box { get; set; } = box;

    public List<PageImage> Images { get; } = [];

    public List<PagePath> Paths { get; } = [];

    public List<PageLine> Lines { get; } = [];

    public string? AltText { get; set; }

    /// <summary>Whether all of the figure is marked as artifacts: decoration, with no alternate text needed.</summary>
    public bool Decorative { get; set; }

    public int Order { get; set; }

    public TagNode? Tag { get; set; }
}

/// <summary>
/// What the analysis has found on a page so far: blocks, tables and figures with one shared reading order, the glyphs no
/// stage has placed yet, and the artifact glyphs (page furniture) kept apart for header and footer detection.
/// </summary>
internal sealed class PageDraft(PageContent content)
{
    public PageContent Content { get; } = content;

    public List<BlockDraft> Blocks { get; } = [];

    public List<TableDraft> Tables { get; } = [];

    public List<FigureDraft> Figures { get; } = [];

    /// <summary>Content glyphs no block, table or figure holds yet.</summary>
    public List<PageGlyph> Unplaced { get; } = [];

    /// <summary>Glyphs marked as artifacts.</summary>
    public List<PageGlyph> Artifacts { get; } = [];

    /// <summary>Whether roles and order came from the structure tree (design Q2).</summary>
    public bool FromTags { get; set; }

    /// <summary>Renumbers the reading order of blocks, tables and figures from 0, keeping their relative order.</summary>
    public void Renumber()
    {
        List<(int Order, Action<int> Set)> items =
        [
            .. Blocks.Select(b => (b.Order, (Action<int>)(o => b.Order = o))),
            .. Tables.Select(t => (t.Order, (Action<int>)(o => t.Order = o))),
            .. Figures.Select(f => (f.Order, (Action<int>)(o => f.Order = o))),
        ];
        int next = 0;
        foreach ((_, Action<int> set) in items.OrderBy(i => i.Order))
        {
            set(next++);
        }
    }
}
