namespace Egap.Documents.Layout;

/// <summary>A column of a page's body: its left edge and width.</summary>
internal readonly record struct ColumnGeometry(double X, double Width);

/// <summary>A section's page geometry: page size, body area and columns.</summary>
internal sealed record Geometry(double PageWidth, double PageHeight, double BodyLeft, double BodyTop, double BodyRight, double BodyBottom, IReadOnlyList<ColumnGeometry> Columns)
{
    // Word's defaults: US Letter, 1 inch margins, header and footer half an inch from the edge.
    // A page is at most 14 400 points on a side, as PDF allows; a larger size read from a file is held to that.
    public static double WidthOf(Section section) => Math.Clamp(section.PageSetup.PageWidth ?? 612, 1, MaxPageSide);

    public static double HeightOf(Section section) => Math.Clamp(section.PageSetup.PageHeight ?? 792, 1, MaxPageSide);

    private const double MaxPageSide = 14400;

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
    private double bandLimit = double.PositiveInfinity;

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

    /// <summary>The margin boxes, laid out in the page margins.</summary>
    public List<AreaBox> MarginBoxes { get; } = [];

    /// <summary>The columns of the band being filled (a continuous section break starts a new band on the same page).</summary>
    public IReadOnlyList<ColumnGeometry> Columns => bands[^1].Columns;

    /// <summary>Starts a band of columns at <paramref name="top"/>, for the page's first section or a continuous break.</summary>
    public void StartColumns(Geometry geometry, double top)
    {
        Geometry = Geometry with { Columns = geometry.Columns };
        bands.Add((geometry.Columns, top, [.. geometry.Columns.Select(_ => new List<LayoutBox>())]));
        bandLimit = double.PositiveInfinity;
    }

    /// <summary>Makes the columns of the band being filled no taller than <paramref name="height"/>, to balance them.</summary>
    public void LimitBand(double height) => bandLimit = bands[^1].Top + height;

    /// <summary>How far down the band being filled its columns' contents reach, column by column.</summary>
    public IEnumerable<double> BandHeights() => bands[^1].Contents.Select(c => c.Count == 0 ? 0 : c.Max(b => b.Bounds.Bottom) - bands[^1].Top);

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

    public double ColumnBottom(int column) => Math.Min(Geometry.BodyBottom - NotesHeight, bandLimit);

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

