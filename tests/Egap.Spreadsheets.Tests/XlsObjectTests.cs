
namespace Egap.Spreadsheets.Tests;

/// <summary>
/// The objects of an XLS file against Excel's own XLSX of it: <c>Oracle/xlsobjects/objects.xls</c> was made by Excel (see
/// <c>objects.txt</c> and <c>tools/excel-xls-objects-oracle.ps1</c>) with notes, hyperlinks, pictures, shapes, a group,
/// embedded charts, a chart sheet and defined names, and <c>objects.xlsx</c> is Excel's save of that file.
/// </summary>
public sealed class XlsObjectTests
{
    private static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Oracle", "xlsobjects", name);

    private static Workbook Excel() => Workbook.Load(Path("objects.xlsx"));

    [Fact]
    public void ReadsTheObjectsExcelSees() => AssertSame(Excel(), Workbook.Load(Path("objects.xls")));

    [Fact]
    public void SavesTheObjectsToXlsx()
    {
        using var stream = new MemoryStream();
        Workbook.Load(Path("objects.xls")).Save(stream, new WorkbookSaveOptions { Format = WorkbookFormat.Xlsx });
        stream.Position = 0;
        AssertSame(Excel(), Workbook.Load(stream));
    }

    [Fact]
    public void ReadsEachKindOfObject()
    {
        Workbook book = Workbook.Load(Path("objects.xls"));
        Worksheet data = book.Worksheets["Data"];
        Assert.Equal(3, data.Comments.Count);
        Assert.Equal("Two\nlines", data.Comments[CellAddress.Parse("C4")]!.Text.Split(':', 2)[^1].TrimStart('\n'));
        Assert.Equal(4, data.Hyperlinks.Count);
        Assert.Contains(data.Hyperlinks, h => h.ToolTip == "Example tip");
        Assert.Contains(data.Hyperlinks, h => h.Location == "Data!A1");
        Assert.Equal(10, data.Drawings.Count(d => d.Kind == SheetDrawingKind.Chart));

        Worksheet art = book.Worksheets["Art"];
        Assert.Equal(["image/png", "image/jpeg"], art.Drawings.Where(d => d.Kind == SheetDrawingKind.Picture).Select(d => d.ImageContentType));
        Assert.Contains(art.Drawings, d => d.Kind == SheetDrawingKind.Shape && d.Text == "Box text");
        Assert.Single(art.Drawings, d => d.Kind == SheetDrawingKind.Group);

        ChartSheet page = Assert.Single(book.ChartSheets);
        Assert.Equal("Chart Sheet", page.Name);
        Assert.Contains(book.Names, n => n.Name == "Hidden_Name" && n.Hidden);
        Assert.Contains(book.Names, n => n.Name == "Local" && n.Sheet == data);
    }

    private static void AssertSame(Workbook excel, Workbook ours)
    {
        foreach (DefinedName name in excel.Names)
        {
            DefinedName mine = Assert.Single(ours.Names, n => n.Name.Equals(name.Name, StringComparison.OrdinalIgnoreCase) && n.Sheet?.Name == name.Sheet?.Name);
            Assert.Equal(name.Formula, mine.Formula);
            Assert.Equal(name.Hidden, mine.Hidden);
        }

        Assert.Equal(excel.Names.Count, ours.Names.Count);
        Assert.Equal(excel.ChartSheets.Select(c => (c.Name, c.Chart?.Type, c.Chart?.Series.Count)), ours.ChartSheets.Select(c => (c.Name, c.Chart?.Type, c.Chart?.Series.Count)));
        foreach (Worksheet sheet in excel.Worksheets)
        {
            Worksheet mine = ours.Worksheets[sheet.Name];
            Assert.Equal(sheet.Comments.Select(c => (c.Cell, c.Author, c.Text)), mine.Comments.Select(c => (c.Cell, c.Author, c.Text)));
            Assert.Equal(sheet.Hyperlinks.Select(h => (h.Range.ToString(), h.Address, h.Location, h.ToolTip)).Order(), mine.Hyperlinks.Select(h => (h.Range.ToString(), h.Address, h.Location, h.ToolTip)).Order());
            Assert.Equal(sheet.Drawings.Count, mine.Drawings.Count);
            List<SheetDrawing> left = [.. mine.Drawings];
            foreach (SheetDrawing drawing in sheet.Drawings)
            {
                SheetDrawing match = left.First(d => d.Kind == drawing.Kind && d.From == drawing.From);
                left.Remove(match);
                Assert.Equal(drawing.To, match.To);
                Assert.Equal(drawing.AnchorKind, match.AnchorKind);
                Assert.Equal(drawing.Kind is SheetDrawingKind.Shape ? drawing.Text ?? "" : "", drawing.Kind is SheetDrawingKind.Shape ? match.Text ?? "" : "");
                if (drawing.Kind == SheetDrawingKind.Picture)
                {
                    Assert.Equal(drawing.ImageContentType, match.ImageContentType);
                    Assert.Equal(drawing.ImageData, match.ImageData);
                }

                if (drawing.Kind == SheetDrawingKind.Chart)
                {
                    AssertSame(drawing.Chart!, match.Chart!);
                }
            }
        }
    }

    private static void AssertSame(Documents.Charts.Chart excel, Documents.Charts.Chart ours)
    {
        Assert.Equal(excel.Type, ours.Type);
        Assert.Equal(excel.Title.Text ?? "", ours.Title.Text ?? "");
        Assert.Equal(excel.HasLegend, ours.HasLegend);
        Assert.Equal(excel.Series.Select(s => (s.Values.Reference, s.Categories.Reference, s.NameReference)), ours.Series.Select(s => (s.Values.Reference, s.Categories.Reference, s.NameReference)));
        Assert.Equal(excel.Plots.Select(p => (p.Type, p.Is3D, p.Grouping, p.GapWidth, p.Overlap ?? 0, p.Series.Count)), ours.Plots.Select(p => (p.Type, p.Is3D, p.Grouping, p.GapWidth, p.Overlap ?? 0, p.Series.Count)));
    }
}
