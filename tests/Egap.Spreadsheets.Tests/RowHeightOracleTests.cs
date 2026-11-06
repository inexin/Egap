using System.Globalization;

using Egap.Pdf;
using Egap.Spreadsheets.Printing;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Printed row heights against Excel's PDF export: <c>Oracle/row-heights.excel.tsv</c> holds, per normal font and size, the
/// row height Excel shows in pixels, the height it prints in dots at 600 dpi, and what it prints for each height of
/// <c>tools/make-row-height-oracle.py</c> (dots again), measured by <see cref="ReadExcelPdfsWhenAsked"/> from the PDFs
/// <c>tools/excel-row-height-oracle.ps1</c> writes.
/// </summary>
public sealed class RowHeightOracleTests
{
    private static readonly double[] Heights = [30, 33.2, 45, 60, 90];

    private static IEnumerable<string[]> Lines() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Oracle", "row-heights.excel.tsv")).Skip(1).Select(l => l.Split('\t'));

    [Fact]
    public void TheTableHoldsWhatExcelMeasures()
    {
        int rows = 0;
        foreach (string[] fields in Lines())
        {
            PrintedRow? row = PrintedRows.Find(fields[0], double.Parse(fields[1], CultureInfo.InvariantCulture));
            Assert.Equal(new PrintedRow(int.Parse(fields[2], CultureInfo.InvariantCulture), int.Parse(fields[3], CultureInfo.InvariantCulture)), row);
            rows++;
        }

        Assert.True(rows > 90);
    }

    [Fact]
    public void RowsPrintAsExcelPrintsThem()
    {
        foreach (string[] fields in Lines())
        {
            Workbook book = BookInFont(fields[0], fields[1], "");
            Worksheet sheet = book.Worksheets[0];
            for (int i = 0; i < Heights.Length; i++)
            {
                sheet.Rows[i].Height = Heights[i];
            }

            SheetGrid grid = new(sheet, new SheetFonts(book, null));
            Assert.Equal(int.Parse(fields[3], CultureInfo.InvariantCulture) * 0.12, grid.DefaultRow, 6);
            for (int i = 0; i < Heights.Length; i++)
            {
                double measured = int.Parse(fields[4 + i], CultureInfo.InvariantCulture) * 0.12;
                Assert.True(Math.Abs(measured - grid.Row(i)) <= 0.121, $"{fields[0]} {fields[1]}, {Heights[i]} points: Excel {measured}, Egap {grid.Row(i)}");
            }
        }
    }

    [Fact]
    public void AFileHeightNotMarkedCustomGivesWayToTheFont()
    {
        Workbook free = BookInFont("Arial", "10", " defaultRowHeight=\"15\"");
        Workbook custom = BookInFont("Arial", "10", " defaultRowHeight=\"15\" customHeight=\"1\"");
        Assert.Equal(103 * 0.12, new SheetGrid(free.Worksheets[0], new SheetFonts(free, null)).DefaultRow, 6);
        Assert.Equal(121 * 0.12, new SheetGrid(custom.Worksheets[0], new SheetFonts(custom, null)).DefaultRow, 6);
    }

    [Fact]
    public void ANewWorkbookNamesItsFontNotTheDefaultThemes()
    {
        using MemoryStream saved = new();
        Workbook.Create().Save(saved);
        saved.Position = 0;
        using System.IO.Compression.ZipArchive package = new(saved);
        using StreamReader styles = new(package.GetEntry("xl/styles.xml")!.Open());
        Assert.DoesNotContain("<scheme", styles.ReadToEnd(), StringComparison.Ordinal);
    }

    /// <summary>A workbook read from a file with this normal font, as Excel would read it, and these sheetFormatPr attributes.</summary>
    private static Workbook BookInFont(string font, string size, string format)
    {
        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string Package = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string Ml = "application/vnd.openxmlformats-officedocument.spreadsheetml.";
        (string Name, string Text)[] parts =
        [
            ("[Content_Types].xml", $"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"{Ml}sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"{Ml}worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"{Ml}styles+xml\"/></Types>"),
            ("_rels/.rels", $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"rId1\" Type=\"{Rel}/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"{Rel}/styles\" Target=\"styles.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Rel}\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Main}\"><sheetFormatPr{format}/><sheetData/></worksheet>"),
            ("xl/styles.xml", $"<styleSheet xmlns=\"{Main}\"><fonts count=\"1\"><font><sz val=\"{size}\"/><name val=\"{font}\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs><cellXfs count=\"1\"><xf/></cellXfs></styleSheet>"),
        ];
        MemoryStream file = new();
        using (System.IO.Compression.ZipArchive package = new(file, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string text) in parts)
            {
                using StreamWriter writer = new(package.CreateEntry(name).Open());
                writer.Write(text);
            }
        }

        file.Position = 0;
        return Workbook.Load(file);
    }

    /// <summary>Measures the PDFs in the folder EGAP_ROW_ORACLE names into row-heights.excel.tsv there; else does nothing.</summary>
    [Fact]
    public void ReadExcelPdfsWhenAsked()
    {
        string? folder = Environment.GetEnvironmentVariable("EGAP_ROW_ORACLE");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        string[] standard = File.ReadAllLines(Path.Combine(folder, "standard.txt"));
        List<string> lines = [standard[0]];
        foreach (string line in standard.Skip(1).Order(StringComparer.Ordinal))
        {
            string[] fields = line.Split('\t');
            int dash = fields[0].LastIndexOf('-');
            int pixels = (int)Math.Round(double.Parse(fields[1], CultureInfo.InvariantCulture) * 96 / 72);
            using PdfDocument pdf = PdfDocument.Open(Path.Combine(folder, fields[0] + ".pdf"));
            double[] y = [.. pdf.Pages[0].GetText().Glyphs.Where(g => g.Text == "x").Select(g => g.Origin.Y).OrderDescending()];
            int[] dots = [.. y.Zip(y.Skip(1), (a, b) => (int)Math.Round((a - b) / 0.12))];
            lines.Add($"{fields[0][..dash]}\t{fields[0][(dash + 1)..]}\t{pixels}\t{dots[1]}\t{string.Join('\t', dots.Skip(2))}");
        }

        File.WriteAllText(Path.Combine(folder, "row-heights.excel.tsv"), string.Join('\n', lines) + "\n");
    }
}
