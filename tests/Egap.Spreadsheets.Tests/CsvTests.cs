using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets.Tests;

/// <summary>Comma- and tab-separated text: RFC 4180 quoting, separators, encodings, cultures and type detection.</summary>
public sealed class CsvTests
{
    private static Workbook Load(string text, CsvOptions? options = null) =>
        Workbook.LoadCsv(new MemoryStream(Encoding.UTF8.GetBytes(text)), options);

    private static string Save(Worksheet sheet, CsvOptions? options = null)
    {
        MemoryStream stream = new();
        sheet.SaveCsv(stream, options);
        return (options?.Encoding ?? Encoding.UTF8).GetString(stream.ToArray()).TrimStart('﻿');
    }

    [Fact]
    public void QuotedFieldsHoldSeparatorsQuotesAndLineBreaks()
    {
        Worksheet sheet = Load("name,note\r\n\"Smith, J\",\"said \"\"hi\"\"\nthen left\"\r\nlast,").Worksheets[0];

        Assert.Equal("Sheet1", sheet.Name);
        Assert.Equal("Smith, J", sheet["A2"].Value);
        Assert.Equal("said \"hi\"\nthen left", sheet["B2"].Value);
        Assert.Equal("last", sheet["A3"].Value);
        Assert.True(sheet["B3"].IsEmpty);
        Assert.Equal("A1:B3", sheet.UsedRange!.Value.Address.ToString());
    }

    [Fact]
    public void TypesAreDetected()
    {
        Worksheet sheet = Load("1234.5,-7,1e3,12.5%,\"1,234\",TRUE,false,#N/A,007,=SUM(A1),2024-03-15,2024-03-15 18:30,3/15/2024,6:30,text").Worksheets[0];

        Assert.Equal(1234.5, sheet["A1"].Value);
        Assert.Equal(-7, sheet["B1"].Value);
        Assert.Equal(1000, sheet["C1"].Value);
        Assert.Equal("1.00E+03", sheet["C1"].Text);
        Assert.Equal(0.125, sheet["D1"].Value);
        Assert.Equal("12.50%", sheet["D1"].Text);
        Assert.Equal(1234, sheet["E1"].Value);
        Assert.Equal("1,234", sheet["E1"].Text);
        Assert.Equal(true, sheet["F1"].Value);
        Assert.Equal(false, sheet["G1"].Value);
        Assert.Equal(CellError.NotAvailable, sheet["H1"].Value);
        Assert.Equal("007", sheet["I1"].Value);
        Assert.Equal("=SUM(A1)", sheet["J1"].Value);
        Assert.False(sheet["J1"].HasFormula);
        Assert.Equal(45366, sheet["K1"].Value);
        Assert.Equal("2024-03-15", sheet["K1"].Text);
        Assert.Equal("2024-03-15 18:30", sheet["L1"].Text);
        Assert.Equal(45366, sheet["M1"].Value);
        Assert.Equal("3/15/2024", sheet["M1"].Text);
        Assert.Equal(6.5 / 24, sheet["N1"].Value.Number, 12);
        Assert.Equal("text", sheet["O1"].Value);

        Worksheet exponents = Load("1.5E+3,6.02e23").Worksheets[0];
        Assert.Equal(1500, exponents["A1"].Value);
        Assert.Equal("1.50E+03", exponents["A1"].Text);
        Assert.Equal("6.02E+23", exponents["B1"].Text);
    }

    [Fact]
    public void WithoutDetectionEveryFieldIsText()
    {
        Worksheet sheet = Load("1,TRUE", new CsvOptions { DetectTypes = false }).Worksheets[0];

        Assert.Equal("1", sheet["A1"].Value);
        Assert.Equal("TRUE", sheet["B1"].Value);
    }

    [Fact]
    public void ACultureReadsItsOwnNumbersAndDates()
    {
        CsvOptions german = new() { Culture = CultureInfo.GetCultureInfo("de-DE") };
        Worksheet sheet = Load("Preis;Datum\n1.234,5;15.03.2024\n1,5;x", german).Worksheets[0];

        Assert.Equal(1234.5, sheet["A2"].Value);
        Assert.Equal(45366, sheet["B2"].Value);
        Assert.Equal(1.5, sheet["A3"].Value);
        Assert.Equal("15.03.2024", Save(sheet, german).Split("\r\n")[1].Split(';')[1]);
    }

    [Theory]
    [InlineData("a,b;c\n", "A1", "a")]
    [InlineData("a;b;c,d\n", "C1", "c,d")]
    [InlineData("a\tb,c\tc\n", "B1", "b,c")]
    [InlineData("sep=|\na|b,c\n", "B1", "b,c")]
    public void TheSeparatorIsDetected(string text, string address, string expected)
    {
        Assert.Equal(expected, Load(text).Worksheets[0][address].Value);
    }

    [Fact]
    public void ANamedSeparatorWinsOverTheHintLine()
    {
        Worksheet sheet = Load("sep=,\na;b,c\n", new CsvOptions { Separator = ';' }).Worksheets[0];

        Assert.Equal("a", sheet["A1"].Value);
        Assert.Equal("b,c", sheet["B1"].Value);
        Assert.True(sheet["A2"].IsEmpty);
    }

