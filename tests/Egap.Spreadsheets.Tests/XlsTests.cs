using Egap.Documents;
using Egap.Documents.Office;

using static Egap.Spreadsheets.Tests.XlsBuilder;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Reading Excel 97-2003 workbooks (BIFF8): cells and shared strings, styles, formulas from their parsed tokens, shared and
/// array formulas, names, merged cells, rows and columns, and the files Egap turns down, from workbooks built here with
/// <see cref="XlsBuilder"/>.
/// </summary>
public sealed class XlsTests
{
    [Fact]
    public void ReadsCellValues()
    {
        Workbook book = new XlsBuilder()
            .Sheet("Data")
            .Cell(0x0203, At(0, 0), Double(1.5))
            .Cell(0x027E, At(1, 0), U32((42u << 2) | 0x02))
            .Cell(0x027E, At(2, 0), U32((1234u << 2) | 0x03))
            .Cell(0x027E, At(3, 0), U32((uint)(BitConverter.DoubleToInt64Bits(2.5) >> 32)))
            .Cell(0x00BD, U16(4), U16(0), U16(15), U32((7u << 2) | 0x02), U16(15), U32((8u << 2) | 0x02), U16(1))
            .Cell(0x0204, At(5, 0), String("label"))
            .Cell(0x0205, At(6, 0), [1, 0])
            .Cell(0x0205, At(7, 0), [0x07, 1])
            .Cell(0x0201, At(8, 0))
            .Cell(0x0203, At(9, 0), Double(double.Epsilon))
            .Load();

        Worksheet sheet = book.Worksheets[0];
        Assert.Equal("Data", sheet.Name);
        Assert.Equal(1.5, sheet["A1"].Value.Number);
        Assert.Equal(42, sheet["A2"].Value.Number);
        Assert.Equal(12.34, sheet["A3"].Value.Number);
        Assert.Equal(2.5, sheet["A4"].Value.Number);
        Assert.Equal(7, sheet["A5"].Value.Number);
        Assert.Equal(8, sheet["B5"].Value.Number);
        Assert.Equal("label", sheet["A6"].Value.Text);
        Assert.True(sheet["A7"].Value.Boolean);
        Assert.Equal(CellError.DivisionByZero, sheet["A8"].Value.Error);
        Assert.Equal(CellValueKind.Empty, sheet["A9"].Value.Kind);
        Assert.Equal(0, sheet["A10"].Value.Number);
    }

    [Fact]
    public void ReadsSharedStringsAcrossContinueRecords()
    {
        // The second string starts in the SST record as 8-bit characters and goes on in a CONTINUE record as UTF-16, with a
        // new option byte, as [MS-XLS] 2.4.265 has it.
        Workbook book = new XlsBuilder()
            .Global(0x00FC, U32(2), U32(2), String("Hello"), U16(4), [0], "ab"u8.ToArray())
            .Global(0x003C, [1], "c\0d\0"u8.ToArray())
            .Sheet("Sheet1")
            .Cell(0x00FD, At(0, 0), U32(0))
            .Cell(0x00FD, At(0, 1), U32(1))
            .Load();

        Worksheet sheet = book.Worksheets[0];
        Assert.Equal("Hello", sheet["A1"].Value.Text);
        Assert.Equal("abcd", sheet["B1"].Value.Text);
    }

    [Fact]
    public void ReadsStyles()
    {
        Workbook book = new XlsBuilder()
            .Global(0x041E, U16(164), String("0.000"))
            .Styles([Font("Courier New", 12, bold: true, italic: true)], Xf(5, 164, alignment: 0x2A, pattern: 1, foreground: 10))
            .Sheet("Sheet1")
            .Cell(0x0203, At(0, 0, 16), Double(1))
            .Cell(0x0203, At(1, 0), Double(2))
            .Load();

        Cell cell = book.Worksheets[0]["A1"];
        Assert.Equal("0.000", cell.NumberFormat);
        Assert.Equal("1.000", cell.Text);
        Assert.Equal("Courier New", cell.Style.Font.Name);
        Assert.Equal(12, cell.Style.Font.Size);
        Assert.True(cell.Style.Font.Bold);
        Assert.True(cell.Style.Font.Italic);
        Assert.Equal(CellHorizontalAlignment.Center, cell.Style.Alignment.Horizontal);
        Assert.True(cell.Style.Alignment.WrapText);
        Assert.Equal(CellFillPattern.Solid, cell.Style.Fill.Pattern);
        Assert.Equal(CellColor.FromIndex(10), cell.Style.Fill.Foreground);
        Assert.Equal("General", book.Worksheets[0]["A2"].NumberFormat);
        Assert.Equal("Arial", book.Worksheets[0]["A2"].Style.Font.Name);
    }

