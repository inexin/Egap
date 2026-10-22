using Egap.Documents.Layout;

namespace Egap.Documents.Html.Tests;

/// <summary>CSS flexbox, grid, boxes with rounded corners and shadows, and positioned boxes read from HTML.</summary>
public sealed class FlexGridHtmlTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = FontSettings.Portable };

    [Fact]
    public void FlexContainersAndTheirItemsAreRead()
    {
        Document document = HtmlFormat.Parse("""
            <div style="display: flex; flex-wrap: wrap; gap: 8px 12px; justify-content: space-between; align-items: center; padding: 6px; background: #eee; border-radius: 4px">
              <div style="flex: 1">One</div>
              <span style="flex: 2 0 100px; order: -1">Two</span>
              Three
              <p style="flex-basis: 25%; align-self: flex-end; margin: 3px">Four</p>
            </div>
            """);

        FlexContainer flex = Assert.Single(document.Descendants<FlexContainer>());
        Assert.Equal(FlexWrap.Wrap, flex.Layout.Wrap);
        Assert.Equal(LayoutAlignment.SpaceBetween, flex.Layout.JustifyContent);
        Assert.Equal(LayoutAlignment.Center, flex.Layout.AlignItems);
        Assert.Equal((6.0, 9.0), (flex.Layout.RowGap, flex.Layout.ColumnGap));
        Assert.Equal(4.5, flex.Box!.PaddingLeft);
        Assert.Equal(3, flex.Box.CornerRadius.TopLeft);
        Assert.NotNull(flex.Box.Background);
        Assert.Equal(4, flex.Items.Count);
        Assert.Equal((1.0, 1.0, TableWidth.Points(0)), (flex.Items[0].Grow, flex.Items[0].Shrink, flex.Items[0].Basis));
        Assert.Equal((2.0, 0.0, -1), (flex.Items[1].Grow, flex.Items[1].Shrink, flex.Items[1].Order));
        Assert.Equal(75, flex.Items[1].Basis.Value, 0.01);
        Assert.Equal("Three", flex.Items[2].GetText().Trim());
        Assert.Equal(TableWidth.Percent(25), flex.Items[3].Basis);
        Assert.Equal(LayoutAlignment.End, flex.Items[3].AlignSelf);
        Assert.Equal(2.25, flex.Items[3].Box!.MarginTop);

        DocumentLayout layout = document.Layout(Isolated);
        Assert.Equal(4, layout.Pages[0].Boxes.SelectMany(b => b.Descendants()).OfType<ItemBox>().Count());
    }

    [Fact]
    public void GridTemplatesAreasAndPlacementsAreRead()
    {
        Document document = HtmlFormat.Parse("""
            <style>
              .page { display: grid; grid-template-columns: 120px repeat(2, minmax(0, 1fr)) fit-content(80px); grid-template-rows: auto 40px;
                      grid-template-areas: "logo head head head" "logo main main side"; column-gap: 4px; row-gap: 2px; justify-items: start }
              .logo { grid-area: logo } .main { grid-area: main } .wide { grid-column: 2 / span 2; grid-row: 3 } .end { grid-column: 1 / -1 }
            </style>
            <div class="page"><div class="logo">L</div><div class="main">M</div><div class="wide">W</div><div class="end">E</div><div style="place-self: center end">P</div></div>
            """);

        GridContainer grid = Assert.Single(document.Descendants<GridContainer>());
        GridLayout layout = grid.Layout;
        Assert.Equal(
            [GridTrack.Points(90), GridTrack.MinMax(GridTrackSize.Points(0), GridTrackSize.Fraction(1)), GridTrack.MinMax(GridTrackSize.Points(0), GridTrackSize.Fraction(1)), GridTrack.MinMax(GridTrackSize.Auto, GridTrackSize.Points(60))],
            layout.Columns);
        Assert.Equal([GridTrack.Auto, GridTrack.Points(30)], layout.Rows);
        Assert.Equal(2, layout.Areas.Count);
        Assert.Equal((1.5, 3.0), (layout.RowGap, layout.ColumnGap));
        Assert.Equal(LayoutAlignment.Start, layout.JustifyItems);
        Assert.Equal("logo", grid.Items[0].Area);
        Assert.Equal((2, 2, 3), (grid.Items[2].Column!.Value, grid.Items[2].ColumnSpan, grid.Items[2].Row!.Value));
        Assert.Equal((1, 4), (grid.Items[3].Column!.Value, grid.Items[3].ColumnSpan));
        Assert.Equal((LayoutAlignment.Center, LayoutAlignment.End), (grid.Items[4].AlignSelf!.Value, grid.Items[4].JustifySelf!.Value));

        DocumentLayout pages = document.Layout(Isolated);
        LayoutRect logo = Bounds(pages, grid.Items[0]);
        LayoutRect main = Bounds(pages, grid.Items[1]);
        Assert.True(main.Y > logo.Y, "main is in the second row, the logo spans both");
        Assert.True(logo.Bottom >= main.Bottom - 0.01);
        Assert.True(main.X > logo.Right, "main is right of the logo column");
    }

    [Fact]
    public void BoxesWithRoundedCornersOrShadowsBecomeGroupsWithABox()
    {
        Document document = HtmlFormat.Parse("""
            <p>Before</p>
            <div style="margin: 10px 0; padding: 8px 12px; border: 1px solid #369; border-radius: 6px; box-shadow: 0 2px 4px rgba(0, 0, 0, 0.25), inset 0 0 1px red; background: #f8f8f8; width: 200px">
              <h2>Card</h2><p>Body text</p>
            </div>
            <div style="border: 1px solid black">Plain border</div>
            """);

        ContentControl card = Assert.Single(document.Descendants<ContentControl>());
        BoxStyle box = card.Box!;
        Assert.Equal((6.0, 9.0), (box.PaddingTop, box.PaddingLeft));
        Assert.Equal(4.5, box.CornerRadius.BottomRight);
        Assert.Equal((0.0, 1.5, 3.0), (box.Shadow!.OffsetX, box.Shadow.OffsetY, box.Shadow.Blur));
        Assert.Equal(0.25, box.Shadow.Opacity, 0.01);
        Assert.Equal(150 + 18 + 1.5, box.Width.Value, 0.01);
        Assert.Equal(2, card.Blocks.Count);
        Assert.Equal(0, ((Paragraph)card.Blocks[1]).Format.LeftIndent ?? 0);
        Assert.Null(((Paragraph)card.Blocks[1]).Format.BorderLeft);

        // A box without corners or a shadow stays as paragraph borders.
        Assert.NotNull(document.Sections[0].Body.Blocks.OfType<Paragraph>().Last().Format.BorderLeft);
        document.Layout(Isolated).Pages[0].Render(72);
    }

    [Fact]
    public void PositionedBoxesBecomeFloatingTextBoxes()
    {
        Document document = HtmlFormat.Parse("""
            <p>First</p>
            <div style="position: absolute; top: 20px; right: 0; width: 160px; padding: 4px; border: 2px solid #c00; border-radius: 6px; background: #fee; box-shadow: 1px 1px 3px #888">Paid</div>
            <div style="position: relative"><p>Anchor</p><div style="position: absolute; left: 10px; top: 5px">Inside</div></div>
            <div style="position: fixed; bottom: 0; left: 0; z-index: -1">Every page</div>
            <p>Last</p>
            """);

        Shape[] shapes = [.. document.Descendants<Shape>()];
        Assert.Equal(3, shapes.Length);
        Shape paid = shapes.Single(s => s.TextBox!.GetText().Trim() == "Paid");
        Assert.Equal((ShapeKind.RoundedRectangle, 4.5), (paid.Kind, paid.CornerRadius));
        Assert.Equal(120 + 3 + 6, paid.Width, 0.01);
        Assert.Equal((VerticalAnchor.Margin, 15.0), (paid.Floating!.VerticalAnchor, paid.Floating.VerticalOffset));
        Assert.Equal(HorizontalAnchor.Margin, paid.Floating.HorizontalAnchor);
        Assert.Equal(document.Sections[0].Body.Paragraphs.First(), paid.GetAncestor<Paragraph>());
        Assert.NotNull(paid.Shadow);
        Assert.True(paid.FitToText);
        Assert.Equal(WrapMode.InFrontOfText, paid.Floating.Wrap);

        Shape inside = shapes.Single(s => s.TextBox!.GetText().Trim() == "Inside");
        Assert.Equal(VerticalAnchor.Paragraph, inside.Floating!.VerticalAnchor);
        Assert.Equal("Anchor", inside.GetAncestor<Paragraph>()!.Runs.First().Text);

        Shape every = shapes.Single(s => s.TextBox!.GetText().Trim() == "Every page");
        Assert.IsType<HeaderFooter>(every.GetAncestor<Story>());
        Assert.Equal(WrapMode.BehindText, every.Floating!.Wrap);

        DocumentLayout layout = document.Layout(Isolated);
        LayoutRect bounds = layout.FindBoxes(paid).Single().Box.Bounds;
        Assert.Equal(612 - 28.8, bounds.Right, 0.5);
        Assert.Equal(28.8 + 15, bounds.Y, 0.5);
    }

    [Fact]
    public void PositionedBoxesAndIdsInsideBoxesAndItemsAreKept()
    {
        Document document = HtmlFormat.Parse("""
            <div id="card" style="border-radius: 8px"><span style="position: absolute; left: 0; width: 40px">NEW</span></div>
            <div style="display: flex"><div id="cell"><span style="position: absolute; left: 0; width: 40px">HOT</span></div></div>
            <p>After</p>
            """);

        Assert.Equal(2, document.Descendants<Shape>().Count(s => s.Floating is not null));
        Assert.Contains(document.Descendants<BookmarkStart>(), b => b.Name == "card");
        Assert.Contains(document.Descendants<BookmarkStart>(), b => b.Name == "cell");
    }

    [Fact]
    public void PositionedBoxesInListItemsAndNotesAreWritten()
    {
        Document document = HtmlFormat.Parse("""
            <ul><li>Item<span style="position: absolute; left: 0; width: 40px">BOX</span></li></ul>
            """);
        string html = document.ToHtml();

        Assert.Contains("BOX", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("BOX", StringComparison.Ordinal) < html.IndexOf("</ul>", StringComparison.Ordinal));
    }

    [Fact]
    public void ContainersAndBoxesWriteBackAsCss()
    {
        Document document = HtmlFormat.Parse("""
            <div style="display: flex; flex-direction: column; gap: 6px; align-items: center"><div style="flex: 1 0 auto; order: 2">A</div><div style="align-self: flex-end">B</div></div>
            <div style="display: grid; grid-template-columns: 50px 1fr minmax(20px, 2fr); grid-template-areas: 'x y y'"><div style="grid-area: y">G</div><div style="grid-column: 1 / span 2">H</div></div>
            <div style="border-radius: 3px; box-shadow: 0 1px 2px rgba(0,0,0,.5); padding: 2px; width: 50%">Card</div>
            """);

        using MemoryStream html = new();
        HtmlFormat.Save(document, html);
        Document back = HtmlFormat.Load(new MemoryStream(html.ToArray()));

        FlexContainer flex = Assert.Single(back.Descendants<FlexContainer>());
        Assert.Equal(document.Descendants<FlexContainer>().Single().Layout, flex.Layout);
        Assert.Equal((1.0, 0.0, 2), (flex.Items[0].Grow, flex.Items[0].Shrink, flex.Items[0].Order));
        Assert.Equal(LayoutAlignment.End, flex.Items[1].AlignSelf);
        GridContainer grid = Assert.Single(back.Descendants<GridContainer>());
        Assert.Equal(document.Descendants<GridContainer>().Single().Layout.Columns, grid.Layout.Columns);
        Assert.Equal(["x y y"], grid.Layout.Areas);
        Assert.Equal("y", grid.Items[0].Area);
        Assert.Equal((1, 2), (grid.Items[1].Column!.Value, grid.Items[1].ColumnSpan));
        ContentControl card = Assert.Single(back.Descendants<ContentControl>());
        Assert.Equal(document.Descendants<ContentControl>().Single().Box!.Shadow, card.Box!.Shadow);
        Assert.Equal(TableWidth.Percent(50), card.Box.Width);
    }

    [Fact]
    public void DeeplyNestedContainersAreCutOffAndLaidOutQuickly()
    {
        string html = string.Concat(Enumerable.Repeat("<div style='display:flex'><div style='display:grid;grid-template-columns:1fr 1fr'>", 2000)) + "deep"
            + string.Concat(Enumerable.Repeat("</div></div>", 2000));
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        Document document = HtmlFormat.Parse(html, out IReadOnlyList<ConversionWarning> warnings);
        DocumentLayout layout = document.Layout(Isolated);

        Assert.Contains("deep", document.GetText(), StringComparison.Ordinal);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.LayoutApproximated);
        Assert.NotEmpty(layout.Pages);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"{watch.Elapsed}");
    }

    [Fact]
    public void HugeGridsArePlacedQuickly()
    {
        string items = string.Concat(Enumerable.Range(0, 3000).Select(i => $"<div style='grid-column: span {(i % 7) + 40}; grid-row: span {(i % 5) + 30}'>x</div>"));
        string html = $"<div style='display:grid; grid-template-columns: repeat(5000, 1px); grid-auto-flow: dense'>{items}</div>";
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        Document document = HtmlFormat.Parse(html);
        document.Layout(Isolated);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"{watch.Elapsed}");
    }

    [Fact]
    public void AFluentReportSavesAsHtml()
    {
        Document report = Reports.Report.Create(r => r.Page(p =>
        {
            p.Header().Row(row =>
            {
                row.Relative().Text("Acme");
                row.Auto().Text("Invoice");
            });
            p.Content().Column(col =>
            {
                col.Spacing(6);
                col.Item().Background("EEEEEE").CornerRadius(4).Padding(6).Text("Boxed");
                col.Item().Table(t => t.Columns(2).Header("A", "B").Rows([1, 2], i => [i, i * 2]));
            });
        }));

        Document back = HtmlFormat.Parse(report.ToHtml());

        Assert.Contains("Boxed", back.GetText(), StringComparison.Ordinal);
        Assert.Single(back.Descendants<ContentControl>(), c => c.Box?.Background is not null && !c.Box.CornerRadius.IsSquare);
        Assert.Equal(3, Assert.Single(back.Descendants<Table>()).Rows.Count);
    }

    [Fact]
    public void MarginsBreaksRulesAndLineHeightsAsBrowsersHaveThem()
    {
        Document document = HtmlFormat.Parse("""
            <p style="margin: 0 0 12px">Intro</p>
            <table><tr><td style="border-bottom: 1px solid black; padding: 2px">cell</td></tr></table>
            <div style="display: flex; flex-direction: column; break-before: page; break-after: page">
              <h3 style="margin: 0 0 6px">Title</h3><p style="margin: 8px 0">Body</p>
            </div>
            <div style="display: grid">After the break</div>
            <hr style="margin: 6px 0">
            <p style="line-height: 2">Tall lines</p>
            """);

        Block[] blocks = [.. document.Sections[0].Body.Blocks];
        Paragraph intro = (Paragraph)blocks[0];
        Assert.Equal(9, intro.Format.SpaceAfter);
        Table table = Assert.IsType<Table>(blocks[1]);
        // Separate borders add their whole width to the cell's height.
        Assert.Equal(1.5 + 0.75, table.Rows[0].Cells[0].Format.MarginBottom!.Value, 0.01);
        Assert.Equal(1.5, table.Rows[0].Cells[0].Format.MarginTop!.Value, 0.01);
        Assert.True(((Paragraph)blocks[2]).Format.PageBreakBefore);
        FlexContainer flex = Assert.IsType<FlexContainer>(blocks[3]);
        Assert.Equal(4.5, flex.Items[0].Box!.MarginBottom);
        Assert.Equal(6, flex.Items[1].Box!.MarginBottom);
        Assert.True(((Paragraph)blocks[4]).Format.PageBreakBefore);
        Assert.IsType<GridContainer>(blocks[5]);
        Paragraph rule = (Paragraph)blocks[6];
        Assert.Equal(LineSpacingRule.Exactly, rule.Format.LineSpacingRule);
        Assert.Equal(4.5, rule.Format.SpaceBefore);
        Assert.Equal(12, ((Paragraph)blocks[7]).Format.SpaceBefore);
        Assert.Equal(LineSpacingRule.AtLeastCentered, ((Paragraph)blocks[7]).Format.LineSpacingRule);

        DocumentLayout layout = document.Layout(Isolated);
        Assert.Equal(3, layout.Pages.Count);
        LineBox tall = layout.Pages[2].BodyLines.Single(l => l.Text.StartsWith("Tall", StringComparison.Ordinal));
        Assert.InRange(tall.Baseline - tall.Bounds.Y, (tall.Bounds.Height / 2) - 1, tall.Bounds.Height * 0.75);
    }

    private static LayoutRect Bounds(DocumentLayout layout, BlockContainer item)
    {
        return layout.FindBoxes(item).Select(b => b.Box).OfType<ItemBox>().Single().Bounds;
    }
}
