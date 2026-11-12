using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

using Egap.Documents;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Excel binary workbooks (.xlsb): the files in <c>Oracle/xlsb</c> (see <c>xlsb.txt</c>) are one workbook Excel saved as XLSB
/// and as XLSX, so reading the XLSB must give what reading Excel's XLSX gives.
/// </summary>
public sealed class XlsbTests
{
    private const string Password = "Egap xlsb 1";

    private static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Oracle", "xlsb", name);

    [Fact]
    public void TextXmlCannotHoldIsEscapedInTheXmlMadeForTheXlsxCode()
    {
        XNamespace ns = "urn:test";
        XElement made = new(ns + "font", new XElement(ns + "name", new XAttribute("val", "Ari\u0001al")),
            new XElement(ns + "t", "a\0b \uD83D\uDE00 kept"));

        Xlsb.XlsbXml.Safe(made);

        Assert.Equal("Ari_x0001_al", made.Element(ns + "name")!.Attribute("val")!.Value);
        Assert.Equal("a_x0000_b \uD83D\uDE00 kept", made.Element(ns + "t")!.Value);
    }

    [Fact]
    public void ReadsWhatExcelsXlsxHolds()
    {
        Workbook expected = Workbook.Load(Path("cells.xlsx"));
        Workbook actual = Workbook.Load(Path("cells.xlsb"));
        AssertSame(expected, actual);
    }

    [Fact]
    public void ReadsFormulasOfEveryKind()
    {
        Worksheet data = Workbook.Load(Path("cells.xlsb")).Worksheets["Data"];
        Assert.Equal("SUM(B2:B7)", data["F2"].Formula);
        Assert.Equal("'Other Sheet'!A1+Hidden!B2", data["F5"].Formula);
        Assert.Equal("SUM(Data:Hidden!A1)", data["F6"].Formula);
        Assert.Equal("_xlfn.SINGLE(Total)*_xleta.RATE", data["F8"].Formula); // as Excel writes it in the XLSX too
        Assert.Equal("ROUND(PI(),3)-{1,2;3,4}", data["F9"].Formula);
        Assert.Equal("IFERROR(INDEX(B:B,MATCH(MAX(B2:B7),B2:B7,0)),-1)", data["F10"].Formula);
        Assert.Equal("COUNTIFS(A2:A7,\"<>West\",B2:B7,\">1000\")", data["F12"].Formula);
        Assert.Equal("A2 B2:B3", data["F13"].Formula);
        Assert.Equal("B7*2", data["G7"].Formula);
        Assert.Equal("B2:B4*10", data["H2"].Formula);
        Assert.Equal(new RangeAddress(1, 7, 3, 7), data["H2"].ArrayRange?.Address);
        Assert.Equal("_xlfn.SEQUENCE(3)", data["J2"].Formula);
        Assert.Equal(new RangeAddress(1, 9, 3, 9), data["J2"].SpillRange?.Address);

        Worksheet other = data.Workbook.Worksheets["Other Sheet"];
        Assert.Equal("SUM(Stock[Qty])", other["F1"].Formula);
        Assert.Equal("COUNTA(Stock[[#All],[Item]])", other["F2"].Formula);
    }

    [Fact]
    public void ReadsNamesStylesAndSheetObjects()
    {
        Workbook book = Workbook.Load(Path("cells.xlsb"));
        Assert.Equal(WorkbookFormat.Xlsx, book.Format);
        Assert.Equal(SheetVisibility.Hidden, book.Worksheets["Hidden"].Visibility);
        Assert.Contains(book.Names, n => n.Name == "Total" && n.Formula == "Data!$F$2" && n.Sheet is null);
        Assert.Contains(book.Names, n => n.Name == "Local" && n.Sheet?.Name == "Data");
        Assert.Contains(book.Names, n => n.Name == "Secret" && n.Hidden);

        Worksheet data = book.Worksheets["Data"];
        Assert.True(data["A1"].Style.Font.Bold);
        Assert.Equal("Courier New", data["A13"].Style.Font.Name);
        Assert.Equal(14, data["A13"].Style.Font.Size);
        Assert.Contains(data.MergedCells, m => m.ToString() == "L2:N3");
        Assert.NotNull(data.Columns["A"].Width);
        Assert.Equal(30, data.Rows[4].Height);
        Assert.Contains(data.Hyperlinks, h => h.Address == "https://example.com/report" && h.ToolTip == "Example tip");
        Assert.Contains(data.Comments, c => c.Text == "A note");
        Assert.Equal(2, data.DataValidations.Count);
        Assert.Equal(3, data.ConditionalFormats.Count);
        Assert.Single(book.Worksheets["Other Sheet"].Tables, t => t.Name == "Stock");
    }

