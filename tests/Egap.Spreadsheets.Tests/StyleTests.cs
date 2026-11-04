using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets.Tests;

/// <summary>Cell styles: values that compare by content, kept once per workbook with their parts in their own tables.</summary>
public sealed class StyleTests
{
    [Fact]
    public void EqualStylesAreKeptOnce()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];

        for (int row = 0; row < 1000; row++)
        {
            sheet[row, 0].Style = CellStyle.Default with { Font = CellFont.Default with { Bold = true } };
            sheet[row, 1].Style = CellStyle.Default with { NumberFormat = "0.00" };
        }

        Assert.Equal(3, book.Styles.Count);
        Assert.Same(sheet["A1"].Style, sheet["A999"].Style);
        Assert.Equal(2, book.Styles.Fonts.Count);
    }

    [Fact]
    public void StylePartsGoToTheirOwnTablesAsInCellXfs()
    {
        StyleTable styles = new();

        // Excel's fixed fills come first: none and gray125.
        Assert.Equal(2, styles.Fills.Count);
        Assert.Equal(CellFillPattern.Gray125, styles.Fills[1].Pattern);

        CellStyle red = CellStyle.Default with
        {
            Font = CellFont.Default with { Color = CellColor.FromRgb(255, 0, 0), Size = 14 },
            Fill = CellFill.Solid(CellColor.FromTheme(4, 0.6)),
            Borders = CellBorders.Box(CellBorder.Thin),
            Alignment = CellAlignment.Default with { Horizontal = CellHorizontalAlignment.Center, WrapText = true },
            Protection = CellProtection.Default with { Locked = false },
            NumberFormat = "#,##0.00",
        };

        int index = styles.Add(red);
        CellFormatRecord record = styles.Record(index);
        Assert.Equal(1, index);
        Assert.Equal(1, record.Font);
        Assert.Equal(2, record.Fill);
        Assert.Equal(1, record.Border);
        Assert.Equal(4, record.NumberFormat);
        Assert.True(record.Alignment.WrapText);
        Assert.False(record.Protection.Locked);
        Assert.Equal(index, styles.Add(red with { }));
    }

    [Fact]
    public void BuiltInNumberFormatsKeepTheirIdsAndOthersStartAt164()
    {
        NumberFormatTable formats = new();

        Assert.Equal(0, formats.Add("General"));
        Assert.Equal(14, formats.Add("mm-dd-yy"));
        Assert.Equal(49, formats.Add("@"));
        Assert.Equal(164, formats.Add("yyyy-mm-dd"));
        Assert.Equal(165, formats.Add("0.000"));
        Assert.Equal(164, formats.Add("yyyy-mm-dd"));
        Assert.Equal("0.000", formats.Code(165));
        Assert.Equal("0%", NumberFormatTable.BuiltIn(9));
        Assert.Null(formats.Code(5));
    }

    [Fact]
    public void StylePropertiesCheckExcelsLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CellFont.Default with { Size = 0.5 });
        Assert.Throws<ArgumentException>(() => CellFont.Default with { Name = "" });
        Assert.Throws<ArgumentOutOfRangeException>(() => CellAlignment.Default with { Indent = 251 });
        Assert.Throws<ArgumentOutOfRangeException>(() => CellAlignment.Default with { TextRotation = 200 });
        Assert.Equal(255, (CellAlignment.Default with { TextRotation = 255 }).TextRotation);
        Assert.Throws<ArgumentException>(() => CellStyle.Default with { NumberFormat = new string('0', 256) });
        Assert.Equal("General", (CellStyle.Default with { NumberFormat = "" }).NumberFormat);
        Assert.Throws<ArgumentNullException>(() => CellStyle.Default with { Font = null! });
        Assert.Throws<ArgumentOutOfRangeException>(() => CellColor.FromTheme(12));
        Assert.Throws<ArgumentOutOfRangeException>(() => CellColor.FromIndex(66));
        Assert.Throws<ArgumentOutOfRangeException>(() => CellColor.FromArgb(0xFF000000, 1.5));
    }

    [Fact]
    public void ColoursDescribeThemselves()
    {
        Assert.Equal("auto", CellColor.Auto.ToString());
        Assert.Equal("#FF4472C4", CellColor.FromRgb(0x44, 0x72, 0xC4).ToString());
        Assert.Equal("theme 4 tint 0.4", CellColor.FromTheme(4, 0.4).ToString());
        Assert.Equal("indexed 10", CellColor.FromIndex(10).ToString());
        Assert.Equal(CellColor.FromArgb(0xFFFFFFFF), CellColor.White);
    }

    [Fact]
    public void NewCellsUseExcelsDefaultStyle()
    {
        CellStyle style = Workbook.Create().Worksheets[0]["A1"].Style;

        Assert.Same(CellStyle.Default, style);
        Assert.Equal("Calibri", style.Font.Name);
        Assert.Equal(11, style.Font.Size);
        Assert.Equal(CellColor.FromTheme(1), style.Font.Color);
        Assert.Equal(CellFontScheme.Minor, style.Font.Scheme);
        Assert.True(style.Protection.Locked);
        Assert.Equal("General", style.NumberFormat);
    }
}
