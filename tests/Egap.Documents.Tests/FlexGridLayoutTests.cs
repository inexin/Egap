using Egami.Pixels;

using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Flex and grid containers and boxed groups in the model and the page flow.</summary>
public sealed class FlexGridLayoutTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = FontSettings.Portable };

    [Fact]
    public void GrowFactorsShareALine()
    {
        (Document document, Body body) = Page(400, 400);
        FlexContainer flex = Add(body, new FlexContainer(document) { Layout = new FlexLayout { ColumnGap = 8 } });
        FlexItem[] items = [flex.AddItem("a"), flex.AddItem("b"), flex.AddItem("c")];
        double[] grow = [1, 2, 1];
        for (int i = 0; i < 3; i++)
        {
            items[i].Basis = TableWidth.Points(0);
            items[i].Grow = grow[i];
        }

        DocumentLayout layout = document.Layout(Isolated);

        LayoutRect[] boxes = [.. items.Select(i => ItemBounds(layout, i))];
        double space = 328 - 16;
        Assert.Equal(space / 4, boxes[0].Width, 0.01);
        Assert.Equal(space / 2, boxes[1].Width, 0.01);
        Assert.Equal(36, boxes[0].X, 0.01);
        Assert.Equal(36 + (space / 4) + 8, boxes[1].X, 0.01);
        Assert.Equal(boxes[0].Y, boxes[2].Y, 0.01);
        Assert.Equal(boxes[0].Height, boxes[1].Height, 0.01);
    }

    [Fact]
    public void ItemsWrapOntoLinesAndJustifyAndAlign()
    {
        (Document document, Body body) = Page(400, 400);
        FlexContainer flex = Add(body, new FlexContainer(document)
        {
            Layout = new FlexLayout { Wrap = FlexWrap.Wrap, ColumnGap = 10, RowGap = 4, JustifyContent = LayoutAlignment.SpaceBetween, AlignItems = LayoutAlignment.Center },
        });
        List<FlexItem> items = [];
        for (int i = 0; i < 5; i++)
        {
            FlexItem item = flex.AddItem("Item " + i);
            item.Basis = TableWidth.Points(120);
            items.Add(item);
        }

        // A taller first item: the others on its line are centred across it.
        items[0].AddParagraph("second line");

        DocumentLayout layout = document.Layout(Isolated);

        LayoutRect[] boxes = [.. items.Select(i => ItemBounds(layout, i))];
        Assert.Equal(boxes[0].Y, boxes[0].Y);
        Assert.True(boxes[2].Y > boxes[0].Bottom, "the third item starts the second line");
        Assert.True(boxes[4].Y > boxes[2].Bottom, "the fifth item starts the third line");
        Assert.Equal(36, boxes[0].X, 0.01);
        Assert.Equal(36 + 328 - 120, boxes[1].X, 0.01);
        Assert.Equal(boxes[0].Y + ((boxes[0].Height - boxes[1].Height) / 2), boxes[1].Y, 0.01);
        Assert.Equal(boxes[0].Bottom + 4, boxes[2].Y, 0.01);
    }

    [Fact]
    public void ShrinkingKeepsItemsAtTheirNarrowestContent()
    {
        (Document document, Body body) = Page(300, 400);
        FlexContainer flex = Add(body, new FlexContainer(document));
        FlexItem wide = flex.AddItem(new string('x', 20));
        FlexItem narrow = flex.AddItem("several short words that have to wrap onto more lines");

        DocumentLayout layout = document.Layout(Isolated);

        LayoutRect a = ItemBounds(layout, wide), b = ItemBounds(layout, narrow);
        Assert.Equal(228, a.Width + b.Width, 0.5);
        Assert.True(layout.FindBoxes(narrow.Blocks[0]).Select(x => x.Box).OfType<ParagraphBox>().Single().Lines.Count > 1, "the item that can wrap gives up its width");
        Assert.Equal(a.Height, b.Height, 0.01);
    }

    [Fact]
    public void ColumnContainersStackItemsAndBreakBetweenThem()
    {
        (Document document, Body body) = Page(300, 300);
        FlexContainer flex = Add(body, new FlexContainer(document)
        {
            Layout = new FlexLayout { Direction = FlexDirection.Column, AlignItems = LayoutAlignment.Center, RowGap = 6 },
            Box = BoxStyle.Padded(5) with { Background = DocumentColor.FromHex("EEEEEE") },
        });
        List<FlexItem> items = [.. Enumerable.Range(0, 30).Select(i => flex.AddItem("Line " + i))];

        DocumentLayout layout = document.Layout(Isolated);

        Assert.True(layout.Pages.Count >= 2);
        List<ContainerBox> parts = [.. layout.FindBoxes(flex).Select(b => b.Box).OfType<ContainerBox>()];
        Assert.True(parts.Count >= 2, $"{parts.Count} parts");
        Assert.True(parts[0].Continues && !parts[0].IsContinuation);
        Assert.True(parts[^1].IsContinuation && !parts[^1].Continues);
        LayoutRect first = ItemBounds(layout, items[0]);
        Assert.Equal(36 + 5, first.Y, 0.01);
        Assert.Equal(150, first.X + (first.Width / 2), 0.5);
        LayoutRect second = ItemBounds(layout, items[1]);
        Assert.Equal(first.Bottom + 6, second.Y, 0.01);
        Assert.Equal(layout.Pages.Count - 1, layout.PageOf(items[^1].Blocks[0])!.Index);
    }

    [Fact]
    public void GridTracksShareTheWidthAndRowsTakeTheirTallestItem()
    {
        (Document document, Body body) = Page(400, 400);
        GridContainer grid = Add(body, new GridContainer(document)
        {
            Layout = new GridLayout { Columns = [GridTrack.Points(100), GridTrack.Fraction(1), GridTrack.Fraction(2)], ColumnGap = 6, RowGap = 3 },
        });
        GridItem[] items = [.. Enumerable.Range(0, 6).Select(i => grid.AddItem("Cell " + i))];
        items[4].AddParagraph("taller");

        DocumentLayout layout = document.Layout(Isolated);

        LayoutRect[] boxes = [.. items.Select(i => ItemBounds(layout, i))];
        double rest = 328 - 100 - 12;
        Assert.Equal(100, boxes[0].Width, 0.01);
        Assert.Equal(rest / 3, boxes[1].Width, 0.01);
        Assert.Equal(rest * 2 / 3, boxes[2].Width, 0.01);
        Assert.Equal(36 + 100 + 6 + (rest / 3) + 6, boxes[2].X, 0.01);
        Assert.Equal(boxes[0].Bottom + 3, boxes[3].Y, 0.01);
        Assert.Equal(boxes[4].Height, boxes[3].Height, 0.01);
        Assert.True(boxes[4].Height > boxes[0].Height);
    }

    [Fact]
    public void GridItemsGoByAreaLineAndSpanAndTheRestFillTheHoles()
    {
        Document document = new();
        GridContainer grid = new(document)
        {
            Layout = new GridLayout { Columns = [GridTrack.Auto, GridTrack.Auto, GridTrack.Auto], Areas = ["head head head", "side main main"], AutoFlow = GridAutoFlow.RowDense },
        };
        GridItem main = grid.AddItem("main");
        main.Area = "main";
        GridItem wide = grid.AddItem("wide");
        wide.ColumnSpan = 2;
        GridItem small = grid.AddItem("small");
        GridItem last = grid.AddItem("last");
        last.Column = -2;
        last.Row = 3;

        GridPlacement placement = Paginator.PlaceGridItems(grid);

        Dictionary<GridItem, GridSlot> slots = placement.Slots.ToDictionary(s => s.Item);
        Assert.Equal((1, 1, 1, 2), (slots[main].Row, slots[main].Column, slots[main].RowSpan, slots[main].ColumnSpan));
        Assert.Equal((2, 2), (slots[last].Row, slots[last].Column));
        Assert.Equal((0, 0, 2), (slots[wide].Row, slots[wide].Column, slots[wide].ColumnSpan));
        Assert.Equal((0, 2), (slots[small].Row, slots[small].Column));
        Assert.Equal(3, placement.Columns);
        Assert.Equal(3, placement.Rows);
    }

    [Fact]
    public void GridsBreakBetweenRowsAndKeepSpannedRowsTogether()
    {
        (Document document, Body body) = Page(300, 300);
        GridContainer grid = Add(body, new GridContainer(document) { Layout = new GridLayout { Columns = [GridTrack.Fraction(1), GridTrack.Fraction(1)] } });
        List<GridItem> items = [];
        for (int i = 0; i < 40; i++)
        {
            items.Add(grid.AddItem("Cell " + i));
        }

        GridItem tall = items[18];
        tall.RowSpan = 2;

        DocumentLayout layout = document.Layout(Isolated);

        Assert.True(layout.Pages.Count >= 2);
        foreach (GridItem item in items)
        {
            LayoutRect bounds = ItemBounds(layout, item);
            Assert.True(bounds.Bottom <= 300 - 36 + 0.01, "every row fits its page");
        }

        LayoutRect tallBox = ItemBounds(layout, tall);
        Assert.True(tallBox.Height > ItemBounds(layout, items[0]).Height * 1.5);
    }

    [Fact]
    public void BoxedGroupsNarrowTheirTextAndBreakOpenAcrossPages()
    {
        (Document document, Body body) = Page(300, 300);
        ContentControl group = new(document)
        {
            Box = BoxStyle.Padded(12).WithBorder(new Border(BorderStyle.Single, 2, DocumentColor.FromHex("336699"))) with
            {
                CornerRadius = BoxCorners.All(8),
                Background = DocumentColor.FromHex("F0F4F8"),
                Shadow = new BoxShadow(2, 3, 6) { Opacity = 0.4 },
            },
        };
        for (int i = 0; i < 25; i++)
        {
            group.AddParagraph("Boxed line " + i);
        }

        body.Blocks.Add(group);

        DocumentLayout layout = document.Layout(Isolated);

        List<ContainerBox> parts = [.. layout.FindBoxes(group).Select(b => b.Box).OfType<ContainerBox>()];
        Assert.True(parts.Count >= 2, $"{parts.Count} parts");
        Assert.Equal(36, parts[0].Bounds.X, 0.01);
        Assert.Equal(228, parts[0].Bounds.Width, 0.01);
        Assert.Equal(300 - 36, parts[0].Bounds.Bottom, 0.01);
        ParagraphBox first = layout.FindBoxes(group.Blocks[0]).Select(b => b.Box).OfType<ParagraphBox>().Single();
        Assert.Equal(36 + 14, first.Bounds.X, 0.01);
        Assert.Equal(228 - 28, first.Bounds.Width, 0.01);
        Assert.Equal(36 + 14, first.Bounds.Y, 0.01);
        Assert.True(parts[1].IsContinuation);
        Assert.Equal(36, parts[1].Bounds.Y, 0.01);
        Assert.Equal(36, parts[1].Descendants().OfType<ParagraphBox>().Min(p => p.Bounds.Y), 0.01);

        // The background and the shadow are drawn: inside the box is the fill, just outside its lower right corner the shadow.
        Egami.Raster<Rgba8> page = layout.Pages[0].Render(72);
        Rgba8 inside = page.GetPixel(40, 200);
        Assert.Equal((0xF0, 0xF4, 0xF8), (inside.R, inside.G, inside.B));
        Egami.Raster<Rgba8> last = layout.Pages[^1].Render(72);
        LayoutRect end = parts[^1].Bounds;
        Rgba8 shadow = last.GetPixel((int)end.Right + 1, (int)end.Bottom + 1);
        Assert.True(shadow.R < 250, $"shadow below the box: {shadow}");
        Rgba8 corner = last.GetPixel((int)end.Right - 1, (int)end.Bottom - 1);
        Assert.True(corner.R < 0xF0 || corner.B != 0xF8, "the rounded corner leaves the square corner unfilled");
    }

    [Fact]
    public void ContainersNestInCellsHeadersAndEachOther()
    {
        (Document document, Body body) = Page(400, 400);
        Table table = body.AddTable(1, 1);
        FlexContainer inCell = new(document);
        inCell.AddItem("left").Grow = 1;
        inCell.AddItem("right");
        table.Rows[0].Cells[0].Blocks.Add(inCell);
        GridContainer outer = Add(body, new GridContainer(document) { Layout = new GridLayout { Columns = [GridTrack.Fraction(1), GridTrack.Fraction(1)] } });
        FlexContainer nested = new(document) { Layout = new FlexLayout { Direction = FlexDirection.Column } };
        nested.AddItem("one");
        nested.AddItem("two");
        outer.AddItem().Blocks.Add(nested);
        outer.AddItem("beside");

        DocumentLayout layout = document.Layout(Isolated);

        Assert.NotEmpty(layout.FindBoxes(inCell.Items[1]));
        LayoutRect one = ItemBounds(layout, nested.Items[0]), two = ItemBounds(layout, nested.Items[1]);
        Assert.Equal(one.Bottom, two.Y, 0.01);
        Assert.Contains("beside", document.GetText(), StringComparison.Ordinal);
        Assert.Contains("two", document.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ClonesKeepTheLayoutAndTheItems()
    {
        Document document = new();
        FlexContainer flex = new(document) { Layout = new FlexLayout { Wrap = FlexWrap.Wrap, RowGap = 3 }, Box = BoxStyle.Padded(4) };
        FlexItem item = flex.AddItem("x");
        item.Grow = 2;
        item.Order = -1;
        GridContainer grid = new(document) { Layout = new GridLayout { Columns = [GridTrack.MinMax(GridTrackSize.Points(20), GridTrackSize.Fraction(1))] } };
        GridItem cell = grid.AddItem("y");
        cell.Area = "a";
        cell.RowSpan = 2;

        FlexContainer flexCopy = flex.Clone();
        GridContainer gridCopy = grid.Clone();

        Assert.Equal(flex.Layout, flexCopy.Layout);
        Assert.Equal(flex.Box, flexCopy.Box);
        Assert.Equal((2.0, -1), (flexCopy.Items[0].Grow, flexCopy.Items[0].Order));
        Assert.Equal(("a", 2), (gridCopy.Items[0].Area, gridCopy.Items[0].RowSpan));
        Assert.Equal(grid.Layout.Columns, gridCopy.Layout.Columns);
        Assert.Throws<ArgumentOutOfRangeException>(() => item.Grow = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => cell.Row = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Layout = new GridLayout { Columns = [new GridTrack(GridTrackSize.Fraction(1), GridTrackSize.Fraction(1))] });
    }

    [Fact]
    public void TextBoxesFitTheirText()
    {
        (Document document, Body body) = Page(400, 400);
        Paragraph anchor = body.AddParagraph("anchor");
        Shape shape = new(document, ShapeKind.Rectangle, 0, 0)
        {
            FitToText = true,
            TextInsets = (4, 4, 4, 4),
            CornerRadius = 6,
            Fill = DocumentColor.FromHex("FFEEDD"),
            Floating = new FloatingPlacement { HorizontalAnchor = HorizontalAnchor.Margin, VerticalAnchor = VerticalAnchor.Margin, HorizontalOffset = 20, VerticalOffset = 30, Wrap = WrapMode.InFrontOfText },
        };
        shape.AddTextBox().AddParagraph("Stamp text");
        anchor.Inlines.Add(shape);

        DocumentLayout layout = document.Layout(Isolated);

        LayoutRect bounds = layout.FindBoxes(shape).Single().Box.Bounds;
        Assert.Equal((56.0, 66.0), (bounds.X, bounds.Y));
        Assert.InRange(bounds.Width, 30, 120);
        Assert.InRange(bounds.Height, 15, 40);
        layout.Pages[0].Render(72);
    }

    [Fact]
    public void DocxWritesContainersAsBorderlessTables()
    {
        (Document document, Body body) = Page(400, 400);
        FlexContainer flex = Add(body, new FlexContainer(document) { Layout = new FlexLayout { Wrap = FlexWrap.Wrap } });
        for (int i = 0; i < 3; i++)
        {
            flex.AddItem("Flex " + i).Basis = TableWidth.Percent(50);
        }

        flex.Items[0].Box = BoxStyle.Padded(4) with { Background = DocumentColor.FromHex("FFEEDD") };
        GridContainer grid = Add(body, new GridContainer(document) { Layout = new GridLayout { Columns = [GridTrack.Points(100), GridTrack.Fraction(1)] } });
        grid.AddItem("Wide").ColumnSpan = 2;
        GridItem tall = grid.AddItem("Tall");
        tall.RowSpan = 2;
        grid.AddItem("Right 1");
        grid.AddItem("Right 2");
        ContentControl group = new(document) { Box = BoxStyle.Padded(6).WithBorder(new Border(BorderStyle.Single, 1)) with { CornerRadius = BoxCorners.All(4) } };
        group.AddParagraph("Boxed");
        body.Blocks.Add(group);
        Paragraph anchor = body.AddParagraph("anchor");
        Shape shape = new(document, ShapeKind.Rectangle, 100, 40) { CornerRadius = 8, Shadow = new BoxShadow(2, 2, 4) { Opacity = 0.5 }, TextInsets = (3, 2, 3, 2), FitToText = true };
        shape.AddTextBox().AddParagraph("Box text");
        anchor.Inlines.Add(shape);

        using MemoryStream saved = new();
        DocxFormat.Save(document, saved);
        Document back = DocxFormat.Load(new MemoryStream(saved.ToArray()));

        List<Table> tables = [.. back.Sections[0].Body.Descendants<Table>()];
        Assert.Equal(3, tables.Count);
        Assert.Equal(2, tables[0].Rows.Count);
        Assert.Equal("Flex 2", tables[0].Rows[1].Cells[0].GetText().Trim());
        Assert.NotNull(tables[0].Rows[0].Cells[0].Format.Shading);
        Assert.Equal(3, tables[1].Rows.Count);
        Assert.Equal(2, tables[1].Rows[0].Cells[0].Format.GridSpan);
        Assert.Equal(VerticalMerge.Restart, tables[1].Rows[1].Cells[0].Format.VerticalMerge);
        Assert.Equal(VerticalMerge.Continue, tables[1].Rows[2].Cells[0].Format.VerticalMerge);
        Assert.Equal("Right 2", tables[1].Rows[2].Cells[1].GetText().Trim());
        Assert.Equal("Boxed", tables[2].GetText().Trim());

        // Two items placed on one cell share it in the table, so neither's text is lost.
        GridContainer stacked = new(document);
        stacked.AddItem("first").Column = 1;
        stacked.AddItem("second").Column = 1;
        stacked.Items[0].Row = stacked.Items[1].Row = 1;
        Table shared = ContainerTablesFor(stacked);
        Assert.Contains("first", shared.GetText(), StringComparison.Ordinal);
        Assert.Contains("second", shared.GetText(), StringComparison.Ordinal);
        Assert.NotNull(tables[2].GetAncestor<ContentControl>());

        Shape read = Assert.Single(back.Descendants<Shape>());
        Assert.Equal(ShapeKind.RoundedRectangle, read.Kind);
        Assert.Equal(8, read.CornerRadius, 0.01);
        Assert.Equal((2.0, 2.0, 4.0, 0.5), (Math.Round(read.Shadow!.OffsetX, 2), Math.Round(read.Shadow.OffsetY, 2), read.Shadow.Blur, read.Shadow.Opacity));
        Assert.Equal((3.0, 2.0, 3.0, 2.0), read.TextInsets);
        Assert.True(read.FitToText);
    }

    [Fact]
    public void TallRowsAndLinesSplitAcrossPagesBetweenLines()
    {
        (Document document, Body body) = Page(300, 300);
        body.AddParagraph("Before the grid");
        GridContainer grid = Add(body, new GridContainer(document) { Layout = new GridLayout { Columns = [GridTrack.Fraction(2), GridTrack.Fraction(1)], ColumnGap = 6 } });
        GridItem tall = grid.AddItem();
        tall.Box = BoxStyle.Padded(4).WithBorder(new Border(BorderStyle.Single, 1));
        for (int i = 0; i < 40; i++)
        {
            tall.AddParagraph("Line " + i);
        }

        Table table = tall.AddTable(12, 2);
        table.Rows[11].Cells[0].AddParagraph("Last row");
        GridItem side = grid.AddItem("Side note");

        DocumentLayout layout = document.Layout(Isolated);

        List<ItemBox> parts = [.. layout.FindBoxes(tall).Select(b => b.Box).OfType<ItemBox>()];
        Assert.True(parts.Count >= 3, $"{parts.Count} parts");
        Assert.True(parts[0].Continues && !parts[0].IsContinuation);
        Assert.True(parts[^1].IsContinuation && !parts[^1].Continues);
        Assert.Equal(300 - 36, parts[0].Bounds.Bottom, 0.01);
        Assert.Equal(0, layout.PageOf(tall.Blocks[0])!.Index);
        Assert.Equal(0, layout.PageOf(side.Blocks[0])!.Index);
        Assert.Single(layout.FindBoxes(side));
        Assert.True(layout.FindBoxes(table).Count >= 1);
        Assert.Equal(layout.Pages.Count - 1, layout.PageOf(table.Rows[11].Cells[0].Blocks[^1])!.Index);
        foreach (LineBox line in layout.Pages.SelectMany(page => page.BodyLines))
        {
            Assert.True(line.Bounds.Bottom <= 300 - 36 + 0.01, "every line inside the body");
        }
    }

    [Fact]
    public void SplitLinesAndRowsGoOnInTheNextColumn()
    {
        (Document document, Body body) = Page(400, 300);
        document.Sections[0].PageSetup.ColumnCount = 2;
        FlexContainer flex = Add(body, new FlexContainer(document));
        FlexItem item = flex.AddItem();
        GridContainer grid = Add(body, new GridContainer(document) { Layout = new GridLayout { Columns = [GridTrack.Fraction(1)] } });
        GridItem cell = grid.AddItem();
        for (int i = 0; i < 30; i++)
        {
            item.AddParagraph("Flex line " + i);
            cell.AddParagraph("Grid line " + i);
        }

        DocumentLayout layout = document.Layout(Isolated);

        foreach (BlockContainer split in new BlockContainer[] { item, cell })
        {
            List<(LayoutBox Box, PageLayout Page)> parts = [.. layout.FindBoxes(split).Select(b => (b.Box, b.Page))];
            Assert.True(parts.Count >= 2, $"{parts.Count} parts");
            foreach ((LayoutBox part, PageLayout page) in parts)
            {
                ContainerBox container = page.Boxes.SelectMany(b => b.Descendants().Prepend(b)).OfType<ContainerBox>().First(c => c.Children.Contains(part));
                Assert.InRange(part.Bounds.X, container.Bounds.X - 0.01, container.Bounds.Right + 0.01);
                foreach (LineBox line in part.Descendants().OfType<LineBox>())
                {
                    Assert.InRange(line.Bounds.X, container.Bounds.X - 0.01, container.Bounds.Right + 0.01);
                }
            }
        }
    }

    private static Table ContainerTablesFor(Block block) => Egap.Documents.Docx.ContainerTables.For(block, 468)!;

    private static T Add<T>(Body body, T block)
        where T : Block
    {
        body.Blocks.Add(block);
        return block;
    }

    private static LayoutRect ItemBounds(DocumentLayout layout, BlockContainer item)
    {
        return layout.FindBoxes(item).Select(b => b.Box).OfType<ItemBox>().Single().Bounds;
    }

    private static (Document, Body) Page(double width, double height)
    {
        Document document = new();
        Section section = document.AddSection();
        section.PageSetup.PageWidth = width;
        section.PageSetup.PageHeight = height;
        section.PageSetup.MarginTop = section.PageSetup.MarginBottom = section.PageSetup.MarginLeft = section.PageSetup.MarginRight = 36;
        return (document, section.Body);
    }
}
