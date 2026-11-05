using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Egami.Pixels;

using Egap.Documents.Layout;
using Egap.Pdf;
using Egap.Spreadsheets.Printing;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Pagination against Excel: the workbooks of <see cref="Cases"/> exported to PDF here and by Excel
/// (<c>tools/excel-print-oracle.ps1</c>), page by page the first cell each page prints, as <c>Oracle/print.excel.tsv</c>
/// records Excel's; with the colours Excel shows for theme colours with a tint.
/// </summary>
/// <remarks>
/// Every cell holds 10000 + 100 × row + column, so a page's text tells the cells it prints, and the row numbers of the
/// headings, all below 10000, don't count.
/// </remarks>
public sealed partial class PrintOracleTests
{
    private static readonly SheetPrintOptions Portable = new() { Fonts = FontSettings.Portable };

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Oracle");

    /// <summary>The theme colours and tints the oracle asks Excel to show.</summary>
    public static readonly (int Theme, double Tint)[] Tints =
    [
        (0, -0.05), (0, -0.15), (0, -0.25), (0, -0.35), (0, -0.5), (1, 0.05), (1, 0.15), (1, 0.25), (1, 0.35), (1, 0.5),
        (2, -0.1), (2, -0.25), (2, -0.5), (2, -0.75), (2, -0.9), (3, 0.8), (3, 0.6), (3, 0.4), (3, -0.25), (3, -0.5),
        (4, 0.8), (4, 0.6), (4, 0.4), (4, -0.25), (4, -0.5), (5, 0.8), (5, 0.6), (5, 0.4), (5, -0.25), (5, -0.5),
        (6, 0.8), (6, -0.5), (7, 0.6), (7, -0.25), (8, 0.4), (8, -0.5), (9, 0.8), (9, -0.25), (10, 0.3), (11, -0.3),
    ];

    /// <summary>The workbooks whose pagination is checked, each with the sheets it prints.</summary>
    public static IEnumerable<(string Name, Workbook Book)> Cases()
    {
        yield return ("a4", Book(s => { }));
        yield return ("letter", Book(s => s.PageSetup.PaperSize = 1));
        yield return ("landscape", Book(s => s.PageSetup.Orientation = PrintOrientation.Landscape));
        yield return ("scale-55", Book(s => s.PageSetup.Scale = 55));
        yield return ("scale-130", Book(s => s.PageSetup.Scale = 130));
        yield return ("fit-wide", Book(s =>
        {
            s.PageSetup.FitToPage = true;
            s.PageSetup.FitToWidth = 1;
            s.PageSetup.FitToHeight = 0;
        }));
        yield return ("fit-2x2", Book(s =>
        {
            s.PageSetup.FitToPage = true;
            s.PageSetup.FitToWidth = 2;
            s.PageSetup.FitToHeight = 2;
        }));
        yield return ("titles", Book(s =>
        {
            s.PageSetup.PrintTitleRows = RangeAddress.Parse("1:3");
            s.PageSetup.PrintTitleColumns = RangeAddress.Parse("A:B");
        }));
        yield return ("breaks", Book(s =>
        {
            s.PageSetup.RowBreaks.Add(20);
            s.PageSetup.RowBreaks.Add(30);
            s.PageSetup.ColumnBreaks.Add(4);
        }));
        yield return ("over-then-down", Book(s => s.PageSetup.PageOrder = PrintPageOrder.OverThenDown));
        yield return ("sizes", Book(s =>
        {
            double[] widths = [6.5, 8.43, 12, 17.25, 25, 40, 7, 10.71, 14.29, 30];
            for (int c = 0; c < 25; c++)
            {
                s.Columns[c].Width = widths[c % widths.Length];
            }

            double[] heights = [15, 30, 12.75, 45, 9, 21];
            for (int r = 0; r < 150; r += 3)
            {
                s.Rows[r].Height = heights[(r / 3) % heights.Length];
            }
        }));
        yield return ("hidden", Book(s =>
        {
            for (int c = 2; c < 25; c += 4)
            {
                s.Columns[c].Hidden = true;
            }

            for (int r = 5; r < 150; r += 7)
            {
                s.Rows[r].Hidden = true;
            }
        }));
        yield return ("print-area", Book(s => s.PageSetup.PrintArea = [RangeAddress.Parse("B3:H70"), RangeAddress.Parse("K5:Y20")]));
        yield return ("headings", Book(s =>
        {
            s.PageSetup.PrintHeadings = true;
            s.PageSetup.PrintGridLines = true;
        }));
        yield return ("margins", Book(s =>
        {
            s.PageSetup.LeftMargin = 0.25;
            s.PageSetup.RightMargin = 1.5;
            s.PageSetup.TopMargin = 2;
            s.PageSetup.BottomMargin = 0.4;
            s.PageSetup.CenterHorizontally = true;
            s.PageSetup.CenterVertically = true;
        }));
        yield return ("offset", Book(s =>
        {
            for (int r = 0; r < 150; r++)
            {
                for (int c = 0; c < 25; c++)
                {
                    s[r, c].Clear();
                }
            }

            for (int r = 40; r < 90; r++)
            {
                for (int c = 6; c < 30; c++)
                {
                    s[r, c].Value = Value(r, c);
                }
            }
        }));
    }

