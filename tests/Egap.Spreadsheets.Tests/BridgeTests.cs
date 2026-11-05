using System.Globalization;

using Egap.Documents;
using Egap.Documents.Pdf;
using Egap.Pdf;

using DocumentCell = Egap.Documents.Cell;

namespace Egap.Spreadsheets.Tests;

/// <summary>Spreadsheets as documents and tables as spreadsheets (Build Plan task 19).</summary>
public sealed class BridgeTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("egap-bridge").FullName;

    public BridgeTests()
    {
        SpreadsheetFormat.Register();
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private static string[][] Texts(Table table)
    {
        return [.. table.Rows.Select(row => row.Cells.Select(cell => cell.GetText().TrimEnd()).ToArray())];
    }

    private static Table OnlyTable(Document document, int section = 0)
    {
        return Assert.Single(document.Sections[section].Body.Tables);
    }

    private string Saved(Workbook book, string name = "book.xlsx")
    {
        string path = Path.Combine(folder, name);
        book.Save(path);
        return path;
    }

    [Fact]
    public void EachVisibleSheetIsASectionWithAHeadingAndATableOfDisplayedValues()
    {
        Workbook book = Workbook.Create();
        Worksheet first = book.Worksheets[0];
        first.Name = "Budget";
        first["A1"].Value = "Item";
        first["B1"].Value = "Cost";
        first["A2"].Value = "Rent";
        first["B2"].Value = 1234.5;
        first["B2"].NumberFormat = "#,##0.00";
        book.Worksheets.Add("Secret").Visibility = SheetVisibility.Hidden;
        book.Worksheets.Add("Notes")["A1"].Value = "Fine";

        Document document = Document.Load(Saved(book));

        Assert.Equal(2, document.Sections.Count);
        Paragraph heading = Assert.Single(document.Sections[0].Body.Paragraphs);
        Assert.Equal("Budget", heading.GetText().TrimEnd());
        Assert.NotNull(heading.Style);
        Assert.Equal([["Item", "Cost"], ["Rent", "1,234.50"]], Texts(OnlyTable(document)));
        Assert.Equal(ParagraphAlignment.Right, OnlyTable(document).Rows[1].Cells[1].Paragraphs.Single().Format.Alignment);
        Assert.Equal([["Fine"]], Texts(OnlyTable(document, 1)));
    }

    [Fact]
    public void AWorkbookSavedWithoutValuesIsCalculatedAndOneWithValuesKeepsThem()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = 2;
        sheet["A2"].Formula = "A1*21";
        Assert.Equal([["2"], ["42"]], Texts(OnlyTable(Document.Load(Saved(book)))));

        book.CalculateFull();
        sheet.Cells.SetValue(0, 0, 3);
        string withValues = Saved(book, "values.xlsx");
        Assert.Equal([["3"], ["42"]], Texts(OnlyTable(Document.Load(withValues))));
    }

    [Fact]
    public void HiddenRowsAndColumnsAndWhatShowsNothingAreLeftOut()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "a";
        sheet["B1"].Value = "hidden column";
        sheet.Columns[1].Hidden = true;
        sheet["A2"].Value = "hidden row";
        sheet.Rows[1].Hidden = true;
        sheet["D5"].Value = "d";
        sheet["C9"].UpdateStyle(style => style with { Font = style.Font with { Bold = true } });

        Assert.Equal([["a"], ["", "d"]], Texts(OnlyTable(book.ToDocument())));
    }

    [Fact]
    public void AnEmptySheetHasItsHeadingAndNoTable()
    {
        Document document = Workbook.Create().ToDocument();
        Assert.Empty(document.Sections[0].Body.Tables);
        Assert.Equal("Sheet1", Assert.Single(document.Sections[0].Body.Paragraphs).GetText().TrimEnd());
    }

    [Fact]
    public void ValuesFarApartStayASmallTable()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "first";
        sheet["XFD1048576"].Value = "last";

        Table table = OnlyTable(book.ToDocument());

        Assert.Equal([["first"], ["", "last"]], Texts(table));
    }

    [Fact]
    public void MergedCellsSpanTheColumnsAndRowsThatAreKept()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "wide and tall";
        sheet["C1"].Value = "c1";
        sheet["C2"].Value = "c2";
        sheet["C3"].Value = "c3";
        sheet["D1"].Value = "whole column";
        sheet["A3"].Value = "a3";
        sheet["B3"].Value = "b3";
        sheet["B2"].Value = "covered";
        sheet.MergedCells.Add(RangeAddress.Parse("A1:B2"));
        sheet.MergedCells.Add(RangeAddress.Parse("D1:D1048576"));

        Table table = OnlyTable(book.ToDocument());

        DocumentCell origin = table.Rows[0].Cells[0];
        Assert.Equal(2, origin.Format.GridSpan);
        Assert.Equal(VerticalMerge.Restart, origin.Format.VerticalMerge);
        Assert.Equal(VerticalMerge.Continue, table.Rows[1].Cells[0].Format.VerticalMerge);
        Assert.Equal(2, table.Rows[1].Cells[0].Format.GridSpan);
        Assert.Equal(VerticalMerge.Continue, table.Rows[2].Cells[^1].Format.VerticalMerge);
        Assert.Equal([["wide and tall", "c1", "whole column"], ["", "c2", ""], ["a3", "b3", "c3", ""]], Texts(table));
    }

    [Fact]
    public void BoldItalicAlignmentAndLinksCarryOver()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "bold";
        sheet["A1"].UpdateStyle(style => style with { Font = style.Font with { Bold = true, Italic = true } });
        sheet["B1"].Value = "centre";
        sheet["B1"].UpdateStyle(style => style with { Alignment = style.Alignment with { Horizontal = CellHorizontalAlignment.Center } });
        sheet["C1"].Value = "site";
        sheet.Hyperlinks.Add("C1", "https://example.com/");

        Row row = OnlyTable(Document.Load(Saved(book))).Rows[0];

        Run run = Assert.IsType<Run>(row.Cells[0].Paragraphs.Single().Inlines.Single());
        Assert.True(run.Format.Bold);
        Assert.True(run.Format.Italic);
        Assert.Equal(ParagraphAlignment.Center, row.Cells[1].Paragraphs.Single().Format.Alignment);
        Assert.Equal("https://example.com/", Assert.IsType<Egap.Documents.Hyperlink>(row.Cells[2].Paragraphs.Single().Inlines.Single()).Address);
    }

    [Fact]
    public void ATableHeaderRowOnTopIsAHeaderRow()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "Name";
        sheet["A2"].Value = "x";
        sheet.Tables.Add("A1:A2");

        Assert.True(OnlyTable(book.ToDocument()).Rows[0].Format.IsHeader);
    }

    [Fact]
    public void CommentsAndDrawingsAreWarnedAbout()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "a";
        sheet.Comments.Add("A1", "note");

        book.ToDocument(out IReadOnlyList<ConversionWarning> warnings);

        Assert.Equal(ConversionWarningKind.CommentsDropped, Assert.Single(warnings).Kind);
    }

    [Fact]
    public void CsvLoadsAsOneSectionWithItsFieldsAsWritten()
    {
        string path = Path.Combine(folder, "prices.csv");
        File.WriteAllText(path, "Item;Price;Code\nTea;1.50;007\n");

        Document document = Document.Load(path);

        Assert.Equal(DocumentFormatKind.Csv, Document.Identify(path));
        Assert.Empty(Assert.Single(document.Sections).Body.Paragraphs);
        Assert.Equal("Item|Price|Code\nTea|1.50|007", string.Join("\n", Texts(OnlyTable(document)).Select(row => string.Join("|", row))));
    }

    [Fact]
    public void TsvAndAnEmptyCsvLoad()
    {
        string tsv = Path.Combine(folder, "a.tsv");
        File.WriteAllText(tsv, "x\ty\n");
        Assert.Equal([["x", "y"]], Texts(OnlyTable(Document.Load(tsv))));

        string empty = Path.Combine(folder, "empty.csv");
        File.WriteAllText(empty, "");
        Assert.Empty(Assert.Single(Document.Load(empty).Sections).Body.Tables);
    }

    [Fact]
    public void ACsvTooWideForASheetIsNotADocument()
    {
        string path = Path.Combine(folder, "wide.csv");
        File.WriteAllText(path, new string(',', CellAddress.MaxColumns + 1));
        Assert.Throws<DocumentException>(() => Document.Load(path));
    }

    [Fact]
    public void LoadOptionsReachTheWorkbookReader()
    {
        string path = Saved(Workbook.Create());
        Assert.Throws<DocumentLimitException>(() => Document.Load(path, new DocumentLoadOptions { Limits = new DocumentLimits { MaxEntries = 1 } }));
    }

    [Theory]
    [InlineData("a.xlsx")]
    [InlineData("a.csv")]
    public void SavingADocumentAsASpreadsheetPointsAtFromTables(string name)
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => new Document().Save(Path.Combine(folder, name)));
        Assert.Contains("Workbook.FromTables", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkbookConvertsToMoreThanTheCellLimitWithAWarning()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        for (int i = 0; i < 2100; i++)
        {
            sheet[i, i].Value = i;
        }

        book.ToDocument(out IReadOnlyList<ConversionWarning> warnings);

        Assert.Equal(ConversionWarningKind.TableSimplified, Assert.Single(warnings).Kind);
    }

    private static Document DocumentWith(params string[][] rows)
    {
        Document document = new();
        Table table = document.AddSection().Body.AddTable(0, 0);
        foreach (string[] row in rows)
        {
            Row added = table.AddRow();
            foreach (string text in row)
            {
                added.AddCell().Paragraphs.Single().AddRun(text);
            }
        }

        return document;
    }

    [Fact]
    public void DocumentTablesBecomeSheetsWithTypedValues()
    {
        Document document = DocumentWith(["Name", "Count", "When"], ["Tea", "1,200", "2024-03-15"], ["Code", "007", "=1+1"]);
        document.Sections[0].Body.Tables.Single().Rows[0].Format.IsHeader = true;
        document.Sections[0].Body.AddParagraph("Between");
        document.Sections[0].Body.AddTable(1, 1).Rows[0].Cells[0].AddParagraph("Second");

        Workbook book = Workbook.FromTables(document);

        Assert.Equal(["Table 1", "Table 2"], book.Worksheets.Select(sheet => sheet.Name));
        Worksheet sheet = book.Worksheets[0];
        Assert.True(sheet["A1"].Style.Font.Bold);
        Assert.False(sheet["A2"].Style.Font.Bold);
        Assert.Equal(1200d, sheet["B2"].Value.Number);
        Assert.Equal("1,200", sheet["B2"].Text);
        Assert.Equal(CellValueKind.Number, sheet["C2"].ValueKind);
        Assert.Equal("2024-03-15", sheet["C2"].Text);
        Assert.Equal(CellValueKind.Text, sheet["B3"].ValueKind);
        Assert.False(sheet["C3"].HasFormula);
        Assert.Equal("Second", book.Worksheets[1]["A1"].Text);
    }

    [Fact]
    public void TypeDetectionFollowsTheOptions()
    {
        Document document = DocumentWith(["1.234,5"]);
        Assert.Equal(1234.5, Workbook.FromTables(document, new TableImportOptions { Culture = CultureInfo.GetCultureInfo("de-DE") }).Worksheets[0]["A1"].Value.Number);
        Assert.Equal(CellValueKind.Text, Workbook.FromTables(document).Worksheets[0]["A1"].ValueKind);

        Assert.Equal(CellValueKind.Text, Workbook.FromTables(DocumentWith(["12"]), new TableImportOptions { DetectTypes = false }).Worksheets[0]["A1"].ValueKind);
    }

    [Fact]
    public void GridSpansAndVerticalMergesBecomeMergedCells()
    {
        Document document = DocumentWith(["wide", "c"], ["tall", "x", "y"], ["", "z", "w"]);
        Table table = document.Sections[0].Body.Tables.Single();
        table.Rows[0].Cells[0].Format.GridSpan = 2;
        table.Rows[1].Cells[0].Format.VerticalMerge = VerticalMerge.Restart;
        table.Rows[2].Cells[0].Format.VerticalMerge = VerticalMerge.Continue;

        Worksheet sheet = Workbook.FromTables(document).Worksheets[0];

        Assert.Equal([RangeAddress.Parse("A1:B1"), RangeAddress.Parse("A2:A3")], sheet.MergedCells);
        Assert.Equal("c", sheet["C1"].Text);
        Assert.True(sheet["A3"].IsEmpty);
        Assert.Equal("w", sheet["C3"].Text);
    }

    [Fact]
    public void NestedTablesFollowTheTableHoldingThemAndParagraphsBecomeLines()
    {
        Document document = DocumentWith(["outer"]);
        DocumentCell cell = document.Sections[0].Body.Tables.Single().Rows[0].Cells[0];
        cell.AddParagraph("second line");
        cell.AddTable(1, 1).Rows[0].Cells[0].AddParagraph("inner");

        Workbook book = Workbook.FromTables(document);

        Assert.Equal("outer\nsecond line", book.Worksheets[0]["A1"].Text);
        Assert.True(book.Worksheets[0]["A1"].Style.Alignment.WrapText);
        Assert.Equal("inner", book.Worksheets[1]["A1"].Text);
    }

    [Fact]
    public void ADocumentWithNoTablesGivesOneEmptySheet()
    {
        Document document = new();
        document.AddSection().Body.AddParagraph("Just text");
        Assert.Null(Assert.Single(Workbook.FromTables(document).Worksheets).UsedRange);
    }

    [Fact]
    public void AWorkbookComesBackThroughADocument()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "Name";
        sheet["B1"].Value = "Total";
        sheet["A2"].Value = "Tea";
        sheet["B2"].Value = 12;

        Worksheet back = Workbook.FromTables(book.ToDocument()).Worksheets[0];

        Assert.Equal("Name", back["A1"].Text);
        Assert.Equal(12d, back["B2"].Value.Number);
    }

    [Fact]
    public void PdfTablesBecomeSheetsAndATableOverPagesIsOneSheet()
    {
        Document document = new();
        Section section = document.AddSection();
        section.Body.AddParagraph("Report");
        Table table = section.Body.AddTable(0, 0);
        table.Style = (TableStyle)document.Styles.AddBuiltIn(BuiltInStyle.TableGrid);
        Row header = table.AddRow();
        header.Format.IsHeader = true;
        header.AddCell().Paragraphs.Single().AddRun("Item");
        header.AddCell().Paragraphs.Single().AddRun("Amount");
        for (int i = 0; i < 120; i++)
        {
            Row row = table.AddRow();
            row.AddCell().Paragraphs.Single().AddRun($"Line {i}");
            row.AddCell().Paragraphs.Single().AddRun((i * 10).ToString(CultureInfo.InvariantCulture));
        }

        string path = Path.Combine(folder, "report.pdf");
        document.SaveAsPdf(path);
        using PdfDocument pdf = PdfDocument.Open(path);
        Assert.True(pdf.Pages.Count > 1);

        Worksheet sheet = Assert.Single(Workbook.FromTables(pdf).Worksheets);

        Assert.Equal("Item", sheet["A1"].Text);
        Assert.Equal("Line 0", sheet["A2"].Text);
        Assert.Equal(CellValueKind.Number, sheet["B3"].ValueKind);
        Assert.Equal(10d, sheet["B3"].Value.Number);
        Assert.Equal("Line 119", sheet["A121"].Text);
        Assert.Equal(1190d, sheet["B121"].Value.Number);
        Assert.True(sheet["A122"].IsEmpty);
    }
}
