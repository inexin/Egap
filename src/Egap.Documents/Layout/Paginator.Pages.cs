namespace Egap.Documents.Layout;

/// <summary>A column of a page's body: its left edge and width.</summary>
internal readonly record struct ColumnGeometry(double X, double Width);

/// <summary>A section's page geometry: page size, body area and columns.</summary>
internal sealed record Geometry(double PageWidth, double PageHeight, double BodyLeft, double BodyTop, double BodyRight, double BodyBottom, IReadOnlyList<ColumnGeometry> Columns)
{
    // Word's defaults: US Letter, 1 inch margins, header and footer half an inch from the edge.
    public static double WidthOf(Section section) => section.PageSetup.PageWidth ?? 612;

    public static double HeightOf(Section section) => section.PageSetup.PageHeight ?? 792;

    /// <summary>A page's geometry; <paramref name="number"/> decides which side the margins go on when they are mirrored.</summary>
    public static Geometry Of(Section section, int number, double? top = null, double? bottom = null)
    {
        PageSetup setup = section.PageSetup;
        DocumentSettings settings = section.Document.Settings;
        double width = WidthOf(section);
        double height = HeightOf(section);
        double gutter = setup.Gutter ?? 0;
        double marginLeft = setup.MarginLeft ?? 72;
        double marginRight = setup.MarginRight ?? 72;
        double gutterTop = 0;
        if (settings.GutterAtTop)
        {
            gutterTop = gutter;
        }
        else
        {
            marginLeft += gutter;
        }

        if (settings.MirrorMargins && number % 2 == 0)
        {
            (marginLeft, marginRight) = (marginRight, marginLeft);
        }

        double left = marginLeft;
        double right = width - marginRight;
        if (right - left < 18)
        {
            right = left + 18;
        }

        double bodyTop = top ?? Math.Abs(setup.MarginTop ?? 72) + gutterTop;
        double bodyBottom = bottom ?? height - Math.Abs(setup.MarginBottom ?? 72);
        if (bodyBottom - bodyTop < 18)
        {
            bodyBottom = bodyTop + 18;
        }

        return new Geometry(width, height, left, bodyTop, right, bodyBottom, ColumnsOf(setup, left, right - left));
    }

    private static List<ColumnGeometry> ColumnsOf(PageSetup setup, double left, double width)
    {
        List<ColumnGeometry> columns = [];
        if (setup.EqualColumnWidths == false && setup.Columns is { Count: > 0 } given)
        {
            double x = left;
            foreach (TextColumn column in given)
            {
                columns.Add(new ColumnGeometry(x, Math.Max(9, column.Width)));
                x += column.Width + column.SpaceAfter;
            }

            return columns;
        }

        int count = Math.Clamp(setup.ColumnCount ?? 1, 1, 45);
        double spacing = count == 1 ? 0 : setup.ColumnSpacing ?? 36;
        double each = Math.Max(9, (width - (spacing * (count - 1))) / count);
        for (int i = 0; i < count; i++)
        {
            columns.Add(new ColumnGeometry(left + (i * (each + spacing)), each));
        }

        return columns;
    }
}

/// <summary>A page being filled: its geometry, the blocks placed in each column, its header and footer.</summary>
internal sealed class PageBuilder
{
    private readonly List<(IReadOnlyList<ColumnGeometry> Columns, double Top, List<List<LayoutBox>> Contents)> bands = [];

    public PageBuilder(Section section, int number, bool firstOfSection)
    {
        Section = section;
        Number = number;
        FirstOfSection = firstOfSection;
        Geometry = Geometry.Of(section, number);
        StartColumns(Geometry, Geometry.BodyTop);
    }

    public Section Section { get; }

    public int Number { get; }

    public bool FirstOfSection { get; }

    public Geometry Geometry { get; private set; }

    public AreaBox? Header { get; set; }

    /// <summary>The floating objects placed on this page.</summary>
    public List<PlacedFloat> Floats { get; } = [];

    public AreaBox? Footer { get; set; }

    /// <summary>The columns of the band being filled (a continuous section break starts a new band on the same page).</summary>
    public IReadOnlyList<ColumnGeometry> Columns => bands[^1].Columns;