        boxes.AddRange(MarginBoxes);

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
        LayMarginBoxes(target, first: target.FirstOfSection);
    }

    /// <summary>
    /// Lays out the section's margin boxes in the page margins, as CSS Paged Media places them: corners in the margin
    /// squares, the boxes of an edge side by side between them, sized by their content, each centred across the margin's
    /// depth, or at the top, middle or bottom of a side.
    /// </summary>
    private void LayMarginBoxes(PageBuilder target, bool first)
    {
        MarginBoxCollection all = target.Section.MarginBoxes;
        if (all.Count == 0)
        {
            return;
        }

        Geometry g = target.Geometry;
        double left = g.BodyLeft;
        double right = g.PageWidth - g.BodyRight;
        double top = g.BodyTop;
        double bottom = g.PageHeight - g.BodyBottom;
        double across = g.BodyRight - g.BodyLeft;
        double down = g.BodyBottom - g.BodyTop;
        Dictionary<MarginBoxPosition, (double, double, double)> sizes = [];
        foreach (MarginBoxPosition position in Enum.GetValues<MarginBoxPosition>())
        {
            if (all.For(position, first) is not { Blocks.Count: > 0 } box)
            {
                continue;
            }

            (double x, double y, double w, double h) = position switch
            {
                MarginBoxPosition.TopLeftCorner => (0, 0, left, top),
                MarginBoxPosition.TopRightCorner => (g.BodyRight, 0, right, top),
                MarginBoxPosition.BottomRightCorner => (g.BodyRight, g.BodyBottom, right, bottom),
                MarginBoxPosition.BottomLeftCorner => (0, g.BodyBottom, left, bottom),
                MarginBoxPosition.TopLeft or MarginBoxPosition.TopCenter or MarginBoxPosition.TopRight
                    => At(Along(position - MarginBoxPosition.TopLeft, Sizes(MarginBoxPosition.TopLeft, MarginBoxPosition.TopCenter, MarginBoxPosition.TopRight, true), left, across), 0, top),
                MarginBoxPosition.BottomLeft or MarginBoxPosition.BottomCenter or MarginBoxPosition.BottomRight
                    => At(Along(MarginBoxPosition.BottomLeft - position, Sizes(MarginBoxPosition.BottomLeft, MarginBoxPosition.BottomCenter, MarginBoxPosition.BottomRight, true), left, across), g.BodyBottom, bottom),
                MarginBoxPosition.LeftTop or MarginBoxPosition.LeftMiddle or MarginBoxPosition.LeftBottom
                    => Down(Along(MarginBoxPosition.LeftTop - position, Sizes(MarginBoxPosition.LeftTop, MarginBoxPosition.LeftMiddle, MarginBoxPosition.LeftBottom, false), top, down), 0, left),
                _ => Down(Along(position - MarginBoxPosition.RightTop, Sizes(MarginBoxPosition.RightTop, MarginBoxPosition.RightMiddle, MarginBoxPosition.RightBottom, false), top, down), g.BodyRight, right),
            };
            if (w <= 0 || h <= 0)
            {
                continue;
            }

            measuring++;
            double height = LayStory(box, x, 0, w).Height;
            measuring--;
            double offset = position switch
            {
                MarginBoxPosition.LeftTop or MarginBoxPosition.RightTop => 0,
                MarginBoxPosition.LeftBottom or MarginBoxPosition.RightBottom => h - height,
                _ => (h - height) / 2,
            };
            (List<LayoutBox> blocks, _) = LayStory(box, x, y + offset, w);
            target.MarginBoxes.Add(new AreaBox(PageArea.MarginBox, box, new LayoutRect(x, y, w, h), blocks));
        }

        // How much room each of an edge's three boxes asks for, in order from its start: the widest line along the top
        // and bottom (max-content), the height down the sides; 0 for a box with no content. Measured once per edge.
        (double, double, double) Sizes(MarginBoxPosition start, MarginBoxPosition middle, MarginBoxPosition end, bool across)
        {
            if (!sizes.TryGetValue(start, out (double, double, double) found))
            {
                double depth = start == MarginBoxPosition.RightTop ? right : left;
                found = (Measure(all.For(start, first), across, depth), Measure(all.For(middle, first), across, depth), Measure(all.For(end, first), across, depth));
                sizes[start] = found;
            }

            return found;
        }

        double Measure(MarginBox? box, bool across, double depth)
        {
            if (box is not { Blocks.Count: > 0 })
            {
                return 0;
            }

            measuring++;
            try
            {
                return Math.Max(1, across ? MaxContentWidth(box) : LayStory(box, 0, 0, Math.Max(1, depth)).Height);
            }
            finally
            {
                measuring--;
            }
        }

        // A box's start and length along its edge, as CSS Paged Media 3 section 5.3.2 resolves boxes of auto size: the
        // middle box centred and sized against twice the larger side box, the side boxes sharing the rest; without a middle
        // box, the two side boxes share the edge in proportion to what they ask for.
        static (double, double) Along(int index, (double Start, double Middle, double End) asked, double start, double length)
        {
            if (asked.Middle > 0)
            {
                double sides = 2 * Math.Max(asked.Start, asked.End);
                double middle = sides > 0 ? length * asked.Middle / (asked.Middle + sides) : length;
                double side = (length - middle) / 2;
                return index switch
                {
                    0 => (start, side),
                    1 => (start + side, middle),
                    _ => (start + length - side, side),
                };
            }

            double total = asked.Start + asked.End;
            double first = total > 0 ? length * asked.Start / total : length / 2;
            return index == 0 ? (start, first) : (start + first, length - first);
        }

        static (double, double, double, double) At((double Start, double Length) along, double y, double depth) => (along.Start, y, along.Length, depth);

        static (double, double, double, double) Down((double Start, double Length) along, double x, double width) => (x, along.Start, width, along.Length);
    }

    /// <summary>The width of a margin box's widest line laid out with no wrapping (CSS's max-content width).</summary>
    private double MaxContentWidth(MarginBox box)
    {
        double widest = 0;
        foreach (Block block in Flatten(box.Blocks))
        {
            if (block is not Paragraph paragraph)
            {
                return double.MaxValue / 4;
            }

            ParagraphContent content = ContentOf(paragraph, PageFieldText);
            double indents = content.Format.LeftIndent + content.Format.RightIndent;
            foreach (Egami.Fonts.Layout.TextLine line in content.Text.Layout(1e6f).Lines)
            {
                widest = Math.Max(widest, line.Width + indents);
            }
        }

        return widest;
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
            if (block is ContentControl { Box: null } control)
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

                default:
                    {
                        if (LayWhole(block, x, at, width, out double height) is { } box)
                        {
                            boxes.Add(box);
                        }

                        at += height;
                        break;
                    }
            }
        }

        return (boxes, at - top);
    }
}
