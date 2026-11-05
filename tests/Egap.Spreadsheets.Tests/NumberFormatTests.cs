using System.Globalization;

namespace Egap.Spreadsheets.Tests;

/// <summary>Number format codes shown as Excel in English shows them.</summary>
public sealed class NumberFormatTests
{
    [Theory]
    [InlineData("General", 0, "0")]
    [InlineData("General", 1234.5, "1234.5")]
    [InlineData("General", -5, "-5")]
    [InlineData("General", 0.30000000000000004, "0.3")]
    [InlineData("General", 1.0 / 3, "0.333333333")]
    [InlineData("General", 12345678901, "12345678901")]
    [InlineData("General", 123456789012, "1.23457E+11")]
    [InlineData("General", 1e-10, "1E-10")]
    [InlineData("General", 1.5e-9, "1.5E-09")]
    [InlineData("General", 1.23456789e-5, "1.23457E-05")]
    [InlineData("General", 0.0001, "0.0001")]
    [InlineData("General", 0.00012345678, "0.000123457")]
    [InlineData("General", 1e-5, "0.00001")]
    [InlineData("General", 1234567890123, "1.23457E+12")]
    [InlineData("General", 1234567890.5, "1234567891")]
    [InlineData("General", 1e100, "1E+100")]
    [InlineData("[$€-2] #,##0.00", 1234.5, "€ 1,234.50")]
    [InlineData("#,##0.00 [$€-1]", 1234.5, "1,234.50 €")]
    [InlineData("0", 1234.5, "1235")]
    [InlineData("0", -1234.5, "-1235")]
    [InlineData("0", -0.4, "0")]
    [InlineData("0.00", 2.675, "2.68")]
    [InlineData("00000", 123, "00123")]
    [InlineData("#.##", 0.5, ".5")]
    [InlineData("#.##", 2, "2.")]
    [InlineData("???.???", 1.5, "  1.5  ")]
    [InlineData("#,##0", 1234567, "1,234,567")]
    [InlineData("#,##0", 0, "0")]
    [InlineData("#,##0.00", -1234.567, "-1,234.57")]
    [InlineData("#,##0,", 1234567, "1,235")]
    [InlineData("0.0,,\"M\"", 1234567, "1.2M")]
    [InlineData("0%", 0.125, "13%")]
    [InlineData("0.0%", 0.125, "12.5%")]
    [InlineData("0.00E+00", 12345, "1.23E+04")]
    [InlineData("0.00E+00", 0.00012, "1.20E-04")]
    [InlineData("##0.0E+0", 12345, "12.3E+3")]
    [InlineData("# ?/?", 1.25, "1 1/4")]
    [InlineData("# ?/?", 0.5, " 1/2")]
    [InlineData("# ?/?", 3, "3    ")]
    [InlineData("?/?", 0.75, "3/4")]
    [InlineData("# ?/8", 1.3, "1 2/8")]
    [InlineData("# ??/100", 1.25, "1 25/100")]
    [InlineData("# ??/100", 0.07, "  7/100")]
    [InlineData("?/10", 0.3, "3/10")]
    [InlineData("?/20", 0.25, "5/20")]
    [InlineData("# ?/50", 2.5, "2 25/50")]
    [InlineData("#/1000", 0.125, "125/1000")]
    [InlineData("\"$\"#,##0.00", 5, "$5.00")]
    [InlineData("0.00_);(0.00)", 1.5, "1.50 ")]
    [InlineData("0.00_);(0.00)", -1.5, "(1.50)")]
    [InlineData("0.00;[Red]-0.00;\"zero\"", 0, "zero")]
    [InlineData("0.00;[Red]-0.00;\"zero\"", -1, "-1.00")]
    [InlineData("0;-0;;@", 0, "")]
    [InlineData("[>=100]\"big\";[<0]\"neg\";0", 150, "big")]
    [InlineData("[>=100]\"big\";[<0]\"neg\";0", -5, "neg")]
    [InlineData("[>=100]\"big\";[<0]\"neg\";0", 5, "5")]
    [InlineData("@", 123, "123")]
    [InlineData("@", -1.5, "-1.5")]
    [InlineData("\"Total: \"@", 5, "5")]
    public void NumbersShowAsInExcel(string code, double value, string expected)
    {
        Assert.Equal(expected, NumberFormatCode.Parse(code).Format(value));
    }

    [Theory]
    [InlineData("yyyy-mm-dd", 45366, "2024-03-15")]
    [InlineData("d mmmm yyyy", 45366, "15 March 2024")]
    [InlineData("ddd d mmm yy", 45366, "Fri 15 Mar 24")]
    [InlineData("dddd", 45366, "Friday")]
    [InlineData("mmmmm", 45366, "M")]
    [InlineData("m/d/yyyy", 1, "1/1/1900")]
    [InlineData("m/d/yyyy", 60, "2/29/1900")]
    [InlineData("m/d/yyyy", 61, "3/1/1900")]
    [InlineData("m/d/yyyy", 0, "1/0/1900")]
    [InlineData("h:mm AM/PM", 0.75, "6:00 PM")]
    [InlineData("h:mm A/P", 0.25, "6:00 A")]
    [InlineData("hh:mm:ss", 0.5, "12:00:00")]
    [InlineData("h:mm", 0.999999, "0:00")]
    [InlineData("[h]:mm", 1.5, "36:00")]
    [InlineData("[mm]:ss", 0.125, "180:00")]
    [InlineData("mm:ss.0", 1.5 / 86400, "00:01.5")]
    [InlineData("yyyy-mm-dd hh:mm", 45366.75, "2024-03-15 18:00")]
    [InlineData("yyyy-mm-dd", -1, "##########")]
    [InlineData("mm-dd-yy", 45366, "3/15/2024")]
    [InlineData("m/d/yy h:mm", 45366.5, "3/15/2024 12:00")]
    public void DatesShowAsInExcel(string code, double value, string expected)
    {
        Assert.Equal(expected, NumberFormatCode.Parse(code).Format(value));
    }