    [Fact]
    public void PagesBreakAsInExcel()
    {
        Dictionary<string, string[]> expected = File.ReadAllLines(Path.Combine(Folder, "print.excel.tsv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .ToDictionary(f => f[0], f => f);
        StringBuilder failures = new();
        int compared = 0;
        foreach ((string name, Workbook book) in Cases())
        {
            Assert.True(expected.TryGetValue(name, out string[]? excel), $"{name} is not in the oracle; run tools/excel-print-oracle.ps1");
            Worksheet sheet = book.Worksheets[0];
            using MemoryStream stream = new();
            sheet.SaveAsPdf(stream, new SheetPdfOptions { Print = Portable });
            (int pages, string firsts) = FirstCells(stream.ToArray(), sheet.PageSetup);
            compared++;
            if (excel[1] != pages.ToString(CultureInfo.InvariantCulture) || excel[2] != firsts)
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"{name}: Excel {excel[1]} pages starting {excel[2]}; Egap {pages} starting {firsts}");
            }
        }

        Assert.True(compared >= 15, $"only {compared} cases");
        Assert.True(failures.Length == 0, failures.ToString());
    }

    /// <summary>
    /// Writes <c>print.excel.tsv</c> from Excel's PDFs (<c>*.excel.pdf</c>) in the folder EGAP_PRINT_ORACLE names, with the
    /// header line the Excel script left in <c>excel.txt</c>, and Egap's own PDFs beside them (<c>*.egap.pdf</c>) to compare
    /// by eye; else does nothing.
    /// </summary>
    [Fact]
    public void ReadExcelPdfsWhenAsked()
    {
        string? folder = Environment.GetEnvironmentVariable("EGAP_PRINT_ORACLE");
        if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, "excel.txt")))
        {
            return;
        }

        List<string> lines = [File.ReadAllText(Path.Combine(folder, "excel.txt")).Trim()];
        foreach ((string name, Workbook book) in Cases())
        {
            byte[] pdf = File.ReadAllBytes(Path.Combine(folder, name + ".excel.pdf"));
            (int pages, string firsts) = FirstCells(pdf, book.Worksheets[0].PageSetup);
            book.Worksheets[0].SaveAsPdf(Path.Combine(folder, name + ".egap.pdf"), new SheetPdfOptions { Print = Portable });
            lines.Add(FormattableString.Invariant($"{name}\t{pages}\t{firsts}"));
        }

        File.WriteAllText(Path.Combine(folder, "print.excel.tsv"), string.Join('\n', lines) + "\n");
    }

    [Fact]
    public void TintsShowAsInExcel()
    {
        string[] lines = [.. File.ReadAllLines(Path.Combine(Folder, "tints.excel.tsv")).Where(l => l.Length > 0 && !l.StartsWith('#'))];
        Assert.Equal(Tints.Length, lines.Length);
        StringBuilder failures = new();
        foreach (string line in lines)
        {
            string[] fields = line.Split('\t');
            int theme = int.Parse(fields[0], CultureInfo.InvariantCulture);
            double tint = double.Parse(fields[1], CultureInfo.InvariantCulture);
            // The workbook has no theme part, so Excel uses its default theme, as Egap does.
            Rgba8 color = SheetColors.Default.Resolve(CellColor.FromTheme(theme, tint), default);
            string ours = $"{color.R:X2}{color.G:X2}{color.B:X2}";
            if (ours != fields[2])
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"theme {theme} tint {tint}: Excel {fields[2]}, Egap {ours}");
            }
        }

        Assert.True(failures.Length == 0, failures.ToString());
    }

    /// <summary>Writes the cases as .xlsx files into the folder EGAP_PRINT_ORACLE names, for the Excel script; else does nothing.</summary>
    [Fact]
    public void WriteCasesWhenAsked()
    {
        string? folder = Environment.GetEnvironmentVariable("EGAP_PRINT_ORACLE");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        foreach ((string name, Workbook book) in Cases())
        {
            book.Save(Path.Combine(folder, name + ".xlsx"));
        }

        // The tints as Excel writes them to a file, in 32767ths: Excel's 50 % is 16383/32767.
        double[] stored = [.. Tints.Select(t => Math.Truncate(t.Tint * 32767) / 32767)];
        Workbook tints = Workbook.Create();
        for (int i = 0; i < Tints.Length; i++)
        {
            tints.Worksheets[0][i, 0].Style = CellStyle.Default with { Fill = CellFill.Solid(CellColor.FromTheme(Tints[i].Theme, stored[i])) };
        }

        // A formatted sheet in each conformance level, for veraPDF.
        Workbook conforming = Book(s =>
        {
            s.PageSetup.PrintTitleRows = RangeAddress.Parse("1:1");
            s.PageSetup.Header = "&CReport";
            s.MergedCells.Add(RangeAddress.Parse("A2:C2"));
            s["A2"].Style = CellStyle.Default with { Fill = CellFill.Solid(CellColor.FromTheme(4, 0.4)) };
        });
        Worksheet formatted = conforming.Worksheets[0];
        formatted.SaveAsPdf(Path.Combine(folder, "verapdf-2a.pdf"), new SheetPdfOptions { Print = Portable, Conformance = Egap.Pdf.PdfAConformance.PdfA2a, Language = "en-US", Title = "Report" });
        formatted.SaveAsPdf(Path.Combine(folder, "verapdf-2b.pdf"), new SheetPdfOptions { Print = Portable, Conformance = Egap.Pdf.PdfAConformance.PdfA2b, Language = "en-US", Title = "Report" });
        formatted.SaveAsPdf(Path.Combine(folder, "verapdf-ua1.pdf"), new SheetPdfOptions { Print = Portable, UniversalAccessibility = true, Language = "en-US", Title = "Report" });

        tints.Save(Path.Combine(folder, "tints.xlsx"));
        File.WriteAllLines(Path.Combine(folder, "tints.txt"), Tints.Select((t, i) => FormattableString.Invariant($"{t.Theme}\t{stored[i]:R}")));
    }

    /// <summary>The page count and, page by page, the first cell each page prints apart from the print titles, as A1 references.</summary>
    private static (int Pages, string Firsts) FirstCells(byte[] pdf, PageSetup setup)
    {
        PdfDocument document = PdfDocument.Open(pdf);
        List<string> firsts = [];
        for (int i = 0; i < document.Pages.Count; i++)
        {
            List<int> rows = [], columns = [];
            foreach (Match match in Number().Matches(document.Pages[i].ExtractText()))
            {
                int value = int.Parse(match.Value, CultureInfo.InvariantCulture) - 10000;
                if (value >= 0)
                {
                    rows.Add(value / 100);
                    columns.Add(value % 100);
                }
            }

            if (rows.Count == 0)
            {
                firsts.Add("-");
                continue;
            }

            int row = Start([.. rows.Distinct().Order()], setup.PrintTitleRows is { } r ? (r.First.Row, r.Last.Row) : null);
            int column = Start([.. columns.Distinct().Order()], setup.PrintTitleColumns is { } c ? (c.First.Column, c.Last.Column) : null);
            firsts.Add(new CellAddress(row, column).ToString());
        }

        return (document.Pages.Count, string.Join(',', firsts));
    }

    /// <summary>The first row or column of a page's body: past the print titles when they repeat on it.</summary>
    private static int Start(int[] indexes, (int First, int Last)? titles)
    {
        if (titles is not { } t || indexes[0] < t.First || indexes[0] > t.Last)
        {
            return indexes[0];
        }

        for (int i = 1; i < indexes.Length; i++)
        {
            if (indexes[i] != indexes[i - 1] + 1)
            {
                return indexes[i];
            }
        }

        return indexes[0];
    }

    private static int Value(int row, int column) => 10000 + (row * 100) + column;

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    /// <summary>A sheet of 150 rows by 25 columns of numbers on A4, changed by <paramref name="change"/>.</summary>
    private static Workbook Book(Action<Worksheet> change)
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet.PageSetup.PaperSize = 9;
        for (int r = 0; r < 150; r++)
        {
            for (int c = 0; c < 25; c++)
            {
                sheet[r, c].Value = Value(r, c);
            }
        }

        change(sheet);
        return book;
    }
}
