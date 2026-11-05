using System.Globalization;

namespace Egap.Spreadsheets.Tests;

/// <summary><see cref="FunctionRegistry"/>: the built-in list, and functions added for formulas. Names are unique per test, as the registry is shared.</summary>
public sealed class FunctionRegistryTests
{
    private static Worksheet Sheet()
    {
        Workbook book = Workbook.Create();
        book.Culture = CultureInfo.InvariantCulture;
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = 1;
        sheet["A2"].Value = 2;
        sheet["A3"].Value = "x";
        return sheet;
    }

    [Fact]
    public void BuiltInFunctionsAreListed()
    {
        Assert.True(FunctionRegistry.BuiltInFunctions.Count >= 150);
        Assert.Contains("VLOOKUP", FunctionRegistry.BuiltInFunctions);
        Assert.Contains("NETWORKDAYS.INTL", FunctionRegistry.BuiltInFunctions);
        Assert.Equal(FunctionRegistry.BuiltInFunctions.Order(StringComparer.Ordinal), FunctionRegistry.BuiltInFunctions);
        Assert.True(FunctionRegistry.Contains("sum"));
        Assert.True(FunctionRegistry.Contains("IF"));
        Assert.True(FunctionRegistry.Contains("_xlfn.CONCAT"));
        Assert.False(FunctionRegistry.Contains("NOSUCHFUNCTION"));
    }

    [Fact]
    public void AddedFunctionIsCalled()
    {
        FunctionRegistry.Add("TEST.DOUBLE", 1, 1, arguments => arguments[0].Value.Number * 2);
        try
        {
            Assert.True(FunctionRegistry.Contains("test.double"));
            Assert.Contains("TEST.DOUBLE", FunctionRegistry.AddedFunctions);
            Assert.Equal(42, Sheet().Evaluate("test.double(21)").Number);
            Assert.Equal(4, Sheet().Evaluate("TEST.DOUBLE(A2)").Number);
            Assert.Equal(CellError.Value, Sheet().Evaluate("TEST.DOUBLE(1,2)").Error);
            Assert.Equal(CellError.Value, Sheet().Evaluate("TEST.DOUBLE(A3)").Error);   // Number on text throws: #VALUE!
        }
        finally
        {
            Assert.True(FunctionRegistry.Remove("TEST.DOUBLE"));
        }

        Assert.False(FunctionRegistry.Contains("TEST.DOUBLE"));
        Assert.Equal(CellError.Name, Sheet().Evaluate("TEST.DOUBLE(21)").Error);
        Assert.False(FunctionRegistry.Remove("TEST.DOUBLE"));
    }

    [Fact]
    public void AddedFunctionSeesRangesAndMissingArguments()
    {
        FunctionRegistry.Add("TEST.SHAPE", 0, 3, arguments =>
            string.Join(";", arguments.Select(a => a.IsMissing ? "missing"
                : a.IsReference ? $"ref {a.Values.GetLength(0)}x{a.Values.GetLength(1)} {a.Values[0, 0]}"
                : $"value {a.Value}")));
        try
        {
            Assert.Equal("ref 3x1 1;missing;value 5", Sheet().Evaluate("TEST.SHAPE(A1:A3,,5)").Text);
            Assert.Equal("value 1", Sheet().Evaluate("TEST.SHAPE({1,2})").Text);
            Assert.Equal("", Sheet().Evaluate("TEST.SHAPE()").Text);
        }
        finally
        {
            FunctionRegistry.Remove("TEST.SHAPE");
        }
    }

    [Fact]
    public void CalculationSeesFunctionsAddedLater()
    {
        Worksheet sheet = Sheet();
        sheet["B1"].Formula = "TEST.LATER(A2)";
        sheet.Workbook.Calculate();
        Assert.Equal(CellError.Name, sheet["B1"].Value.Error);

        FunctionRegistry.Add("TEST.LATER", 1, 1, arguments => arguments[0].Value.Number + 40);
        try
        {
            sheet.Workbook.Calculate();
            Assert.Equal(42, sheet["B1"].Value.Number);
        }
        finally
        {
            FunctionRegistry.Remove("TEST.LATER");
        }
    }

    [Fact]
    public void UserDefinedPrefixIsStripped()
    {
        FunctionRegistry.Add("TEST_UDF", 0, 0, _ => 7);
        try
        {
            Assert.Equal(7, Sheet().Evaluate("_xludf.TEST_UDF()").Number);
        }
        finally
        {
            FunctionRegistry.Remove("TEST_UDF");
        }
    }

    [Theory]
    [InlineData("SUM")]
    [InlineData("if")]
    [InlineData("IFERROR")]
    [InlineData("1ABC")]
    [InlineData("A B")]
    [InlineData("")]
    public void BadNamesAreRefused(string name) =>
        Assert.Throws<ArgumentException>(() => FunctionRegistry.Add(name, 0, 0, _ => 0));

    [Fact]
    public void BadCountsAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FunctionRegistry.Add("TEST.COUNTS", -1, 0, _ => 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FunctionRegistry.Add("TEST.COUNTS", 2, 1, _ => 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FunctionRegistry.Add("TEST.COUNTS", 0, 256, _ => 0));
        Assert.Throws<ArgumentNullException>(() => FunctionRegistry.Add("TEST.COUNTS", 0, 0, null!));
        Assert.False(FunctionRegistry.Contains("TEST.COUNTS"));
    }
}