    [Fact]
    public void The1904SystemCountsFrom1904()
    {
        Assert.Equal("1904-01-01", NumberFormatCode.Parse("yyyy-mm-dd").Format(0, date1904: true));
        Assert.Equal("2024-03-15", NumberFormatCode.Parse("yyyy-mm-dd").Format(45366 - 1462, date1904: true));
    }

    [Fact]
    public void TextGoesThroughTheTextSection()
    {
        Assert.Equal("Total: x", NumberFormatCode.Parse("\"Total: \"@").Format("x"));
        Assert.Equal("a", NumberFormatCode.Parse("0;-0;;@").Format("a"));
        Assert.Equal("a", NumberFormatCode.Parse("0.00").Format("a"));
        Assert.Equal("TRUE", NumberFormatCode.Parse("0.00").Format(true));
        Assert.Equal("#DIV/0!", NumberFormatCode.General.Format(CellError.DivisionByZero));
        Assert.Equal("", NumberFormatCode.General.Format(CellValue.Empty));
    }

    [Fact]
    public void ACultureGivesSeparatorsNamesAndShortDate()
    {
        CultureInfo german = CultureInfo.GetCultureInfo("de-DE");

        Assert.Equal("1.234,50", NumberFormatCode.Parse("#,##0.00").Format(1234.5, german));
        Assert.Equal("15. März 2024", NumberFormatCode.Parse("d. mmmm yyyy").Format(45366, german));
        Assert.Equal("15.03.2024", NumberFormatCode.Parse("mm-dd-yy").Format(45366, german));
        Assert.Equal("1234,5", NumberFormatCode.General.Format(1234.5, german));
        Assert.Equal("1.234,50 €", NumberFormatCode.Parse("#,##0.00 [$€-1]").Format(1234.5, german));
    }

    [Theory]
    [InlineData("M/d/yy", "mm-dd-yy", "3/15/24")]
    [InlineData("M/d/yy", "m/d/yy h:mm", "3/15/24 18:00")]
    [InlineData("MM-dd-yy", "mm-dd-yy", "03-15-24")]
    [InlineData("MM-dd-yy", "m/d/yy h:mm", "03-15-24 18:00")]
    public void AShortDateThatIsTheBuiltInCodeDoesNotLoop(string pattern, string code, string expected)
    {
        CultureInfo culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.DateTimeFormat.ShortDatePattern = pattern;

        Assert.Equal(expected, NumberFormatCode.Parse(code).Format(45366.75, culture));
    }

    [Fact]
    public void ALocaleTagOverridesTheCulture()
    {
        Assert.Equal("mars", NumberFormatCode.Parse("[$-414]mmmm").Format(45366));
        Assert.Equal("kr 5", NumberFormatCode.Parse("[$kr-414] 0").Format(5));
    }

    [Fact]
    public void ColorsComeFromTheSectionThatApplies()
    {
        NumberFormatCode code = NumberFormatCode.Parse("[Blue]0;[Red]-0");

        Assert.NotNull(code.GetColor(1));
        Assert.NotEqual(code.GetColor(1), code.GetColor(-1));
        Assert.Null(NumberFormatCode.General.GetColor(1));
        Assert.True(NumberFormatCode.Parse("yyyy-mm-dd").IsDateTime);
        Assert.False(NumberFormatCode.Parse("0.00").IsDateTime);
    }

    [Fact]
    public void CellTextUsesTheCellsFormatAndTheWorkbooksCulture()
    {
        Workbook book = Workbook.Create();
        Cell cell = book.Worksheets[0]["A1"];
        cell.Value = 1234.5;
        cell.NumberFormat = "#,##0.00";

        Assert.Equal("1,234.50", cell.Text);
        book.Culture = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("1.234,50", cell.Text);
        Assert.Equal("", book.Worksheets[0]["B1"].Text);
        Assert.Throws<ArgumentNullException>(() => book.Culture = null!);
    }

    [Fact]
    public void ANumberInATextFormatShowsAsGeneral()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = 123;
        sheet["A1"].NumberFormat = "@";
        sheet["B1"].Value = "abc";
        sheet["B1"].NumberFormat = "@";

        Assert.Equal("123", sheet["A1"].Text);
        Assert.Equal("abc", sheet["B1"].Text);
        MemoryStream stream = new();
        sheet.SaveCsv(stream);
        Assert.Equal("123,abc\r\n", System.Text.Encoding.UTF8.GetString(stream.ToArray()).TrimStart('﻿'));
    }

    [Theory]
    [InlineData(2958465.0, false, "9999-12-31 00:00")]
    [InlineData(2958465.999, false, "9999-12-31 23:58")]
    [InlineData(2958465.99999999, false, "##########")]
    [InlineData(2958466.0, false, "##########")]
    [InlineData(2958466.5, false, "##########")]
    [InlineData(2957003.0, true, "9999-12-31 00:00")]
    [InlineData(2957004.0, true, "##########")]
    [InlineData(2958466.5, true, "##########")]
    [InlineData(-0.5, false, "##########")]
    [InlineData(-0.5, true, "##########")]
    [InlineData(1e300, false, "##########")]
    public void DatesPastTheCalendarShowAsHashes(double serial, bool date1904, string expected)
    {
        Assert.Equal(expected, NumberFormatCode.Parse("yyyy-mm-dd hh:mm").Format(serial, date1904: date1904));
    }
}
