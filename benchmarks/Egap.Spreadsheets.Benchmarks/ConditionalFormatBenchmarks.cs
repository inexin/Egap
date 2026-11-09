using BenchmarkDotNet.Attributes;

using Egap.Spreadsheets;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// Conditional formatting and data validation worked out over 10,000 rows of four columns (40,000 cells): a comparison, a
/// formula, a comparison with cells, a colour scale, a data bar and an icon set evaluated for every cell, and every cell
/// checked against a whole number rule, a list of cells and a custom formula.
/// </summary>
[MemoryDiagnoser]
public class ConditionalFormatBenchmarks
{
    private const int Rows = 10_000;

    private Worksheet sheet = null!;

    [GlobalSetup]
    public void Setup()
    {
        sheet = Workbook.Create().Worksheets[0];
        Random random = new(1);
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                sheet.Cells[row, column].Value = CellValue.FromNumber(random.Next(1000));
            }
        }

        for (int row = 0; row < 20; row++)
        {
            sheet.Cells[row, 6].Value = CellValue.FromNumber(row * 50);
        }

        CellStyle bold = CellStyle.Default with { Font = CellStyle.Default.Font with { Bold = true } };
        CellStyle red = CellStyle.Default with { Fill = CellFill.Solid(CellColor.FromRgb(255, 199, 206)) };
        sheet.ConditionalFormats.Add("A1:A10000", ConditionalFormatRule.CellIs(ComparisonOperator.GreaterThan, "500", bold), ConditionalFormatRule.ColorScale(CellColor.White, CellColor.FromRgb(99, 190, 123)));
        sheet.ConditionalFormats.Add("B1:B10000", ConditionalFormatRule.Expression("B1>A1", red), ConditionalFormatRule.DataBar(CellColor.FromRgb(99, 142, 198)));
        sheet.ConditionalFormats.Add("C1:C10000", ConditionalFormatRule.IconSet("3Arrows"));
        sheet.ConditionalFormats.Add("D1:D10000", ConditionalFormatRule.CellIs(ComparisonOperator.Between, "$G$2", red, "$G$5"), ConditionalFormatRule.Expression("ISODD(D1)", bold));
        sheet.DataValidations.Add(RangeAddress.Parse("A1:A10000"), DataValidationType.WholeNumber, "100", "900").Operator = ComparisonOperator.Between;
        sheet.DataValidations.Add(RangeAddress.Parse("B1:B10000"), DataValidationType.List, "$G$1:$G$20");
        sheet.DataValidations.Add(RangeAddress.Parse("C1:D10000"), DataValidationType.Custom, "MOD(C1,7)<>0");
    }

    [Benchmark(Description = "Evaluate conditional formats, 40k cells")]
    public int Evaluate()
    {
        ConditionalFormatEvaluation evaluation = sheet.ConditionalFormats.Evaluate();
        int applied = 0;
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                applied += evaluation[row, column].Rules.Count;
            }
        }

        return applied;
    }

    [Benchmark(Description = "Find invalid data, 40k cells")]
    public int FindInvalid() => sheet.DataValidations.FindInvalid().Count;
}