    /// <summary>Starts a band of columns at <paramref name="top"/>, for the page's first section or a continuous break.</summary>
    public void StartColumns(Geometry geometry, double top)
    {
        Geometry = Geometry with { Columns = geometry.Columns };
        bands.Add((geometry.Columns, top, [.. geometry.Columns.Select(_ => new List<LayoutBox>())]));
    }

    /// <summary>Moves the body down and up around the header and footer laid out on this page.</summary>
    public void SetBodyLimits(double top, double bottom)
    {
        Geometry = Geometry with { BodyTop = top, BodyBottom = bottom };
        if (bands.Count == 1)
        {
            bands[0] = (bands[0].Columns, top, bands[0].Contents);
        }
    }

    public double ColumnTop(int column) => bands[^1].Top;

    public double ColumnBottom(int column) => Geometry.BodyBottom - NotesHeight;

    /// <summary>The footnotes whose references are on this page, in order.</summary>
    public List<Footnote> Notes { get; } = [];

    /// <summary>The height the footnotes and their separator take at the foot of the body.</summary>
    public double NotesHeight { get; set; }

    public List<LayoutBox> ColumnContents(int column) => bands[^1].Contents[column];

    public void Add(int column, LayoutBox box) => bands[^1].Contents[column].Add(box);

    public PageLayout Build(int index, Paginator paginator)
    {
        List<LayoutBox> boxes = [];
        if (Header is not null)
        {
            boxes.Add(Header);
        }

        foreach ((IReadOnlyList<ColumnGeometry> columns, double top, List<List<LayoutBox>> contents) in bands)
        {
            for (int c = 0; c < columns.Count; c++)
            {
                List<LayoutBox> blocks = contents[c];
                double bottom = blocks.Count == 0 ? top : blocks.Max(b => b.Bounds.Bottom);
                boxes.Add(new AreaBox(PageArea.Body, Section.Body, new LayoutRect(columns[c].X, top, columns[c].Width, Math.Max(0, bottom - top)), blocks));
            }
        }

        if (Notes.Count > 0)
        {
            // Footnotes stack up from the separator at the foot of the body.
            double width = Geometry.BodyRight - Geometry.BodyLeft;
            double top = Geometry.BodyBottom - NotesHeight;
            double at = top + 12;
            List<LayoutBox> notes = [];
            foreach (Footnote note in Notes)
            {
                (List<LayoutBox> blocks, double height) = paginator.LayNote(note, Geometry.BodyLeft, at, width);
                notes.AddRange(blocks);
                at += height;
            }

            boxes.Add(new AreaBox(PageArea.Notes, null, new LayoutRect(Geometry.BodyLeft, top, width, NotesHeight), notes));
        }

        if (Footer is not null)
        {
            boxes.Add(Footer);
        }

        boxes.AddRange(Floats.Select(f => f.Box));

        return new PageLayout(index, Numbering.Format(Number, Section.PageSetup.PageNumberStyle ?? NumberStyle.Decimal), Section, Geometry.PageWidth, Geometry.PageHeight, boxes);
    }
}

/// <summary>Headers, footers and tables.</summary>
internal sealed partial class Paginator
{
    /// <summary>The header or footer a page shows: the section's own or, when it has none of that kind, the one before it.</summary>
    private HeaderFooter? StoryFor(Section current, bool header, bool first, bool even)
    {
        HeaderFooterKind kind = (header, first, even) switch
        {
            (true, true, _) => HeaderFooterKind.HeaderFirst,
            (true, _, true) => HeaderFooterKind.HeaderEven,
            (true, _, _) => HeaderFooterKind.HeaderPrimary,
            (false, true, _) => HeaderFooterKind.FooterFirst,
            (false, _, true) => HeaderFooterKind.FooterEven,
            _ => HeaderFooterKind.FooterPrimary,
        };
        for (int s = document.Sections.IndexOf(current); s >= 0; s--)
        {
            if (document.Sections[s].HeadersFooters[kind] is { } story)
            {
                return story;
            }
        }

        return null;
    }