    [Fact]
    public void ReadsFormulasWithTheirCachedValues()
    {
        Workbook book = new XlsBuilder()
            .Sheet("Sheet1")
            .Cell(0x0203, At(0, 0), Double(1))
            .Cell(0x0203, At(1, 0), Double(2))
            .Cell(0x0006, Formula(0, 1, 3, [.. Ref(0x24, 0, 0), 0x1E, .. U16(2), 0x03]))
            .Cell(0x0006, Formula(1, 1, 3, [.. Area(0x25, 0, 1, 0, 0), 0x22, 1, .. U16(4)]))
            .Cell(0x0006, Formula(2, 1, 0, 0, [0x17, .. ShortString("x"), 0x17, .. ShortString("y"), 0x08]))
            .Cell(0x0207, String("xy"))
            .Cell(0x0006, Formula(3, 1, 1, 1, [.. Ref(0x24, 0, 0), 0x1E, .. U16(0), 0x0D]))
            .Cell(0x0006, Formula(4, 1, 2, 0x07, [0x1E, .. U16(1), 0x1E, .. U16(0), 0x06]))
            .Cell(0x0006, Formula(5, 1, 3, 0, [0x17, .. ShortString(string.Empty)]))
            .Load();

        Worksheet sheet = book.Worksheets[0];
        Assert.Equal("A1+2", sheet["B1"].Formula);
        Assert.Equal(3, sheet["B1"].Value.Number);
        Assert.Equal("SUM(A1:A2)", sheet["B2"].Formula);
        Assert.Equal("\"x\"&\"y\"", sheet["B3"].Formula);
        Assert.Equal("xy", sheet["B3"].Value.Text);
        Assert.Equal("A1>0", sheet["B4"].Formula);
        Assert.True(sheet["B4"].Value.Boolean);
        Assert.Equal("1/0", sheet["B5"].Formula);
        Assert.Equal(CellError.DivisionByZero, sheet["B5"].Value.Error);
        Assert.Equal(string.Empty, sheet["B6"].Value.Text);
    }

    [Fact]
    public void ReadsSharedFormulas()
    {
        // B1:B3 share =A1*2; the SHRFMLA record follows the first cell's FORMULA, and each cell points at that first cell.
        byte[] exp = [0x01, .. U16(0), .. U16(1)];
        Workbook book = new XlsBuilder()
            .Sheet("Sheet1")
            .Cell(0x0006, Formula(0, 1, 2, exp))
            .Cell(0x04BC, U16(0), U16(2), [1, 1], [0, 3], U16(9), Ref(0x2C, 0, 0xFF), [0x1E], U16(2), [0x05])
            .Cell(0x0006, Formula(1, 1, 4, exp))
            .Cell(0x0006, Formula(2, 1, 6, exp))
            .Load();

        Worksheet sheet = book.Worksheets[0];
        Assert.Equal("A1*2", sheet["B1"].Formula);
        Assert.Equal("A2*2", sheet["B2"].Formula);
        Assert.Equal("A3*2", sheet["B3"].Formula);
        Assert.Equal(6, sheet["B3"].Value.Number);
    }

    [Fact]
    public void ReadsArrayFormulas()
    {
        byte[] exp = [0x01, .. U16(0), .. U16(2)];
        Workbook book = new XlsBuilder()
            .Sheet("Sheet1")
            .Cell(0x0203, At(0, 0), Double(1))
            .Cell(0x0203, At(1, 0), Double(2))
            .Cell(0x0006, Formula(0, 2, 2, exp))
            .Cell(0x0221, U16(0), U16(1), [2, 2], U16(0), U32(0), U16(13), Area(0x65, 0, 1, 0, 0), [0x1E], U16(2), [0x05])
            .Cell(0x0006, Formula(1, 2, 4, exp))
            .Load();

        Worksheet sheet = book.Worksheets[0];
        Assert.Equal("A1:A2*2", sheet["C1"].Formula);
        Assert.Equal("C1:C2", sheet["C1"].ArrayRange?.Address.ToString());
        Assert.Equal(2, sheet["C1"].Value.Number);
        Assert.Equal(4, sheet["C2"].Value.Number);
    }

