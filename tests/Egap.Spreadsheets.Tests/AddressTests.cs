namespace Egap.Spreadsheets.Tests;

/// <summary>A1 references: cells, ranges, whole rows and columns, and column names.</summary>
public sealed class AddressTests
{
    [Theory]
    [InlineData("A1", 0, 0)]
    [InlineData("B4", 3, 1)]
    [InlineData("$B$4", 3, 1)]
    [InlineData("b$4", 3, 1)]
    [InlineData("Z9", 8, 25)]
    [InlineData("AA10", 9, 26)]
    [InlineData("AZ1", 0, 51)]
    [InlineData("ZZ1", 0, 701)]
    [InlineData("AAA1", 0, 702)]
    [InlineData("XFD1048576", 1048575, 16383)]
    public void CellReferencesParseAndFormat(string text, int row, int column)
    {
        CellAddress address = CellAddress.Parse(text);

        Assert.Equal(row, address.Row);
        Assert.Equal(column, address.Column);
        Assert.Equal(text.Replace("$", "", StringComparison.Ordinal).ToUpperInvariant(), address.ToString());
        Assert.Equal(address, new CellAddress(row, column));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("1")]
    [InlineData("A0")]
    [InlineData("A01")]
    [InlineData("XFE1")]
    [InlineData("A1048577")]
    [InlineData("AAAA1")]
    [InlineData("A1B")]
    [InlineData("$$A1")]
    [InlineData("A-1")]
    public void BadCellReferencesAreRefused(string text)
    {
        Assert.False(CellAddress.TryParse(text, out _));
        Assert.Throws<FormatException>(() => CellAddress.Parse(text));
    }

    [Fact]
    public void ColumnNamesRoundTripAcrossTheSheet()
    {
        for (int column = 0; column < CellAddress.MaxColumns; column++)
        {
            Assert.Equal(column, CellAddress.ColumnIndex(CellAddress.ColumnName(column)));
        }

        Assert.Equal("XFD", CellAddress.ColumnName(16383));
        Assert.Throws<ArgumentOutOfRangeException>(() => CellAddress.ColumnName(16384));
        Assert.Throws<FormatException>(() => CellAddress.ColumnIndex("XFE"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellAddress(CellAddress.MaxRows, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellAddress(0, -1));
    }

    [Theory]
    [InlineData("A1:D20", 0, 0, 19, 3, "A1:D20")]
    [InlineData("D20:A1", 0, 0, 19, 3, "A1:D20")]
    [InlineData("B20:D1", 0, 1, 19, 3, "B1:D20")]
    [InlineData("$A$1:$B$2", 0, 0, 1, 1, "A1:B2")]
    [InlineData("B4", 3, 1, 3, 1, "B4")]
    [InlineData("B4:B4", 3, 1, 3, 1, "B4")]
    [InlineData("A:C", 0, 0, 1048575, 2, "A:C")]
    [InlineData("$C:$A", 0, 0, 1048575, 2, "A:C")]
    [InlineData("2:5", 1, 0, 4, 16383, "2:5")]
    [InlineData("1:1048576", 0, 0, 1048575, 16383, "1:1048576")]
    public void RangesParseAndFormat(string text, int firstRow, int firstColumn, int lastRow, int lastColumn, string formatted)
    {
        RangeAddress range = RangeAddress.Parse(text);

        Assert.Equal(new RangeAddress(firstRow, firstColumn, lastRow, lastColumn), range);
        Assert.Equal(formatted, range.ToString());
        Assert.Equal(range, RangeAddress.Parse(formatted));
    }

    [Theory]
    [InlineData("A1:")]
    [InlineData(":B2")]
    [InlineData("A1:B2:C3")]
    [InlineData("A:1")]
    [InlineData("0:3")]
    [InlineData("A:XFE")]
    public void BadRangesAreRefused(string text)
    {
        Assert.False(RangeAddress.TryParse(text, out _));
    }

    [Fact]
    public void RangesMeasureAndCompare()
    {
        RangeAddress range = RangeAddress.Parse("B2:D5");

        Assert.Equal(4, range.RowCount);
        Assert.Equal(3, range.ColumnCount);
        Assert.Equal(12, range.CellCount);
        Assert.True(range.Contains(CellAddress.Parse("C3")));
        Assert.False(range.Contains(CellAddress.Parse("A1")));
        Assert.True(range.Contains(RangeAddress.Parse("C3:D4")));
        Assert.True(range.Intersects(RangeAddress.Parse("D5:F9")));
        Assert.False(range.Intersects(RangeAddress.Parse("E1:F9")));
        Assert.Equal(17_179_869_184, RangeAddress.Parse("1:1048576").CellCount);
    }
}