    private void LayHeadersAndFooters(PageBuilder target)
    {
        Section current = target.Section;
        PageSetup setup = current.PageSetup;
        bool first = target.FirstOfSection && setup.DifferentFirstPage == true;
        bool even = document.Settings.EvenAndOddHeaders && target.Number % 2 == 0;
        Geometry geometry = target.Geometry;
        double top = geometry.BodyTop;
        double bottom = geometry.BodyBottom;
        double width = geometry.BodyRight - geometry.BodyLeft;

        if (StoryFor(current, header: true, first, even) is { } header)
        {
            double from = setup.HeaderDistance ?? 36;
            (List<LayoutBox> blocks, double height) = LayStory(header, geometry.BodyLeft, from, width);
            target.Header = new AreaBox(PageArea.Header, header, new LayoutRect(geometry.BodyLeft, from, width, height), blocks);

            // The body starts below the header when the header reaches past the top margin, unless the margin is exact (negative).
            if ((setup.MarginTop ?? 72) >= 0)
            {
                top = Math.Max(top, from + height);
            }
        }

        if (StoryFor(current, header: false, first, even) is { } footer)
        {
            double distance = setup.FooterDistance ?? 36;
            measuring++;
            (List<LayoutBox> measure, double height) = LayStory(footer, geometry.BodyLeft, 0, width);
            measuring--;
            double from = geometry.PageHeight - distance - height;
            (List<LayoutBox> blocks, _) = LayStory(footer, geometry.BodyLeft, from, width);
            target.Footer = new AreaBox(PageArea.Footer, footer, new LayoutRect(geometry.BodyLeft, from, width, height), blocks);
            _ = measure;
            if ((setup.MarginBottom ?? 72) >= 0)
            {
                bottom = Math.Min(bottom, from);
            }
        }

        target.SetBodyLimits(top, Math.Max(top + 18, bottom));
    }

    /// <summary>Lays a header, footer or other story's blocks out in one column without page breaks.</summary>
    private (List<LayoutBox> Blocks, double Height) LayStory(BlockContainer story, double x, double top, double width)
    {
        return LayBlocks(story.Blocks, x, top, width);
    }

    /// <summary>The blocks with every content control's blocks in its place, as layout reads them.</summary>
    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks)
    {
        foreach (Block block in blocks)
        {
            if (block is ContentControl control)
            {
                foreach (Block inner in Flatten(control.Blocks))
                {
                    yield return inner;
                }
            }
            else
            {
                yield return block;
            }
        }
    }

    /// <summary>The height blocks take in one column of <paramref name="width"/>, laid out without page breaks.</summary>
    private double MeasureBlocks(IEnumerable<Block> blocks, double width)
    {
        measuring++;
        try
        {
            return LayBlocks(blocks, 0, 0, width).Height;
        }
        finally
        {
            measuring--;
        }
    }

    /// <summary>Lays blocks out in one column without page breaks, content controls' blocks in their place.</summary>
    private (List<LayoutBox> Blocks, double Height) LayBlocks(IEnumerable<Block> story, double x, double top, double width)
    {
        List<LayoutBox> boxes = [];
        double at = top;
        foreach (Block block in Flatten(story))
        {
            switch (block)
            {
                case Paragraph paragraph:
                    {
                        ParagraphContent content = ContentOf(paragraph, PageFieldText);
                        ResolvedParagraphFormat format = content.Format;
                        at += format.SpaceBefore;
                        if (content.Floats.Count > 0 && page is not null)
                        {
                            // A header's logo or a cell's floating picture is placed on the page like the body's.
                            PlaceFloats(content, at);
                        }

                        List<PlacedLine> lines = Lines(content, width);
                        List<LineBox> lineBoxes = [];
                        double lineTop = at;
                        foreach (PlacedLine line in lines)
                        {
                            lineBoxes.Add(new LineBox(paragraph, new LayoutRect(x + line.Indent, lineTop, line.Available, line.Height), lineTop + line.Ascent, line.Line, content));
                            lineTop += line.Height;
                        }

                        boxes.Add(new ParagraphBox(paragraph, new LayoutRect(x, at, width, lineTop - at), lineBoxes, false, false, InlineObjectBoxes(content, lineBoxes)));
                        at = lineTop + format.SpaceAfter;
                        break;
                    }

                case Table table when measuring > 0:
                    {
                        // Measuring needs only the height: the plan, without placing every nested cell again.
                        at += Plan(table, x, width).Rows.Sum(r => r.Height);
                        break;
                    }

                case Table table:
                    {
                        TableBox box = LayTable(table, x, at, width);
                        boxes.Add(box);
                        at = box.Bounds.Bottom;
                        break;
                    }
            }
        }

        return (boxes, at - top);
    }
}