    [Fact]
    public void ReadsNamesAndSheetReferences()
    {
        byte[] area = [0x3B, .. U16(0), .. Area(0, 0, 1, 0, 1, relative: false)[1..]];
        Workbook book = new XlsBuilder()
            .Global(0x01AE, U16(2), U16(0x0401))
            .Global(0x0017, U16(2), U16(0), U16(0), U16(0), U16(0), U16(1), U16(1))
            .Global(0x0018, U16(0), [0, 4], U16(area.Length), U16(0), U16(0), U32(0), Characters("Data"), area)
            .Global(0x0018, U16(0x20), [0, 1], U16(area.Length), U16(0), U16(1), U32(0), [0, 0x06], area)
            .Global(0x0018, U16(0), [0, 5], U16(area.Length), U16(0), U16(0), U32(0), Characters("2000Q"), area)
            .Sheet("Sheet1")
            .Cell(0x0006, Formula(0, 2, 0, [0x3A, .. U16(1), .. Ref(0, 0, 0, rowRelative: false, columnRelative: false)[1..]]))
            .Cell(0x0006, Formula(1, 2, 0, [0x23, .. U32(1)]))
            .Sheet("May06")
            .Load();

        Assert.Equal("Sheet1!$A$1:$B$2", book.Names["Data"].Formula);
        DefinedName printArea = Assert.Single(book.Names, n => n.Name == "_xlnm.Print_Area");
        Assert.Same(book.Worksheets[0], printArea.Sheet);
        Assert.Equal("Sheet1!$A$1:$B$2", book.Names["_1_2000Q"].Formula);
        Assert.Equal("'May06'!$A$1", book.Worksheets[0]["C1"].Formula);
        Assert.Equal("Data", book.Worksheets[0]["C2"].Formula);
    }

    [Fact]
    public void ReadsMergedCellsRowsAndColumns()
    {
        Workbook book = new XlsBuilder()
            .Sheet("Sheet1")
            .Cell(0x0055, U16(10))
            .Cell(0x0225, U16(0), U16(255))
            .Cell(0x0208, U16(1), U16(0), U16(1), U16(600), U16(0), U16(0), U16(0x0140), U16(15))
            .Cell(0x0208, U16(2), U16(0), U16(1), U16(255), U16(0), U16(0), U16(0x0120), U16(15))
            .Cell(0x007D, U16(1), U16(2), U16(5120), U16(15), U16(0), U16(0))
            .Cell(0x007D, U16(3), U16(3), U16(2560), U16(15), U16(1), U16(0))
            .Cell(0x00E5, U16(1), U16(0), U16(1), U16(0), U16(2))
            .Load();

        Worksheet sheet = book.Worksheets[0];
        Assert.Equal(new RangeAddress(0, 0, 1, 2), Assert.Single(sheet.MergedCells));
        Assert.Equal(30, sheet.Rows[1].Height);
        Assert.True(sheet.Rows[2].Hidden);
        Assert.Equal(20, sheet.Columns[1].Width);
        Assert.Equal(20, sheet.Columns[2].Width);
        Assert.True(sheet.Columns[3].Hidden);

        // DEFCOLWIDTH 10 is ten digits and five pixels, rounded up to 80 pixels: 80 / 7 of a digit at Arial 10.
        Assert.Equal(2925 / 256.0, sheet.DefaultColumnWidth);
    }

    [Fact]
    public void ASpanToTheLastColumnSetsTheDefaultWidth()
    {
        Workbook book = new XlsBuilder()
            .Sheet("Sheet1")
            .Cell(0x007D, U16(2), U16(256), U16(2925), U16(15), U16(0), U16(0))
            .Load();

        Worksheet sheet = book.Worksheets[0];
        Assert.Equal(2925 / 256.0, sheet.DefaultColumnWidth);
        Assert.Null(sheet.Columns[5].Width);
    }

    [Fact]
    public void WidthsSnapToWholePixels()
    {
        // 1910 / 256 of a digit is 52.2 pixels at seven to a digit; Excel keeps 52 pixels, 1901 / 256 of a digit.
        Workbook book = new XlsBuilder()
            .Sheet("Sheet1")
            .Cell(0x007D, U16(0), U16(0), U16(1910), U16(15), U16(0), U16(0))
            .Load();

        Assert.Equal(1901 / 256.0, book.Worksheets[0].Columns[0].Width);
    }