    [Fact]
    public void OpensAnEncryptedXlsbWithThePassword()
    {
        Workbook book = Workbook.Load(Path("encrypted.xlsb"), new WorkbookLoadOptions { Password = Password });
        AssertSame(Workbook.Load(Path("cells.xlsb")), book);

        DocumentPasswordException missing = Assert.Throws<DocumentPasswordException>(() => Workbook.Load(Path("encrypted.xlsb")));
        Assert.False(missing.WrongPassword);
        DocumentPasswordException wrong = Assert.Throws<DocumentPasswordException>(() => Workbook.Load(Path("encrypted.xlsb"), new WorkbookLoadOptions { Password = "egap xlsb 1" }));
        Assert.True(wrong.WrongPassword);
    }

    [Fact]
    public void SavesAsXlsxThatReadsTheSame()
    {
        Workbook book = Workbook.Load(Path("cells.xlsb"));
        using MemoryStream saved = new();
        book.Save(saved);
        saved.Position = 0;
        AssertSame(Workbook.Load(Path("cells.xlsx")), Workbook.Load(saved));
    }

    [Fact]
    public void RefusesADamagedPart()
    {
        // The sheet part cut short in the middle of a record.
        using MemoryStream damaged = new();
        using (ZipArchive source = ZipFile.OpenRead(Path("cells.xlsb")))
        using (ZipArchive target = new(damaged, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                using Stream input = entry.Open();
                using MemoryStream bytes = new();
                input.CopyTo(bytes);
                byte[] data = bytes.ToArray();
                if (entry.FullName == "xl/worksheets/sheet1.bin")
                {
                    data = data[..(data.Length / 2)];
                }

                using Stream output = target.CreateEntry(entry.FullName).Open();
                output.Write(data);
            }
        }

        damaged.Position = 0;
        Assert.Throws<DocumentException>(() => Workbook.Load(damaged));
    }

    private static void AssertSame(Workbook expected, Workbook actual)
    {
        Assert.Equal(expected.Worksheets.Select(s => (s.Name, s.Visibility)), actual.Worksheets.Select(s => (s.Name, s.Visibility)));
        Assert.Equal(expected.Date1904, actual.Date1904);
        Assert.Equal(Names(expected), Names(actual));
        foreach (Worksheet sheet in expected.Worksheets)
        {
            Worksheet other = actual.Worksheets[sheet.Name];
            Assert.Equal(Cells(sheet), Cells(other));
            Assert.Equal(sheet.MergedCells.Select(m => m.ToString()).Order(), other.MergedCells.Select(m => m.ToString()).Order());
            Assert.Equal(sheet.Columns.Select(c => (c.Name, c.Width, c.Hidden)), other.Columns.Select(c => (c.Name, c.Width, c.Hidden)));
            Assert.Equal(Rows(sheet), Rows(other));
            Assert.Equal(sheet.Hyperlinks.Select(h => (h.Range.ToString(), h.Address, h.Location, h.Display, h.ToolTip)), other.Hyperlinks.Select(h => (h.Range.ToString(), h.Address, h.Location, h.Display, h.ToolTip)));
            Assert.Equal(sheet.Comments.Select(c => (c.Cell.ToString(), c.Author, c.Text)), other.Comments.Select(c => (c.Cell.ToString(), c.Author, c.Text)));
            Assert.Equal(sheet.Tables.Select(t => (t.Name, t.Range.ToString(), t.StyleName)), other.Tables.Select(t => (t.Name, t.Range.ToString(), t.StyleName)));
            Assert.Equal(sheet.DataValidations.Select(v => (string.Join(" ", v.Ranges), v.Type, v.Formula1, v.Formula2)), other.DataValidations.Select(v => (string.Join(" ", v.Ranges), v.Type, v.Formula1, v.Formula2)));
            Assert.Equal(Conditional(sheet), Conditional(other));
        }
    }

    private static string[] Names(Workbook book) =>
        [.. book.Names.Select(n => $"{n.Name}@{n.Sheet?.Name}={n.Formula} {n.Hidden}").Order(StringComparer.Ordinal)];

    private static string[] Cells(Worksheet sheet) =>
        [.. sheet.Cells.Select(c => $"{new CellAddress(c.Row, c.Column)} {c.Value.Kind} {Text(c.Value)} {c.Formula} {c.Style}")];

    private static string[] Rows(Worksheet sheet) =>
        [.. sheet.Rows.Where(r => r.Height is not null || r.Hidden || r.OutlineLevel != 0).Select(r => $"{r.Index} {r.Height} {r.Hidden} {r.OutlineLevel}")];

    private static string[] Conditional(Worksheet sheet) =>
        [.. sheet.ConditionalFormats.Select(f => string.Join(" ", f.Ranges) + " " + string.Join(",", f.Rules.Select(r => $"{r.Type} {r.Operator} {string.Join("|", r.Formulas)} {r.Style}")))];

    private static string Text(CellValue value) => value.Kind switch
    {
        CellValueKind.Number => value.Number.ToString("R", CultureInfo.InvariantCulture),
        CellValueKind.Text => value.Text,
        CellValueKind.Boolean => value.Boolean.ToString(),
        CellValueKind.Error => value.Error.ToString(),
        _ => "",
    };
}