    [Fact]
    public void TsvPathsUseTabs()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egap-{Guid.NewGuid():N}.tsv");
        try
        {
            Workbook book = Workbook.Create();
            book.Worksheets[0]["A1"].Value = "a,b";
            book.Worksheets[0]["B1"].Value = 2;
            book.Worksheets[0].SaveCsv(path);

            Assert.Equal("a,b\t2\r\n", File.ReadAllText(path));
            Assert.Equal(2, Workbook.LoadCsv(path).Worksheets[0]["B1"].Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAndSaveGoByTheExtension()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"egap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            Workbook book = Workbook.Create();
            book.Worksheets[0].Visibility = SheetVisibility.Hidden;
            Worksheet shown = book.Worksheets.Add("Shown");
            shown["A1"].Value = "a";
            shown["B1"].Value = 45366;
            shown["B1"].NumberFormat = "yyyy-mm-dd";

            book.Save(Path.Combine(folder, "out.csv"));
            book.Save(Path.Combine(folder, "out.tsv"), new WorkbookSaveOptions { Csv = new CsvOptions { WriteSeparatorHint = true } });
            book.Save(Path.Combine(folder, "out.xlsx"));

            Assert.Equal("a,2024-03-15\r\n", File.ReadAllText(Path.Combine(folder, "out.csv")));
            Assert.Equal("sep=\t\r\na\t2024-03-15\r\n", File.ReadAllText(Path.Combine(folder, "out.tsv")));
            Workbook csv = Workbook.Load(Path.Combine(folder, "out.csv"), new WorkbookLoadOptions { Csv = new CsvOptions { SheetName = "Data" } });
            Assert.Equal("Data", csv.Worksheets[0].Name);
            Assert.Equal(45366, csv.Worksheets[0]["B1"].Value);
            Assert.Equal(45366, Workbook.Load(Path.Combine(folder, "out.tsv")).Worksheets[0]["B1"].Value);
            Assert.Equal(2, Workbook.Load(Path.Combine(folder, "out.xlsx")).Worksheets.Count);

            Workbook hidden = Workbook.Create();
            hidden.Worksheets[0].Visibility = SheetVisibility.Hidden;
            Assert.Throws<InvalidOperationException>(() => hidden.Save(Path.Combine(folder, "none.csv")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void WritingUsesTheWorkbooksDateSystem()
    {
        Workbook book = Workbook.Create();
        book.Date1904 = true;
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = 0;
        sheet["A1"].NumberFormat = "yyyy-mm-dd";

        Assert.Equal("1904-01-01", sheet["A1"].Text);
        Assert.Equal("1904-01-01\r\n", Save(sheet));
    }

    [Fact]
    public void WritingQuotesWhatNeedsItAndPadsRows()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "x,y";
        sheet["C1"].Value = "say \"hi\"";
        sheet["B3"].Value = "two\nlines";
        sheet["A4"].Value = " padded";

        Assert.Equal("\"x,y\",,\"say \"\"hi\"\"\"\r\n,,\r\n,\"two\nlines\",\r\n\" padded\",,\r\n", Save(sheet));
        Assert.Equal("", Save(Workbook.Create().Worksheets[0]));
    }

    [Fact]
    public void WritingShowsValuesAsTheirCellsDo()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = 1234.5678;
        sheet["A1"].NumberFormat = "#,##0.00";
        sheet["B1"].Value = 45366;
        sheet["B1"].NumberFormat = "yyyy-mm-dd";
        sheet["C1"].Value = true;
        sheet["D1"].Value = CellError.DivisionByZero;
        sheet["E1"].Value = 0.1 + 0.2;

        Assert.Equal("\"1,234.57\",2024-03-15,TRUE,#DIV/0!,0.3\r\n", Save(sheet));
        Assert.Equal("1234.5678,45366,TRUE,#DIV/0!,0.30000000000000004\r\n", Save(sheet, new CsvOptions { WriteFormattedText = false }));
        Assert.Equal("sep=;\n1.234,57;2024-03-15;TRUE;#DIV/0!;0,3\n", Save(sheet, new CsvOptions
        {
            Separator = ';',
            Culture = CultureInfo.GetCultureInfo("de-DE"),
            WriteSeparatorHint = true,
            NewLine = "\n",
        }));
    }

    [Fact]
    public void EncodingsAreReadAndWritten()
    {
        Workbook book = Workbook.Create();
        book.Worksheets[0]["A1"].Value = "Ærø";
        MemoryStream utf8 = new();
        book.Worksheets[0].SaveCsv(utf8);
        Assert.Equal([0xEF, 0xBB, 0xBF], utf8.ToArray()[..3]);

        MemoryStream unicode = new();
        book.Worksheets[0].SaveCsv(unicode, new CsvOptions { Encoding = Encoding.Unicode });
        unicode.Position = 0;
        Assert.Equal("Ærø", Workbook.LoadCsv(unicode).Worksheets[0]["A1"].Value);

        Encoding latin1 = Encoding.Latin1;
        Workbook read = Workbook.LoadCsv(new MemoryStream(latin1.GetBytes("Ærø")), new CsvOptions { Encoding = latin1 });
        Assert.Equal("Ærø", read.Worksheets[0]["A1"].Value);
    }

    [Fact]
    public void TextRoundTrips()
    {
        string text = "id,name,amount\r\n1,\"Smith, J\",12.5\r\n2,\"a \"\"b\"\"\",-3\r\n";
        Worksheet sheet = Load(text).Worksheets[0];

        Assert.Equal(text, Save(sheet));
    }

    [Fact]
    public void LimitsAreChecked()
    {
        string wide = string.Join(",", Enumerable.Repeat("x", CellAddress.MaxColumns + 1));
        Assert.Throws<InvalidDataException>(() => Load(wide));
        Assert.Throws<InvalidDataException>(() => Load(new string('x', CellValue.MaxTextLength + 1)));
        Assert.Throws<ArgumentException>(() => new CsvOptions { NewLine = "|" });
        Assert.Throws<ArgumentException>(() => Save(Workbook.Create().Worksheets[0], new CsvOptions { Separator = '"' }));
    }
}