    [Fact]
    public void AMacroSheetKeepsItsConstants()
    {
        Workbook book = new XlsBuilder()
            .Sheet("Sheet1")
            .Sheet("Macro1", type: 1, state: 2)
            .Cell(0x0204, At(0, 0), String("Auto_Open"))
            .Cell(0x0006, Formula(1, 0, 1, 1, [0x1E, .. U16(1)]))
            .Load();

        Worksheet macro = book.Worksheets[1];
        Assert.Equal(SheetVisibility.VeryHidden, macro.Visibility);
        Assert.Equal("Auto_Open", macro["A1"].Value.Text);
        Assert.True(macro["A2"].IsEmpty);
    }

    [Fact]
    public void SavesAsXlsx()
    {
        Workbook book = new XlsBuilder()
            .Sheet("Sheet1")
            .Cell(0x0203, At(0, 0), Double(1))
            .Cell(0x0006, Formula(0, 1, 2, [.. Ref(0x24, 0, 0), 0x1E, .. U16(2), 0x05]))
            .Load();

        using var stream = new MemoryStream();
        book.Save(stream);
        stream.Position = 0;
        Workbook again = Workbook.Load(stream);
        Assert.Equal("A1*2", again.Worksheets[0]["B1"].Formula);
        Assert.Equal(2, again.Worksheets[0]["B1"].Value.Number);
    }

    [Fact]
    public void SavesFormulaTextXmlCannotHold()
    {
        // A text constant with a control character goes to XLSX as an _xHHHH_ escape, and comes back.
        Workbook book = new XlsBuilder()
            .Sheet("Sheet1")
            .Cell(0x0006, Formula(0, 0, 0, 0, [0x17, .. ShortString("ab")]))
            .Cell(0x0207, String("ab"))
            .Load();

        using var stream = new MemoryStream();
        book.Save(stream);
        stream.Position = 0;
        Workbook again = Workbook.Load(stream);
        Assert.Equal("\"ab\"", again.Worksheets[0]["A1"].Formula);
        Assert.Equal("ab", again.Worksheets[0]["A1"].Value.Text);
    }

    [Fact]
    public void IgnoresASharedFormulaAnchorOffTheSheet()
    {
        Workbook book = new XlsBuilder()
            .Sheet("Sheet1")
            .Cell(0x0006, Formula(0, 0, 5, [0x01, .. U16(0), .. U16(0xFFFF)]))
            .Load();

        Assert.Equal(5, book.Worksheets[0]["A1"].Value.Number);
    }

    [Fact]
    public void TurnsDownExcel95Workbooks()
    {
        byte[] file = new XlsBuilder().Sheet("Sheet1").Build("Book");
        var error = Assert.Throws<DocumentException>(() => Workbook.Load(new MemoryStream(file)));
        Assert.Contains("BIFF5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TurnsDownEncryptedWorkbooks()
    {
        byte[] file = new XlsBuilder().Global(0x002F, U16(1), new byte[52]).Sheet("Sheet1").Build();
        Assert.Throws<DocumentPasswordException>(() => Workbook.Load(new MemoryStream(file)));
    }

    [Fact]
    public void TurnsDownACompoundFileWithoutAWorkbook()
    {
        using var file = new MemoryStream();
        CompoundFileWriter.Write(file, [("WordDocument", new byte[16])]);
        file.Position = 0;
        Assert.Throws<DocumentException>(() => Workbook.Load(file));
    }

    [Fact]
    public void SurvivesTruncatedRecords()
    {
        byte[] stream = new XlsBuilder()
            .Sheet("Sheet1")
            .Cell(0x0203, At(0, 0), Double(1))
            .Cell(0x0006, Formula(0, 1, 2, [.. Ref(0x24, 0, 0), 0x1E, .. U16(2), 0x05]))
            .Stream();
        for (int cut = 1; cut < stream.Length; cut += 7)
        {
            using var file = new MemoryStream();
            CompoundFileWriter.Write(file, [("Workbook", stream[..cut])]);
            file.Position = 0;
            try
            {
                Workbook.Load(file);
            }
            catch (DocumentException)
            {
            }
        }
    }
}
